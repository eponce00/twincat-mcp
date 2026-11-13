using System;
using System.Diagnostics;
using System.Text.Json;
using TwinCAT.Ads;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Ping a TwinCAT target over ADS and classify its reachability.
    ///
    /// The goal is to give the agent a reliable way to tell three states
    /// apart, because a plain "connection refused" error from any of the
    /// other commands is indistinguishable:
    ///   - reachable        → AMS router on target answers; runtime may
    ///                        or may not be in Run state (returned via
    ///                        `runtimeState` on port 851).
    ///   - unreachable      → AMS route exists locally but the remote
    ///                        router doesn't answer. Target is probably
    ///                        off, firewalled, or cable pulled.
    ///   - config           → the system service (port 10000) reports
    ///                        Config: TwinCAT is stopped in Config mode,
    ///                        not rebooting.
    ///   - stopped          → the system is in Run and the PLC runtime
    ///                        answers but is not in Run (Stop).
    ///   - runtimeUnavailable → the system is in Run but nothing answers
    ///                        on the runtime port (no PLC loaded there).
    ///   - error            → the system service reports Error.
    ///   - rebooting        → a state is changing: a transitional state
    ///                        (Start, Init, Reset, Reconfig, ...) or two
    ///                        samples about SettleMs apart differ.
    ///   - routeMissing     → The local AMS router has no route to the
    ///                        target AMS Net ID. This is a setup issue,
    ///                        not a runtime one.
    ///
    /// All checks have explicit timeouts so this command never hangs —
    /// the single biggest reason we needed a dedicated ping tool was
    /// that other commands would block for the full shell timeout when
    /// the target crashed.
    /// </summary>
    public static class PingTargetCommand
    {
        private const int DefaultTimeoutMs = 2500;
        // Second sample, to tell a stable state from one that is changing.
        private const int SettleMs = 300;
        // States a running or stopped target holds; anything else is on its way somewhere.
        private static readonly string[] StableStates = { "Run", "Config", "Stop", "Error" };

        public static int Execute(string amsNetId, int runtimePort, int timeoutMs)
        {
            var result = new PingTargetResult
            {
                AmsNetId = amsNetId,
                RuntimePort = runtimePort,
                TimeoutMs = timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs
            };

            try
            {
                // Step 1: Try the AMS system service on port 10000. This
                // is the target's router/OS-level service — it answers
                // even if the PLC runtime is stopped, so it's the right
                // probe for "is the machine up at all?".
                var sysProbe = ProbePort(amsNetId, (int)AmsPort.SystemService, result.TimeoutMs);
                result.SystemServiceReachable = sysProbe.Reachable;
                result.SystemServiceState = sysProbe.AdsState;
                result.SystemServiceDurationMs = sysProbe.DurationMs;
                if (!string.IsNullOrEmpty(sysProbe.Error))
                    result.SystemServiceError = sysProbe.Error;

                // If the system service didn't answer we classify once
                // and short-circuit — no point probing the runtime.
                if (!sysProbe.Reachable)
                {
                    result.Classification = ClassifyNoSystemService(sysProbe);
                    result.Message = BuildMessage(result);
                    result.Success = true; // the probe itself succeeded
                    Emit(result);
                    return 0;
                }

                // Step 2: Probe the PLC runtime port (default 851). A
                // healthy system service but dead runtime means the OS
                // is up but TwinCAT is rebooting, stopped, or crashed.
                var rtProbe = ProbePort(amsNetId, runtimePort, result.TimeoutMs);
                result.RuntimeReachable = rtProbe.Reachable;
                result.RuntimeState = rtProbe.AdsState;
                result.RuntimeDurationMs = rtProbe.DurationMs;
                if (!string.IsNullOrEmpty(rtProbe.Error))
                    result.RuntimeError = rtProbe.Error;

                // Step 3: unless everything is in Run, sample both again. A
                // state that differs between the samples, or a transitional
                // state, is a target that is changing; otherwise the states
                // are reported as they are (a target stopped in Config is
                // not rebooting).
                result.StateChanging = !IsStable(sysProbe.AdsState) || (rtProbe.Reachable && !IsStable(rtProbe.AdsState));
                if (!(sysProbe.AdsState == "Run" && rtProbe.Reachable && rtProbe.AdsState == "Run"))
                {
                    System.Threading.Thread.Sleep(SettleMs);
                    var sys2 = ProbePort(amsNetId, (int)AmsPort.SystemService, result.TimeoutMs);
                    var rt2 = ProbePort(amsNetId, runtimePort, result.TimeoutMs);
                    result.Samples = 2;
                    if (sys2.Reachable != sysProbe.Reachable || sys2.AdsState != sysProbe.AdsState
                        || rt2.Reachable != rtProbe.Reachable || rt2.AdsState != rtProbe.AdsState)
                        result.StateChanging = true;
                    if (sys2.Reachable)
                    {
                        sysProbe = sys2;
                        result.SystemServiceState = sys2.AdsState;
                    }
                    rtProbe = rt2;
                    result.RuntimeReachable = rt2.Reachable;
                    result.RuntimeState = rt2.AdsState;
                    result.RuntimeError = string.IsNullOrEmpty(rt2.Error) ? null : rt2.Error;
                }

                result.Classification = ClassifyWithSystemService(sysProbe, rtProbe, result.StateChanging);
                result.Message = BuildMessage(result);
                result.Success = true;
                Emit(result);
                return 0;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                Emit(result);
                return 1;
            }
        }

        private static ProbeResult ProbePort(string amsNetId, int port, int timeoutMs)
        {
            var probe = new ProbeResult();
            var sw = Stopwatch.StartNew();
            try
            {
                using var client = new AdsClient();
                client.Timeout = timeoutMs;
                client.Connect(amsNetId, port);
                if (!client.IsConnected)
                {
                    probe.Reachable = false;
                    probe.Error = "Connect returned but IsConnected=false";
                    return probe;
                }

                // ReadState is the cheapest ADS call that actually
                // round-trips to the remote router. Connect alone just
                // opens a local handle — it doesn't prove the target is
                // answering.
                var state = client.ReadState();
                probe.Reachable = true;
                probe.AdsState = state.AdsState.ToString();
                return probe;
            }
            catch (AdsErrorException ex)
            {
                probe.Reachable = false;
                probe.AdsErrorCode = (int)ex.ErrorCode;
                probe.Error = $"ADS {ex.ErrorCode}: {ex.Message}";
                return probe;
            }
            catch (Exception ex)
            {
                probe.Reachable = false;
                probe.Error = ex.Message;
                return probe;
            }
            finally
            {
                sw.Stop();
                probe.DurationMs = (int)sw.ElapsedMilliseconds;
            }
        }

        private static string ClassifyNoSystemService(ProbeResult sysProbe)
        {
            // ADS error 0x7 = target port not found (route missing on
            // local router). 0x745 = target machine not found. 0x748 =
            // timeout (no response from remote router).
            if (sysProbe.AdsErrorCode == 0x7 || sysProbe.AdsErrorCode == 0x6)
                return "routeMissing";
            return "unreachable";
        }

        private static bool IsStable(string? state) => state != null && Array.IndexOf(StableStates, state) >= 0;

        private static string ClassifyWithSystemService(ProbeResult sysProbe, ProbeResult rtProbe, bool changing)
        {
            if (changing)
                return "rebooting";
            if (sysProbe.AdsState == "Config")
                return "config";
            if (sysProbe.AdsState == "Error")
                return "error";
            if (rtProbe.Reachable && rtProbe.AdsState == "Run")
                return "reachable";
            if (rtProbe.Reachable)
                return "stopped"; // System in Run; the runtime answers but is stopped
            // System in Run and steady, but nothing answers on the runtime port.
            return "runtimeUnavailable";
        }

        private static string BuildMessage(PingTargetResult r)
        {
            switch (r.Classification)
            {
                case "reachable":
                    return $"Target {r.AmsNetId} reachable; runtime in Run ({r.RuntimeDurationMs}ms).";
                case "rebooting":
                    return
                        $"Target {r.AmsNetId} is changing state (system {r.SystemServiceState ?? "unknown"}, " +
                        $"runtime {r.RuntimeState ?? "not answering"}): rebooting, starting or just activated. " +
                        "Retry in a few seconds.";
                case "config":
                    return
                        $"Target {r.AmsNetId} is in Config mode (port 10000 reports Config); the PLC runtime " +
                        "is not running. It is not rebooting: activate the configuration or restart TwinCAT to Run.";
                case "stopped":
                    return
                        $"Target {r.AmsNetId} system is in Run; the PLC runtime on port {r.RuntimePort} " +
                        $"is {r.RuntimeState} (steady). Start the PLC to run it.";
                case "runtimeUnavailable":
                    return
                        $"Target {r.AmsNetId} system is in Run, but nothing answers on port {r.RuntimePort} " +
                        "(steady): no PLC runtime is loaded on that port.";
                case "error":
                    return $"Target {r.AmsNetId} system service reports Error. Check the target's TwinCAT log.";
                case "unreachable":
                    return
                        $"Target {r.AmsNetId} not answering on the AMS " +
                        "system service. Target is likely powered off, " +
                        "the network cable is pulled, a firewall is in " +
                        "the way, or (after a crash) the Windows OS " +
                        "hasn't come back yet.";
                case "routeMissing":
                    return
                        $"Local AMS router has no route to {r.AmsNetId}. " +
                        "Add the route in TwinCAT System Manager → " +
                        "Routes before retrying.";
                default:
                    return $"Target {r.AmsNetId} status unknown.";
            }
        }

        private static void Emit(PingTargetResult r)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                r, new JsonSerializerOptions { WriteIndented = true }));
        }

        private class ProbeResult
        {
            public bool Reachable { get; set; }
            public string? AdsState { get; set; }
            public int DurationMs { get; set; }
            public int AdsErrorCode { get; set; }
            public string? Error { get; set; }
        }
    }

    public class PingTargetResult
    {
        public string AmsNetId { get; set; } = "";
        public int RuntimePort { get; set; }
        public int TimeoutMs { get; set; }
        public bool Success { get; set; }
        public string Classification { get; set; } = "";
        public string Message { get; set; } = "";

        public bool SystemServiceReachable { get; set; }
        /// <summary>The system service's own ADS state (port 10000), e.g. Run or Config.</summary>
        public string? SystemServiceState { get; set; }
        public int SystemServiceDurationMs { get; set; }
        /// <summary>True when a state is transitional or changed between the two samples.</summary>
        public bool StateChanging { get; set; }
        /// <summary>Probe rounds taken (2 unless everything was already in Run).</summary>
        public int Samples { get; set; } = 1;
        public string? SystemServiceError { get; set; }

        public bool RuntimeReachable { get; set; }
        public string? RuntimeState { get; set; }
        public int RuntimeDurationMs { get; set; }
        public string? RuntimeError { get; set; }

        public string? ErrorMessage { get; set; }
    }
}
