using System;
using System.Text.Json;
using TcAutomation.Core;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Sets the target AMS Net ID for deployment without activating
    /// </summary>
    public class SetTargetCommand
    {
        public static int Execute(string solutionPath, string amsNetId, string? tcVersion)
        {
            VisualStudioInstance? vsInstance = null;
            
            try
            {
                // Validate AMS Net ID format (basic check)
                if (!IsValidAmsNetId(amsNetId))
                {
                    OutputError($"Invalid AMS Net ID format: {amsNetId}. Expected format: x.x.x.x.x.x");
                    return 1;
                }

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

                // Get automation interface and set target
                var automationInterface = new AutomationInterface(vsInstance);
                
                string previousTarget = automationInterface.TargetNetId;
                automationInterface.TargetNetId = amsNetId;

                // Output result
                var result = new
                {
                    success = true,
                    solution = solutionPath,
                    previousTarget = previousTarget,
                    newTarget = amsNetId,
                    message = $"Target AMS Net ID set to {amsNetId}"
                };

                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            catch (Exception ex)
            {
                OutputError($"Set target failed: {ex.Message}");
                return 1;
            }
            finally
            {
                vsInstance?.Close();
            }
        }

        private static bool IsValidAmsNetId(string amsNetId)
        {
            if (string.IsNullOrWhiteSpace(amsNetId))
                return false;
                
            var parts = amsNetId.Split('.');
            if (parts.Length != 6)
                return false;
                
            foreach (var part in parts)
            {
                if (!int.TryParse(part, out int value) || value < 0 || value > 255)
                    return false;
            }
            
            return true;
        }

        private static void OutputError(string message)
        {
            var result = new { success = false, error = message };
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
    }
}
