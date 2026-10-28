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

                var result = ExecuteInSession(vsInstance, solutionPath);
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                return result.Success ? 0 : 1;
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

        /// <summary>
        /// Clean an already-loaded solution. Used by batch mode.
        /// </summary>
        public static CleanResult ExecuteInSession(VisualStudioInstance vsInstance, string solutionPath)
        {
            var result = new CleanResult { Solution = solutionPath };
            using var deadline = new OwnedDeadline(() => vsInstance.KillOwnedProcess("clean operation deadline"), 180);
            try
            {
                vsInstance.CleanSolution();
                result.RetainedArtifacts = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(
                    System.Linq.Enumerable.Where(BuildArtifacts.Capture(vsInstance), a => a.Exists), a => a.Path));
                result.Success = true;
                deadline.Complete();
                result.Message = "Solution clean completed. Retained TMC files are listed; their presence does not prove a current successful build.";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.OutcomeUnknown = deadline.Expired;
                result.ErrorMessage = $"Clean failed: {ex.Message}";
            }
            return result;
        }

        private static void OutputError(string message)
        {
            var result = new { success = false, error = message };
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
    }

    public class CleanResult
    {
        public bool OutcomeUnknown { get; set; }
        public string[] RetainedArtifacts { get; set; } = new string[0];
        public bool Success { get; set; }
        public string Solution { get; set; } = "";
        public string? Message { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
