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
                var started = Stopwatch.StartNew();
                HmiMappings.Plan plan = null;
                List<object> missing = null;
                if (command == "hmi-refresh-mappings")
                {
                    // ADS checks come first: an unreachable PLC must stop this before the HMI API is touched.
                    stage = "hmi_mapping_prepare";
                    var root = Path.GetDirectoryName(projectPath);
                    using (var config = HmiMappings.Read(HmiMappings.ConfigPath(root)))
                        plan = HmiMappings.Select(root, config.RootElement, Requested(args));
                    stage = "hmi_mapping_ads";
                    missing = CheckTargets(plan, started, timeout);
                }
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
                if (command == "hmi-refresh-mappings")
                {
                    stage = "hmi_mapping_server";
                    return Refresh(assembly, project, projectPath, plan, missing, sdkVersion, started, timeout);
                }
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

        private static List<string> Requested(JsonElement args)
        {
            if (!args.TryGetProperty("symbols", out var list) || list.ValueKind == JsonValueKind.Null) return null;
            if (list.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("hmi_mapping_arguments");
            var names = list.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null).ToList();
            if (names.Count == 0 || names.Any(string.IsNullOrEmpty) || names.Distinct(StringComparer.Ordinal).Count() != names.Count)
                throw new InvalidOperationException("hmi_mapping_arguments");
            return names;
        }

        /// <summary>Removes confirmed-missing targets from the plan. Reads symbol metadata only, never values.</summary>
        private static List<object> CheckTargets(HmiMappings.Plan plan, Stopwatch started, int timeout)
        {
            var missing = new List<object>();
            foreach (var group in plan.Eligible.GroupBy(m => m.Runtime).ToList())
            {
                var rt = plan.Runtimes[group.Key];
                using var ads = new TwinCAT.Ads.AdsClient();
                ads.Timeout = 2500;
                try
                {
                    ads.Connect(rt.NetId, rt.Port);
                    var state = ads.ReadState().AdsState;
                    if (state != TwinCAT.Ads.AdsState.Run && state != TwinCAT.Ads.AdsState.Stop)
                        throw new InvalidOperationException("hmi_mapping_runtime_unavailable");
                }
                catch (TwinCAT.Ads.AdsErrorException) { throw new InvalidOperationException("hmi_mapping_runtime_unavailable"); }
                foreach (var m in group)
                {
                    if (started.Elapsed.TotalSeconds >= timeout) throw new InvalidOperationException("hmi_mapping_deadline");
                    try { ads.ReadSymbol(m.AdsPath); }
                    catch (TwinCAT.Ads.AdsErrorException ex) when (ex.ErrorCode == TwinCAT.Ads.AdsErrorCode.DeviceSymbolNotFound)
                    {
                        plan.Eligible.Remove(m);
                        missing.Add(new { name = m.Name, reason = "target_missing" });
                    }
                    catch (TwinCAT.Ads.AdsErrorException) { throw new InvalidOperationException("hmi_mapping_target_unverified"); }
                }
            }
            return missing;
        }

        /// <summary>
        /// The designer's refresh is unmap then auto-map. The SDK sends them as two requests, so a failed
        /// map restores the original entry. Every outcome is read back from the persisted configuration.
        /// </summary>
        private static object Refresh(Assembly sdk, object project, string projectPath, HmiMappings.Plan plan,
            List<object> missing, string sdkVersion, Stopwatch started, int timeout)
        {
            var configPath = HmiMappings.ConfigPath(Path.GetDirectoryName(projectPath));
            var skipped = plan.Skipped.Select(s => (object)new { name = s.Key, reason = s.Value }).Concat(missing).ToList();
            if (plan.Eligible.Count == 0)
                return new { Success = false, code = "hmi_mapping_nothing_selected", outcomeUnknown = false, skipped,
                    message = "No mapping was eligible for refresh. Nothing was changed." };
            if (!Resolves(sdk, "UnMapSymbol", typeof(string), typeof(string), typeof(bool))
                || !Resolves(sdk, "MapSymbol", typeof(string), typeof(string), typeof(string))
                || !Resolves(sdk, "WriteSymbolJson", typeof(string), typeof(string)))
                return Failure("hmi_sdk_method_unavailable");
            object server = Call(sdk, project, "ITcHmiProject", "GetServerInterface");
            var domains = new HashSet<string>(plan.Eligible.Select(m => m.Domain), StringComparer.Ordinal);
            foreach (object ext in (Array)Call(sdk, server, "ITcHmiServer", "GetServerExtensions", false))
                if (domains.Remove((string)Get(sdk, ext, "ITcHmiServerExtension", "DomainName"))
                    && (!(bool)Get(sdk, ext, "ITcHmiServerExtension", "Loaded") || (string)Get(sdk, ext, "ITcHmiServerExtension", "State") != "Initialized"))
                    return Failure("hmi_server_not_ready");
            if (domains.Count != 0) return Failure("hmi_server_not_ready");

            using var before = ReadConfig(configPath) ?? throw new InvalidOperationException("hmi_mapping_config_invalid");
            // A duplicated name would fail the final check after the work was done: refuse it first.
            foreach (var key in new[] { "SYMBOLS", "USERGROUPS", "DEFINITIONS" })
                if (HmiMappings.Changed(before.RootElement, before.RootElement, key).Count != 0) return Failure("hmi_mapping_config_duplicate");
            foreach (var m in plan.Eligible)
            {
                var entry = HmiMappings.Entry(before.RootElement, m.Name);
                if (entry == null || !HmiMappings.Same(entry, m.Entry)) return Failure("hmi_mapping_changed");
            }

            // Each touched symbol's expected persisted entry; the final check compares the whole file against it.
            var expected = new Dictionary<string, string>(StringComparer.Ordinal);
            var refreshed = new List<object>();
            string current = null, code = null;
            bool dispatched = false, unknown = false;
            bool? restored = null;
            // Definitions as they stood after the last verified remap: a stop must leave them so.
            JsonDocument settled = null;
            try
            {
                foreach (var m in plan.Eligible)
                {
                    if (started.Elapsed.TotalSeconds >= timeout) { code = "hmi_mapping_deadline"; break; }
                    current = m.Name;
                    var path = "TcHmiSrv.Config::SYMBOLS::" + m.Name;
                    expected[m.Name] = m.Entry;
                    dispatched = true;
                    // Never ask the server to remove historized data.
                    if (!(bool)Call(sdk, server, "ITcHmiServer", "UnMapSymbol", m.Name, m.Domain, false))
                    { code = "hmi_unmap_failed"; unknown = !Persisted(configPath, m.Name, m.Entry); break; }
                    if (!Absent(configPath, m.Name)) { code = "hmi_unmap_unconfirmed"; unknown = true; break; }
                    bool mapped = (bool)Call(sdk, server, "ITcHmiServer", "MapSymbol", m.Name, m.Target, m.Domain);
                    var after = mapped ? WaitEntry(configPath, m.Name) : null;
                    if (after == null)
                    {
                        restored = (bool)Call(sdk, server, "ITcHmiServer", "WriteSymbolJson", path, m.Entry)
                            && Persisted(configPath, m.Name, m.Entry);
                        code = "hmi_map_failed";
                        unknown = restored != true;
                        break;
                    }
                    // The remap must bind the planned target; anything else is not this refresh's result.
                    if (!HmiMappings.SameTarget(after, m.Domain, m.Target)) { code = "hmi_mapping_target_changed"; unknown = true; break; }
                    var merged = HmiMappings.Merge(m.Entry, after);
                    expected[m.Name] = merged;
                    if (!HmiMappings.Same(merged, after)
                        && !((bool)Call(sdk, server, "ITcHmiServer", "WriteSymbolJson", path, merged) && Persisted(configPath, m.Name, merged)))
                    { code = "hmi_mapping_preserve_failed"; unknown = true; break; }
                    refreshed.Add(new { name = m.Name, schemaChanged = !SameSchema(m.Entry, merged) });
                    // An unreadable snapshot keeps the older baseline: the stop check only gets stricter.
                    var next = ReadConfig(configPath);
                    if (next != null) { settled?.Dispose(); settled = next; }
                }
                if (code == null || code == "hmi_mapping_deadline") current = null;

                // Known exits are checked against the whole file: only touched symbols may differ, and only as expected.
                using var final = ReadConfig(configPath);
                if (final == null)
                    return new { Success = false, code = "hmi_mapping_unverified", outcomeUnknown = dispatched, symbol = current, refreshed, skipped,
                        message = "The configuration could not be read back after the refresh." };
                var drifted = expected.Where(e => !(HmiMappings.Entry(final.RootElement, e.Key) is string s) || !HmiMappings.Same(s, e.Value))
                    .Select(e => e.Key).ToList();
                var unexpected = HmiMappings.Changed(before.RootElement, final.RootElement, "SYMBOLS").Where(n => !expected.ContainsKey(n))
                    .Concat(drifted).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
                // A touched entry that no longer matches what was verified means an earlier verdict no longer holds.
                if (drifted.Count != 0) unknown = true;
                if (restored == true && drifted.Contains(current)) restored = false;
                // A present section that is not an object hides its members. An absent one may appear or go with a remap.
                bool sectionsChanged = new[] { "SYMBOLS", "USERGROUPS", "DEFINITIONS" }.Any(k =>
                    HmiMappings.Kind(final.RootElement, k) is var kind && kind != JsonValueKind.Object && kind != JsonValueKind.Undefined);
                bool groupsChanged = HmiMappings.Changed(before.RootElement, final.RootElement, "USERGROUPS").Count != 0;
                var definitions = HmiMappings.Changed(before.RootElement, final.RootElement, "DEFINITIONS").Count;
                // A remap may regenerate definitions. A stopped symbol must not change them further.
                // A duplicated name in the result is ambiguous whether or not the run stopped.
                bool definitionsUnexpected = HmiMappings.Changed(final.RootElement, final.RootElement, "DEFINITIONS").Count != 0
                    || code != null && HmiMappings.Changed((settled ?? before).RootElement, final.RootElement, "DEFINITIONS").Count != 0;
                if (unknown || code != null || unexpected.Count != 0 || groupsChanged || definitionsUnexpected || sectionsChanged)
                    return new { Success = false, code = code ?? "hmi_mapping_unexpected_change", outcomeUnknown = unknown, symbol = current, restored,
                        unexpectedSymbols = unexpected, groupsChanged, definitionsUnexpected, sectionsChanged, definitionsChanged = definitions, refreshed, skipped,
                        message = code == "hmi_mapping_deadline"
                            ? "Deadline reached before the next mapping. Completed mappings are listed; later ones were not changed."
                            : "Mapping refresh stopped. Later mappings were not changed. No retry was attempted." };
                return new { Success = true, projectPath, sdkVersion, refreshed, skipped, definitionsChanged = definitions,
                    message = "Mappings refreshed. Run hmi.build and hmi.preflight again before publishing." };
            }
            catch (Exception ex)
            {
                // After an unmap, a failed call or check leaves the state unknown. Never restore over a call that may still finish.
                Console.Error.WriteLine($"[DEBUG] HMI mapping refresh: {ex.GetType().Name}, HRESULT {ex.HResult}");
                return new { Success = false, code = "hmi_mapping_interrupted", outcomeUnknown = dispatched, symbol = current, refreshed, skipped,
                    message = "Mapping refresh was interrupted. Inspect the named mapping before any other HMI operation." };
            }
            finally { settled?.Dispose(); }
        }

        private static bool Resolves(Assembly sdk, string method, params Type[] types) =>
            sdk.GetType("TcHmiAutomation.ITcHmiServer", true).GetMethods().Count(m => m.Name == method
                && m.GetParameters().Length == types.Length
                && m.GetParameters().Select((p, i) => p.ParameterType.IsAssignableFrom(types[i])).All(v => v)) == 1;

        private static bool SameSchema(string a, string b)
        {
            using var x = JsonDocument.Parse(a);
            using var y = JsonDocument.Parse(b);
            bool hx = x.RootElement.TryGetProperty("SCHEMA", out var sx), hy = y.RootElement.TryGetProperty("SCHEMA", out var sy);
            return hx == hy && (!hx || HmiMappings.Same(sx, sy));
        }

        private static string WaitEntry(string configPath, string name) => Poll(configPath, root => HmiMappings.Entry(root, name));

        private static bool Persisted(string configPath, string name, string expected) =>
            Poll(configPath, root => HmiMappings.Entry(root, name) is string e && HmiMappings.Same(e, expected) ? e : null) != null;

        // Absent means no occurrence: a duplicated name is not gone.
        private static bool Absent(string configPath, string name) =>
            Poll(configPath, root => HmiMappings.Occurrences(root, name) == 0 ? "" : null) != null;

        // Measured: the server persists each change before its call returns. The 2 s poll only absorbs a concurrent rewrite.
        private static string Poll(string configPath, Func<JsonElement, string> probe)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                using (var doc = ReadConfig(configPath, 0))
                    if (doc != null && probe(doc.RootElement) is string found) return found;
                if (clock.Elapsed.TotalSeconds >= 2) return null;
                Thread.Sleep(100);
            }
        }

        // The server rewrites the file in place; a read that races it is retried, then reported as null.
        private static JsonDocument ReadConfig(string configPath, double seconds = 2)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                try { return HmiMappings.Read(configPath); }
                catch (Exception ex) when (ex is InvalidOperationException || ex is IOException) { }
                if (clock.Elapsed.TotalSeconds >= seconds) return null;
                Thread.Sleep(100);
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
