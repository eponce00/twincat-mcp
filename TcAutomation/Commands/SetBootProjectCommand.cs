using System;
using System.Text.Json;
using TcAutomation.Core;
using TCatSysManagerLib;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Configures boot project settings for PLC projects.
    /// Enables/disables autostart and generates boot project on target.
    /// </summary>
    public static class SetBootProjectCommand
    {
        public static int Execute(string solutionPath, string? tcVersion, string? plcName, bool enableAutostart, bool generateBoot)
        {
            VisualStudioInstance? vsInstance = null;
            var result = new SetBootProjectResult();

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

                result = ExecuteInSession(vsInstance, solutionPath, plcName, enableAutostart, generateBoot);
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
        /// Configure boot project using an already-open VS instance. Used by batch mode.
        /// </summary>
        public static SetBootProjectResult ExecuteInSession(VisualStudioInstance vsInstance, string solutionPath, string? plcName, bool enableAutostart, bool generateBoot)
        {
            var result = new SetBootProjectResult { SolutionPath = solutionPath };

            try
            {
                var automation = new AutomationInterface(vsInstance);

                if (automation.PlcTreeItem.ChildCount <= 0)
                {
                    result.ErrorMessage = "No PLC projects found in solution";
                    return result;
                }

                bool foundTarget = false;

                for (int i = 1; i <= automation.PlcTreeItem.ChildCount; i++)
                {
                    var plcProject = automation.PlcTreeItem.Child[i];

                    if (!string.IsNullOrEmpty(plcName) &&
                        !plcProject.Name.Equals(plcName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foundTarget = true;
                    var plcResult = new PlcBootResult { Name = plcProject.Name };

                    try
                    {
                        var iecProject = (ITcPlcProject)plcProject;

                        iecProject.BootProjectAutostart = enableAutostart;
                        plcResult.AutostartEnabled = enableAutostart;

                        if (generateBoot)
                        {
                            iecProject.GenerateBootProject(true);
                            plcResult.BootProjectGenerated = true;
                        }

                        plcResult.Success = true;
                    }
                    catch (Exception ex)
                    {
                        plcResult.Error = ex.Message;
                        plcResult.Success = false;
                    }

                    result.PlcResults.Add(plcResult);
                }

                if (!string.IsNullOrEmpty(plcName) && !foundTarget)
                {
                    result.ErrorMessage = $"PLC '{plcName}' not found in solution";
                    return result;
                }

                result.Success = result.PlcResults.TrueForAll(p => p.Success);
                if (!result.Success && string.IsNullOrEmpty(result.ErrorMessage))
                {
                    var failed = result.PlcResults.Find(p => !p.Success);
                    result.ErrorMessage = failed?.Error ?? "Boot project configuration failed";
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }

            return result;
        }
    }

    public class SetBootProjectResult
    {
        public string SolutionPath { get; set; } = "";
        public bool Success { get; set; }
        public System.Collections.Generic.List<PlcBootResult> PlcResults { get; set; } = new System.Collections.Generic.List<PlcBootResult>();
        public string? ErrorMessage { get; set; }
    }

    public class PlcBootResult
    {
        public string Name { get; set; } = "";
        public bool Success { get; set; }
        public bool AutostartEnabled { get; set; }
        public bool BootProjectGenerated { get; set; }
        public string? Error { get; set; }
    }
}
