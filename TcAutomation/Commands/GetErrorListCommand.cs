using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;
using TcAutomation.Core;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Get the current contents of the Visual Studio Error List window.
    /// This includes errors, warnings, and messages (like ADS logs from the PLC).
    /// </summary>
    public static class GetErrorListCommand
    {
        public class ErrorListResult
        {
            public bool Success { get; set; }
            public string? ErrorMessage { get; set; }
            public int ErrorCount { get; set; }
            public int WarningCount { get; set; }
            public int MessageCount { get; set; }
            public int TotalCount { get; set; }
            public List<ErrorListItem> Items { get; set; } = new List<ErrorListItem>();
        }

        public class ErrorListItem
        {
            public string Level { get; set; } = "";  // "Error", "Warning", "Message"
            public string Description { get; set; } = "";
            public string FileName { get; set; } = "";
            public int Line { get; set; }
            public int Column { get; set; }
            public string Project { get; set; } = "";
        }

        public static async Task<ErrorListResult> ExecuteAsync(
            string solutionPath, 
            string? tcVersion,
            bool includeMessages = true,
            bool includeWarnings = true,
            bool includeErrors = true,
            int waitSeconds = 0,
            string? contains = null)
        {
            var result = new ErrorListResult();

            // Validate input
            if (!File.Exists(solutionPath))
            {
                result.Success = false;
                result.ErrorMessage = $"Solution file not found: {solutionPath}";
                return result;
            }

            VisualStudioInstance? vsInstance = null;

            try
            {
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

                result = ExecuteInSession(vsInstance, includeMessages, includeWarnings, includeErrors, waitSeconds, contains);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to read error list: {ex.Message}";
            }
            finally
            {
                vsInstance?.Close();
                MessageFilter.Revoke();
            }

            return await Task.FromResult(result);
        }

        /// <summary>
        /// Read the VS error list using an already-open VS instance. Used by batch mode.
        /// </summary>
        public static ErrorListResult ExecuteInSession(
            VisualStudioInstance vsInstance,
            bool includeMessages = true,
            bool includeWarnings = true,
            bool includeErrors = true,
            int waitSeconds = 0,
            string? contains = null)
        {
            var result = new ErrorListResult();

            try
            {
                if (waitSeconds > 0)
                {
                    Thread.Sleep(waitSeconds * 1000);
                }

                var errorItems = vsInstance.GetErrorItems();

                // Case-insensitive substring filter when `contains` is set.
                // Kept client-side of the MCP boundary so the agent doesn't
                // pay the JSON-serialize cost for hundreds of noise
                // messages when it only cares about "FAILED TEST" or
                // "E_SM_Fault".
                bool hasFilter = !string.IsNullOrEmpty(contains);
                string filterLower = hasFilter ? contains!.ToLowerInvariant() : "";

                for (int i = 1; i <= errorItems.Count; i++)
                {
                    var item = errorItems.Item(i);
                    string level;
                    bool include = false;

                    if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh)
                    {
                        level = "Error";
                        include = includeErrors;
                    }
                    else if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelMedium)
                    {
                        level = "Warning";
                        include = includeWarnings;
                    }
                    else
                    {
                        level = "Message";
                        include = includeMessages;
                    }

                    if (!include) continue;

                    string description = item.Description ?? "";
                    if (hasFilter
                        && !description.ToLowerInvariant().Contains(filterLower))
                    {
                        continue;
                    }

                    if (level == "Error") result.ErrorCount++;
                    else if (level == "Warning") result.WarningCount++;
                    else result.MessageCount++;

                    result.Items.Add(new ErrorListItem
                    {
                        Level = level,
                        Description = description,
                        FileName = item.FileName ?? "",
                        Line = item.Line,
                        Column = item.Column,
                        Project = item.Project ?? ""
                    });
                }

                result.TotalCount = result.Items.Count;
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to read error list: {ex.Message}";
            }

            return result;
        }
    }
}
