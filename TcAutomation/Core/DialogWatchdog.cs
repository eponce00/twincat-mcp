using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TcAutomation.Core
{
    /// <summary>
    /// Background watchdog that enumerates top-level windows owned by a
    /// specific TcXaeShell / Visual Studio process and auto-dismisses known
    /// modal dialogs that can block headless automation. These dialogs appear
    /// even with DTE.SuppressUI = true because they originate from the IDE
    /// shell itself, not from extensions, and modals need a visible parent
    /// window — so Visual Studio will unhide our hidden MainWindow to attach
    /// the dialog. That's the "TcXaeShell flashed into view" the user sees.
    ///
    /// Safety model (critical):
    /// - Every Start() call MUST supply the PID of the DTE process it owns
    ///   (from <see cref="VisualStudioInstance.DteProcessId"/>). The watchdog
    ///   refuses to act on windows from any other process. This is the
    ///   guardrail that prevents us from auto-clicking buttons in the user's
    ///   OWN interactive IDE or another automation's shell.
    /// - Start/Stop are reference-counted, so overlapping subsystems
    ///   (persistent host + in-flight TcUnit command) cannot accidentally
    ///   stop each other's watchdog. The thread keeps running until every
    ///   registered PID has called Stop().
    ///
    /// Dialogs handled:
    /// - "File has been changed outside the environment. Reload?"
    ///     -> click "Yes to All" (reload all changed files — keeps VS in sync
    ///        with what TwinCAT wrote to disk, which is needed for error list)
    /// - "The project has been modified outside of the environment. Reload?"
    ///     -> click "Reload All" / "Reload" (keep project state in sync; if we
    ///        clicked Ignore, the next build would compile from stale in-memory
    ///        project data while disk has the user's latest edits)
    /// - "Conflicting File Modification Detected" (project-level, legacy variant)
    ///     -> click "Reload All" or "Ignore"
    /// - "Target system reports a fatal error" (AdsError popup from activation)
    ///     -> click "OK" (the failure is already returned via the ADS exception)
    /// </summary>
    public static class DialogWatchdog
    {
        // ----- P/Invoke -----

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private const uint BM_CLICK = 0x00F5;

        // ----- Ref-counted lifecycle -----
        //
        // Two subsystems can own the watchdog simultaneously (the persistent
        // host and an in-flight TcUnit run). A naive bool flag would let one
        // Stop() call drop the other's coverage. Instead we track each
        // owner's PID: the thread runs while ANY PID is registered, and we
        // only dismiss dialogs whose owning process is in the registered
        // set. This also gives us the per-PID safety scope for free.

        private static readonly object _lock = new object();
        private static readonly Dictionary<int, int> _pidRefCounts = new Dictionary<int, int>();
        private static Thread? _thread;
        private static volatile bool _running;

        // Polling interval. 150ms is fast enough that a dialog appearing
        // between ticks is dismissed before the user perceives a flash
        // (the click typically closes the dialog within one render frame
        // of it being shown), and slow enough that the CPU cost is
        // negligible (~one EnumWindows call every 150ms).
        private const int TickMs = 150;

        /// <summary>
        /// Register a DTE process PID with the watchdog, starting the
        /// background thread on the first registration. MUST be paired with
        /// a <see cref="Stop"/> call for the SAME PID when the caller is
        /// finished (typically in a finally block). Calling Start twice for
        /// the same PID is safe (ref-counted).
        /// </summary>
        public static void Start(int targetPid)
        {
            if (targetPid <= 0)
            {
                // Refuse to monitor "every process on the box" — that's the
                // unsafe case that could auto-click dialogs in the user's
                // own IDE or another automation's shell.
                Console.Error.WriteLine(
                    $"[DialogWatchdog] Refusing Start({targetPid}): PID must be > 0");
                return;
            }

            lock (_lock)
            {
                if (_pidRefCounts.TryGetValue(targetPid, out var count))
                {
                    _pidRefCounts[targetPid] = count + 1;
                }
                else
                {
                    _pidRefCounts[targetPid] = 1;
                    Console.Error.WriteLine(
                        $"[DialogWatchdog] Now monitoring PID {targetPid} (active set: {_pidRefCounts.Count})");
                }

                if (_running) return;

                _running = true;
                _thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = "DialogWatchdog",
                };
                _thread.Start();
            }
        }

        /// <summary>
        /// Deregister a DTE process PID. When all registered PIDs have
        /// called Stop the thread exits. Calling Stop more times than Start
        /// for a given PID is safe (no-op after the count reaches zero).
        /// </summary>
        public static void Stop(int targetPid)
        {
            Thread? toJoin = null;

            lock (_lock)
            {
                if (_pidRefCounts.TryGetValue(targetPid, out var count))
                {
                    if (count <= 1)
                    {
                        _pidRefCounts.Remove(targetPid);
                        Console.Error.WriteLine(
                            $"[DialogWatchdog] Stopped monitoring PID {targetPid} (active set: {_pidRefCounts.Count})");
                    }
                    else
                    {
                        _pidRefCounts[targetPid] = count - 1;
                    }
                }

                if (_pidRefCounts.Count == 0)
                {
                    _running = false;
                    toJoin = _thread;
                    _thread = null;
                }
            }

            if (toJoin != null)
            {
                try { toJoin.Join(1000); } catch { }
            }
        }

        private static void Run()
        {
            while (_running)
            {
                try { EnumWindows(OnWindow, IntPtr.Zero); }
                catch { /* RPC / transient UI errors — next tick retries */ }
                Thread.Sleep(TickMs);
            }
        }

        private static bool OnWindow(IntPtr hWnd, IntPtr lParam)
        {
            // Skip invisible tops — they're not modals holding up our DTE.
            // (VS sometimes creates hidden "message-only" windows we don't
            // care about.)
            if (!IsWindowVisible(hWnd)) return true;

            // Safety scope: only operate on windows owned by a PID we've
            // been asked to monitor. Everything else (user's interactive
            // VS, other automation's shell, unrelated apps) is off-limits.
            GetWindowThreadProcessId(hWnd, out var winPid);
            lock (_lock)
            {
                if (!_pidRefCounts.ContainsKey((int)winPid)) return true;
            }

            var title = new StringBuilder(256);
            GetWindowText(hWnd, title, title.Capacity);
            string t = title.ToString();

            if (string.IsNullOrEmpty(t)) return true;

            // Both the file-reload and project-reload dialogs are hosted in
            // windows titled "Microsoft Visual Studio" (or "TcXaeShell" on
            // some SKUs). They're distinguished by the body text of their
            // static label controls.
            bool isShellHostedDialog =
                t == "Microsoft Visual Studio" ||
                t == "TcXaeShell" ||
                t.StartsWith("TcXaeShell", StringComparison.Ordinal);

            if (isShellHostedDialog)
            {
                // Project reload — the one causing the main window to flash
                // into view when the user edits project files on disk. VS
                // treats project reloads as a separate mechanism from
                // document reloads, so AutoloadExternalChanges does NOT
                // silence this one; it has to be dismissed manually.
                //
                // The phrasing varies slightly by VS version ("has been
                // modified outside of the environment", "... outside of
                // the source editor", "... changed outside the
                // environment"), so we match the stable substring.
                if (HasChildText(hWnd, "modified outside")
                    || HasChildText(hWnd, "changed outside"))
                {
                    // Clicking "Reload" (or "Reload All" for the multi-
                    // project variant) is what the user would do anyway —
                    // they want the on-disk edits to take effect in the
                    // next build. Ignore would desync VS's in-memory model
                    // from disk and cause a stale compile.
                    Console.Error.WriteLine(
                        "[DialogWatchdog] Auto-dismissing modified-outside dialog " +
                        $"(pid={winPid}, title='{t}')");
                    if (!ClickButton(hWnd, "Reload &All")
                        && !ClickButton(hWnd, "&Reload")
                        && !ClickButton(hWnd, "Yes to &All")
                        && !ClickButton(hWnd, "&Yes"))
                    {
                        Console.Error.WriteLine(
                            "[DialogWatchdog] ... no known Reload/Yes button found; leaving dialog");
                    }
                    return true;
                }
            }

            // Legacy variant seen on older TwinCAT shells.
            if (t == "Conflicting File Modification Detected")
            {
                Console.Error.WriteLine(
                    $"[DialogWatchdog] Auto-dismissing 'Conflicting File Modification' dialog (pid={winPid})");
                if (!ClickButton(hWnd, "Reload &All"))
                    ClickButton(hWnd, "&Ignore");
                return true;
            }

            // ADS-layer activation error. We return the error from the ADS
            // exception already; the dialog is redundant and blocks progress.
            if (t == "Target system reports a fatal error")
            {
                Console.Error.WriteLine(
                    $"[DialogWatchdog] Auto-dismissing 'Target fatal error' dialog (pid={winPid})");
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

        private static bool ClickButton(IntPtr dialog, string buttonText)
        {
            IntPtr btn = FindChildButton(dialog, buttonText);
            if (btn != IntPtr.Zero)
            {
                SendMessage(btn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                return true;
            }
            return false;
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
