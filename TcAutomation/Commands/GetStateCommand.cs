using System;
using System.Text.Json;
using TwinCAT.Ads;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Gets the TwinCAT runtime state from a target PLC via ADS.
    /// This command connects directly to the PLC without opening Visual Studio.
    /// </summary>
    public static class GetStateCommand
    {
        public static int Execute(string amsNetId, int port)
        {
            var result = new GetStateResult
            {
                AmsNetId = amsNetId,
                Port = port
            };

            try
            {
                using (var adsClient = new AdsClient())
                {
                    // Connect to the target
                    adsClient.Connect(amsNetId, port);
                    
                    if (!adsClient.IsConnected)
                    {
                        result.ErrorMessage = $"Failed to connect to {amsNetId}:{port}";
                        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                        return 1;
                    }

                    // Read the state
                    var stateInfo = adsClient.ReadState();
                    
                    result.AdsState = stateInfo.AdsState.ToString();
                    result.DeviceState = stateInfo.DeviceState;
                    result.IsRunning = stateInfo.AdsState == AdsState.Run;
                    result.Success = true;

                    // Map ADS state to human-readable description
                    result.StateDescription = GetStateDescription(stateInfo.AdsState);
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

        private static string GetStateDescription(AdsState state)
        {
            switch (state)
            {
                case AdsState.Invalid: return "Invalid state";
                case AdsState.Idle: return "Idle - System idle";
                case AdsState.Reset: return "Reset - System reset";
                case AdsState.Init: return "Init - Initializing";
                case AdsState.Start: return "Start - Starting up";
                case AdsState.Run: return "Run - Running normally";
                case AdsState.Stop: return "Stop - Stopped";
                case AdsState.SaveConfig: return "SaveConfig - Saving configuration";
                case AdsState.LoadConfig: return "LoadConfig - Loading configuration";
                case AdsState.PowerFailure: return "PowerFailure - Power failure detected";
                case AdsState.PowerGood: return "PowerGood - Power restored";
                case AdsState.Error: return "Error - Error state";
                case AdsState.Shutdown: return "Shutdown - Shutting down";
                case AdsState.Suspend: return "Suspend - Suspended";
                case AdsState.Resume: return "Resume - Resuming";
                case AdsState.Config: return "Config - Configuration mode";
                case AdsState.Reconfig: return "Reconfig - Reconfiguring";
                default: return $"Unknown state: {state}";
            }
        }
    }

    public class GetStateResult
    {
        public string AmsNetId { get; set; } = "";
        public int Port { get; set; }
        public bool Success { get; set; }
        public string AdsState { get; set; } = "";
        public string StateDescription { get; set; } = "";
        public short DeviceState { get; set; }
        public bool IsRunning { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
