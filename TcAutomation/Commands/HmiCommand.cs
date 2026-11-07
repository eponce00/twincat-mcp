using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;
using TcAutomation.Core;

namespace TcAutomation.Commands
{
    /// <summary>Native TE2000 publishing only. Does not use PLC APIs or copy deployed files.</summary>
    public static class HmiCommand
    {
        public const string AutomationObject = "Beckhoff.TcHmi.1.12";
        private static string _attempt;
        private static string _attemptProject;

        private static string S(JsonElement args, string key) => StepDispatcher.GetString(args, key, true);
        private static object Failure(string code, bool unknown = false, string attempt = null) => new
            { Success = false, code, outcomeUnknown = unknown, submissionId = attempt,
                message = "HMI operation failed at " + code + ". No automatic retry or alternate deployment was attempted." };

        public static object ExecuteInSession(VisualStudioInstance vs, string command, JsonElement args)
        {
            bool dispatched = false;
            string stage = "hmi_sdk";
            try
            {
                if (!vs.HmiOnly) return Failure("wrong_context_kind");
                var projectPath = Path.GetFullPath(S(args, "projectPath"));
                var expectedUrl = HmiPolicy.Target(S(args, "targetUrl"));
                var timeout = StepDispatcher.GetInt(args, "timeoutSeconds", true) ?? 180;
                var sdk = FindSdk();
                var assembly = Assembly.LoadFrom(sdk);
                object api = vs.Dte.GetObject(AutomationObject);
                // This suppresses HMI dialogs, not certificate checks or authentication.
                Call(assembly, api, "ITcHmiAutomation", "SuppressUi", true);
                stage = "hmi_project";
                object project = null;
                var ready = Stopwatch.StartNew();
                while (ready.Elapsed.TotalSeconds < Math.Min(timeout, 60))
                {
                    foreach (object candidate in (Array)Call(assembly, api, "ITcHmiAutomation", "GetHmiProjects"))
                    {
                        object info = Call(assembly, candidate, "ITcHmiProject", "GetProjectInformation");
                        string path = Path.Combine((string)Get(assembly, info, "ITcHmiProjectInformation", "ProjectDirectory"),
                            (string)Get(assembly, info, "ITcHmiProjectInformation", "ProjectName") + ".hmiproj");
                        if (string.Equals(Path.GetFullPath(path), projectPath, StringComparison.OrdinalIgnoreCase))
                        { project = candidate; break; }
                    }
                    if (project != null && (bool)Call(assembly, project, "ITcHmiProject", "IsReady")) break;
                    Thread.Sleep(200);
                }
                if (project == null || !(bool)Call(assembly, project, "ITcHmiProject", "IsReady")) return Failure("hmi_project_not_ready");
                var directory = Path.GetDirectoryName(projectPath);
                var sdkVersion = FileVersionInfo.GetVersionInfo(sdk).FileVersion;
                if (command == "hmi-status")
                {
                    object result = Call(assembly, project, "ITcHmiProject", "GetPublishResult");
                    return new { Success = true, projectPath, targetUrl = expectedUrl,
                        hasSubmission = _attemptProject == projectPath && _attempt != null,
                        isPublishing = (bool)Call(assembly, project, "ITcHmiProject", "IsPublishRunning"), lastResult = result is bool ? (bool?)result : null,
                        submissionId = _attemptProject == projectPath ? _attempt : null };
                }
                if ((bool)Call(assembly, project, "ITcHmiProject", "IsPublishRunning")) return Failure("hmi_publish_busy");
                if (command == "hmi-inspect")
                    return new { Success = true, projectPath, targetUrl = expectedUrl, ready = true,
                        automationObject = AutomationObject, sdkVersion, profiles = HmiPolicy.Profiles(directory) };
                if (command == "hmi-build")
                {
                    stage = "hmi_build";
                    bool built = (bool)Call(assembly, project, "ITcHmiProject", "Build", "Release", false);
                    return new { Success = built, projectPath, configuration = "Release", sdkVersion,
                        message = built ? "Native HMI build completed." : "Native HMI build failed." };
                }

                stage = "hmi_profile";
                var name = S(args, "profileName");
                if (!HmiPolicy.Profiles(directory).Contains(name, StringComparer.Ordinal)) return Failure("hmi_profile_missing");
                object profile = Call(assembly, project, "ITcHmiProject", "GetPublishProfile", name);
                HmiPolicy.Profile((string)Get(assembly, profile, "Publish.ITcHmiProfile", "TargetUrl"), expectedUrl, (bool)Get(assembly, profile, "Publish.ITcHmiProfile", "DeleteAllFilesBeforePublish"),
                    (bool)Get(assembly, profile, "Publish.ITcHmiProfile", "DeleteExtensionDateBeforePublish"), (bool)Get(assembly, profile, "Publish.ITcHmiProfile", "AbortOnErrors"));
                if (!(bool)Call(assembly, project, "ITcHmiProject", "IsValidPublishProfile", name)) return Failure("hmi_profile_invalid");
                var configuration = (string)Get(assembly, profile, "Publish.ITcHmiProfile", "PublishConfiguration");
                stage = "hmi_source";
                var fingerprint = HmiPolicy.Fingerprint(projectPath, expectedUrl, name, sdk);
                if (command == "hmi-preflight")
                    return new { Success = true, projectPath, targetUrl = expectedUrl, profileName = name,
                        publishConfiguration = configuration, sdkVersion, fingerprint, deleteFiles = false, deleteExtensionData = false };
                if (command != "hmi-publish") return Failure("unsupported_hmi_operation");
                if (!string.Equals(fingerprint, S(args, "expectedFingerprint"), StringComparison.Ordinal))
                    return Failure("hmi_preflight_changed");

                _attempt = Guid.NewGuid().ToString("N");
                _attemptProject = projectPath;
                // Submit once. A timeout or lost reply is never followed by another publish.
                stage = "hmi_publish";
                dispatched = true;
                var clock = Stopwatch.StartNew();
                bool accepted = (bool)Call(assembly, project, "ITcHmiProject", "Publish", name, false, null);
                if (!accepted) return Failure("hmi_publish_failed", false, _attempt);
                while ((bool)Call(assembly, project, "ITcHmiProject", "IsPublishRunning") && clock.Elapsed.TotalSeconds < timeout) Thread.Sleep(200);
                if ((bool)Call(assembly, project, "ITcHmiProject", "IsPublishRunning")) return Failure("hmi_publish_timeout", true, _attempt);
                object outcome = Call(assembly, project, "ITcHmiProject", "GetPublishResult");
                if (!(outcome is bool)) return Failure("unsupported_hmi_result", true, _attempt);
                if (!(bool)outcome) return Failure("hmi_publish_failed", false, _attempt);
                string after;
                try { after = HmiPolicy.Fingerprint(projectPath, expectedUrl, name, sdk); }
                catch { after = null; }
                return new { Success = true, projectPath, targetUrl = expectedUrl, profileName = name,
                    publishConfiguration = configuration, submissionId = _attempt, nativeResult = true, sdkVersion,
                    fingerprint, sourceChangedDuringPublish = after != fingerprint, elapsedSeconds = clock.Elapsed.TotalSeconds,
                    message = "Native HMI publication completed. PLC configuration and state were not changed." };
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("hmi_") || ex.Message.StartsWith("invalid_hmi") || ex.Message.StartsWith("destructive_hmi"))
            { return Failure(ex.Message, dispatched, dispatched ? _attempt : null); }
            catch (Exception ex)
            {
                // Vendor exceptions may carry URLs or credentials; do not serialize their text.
                Console.Error.WriteLine($"[DEBUG] HMI {stage}: {ex.GetType().Name}, HRESULT {ex.HResult}");
                return Failure(stage, dispatched, dispatched ? _attempt : null);
            }
        }

        private static object Call(Assembly sdk, object instance, string contract, string method, params object[] args)
        {
            var type = sdk.GetType("TcHmiAutomation." + contract, true);
            var matches = type.GetMethods().Where(m => m.Name == method && m.GetParameters().Length == args.Length)
                .Where(m => m.GetParameters().Select((p, i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i])).All(v => v)).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("hmi_sdk_method_unavailable");
            return matches[0].Invoke(instance, args);
        }

        private static object Get(Assembly sdk, object instance, string contract, string property)
        {
            var type = sdk.GetType("TcHmiAutomation." + contract, true);
            return type.GetProperty(property).GetValue(instance);
        }

        private static string FindSdk()
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
                foreach (var key in new[] { @"SOFTWARE\Beckhoff\TwinCAT3\3.1", @"SOFTWARE\WOW6432Node\Beckhoff\TwinCAT3\3.1" })
                    using (var entry = hive.OpenSubKey(key))
                    {
                        var root = entry?.GetValue("InstallDir") as string;
                        if (string.IsNullOrWhiteSpace(root)) continue;
                        var path = Path.GetFullPath(Path.Combine(root, "..", "Functions", "TE2000-HMI-Engineering", "MSBuild", "TcHmiAutomation.dll"));
                        if (File.Exists(path)) return path;
                    }
            throw new InvalidOperationException("hmi_sdk_missing");
        }
    }
}
