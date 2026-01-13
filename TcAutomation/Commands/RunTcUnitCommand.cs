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
        private const string MARKER_EXPORTED = "TEST RESULTS EXPORTED";

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
            var stopwatch = Stopwatch.StartNew();

            // Default to local runtime
            amsNetId = amsNetId ?? "127.0.0.1.1.1";

            // Helper to output progress
            void Progress(string step, string message)
            {
                Console.Error.WriteLine($"[PROGRESS] {step}: {message}");
                Console.Error.Flush();
            }

            // Validate input
            if (!File.Exists(solutionPath))
            {
                result.Success = false;
                result.ErrorMessage = $"Solution file not found: {solutionPath}";
                return result;
            }

            Progress("init", "Starting TcUnit test run...");

            VisualStudioInstance? vsInstance = null;
            AdsClient? adsClient = null;

            try
            {
                // Register COM message filter
                MessageFilter.Register();

                // Find TwinCAT project
                Progress("init", "Looking for TwinCAT project...");
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
                Progress("vs", "Opening Visual Studio and loading solution...");
                vsInstance = new VisualStudioInstance(solutionPath, projectTcVersion, tcVersion);
                vsInstance.Load();
                vsInstance.LoadSolution();
                Progress("vs", "Solution loaded successfully");

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
                        ConfigureTask(realTimeConfig, taskName, false);
                        Progress("config", $"Auto-detected task '{taskName}'");
                    }
                    else
                    {
                        Progress("config", $"Multiple tasks found ({realTimeConfig.ChildCount}), using default configuration");
                    }
                }

                // Set boot project autostart for all PLCs (or specific one)
                Progress("config", "Configuring boot project...");
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
                    iecProject.GenerateBootProject(true);

                    // Get AMS port from project
                    string xml = plcProject.ProduceXml();
                    amsPort = ExtractAmsPort(xml) ?? 851;
                    
                    Progress("config", $"Boot project configured for '{plcProjectName}' (port {amsPort})");
                }

                // Set target
                Progress("target", $"Setting target to {amsNetId}...");
                sysManager.SetTargetNetId(amsNetId);

                // Disable I/O if requested
                if (disableIo)
                {
                    Progress("io", "Disabling I/O devices...");
                    DisableAllIoDevices(sysManager);
                    Progress("io", "I/O devices disabled");
                }

                // Clean error list before activation
                Progress("activate", "Preparing for activation...");
                vsInstance.CleanSolution();
                Thread.Sleep(2000);

                // Activate configuration
                Progress("activate", "Activating configuration on target...");
                sysManager.ActivateConfiguration();
                Thread.Sleep(5000);
                Progress("activate", "Configuration activated");

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
                        adsClient.Connect(amsNetId, amsPort);
                        var state = adsClient.ReadState();
                        if (state.AdsState == AdsState.Run)
                        {
                            plcRunning = true;
                            Progress("wait", "PLC is now in Run state");
                            break;
                        }
                        adsClient.Disconnect();
                        
                        waitAttempts++;
                        if (waitAttempts % 5 == 0)
                        {
                            Progress("wait", $"Still waiting for Run state (current: {state.AdsState})...");
                        }
                    }
                    catch { }
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
                    
                    for (int i = 1; i <= errorItems.Count; i++)
                    {
                        var item = errorItems.Item(i);
                        string desc = item.Description ?? "";

                        // Collect all TcUnit messages
                        if (desc.Contains("|") && (desc.Contains("Test") || desc.Contains("test")))
                        {
                            if (!result.TestMessages.Contains(desc))
                                result.TestMessages.Add(desc);
                        }

                        // Parse summary markers
                        if (desc.Contains(MARKER_TEST_SUITES))
                        {
                            testSuites = ExtractNumber(desc, MARKER_TEST_SUITES);
                        }
                        else if (desc.Contains(MARKER_TESTS))
                        {
                            tests = ExtractNumber(desc, MARKER_TESTS);
                        }
                        else if (desc.Contains(MARKER_SUCCESSFUL))
                        {
                            passed = ExtractNumber(desc, MARKER_SUCCESSFUL);
                        }
                        else if (desc.Contains(MARKER_FAILED))
                        {
                            failed = ExtractNumber(desc, MARKER_FAILED);
                        }
                        else if (desc.Contains(MARKER_DURATION))
                        {
                            duration = ExtractDouble(desc, MARKER_DURATION);
                        }
                        else if (desc.Contains(MARKER_EXPORTED))
                        {
                            resultsExported = true;
                        }

                        // Capture actual failed test details (status=FAIL, not summary lines)
                        if (desc.Contains("status=FAIL") || 
                            (desc.Contains("FAILED") && !desc.Contains("Failed tests:") && !desc.Contains("failed tests=")))
                        {
                            if (!result.FailedTestDetails.Contains(desc))
                                result.FailedTestDetails.Add(desc);
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
                            Progress("poll", "Waiting for TcUnit results...");
                        }
                    }

                    // Check if results are complete
                    if (resultsExported && testSuites >= 0 && tests >= 0 && passed >= 0 && failed >= 0)
                    {
                        Progress("poll", "TcUnit results received!");
                        // Wait a bit more for final messages
                        Thread.Sleep(3000);
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
                adsClient?.Disconnect();
                adsClient?.Dispose();
                vsInstance?.Close();
                MessageFilter.Revoke();
            }

            return result;
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
                    // Enable and autostart the test task
                    xml = SetTaskDisabledAndAutostart(xml, false, true);
                }
                else if (disableOthers)
                {
                    // Disable other tasks
                    xml = SetTaskDisabledAndAutostart(xml, true, false);
                }

                task.ConsumeXml(xml);
                Thread.Sleep(500);
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
                var nameNode = doc.SelectSingleNode("//Name");
                return nameNode?.InnerText ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string SetTaskDisabledAndAutostart(string xml, bool disabled, bool autostart)
        {
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xml);

                // Find or create Disabled node
                var disabledNode = doc.SelectSingleNode("//Disabled");
                if (disabledNode != null)
                    disabledNode.InnerText = disabled.ToString().ToLower();

                // Find or create AutoStart node
                var autostartNode = doc.SelectSingleNode("//AutoStart");
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
                var portNode = doc.SelectSingleNode("//AmsPort");
                if (portNode != null && int.TryParse(portNode.InnerText, out int port))
                    return port;
            }
            catch { }
            return null;
        }

        private static void DisableAllIoDevices(ITcSysManager10 sysManager)
        {
            try
            {
                ITcSmTreeItem ioConfig = sysManager.LookupTreeItem("TIID");
                for (int i = 1; i <= ioConfig.ChildCount; i++)
                {
                    ITcSmTreeItem device = ioConfig.Child[i];
                    string xml = device.ProduceXml();
                    
                    var doc = new XmlDocument();
                    doc.LoadXml(xml);
                    var disabledNode = doc.SelectSingleNode("//Disabled");
                    if (disabledNode != null)
                    {
                        disabledNode.InnerText = "true";
                        device.ConsumeXml(doc.OuterXml);
                    }
                }
            }
            catch { }
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
    }
}
