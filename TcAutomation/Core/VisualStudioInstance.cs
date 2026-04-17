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

        // Silent-reload state. When we change DTE options (AutoloadExternalChanges,
        // etc.) we remember the previous value so we can restore it on Close()
        // — DTE writes these options back to the user's registry profile, so
        // leaving them tweaked would leak into the user's interactive TcXaeShell.
        private readonly List<(string Category, string Page, string Item, object? Original)> _savedPreferences
            = new List<(string, string, string, object?)>();

        // Belt-and-suspenders visibility watchdog. If any modal we couldn't
        // suppress promotes the main window (modals need a visible parent),
        // this thread flips MainWindow.Visible back to false within ~500ms so
        // the user doesn't see a TcXaeShell frame flash into view.
        private Thread? _visibilityWatchdog;
        private volatile bool _watchdogShouldStop;

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
                                // Re-hide after solution open: the shell will
                                // often restore visibility while loading projects.
                                HideMainWindow();
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
            // Stop the watchdog FIRST so it doesn't race DTE teardown by
            // calling MainWindow on a dying COM object.
            StopVisibilityWatchdog();

            if (_dte != null)
            {
                // Restore any user preferences we tweaked at startup BEFORE
                // Quit, because Quit may persist the current (our-modified)
                // values to the registry profile.
                RestoreDteOptions();

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
            // Snapshot existing TcXaeShell/devenv PIDs so we can identify ours
            // later via PID-diff. Every PID captured here belongs to someone
            // else (user's IDE, another automation, etc.) and is OFF-LIMITS
            // for the rest of this instance's lifetime.
            //
            // NOTE: we deliberately do NOT kill TcXaeShell instances with an
            // empty MainWindowTitle here. That heuristic was unsafe — a
            // legitimately user-opened IDE reports an empty title during
            // startup, when a modal dialog (e.g. Static Routes) is active,
            // or when minimized to tray. Orphan cleanup is now done
            // precisely via SessionFile.ReapOrphans() using recorded PIDs
            // + start-time fingerprints.
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

            // Hide the main window so the shell runs truly headless.
            // This is the reason users would see a TcXaeShell window pop up
            // during every build/activate/deploy — SuppressUI only hides
            // modal dialogs, not the IDE frame itself.
            //
            // MainWindow can be briefly unavailable immediately after the
            // Activator.CreateInstance call (the shell is still initializing
            // its UI thread), so retry a few times before giving up.
            HideMainWindow();

            // Configure error list to capture all types
            _dte.ToolWindows.ErrorList.ShowErrors = true;
            _dte.ToolWindows.ErrorList.ShowMessages = true;
            _dte.ToolWindows.ErrorList.ShowWarnings = true;

            // Silent-reload: when an external edit touches a file that VS is
            // tracking, reload it silently instead of popping the
            // "This item has been modified outside of the source editor.
            //  Do you want to reload it?" modal. That modal is the reason
            // users occasionally see our hidden main window flash into view —
            // a modal dialog needs a visible parent window, so VS promotes
            // the main window out from under us to attach it.
            //
            // We KEEP DetectFileChangesOutsideIDE=true so the shell still
            // picks up edits (otherwise a subsequent build would compile
            // stale content); we just don't want the dialog.
            //
            // AutoloadExternalChanges only fires silently when there are no
            // in-memory edits to the file. Our headless shell never opens
            // documents interactively (we even delete the .suo to keep it
            // that way), so the precondition is always met.
            ApplyDteOption("Environment", "Documents", "DetectFileChangesOutsideIDE", true);
            ApplyDteOption("Environment", "Documents", "AutoloadExternalChanges", true);

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

            // Start the visibility watchdog last, after all configuration is
            // done. If anything above somehow unhid the window (race with UI
            // thread), the watchdog will re-hide on its first tick.
            StartVisibilityWatchdog();
        }

        /// <summary>
        /// Apply a DTE option and remember the previous value so we can
        /// restore it on Close(). DTE options can persist to the user's
        /// registry profile, so restoring is important to avoid leaking our
        /// session-local tweaks into the user's interactive shell.
        ///
        /// Silently swallows failures — the property may not exist on every
        /// shell SKU / version, and a missing preference isn't worth
        /// aborting startup over.
        /// </summary>
        private void ApplyDteOption(string category, string page, string item, object newValue)
        {
            if (_dte == null) return;

            try
            {
                var props = _dte.Properties[category, page];
                var prop = props.Item(item);
                object? original = null;
                try { original = prop.Value; } catch { /* readable-only in some SKUs */ }

                _savedPreferences.Add((category, page, item, original));
                prop.Value = newValue;
                Console.Error.WriteLine(
                    $"[DEBUG] DTE option {category}.{page}.{item}: {original ?? "(unknown)"} -> {newValue}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[DEBUG] DTE option {category}.{page}.{item} not applied (non-fatal): {ex.Message}");
            }
        }

        /// <summary>
        /// Restore every DTE option we tweaked in ConfigureDte. Best-effort:
        /// if the DTE is already tearing down, we swallow errors.
        /// </summary>
        private void RestoreDteOptions()
        {
            if (_dte == null) return;

            foreach (var (category, page, item, original) in _savedPreferences)
            {
                if (original == null) continue;
                try
                {
                    _dte.Properties[category, page].Item(item).Value = original;
                    Console.Error.WriteLine(
                        $"[DEBUG] DTE option {category}.{page}.{item} restored to {original}");
                }
                catch { /* shutting down; best effort */ }
            }
            _savedPreferences.Clear();
        }

        /// <summary>
        /// Start a background thread that re-hides the main window whenever
        /// it observes it become visible. Modal dialogs (from TwinCAT, VS
        /// itself, or extensions) need a visible parent window; some of
        /// them bypass SuppressUI entirely and will promote our hidden
        /// MainWindow to attach. This thread snaps it back to invisible
        /// within ~500ms so the user doesn't see a frame flash.
        ///
        /// The watchdog only writes when the observed state is `visible` —
        /// it doesn't busy-write `Visible=false` on every tick.
        /// </summary>
        private void StartVisibilityWatchdog()
        {
            if (_visibilityWatchdog != null) return;
            _watchdogShouldStop = false;

            _visibilityWatchdog = new Thread(VisibilityWatchdogLoop)
            {
                IsBackground = true,
                Name = "DteVisibilityWatchdog",
            };
            _visibilityWatchdog.Start();
            Console.Error.WriteLine("[DEBUG] VisibilityWatchdog started");
        }

        private void VisibilityWatchdogLoop()
        {
            while (!_watchdogShouldStop)
            {
                try
                {
                    // Local copy — Close() may null out _dte concurrently.
                    var dte = _dte;
                    if (dte == null)
                    {
                        return;
                    }

                    var mainWin = dte.MainWindow;
                    if (mainWin != null && mainWin.Visible)
                    {
                        mainWin.Visible = false;
                        try { mainWin.WindowState = EnvDTE.vsWindowState.vsWindowStateMinimize; } catch { }
                        Console.Error.WriteLine("[DEBUG] VisibilityWatchdog re-hid the DTE main window");
                    }
                }
                catch
                {
                    // COM busy, RPC disconnected, or DTE torn down. Next
                    // tick will retry or the stop flag will exit.
                }

                // 500ms is fast enough that a briefly-raised window is
                // imperceptible, and slow enough that the cost is
                // negligible (two COM calls per tick in the idle case).
                Thread.Sleep(500);
            }
            Console.Error.WriteLine("[DEBUG] VisibilityWatchdog stopped");
        }

        private void StopVisibilityWatchdog()
        {
            _watchdogShouldStop = true;
            var t = _visibilityWatchdog;
            _visibilityWatchdog = null;
            if (t != null)
            {
                try { t.Join(2000); } catch { }
            }
        }

        /// <summary>
        /// Hide the DTE main window. Uses a short retry loop because
        /// MainWindow can throw RPC_E_SERVERCALL_RETRYLATER or return null
        /// while the shell is still coming up after CreateInstance.
        /// Also tries to minimize as a belt-and-braces fallback if hiding
        /// ever fails silently on a given shell version.
        /// </summary>
        private void HideMainWindow()
        {
            if (_dte == null) return;

            const int maxAttempts = 10;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    var mainWin = _dte.MainWindow;
                    if (mainWin != null)
                    {
                        mainWin.Visible = false;
                        try { mainWin.WindowState = EnvDTE.vsWindowState.vsWindowStateMinimize; } catch { }
                        Console.Error.WriteLine($"[DEBUG] DTE MainWindow hidden (attempt {attempt})");
                        return;
                    }
                }
                catch (Exception ex) when (attempt < maxAttempts)
                {
                    Console.Error.WriteLine($"[DEBUG] HideMainWindow attempt {attempt} transient: {ex.Message}");
                    Thread.Sleep(250);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[DEBUG] HideMainWindow failed after {attempt} attempts: {ex.Message}");
                    return;
                }
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
