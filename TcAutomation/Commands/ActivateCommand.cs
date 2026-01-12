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

                // Get automation interface
                var automationInterface = new AutomationInterface(vsInstance);

                // Set target if provided
                string targetNetId = amsNetId ?? automationInterface.TargetNetId;
                if (!string.IsNullOrEmpty(amsNetId))
                {
                    automationInterface.TargetNetId = amsNetId;
                }

                // Activate configuration
                automationInterface.ActivateConfiguration();
                
                // Wait for activation to complete
                System.Threading.Thread.Sleep(5000);

                // Output result
                var result = new
                {
                    success = true,
                    solution = solutionPath,
                    targetNetId = targetNetId,
                    message = $"Configuration activated on {targetNetId}"
                };

                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
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

        private static void OutputError(string message)
        {
            var result = new { success = false, error = message };
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
    }
}
