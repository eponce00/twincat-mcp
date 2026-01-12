using System;
using System.Text.Json;
using TcAutomation.Core;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Cleans a TwinCAT solution (removes build artifacts)
    /// </summary>
    public class CleanCommand
    {
        public static int Execute(string solutionPath, string? tcVersion)
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

                // Clean the solution
                vsInstance.CleanSolution();

                // Output result
                var result = new
                {
                    success = true,
                    solution = solutionPath,
                    message = "Solution cleaned successfully"
                };

                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            catch (Exception ex)
            {
                OutputError($"Clean failed: {ex.Message}");
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
