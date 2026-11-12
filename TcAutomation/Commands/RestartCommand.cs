using System;
using System.Text.Json;
using System.Collections.Generic;
using TCatSysManagerLib;
using TcAutomation.Core;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Restarts TwinCAT runtime on the target PLC
    /// </summary>
    public class RestartCommand
    {
        public static int Execute(string solutionPath, string? amsNetId, string? tcVersion, int runTimeoutSeconds = 120)
        {
            VisualStudioInstance? vsInstance = null;
            
            try
            {
                // Find TwinCAT project
                string tcProjectPath = TcFileUtilities.FindTwinCATProjectFile(solutionPath);
                if (string.IsNullOrEmpty(tcProjectPath))
                {
                    OutputError("Could not find TwinCAT project file in solution");
                    return 1;
                }

                string projectTcVersion = TcFileUtilities.GetTcVersion(tcProjectPath);
                
                // Load Visual Studio
                vsInstance = new VisualStudioInstance(solutionPath, projectTcVersion, tcVersion);
                vsInstance.Load();
                vsInstance.LoadSolution();

                var result = ExecuteInSession(vsInstance, solutionPath, amsNetId, runTimeoutSeconds);
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                return result.Success ? 0 : 1;
            }
            catch (Exception ex)
            {
                OutputError($"Restart failed: {ex.Message}");
                return 1;
            }
            finally
            {
                vsInstance?.Close();
            }
        }

        /// <summary>
        /// Restart TwinCAT on target using an already-open VS instance. Used by batch mode.
        /// </summary>
        public static RestartResult ExecuteInSession(VisualStudioInstance vsInstance, string solutionPath, string? amsNetId, int runTimeoutSeconds = 120)
        {
            var result = new RestartResult { Solution = solutionPath };
            try
            {
                var automationInterface = new AutomationInterface(vsInstance);

                string targetNetId = amsNetId ?? automationInterface.TargetNetId;
                if (!string.IsNullOrEmpty(amsNetId))
                {
                    automationInterface.TargetNetId = amsNetId;
                }

                // Wait for Run on the system service and every PLC that starts by itself.
                var ports = new List<int>();
                for (int i = 1; i <= automationInterface.PlcTreeItem.ChildCount; i++)
                {
                    var plc = automationInterface.PlcTreeItem.Child[i];
                    bool autostart;
                    try { autostart = ((ITcPlcProject)plc).BootProjectAutostart; } catch { autostart = false; }
                    int? port = PlcPorts.Of(plc);
                    if (autostart && port != null) ports.Add(port.Value);
                }
                var run = RunWait.RestartAndWait(targetNetId, ports, automationInterface.StartRestartTwinCAT,
                    new RunWaitOptions { Timeout = TimeSpan.FromSeconds(runTimeoutSeconds) },
                    message => Console.Error.WriteLine($"[PROGRESS] run: {message}"));
                result.TargetNetId = targetNetId;
                result.Run = run;
                result.Success = run.ReachedRun;
                if (run.ReachedRun)
                    result.Message = $"TwinCAT restarted on {targetNetId}; {run.Message}";
                else
                    result.ErrorMessage = run.Message + RunWait.Summary(run);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Restart failed: {ex.Message}";
            }
            return result;
        }

        private static void OutputError(string message)
        {
            var result = new { success = false, error = message };
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
    }

    public class RestartResult
    {
        public bool Success { get; set; }
        public string Solution { get; set; } = "";
        public string TargetNetId { get; set; } = "";
        public string? Message { get; set; }
        public string? ErrorMessage { get; set; }
        public RunWaitResult? Run { get; set; }
    }
}
