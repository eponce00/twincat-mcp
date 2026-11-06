#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;
using TwinCAT.Ads;
using TwinCAT.SystemService;

namespace TcAutomation.Core
{
    /// <summary>
    /// One reading of the target's TwinCAT system service (AMS port 10000).
    /// RestartIndex is null when the target only answers a plain ReadState.
    /// </summary>
    public sealed class SystemStateSample
    {
        public AdsState State { get; set; }
        public int? RestartIndex { get; set; }
        public int? Flags { get; set; }
        public override string ToString() =>
            RestartIndex.HasValue ? $"{State} (restart index {RestartIndex})" : State.ToString();
    }

    /// <summary>
    /// Fingerprint of the boot configuration written by an activation on a
    /// target whose boot folder is on this PC. Stamp changes whenever the
    /// configuration files are rewritten; CreateTime is the TcBootProject
    /// CreateTime attribute and is null while CurrentConfig.xml is unreadable
    /// or partially written.
    /// </summary>
    public sealed class BootConfigSample
    {
        public bool Exists { get; set; }
        /// <summary>CurrentConfig.xml write time and length.</summary>
        public string ConfigStamp { get; set; } = "";
        /// <summary>CurrentConfig.xml and the files written with it, for stability checks.</summary>
        public string Stamp { get; set; } = "";
        public DateTime LastWriteUtc { get; set; }
        public string? CreateTime { get; set; }
        public override string ToString() => Exists
            ? $"CurrentConfig.xml written {LastWriteUtc:yyyy-MM-ddTHH:mm:ss.fff}Z, CreateTime {CreateTime ?? "unreadable"}"
            : "CurrentConfig.xml missing";
    }

    /// <summary>Signals the activation waiter can read from a target.</summary>
    public interface IActivationProbe
    {
        /// <summary>Local boot folder of the target, or null for remote targets.</summary>
        string? BootDirectory { get; }
        /// <summary>System service state, or null when port 10000 cannot be read.</summary>
        SystemStateSample? ReadSystemState();
        /// <summary>Boot configuration fingerprint, or null when no local boot folder is known.</summary>
        BootConfigSample? ReadBootConfig();
    }

    public sealed class ActivationWaitOptions
    {
        public const string TimeoutEnvironmentVariable = "TWINCAT_MCP_ACTIVATION_TIMEOUT_SECONDS";

        /// <summary>Maximum time the final activation attempt may take to land.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(120);
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
        /// <summary>Signals must stay unchanged for this long before the activation counts as complete.</summary>
        public TimeSpan SettleTime { get; set; } = TimeSpan.FromSeconds(3);
        /// <summary>Fixed delay used only when no signal can be read at all.</summary>
        public TimeSpan UnverifiedDelay { get; set; } = TimeSpan.FromSeconds(20);
        /// <summary>
        /// Local targets only: when CurrentConfig.xml has not been rewritten at
        /// all within this time, the activation did not reach the target and is
        /// dispatched again (up to MaxAttempts in total).
        /// </summary>
        public TimeSpan RetryAfter { get; set; } = TimeSpan.FromSeconds(30);
        public int MaxAttempts { get; set; } = 2;

        /// <summary>Defaults, with the timeout optionally overridden (10–1800 s) by the environment.</summary>
        public static ActivationWaitOptions FromEnvironment()
        {
            var options = new ActivationWaitOptions();
            var text = Environment.GetEnvironmentVariable(TimeoutEnvironmentVariable);
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
            {
                options.Timeout = TimeSpan.FromSeconds(Math.Max(10, Math.Min(1800, seconds)));
            }
            return options;
        }
    }

    /// <summary>State captured immediately before the first ActivateConfiguration is dispatched.</summary>
    public sealed class ActivationBaseline
    {
        public SystemStateSample? SystemState { get; set; }
        public BootConfigSample? BootConfig { get; set; }
    }

    /// <summary>Receipt describing how activation completion was established.</summary>
    public sealed class ActivationWaitResult
    {
        /// <summary>True when the caller may continue (restart, poll, report success).</summary>
        public bool Completed { get; set; }
        /// <summary>True when the rewritten boot configuration was observed on the target.</summary>
        public bool Verified { get; set; }
        /// <summary>bootConfig (verified), systemState (remote, unverified) or none (nothing readable).</summary>
        public string Signal { get; set; } = "none";
        /// <summary>ActivateConfiguration calls dispatched.</summary>
        public int Attempts { get; set; } = 1;
        public long ElapsedMs { get; set; }
        public string? BootDirectory { get; set; }
        /// <summary>Local targets: whether CurrentConfig.xml differs from the baseline.</summary>
        public bool? BootConfigRewritten { get; set; }
        public string? SystemStateBefore { get; set; }
        public string? SystemStateAfter { get; set; }
        public string? BootConfigBefore { get; set; }
        public string? BootConfigAfter { get; set; }
        public List<string> Observations { get; set; } = new List<string>();
        public string? Warning { get; set; }
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Verifies that ActivateConfiguration reached the target before the caller
    /// restarts TwinCAT. XAE can report a successful activation that never
    /// reached the target, or that is still being written; restarting then
    /// starts the PLC (whose boot files XAE already replaced) against the
    /// previous Boot\CurrentConfig.xml.
    ///
    /// Local targets (boot folder on this PC) are verified: CurrentConfig.xml
    /// must differ from the pre-activation baseline (file stamp or CreateTime),
    /// parse as a complete TcBootProject, and stay unchanged, together with the
    /// files written with it, for the settle time; a readable system service
    /// (port 10000) must also be in a settled state. If CurrentConfig.xml has
    /// not changed at all after RetryAfter, the activation is dispatched once
    /// more (the documented manual recovery). A landed activation does not
    /// change the system service state or restart index, so remote targets
    /// cannot be verified over ADS: the wait requires a settled state and
    /// reports the activation as unverified. With nothing readable it waits a
    /// fixed delay, also unverified.
    /// </summary>
    public static class ActivationWait
    {
        static readonly AdsState[] SettledStates = { AdsState.Config, AdsState.Run, AdsState.Stop };

        public static bool IsSettled(AdsState state) => SettledStates.Contains(state);

        public static ActivationBaseline Capture(IActivationProbe probe) => new ActivationBaseline
        {
            SystemState = SafeRead(probe.ReadSystemState),
            BootConfig = SafeRead(probe.ReadBootConfig),
        };

        /// <summary>Production entry point: probe the real target, activate, verify.</summary>
        public static ActivationWaitResult ActivateAndWait(string? targetNetId, Action activate, Action<string>? progress = null)
        {
            using var probe = new TargetActivationProbe(targetNetId);
            return ActivateAndWait(probe, activate, ActivationWaitOptions.FromEnvironment(), progress);
        }

        public static ActivationWaitResult ActivateAndWait(
            IActivationProbe probe,
            Action activate,
            ActivationWaitOptions options,
            Action<string>? progress = null,
            Func<TimeSpan>? clock = null,
            Action<TimeSpan>? sleep = null)
        {
            var baseline = Capture(probe);
            progress?.Invoke(Describe(probe, baseline));
            var observations = new List<string>();
            long elapsed = 0;
            int attempt = 0;
            ActivationWaitResult result;
            while (true)
            {
                attempt++;
                activate();
                bool canRetry = probe.BootDirectory != null && attempt < options.MaxAttempts;
                result = Wait(probe, baseline, options, progress, clock, sleep, canRetry ? options.RetryAfter : (TimeSpan?)null);
                elapsed += result.ElapsedMs;
                int current = attempt;
                observations.AddRange(result.Observations.Select(o => options.MaxAttempts > 1 ? $"attempt {current}: {o}" : o));
                if (result.Completed || !canRetry || result.BootConfigRewritten != false)
                {
                    break;
                }
                progress?.Invoke($"Boot\\CurrentConfig.xml was not rewritten within {options.RetryAfter.TotalSeconds:0} s; the activation did not reach the target. Activating again (attempt {attempt + 1} of {options.MaxAttempts}).");
            }

            result.Attempts = attempt;
            result.ElapsedMs = elapsed;
            result.Observations = observations;
            if (!result.Completed && attempt > 1)
            {
                result.ErrorMessage = $"{attempt} activation attempts: " + result.ErrorMessage;
            }
            return result;
        }

        public static string Describe(IActivationProbe probe, ActivationBaseline baseline)
        {
            var parts = new List<string>();
            parts.Add(baseline.SystemState != null ? $"system state {baseline.SystemState}" : "system state unreadable");
            parts.Add(probe.BootDirectory != null
                ? $"local boot folder {probe.BootDirectory}, {baseline.BootConfig?.ToString() ?? "boot configuration unreadable"}"
                : "no local boot folder (remote target)");
            return "Before activation: " + string.Join("; ", parts);
        }

        /// <summary>
        /// Waits for one dispatched activation. With retryAfter set, returns
        /// early (not completed, BootConfigRewritten=false) when CurrentConfig.xml
        /// has not changed at all by then.
        /// </summary>
        public static ActivationWaitResult Wait(
            IActivationProbe probe,
            ActivationBaseline baseline,
            ActivationWaitOptions options,
            Action<string>? progress = null,
            Func<TimeSpan>? clock = null,
            Action<TimeSpan>? sleep = null,
            TimeSpan? retryAfter = null)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            clock ??= () => watch.Elapsed;
            sleep ??= d => Thread.Sleep(d);
            var result = new ActivationWaitResult
            {
                BootDirectory = probe.BootDirectory,
                SystemStateBefore = baseline.SystemState?.ToString(),
                BootConfigBefore = baseline.BootConfig?.ToString(),
            };

            bool bootAvailable = probe.BootDirectory != null;
            bool stateEverReadable = baseline.SystemState != null;
            bool rewritten = false;
            bool bootDone = false, stateSettled = false;
            string? lastState = null;
            string? lastBootStamp = null;
            TimeSpan bootStableSince = TimeSpan.Zero, stateStableSince = TimeSpan.Zero;
            SystemStateSample? state = null;
            BootConfigSample? boot = null;
            TimeSpan start = clock();
            TimeSpan nextReport = start + TimeSpan.FromSeconds(10);

            while (true)
            {
                TimeSpan now = clock();
                state = SafeRead(probe.ReadSystemState);
                boot = bootAvailable ? SafeRead(probe.ReadBootConfig) : null;

                string stateKey = state?.ToString() ?? "unreadable";
                if (stateKey != lastState)
                {
                    result.Observations.Add($"+{(now - start).TotalSeconds:0.0}s system state {stateKey}");
                    lastState = stateKey;
                    stateStableSince = now;
                }
                if (state != null) stateEverReadable = true;

                string bootKey = boot == null ? "unreadable" : boot.Exists ? boot.Stamp + "|" + boot.CreateTime : "missing";
                if (bootAvailable && bootKey != lastBootStamp)
                {
                    if (lastBootStamp != null)
                    {
                        result.Observations.Add($"+{(now - start).TotalSeconds:0.0}s {boot?.ToString() ?? "boot configuration unreadable"}");
                    }
                    lastBootStamp = bootKey;
                    bootStableSince = now;
                }
                if (boot != null && BootChanged(baseline.BootConfig, boot)) rewritten = true;

                stateSettled = state != null && IsSettled(state.State) && now - stateStableSince >= options.SettleTime;
                bootDone = boot != null && boot.Exists && boot.CreateTime != null
                    && BootChanged(baseline.BootConfig, boot)
                    && now - bootStableSince >= options.SettleTime;

                if (bootAvailable)
                {
                    if (bootDone && (stateSettled || !stateEverReadable))
                    {
                        result.Completed = true;
                        result.Verified = true;
                        result.Signal = "bootConfig";
                        if (!stateEverReadable)
                        {
                            result.Warning = "System service state (port 10000) was unreadable; completion was verified from the local boot configuration only.";
                        }
                        break;
                    }
                }
                else if (stateEverReadable)
                {
                    if (stateSettled)
                    {
                        result.Completed = true;
                        result.Verified = false;
                        result.Signal = "systemState";
                        result.Warning = $"Remote target: a landed activation is not visible over ADS, so it was not verified. The system service settled in {state}. If the PLC reports a symbol mismatch after the restart, check Boot\\CurrentConfig.xml on the target.";
                        break;
                    }
                }
                else if (now - start >= options.UnverifiedDelay)
                {
                    result.Completed = true;
                    result.Verified = false;
                    result.Signal = "none";
                    result.Warning = $"Activation could not be verified: the target system service (port 10000) was unreadable and no local boot folder is known. Waited a fixed {options.UnverifiedDelay.TotalSeconds:0} s.";
                    break;
                }

                if (bootAvailable && retryAfter.HasValue && !rewritten && now - start >= retryAfter.Value)
                {
                    result.Completed = false;
                    result.ErrorMessage = $"Boot\\CurrentConfig.xml in {probe.BootDirectory} was not rewritten within {retryAfter.Value.TotalSeconds:0} s.";
                    break;
                }

                if (now - start >= options.Timeout)
                {
                    result.Completed = false;
                    var pending = new List<string>();
                    if (bootAvailable && !bootDone)
                    {
                        pending.Add(!rewritten
                            ? $"Boot\\CurrentConfig.xml in {probe.BootDirectory} was not rewritten ({boot?.ToString() ?? "unreadable"})"
                            : $"Boot\\CurrentConfig.xml in {probe.BootDirectory} is still changing or incomplete");
                    }
                    if (stateEverReadable && !stateSettled)
                    {
                        pending.Add($"system service did not settle (last {stateKey})");
                    }
                    result.ErrorMessage = $"Activation did not complete within {options.Timeout.TotalSeconds:0} s: {string.Join("; ", pending)}. TwinCAT was not restarted. The target may already hold the new PLC boot files next to the previous configuration; do not restart it until an activation has landed. Activate again, or raise {ActivationWaitOptions.TimeoutEnvironmentVariable} for slow targets.";
                    break;
                }

                if (now >= nextReport)
                {
                    progress?.Invoke($"Waiting for activation to land ({(now - start).TotalSeconds:0} s; system state {stateKey}{(bootAvailable ? "; " + (boot?.ToString() ?? "boot configuration unreadable") : "")})");
                    nextReport = now + TimeSpan.FromSeconds(10);
                }
                sleep(options.PollInterval);
            }

            result.ElapsedMs = (long)(clock() - start).TotalMilliseconds;
            result.BootConfigRewritten = bootAvailable ? rewritten : (bool?)null;
            result.SystemStateAfter = state?.ToString();
            result.BootConfigAfter = boot?.ToString();
            if (result.Completed)
            {
                progress?.Invoke(result.Verified
                    ? $"Activation landed: {boot} ({result.ElapsedMs / 1000.0:0.0} s)"
                    : $"Activation not verified ({result.Signal}): {result.Warning}");
            }
            else if (!retryAfter.HasValue || rewritten)
            {
                progress?.Invoke(result.ErrorMessage!);
            }
            return result;
        }

        /// <summary>
        /// Whether CurrentConfig.xml itself was rewritten. PLC boot files are
        /// excluded: XAE replaces them even when the activation never lands.
        /// </summary>
        public static bool BootChanged(BootConfigSample? before, BootConfigSample after)
        {
            if (!after.Exists) return false;
            if (before == null || !before.Exists) return true;
            return before.ConfigStamp != after.ConfigStamp || before.CreateTime != after.CreateTime;
        }

        static T? SafeRead<T>(Func<T?> read) where T : class
        {
            try { return read(); } catch { return null; }
        }

        // ---------------------------------------------------------------
        // Boot folder discovery and fingerprinting (local targets only)
        // ---------------------------------------------------------------

        /// <summary>
        /// Boot files rewritten by an activation. CurrentConfig.xml is required;
        /// the others are included in the stability fingerprint when present.
        /// </summary>
        static readonly string[] BootFiles = { "CurrentConfig.xml", "CurrentConfig.tszip", "CurrentProjectInfo.json" };

        public static BootConfigSample ReadBootConfig(string bootDirectory)
        {
            var config = new FileInfo(Path.Combine(bootDirectory, "CurrentConfig.xml"));
            if (!config.Exists) return new BootConfigSample { Exists = false, Stamp = "missing", ConfigStamp = "missing" };

            var parts = new List<string>();
            foreach (var name in BootFiles)
            {
                var file = new FileInfo(Path.Combine(bootDirectory, name));
                if (file.Exists) parts.Add($"{name}:{file.LastWriteTimeUtc.Ticks}:{file.Length}");
            }
            var plc = new DirectoryInfo(Path.Combine(bootDirectory, "Plc"));
            if (plc.Exists)
            {
                foreach (var file in plc.GetFiles("*.app").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                {
                    parts.Add($"Plc/{file.Name}:{file.LastWriteTimeUtc.Ticks}:{file.Length}");
                }
            }

            return new BootConfigSample
            {
                Exists = true,
                ConfigStamp = $"{config.LastWriteTimeUtc.Ticks}:{config.Length}",
                Stamp = string.Join(";", parts),
                LastWriteUtc = config.LastWriteTimeUtc,
                CreateTime = ReadCreateTime(config.FullName),
            };
        }

        /// <summary>
        /// TcBootProject/@CreateTime of a complete CurrentConfig.xml, or null if
        /// the file is locked, partially written or not a boot project.
        /// </summary>
        public static string? ReadCreateTime(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using var reader = XmlReader.Create(stream, settings);
                var root = XDocument.Load(reader).Root;
                if (root == null || root.Name.LocalName != "TcBootProject") return null;
                return root.Attribute("CreateTime")?.Value ?? "";
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Boot folder for a target that runs on this PC: a user-mode runtime
        /// (Runtimes\*\3.1\TcRegistry.xml) or the local XAR (registry). Null for
        /// remote targets or when the folder cannot be identified.
        /// </summary>
        public static string? FindLocalBootDirectory(string? targetNetId, string? runtimesRoot = null, Func<(string? netId, string? bootDir)>? localSystem = null)
        {
            if (string.IsNullOrWhiteSpace(targetNetId)) return null;
            string target = targetNetId!.Trim();

            runtimesRoot ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Beckhoff", "TwinCAT", "3.1", "Runtimes");
            try
            {
                if (Directory.Exists(runtimesRoot))
                {
                    foreach (var registry in Directory.GetFiles(runtimesRoot, "TcRegistry.xml", SearchOption.AllDirectories))
                    {
                        var (netId, bootDir) = ReadRuntimeRegistry(registry);
                        if (netId == target && bootDir != null && Directory.Exists(bootDir)) return bootDir;
                    }
                }
            }
            catch
            {
                // Unreadable runtime folders mean "not identified", never a failure.
            }

            try
            {
                var (localNetId, localBootDir) = (localSystem ?? ReadLocalSystemRegistry)();
                if (localNetId != null && (localNetId == target || target == "127.0.0.1.1.1")
                    && localBootDir != null && Directory.Exists(localBootDir))
                {
                    return localBootDir;
                }
            }
            catch
            {
            }
            return null;
        }

        /// <summary>AmsNetId and BootDir from a user-mode runtime TcRegistry.xml.</summary>
        public static (string? netId, string? bootDir) ReadRuntimeRegistry(string path)
        {
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using var reader = XmlReader.Create(path, settings);
                var doc = XDocument.Load(reader);
                string? Value(string name) => doc.Descendants("Value")
                    .FirstOrDefault(v => string.Equals((string?)v.Attribute("Name"), name, StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
                var netId = FormatNetId(Value("AmsNetId"));
                var bootDir = Value("BootDir");
                return (netId, string.IsNullOrEmpty(bootDir) ? null : Path.GetFullPath(bootDir));
            }
            catch
            {
                return (null, null);
            }
        }

        /// <summary>Hex "C0A8016C0101" (or raw bytes) to "192.168.1.108.1.1".</summary>
        public static string? FormatNetId(string? hex)
        {
            if (hex == null) return null;
            hex = hex.Trim();
            if (hex.Length != 12 || !hex.All(Uri.IsHexDigit)) return null;
            return string.Join(".", Enumerable.Range(0, 6).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16).ToString(CultureInfo.InvariantCulture)));
        }

        static (string? netId, string? bootDir) ReadLocalSystemRegistry()
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var system = hklm.OpenSubKey(@"SOFTWARE\Beckhoff\TwinCAT3\System");
                using var version = hklm.OpenSubKey(@"SOFTWARE\Beckhoff\TwinCAT3\3.1");
                if (system?.GetValue("AmsNetId") is byte[] bytes && bytes.Length == 6)
                {
                    var netId = string.Join(".", bytes.Select(b => b.ToString(CultureInfo.InvariantCulture)));
                    var bootDir = version?.GetValue("BootDir") as string;
                    return (netId, string.IsNullOrEmpty(bootDir) ? null : Path.GetFullPath(bootDir));
                }
            }
            return (null, null);
        }
    }

    /// <summary>Reads the real target: ADS port 10000 and, if local, its boot folder.</summary>
    public sealed class TargetActivationProbe : IActivationProbe, IDisposable
    {
        readonly string? _netId;
        AdsClient? _client;

        public TargetActivationProbe(string? targetNetId)
        {
            _netId = string.IsNullOrWhiteSpace(targetNetId) ? null : targetNetId!.Trim();
            BootDirectory = ActivationWait.FindLocalBootDirectory(_netId);
        }

        public string? BootDirectory { get; }

        public BootConfigSample? ReadBootConfig() =>
            BootDirectory == null ? null : ActivationWait.ReadBootConfig(BootDirectory);

        public SystemStateSample? ReadSystemState()
        {
            if (_netId == null) return null;
            try
            {
                if (_client == null)
                {
                    _client = new AdsClient { Timeout = 2000 };
                    _client.Connect(_netId, (int)AmsPort.SystemService);
                }
                var extended = _client.ReadSysServState();
                if (extended.ErrorCode == AdsErrorCode.NoError)
                {
                    return new SystemStateSample
                    {
                        State = extended.Value.AdsState,
                        RestartIndex = extended.Value.RestartIndex,
                        Flags = (int)extended.Value.Flags,
                    };
                }
                return new SystemStateSample { State = _client.ReadState().AdsState };
            }
            catch
            {
                // The system service may drop connections while it reconfigures.
                Dispose();
                return null;
            }
        }

        public void Dispose()
        {
            try { _client?.Dispose(); } catch { }
            _client = null;
        }
    }
}
