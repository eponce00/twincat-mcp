using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using EnvDTE80;
using TcAutomation.Core;
using TcAutomation.Models;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Build a TwinCAT solution and collect errors/warnings.
    /// </summary>
    public static class BuildCommand
    {
        public static async Task<BuildResult> ExecuteAsync(string solutionPath, bool clean, string? tcVersion)
        {
            var result = new BuildResult();

            if (!File.Exists(solutionPath))
            {
                result.Success = false;
                result.ErrorMessage = $"Solution file not found: {solutionPath}";
                return result;
            }

            VisualStudioInstance? vsInstance = null;

            try
            {
                MessageFilter.Register();

                var tcProjectPath = TcFileUtilities.FindTwinCATProjectFile(solutionPath);
                if (string.IsNullOrEmpty(tcProjectPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "No TwinCAT project (.tsproj) found in solution";
                    return result;
                }

                var projectTcVersion = TcFileUtilities.GetTcVersion(tcProjectPath);
                if (string.IsNullOrEmpty(projectTcVersion))
                {
                    result.Success = false;
                    result.ErrorMessage = "Could not determine TwinCAT version from project";
                    return result;
                }

                vsInstance = new VisualStudioInstance(solutionPath, projectTcVersion, tcVersion);
                vsInstance.Load();
                vsInstance.LoadSolution();

                result = ExecuteInSession(vsInstance, clean);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Build failed: {ex.Message}";
            }
            finally
            {
                vsInstance?.Close();
                MessageFilter.Revoke();
            }

            return await Task.FromResult(result);
        }

        /// <summary>
        /// Build the already-loaded solution. Use this when VS is already open (batch mode).
        /// Does NOT open or close VS and does NOT register/revoke the COM message filter.
        /// </summary>
        public static BuildResult ExecuteInSession(VisualStudioInstance vsInstance, bool clean)
        {
            var result = new BuildResult();
            var stopwatch = Stopwatch.StartNew();

            try
            {
                if (clean)
                {
                    vsInstance.CleanSolution();
                }

                vsInstance.BuildSolution();

                var errorItems = vsInstance.GetErrorItems();

                for (int i = 1; i <= errorItems.Count; i++)
                {
                    var item = errorItems.Item(i);

                    if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh)
                    {
                        result.Errors.Add(new BuildError
                        {
                            Description = item.Description ?? "",
                            FileName = item.FileName ?? "",
                            Line = item.Line,
                            Column = item.Column,
                            Project = item.Project ?? ""
                        });
                    }
                    else if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelMedium)
                    {
                        result.Warnings.Add(new BuildWarning
                        {
                            Description = item.Description ?? "",
                            FileName = item.FileName ?? "",
                            Line = item.Line,
                            Column = item.Column,
                            Project = item.Project ?? ""
                        });
                    }
                }

                stopwatch.Stop();

                result.Success = result.Errors.Count == 0;
                result.ErrorCount = result.Errors.Count;
                result.WarningCount = result.Warnings.Count;
                result.BuildTime = $"{stopwatch.Elapsed.TotalSeconds:F1}s";
                result.Summary = result.Success
                    ? $"Build succeeded with {result.WarningCount} warning(s) in {result.BuildTime}"
                    : $"Build failed with {result.ErrorCount} error(s) and {result.WarningCount} warning(s)";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Build failed: {ex.Message}";
            }

            return result;
        }
    }
}
