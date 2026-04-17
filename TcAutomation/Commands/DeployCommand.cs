using System;
using System.Collections.Generic;
using System.Text.Json;
using EnvDTE80;
using TCatSysManagerLib;
using TcAutomation.Core;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Full deployment workflow: build, set target, activate boot project, activate config, restart TwinCAT
    /// </summary>
    public class DeployCommand
    {
        /// <summary>
        /// CLI entrypoint: opens a one-shot VS instance, runs the deploy workflow,
        /// closes it, and writes the JSON result to stdout.
        /// </summary>
        public static int Execute(
            string solutionPath, 
            string amsNetId, 
            string? plcName = null,
            string? tcVersion = null,
            bool skipBuild = false,
            bool dryRun = false)
        {
            if (!IsValidAmsNetId(amsNetId))
            {
                OutputError($"Invalid AMS Net ID format: {amsNetId}. Expected format: x.x.x.x.x.x");
                return 1;
            }

            string tcProjectPath = TcFileUtilities.FindTwinCATProjectFile(solutionPath);
            if (string.IsNullOrEmpty(tcProjectPath))
            {
                OutputError("Could not find TwinCAT project file in solution");
                return 1;
            }
            string projectTcVersion = TcFileUtilities.GetTcVersion(tcProjectPath);

            VisualStudioInstance? vsInstance = null;
            try
            {
                vsInstance = new VisualStudioInstance(solutionPath, projectTcVersion, tcVersion);
                vsInstance.Load();
                vsInstance.LoadSolution();

                var result = ExecuteInSession(vsInstance, solutionPath, amsNetId, plcName, skipBuild, dryRun);

                // Keep camelCase to match the legacy CLI output format.
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }));
                return result.Success ? 0 : 1;
            }
            catch (Exception ex)
            {
                OutputError($"Deployment failed: {ex.Message}");
                return 1;
            }
            finally
            {
                vsInstance?.Close();
            }
        }

        /// <summary>
        /// Runs the deploy workflow against an already-open VS instance. Used by
        /// the persistent shell host so the ~30s TcXaeShell cold-start is paid
        /// once per MCP session instead of per deploy.
        ///
        /// NOTE: This mutates the solution on disk (BootProjectAutostart=true on
        /// deployed PLCs, target netId). Callers that want to keep the in-memory
        /// solution pristine should call VisualStudioInstance.ReloadSolution after.
        /// </summary>
        public static DeployResult ExecuteInSession(
            VisualStudioInstance vsInstance,
            string solutionPath,
            string amsNetId,
            string? plcName = null,
            bool skipBuild = false,
            bool dryRun = false)
        {
            var steps = new List<object>();
            var result = new DeployResult
            {
                Solution = solutionPath,
                TargetNetId = amsNetId,
                DryRun = dryRun,
                Steps = steps
            };

            try
            {
                if (!IsValidAmsNetId(amsNetId))
                {
                    result.Success = false;
                    result.ErrorMessage = $"Invalid AMS Net ID format: {amsNetId}. Expected format: x.x.x.x.x.x";
                    return result;
                }

                string tcProjectPath = TcFileUtilities.FindTwinCATProjectFile(solutionPath);
                if (string.IsNullOrEmpty(tcProjectPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "Could not find TwinCAT project file in solution";
                    return result;
                }
                string projectTcVersion = TcFileUtilities.GetTcVersion(tcProjectPath);
                steps.Add(new { step = 1, action = "Found TwinCAT project", tcVersion = projectTcVersion });
                steps.Add(new { step = 2, action = "Reused shared solution" });

                var automationInterface = new AutomationInterface(vsInstance);
                if (automationInterface.PlcTreeItem.ChildCount <= 0)
                {
                    result.Success = false;
                    result.ErrorMessage = "No PLC project found in TwinCAT project";
                    return result;
                }
                int plcCount = automationInterface.PlcTreeItem.ChildCount;
                steps.Add(new { step = 3, action = $"Found {plcCount} PLC project(s)" });

                if (!skipBuild)
                {
                    if (!dryRun)
                    {
                        vsInstance.CleanSolution();
                        vsInstance.BuildSolution();

                        var errorItems = vsInstance.GetErrorItems();
                        int errorCount = CountBuildErrors(errorItems);
                        if (errorCount > 0)
                        {
                            result.Success = false;
                            result.ErrorMessage = $"Build failed with {errorCount} error(s)";
                            result.BuildErrors = CollectErrors(errorItems);
                            return result;
                        }
                    }
                    steps.Add(new { step = 4, action = "Build completed", dryRun = dryRun });
                }
                else
                {
                    steps.Add(new { step = 4, action = "Build skipped" });
                }

                if (!dryRun)
                {
                    automationInterface.TargetNetId = amsNetId;
                }
                steps.Add(new { step = 5, action = $"Set target to {amsNetId}", dryRun = dryRun });

                var deployedPlcs = new List<string>();
                bool foundTargetPlc = false;

                for (int i = 1; i <= automationInterface.PlcTreeItem.ChildCount; i++)
                {
                    ITcSmTreeItem plcProject = automationInterface.PlcTreeItem.Child[i];

                    if (!string.IsNullOrEmpty(plcName) &&
                        !plcProject.Name.Equals(plcName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foundTargetPlc = true;

                    if (!dryRun)
                    {
                        ITcPlcProject iecProject = (ITcPlcProject)plcProject;
                        iecProject.BootProjectAutostart = true;
                        iecProject.GenerateBootProject(true);
                    }

                    deployedPlcs.Add(plcProject.Name);
                }

                if (!string.IsNullOrEmpty(plcName) && !foundTargetPlc)
                {
                    var availablePlcs = new List<string>();
                    for (int i = 1; i <= automationInterface.PlcTreeItem.ChildCount; i++)
                    {
                        availablePlcs.Add(automationInterface.PlcTreeItem.Child[i].Name);
                    }
                    result.Success = false;
                    result.ErrorMessage = $"PLC '{plcName}' not found. Available: {string.Join(", ", availablePlcs)}";
                    return result;
                }

                steps.Add(new { step = 6, action = "Activated boot project", plcs = deployedPlcs, dryRun = dryRun });

                if (!dryRun)
                {
                    automationInterface.ActivateConfiguration();
                    System.Threading.Thread.Sleep(5000);
                }
                steps.Add(new { step = 7, action = "Configuration activated", dryRun = dryRun });

                if (!dryRun)
                {
                    automationInterface.StartRestartTwinCAT();
                    System.Threading.Thread.Sleep(10000);
                }
                steps.Add(new { step = 8, action = "TwinCAT restarted", dryRun = dryRun });

                result.Success = true;
                result.DeployedPlcs = deployedPlcs;
                result.Message = dryRun
                    ? $"DRY RUN: Would deploy to {amsNetId}"
                    : $"Successfully deployed to {amsNetId}";
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Deployment failed: {ex.Message}";
                return result;
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

        private static int CountBuildErrors(ErrorItems errorItems)
        {
            int errorCount = 0;
            for (int i = 1; i <= errorItems.Count; i++)
            {
                ErrorItem item = errorItems.Item(i);
                if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh)
                {
                    errorCount++;
                }
            }
            return errorCount;
        }

        private static List<object> CollectErrors(ErrorItems errorItems)
        {
            var errors = new List<object>();
            for (int i = 1; i <= errorItems.Count; i++)
            {
                ErrorItem item = errorItems.Item(i);
                if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh)
                {
                    errors.Add(new
                    {
                        description = item.Description,
                        file = item.FileName,
                        line = item.Line
                    });
                }
            }
            return errors;
        }

        private static void OutputError(string message, List<object>? errors = null)
        {
            var result = new { success = false, error = message, errors = errors };
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
    }

    /// <summary>
    /// Strongly-typed deploy result used by both the CLI entry point and the
    /// persistent host path. Success / ErrorMessage naming matches the other
    /// session-result types so StepDispatcher can inspect them uniformly.
    /// </summary>
    public class DeployResult
    {
        public bool Success { get; set; }
        public string Solution { get; set; } = "";
        public string TargetNetId { get; set; } = "";
        public List<string> DeployedPlcs { get; set; } = new List<string>();
        public bool DryRun { get; set; }
        public List<object> Steps { get; set; } = new List<object>();
        public string? Message { get; set; }
        public string? ErrorMessage { get; set; }
        public List<object>? BuildErrors { get; set; }
    }
}
