using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TcAutomation.Core
{
    /// <summary>
    /// Background watchdog that enumerates top-level windows and auto-dismisses
    /// known modal dialogs that can block headless TcXaeShell / Visual Studio
    /// automation. These dialogs appear even with DTE.SuppressUI = true because
    /// they originate from the IDE shell, not from extensions.
    ///
    /// Dialogs handled:
    /// - "File has been changed outside the environment. Reload?"
    ///     -> click "No" (keep in-memory version, don't reload — TwinCAT has
    ///        just rewritten the file on disk, reload would trigger more churn)
    /// - "Conflicting File Modification Detected" (project-level)
    ///     -> click "Ignore" (keep project state as-is)
    /// - "Target system reports a fatal error" (AdsError popup from activation)
    ///     -> click "OK" (the failure is already returned via the ADS exception)
    /// </summary>
    public static class DialogWatchdog
    {
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private const uint WM_COMMAND = 0x0111;
        private const uint BM_CLICK = 0x00F5;

        private static Thread? _thread;
        private static volatile bool _running;

        /// <summary>
        /// Start the watchdog. Dialogs are polled every 500ms.
        /// </summary>
        public static void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "DialogWatchdog" };
            _thread.Start();
        }

        /// <summary>
        /// Stop the watchdog.
        /// </summary>
        public static void Stop()
        {
            _running = false;
            try { _thread?.Join(1000); } catch { }
            _thread = null;
        }

        private static void Run()
        {
            while (_running)
            {
                try { EnumWindows(OnWindow, IntPtr.Zero); }
                catch { }
                Thread.Sleep(500);
            }
        }

        private static bool OnWindow(IntPtr hWnd, IntPtr lParam)
        {
            if (!IsWindowVisible(hWnd)) return true;

            var title = new StringBuilder(256);
            GetWindowText(hWnd, title, title.Capacity);
            string t = title.ToString();

            if (string.IsNullOrEmpty(t)) return true;

            // "File has been changed outside the environment" — dialog title is usually
            // "Microsoft Visual Studio" or "TcXaeShell". Detect by scanning child text.
            if (t == "Microsoft Visual Studio" || t == "TcXaeShell" || t.StartsWith("TcXaeShell"))
            {
                if (HasChildText(hWnd, "changed outside the environment"))
                {
                    Console.Error.WriteLine("[DialogWatchdog] Auto-dismissing 'file changed outside' dialog (No)");
                    ClickButton(hWnd, "&No");
                    return true;
                }
            }

            if (t == "Conflicting File Modification Detected")
            {
                Console.Error.WriteLine("[DialogWatchdog] Auto-dismissing 'Conflicting File Modification' dialog (Ignore)");
                ClickButton(hWnd, "&Ignore");
                return true;
            }

            if (t == "Target system reports a fatal error")
            {
                Console.Error.WriteLine("[DialogWatchdog] Auto-dismissing 'Target fatal error' dialog (OK)");
                ClickButton(hWnd, "OK");
                return true;
            }

            return true;
        }

        private static bool HasChildText(IntPtr parent, string needle)
        {
            bool found = false;
            EnumChildWindows(parent, (child, _) =>
            {
                var sb = new StringBuilder(512);
                GetWindowText(child, sb, sb.Capacity);
                if (sb.ToString().IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static void ClickButton(IntPtr dialog, string buttonText)
        {
            IntPtr btn = FindChildButton(dialog, buttonText);
            if (btn != IntPtr.Zero)
            {
                SendMessage(btn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            }
        }

        private static IntPtr FindChildButton(IntPtr parent, string text)
        {
            IntPtr result = IntPtr.Zero;
            EnumChildWindows(parent, (child, _) =>
            {
                var cls = new StringBuilder(64);
                GetClassName(child, cls, cls.Capacity);
                if (cls.ToString().IndexOf("Button", StringComparison.OrdinalIgnoreCase) < 0)
                    return true;

                var tx = new StringBuilder(128);
                GetWindowText(child, tx, tx.Capacity);
                string actual = tx.ToString().Replace("&", "");
                string expected = text.Replace("&", "");
                if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    result = child;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }
    }
}
