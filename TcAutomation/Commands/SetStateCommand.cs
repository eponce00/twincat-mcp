using System;
using System.Text.Json;
using TwinCAT.Ads;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Sets the TwinCAT runtime state (Run, Stop, Config) via direct ADS connection.
    /// Note: Remote state changes may be limited depending on target device configuration.
    /// For full control, use the restart command with Automation Interface.
    /// </summary>
    public static class SetStateCommand
    {
        public static int Execute(string amsNetId, int port, string targetState)
        {
            var result = new SetStateResult
            {
                AmsNetId = amsNetId,
                Port = port,
                RequestedState = targetState
            };

            try
            {
                // Parse the target state
                if (!TryParseState(targetState, out AdsState adsState))
                {
                    result.ErrorMessage = $"Invalid state '{targetState}'. Valid states: Run, Stop, Config, Reset";
                    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                    return 1;
                }

                // For system state changes, use port 10000 (TwinCAT System Service)
                int effectivePort = port;
                if (port == 851 && (adsState == AdsState.Config || adsState == AdsState.Run))
                {
                    // System state changes need to go to the system service
                    effectivePort = 10000;
                    result.Port = effectivePort;
                }

                using (var adsClient = new AdsClient())
                {
                    // Connect to the target
                    adsClient.Connect(amsNetId, effectivePort);
                    
                    if (!adsClient.IsConnected)
                    {
                        result.ErrorMessage = $"Failed to connect to {amsNetId}:{effectivePort}";
                        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                        return 1;
                    }

                    // Read current state first
                    var currentStateInfo = adsClient.ReadState();
                    result.PreviousState = currentStateInfo.AdsState.ToString();

                    // Check if already in desired state
                    if (currentStateInfo.AdsState == adsState)
                    {
                        result.CurrentState = currentStateInfo.AdsState.ToString();
                        result.StateDescription = GetStateDescription(currentStateInfo.AdsState);
                        result.Success = true;
                        result.Warning = "Already in requested state";
                        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                        return 0;
                    }

                    // Write the new state
                    try
                    {
                        adsClient.WriteControl(new StateInfo(adsState, currentStateInfo.DeviceState));
                    }
                    catch (AdsErrorException ex) when (ex.ErrorCode == AdsErrorCode.DeviceServiceNotSupported)
                    {
                        result.ErrorMessage = $"State change not supported via ADS on this target. Use 'twincat_restart' (which uses Automation Interface) for remote state changes, or control the target locally.";
                        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                        return 1;
                    }

                    // Give it a moment to transition
                    System.Threading.Thread.Sleep(1000);

                    // Read back the new state
                    var newStateInfo = adsClient.ReadState();
                    result.CurrentState = newStateInfo.AdsState.ToString();
                    result.StateDescription = GetStateDescription(newStateInfo.AdsState);
                    result.Success = true;

                    // Check if transition succeeded
                    if (newStateInfo.AdsState != adsState)
                    {
                        // Some transitions go through intermediate states
                        result.Warning = $"State is {newStateInfo.AdsState}, may still be transitioning to {targetState}";
                    }
                }

                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            catch (AdsErrorException ex)
            {
                result.ErrorMessage = $"ADS Error: {ex.ErrorCode} - {ex.Message}";
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                return 1;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                return 1;
            }
        }

        private static bool TryParseState(string state, out AdsState adsState)
        {
            adsState = AdsState.Invalid;
            
            switch (state.ToLowerInvariant())
            {
                case "run":
                case "running":
                    adsState = AdsState.Run;
                    return true;
                case "stop":
                case "stopped":
                    adsState = AdsState.Stop;
                    return true;
                case "config":
                case "configuration":
                    adsState = AdsState.Config;
                    return true;
                case "reset":
                    adsState = AdsState.Reset;
                    return true;
                case "reconfig":
                    adsState = AdsState.Reconfig;
                    return true;
                default:
                    return false;
            }
        }

        private static string GetStateDescription(AdsState state)
        {
            switch (state)
            {
                case AdsState.Invalid: return "Invalid state";
                case AdsState.Idle: return "Idle - System idle";
                case AdsState.Reset: return "Reset - System reset";
                case AdsState.Init: return "Init - Initializing";
                case AdsState.Start: return "Start - Starting up";
                case AdsState.Run: return "Run - Running normally 🟢";
                case AdsState.Stop: return "Stop - Stopped 🔴";
                case AdsState.SaveConfig: return "SaveConfig - Saving configuration";
                case AdsState.LoadConfig: return "LoadConfig - Loading configuration";
                case AdsState.PowerFailure: return "PowerFailure - Power failure detected";
                case AdsState.PowerGood: return "PowerGood - Power restored";
                case AdsState.Error: return "Error - Error state ⚠️";
                case AdsState.Shutdown: return "Shutdown - Shutting down";
                case AdsState.Suspend: return "Suspend - Suspended";
                case AdsState.Resume: return "Resume - Resuming";
                case AdsState.Config: return "Config - Configuration mode 🔧";
                case AdsState.Reconfig: return "Reconfig - Reconfiguring";
                default: return $"Unknown state: {state}";
            }
        }
    }

    public class SetStateResult
    {
        public string AmsNetId { get; set; } = "";
        public int Port { get; set; }
        public string RequestedState { get; set; } = "";
        public string PreviousState { get; set; } = "";
        public string CurrentState { get; set; } = "";
        public string StateDescription { get; set; } = "";
        public bool Success { get; set; }
        public string? Warning { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
