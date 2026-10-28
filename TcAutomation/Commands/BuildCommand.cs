using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        public static BuildResult ExecuteInSession(VisualStudioInstance vsInstance, bool clean,
            string plcName = null, string[] expectedSymbols = null, string[] expectedTypes = null, int timeoutSeconds = 180)
        {
            var result = new BuildResult();
            var stopwatch = Stopwatch.StartNew();
            using var deadline = new OwnedDeadline(() => vsInstance.KillOwnedProcess("build operation deadline"), timeoutSeconds);

            try
            {
                vsInstance.Dte.ExecuteCommand("File.SaveAll", "");
                result.Phase = "snapshot_before";
                var before = BuildArtifacts.Capture(vsInstance, plcName);
                if (clean)
                {
                    vsInstance.CleanSolution();
                }

                result.Phase = "dispatch";
                vsInstance.BuildSolution(timeoutSeconds);
                result.Build = vsInstance.LastBuildRun;
                result.FailedProjectCount = result.Build.FailedProjects;
                result.CompilerSuccess = result.FailedProjectCount == 0;
                vsInstance.LastCompilerCheckSucceeded = result.CompilerSuccess;
                vsInstance.LastCheckedSourceRevision = result.Build.SourceRevision;

                result.Phase = "diagnostics";
                var errorItems = vsInstance.GetErrorItems();

                for (int i = 1; i <= errorItems.Count; i++)
                {
                    var item = errorItems.Item(i);

                    bool inferred = item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelMedium &&
                        !result.CompilerSuccess && !string.IsNullOrWhiteSpace(item.FileName);
                    if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh || inferred)
                    {
                        result.Errors.Add(new BuildError
                        {
                            RawSeverity = (int)item.ErrorLevel,
                            SeverityInferred = inferred,
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

                result.Phase = "snapshot_after";
                result.Artifacts = BuildArtifacts.Capture(vsInstance, plcName);
                foreach (var artifact in result.Artifacts)
                {
                    BuildArtifacts.Compare(before.FirstOrDefault(a => a.PlcName == artifact.PlcName), artifact,
                        result.Build.BuildId, result.CompilerSuccess, expectedSymbols, expectedTypes);
                    vsInstance.LastBuildArtifacts[artifact.PlcName] = artifact;
                }
                result.BuildOutput = result.Build.Output;
                if (!result.CompilerSuccess && result.Errors.Count == 0)
                    result.Errors.Add(new BuildError { Description = "One or more projects failed compilation; inspect build-scoped output.", SeverityInferred = true });
                result.Success = result.Errors.Count == 0 && result.CompilerSuccess &&
                    result.Artifacts.All(a => !a.VerificationRequested || a.VerificationPassed);
                result.ErrorCount = result.Errors.Count;
                result.WarningCount = result.Warnings.Count;
                result.Phase = "complete";
                deadline.Complete();
                result.BuildTime = $"{stopwatch.Elapsed.TotalSeconds:F1}s";
                result.Summary = result.Success
                    ? $"Build succeeded with {result.WarningCount} warning(s) in {result.BuildTime}"
                    : !result.CompilerSuccess ? $"Build failed: {result.FailedProjectCount} failed project(s); see build-scoped output and diagnostics"
                    : "Compilation completed, but expected artifact symbols/types were not verified. Inspect artifact evidence and linking policy.";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.OutcomeUnknown = deadline.Expired || ex is TimeoutException || !vsInstance.IsOwnedProcessRunning || result.Phase == "dispatch";
                result.ErrorMessage = $"Build failed: {ex.Message}";
                if (result.Build != null && !result.CompilerSuccess && result.Errors.Count == 0)
                    result.Errors.Add(new BuildError { Description = "One or more projects failed compilation; detailed feedback is unavailable.", SeverityInferred = true });
                result.ErrorCount = result.Errors.Count;
                result.WarningCount = result.Warnings.Count;
            }

            return result;
        }
    }
}
