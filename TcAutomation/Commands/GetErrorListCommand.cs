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
            int waitSeconds = 0)
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

                // Optional wait for messages to accumulate (useful for async ADS logs)
                if (waitSeconds > 0)
                {
                    Thread.Sleep(waitSeconds * 1000);
                }

                // Collect error list items
                var errorItems = vsInstance.GetErrorItems();
                
                for (int i = 1; i <= errorItems.Count; i++)
                {
                    var item = errorItems.Item(i);
                    string level;
                    bool include = false;

                    if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh)
                    {
                        level = "Error";
                        include = includeErrors;
                        if (include) result.ErrorCount++;
                    }
                    else if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelMedium)
                    {
                        level = "Warning";
                        include = includeWarnings;
                        if (include) result.WarningCount++;
                    }
                    else // vsBuildErrorLevelLow = Messages
                    {
                        level = "Message";
                        include = includeMessages;
                        if (include) result.MessageCount++;
                    }

                    if (include)
                    {
                        result.Items.Add(new ErrorListItem
                        {
                            Level = level,
                            Description = item.Description ?? "",
                            FileName = item.FileName ?? "",
                            Line = item.Line,
                            Column = item.Column,
                            Project = item.Project ?? ""
                        });
                    }
                }

                result.TotalCount = result.Items.Count;
                result.Success = true;
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
    }
}
