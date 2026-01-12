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
        public static int Execute(
            string solutionPath, 
            string amsNetId, 
            string? plcName = null,
            string? tcVersion = null,
            bool skipBuild = false,
            bool dryRun = false)
        {
            VisualStudioInstance? vsInstance = null;
            var steps = new List<object>();
            
            try
            {
                // Validate AMS Net ID
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
                steps.Add(new { step = 1, action = "Found TwinCAT project", tcVersion = projectTcVersion });
                
                // Load Visual Studio
                vsInstance = new VisualStudioInstance(solutionPath, projectTcVersion, tcVersion);
                vsInstance.Load();
                vsInstance.LoadSolution();
                steps.Add(new { step = 2, action = "Loaded solution" });

                // Get automation interface
                var automationInterface = new AutomationInterface(vsInstance);
                
                if (automationInterface.PlcTreeItem.ChildCount <= 0)
                {
                    OutputError("No PLC project found in TwinCAT project");
                    return 1;
                }
                
                int plcCount = automationInterface.PlcTreeItem.ChildCount;
                steps.Add(new { step = 3, action = $"Found {plcCount} PLC project(s)" });

                // Build solution (unless skipped)
                if (!skipBuild)
                {
                    if (!dryRun)
                    {
                        vsInstance.CleanSolution();
                        vsInstance.BuildSolution();
                        
                        // Check for build errors
                        var errorItems = vsInstance.GetErrorItems();
                        int errorCount = CountBuildErrors(errorItems);
                        
                        if (errorCount > 0)
                        {
                            var errors = CollectErrors(errorItems);
                            OutputError($"Build failed with {errorCount} error(s)", errors);
                            return 1;
                        }
                    }
                    steps.Add(new { step = 4, action = "Build completed", dryRun = dryRun });
                }
                else
                {
                    steps.Add(new { step = 4, action = "Build skipped" });
                }

                // Set target AMS Net ID
                if (!dryRun)
                {
                    automationInterface.TargetNetId = amsNetId;
                }
                steps.Add(new { step = 5, action = $"Set target to {amsNetId}", dryRun = dryRun });

                // Configure boot project for each PLC
                var deployedPlcs = new List<string>();
                bool foundTargetPlc = false;
                
                for (int i = 1; i <= automationInterface.PlcTreeItem.ChildCount; i++)
                {
                    ITcSmTreeItem plcProject = automationInterface.PlcTreeItem.Child[i];
                    
                    // If plcName specified, only deploy to that PLC
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
                    OutputError($"PLC '{plcName}' not found. Available: {string.Join(", ", availablePlcs)}");
                    return 1;
                }
                
                steps.Add(new { step = 6, action = "Activated boot project", plcs = deployedPlcs, dryRun = dryRun });

                // Activate configuration
                if (!dryRun)
                {
                    automationInterface.ActivateConfiguration();
                    System.Threading.Thread.Sleep(5000);
                }
                steps.Add(new { step = 7, action = "Configuration activated", dryRun = dryRun });

                // Restart TwinCAT
                if (!dryRun)
                {
                    automationInterface.StartRestartTwinCAT();
                    System.Threading.Thread.Sleep(10000);
                }
                steps.Add(new { step = 8, action = "TwinCAT restarted", dryRun = dryRun });

                // Output result
                var result = new
                {
                    success = true,
                    solution = solutionPath,
                    targetNetId = amsNetId,
                    deployedPlcs = deployedPlcs,
                    dryRun = dryRun,
                    steps = steps,
                    message = dryRun 
                        ? $"DRY RUN: Would deploy to {amsNetId}" 
                        : $"Successfully deployed to {amsNetId}"
                };

                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
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
}
