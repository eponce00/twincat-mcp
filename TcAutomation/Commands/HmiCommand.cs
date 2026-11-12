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
                bool recover = command == "hmi-refresh-mappings" && StepDispatcher.GetBool(args, "recover", true) == true;
                if (recover && Requested(args) != null) return Failure("hmi_mapping_arguments");
                // One refresh or recovery per configuration across workers, from the pending check until the record settles.
                using var gate = command == "hmi-refresh-mappings"
                    ? HmiRecovery.Lock(SessionFile.SessionDir, HmiMappings.ConfigPath(Path.GetDirectoryName(projectPath)))
                        ?? throw new InvalidOperationException("hmi_mapping_busy")
                    : null;
                if (command == "hmi-refresh-mappings" && !recover)
                {
                    // A mapping a stopped refresh left unmapped must be recovered first; a new refresh would overwrite its record.
                    var configFile = HmiMappings.ConfigPath(Path.GetDirectoryName(projectPath));
                    var recordPath = HmiRecovery.PathFor(SessionFile.SessionDir, configFile);
                    HmiRecovery.Record pending;
                    try { pending = HmiRecovery.Read(SessionFile.SessionDir, configFile); }
                    catch (InvalidOperationException)
                    {
                        return new { Success = false, code = "hmi_recovery_unreadable", outcomeUnknown = false, recordPath,
                            message = "A recovery record of this configuration cannot be read. Inspect it, resolve its mapping by hand, then delete it." };
                    }
                    string first = null;
                    if (pending != null)
                    {
                        if (HmiRecovery.OwnerRunning(pending))
                            return new { Success = false, code = "hmi_recovery_owner_running", outcomeUnknown = false, symbol = pending.Symbol, recordPath,
                                message = "The worker that wrote this recovery record is still running. Close its context first." };
                        string now;
                        string[] needed;
                        using (var config = HmiMappings.Read(configFile))
                        {
                            now = HmiMappings.Entry(config.RootElement, pending.Symbol);
                            needed = HmiRecovery.MissingDefinitions(pending.OriginalEntry, config.RootElement);
                        }
                        var requested = Requested(args);
                        if (now == null || !HmiMappings.Same(now, pending.OriginalEntry))
                            return new { Success = false, code = "hmi_mapping_recovery_pending", outcomeUnknown = false, symbol = pending.Symbol, recordPath,
                                message = "A stopped refresh left this mapping unsettled. Run hmi.refresh_mappings with recover true first." };
                        // Back exactly as it was. With its definitions present the record is done. Otherwise this refresh
                        // must remap that symbol first; its own record replaces this one and settles with it.
                        if (needed.Length == 0) HmiRecovery.Clear(SessionFile.SessionDir, configFile);
                        else if (requested == null || requested.Contains(pending.Symbol, StringComparer.Ordinal)) first = pending.Symbol;
                        else
                            return new { Success = false, code = "hmi_mapping_recovery_pending", outcomeUnknown = false, symbol = pending.Symbol, recordPath,
                                missingDefinitions = needed, message = "Include this mapping in symbols, so that the refresh regenerates its definitions." };
                    }
                    // ADS checks come first: an unreachable PLC must stop this before the HMI API is touched.
                    stage = "hmi_mapping_prepare";
                    var root = Path.GetDirectoryName(projectPath);
                    using (var config = HmiMappings.Read(HmiMappings.ConfigPath(root)))
                        plan = HmiMappings.Select(root, config.RootElement, Requested(args));
                    stage = "hmi_mapping_ads";
                    missing = CheckTargets(plan, started, timeout);
                    if (first != null)
                    {
                        var head = plan.Eligible.FirstOrDefault(m => m.Name == first);
                        if (head == null)
                            return new { Success = false, code = "hmi_mapping_recovery_pending", outcomeUnknown = false, symbol = first, recordPath, skipped = missing,
                                message = "The mapping with missing definitions cannot be refreshed now. Nothing was changed." };
                        plan.Eligible.Remove(head);
                        plan.Eligible.Insert(0, head);
                    }
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
                    return recover
                        ? Recover(assembly, project, projectPath, sdkVersion)
                        : Refresh(assembly, project, projectPath, plan, missing, sdkVersion, started, timeout);
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
            if (!ServerReady(sdk, server, plan.Eligible.Select(m => m.Domain))) return Failure("hmi_server_not_ready");

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
                    // No unmap without a durable copy of the entry: it is the only way back if the shell or worker dies.
                    try
                    {
                        HmiRecovery.Write(SessionFile.SessionDir, new HmiRecovery.Record
                        { ProjectPath = projectPath, ConfigPath = configPath, Symbol = m.Name, OriginalEntry = m.Entry, CreatedUtc = DateTime.UtcNow });
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    { code = "hmi_mapping_recovery_unavailable"; break; }
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
                if (code == null || code == "hmi_mapping_deadline" || code == "hmi_mapping_recovery_unavailable") current = null;

                // Known exits are checked against the whole file: only touched symbols may differ, and only as expected.
                using var final = ReadConfig(configPath);
                if (final == null)
                    return new { Success = false, code = "hmi_mapping_unverified", outcomeUnknown = dispatched, symbol = current, refreshed, skipped,
                        recovery = Settle(configPath, expected, allowClear: !dispatched), message = "The configuration could not be read back after the refresh." };
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
                        // An uncertain outcome (an unmap not confirmed, a drifted entry) keeps the record.
                        recovery = Settle(configPath, expected, allowClear: !unknown),
                        message = code == "hmi_mapping_deadline"
                            ? "Deadline reached before the next mapping. Completed mappings are listed; later ones were not changed."
                            : "Mapping refresh stopped. Later mappings were not changed. No retry was attempted." };
                var recovery = Settle(configPath, expected);
                if (recovery != null)
                    return new { Success = false, code = "hmi_mapping_recovery_unsettled", outcomeUnknown = true, symbol = current, refreshed, skipped,
                        recovery, definitionsChanged = definitions, message = "Mappings refreshed, but the recovery record could not be cleared." };
                return new { Success = true, projectPath, sdkVersion, refreshed, skipped, definitionsChanged = definitions,
                    message = "Mappings refreshed. Run hmi.build and hmi.preflight again before publishing." };
            }
            catch (Exception ex)
            {
                // After an unmap, a failed call or check leaves the state unknown. Never restore over a call that may still finish.
                Console.Error.WriteLine($"[DEBUG] HMI mapping refresh: {ex.GetType().Name}, HRESULT {ex.HResult}");
                return new { Success = false, code = "hmi_mapping_interrupted", outcomeUnknown = dispatched, symbol = current, refreshed, skipped,
                    // A call may still be running in the server: never clear the record here.
                    recovery = Settle(configPath, expected, allowClear: false),
                    message = "Mapping refresh was interrupted. If recovery names a mapping, run hmi.refresh_mappings with recover true in a new context." };
            }
            finally { settled?.Dispose(); }
        }

        /// <summary>
        /// Clears the recovery record once its mapping is present as verified or as it was. Otherwise returns
        /// the record for the result, so a person or recover mode can restore it. Never writes the configuration.
        /// </summary>
        private static object Settle(string configPath, Dictionary<string, string> expected, bool allowClear = true)
        {
            try
            {
                var record = HmiRecovery.Read(SessionFile.SessionDir, configPath);
                if (record == null) return null;
                using var config = ReadConfig(configPath);
                var entry = config == null ? null : HmiMappings.Entry(config.RootElement, record.Symbol);
                if (HmiRecovery.CanClear(record.OriginalEntry, entry, expected.TryGetValue(record.Symbol, out var verified) ? verified : null, allowClear))
                {
                    HmiRecovery.Clear(SessionFile.SessionDir, configPath);
                    return null;
                }
                using var original = JsonDocument.Parse(record.OriginalEntry);
                return new { symbol = record.Symbol, present = entry != null, originalEntry = original.RootElement.Clone(),
                    recordPath = HmiRecovery.PathFor(SessionFile.SessionDir, configPath) };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DEBUG] HMI recovery record: {ex.GetType().Name}");
                return new { unreadable = true };
            }
        }

        /// <summary>
        /// Settles the record a stopped refresh left. An absent mapping gets its original entry back. A mapping
        /// bound to the original target (a remap that finished) gets its preserved fields back. An identical
        /// entry needs no write. Any other entry is never overwritten. Every branch verifies the persisted
        /// result and the referenced definitions before the record is cleared.
        /// </summary>
        private static object Recover(Assembly sdk, object project, string projectPath, string sdkVersion)
        {
            var configPath = HmiMappings.ConfigPath(Path.GetDirectoryName(projectPath));
            var recordPath = HmiRecovery.PathFor(SessionFile.SessionDir, configPath);
            HmiRecovery.Record record;
            try { record = HmiRecovery.Read(SessionFile.SessionDir, configPath); }
            catch (InvalidOperationException)
            {
                return new { Success = false, code = "hmi_recovery_unreadable", outcomeUnknown = false, recordPath,
                    message = "The recovery record cannot be read. Inspect it, resolve its mapping by hand, then delete it." };
            }
            if (record == null)
                return new { Success = false, code = "hmi_recovery_none", outcomeUnknown = false,
                    message = "No mapping of this project is waiting for recovery. Nothing was changed." };
            if (HmiRecovery.OwnerRunning(record))
                return new { Success = false, code = "hmi_recovery_owner_running", outcomeUnknown = false, symbol = record.Symbol, recordPath,
                    message = "The worker that wrote this recovery record is still running. Close its context first." };
            if (!string.Equals(Path.GetFullPath(record.ProjectPath), projectPath, StringComparison.OrdinalIgnoreCase))
                return new { Success = false, code = "hmi_recovery_unreadable", outcomeUnknown = false, recordPath,
                    message = "The recovery record names another project. Nothing was changed." };
            if (!Resolves(sdk, "WriteSymbolJson", typeof(string), typeof(string))) return Failure("hmi_sdk_method_unavailable");
            string domain;
            using (var parsed = JsonDocument.Parse(record.OriginalEntry))
                domain = parsed.RootElement.TryGetProperty("DOMAIN", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            object server = Call(sdk, project, "ITcHmiProject", "GetServerInterface");
            if (domain == null || !ServerReady(sdk, server, new[] { domain })) return Failure("hmi_server_not_ready");

            using var before = ReadConfig(configPath) ?? throw new InvalidOperationException("hmi_mapping_config_invalid");
            using var original = JsonDocument.Parse(record.OriginalEntry);
            var entry = original.RootElement.Clone();
            var current = HmiMappings.Entry(before.RootElement, record.Symbol);
            var verdict = HmiMappings.Occurrences(before.RootElement, record.Symbol) > 1
                ? HmiRecovery.Verdict.Conflict : HmiRecovery.Decide(record.OriginalEntry, current);
            if (verdict == HmiRecovery.Verdict.Conflict)
                return new { Success = false, code = "hmi_recovery_conflict", outcomeUnknown = false, symbol = record.Symbol, originalEntry = entry, recordPath,
                    message = "The mapping exists with another target or more than once. Nothing was changed; the recovery record is kept. "
                        + "Resolve the mapping by hand, then delete the record file." };
            var target = verdict == HmiRecovery.Verdict.Restore ? record.OriginalEntry
                : verdict == HmiRecovery.Verdict.Reconcile ? HmiMappings.Merge(record.OriginalEntry, current) : current;

            bool written = true;
            if (!HmiMappings.Same(target, current ?? "null"))
            {
                try
                {
                    written = (bool)Call(sdk, server, "ITcHmiServer", "WriteSymbolJson", "TcHmiSrv.Config::SYMBOLS::" + record.Symbol, target)
                        && Persisted(configPath, record.Symbol, target);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[DEBUG] HMI mapping recovery: {ex.GetType().Name}, HRESULT {ex.HResult}");
                    return new { Success = false, code = "hmi_recovery_interrupted", outcomeUnknown = true, symbol = record.Symbol, originalEntry = entry, recordPath,
                        message = "The write was interrupted. The recovery record is kept." };
                }
            }
            using var after = ReadConfig(configPath);
            // The symbol itself must be there once, as written: a concurrent change must not pass as recovered.
            bool confirmed = written && after != null && HmiMappings.Occurrences(after.RootElement, record.Symbol) == 1
                && HmiMappings.Entry(after.RootElement, record.Symbol) is string now && HmiMappings.Same(now, target);
            if (!confirmed)
                return new { Success = false, code = "hmi_recovery_failed", outcomeUnknown = true, symbol = record.Symbol, originalEntry = entry, recordPath,
                    message = "The entry could not be confirmed. The recovery record is kept." };
            var others = HmiMappings.Changed(before.RootElement, after.RootElement, "SYMBOLS").Where(n => n != record.Symbol).ToList();
            bool groupsChanged = HmiMappings.Changed(before.RootElement, after.RootElement, "USERGROUPS").Count != 0;
            var missingDefinitions = HmiRecovery.MissingDefinitions(target, after.RootElement);
            if (others.Count != 0 || groupsChanged || missingDefinitions.Length != 0)
                return new { Success = false, code = missingDefinitions.Length != 0 ? "hmi_recovery_definitions_missing" : "hmi_mapping_unexpected_change",
                    outcomeUnknown = others.Count != 0 || groupsChanged, symbol = record.Symbol, unexpectedSymbols = others, groupsChanged, missingDefinitions, recordPath,
                    message = "The mapping is in place, but the result is incomplete. The recovery record is kept." };
            HmiRecovery.Clear(SessionFile.SessionDir, configPath);
            return new { Success = true, projectPath, sdkVersion, symbol = record.Symbol,
                outcome = verdict == HmiRecovery.Verdict.Restore ? "restored" : verdict == HmiRecovery.Verdict.Reconcile ? "reconciled" : "alreadyPresent",
                message = "The mapping is in place and the recovery record is cleared. Refresh it again so that its schema is current, "
                    + "then run hmi.build and hmi.preflight before publishing." };
        }

        private static bool ServerReady(Assembly sdk, object server, IEnumerable<string> wanted)
        {
            var domains = new HashSet<string>(wanted, StringComparer.Ordinal);
            foreach (object ext in (Array)Call(sdk, server, "ITcHmiServer", "GetServerExtensions", false))
                if (domains.Remove((string)Get(sdk, ext, "ITcHmiServerExtension", "DomainName"))
                    && (!(bool)Get(sdk, ext, "ITcHmiServerExtension", "Loaded") || (string)Get(sdk, ext, "ITcHmiServerExtension", "State") != "Initialized"))
                    return false;
            return domains.Count == 0;
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
