using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Xml;
using EnvDTE80;
using TCatSysManagerLib;
using TwinCAT.Ads;
using TcAutomation.Core;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Run TcUnit tests on a TwinCAT project.
    /// 
    /// Workflow:
    /// 1. Build solution
    /// 2. Configure test task (enable it, disable others if specified)
    /// 3. Set boot project autostart
    /// 4. Optionally disable I/O devices
    /// 5. Activate configuration
    /// 6. Restart TwinCAT
    /// 7. Poll Error List for TcUnit results
    /// 8. Return test results
    /// </summary>
    public static class RunTcUnitCommand
    {
        public class TcUnitResult
        {
            public bool Success { get; set; }
            public string? ErrorMessage { get; set; }
            public int TestSuites { get; set; }
            public int TotalTests { get; set; }
            public int PassedTests { get; set; }
            public int FailedTests { get; set; }
            public double Duration { get; set; }
            public bool AllTestsPassed { get; set; }
            public List<string> TestMessages { get; set; } = new List<string>();
            public List<string> FailedTestDetails { get; set; } = new List<string>();
            public string Summary { get; set; } = "";
        }

        // TcUnit result markers
        private const string MARKER_TEST_SUITES = "| Test suites:";
        private const string MARKER_TESTS = "| Tests:";
        private const string MARKER_SUCCESSFUL = "| Successful tests:";
        private const string MARKER_FAILED = "| Failed tests:";
        private const string MARKER_DURATION = "| Duration:";
        // TcUnit prints one of these as the terminator:
        //   "==========TESTS FINISHED RUNNING==========" (standard TcUnit)
        //   "TEST RESULTS EXPORTED"                     (TcUnit-Runner-Support library)
        private const string MARKER_FINISHED = "TESTS FINISHED RUNNING";
        private const string MARKER_EXPORTED = "TEST RESULTS EXPORTED";

        /// <summary>
        /// CLI entrypoint: opens a one-shot VS instance, runs the test workflow,
        /// closes it. Used by `TcAutomation.exe run-tcunit` from the MCP's
        /// CLI-fallback path and anyone invoking the tool directly.
        /// </summary>
        public static TcUnitResult Execute(
            string solutionPath,
            string? amsNetId = null,
            string? taskName = null,
            string? plcName = null,
            string? tcVersion = null,
            int timeoutMinutes = 10,
            bool disableIo = false,
            bool skipBuild = false)
        {
            var result = new TcUnitResult();

            if (!File.Exists(solutionPath))
            {
                result.Success = false;
                result.ErrorMessage = $"Solution file not found: {solutionPath}";
                return result;
            }

            ProgressStatic("init", "Starting TcUnit test run...");

            VisualStudioInstance? vsInstance = null;
            try
            {
                MessageFilter.Register();

                ProgressStatic("init", "Looking for TwinCAT project...");
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

                ProgressStatic("vs", "Opening Visual Studio and loading solution...");
                vsInstance = new VisualStudioInstance(solutionPath, projectTcVersion, tcVersion);
                vsInstance.Load();
                vsInstance.LoadSolution();
                vsInstance.CloseAllDocuments();
                ProgressStatic("vs", "Solution loaded successfully");

                return ExecuteInSession(
                    vsInstance, solutionPath,
                    amsNetId, taskName, plcName,
                    timeoutMinutes, disableIo, skipBuild);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"TcUnit execution failed: {ex.Message}";
                ProgressStatic("error", $"Exception: {ex.Message}");
                return result;
            }
            finally
            {
                try { MessageFilter.Revoke(); } catch { }
                try { vsInstance?.Close(); } catch { }
            }
        }

        /// <summary>
        /// Runs the TcUnit workflow against an already-open VS instance.
        /// Used by the persistent shell host so the ~30s TcXaeShell cold-start
        /// is paid once per MCP session instead of per test run.
        ///
        /// Contract differences vs Execute:
        ///   - Caller owns the VisualStudioInstance (and the MessageFilter).
        ///     This method will NOT close the DTE or revoke the MessageFilter.
        ///   - Caller is responsible for ensuring the solution is loaded.
        ///
        /// NOTE: This mutates the solution on disk (task enable/autostart XML,
        /// BootProjectAutostart=true, target netId, optionally I/O disabled).
        /// Same side effects as the standalone Execute() — migration to host
        /// does NOT change this. If you need a pristine solution afterwards,
        /// call VisualStudioInstance.ReloadSolution().
        /// </summary>
        public static TcUnitResult ExecuteInSession(
            VisualStudioInstance vsInstance,
            string solutionPath,
            string? amsNetId = null,
            string? taskName = null,
            string? plcName = null,
            int timeoutMinutes = 10,
            bool disableIo = false,
            bool skipBuild = false)
        {
            var result = new TcUnitResult();
            var stopwatch = Stopwatch.StartNew();

            amsNetId = amsNetId ?? "127.0.0.1.1.1";

            void Progress(string step, string message) => ProgressStatic(step, message);

            AdsClient? adsClient = null;
            int? dialogWatchdogPid = null;

            try
            {
                // Close any auto-reopened documents from the .suo. In host mode
                // this has usually already been done at ensure-solution, but it
                // is cheap and idempotent so we repeat to be safe.
                try { vsInstance.CloseAllDocuments(); } catch { }

                // Register this DTE with the dialog-dismisser so modals
                // (AdsError popup on activation, "file changed outside"
                // prompts, etc.) auto-close. Scoped to our PID so the user's
                // own IDE is never touched.
                //
                // In the persistent-host path this is usually already running
                // (VisualStudioInstance.ConfigureDte starts it for the DTE's
                // whole lifetime); Start is ref-counted so this Start/Stop
                // pair is safe and nests correctly.
                if (vsInstance.DteProcessId.HasValue && vsInstance.DteProcessId.Value > 0)
                {
                    DialogWatchdog.Start(vsInstance.DteProcessId.Value);
                    dialogWatchdogPid = vsInstance.DteProcessId.Value;
                }

                var sysManager = vsInstance.GetSystemManager();

                // Find PLC projects
                ITcSmTreeItem plcConfig = sysManager.LookupTreeItem("TIPC");
                if (plcConfig.ChildCount == 0)
                {
                    result.Success = false;
                    result.ErrorMessage = "No PLC project found in solution";
                    return result;
                }

                // Build if not skipped
                if (!skipBuild)
                {
                    Progress("build", "Cleaning solution...");
                    vsInstance.CleanSolution();
                    
                    Progress("build", "Building solution...");
                    vsInstance.BuildSolution();

                    // Check for build errors
                    var errorItems = vsInstance.GetErrorItems();
                    int buildErrors = 0;
                    for (int i = 1; i <= errorItems.Count; i++)
                    {
                        var item = errorItems.Item(i);
                        if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh)
                        {
                            buildErrors++;
                            result.TestMessages.Add($"Build Error: {item.Description}");
                        }
                    }

                    if (buildErrors > 0)
                    {
                        result.Success = false;
                        result.ErrorMessage = $"Build failed with {buildErrors} error(s)";
                        Progress("build", $"Build FAILED with {buildErrors} error(s)");
                        return result;
                    }
                    Progress("build", "Build succeeded");
                }
                else
                {
                    Progress("build", "Skipping build (--skip-build)");
                }

                // Configure task if specified
                Progress("config", "Configuring tasks...");
                ITcSmTreeItem realTimeConfig = sysManager.LookupTreeItem("TIRT");
                if (!string.IsNullOrEmpty(taskName))
                {
                    ConfigureTask(realTimeConfig, taskName, true);
                    Progress("config", $"Configured task '{taskName}' for testing");
                }
                else
                {
                    // Auto-detect single task
                    if (realTimeConfig.ChildCount == 1)
                    {
                        var singleTask = realTimeConfig.Child[1];
                        taskName = GetTaskName(singleTask);
                        ConfigureTask(realTimeConfig, taskName, true);  // Still disable others for consistency
                        Progress("config", $"Auto-detected task '{taskName}'");
                    }
                    else
                    {
                        // Multiple tasks but no task specified - this is an error for unit testing
                        result.Success = false;
                        result.ErrorMessage = $"Multiple tasks found ({realTimeConfig.ChildCount}). Please specify which task runs TcUnit tests using --task parameter";
                        return result;
                    }
                }

                // Enable boot project autostart for all PLCs (or specific one).
                // IMPORTANT: Do NOT call GenerateBootProject() here. Per TcUnit-Runner
                // reference, ActivateConfiguration() generates the boot project
                // internally. Calling GenerateBootProject() explicitly triggers
                // E_FAIL/E_UNEXPECTED because it requires active solution build
                // configuration state that is only set during an actual build.
                Progress("config", "Enabling boot project autostart...");
                int amsPort = 851;
                for (int i = 1; i <= plcConfig.ChildCount; i++)
                {
                    ITcSmTreeItem plcProject = plcConfig.Child[i];
                    string plcProjectName = plcProject.Name;

                    // Skip if plcName specified and doesn't match
                    if (!string.IsNullOrEmpty(plcName) && !plcProjectName.Equals(plcName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    ITcPlcProject iecProject = (ITcPlcProject)plcProject;
                    iecProject.BootProjectAutostart = true;

                    // Get AMS port from project XML (read-only)
                    string xml = plcProject.ProduceXml();
                    var extractedPort = ExtractAmsPort(xml);
                    Console.Error.WriteLine($"[DEBUG] ExtractAmsPort for '{plcProjectName}': {extractedPort?.ToString() ?? "null"}");
                    amsPort = extractedPort ?? 851;

                    Progress("config", $"BootProjectAutostart enabled for '{plcProjectName}' (port {amsPort})");
                }
                Thread.Sleep(1000);

                // Set target
                Progress("target", $"Setting target to {amsNetId}...");
                sysManager.SetTargetNetId(amsNetId);

                // Create AutomationInterface for additional configuration
                var automationInterface = new AutomationInterface(vsInstance.GetProject());

                // Set DontCheckTarget to suppress activation confirmation dialog
                // This is necessary because SilentMode doesn't suppress this specific dialog
                Progress("config", "Setting DontCheckTarget to suppress activation dialog...");
                automationInterface.SetDontCheckTarget(amsNetId);

                // NOTE: Do NOT call AssignCPUCores() here. The project's RT config
                // (MaxCpus, NonWinCpus, Affinity) must match the target hardware.
                // Overwriting it causes activation failures on remote PLCs.
                // TcUnit-Runner does not modify RT config either.

                // Disable I/O if requested - use the improved method from AutomationInterface
                if (disableIo)
                {
                    Progress("io", "Disabling I/O devices...");
                    automationInterface.DisableAllIoDevices(true);
                }

                // Activate configuration
                Progress("activate", "Activating configuration on target...");
                sysManager.ActivateConfiguration();
                Thread.Sleep(10000);
                Progress("activate", "Configuration activated");

                // Clean the solution AFTER activation to clear Error List of build messages
                // This is critical - TcUnit messages won't appear if Error List has build clutter
                // This must happen BETWEEN activation and restart (TcUnit-Runner does this)
                Progress("activate", "Clearing Error List for TcUnit messages...");
                vsInstance.CleanSolution();
                Thread.Sleep(10000);

                // Restart TwinCAT
                Progress("restart", "Restarting TwinCAT runtime...");
                sysManager.StartRestartTwinCAT();
                Thread.Sleep(10000);
                Progress("restart", "TwinCAT restart initiated");

                // Wait for TwinCAT to be in Run state
                Progress("wait", $"Waiting for PLC to enter Run state (timeout: {timeoutMinutes} min)...");
                adsClient = new AdsClient();
                var timeout = DateTime.Now.AddMinutes(timeoutMinutes);
                bool plcRunning = false;
                int waitAttempts = 0;

                while (DateTime.Now < timeout)
                {
                    try
                    {
                        if (!adsClient.IsConnected)
                        {
                            adsClient.Connect(amsNetId, amsPort);
                        }
                        var state = adsClient.ReadState();
                        if (state.AdsState == AdsState.Run)
                        {
                            plcRunning = true;
                            Progress("wait", "PLC is now in Run state");
                            break;
                        }
                        
                        waitAttempts++;
                        if (waitAttempts % 5 == 0)
                        {
                            Progress("wait", $"Still waiting for Run state (current: {state.AdsState})...");
                        }
                    }
                    catch (Exception ex)
                    {
                        waitAttempts++;
                        if (waitAttempts % 5 == 0)
                        {
                            Progress("wait", $"ADS connection attempt failed: {ex.Message}");
                        }
                        // Disconnect and retry
                        try { adsClient.Disconnect(); } catch { }
                    }
                    Thread.Sleep(2000);
                }

                if (!plcRunning)
                {
                    result.Success = false;
                    result.ErrorMessage = "PLC did not reach Run state within timeout";
                    Progress("wait", "TIMEOUT: PLC did not reach Run state");
                    return result;
                }

                // Poll Error List for TcUnit results
                Progress("poll", "Polling for TcUnit test results...");
                int testSuites = -1, tests = -1, passed = -1, failed = -1;
                double duration = 0;
                bool resultsExported = false;
                int pollCount = 0;
                
                // Track current test context for better failure reporting
                string currentTestSuite = "";
                string currentTestName = "";

                while (DateTime.Now < timeout)
                {
                    Thread.Sleep(5000);
                    pollCount++;

                    // Check PLC state
                    try
                    {
                        var state = adsClient.ReadState();
                        if (state.AdsState != AdsState.Run)
                        {
                            result.Success = false;
                            result.ErrorMessage = $"PLC entered unexpected state: {state.AdsState}";
                            Progress("poll", $"ERROR: PLC entered unexpected state: {state.AdsState}");
                            return result;
                        }
                    }
                    catch { }

                    // Read error list
                    var errorItems = vsInstance.GetErrorItems();
                    
                    // First pass: Check ALL messages for summary markers (like TcUnit-Runner does)
                    // Summary messages may not have the task name format, so check before filtering
                    for (int i = 1; i <= errorItems.Count; i++)
                    {
                        var item = errorItems.Item(i);
                        string desc = item.Description ?? "";

                        // Parse summary markers from ANY message (not filtered by task)
                        if (desc.Contains(MARKER_TEST_SUITES))
                        {
                            testSuites = ExtractNumber(desc, MARKER_TEST_SUITES);
                        }
                        if (desc.Contains(MARKER_TESTS))
                        {
                            tests = ExtractNumber(desc, MARKER_TESTS);
                        }
                        if (desc.Contains(MARKER_SUCCESSFUL))
                        {
                            passed = ExtractNumber(desc, MARKER_SUCCESSFUL);
                        }
                        if (desc.Contains(MARKER_FAILED))
                        {
                            failed = ExtractNumber(desc, MARKER_FAILED);
                        }
                        if (desc.Contains(MARKER_DURATION))
                        {
                            duration = ExtractDouble(desc, MARKER_DURATION);
                        }
                        if (desc.Contains(MARKER_EXPORTED) || desc.Contains(MARKER_FINISHED))
                        {
                            resultsExported = true;
                        }
                    }
                    
                    // Second pass: Process TcUnit task messages for detailed results
                    for (int i = 1; i <= errorItems.Count; i++)
                    {
                        var item = errorItems.Item(i);
                        string desc = item.Description ?? "";

                        // Only process messages from the TcUnit task (filter out license server, etc.)
                        if (!IsTcUnitAdsMessage(desc, taskName))
                            continue;

                        // Extract just the TcUnit message part
                        string tcUnitMsg = ExtractTcUnitMessage(desc, taskName);

                        // Collect TcUnit messages (for debugging if needed)
                        if (!result.TestMessages.Contains(tcUnitMsg))
                            result.TestMessages.Add(tcUnitMsg);

                        // Track test suite name: "Test suite ID=0 'PRG_TEST.TestSuite'"
                        if (tcUnitMsg.Contains("Test suite ID="))
                        {
                            int quoteStart = tcUnitMsg.IndexOf("'");
                            int quoteEnd = tcUnitMsg.LastIndexOf("'");
                            if (quoteStart >= 0 && quoteEnd > quoteStart)
                            {
                                currentTestSuite = tcUnitMsg.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
                            }
                        }
                        // Track test name: "Test name=TestMethod"
                        else if (tcUnitMsg.Contains("Test name="))
                        {
                            int eqIdx = tcUnitMsg.IndexOf("Test name=");
                            if (eqIdx >= 0)
                            {
                                currentTestName = tcUnitMsg.Substring(eqIdx + 10).Trim();
                            }
                        }

                        // Capture failed test with full context (suite.test: details)
                        // TcUnit format: FAILED TEST 'Suite@TestName', EXP: expected, ACT: actual, MSG: message
                        if (tcUnitMsg.Contains("FAILED TEST"))
                        {
                            // Extract the failure details directly from the message
                            // Format: FAILED TEST 'MAIN.fbTests@TestMethod', EXP: value, ACT: value, MSG: message
                            string failDetail = tcUnitMsg;
                            
                            // Clean up - remove "FAILED TEST " prefix for cleaner output
                            if (failDetail.StartsWith("FAILED TEST "))
                            {
                                failDetail = failDetail.Substring(12).Trim();
                                // Remove surrounding quotes if present
                                if (failDetail.StartsWith("'"))
                                {
                                    int endQuote = failDetail.IndexOf("'", 1);
                                    if (endQuote > 0)
                                    {
                                        string testName = failDetail.Substring(1, endQuote - 1);
                                        string remainder = failDetail.Substring(endQuote + 1).TrimStart(',', ' ');
                                        // Replace @ with . for readability
                                        testName = testName.Replace("@", ".");
                                        failDetail = $"{testName}: {remainder}";
                                    }
                                }
                            }
                            
                            if (!result.FailedTestDetails.Contains(failDetail))
                                result.FailedTestDetails.Add(failDetail);
                        }
                    }

                    // Progress update every few polls
                    if (pollCount % 2 == 0)
                    {
                        if (tests >= 0)
                        {
                            Progress("poll", $"Found {tests} tests so far (suites: {testSuites}, passed: {passed}, failed: {failed})");
                        }
                        else
                        {
                            Progress("poll", $"Waiting for TcUnit results... (error list has {errorItems.Count} items)");
                            // Debug: dump first 5 error list items to help diagnose
                            for (int dbg = 1; dbg <= Math.Min(5, errorItems.Count); dbg++)
                            {
                                Console.Error.WriteLine($"[DEBUG] ErrorItem[{dbg}]: {errorItems.Item(dbg).Description?.Substring(0, Math.Min(200, errorItems.Item(dbg).Description?.Length ?? 0))}");
                            }
                        }
                    }

                    // Check if results are complete.
                    // TcUnit-Runner only checks that all summary values are populated.
                    // No need to wait for "TESTS FINISHED RUNNING" / "TEST RESULTS EXPORTED".
                    if (testSuites >= 0 && tests >= 0 && passed >= 0 && failed >= 0)
                    {
                        Progress("poll", "TcUnit results received!");
                        // Wait for any remaining detailed messages (test names, failures, etc.)
                        Thread.Sleep(10000);
                        break;
                    }
                }

                // Populate result
                if (resultsExported)
                {
                    result.Success = true;
                    result.TestSuites = testSuites;
                    result.TotalTests = tests;
                    result.PassedTests = passed;
                    result.FailedTests = failed;
                    result.Duration = duration;
                    result.AllTestsPassed = failed == 0;
                    result.Summary = $"TcUnit: {passed}/{tests} tests passed ({testSuites} suites) in {duration:F1}s";

                    if (failed > 0)
                    {
                        result.Summary += $" - {failed} FAILED";
                        Progress("complete", $"Tests completed: {passed}/{tests} passed, {failed} FAILED");
                    }
                    else
                    {
                        Progress("complete", $"Tests completed: ALL {tests} TESTS PASSED!");
                    }
                }
                else
                {
                    result.Success = false;
                    result.ErrorMessage = "TcUnit results not received within timeout. Check if TcUnit is properly configured and the test task is running.";
                    Progress("complete", "TIMEOUT: TcUnit results not received");
                }

                stopwatch.Stop();
                Progress("complete", $"Total execution time: {stopwatch.Elapsed.TotalSeconds:F1}s");
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"TcUnit execution failed: {ex.Message}";
                Progress("error", $"Exception: {ex.Message}");
            }
            finally
            {
                if (dialogWatchdogPid.HasValue)
                {
                    try { DialogWatchdog.Stop(dialogWatchdogPid.Value); } catch { }
                }
                try { adsClient?.Disconnect(); } catch { }
                try { adsClient?.Dispose(); } catch { }
                // NOTE: We do NOT close vsInstance here. The caller (host or CLI
                // wrapper) owns its lifetime. Same for MessageFilter.Revoke().
            }

            return result;
        }

        /// <summary>
        /// stderr progress helper — matches the legacy "[PROGRESS] step: message"
        /// line format that both run_tc_automation_with_progress and
        /// ShellHost.stderr_loop know how to parse.
        /// </summary>
        private static void ProgressStatic(string step, string message)
        {
            try
            {
                Console.Error.WriteLine($"[PROGRESS] {step}: {message}");
                Console.Error.Flush();
            }
            catch { }
        }

        private static void ConfigureTask(ITcSmTreeItem realTimeConfig, string taskName, bool disableOthers)
        {
            for (int i = 1; i <= realTimeConfig.ChildCount; i++)
            {
                ITcSmTreeItem task = realTimeConfig.Child[i];
                string xml = task.ProduceXml();
                string currentTaskName = GetTaskNameFromXml(xml);

                bool isTargetTask = currentTaskName.Equals(taskName, StringComparison.OrdinalIgnoreCase);
                
                if (isTargetTask)
                {
                    // Enable and autostart the test task — all via XML like TcUnit-Runner
                    string newXml = SetDisabledAndAutoStartInXml(xml, false, true);
                    task.ConsumeXml(newXml);
                }
                else if (disableOthers)
                {
                    // Disable other tasks — all via XML like TcUnit-Runner
                    string newXml = SetDisabledAndAutoStartInXml(xml, true, false);
                    task.ConsumeXml(newXml);
                }

                // Same 3 second delay as TcUnit-Runner after each task update
                Thread.Sleep(3000);
            }
        }

        private static string GetTaskName(ITcSmTreeItem task)
        {
            string xml = task.ProduceXml();
            return GetTaskNameFromXml(xml);
        }

        private static string GetTaskNameFromXml(string xml)
        {
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                // Use same XPath as TcUnit-Runner: /TreeItem/ItemName
                var nameNode = doc.SelectSingleNode("/TreeItem/ItemName");
                return nameNode?.InnerText ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string SetDisabledAndAutoStartInXml(string xml, bool disabled, bool autostart)
        {
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xml);

                // Set Disabled flag: /TreeItem/TaskDef/Disabled
                var disabledNode = doc.SelectSingleNode("/TreeItem/TaskDef/Disabled");
                if (disabledNode != null)
                    disabledNode.InnerText = disabled ? "true" : "false";

                // Set AutoStart flag: /TreeItem/TaskDef/AutoStart
                var autostartNode = doc.SelectSingleNode("/TreeItem/TaskDef/AutoStart");
                if (autostartNode != null)
                    autostartNode.InnerText = autostart.ToString().ToLower();

                return doc.OuterXml;
            }
            catch
            {
                return xml;
            }
        }

        private static int? ExtractAmsPort(string xml)
        {
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                
                // TwinCAT uses AdsPort (not AmsPort) in the PlcProjectDef element
                // Path: /TreeItem/PlcProjectDef/AdsPort
                var portNode = doc.SelectSingleNode("/TreeItem/PlcProjectDef/AdsPort");
                if (portNode != null && int.TryParse(portNode.InnerText, out int port))
                    return port;
            }
            catch { }
            return null;
        }

        private static int ExtractNumber(string text, string marker)
        {
            try
            {
                int idx = text.LastIndexOf(marker);
                if (idx >= 0)
                {
                    string numStr = text.Substring(idx + marker.Length).Trim();
                    if (int.TryParse(numStr, out int num))
                        return num;
                }
            }
            catch { }
            return -1;
        }

        private static double ExtractDouble(string text, string marker)
        {
            try
            {
                int idx = text.LastIndexOf(marker);
                if (idx >= 0)
                {
                    string numStr = text.Substring(idx + marker.Length).Trim();
                    if (double.TryParse(numStr, out double num))
                        return num;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// Returns whether the message is a message that originated from TcUnit.
        /// Filters by task name to avoid picking up unrelated ADS messages.
        /// 
        /// For example, this would return false:
        /// Message 20 2020-04-09 07:36:00 901 ms | 'License Server' (30): license validation status is Valid(3)
        /// 
        /// While this would return true:
        /// Message 29 2020-04-09 07:36:01 464 ms | 'UnitTestTask' (351): | Test suite ID=0 'PRG_TEST.Test'
        /// </summary>
        private static bool IsTcUnitAdsMessage(string message, string taskName)
        {
            if (string.IsNullOrEmpty(taskName))
            {
                // Fallback: if no task name, check for TcUnit-specific markers
                return message.Contains("|") && 
                       (message.Contains("Test suite ID=") ||
                        message.Contains("Test name=") ||
                        message.Contains("Test status=") ||
                        message.Contains("Test class name=") ||
                        message.Contains(MARKER_TEST_SUITES) ||
                        message.Contains(MARKER_TESTS) ||
                        message.Contains(MARKER_SUCCESSFUL) ||
                        message.Contains(MARKER_FAILED) ||
                        message.Contains(MARKER_DURATION) ||
                        message.Contains(MARKER_EXPORTED) ||
                        message.Contains(MARKER_FINISHED));
            }

            // Look for task name in format 'TaskName'
            string taskMarker = "'" + taskName + "'";
            int idx = message.IndexOf(taskMarker);
            if (idx < 0)
                return false;

            // Look for the | character after the task name (TcUnit messages have this)
            string remainingString = message.Substring(idx + taskMarker.Length);
            return remainingString.Contains("|");
        }

        /// <summary>
        /// Removes everything from the error-log other than the ADS message from TcUnit.
        /// Converts messages like:
        /// Message 53 2020-04-09 07:36:01 864 ms | 'UnitTestTask' (351): | Test class name=PRG_TEST.Test
        /// to:
        /// Test class name=PRG_TEST.Test
        /// </summary>
        private static string ExtractTcUnitMessage(string message, string taskName)
        {
            try
            {
                if (string.IsNullOrEmpty(taskName))
                {
                    // Fallback: find the last | and return everything after
                    int lastPipe = message.LastIndexOf("| ");
                    if (lastPipe >= 0)
                        return message.Substring(lastPipe + 2).Trim();
                    return message;
                }

                string taskMarker = "'" + taskName + "'";
                int idx = message.IndexOf(taskMarker);
                if (idx < 0)
                    return message;

                // Get everything after the task name
                string remaining = message.Substring(idx + taskMarker.Length);

                // Find the | character and get everything after it
                int pipeIdx = remaining.IndexOf("|");
                if (pipeIdx >= 0)
                    return remaining.Substring(pipeIdx + 1).Trim();

                return remaining.Trim();
            }
            catch
            {
                return message;
            }
        }
    }
}
