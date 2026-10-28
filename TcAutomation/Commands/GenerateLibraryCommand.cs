using System;
using System.IO;
using TCatSysManagerLib;
using TcAutomation.Core;

namespace TcAutomation.Commands
{
    public class GenerateLibraryCommand
    {
        public class GenerateLibraryResult
        {
            public bool Success { get; set; }
            public string Message { get; set; }
            public string ErrorMessage { get; set; }
            public string SolutionPath { get; set; }
            public string PlcName { get; set; }
            public string OutputLibraryPath { get; set; }
            public bool BuildSkipped { get; set; }
            public bool DryRun { get; set; }
            public bool BuildPerformed { get; set; }
            public bool ExportAttempted { get; set; }
            public bool FileWriteAttempted { get; set; }
            public bool OutcomeUnknown { get; set; }
            public bool Installed { get; set; }
            public string StagePath { get; set; }
            public string Sha256 { get; set; }
        }
        public static GenerateLibraryResult Execute(string solutionPath, string plcName, string libraryLocation = null,
            string tcVersion = null, bool skipBuild = false, bool dryRun = false)
        {
            VisualStudioInstance vs = null;
            try
            {
                MessageFilter.Register();
                string project = TcFileUtilities.FindTwinCATProjectFile(solutionPath);
                if (string.IsNullOrEmpty(project)) throw new ArgumentException("No TwinCAT project found.");
                vs = new VisualStudioInstance(solutionPath, TcFileUtilities.GetTcVersion(project), tcVersion);
                vs.Load(); vs.LoadSolution();
                return ExecuteInSession(vs, solutionPath, plcName, libraryLocation, skipBuild, dryRun);
            }
            catch (Exception ex) { return new GenerateLibraryResult { Success = false, ErrorMessage = ex.Message }; }
            finally { vs?.Close(); MessageFilter.Revoke(); }
        }
        public static GenerateLibraryResult ExecuteInSession(VisualStudioInstance vs, string solutionPath, string plcName,
            string libraryLocation = null, bool skipBuild = false, bool dryRun = false)
        {
            GenerateLibraryResult result = null;
            using var deadline = new OwnedDeadline(() => vs.KillOwnedProcess("library export deadline"), 180);
            try
            {
                vs.EnsureExclusiveSolution();
                var nested = ((ITcProjectRoot)vs.GetSystemManager().LookupTreeItem("TIPC^" + plcName)).NestedProject;
                if (MatchingLoginCommand.IsLoggedIn(nested)) throw new InvalidOperationException("Library export requires an offline PLC project.");
                var iec = nested as ITcPlcIECProject ?? throw new NotSupportedException("The documented ITcPlcIECProject export interface is unavailable.");
                string path = ResolveOutputLibraryPath(solutionPath, plcName, libraryLocation);
                result = Export(path, (output, install) => iec.SaveAsLibrary(output, install), skipBuild ? (Action)null : () =>
                {
                    vs.Dte.ExecuteCommand("File.SaveAll", "");
                    var check = CheckAllObjectsCommand.ExecuteInSession(vs, plcName);
                    if (!check.Success) throw new InvalidOperationException("PLC object checking failed. " + check.Message + " " + check.ErrorMessage);
                    vs.BuildSolution();
                    if (vs.LastBuildInfo != 0) throw new InvalidOperationException("Compilation failed. " + vs.GetBuildOutput());
                }, dryRun);
                result.SolutionPath = solutionPath; result.PlcName = plcName; result.BuildSkipped = skipBuild;
                deadline.Complete();
                return result;
            }
            catch (Exception ex)
            {
                result = result ?? new GenerateLibraryResult(); result.Success = false;
                result.OutcomeUnknown |= deadline.Expired || !vs.IsOwnedProcessRunning;
                result.ErrorMessage = ex.Message; return result;
            }
        }
        // A single documented export call; false means do not install into the repository.
        public static GenerateLibraryResult Export(string output, Action<string, bool> saveAsLibrary, Action build, bool dryRun)
        {
            var result = new GenerateLibraryResult { OutputLibraryPath = output, DryRun = dryRun, Installed = false };
            try
            {
                output = ArtifactFiles.ValidateNew(output, ".library");
                if (dryRun) { result.Success = true; result.Message = "Preview only; no build, export or installation performed."; return result; }
                if (build != null) { build(); result.BuildPerformed = true; }
                string stage = Path.Combine(Path.GetTempPath(), "twincat-library-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stage);
                result.StagePath = Path.Combine(stage, "export.library");
                result.ExportAttempted = true;
                saveAsLibrary(result.StagePath, false);
                result.FileWriteAttempted = true;
                ArtifactFiles.Publish(result.StagePath, output, ".library");
                result.Sha256 = ArtifactFiles.Sha256(output);
                result.Success = true; result.Message = "Library exported without repository installation.";
            }
            catch (Exception ex)
            {
                result.Success = false; result.ErrorMessage = ex.Message;
                result.OutcomeUnknown = result.ExportAttempted || result.FileWriteAttempted;
            }
            return result;
        }
        public static string ResolveOutputLibraryPath(string solution, string plcName, string location)
        {
            if (string.IsNullOrWhiteSpace(plcName) || plcName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || plcName == "." || plcName == "..")
                throw new ArgumentException("Invalid PLC artifact name.");
            if (string.IsNullOrWhiteSpace(location)) return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(solution)), plcName + ".library");
            if (!Path.IsPathRooted(location)) throw new ArgumentException("libraryLocation must be absolute.");
            string path = Path.GetFullPath(location);
            return Directory.Exists(path) || string.IsNullOrEmpty(Path.GetExtension(path)) || location.EndsWith("\\") || location.EndsWith("/")
                ? Path.Combine(path, plcName + ".library") : path;
        }
    }
}
