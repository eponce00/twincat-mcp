using System;
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
        private readonly string _solutionFilePath;
        private readonly string _tcVersion;
        private readonly string? _forceTcVersion;
        
        private DTE2? _dte;
        private EnvDTE.Solution? _solution;
        private EnvDTE.Project? _tcProject;
        private bool _loaded;

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
        /// Close Visual Studio instance.
        /// </summary>
        public void Close()
        {
            if (_loaded && _dte != null)
            {
                Thread.Sleep(3000); // Avoid busy errors
                try
                {
                    _dte.Quit();
                }
                catch { }
            }
            _loaded = false;
        }

        public void Dispose()
        {
            Close();
        }

        private void LoadDevelopmentToolsEnvironment(string vsVersion)
        {
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
