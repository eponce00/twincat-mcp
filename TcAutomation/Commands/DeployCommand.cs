using System;
using System.Collections.Generic;
using System.Text.Json;
using EnvDTE80;
using TCatSysManagerLib;
using TcAutomation.Core;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Full deployment workflow: build, set target, activate boot project, activate config, restart TwinCAT
    /// </summary>
    public class DeployCommand
    {
        /// <summary>
        /// CLI entrypoint: opens a one-shot VS instance, runs the deploy workflow,
        /// closes it, and writes the JSON result to stdout.
        /// </summary>
        public static int Execute(string solutionPath, string amsNetId, string? tcVersion, DeployOptions options)
        {
            if (!IsValidAmsNetId(amsNetId))
            {
                OutputError($"Invalid AMS Net ID format: {amsNetId}. Expected format: x.x.x.x.x.x");
                return 1;
            }

            string tcProjectPath = TcFileUtilities.FindTwinCATProjectFile(solutionPath);
            if (string.IsNullOrEmpty(tcProjectPath))
            {
                OutputError("Could not find TwinCAT project file in solution");
                return 1;
            }
            string projectTcVersion = TcFileUtilities.GetTcVersion(tcProjectPath);

            VisualStudioInstance? vsInstance = null;
            try
            {
                vsInstance = new VisualStudioInstance(solutionPath, projectTcVersion, tcVersion);
                vsInstance.Load();
                vsInstance.LoadSolution();

                var result = ExecuteInSession(vsInstance, solutionPath, amsNetId, options);

                // Keep camelCase to match the legacy CLI output format.
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }));
                return result.Success ? 0 : 1;
            }
            catch (Exception ex)
            {
                OutputError($"Deployment failed: {ex.Message}");
                return 1;
            }
            finally
            {
                vsInstance?.Close();
            }
        }

        /// <summary>
        /// Runs the deploy workflow against an already-open VS instance. Used by
        /// the persistent shell host so the ~30s TcXaeShell cold-start is paid
        /// once per MCP session instead of per deploy.
        ///
        /// NOTE: This mutates the solution on disk (BootProjectAutostart=true on
        /// deployed PLCs, target netId). Callers that want to keep the in-memory
        /// solution pristine should call VisualStudioInstance.ReloadSolution after.
        /// </summary>
        public static DeployResult ExecuteInSession(
            VisualStudioInstance vsInstance,
            string solutionPath,
            string amsNetId,
            DeployOptions options)
        {
            var steps = new List<object>();
            bool dryRun = options.DryRun;
            var result = new DeployResult
            {
                Solution = solutionPath,
                TargetNetId = amsNetId,
                DryRun = dryRun,
                Steps = steps
            };

            try
            {
                if (!IsValidAmsNetId(amsNetId))
                {
                    result.Success = false;
                    result.ErrorMessage = $"Invalid AMS Net ID format: {amsNetId}. Expected format: x.x.x.x.x.x";
                    return result;
                }

                string tcProjectPath = TcFileUtilities.FindTwinCATProjectFile(solutionPath);
                if (string.IsNullOrEmpty(tcProjectPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "Could not find TwinCAT project file in solution";
                    return result;
                }
                string projectTcVersion = TcFileUtilities.GetTcVersion(tcProjectPath);
                steps.Add(new { step = 1, action = "Found TwinCAT project", tcVersion = projectTcVersion });

                var automationInterface = new AutomationInterface(vsInstance);
                if (automationInterface.PlcTreeItem.ChildCount <= 0)
                {
                    result.Success = false;
                    result.ErrorMessage = "No PLC project found in TwinCAT project";
                    return result;
                }
                int plcCount = automationInterface.PlcTreeItem.ChildCount;

                // What to build, decided before anything is built or downloaded. The shell may be shared with an
                // earlier session, so its active configuration is never trusted as is.
                var (activeConfiguration, activePlatform) = SolutionPlatform.Active(vsInstance);
                var target = TargetPlatform.Read(amsNetId);
                result.Target = target;
                BuildSelection selection;
                try
                {
                    selection = BuildPlanner.Resolve(ProjectVariants.Read(tcProjectPath), options.Variant, options.Configuration,
                        options.Platform, activeConfiguration, activePlatform, amsNetId, target, options.AllowUnverifiedPlatform);
                }
                catch (DeployRefusedException refused)
                {
                    steps.Add(new { step = 2, action = "Deployment refused before building", reason = refused.Message, active = activeConfiguration + "|" + activePlatform });
                    result.Success = false;
                    result.ErrorMessage = refused.Message;
                    return result;
                }
                result.Variant = selection.Variant;
                result.Configuration = selection.Configuration;
                result.Platform = selection.Platform;
                result.PlatformVerified = selection.PlatformVerified;
                result.PlatformStatus = selection.PlatformStatus;
                steps.Add(new
                {
                    step = 2,
                    action = "Selected build",
                    variant = selection.Variant,
                    configuration = selection.Configuration,
                    platform = selection.Platform,
                    source = selection.Source,
                    previous = activeConfiguration + "|" + activePlatform,
                    target = target.TargetType,
                    platformStatus = selection.PlatformStatus,
                    dryRun
                });

                if (options.SkipBuild && !(Same(activeConfiguration, selection.Configuration) && Same(activePlatform, selection.Platform)))
                {
                    result.Success = false;
                    result.ErrorMessage = $"skipBuild needs the last build to be '{selection.Configuration}|{selection.Platform}', but the solution is on '{activeConfiguration}|{activePlatform}'. Build for the new platform.";
                    return result;
                }

                if (!dryRun)
                {
                    if (selection.Variant != null)
                    {
                        var sysManager14 = (ITcSysManager14)automationInterface.SystemManager;
                        if (!Same(sysManager14.CurrentProjectVariant ?? "", selection.Variant))
                            sysManager14.CurrentProjectVariant = selection.Variant;
                    }
                    SolutionPlatform.Select(vsInstance, automationInterface, selection.Configuration, selection.Platform);
                }
                steps.Add(new { step = 3, action = $"Found {plcCount} PLC project(s)" });

                if (!options.SkipBuild)
                {
                    if (!dryRun)
                    {
                        vsInstance.CleanSolution();
                        vsInstance.BuildSolution();

                        var errorItems = vsInstance.GetErrorItems();
                        int errorCount = CountBuildErrors(errorItems);
                        if (vsInstance.LastBuildInfo != 0 || errorCount > 0)
                        {
                            result.Success = false;
                            result.ErrorMessage = $"Build failed: {vsInstance.LastBuildInfo} failed project(s), {errorCount} reported error(s). {vsInstance.GetBuildOutput()}";
                            result.BuildErrors = CollectErrors(errorItems);
                            return result;
                        }
                        var (builtConfiguration, builtPlatform) = SolutionPlatform.Active(vsInstance);
                        if (!Same(builtConfiguration, selection.Configuration) || !Same(builtPlatform, selection.Platform))
                        {
                            result.Success = false;
                            result.ErrorMessage = $"The build ran as '{builtConfiguration}|{builtPlatform}', not '{selection.Configuration}|{selection.Platform}'. Nothing was downloaded.";
                            return result;
                        }
                    }
                    steps.Add(new { step = 4, action = "Build completed", configuration = selection.Configuration, platform = selection.Platform, dryRun });
                }
                else
                {
                    steps.Add(new { step = 4, action = "Build skipped", configuration = selection.Configuration, platform = selection.Platform });
                }

                if (!dryRun)
                {
                    automationInterface.TargetNetId = amsNetId;
                }
                steps.Add(new { step = 5, action = $"Set target to {amsNetId}", dryRun });

                var deployedPlcs = new List<string>();
                var plcPorts = new List<int>();
                bool foundTargetPlc = false;
                string? plcName = options.PlcName;

                for (int i = 1; i <= automationInterface.PlcTreeItem.ChildCount; i++)
                {
                    ITcSmTreeItem plcProject = automationInterface.PlcTreeItem.Child[i];

                    if (!string.IsNullOrEmpty(plcName) &&
                        !plcProject.Name.Equals(plcName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foundTargetPlc = true;

                    if (!dryRun)
                    {
                        ITcPlcProject iecProject = (ITcPlcProject)plcProject;
                        iecProject.BootProjectAutostart = true;
                        // Generating the boot project separately re-enables disabled PLC projects on some
                        // solutions; without it the activation writes the boot files.
                        if (options.GenerateBootProject)
                            iecProject.GenerateBootProject(true);
                    }

                    deployedPlcs.Add(plcProject.Name);
                    int? port = PlcPorts.Of(plcProject);
                    if (port != null) plcPorts.Add(port.Value);
                }

                if (!string.IsNullOrEmpty(plcName) && !foundTargetPlc)
                {
                    var availablePlcs = new List<string>();
                    for (int i = 1; i <= automationInterface.PlcTreeItem.ChildCount; i++)
                    {
                        availablePlcs.Add(automationInterface.PlcTreeItem.Child[i].Name);
                    }
                    result.Success = false;
                    result.ErrorMessage = $"PLC '{plcName}' not found. Available: {string.Join(", ", availablePlcs)}";
                    return result;
                }

                steps.Add(new { step = 6, action = options.GenerateBootProject ? "Boot project generated, autostart on" : "Autostart on; the activation writes the boot project", plcs = deployedPlcs, dryRun });

                if (!dryRun)
                {
                    // Restarting before the activation has landed starts the new
                    // PLC boot files against the previous CurrentConfig.xml, so
                    // verify it first and never restart when it did not land.
                    var activation = ActivationWait.ActivateAndWait(
                        amsNetId,
                        automationInterface.ActivateConfiguration,
                        message => Console.Error.WriteLine($"[PROGRESS] activate: {message}"));
                    result.Activation = activation;
                    if (!activation.Completed)
                    {
                        steps.Add(new { step = 7, action = "Configuration activation did not complete; TwinCAT not restarted", activation });
                        result.Success = false;
                        result.ErrorMessage = activation.ErrorMessage;
                        return result;
                    }
                    steps.Add(new { step = 7, action = "Configuration activated", dryRun, verified = activation.Verified, signal = activation.Signal, attempts = activation.Attempts, elapsedMs = activation.ElapsedMs, warning = activation.Warning });
                }
                else
                {
                    steps.Add(new { step = 7, action = "Configuration activated", dryRun });
                }

                if (!dryRun)
                {
                    // XAE's activation prompt restarts in Run; the deployment is done only when the target is in Run.
                    var run = RunWait.RestartAndWait(amsNetId, plcPorts, automationInterface.StartRestartTwinCAT,
                        new RunWaitOptions { Timeout = TimeSpan.FromSeconds(options.RunTimeoutSeconds) },
                        message => Console.Error.WriteLine($"[PROGRESS] run: {message}"));
                    result.Run = run;
                    if (!run.ReachedRun)
                    {
                        steps.Add(new { step = 8, action = "TwinCAT restarted; the target did not reach Run", run.SystemState, run.PlcStates, run.Transitions });
                        result.Success = false;
                        result.DeployedPlcs = deployedPlcs;
                        result.ErrorMessage = run.Message + RunWait.Summary(run);
                        return result;
                    }
                    steps.Add(new { step = 8, action = "TwinCAT restarted; Run verified", run.SystemState, run.PlcStates, run.ElapsedMs });
                }
                else
                {
                    steps.Add(new { step = 8, action = $"TwinCAT restarted; wait up to {options.RunTimeoutSeconds} s for Run on port 10000 and PLC ports {string.Join(", ", plcPorts)}", dryRun });
                }

                result.Success = true;
                result.DeployedPlcs = deployedPlcs;
                string build = $"{selection.Configuration}|{selection.Platform}{(selection.PlatformVerified ? "" : " (platform not verified)")}";
                result.Message = dryRun
                    ? $"DRY RUN: Would deploy {build} to {amsNetId}"
                    : $"Deployed {build} to {amsNetId}";
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Deployment failed: {ex.Message}";
                Console.Error.WriteLine($"[DEBUG] Deployment failed: {ex}");
                return result;
            }
        }

        static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

        private static bool IsValidAmsNetId(string amsNetId)
        {
            if (string.IsNullOrWhiteSpace(amsNetId))
                return false;
                
            var parts = amsNetId.Split('.');
            if (parts.Length != 6)
                return false;
                
            foreach (var part in parts)
            {
                if (!int.TryParse(part, out int value) || value < 0 || value > 255)
                    return false;
            }
            
            return true;
        }

        private static int CountBuildErrors(ErrorItems errorItems)
        {
            int errorCount = 0;
            for (int i = 1; i <= errorItems.Count; i++)
            {
                ErrorItem item = errorItems.Item(i);
                if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh)
                {
                    errorCount++;
                }
            }
            return errorCount;
        }

        private static List<object> CollectErrors(ErrorItems errorItems)
        {
            var errors = new List<object>();
            for (int i = 1; i <= errorItems.Count; i++)
            {
                ErrorItem item = errorItems.Item(i);
                if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh)
                {
                    errors.Add(new
                    {
                        description = item.Description,
                        file = item.FileName,
                        line = item.Line
                    });
                }
            }
            return errors;
        }

        private static void OutputError(string message, List<object>? errors = null)
        {
            var result = new { success = false, error = message, errors = errors };
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
    }

    /// <summary>
    /// Strongly-typed deploy result used by both the CLI entry point and the
    /// persistent host path. Success / ErrorMessage naming matches the other
    /// session-result types so StepDispatcher can inspect them uniformly.
    /// </summary>
    public class DeployResult
    {
        public bool Success { get; set; }
        public string Solution { get; set; } = "";
        public string TargetNetId { get; set; } = "";
        public List<string> DeployedPlcs { get; set; } = new List<string>();
        public bool DryRun { get; set; }
        public List<object> Steps { get; set; } = new List<object>();
        public string? Message { get; set; }
        public string? ErrorMessage { get; set; }
        public List<object>? BuildErrors { get; set; }
        public ActivationWaitResult? Activation { get; set; }
        public string? Variant { get; set; }
        public string? Configuration { get; set; }
        public string? Platform { get; set; }
        public bool PlatformVerified { get; set; }
        public string? PlatformStatus { get; set; }
        public TargetDescription? Target { get; set; }
        public RunWaitResult? Run { get; set; }
    }

    /// <summary>What a deployment does; every field has the workflow's documented default.</summary>
    public class DeployOptions
    {
        public string? PlcName { get; set; }
        public bool SkipBuild { get; set; }
        public bool DryRun { get; set; }
        public string? Variant { get; set; }
        public string? Configuration { get; set; }
        public string? Platform { get; set; }
        public bool GenerateBootProject { get; set; } = true;
        public bool AllowUnverifiedPlatform { get; set; }
        public int RunTimeoutSeconds { get; set; } = 120;
    }
}
