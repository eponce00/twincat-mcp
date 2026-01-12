using System;
using System.Collections.Generic;
using System.IO;
using EnvDTE80;
using TCatSysManagerLib;
using TcAutomation.Core;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Command to check all objects in PLC projects, including unused ones.
    /// This catches errors in function blocks that aren't called anywhere.
    /// </summary>
    public class CheckAllObjectsCommand
    {
        public class CheckAllObjectsResult
        {
            public bool Success { get; set; }
            public string Message { get; set; }
            public string ErrorMessage { get; set; }
            public int PlcCount { get; set; }
            public List<PlcCheckResult> PlcResults { get; set; } = new List<PlcCheckResult>();
            public List<ErrorItem> Errors { get; set; } = new List<ErrorItem>();
            public List<ErrorItem> Warnings { get; set; } = new List<ErrorItem>();
            public int ErrorCount { get; set; }
            public int WarningCount { get; set; }
        }

        public class PlcCheckResult
        {
            public string Name { get; set; }
            public bool Success { get; set; }
            public string Error { get; set; }
        }

        public class ErrorItem
        {
            public string FileName { get; set; }
            public int Line { get; set; }
            public int Column { get; set; }
            public string ErrorCode { get; set; }
            public string Description { get; set; }
            public string Project { get; set; }
        }

        public static CheckAllObjectsResult Execute(string solutionPath, string plcName = null, string tcVersion = null)
        {
            var result = new CheckAllObjectsResult();
            VisualStudioInstance vsInstance = null;

            try
            {
                // Validate input
                if (!File.Exists(solutionPath))
                {
                    result.Success = false;
                    result.ErrorMessage = $"Solution file not found: {solutionPath}";
                    return result;
                }

                // Register COM message filter
                MessageFilter.Register();

                // Find TwinCAT project
                var tcProjectPath = TcFileUtilities.FindTwinCATProjectFile(solutionPath);
                if (string.IsNullOrEmpty(tcProjectPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "No TwinCAT project (.tsproj) found in solution";
                    return result;
                }

                // Get TwinCAT version
                var projectTcVersion = TcFileUtilities.GetTcVersion(tcProjectPath);
                if (string.IsNullOrEmpty(projectTcVersion))
                {
                    result.Success = false;
                    result.ErrorMessage = "Could not determine TwinCAT version from project";
                    return result;
                }

                // Load Visual Studio
                vsInstance = new VisualStudioInstance(solutionPath, projectTcVersion, tcVersion);
                vsInstance.Load();
                vsInstance.LoadSolution();

                var systemManager = vsInstance.GetSystemManager();

                // Get PLC node
                ITcSmTreeItem plcNode;
                try
                {
                    plcNode = systemManager.LookupTreeItem("TIPC");
                }
                catch
                {
                    result.Success = false;
                    result.ErrorMessage = "No PLC configuration found in solution";
                    return result;
                }

                // Iterate through PLC projects
                int plcCount = 0;
                foreach (ITcSmTreeItem plcProject in plcNode)
                {
                    // Skip if specific PLC requested and this isn't it
                    if (!string.IsNullOrEmpty(plcName) && 
                        !plcProject.Name.Equals(plcName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    plcCount++;
                    var plcResult = new PlcCheckResult { Name = plcProject.Name };

                    try
                    {
                        // Get the nested project (contains the actual PLC code)
                        ITcProjectRoot projectRoot = (ITcProjectRoot)plcProject;
                        ITcSmTreeItem nestedProject = projectRoot.NestedProject;

                        // Cast to ITcPlcIECProject2 for CheckAllObjects
                        ITcPlcIECProject2 iecProject2 = nestedProject as ITcPlcIECProject2;

                        if (iecProject2 != null)
                        {
                            // Call CheckAllObjects - this compiles all objects including unused ones
                            iecProject2.CheckAllObjects();
                            plcResult.Success = true;
                        }
                        else
                        {
                            plcResult.Success = false;
                            plcResult.Error = "ITcPlcIECProject2 interface not available (requires TwinCAT 3.1 Build 4018+)";
                        }
                    }
                    catch (Exception ex)
                    {
                        plcResult.Success = false;
                        plcResult.Error = ex.Message;
                    }

                    result.PlcResults.Add(plcResult);
                }

                result.PlcCount = plcCount;

                if (plcCount == 0)
                {
                    result.Success = false;
                    result.ErrorMessage = string.IsNullOrEmpty(plcName) 
                        ? "No PLC projects found" 
                        : $"PLC project '{plcName}' not found";
                    return result;
                }

                // Wait for error list to populate
                System.Threading.Thread.Sleep(1000);

                // Collect errors from Visual Studio Error List
                try
                {
                    var errorItems = vsInstance.GetErrorItems();

                    for (int i = 1; i <= errorItems.Count; i++)
                    {
                        var item = errorItems.Item(i);
                        var errorItem = new ErrorItem
                        {
                            FileName = item.FileName ?? "",
                            Line = item.Line,
                            Column = item.Column,
                            Description = item.Description ?? "",
                            Project = item.Project ?? ""
                        };

                        // Try to extract error code from description
                        if (!string.IsNullOrEmpty(errorItem.Description))
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(
                                errorItem.Description, @"^([A-Z]\d+):");
                            if (match.Success)
                            {
                                errorItem.ErrorCode = match.Groups[1].Value;
                            }
                        }

                        // Categorize by severity
                        if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh)
                        {
                            result.Errors.Add(errorItem);
                        }
                        else if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelMedium)
                        {
                            result.Warnings.Add(errorItem);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Error list access failed, but check might have succeeded
                    Console.Error.WriteLine($"Warning: Could not read error list: {ex.Message}");
                }

                result.ErrorCount = result.Errors.Count;
                result.WarningCount = result.Warnings.Count;

                // Determine overall success
                bool allPlcsOk = result.PlcResults.TrueForAll(p => p.Success);
                result.Success = allPlcsOk && result.ErrorCount == 0;

                if (result.Success)
                {
                    result.Message = $"Check all objects completed successfully ({plcCount} PLC project(s), {result.WarningCount} warning(s))";
                }
                else if (result.ErrorCount > 0)
                {
                    result.Message = $"Check all objects found {result.ErrorCount} error(s) and {result.WarningCount} warning(s)";
                }
                else
                {
                    result.Message = "Check all objects completed with issues";
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }
            finally
            {
                vsInstance?.Close();
                MessageFilter.Revoke();
            }

            return result;
        }
    }
}
