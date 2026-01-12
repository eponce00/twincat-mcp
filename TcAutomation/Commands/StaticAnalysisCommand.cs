using System;
using System.Collections.Generic;
using System.IO;
using EnvDTE80;
using TCatSysManagerLib;
using TcAutomation.Core;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Command to run static code analysis on PLC projects.
    /// Requires TE1200 license for full functionality.
    /// </summary>
    public class StaticAnalysisCommand
    {
        public class StaticAnalysisResult
        {
            public bool Success { get; set; }
            public string Message { get; set; }
            public string ErrorMessage { get; set; }
            public int PlcCount { get; set; }
            public bool CheckedAllObjects { get; set; }
            public List<PlcAnalysisResult> PlcResults { get; set; } = new List<PlcAnalysisResult>();
            public List<AnalysisItem> Errors { get; set; } = new List<AnalysisItem>();
            public List<AnalysisItem> Warnings { get; set; } = new List<AnalysisItem>();
            public int ErrorCount { get; set; }
            public int WarningCount { get; set; }
        }

        public class PlcAnalysisResult
        {
            public string Name { get; set; }
            public bool Success { get; set; }
            public string Error { get; set; }
        }

        public class AnalysisItem
        {
            public string FileName { get; set; }
            public int Line { get; set; }
            public int Column { get; set; }
            public string RuleId { get; set; }
            public string Description { get; set; }
            public string Project { get; set; }
        }

        /// <summary>
        /// Run static analysis on PLC projects.
        /// </summary>
        /// <param name="solutionPath">Path to .sln file</param>
        /// <param name="checkAll">If true, check all objects including unused ones</param>
        /// <param name="plcName">Optional: specific PLC project to analyze</param>
        /// <param name="tcVersion">Optional: specific TwinCAT version</param>
        public static StaticAnalysisResult Execute(string solutionPath, bool checkAll = true, string plcName = null, string tcVersion = null)
        {
            var result = new StaticAnalysisResult();
            result.CheckedAllObjects = checkAll;
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
                    var plcResult = new PlcAnalysisResult { Name = plcProject.Name };

                    try
                    {
                        // Get the nested project (contains the actual PLC code)
                        ITcProjectRoot projectRoot = (ITcProjectRoot)plcProject;
                        ITcSmTreeItem nestedProject = projectRoot.NestedProject;

                        // Try ITcPlcIECProject3 first (TwinCAT 3.1 Build 4024+)
                        ITcPlcIECProject3 iecProject3 = nestedProject as ITcPlcIECProject3;

                        if (iecProject3 != null)
                        {
                            // RunStaticAnalysis() without params = check all objects
                            // RunStaticAnalysis(true) = check all objects
                            // RunStaticAnalysis(false) = check only used objects
                            if (checkAll)
                            {
                                iecProject3.RunStaticAnalysis();
                            }
                            else
                            {
                                // Pass false to check only used objects
                                try
                                {
                                    // Try the overload with parameter
                                    dynamic dynamicProject = nestedProject;
                                    dynamicProject.RunStaticAnalysis(false);
                                }
                                catch
                                {
                                    // Fall back to parameterless version
                                    iecProject3.RunStaticAnalysis();
                                }
                            }
                            plcResult.Success = true;
                        }
                        else
                        {
                            plcResult.Success = false;
                            plcResult.Error = "ITcPlcIECProject3 interface not available. Requires TwinCAT 3.1 Build 4024+ and TE1200 license.";
                        }
                    }
                    catch (Exception ex)
                    {
                        plcResult.Success = false;
                        
                        // Check for common errors
                        if (ex.Message.Contains("license") || ex.Message.Contains("TE1200"))
                        {
                            plcResult.Error = "Static Analysis requires TE1200 license";
                        }
                        else
                        {
                            plcResult.Error = ex.Message;
                        }
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

                // Wait for analysis to complete and collect results
                System.Threading.Thread.Sleep(2000);

                try
                {
                    var errorItems = vsInstance.GetErrorItems();

                    for (int i = 1; i <= errorItems.Count; i++)
                    {
                        var item = errorItems.Item(i);
                        var analysisItem = new AnalysisItem
                        {
                            FileName = item.FileName ?? "",
                            Line = item.Line,
                            Column = item.Column,
                            Description = item.Description ?? "",
                            Project = item.Project ?? ""
                        };

                        // Extract rule ID (SA0001, SA0002, etc.)
                        if (!string.IsNullOrEmpty(analysisItem.Description))
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(
                                analysisItem.Description, @"\b(SA\d+)\b");
                            if (match.Success)
                            {
                                analysisItem.RuleId = match.Groups[1].Value;
                            }
                            else
                            {
                                // Try other error code patterns
                                var altMatch = System.Text.RegularExpressions.Regex.Match(
                                    analysisItem.Description, @"^([A-Z]\d+):");
                                if (altMatch.Success)
                                {
                                    analysisItem.RuleId = altMatch.Groups[1].Value;
                                }
                            }
                        }

                        // Categorize by severity
                        if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh)
                        {
                            result.Errors.Add(analysisItem);
                        }
                        else if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelMedium)
                        {
                            result.Warnings.Add(analysisItem);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Warning: Could not read error list: {ex.Message}");
                }

                result.ErrorCount = result.Errors.Count;
                result.WarningCount = result.Warnings.Count;

                // Determine overall success
                bool allPlcsOk = result.PlcResults.TrueForAll(p => p.Success);
                
                if (!allPlcsOk)
                {
                    result.Success = false;
                    var failedPlc = result.PlcResults.Find(p => !p.Success);
                    result.ErrorMessage = failedPlc?.Error ?? "Static analysis failed";
                }
                else
                {
                    result.Success = true;
                    string scope = checkAll ? "all objects" : "used objects";
                    result.Message = $"Static analysis completed ({scope}): {result.ErrorCount} error(s), {result.WarningCount} warning(s)";
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
