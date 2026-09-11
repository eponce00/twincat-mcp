using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using TCatSysManagerLib;
using TcAutomation.Core;
using TwinCAT.Ads;

namespace TcAutomation.Commands
{
    public static class MatchingLoginCommand
    {
        public static MatchingLoginResult ExecuteInSession(VisualStudioInstance vs,
            string target, string plcName, int port, string configuration, string platform)
        {
            var result = new MatchingLoginResult();
            try
            {
                var automation = new AutomationInterface(vs);
                if (string.IsNullOrWhiteSpace(target) || automation.TargetNetId != target ||
                    string.IsNullOrWhiteSpace(plcName) || plcName.Contains("^") || port < 851 || port > 899)
                    throw new ArgumentException("Select the explicit target and PLC before preparing login.");
                var root = automation.SystemManager.LookupTreeItem("TIPC^" + plcName);
                var rootXml = XDocument.Parse(root.ProduceXml(false));
                if (rootXml.Root?.Element("PlcProjectDef")?.Element("AdsPort")?.Value != port.ToString())
                    throw new InvalidOperationException("Configured ADS port mismatch.");
                var plc = ((ITcProjectRoot)root).NestedProject;
                if (string.IsNullOrWhiteSpace(configuration) || string.IsNullOrWhiteSpace(platform))
                    throw new ArgumentException("Explicit baseline configuration and platform required.");
                var active = (EnvDTE80.SolutionConfiguration2)vs.Dte.Solution.SolutionBuild.ActiveConfiguration;
                bool changeConfiguration = active.Name != configuration || active.PlatformName != platform;
                foreach (ITcSmTreeItem other in automation.PlcTreeItem)
                    if (IsLoggedIn(((ITcProjectRoot)other).NestedProject) &&
                        (other.PathName != root.PathName || changeConfiguration))
                        throw new InvalidOperationException("Another PLC is logged in or a logged-in configuration would change.");
                if (changeConfiguration)
                {
                    EnvDTE80.SolutionConfiguration2 selected = null;
                    foreach (EnvDTE.SolutionConfiguration candidate in vs.Dte.Solution.SolutionBuild.SolutionConfigurations)
                    {
                        var value = (EnvDTE80.SolutionConfiguration2)candidate;
                        if (value.Name == configuration && value.PlatformName == platform) selected = value;
                    }
                    if (selected == null) throw new ArgumentException("Requested baseline configuration/platform not found.");
                    selected.Activate();
                }
                active = (EnvDTE80.SolutionConfiguration2)vs.Dte.Solution.SolutionBuild.ActiveConfiguration;
                if (active.Name != configuration || active.PlatformName != platform)
                    throw new InvalidOperationException("Baseline configuration/platform selection was not verified.");
                result.Configuration = active.Name; result.Platform = active.PlatformName;
                RequireUnchanged(plc, beforeLogin: true);
                using (var ads = new AdsClient())
                {
                    ads.Timeout = 1000;
                    ads.Connect(target, port);
                    if (ads.ReadState().AdsState != AdsState.Run)
                        throw new InvalidOperationException("The existing application must be RUN.");
                    var before = ReadCount(ads);
                    var settings = (ITcAutomationSettings)vs.Dte.GetObject("TcAutomationSettings");
                    bool silent = settings.SilentMode, suppress = vs.Dte.SuppressUI;
                    using (var guard = new RejectDownloadDialogs(vs.DteProcessId ?? throw new InvalidOperationException("Owned XAE PID required")))
                    {
                        try
                        {
                            // Matching login has no dialog. Let XAE ask on mismatch,
                            // then cancel; never use silent-mode download defaults.
                            settings.SilentMode = false;
                            vs.Dte.SuppressUI = false;
                            if (!IsLoggedIn(plc))
                            {
                                result.LoginAttempted = true;
                                plc.ConsumeXml("<TreeItem><IECProjectDef><OnlineSettings><Commands><LoginCmd>true</LoginCmd></Commands></OnlineSettings></IECProjectDef></TreeItem>");
                            }
                            var timer = Stopwatch.StartNew();
                            while (!IsLoggedIn(plc) && !guard.Seen && timer.ElapsedMilliseconds < 30000)
                                Thread.Sleep(50);
                            result.PromptRejected = guard.Seen;
                            if (guard.Seen || !IsLoggedIn(plc))
                                throw new InvalidOperationException("Matching login was not established. A change/download prompt is not approved by this operation.");
                            RequireUnchanged(plc);
                            if (ads.ReadState().AdsState != AdsState.Run || ReadCount(ads) != before)
                                throw new InvalidOperationException("Runtime state changed during login; inspect before proceeding.");
                            result.LoggedIn = true;
                            result.Success = true;
                        }
                        finally
                        {
                            result.PromptRejected = guard.Seen;
                            settings.SilentMode = silent; vs.Dte.SuppressUI = suppress;
                            if (!result.Success && result.LoginAttempted && IsLoggedIn(plc))
                                plc.ConsumeXml("<TreeItem><IECProjectDef><OnlineSettings><Commands><LogoutCmd>true</LogoutCmd></Commands></OnlineSettings></IECProjectDef></TreeItem>");
                        }
                    }
                }
            }
            catch (Exception ex) { result.Success = false; result.ErrorMessage = ex.Message; }
            return result;
        }
        static uint ReadCount(AdsClient ads)
        {
            uint h = ads.CreateVariableHandle("TwinCAT_SystemInfoVarList._AppInfo.OnlineChangeCnt");
            try { return (uint)ads.ReadAny(h, typeof(uint)); }
            finally { ads.DeleteVariableHandle(h); }
        }
        internal static bool IsLoggedIn(ITcSmTreeItem plc) =>
            XDocument.Parse(plc.ProduceXml(false)).Root?.Element("IECProjectDef")?.Element("OnlineSettings")?.Element("LoggedIn")?.Value == "true";
        static void RequireUnchanged(ITcSmTreeItem plc, bool beforeLogin = false)
        {
            var state = XDocument.Parse(plc.ProduceXml(false)).Descendants("VSProperty")
                .FirstOrDefault(p => p.Element("Name")?.Value == "CodeState")?.Element("Value")?.Value;
            // A newly opened offline XAE session has no online reference yet.
            // The non-silent login compares it with the runtime; mismatch dialogs
            // are cancelled. Only the post-login "Code unchanged" state passes.
            if (state != "Code unchanged" && !(beforeLogin && state == "No reference code generated"))
                throw new InvalidOperationException("Prepare login from unchanged baseline source and retained compile information, before editing. CodeState: " + state);
        }
    }
    public class MatchingLoginResult
    {
        public bool Success { get; set; }
        public bool LoginAttempted { get; set; }
        public bool LoggedIn { get; set; }
        public bool PromptRejected { get; set; }
        public string Configuration { get; set; }
        public string Platform { get; set; }
        public string ErrorMessage { get; set; }
    }

    // Only cancellation, only dialogs from the owned XAE process. Unknown dialog
    // variants may block until the host timeout; they are never approved.
    sealed class RejectDownloadDialogs : IDisposable
    {
        delegate bool EnumProc(IntPtr window, IntPtr arg);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr arg);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr arg);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
        readonly int pid;
        readonly Thread thread;
        volatile bool running = true;
        public volatile bool Seen;
        public RejectDownloadDialogs(int pid)
        {
            this.pid = pid;
            thread = new Thread(() => { while (running) { try { EnumWindows(Inspect, IntPtr.Zero); } catch { } Thread.Sleep(50); } });
            thread.IsBackground = true; thread.Start();
        }
        bool Inspect(IntPtr window, IntPtr unused)
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != pid) return true;
            var cls = new StringBuilder(256); GetClassName(window, cls, cls.Capacity);
            if (cls.ToString() != "#32770" && !cls.ToString().StartsWith("WindowsForms10.Window")) return true;
            bool changes = false; IntPtr cancel = IntPtr.Zero;
            EnumChildWindows(window, (child, arg) =>
            {
                var text = new StringBuilder(1024); GetWindowText(child, text, text.Capacity);
                string value = text.ToString().Replace("&", "").ToLowerInvariant();
                if (value.Contains("download") || value.Contains("online change") || value.Contains("overwrite")) changes = true;
                var childClass = new StringBuilder(128); GetClassName(child, childClass, childClass.Capacity);
                if (childClass.ToString().IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0 && value == "cancel") cancel = child;
                return true;
            }, IntPtr.Zero);
            if (changes)
            {
                Seen = true;
                if (cancel != IntPtr.Zero) PostMessage(cancel, 0xF5, IntPtr.Zero, IntPtr.Zero); // BM_CLICK
                else PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
            }
            return true;
        }
        public void Dispose() { running = false; thread.Join(1000); }
    }
}
