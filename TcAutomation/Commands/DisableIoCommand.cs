using System;
using System.Text.Json;
using TcAutomation.Core;
using TCatSysManagerLib;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Disables all top-level I/O devices in the TwinCAT configuration.
    /// Useful when running tests on a different machine than the target PLC,
    /// where the physical I/O hardware is not present.
    /// </summary>
    public static class DisableIoCommand
    {
        private const string IO_DEVICES_SHORTCUT = "TIID"; // I/O Devices tree item shortcut

        public static int Execute(string solutionPath, string? tcVersion, bool enable)
        {
            VisualStudioInstance? vsInstance = null;
            var result = new DisableIoResult();

            try
            {
                // Find TwinCAT project and version
                string tsprojPath = TcFileUtilities.FindTwinCATProjectFile(solutionPath);
                if (string.IsNullOrEmpty(tsprojPath))
                {
                    result.ErrorMessage = "Could not find TwinCAT project file (.tsproj) in solution";
                    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                    return 1;
                }

                string projectTcVersion = TcFileUtilities.GetTcVersion(tsprojPath);

                // Load Visual Studio
                vsInstance = new VisualStudioInstance(solutionPath, projectTcVersion, tcVersion);
                vsInstance.Load();
                vsInstance.LoadSolution();

                result = ExecuteInSession(vsInstance, solutionPath, enable);
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                return result.Success ? 0 : 1;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                return 1;
            }
            finally
            {
                vsInstance?.Close();
            }
        }

        /// <summary>
        /// Disable/enable I/O devices using an already-open VS instance. Used by batch mode.
        /// </summary>
        public static DisableIoResult ExecuteInSession(VisualStudioInstance vsInstance, string solutionPath, bool enable)
        {
            var result = new DisableIoResult { SolutionPath = solutionPath };

            try
            {
                var automation = new AutomationInterface(vsInstance);

                ITcSmTreeItem ioDevicesRoot;
                try
                {
                    ioDevicesRoot = automation.SystemManager.LookupTreeItem(IO_DEVICES_SHORTCUT);
                }
                catch
                {
                    result.ErrorMessage = "I/O Devices tree not found in project";
                    return result;
                }

                int childCount = ioDevicesRoot.ChildCount;
                result.TotalDevices = childCount;

                if (childCount == 0)
                {
                    result.Success = true;
                    result.Message = "No I/O devices found to modify";
                    return result;
                }

                DISABLED_STATE targetState = enable ? (DISABLED_STATE)0 : DISABLED_STATE.SMDS_DISABLED;
                string action = enable ? "enabled" : "disabled";

                for (int i = 1; i <= childCount; i++)
                {
                    ITcSmTreeItem device = ioDevicesRoot.Child[i];
                    var deviceResult = new IoDeviceResult { Name = device.Name };

                    try
                    {
                        DISABLED_STATE currentState = device.Disabled;
                        deviceResult.PreviousState = currentState.ToString();

                        if (currentState == targetState)
                        {
                            deviceResult.Action = $"Already {action}";
                            deviceResult.Modified = false;
                        }
                        else
                        {
                            device.Disabled = targetState;
                            deviceResult.Action = action;
                            deviceResult.Modified = true;
                            result.ModifiedCount++;
                        }

                        deviceResult.CurrentState = device.Disabled.ToString();
                    }
                    catch (Exception ex)
                    {
                        deviceResult.Error = ex.Message;
                    }

                    result.Devices.Add(deviceResult);
                }

                result.Success = true;
                result.Message = result.ModifiedCount > 0
                    ? $"{result.ModifiedCount} device(s) {action}"
                    : $"All {result.TotalDevices} device(s) already {action} - no changes needed";
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }

            return result;
        }
    }

    public class DisableIoResult
    {
        public string SolutionPath { get; set; } = "";
        public bool Success { get; set; }
        public string? Message { get; set; }
        public int TotalDevices { get; set; }
        public int ModifiedCount { get; set; }
        public System.Collections.Generic.List<IoDeviceResult> Devices { get; set; } = new System.Collections.Generic.List<IoDeviceResult>();
        public string? ErrorMessage { get; set; }
    }

    public class IoDeviceResult
    {
        public string Name { get; set; } = "";
        public string? PreviousState { get; set; }
        public string? CurrentState { get; set; }
        public string? Action { get; set; }
        public bool Modified { get; set; }
        public string? Error { get; set; }
    }
}
