using System;
using System.Text.Json;
using TcAutomation.Core;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Restarts TwinCAT runtime on the target PLC
    /// </summary>
    public class RestartCommand
    {
        public static int Execute(string solutionPath, string? amsNetId, string? tcVersion)
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

                var result = ExecuteInSession(vsInstance, solutionPath, amsNetId);
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
        public static RestartResult ExecuteInSession(VisualStudioInstance vsInstance, string solutionPath, string? amsNetId)
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

                automationInterface.StartRestartTwinCAT();
                System.Threading.Thread.Sleep(10000);

                result.TargetNetId = targetNetId;
                result.Success = true;
                result.Message = $"TwinCAT restarted on {targetNetId}";
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
    }
}
