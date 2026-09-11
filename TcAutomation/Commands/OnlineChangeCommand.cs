using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using TCatSysManagerLib;
using TcAutomation.Core;
using TwinCAT.Ads;

namespace TcAutomation.Commands
{
    /// <summary>Apply once in an already logged-in engineering session. No login,
    /// download, clean, activation, restart or boot-project update is performed.</summary>
    public static class OnlineChangeCommand
    {
        public static OnlineChangeResult ExecuteInSession(VisualStudioInstance vs,
            string amsNetId, string plcName, int port, string cycleSymbol,
            uint expectedOnlineChangeCount, int timeoutMs = 10000)
        {
            var result = new OnlineChangeResult { TargetNetId = amsNetId, Port = port, PlcName = plcName };
            try
            {
                if (string.IsNullOrWhiteSpace(amsNetId) || string.IsNullOrWhiteSpace(plcName) ||
                    plcName.Contains("^") || port < 851 || port > 899 ||
                    string.IsNullOrWhiteSpace(cycleSymbol) || timeoutMs < 100 || timeoutMs > 60000)
                    throw new ArgumentException("Explicit target, PLC name, port 851..899, ULINT cycle symbol and timeout 100..60000 ms required.");
                var automation = new AutomationInterface(vs);
                if (automation.TargetNetId != amsNetId)
                    throw new InvalidOperationException("Engineering target does not match; select the target separately before login.");
                var root = automation.SystemManager.LookupTreeItem("TIPC^" + plcName);
                var xml = XDocument.Parse(root.ProduceXml(false));
                var configuredPort = xml.Root?.Element("PlcProjectDef")?.Element("AdsPort")?.Value;
                if (configuredPort != port.ToString())
                    throw new InvalidOperationException("PLC ADS port could not be verified from the selected project.");
                var plc = ((ITcProjectRoot)root).NestedProject;
                RequireLogin(plc);
                // XAE PLC editors do not reliably expose DTE ProjectItem ownership.
                // Online Change can act only on an online project. Require this
                // to be the sole logged-in PLC, so an available command cannot
                // dispatch to another online application in this solution.
                foreach (ITcSmTreeItem candidate in automation.PlcTreeItem)
                {
                    if (candidate.PathName == root.PathName) continue;
                    var other = ((ITcProjectRoot)candidate).NestedProject;
                    var status = XDocument.Parse(other.ProduceXml(false)).Root?.Element("IECProjectDef")?.Element("OnlineSettings");
                    if (status?.Element("LoggedIn")?.Value == "true")
                        throw new InvalidOperationException("Another PLC is logged in; Online Change requires an unambiguous sole online PLC.");
                }
                string command = null;
                foreach (var name in new[] { "PLC.OnlineChangenone", "OtherContextMenus.Plc2Projects.OnlineChange" })
                {
                    try { if (vs.Dte.Commands.Item(name).IsAvailable) { command = name; break; } }
                    catch (System.Runtime.InteropServices.COMException) { }
                }
                if (command == null)
                    throw new InvalidOperationException("Online Change unavailable; preserve matching compile information. No download fallback.");
                using (var ads = new AdsClient())
                {
                    ads.Timeout = 1000;
                    ads.Connect(amsNetId, port);
                    RequireRun(ads);
                    result.CountBefore = Read<uint>(ads, "TwinCAT_SystemInfoVarList._AppInfo.OnlineChangeCnt");
                    if (result.CountBefore != expectedOnlineChangeCount)
                        throw new InvalidOperationException("Online-change count differs from the expected baseline; do not replay an uncertain operation.");
                    result.CyclesBefore = Read<ulong>(ads, cycleSymbol);
                    Thread.Sleep(50);
                    if (Read<ulong>(ads, cycleSymbol) <= result.CyclesBefore)
                        throw new InvalidOperationException("Cyclic execution is not advancing before dispatch.");
                    RequireLogin(plc);
                    result.Command = command;
                    // Mark before calling: COM may throw after the target applied it.
                    result.DispatchAttempted = true;
                    vs.Dte.ExecuteCommand(command, "");
                    result.Dispatched = true;
                    var timer = Stopwatch.StartNew();
                    while (timer.ElapsedMilliseconds < timeoutMs)
                    {
                        RequireRun(ads);
                        // Fresh handles: online change can invalidate cached handles.
                        result.CountAfter = Read<uint>(ads, "TwinCAT_SystemInfoVarList._AppInfo.OnlineChangeCnt");
                        result.CyclesAfter = Read<ulong>(ads, cycleSymbol);
                        if (result.CountAfter == unchecked(expectedOnlineChangeCount + 1) &&
                            result.CyclesAfter > result.CyclesBefore)
                        {
                            result.RuntimeVerified = true;
                            result.Success = true;
                            return result;
                        }
                        if (result.CountAfter != expectedOnlineChangeCount)
                            throw new InvalidOperationException("Unexpected online-change count or reset cycle counter.");
                        Thread.Sleep(20);
                    }
                    throw new TimeoutException("Online Change was dispatched but the runtime outcome was not verified in time.");
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                result.OutcomeUnknown = result.DispatchAttempted;
            }
            return result;
        }

        private static void RequireLogin(ITcSmTreeItem plc)
        {
            var status = XDocument.Parse(plc.ProduceXml(false)).Root?.Element("IECProjectDef")?.Element("OnlineSettings");
            if (status?.Element("LoggedIn")?.Value != "true" || status?.Element("PlcAppState")?.Value != "Run")
                throw new InvalidOperationException("The selected PLC must already be logged in and running with matching compile information.");
        }
        private static void RequireRun(AdsClient ads)
        {
            if (ads.ReadState().AdsState != AdsState.Run)
                throw new InvalidOperationException("PLC is not RUN.");
        }
        private static T Read<T>(AdsClient ads, string symbol)
        {
            var info = ads.ReadSymbol(symbol);
            if (info.TypeName != (typeof(T) == typeof(uint) ? "UDINT" : "ULINT"))
                throw new InvalidOperationException("Unexpected verification symbol type: " + symbol);
            var handle = ads.CreateVariableHandle(symbol);
            try { return (T)ads.ReadAny(handle, typeof(T)); }
            finally { ads.DeleteVariableHandle(handle); }
        }
    }
    public class OnlineChangeResult
    {
        public bool Success { get; set; }
        public string Operation => "OnlineChange";
        public string TargetNetId { get; set; }
        public string PlcName { get; set; }
        public int Port { get; set; }
        public string Command { get; set; }
        public string ContextPolicy => "OnlyLoggedInPlc";
        public bool DispatchAttempted { get; set; }
        public bool Dispatched { get; set; }
        public bool RuntimeVerified { get; set; }
        public bool OutcomeUnknown { get; set; }
        public uint CountBefore { get; set; }
        public uint CountAfter { get; set; }
        public ulong CyclesBefore { get; set; }
        public ulong CyclesAfter { get; set; }
        public string ErrorMessage { get; set; }
    }
}
