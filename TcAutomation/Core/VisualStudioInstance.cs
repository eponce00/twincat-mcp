using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using EnvDTE80;
using TCatSysManagerLib;

namespace TcAutomation.Core
{
    /// <summary>
    /// Manages a Visual Studio DTE instance for TwinCAT automation.
    /// 
    /// Handles:
    /// - Creating/loading VS DTE (TcXaeShell or Visual Studio) in headless mode
    /// - Reusing existing automation instances with the same solution open
    /// - Opening TwinCAT solutions
    /// - Building solutions
    /// - Extracting errors from Error List
    /// 
    /// Instance reuse strategy:
    /// - Searches ROT (Running Object Table) for existing DTE instances
    /// - Only reuses instances with SuppressUI=true (automation instances we created)
    /// - Skips instances with SuppressUI=false (user-opened VS - won't hijack)
    /// - Matches by solution path (different solutions get separate instances)
    /// - Handles disconnected/crashed instances gracefully
    /// 
    /// This provides ~10x speedup for consecutive operations on the same solution
    /// while remaining completely headless (no window shown).
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
        private bool _reusedExisting; // Track if we reused an existing instance

        public VisualStudioInstance(string solutionFilePath, string tcVersion, string? forceTcVersion = null)
        {
            _solutionFilePath = solutionFilePath;
            _tcVersion = tcVersion;
            _forceTcVersion = forceTcVersion;
        }

        /// <summary>
        /// Load the Visual Studio DTE instance.
        /// First tries to find an existing VS instance with the solution already open.
        /// </summary>
        public void Load()
        {
            // First, try to find an existing VS instance with this solution open
            if (TryAttachToExistingInstance())
            {
                _reusedExisting = true;
                Console.Error.WriteLine("[PROGRESS] vs: Reusing existing Visual Studio instance");
                return;
            }

            // Determine VS version from solution
            var vsVersion = TcFileUtilities.GetVisualStudioVersion(_solutionFilePath) ?? "17.0";
            
            LoadDevelopmentToolsEnvironment(vsVersion);
            _reusedExisting = false;
        }

        /// <summary>
        /// Try to attach to an existing VS instance that has our solution open.
        /// Uses a timeout to avoid hanging on zombie COM instances.
        /// </summary>
        private bool TryAttachToExistingInstance()
        {
            try
            {
                // Get running object table
                IRunningObjectTable rot;
                if (GetRunningObjectTable(0, out rot) != 0)
                    return false;

                IEnumMoniker enumMoniker;
                rot.EnumRunning(out enumMoniker);

                IMoniker[] monikers = new IMoniker[1];
                IntPtr fetched = IntPtr.Zero;

                while (enumMoniker.Next(1, monikers, fetched) == 0)
                {
                    IBindCtx bindCtx;
                    CreateBindCtx(0, out bindCtx);

                    string displayName;
                    monikers[0].GetDisplayName(bindCtx, null, out displayName);

                    // Look for VS DTE instances
                    if (displayName.StartsWith("!TcXaeShell.DTE") || displayName.StartsWith("!VisualStudio.DTE"))
                    {
                        object obj;
                        rot.GetObject(monikers[0], out obj);

                        if (obj is DTE2 dte)
                        {
                            // Use a timeout task to avoid hanging on zombie COM instances
                            var checkTask = System.Threading.Tasks.Task.Run(() => TryCheckAndReuseDte(dte));
                            if (checkTask.Wait(TimeSpan.FromSeconds(5)) && checkTask.Result)
                            {
                                return true;
                            }
                            // If timeout or returned false, continue to next instance
                        }
                    }
                }
            }
            catch
            {
                // Failed to enumerate ROT, fall back to creating new instance
            }

            return false;
        }

        /// <summary>
        /// Check if a DTE instance can be reused (runs in a separate task with timeout).
        /// </summary>
        private bool TryCheckAndReuseDte(DTE2 dte)
        {
            try
            {
                // Check if this instance has our solution open
                var solution = dte.Solution;
                
                if (solution != null && !string.IsNullOrEmpty(solution.FullName))
                {
                    // Compare solution paths (case-insensitive on Windows)
                    if (string.Equals(
                        Path.GetFullPath(solution.FullName), 
                        Path.GetFullPath(_solutionFilePath), 
                        StringComparison.OrdinalIgnoreCase))
                    {
                        // Found an instance with our solution!
                        // Check if it's an automation instance (SuppressUI=true)
                        // or a user-launched interactive VS (SuppressUI=false)
                        bool suppressUI = dte.SuppressUI;
                        
                        if (suppressUI)
                        {
                            // This is an automation instance we created - safe to reuse
                            _dte = dte;
                            ConfigureDte();
                            return true;
                        }
                        // If SuppressUI=false, it's an interactive session - skip
                    }
                }
            }
            catch
            {
                // DTE may be disconnected or invalid - skip this instance
            }
            return false;
        }

        [DllImport("ole32.dll")]
        private static extern int GetRunningObjectTable(uint reserved, out IRunningObjectTable pprot);

        [DllImport("ole32.dll")]
        private static extern int CreateBindCtx(uint reserved, out IBindCtx ppbc);

        /// <summary>
        /// Open the solution and find the TwinCAT project.
        /// </summary>
        public void LoadSolution()
        {
            if (_dte == null)
                throw new InvalidOperationException("DTE not loaded. Call Load() first.");

            _solution = _dte.Solution;
            
            // If reusing existing instance, solution is already open
            if (!_reusedExisting || string.IsNullOrEmpty(_solution.FullName))
            {
                Console.Error.WriteLine("[PROGRESS] vs: Opening solution...");
                _solution.Open(_solutionFilePath);
            }
            else
            {
                Console.Error.WriteLine("[PROGRESS] vs: Solution already open");
            }

            // Wait for solution to load and find TwinCAT project
            // TwinCAT projects can take a while to fully load
            // When reusing, project should be found immediately
            int maxAttempts = _reusedExisting ? 5 : 30;
            
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (!_reusedExisting)
                    Thread.Sleep(1000);
                else if (attempt > 1)
                    Thread.Sleep(500);

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
                                Console.Error.WriteLine($"[PROGRESS] vs: Found TwinCAT project (attempt {attempt})");
                                return;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            throw new InvalidOperationException($"No TwinCAT project found in solution after {maxAttempts} attempts.");
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
        /// Build the solution.
        /// </summary>
        public void BuildSolution()
        {
            if (_solution == null)
                throw new InvalidOperationException("Solution not loaded.");

            _solution.SolutionBuild.Build(true);
            Thread.Sleep(3000);
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
        /// Automation instances (UserControl=false) are left running for reuse by subsequent calls.
        /// This significantly speeds up consecutive operations on the same solution.
        /// </summary>
        public void Close()
        {
            // Don't quit or release automation instances - leave them running for reuse
            // This provides significant speedup for consecutive operations
            // Orphaned instances will be found and reused by TryAttachToExistingInstance()
            // Note: We intentionally don't set _dte = null to avoid releasing the COM reference
            // which would cause VS to auto-quit (since UserControl=false)
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

            // UserControl = false keeps VS headless (no window shown)
            // SuppressUI = true marks this as an automation instance
            // Instance reuse works because TwinCAT keeps internal COM references
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
            }
            catch { }
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
