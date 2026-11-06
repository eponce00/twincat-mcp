using System;
using System.Text.Json;
using TcAutomation.Core;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Activates TwinCAT configuration on the target PLC
    /// </summary>
    public class ActivateCommand
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
                OutputError($"Activation failed: {ex.Message}");
                return 1;
            }
            finally
            {
                vsInstance?.Close();
            }
        }

        /// <summary>
        /// Activate configuration on target using an already-open VS instance. Used by batch mode.
        /// </summary>
        public static ActivateResult ExecuteInSession(VisualStudioInstance vsInstance, string solutionPath, string? amsNetId)
        {
            var result = new ActivateResult { Solution = solutionPath };
            try
            {
                var automationInterface = new AutomationInterface(vsInstance);

                string targetNetId = amsNetId ?? automationInterface.TargetNetId;
                if (!string.IsNullOrEmpty(amsNetId))
                {
                    automationInterface.TargetNetId = amsNetId;
                }

                // Wait for the target to finish switching to Config mode and
                // writing its boot configuration instead of a fixed sleep.
                var activation = ActivationWait.ActivateAndWait(
                    targetNetId,
                    automationInterface.ActivateConfiguration,
                    message => Console.Error.WriteLine($"[PROGRESS] activate: {message}"));

                result.TargetNetId = targetNetId;
                result.Activation = activation;
                result.Success = activation.Completed;
                if (activation.Completed)
                {
                    result.Message = activation.Verified
                        ? $"Configuration activated on {targetNetId} (verified by {activation.Signal} after {activation.ElapsedMs / 1000.0:0.0} s)"
                        : $"Configuration activated on {targetNetId} (completion unverified)";
                }
                else
                {
                    result.ErrorMessage = activation.ErrorMessage;
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Activation failed: {ex.Message}";
            }
            return result;
        }

        private static void OutputError(string message)
        {
            var result = new { success = false, error = message };
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
    }

    public class ActivateResult
    {
        public bool Success { get; set; }
        public string Solution { get; set; } = "";
        public string TargetNetId { get; set; } = "";
        public string? Message { get; set; }
        public string? ErrorMessage { get; set; }
        public ActivationWaitResult? Activation { get; set; }
    }
}
