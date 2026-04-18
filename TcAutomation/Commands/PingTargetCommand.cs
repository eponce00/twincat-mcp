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
    ///   - rebooting        → AMS route answers on system service
    ///                        (port 10000) but the PLC runtime (port
    ///                        851) is not in Run state yet.
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

                result.Classification = ClassifyWithSystemService(rtProbe);
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

        private static string ClassifyWithSystemService(ProbeResult rtProbe)
        {
            if (rtProbe.Reachable && rtProbe.AdsState == "Run")
                return "reachable";
            if (rtProbe.Reachable)
                return "rebooting"; // OS up, runtime not in Run
            // System service up but runtime port doesn't answer at all —
            // PLC is either stopped, starting, or recovering from a crash.
            return "rebooting";
        }

        private static string BuildMessage(PingTargetResult r)
        {
            switch (r.Classification)
            {
                case "reachable":
                    return $"Target {r.AmsNetId} reachable; runtime in Run ({r.RuntimeDurationMs}ms).";
                case "rebooting":
                    return
                        $"Target {r.AmsNetId} OS is up but TwinCAT runtime " +
                        $"is {r.RuntimeState ?? "unresponsive"} — " +
                        "likely rebooting, stopped, or just activated. " +
                        "Retry in a few seconds.";
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
        public int SystemServiceDurationMs { get; set; }
        public string? SystemServiceError { get; set; }

        public bool RuntimeReachable { get; set; }
        public string? RuntimeState { get; set; }
        public int RuntimeDurationMs { get; set; }
        public string? RuntimeError { get; set; }

        public string? ErrorMessage { get; set; }
    }
}
