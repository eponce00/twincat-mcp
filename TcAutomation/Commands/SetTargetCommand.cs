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

                var result = ExecuteInSession(vsInstance, solutionPath, amsNetId);
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                return result.Success ? 0 : 1;
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

        /// <summary>
        /// Set target AMS Net ID using an already-open VS instance. Used by batch mode.
        /// </summary>
        public static SetTargetResult ExecuteInSession(VisualStudioInstance vsInstance, string solutionPath, string amsNetId)
        {
            var result = new SetTargetResult { Solution = solutionPath, NewTarget = amsNetId };
            try
            {
                if (!IsValidAmsNetId(amsNetId))
                {
                    result.Success = false;
                    result.ErrorMessage = $"Invalid AMS Net ID format: {amsNetId}. Expected format: x.x.x.x.x.x";
                    return result;
                }

                var automationInterface = new AutomationInterface(vsInstance);
                result.PreviousTarget = automationInterface.TargetNetId;
                automationInterface.TargetNetId = amsNetId;
                result.Success = true;
                result.Message = $"Target AMS Net ID set to {amsNetId}";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Set target failed: {ex.Message}";
            }
            return result;
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

    public class SetTargetResult
    {
        public bool Success { get; set; }
        public string Solution { get; set; } = "";
        public string PreviousTarget { get; set; } = "";
        public string NewTarget { get; set; } = "";
        public string? Message { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
