using System;
using System.Text.Json;
using TcAutomation.Core;
using TCatSysManagerLib;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Gets or sets the TwinCAT Project Variant.
    /// Requires TwinCAT XAE 4024+ (TCatSysManagerLib V 3.3.0.0 or later).
    /// </summary>
    public static class SetVariantCommand
    {
        public static int Execute(string solutionPath, string? tcVersion, string? variantName, bool getOnly)
        {
            VisualStudioInstance? vsInstance = null;
            var result = new SetVariantResult();

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

                result = ExecuteInSession(vsInstance, solutionPath, variantName, getOnly);
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
        /// Get/set project variant using an already-open VS instance. Used by batch mode.
        /// </summary>
        public static SetVariantResult ExecuteInSession(VisualStudioInstance vsInstance, string solutionPath, string? variantName, bool getOnly)
        {
            var result = new SetVariantResult { SolutionPath = solutionPath };

            try
            {
                var automation = new AutomationInterface(vsInstance);

                ITcSysManager14 sysManager14;
                try
                {
                    sysManager14 = (ITcSysManager14)automation.SystemManager;
                }
                catch (InvalidCastException)
                {
                    result.ErrorMessage = "Project variants require TwinCAT XAE 4024 or later";
                    return result;
                }

                result.PreviousVariant = sysManager14.CurrentProjectVariant ?? "";

                if (getOnly || string.IsNullOrEmpty(variantName))
                {
                    result.CurrentVariant = result.PreviousVariant;
                    result.Success = true;
                    result.Message = string.IsNullOrEmpty(result.CurrentVariant)
                        ? "No project variant set (using default)"
                        : $"Current variant: {result.CurrentVariant}";
                }
                else
                {
                    try
                    {
                        sysManager14.CurrentProjectVariant = variantName;
                        result.CurrentVariant = sysManager14.CurrentProjectVariant ?? "";
                        result.Success = true;
                        result.Message = $"Project variant changed from '{result.PreviousVariant}' to '{result.CurrentVariant}'";
                    }
                    catch (Exception ex)
                    {
                        result.ErrorMessage = $"Failed to set variant '{variantName}': {ex.Message}";
                    }
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }

            return result;
        }
    }

    public class SetVariantResult
    {
        public string SolutionPath { get; set; } = "";
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string PreviousVariant { get; set; } = "";
        public string CurrentVariant { get; set; } = "";
        public string? ErrorMessage { get; set; }
    }
}
