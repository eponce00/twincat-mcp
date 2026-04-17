using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using EnvDTE80;
using TCatSysManagerLib;

namespace TcAutomation.Core
{
    /// <summary>
    /// Manages a Visual Studio DTE instance for TwinCAT automation.
    /// 
    /// Handles:
    /// - Creating/loading VS DTE (TcXaeShell or Visual Studio)
    /// - Opening TwinCAT solutions
    /// - Building solutions
    /// - Extracting errors from Error List
    /// </summary>
    public class VisualStudioInstance : IDisposable
    {
        private string _solutionFilePath;
        private string _tcVersion;
        private string? _forceTcVersion;
        
        private DTE2? _dte;
        private EnvDTE.Solution? _solution;
        private EnvDTE.Project? _tcProject;
        private bool _loaded;
        private HashSet<int>? _preExistingPids;

        /// <summary>
        /// The Windows PID of the TcXaeShell/devenv process we launched.
        /// Populated after Load() via PID-diff against the pre-existing snapshot.
        /// Used by the persistent host to write session files and force-kill on
        /// shutdown even if DTE.Quit() fails or the DTE proxy becomes unresponsive.
        /// </summary>
        public int? DteProcessId { get; private set; }

        /// <summary>
        /// True once a solution is successfully opened and a TwinCAT project is found.
        /// Cleared by ReloadSolution() until the new solution is fully loaded.
        /// </summary>
        public bool IsSolutionLoaded => _loaded;

        /// <summary>
        /// The solution path currently loaded (or most recently requested).
        /// </summary>
        public string SolutionFilePath => _solutionFilePath;

        public VisualStudioInstance(string solutionFilePath, string tcVersion, string? forceTcVersion = null)
        {
            _solutionFilePath = solutionFilePath;
            _tcVersion = tcVersion;
            _forceTcVersion = forceTcVersion;
        }

        /// <summary>
        /// Load the Visual Studio DTE instance.
        /// </summary>
        public void Load()
        {
            // Determine VS version from solution
            var vsVersion = TcFileUtilities.GetVisualStudioVersion(_solutionFilePath) ?? "17.0";
            
            LoadDevelopmentToolsEnvironment(vsVersion);
        }

        /// <summary>
        /// Open the solution and find the TwinCAT project.
        /// </summary>
        public void LoadSolution()
        {
            if (_dte == null)
                throw new InvalidOperationException("DTE not loaded. Call Load() first.");

            // Delete the .suo file before opening. The .suo stores per-user
            // window state (open documents, docking layout, bookmarks). If it
            // has open documents, VS auto-reopens them when the solution loads
            // — which causes "File changed outside environment" dialogs to
            // spam throughout build/activate as TwinCAT rewrites those files.
            // Deleting it has no effect on actual project data.
            try
            {
                string suoDir = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(_solutionFilePath) ?? "",
                    ".vs",
                    System.IO.Path.GetFileNameWithoutExtension(_solutionFilePath));
                if (System.IO.Directory.Exists(suoDir))
                {
                    foreach (var suo in System.IO.Directory.GetFiles(suoDir, "*.suo", System.IO.SearchOption.AllDirectories))
                    {
                        try { System.IO.File.Delete(suo); Console.Error.WriteLine($"[DEBUG] Deleted stale .suo: {suo}"); }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DEBUG] .suo cleanup failed (non-fatal): {ex.Message}");
            }

            _solution = _dte.Solution;
            _solution.Open(_solutionFilePath);

            // Wait for solution to load and find TwinCAT project
            // TwinCAT projects can take a while to fully load
            for (int attempt = 1; attempt <= 30; attempt++)
            {
                Thread.Sleep(1000);

                try
                {
                    for (int i = 1; i <= _solution.Projects.Count; i++)
                    {
                        EnvDTE.Project? proj;
                        try { proj = _solution.Projects.Item(i); }
                        catch { continue; }

                        // Check if this project has ITcSysManager (TwinCAT project)
                        try
                        {
                            if (proj.Object is ITcSysManager)
                            {
                                _tcProject = proj;
                                _loaded = true;
                                return;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            throw new InvalidOperationException("No TwinCAT project found in solution after 30 seconds.");
        }

        /// <summary>
        /// Close the currently-loaded solution (if any) and open a different one
        /// in the SAME DTE instance. Avoids paying the ~25-30s shell-startup cost
        /// when the host is asked to switch projects. Only the solution-close /
        /// solution-open cost applies (seconds).
        /// </summary>
        public void ReloadSolution(string newSolutionFilePath, string newTcVersion, string? newForceTcVersion = null)
        {
            if (_dte == null)
                throw new InvalidOperationException("DTE not loaded. Call Load() first.");

            // Close the existing solution without saving. We intentionally do not
            // call DTE.Solution.Close(true) because TwinCAT can rewrite files on
            // close, which triggers modal save dialogs.
            try
            {
                if (_solution != null)
                {
                    try
                    {
                        if (_solution.IsOpen)
                        {
                            _solution.Close(false);
                            Thread.Sleep(500);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[DEBUG] Solution.Close raised (non-fatal): {ex.Message}");
                    }
                }
            }
            finally
            {
                _solution = null;
                _tcProject = null;
                _loaded = false;
            }

            _solutionFilePath = newSolutionFilePath;
            _tcVersion = newTcVersion;
            _forceTcVersion = newForceTcVersion;

            // Switch TC version in-place. Different solutions may target different
            // versions; this is cheap when we're just changing a registry-backed
            // selector on the remote manager.
            LoadTwinCATVersion();

            LoadSolution();
        }

        /// <summary>
        /// Close all open documents in the DTE without saving. Prevents the
        /// "File has been changed outside the environment" and "Conflicting
        /// File Modification Detected" modal dialogs from appearing when
        /// TwinCAT rewrites files (like PlcTask.TcTTO) during build/activate.
        /// The .suo file from prior IDE sessions can cause documents to be
        /// reopened automatically when our headless DTE loads the solution,
        /// so this must be called right after LoadSolution().
        /// </summary>
        public void CloseAllDocuments()
        {
            if (_dte == null) return;

            try
            {
                var docs = new System.Collections.Generic.List<EnvDTE.Document>();
                foreach (EnvDTE.Document d in _dte.Documents)
                    docs.Add(d);

                foreach (var doc in docs)
                {
                    try { doc.Close(EnvDTE.vsSaveChanges.vsSaveChangesNo); }
                    catch { }
                }

                // Also close any open windows (tool windows excluded by filter).
                var windows = new System.Collections.Generic.List<EnvDTE.Window>();
                foreach (EnvDTE.Window w in _dte.Windows)
                {
                    if (w.Kind == "Document") windows.Add(w);
                }
                foreach (var w in windows)
                {
                    try { w.Close(EnvDTE.vsSaveChanges.vsSaveChangesNo); }
                    catch { }
                }

                Console.Error.WriteLine($"[DEBUG] Closed {docs.Count} document(s) and {windows.Count} window(s) in DTE");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DEBUG] CloseAllDocuments failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Get the TwinCAT project.
        /// </summary>
        public EnvDTE.Project GetProject()
        {
            if (_tcProject == null)
                throw new InvalidOperationException("Project not loaded. Call LoadSolution() first.");
            return _tcProject;
        }

        /// <summary>
        /// Get the TwinCAT System Manager interface.
        /// </summary>
        public ITcSysManager10 GetSystemManager()
        {
            if (_tcProject?.Object == null)
                throw new InvalidOperationException("Project not loaded.");
            return (ITcSysManager10)_tcProject.Object;
        }

        /// <summary>
        /// Clean the solution.
        /// </summary>
        public void CleanSolution()
        {
            if (_solution == null)
                throw new InvalidOperationException("Solution not loaded.");

            _solution.SolutionBuild.Clean(true);
            Thread.Sleep(2000);
        }

        /// <summary>
        /// Build the solution. Uses async Build(false) + SpinWait on BuildState
        /// to reliably wait until the build completes, matching the pattern used
        /// by TcUnit-Runner. Synchronous Build(true) + Sleep is unreliable.
        /// </summary>
        public void BuildSolution()
        {
            if (_solution == null)
                throw new InvalidOperationException("Solution not loaded.");

            _solution.SolutionBuild.Build(false);
            System.Threading.SpinWait.SpinUntil(
                () => _solution.SolutionBuild.BuildState == EnvDTE.vsBuildState.vsBuildStateDone);
        }

        /// <summary>
        /// Get error items from the Error List window.
        /// </summary>
        public ErrorItems GetErrorItems()
        {
            if (_dte == null)
                throw new InvalidOperationException("DTE not loaded.");
            return _dte.ToolWindows.ErrorList.ErrorItems;
        }

        /// <summary>
        /// Close Visual Studio instance. Force-kills the process if DTE.Quit() fails.
        /// </summary>
        public void Close()
        {
            if (_dte != null)
            {
                Thread.Sleep(3000); // Avoid busy errors
                try
                {
                    _dte.Quit();
                    Thread.Sleep(5000);
                }
                catch { }

                // Force-kill the process we spawned (identify by PID diff)
                try
                {
                    var currentPids = Process.GetProcessesByName("TcXaeShell")
                        .Concat(Process.GetProcessesByName("devenv"));
                    foreach (var proc in currentPids)
                    {
                        if (_preExistingPids != null && !_preExistingPids.Contains(proc.Id))
                        {
                            Console.Error.WriteLine($"[DEBUG] Force-killing spawned DTE process (PID {proc.Id})");
                            try { proc.Kill(); proc.WaitForExit(5000); } catch { }
                        }
                    }
                }
                catch { }
            }
            _dte = null;
            _loaded = false;
        }

        public void Dispose()
        {
            Close();
        }

        private void LoadDevelopmentToolsEnvironment(string vsVersion)
        {
            // Snapshot existing TcXaeShell/devenv PIDs so we can identify ours later
            _preExistingPids = new HashSet<int>(
                Process.GetProcessesByName("TcXaeShell").Select(p => p.Id)
                .Concat(Process.GetProcessesByName("devenv").Select(p => p.Id)));

            // Kill any orphaned headless TcXaeShell processes from previous crashed runs
            // (those with no main window title are headless zombies)
            foreach (var proc in Process.GetProcessesByName("TcXaeShell"))
            {
                try
                {
                    if (string.IsNullOrEmpty(proc.MainWindowTitle))
                    {
                        Console.Error.WriteLine($"[DEBUG] Killing orphaned headless TcXaeShell (PID {proc.Id}, started {proc.StartTime})");
                        proc.Kill();
                        proc.WaitForExit(5000);
                    }
                }
                catch { }
            }

            // Re-snapshot after cleanup
            _preExistingPids = new HashSet<int>(
                Process.GetProcessesByName("TcXaeShell").Select(p => p.Id)
                .Concat(Process.GetProcessesByName("devenv").Select(p => p.Id)));

            // Try TcXaeShell first, then Visual Studio
            string[] progIds = new[]
            {
                $"TcXaeShell.DTE.{vsVersion}",
                $"VisualStudio.DTE.{vsVersion}",
                "TcXaeShell.DTE.17.0",
                "TcXaeShell.DTE.15.0",
                "VisualStudio.DTE.17.0",
            };

            foreach (var progId in progIds)
            {
                try
                {
                    var type = Type.GetTypeFromProgID(progId);
                    if (type == null) continue;

                    _dte = (DTE2)Activator.CreateInstance(type)!;

                    // Identify which TcXaeShell/devenv we just spawned by diffing PIDs.
                    // This PID is what the persistent host persists + force-kills on
                    // cleanup (the COM runtime owns the process lifecycle otherwise).
                    try
                    {
                        var newPids = Process.GetProcessesByName("TcXaeShell")
                            .Select(p => p.Id)
                            .Concat(Process.GetProcessesByName("devenv").Select(p => p.Id))
                            .ToList();
                        foreach (var pid in newPids)
                        {
                            if (_preExistingPids != null && !_preExistingPids.Contains(pid))
                            {
                                DteProcessId = pid;
                                Console.Error.WriteLine($"[DEBUG] Tracked DTE process PID: {pid}");
                                break;
                            }
                        }
                    }
                    catch { }

                    ConfigureDte();
                    LoadTwinCATVersion();
                    return;
                }
                catch
                {
                    // Try next ProgID
                }
            }

            throw new InvalidOperationException("Could not load TcXaeShell or Visual Studio DTE. Ensure TwinCAT XAE is installed.");
        }

        private void ConfigureDte()
        {
            if (_dte == null) return;

            _dte.UserControl = false;
            _dte.SuppressUI = true;

            // Configure error list to capture all types
            _dte.ToolWindows.ErrorList.ShowErrors = true;
            _dte.ToolWindows.ErrorList.ShowMessages = true;
            _dte.ToolWindows.ErrorList.ShowWarnings = true;

            // Enable TwinCAT silent mode
            try
            {
                var settings = (ITcAutomationSettings)_dte.GetObject("TcAutomationSettings");
                settings.SilentMode = true;
                Console.Error.WriteLine("[DEBUG] TcAutomationSettings.SilentMode set to true");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DEBUG] Failed to set SilentMode: {ex.Message}");
            }
        }

        private void LoadTwinCATVersion()
        {
            if (_dte == null) return;

            try
            {
                var remoteManager = (ITcRemoteManager)_dte.GetObject("TcRemoteManager");
                var versionToUse = _forceTcVersion ?? _tcVersion;

                // Check if requested version is available
                bool versionFound = false;
                Version? latestVersion = null;

                foreach (string version in remoteManager.Versions)
                {
                    var v = new Version(version);
                    if (latestVersion == null || v > latestVersion)
                        latestVersion = v;

                    if (version == versionToUse)
                        versionFound = true;
                }

                if (versionFound)
                {
                    remoteManager.Version = versionToUse;
                }
                else if (latestVersion != null)
                {
                    remoteManager.Version = latestVersion.ToString();
                }
            }
            catch { }
        }
    }
}
