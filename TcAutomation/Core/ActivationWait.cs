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

        /// <summary>Maximum time to wait for the activation to land.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(180);
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
        /// <summary>Signals must stay unchanged for this long before the activation counts as complete.</summary>
        public TimeSpan SettleTime { get; set; } = TimeSpan.FromSeconds(3);
        /// <summary>Fixed delay used only when no completion signal can be read at all.</summary>
        public TimeSpan UnverifiedDelay { get; set; } = TimeSpan.FromSeconds(20);

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

    /// <summary>State captured immediately before ActivateConfiguration is dispatched.</summary>
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
        /// <summary>True when completion was observed on the target rather than assumed after a fixed delay.</summary>
        public bool Verified { get; set; }
        /// <summary>bootConfig+systemState, bootConfig, systemState or none.</summary>
        public string Signal { get; set; } = "none";
        public long ElapsedMs { get; set; }
        public string? BootDirectory { get; set; }
        public string? SystemStateBefore { get; set; }
        public string? SystemStateAfter { get; set; }
        public string? BootConfigBefore { get; set; }
        public string? BootConfigAfter { get; set; }
        public List<string> Observations { get; set; } = new List<string>();
        public string? Warning { get; set; }
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Waits for ActivateConfiguration to land on the target before the caller
    /// restarts TwinCAT. The XAE call can return while the target is still
    /// switching to Config mode and writing its boot configuration; restarting
    /// in that window starts the PLC against the previous configuration.
    ///
    /// Completion requires every readable signal to agree:
    /// - local targets: Boot\CurrentConfig.xml (and the files written with it)
    ///   differ from the baseline, parse as a complete TcBootProject, and stay
    ///   unchanged for the settle time;
    /// - all targets: the system service (port 10000) left its baseline
    ///   (restart index or state changed) and has stayed in a settled state
    ///   (Config, Run or Stop) for the settle time.
    /// When neither signal is readable, the waiter falls back to a fixed delay
    /// and reports the activation as unverified.
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

        /// <summary>Production entry point: build the probe, capture, activate, wait.</summary>
        public static ActivationWaitResult ActivateAndWait(string? targetNetId, Action activate, Action<string>? progress = null)
        {
            using var probe = new TargetActivationProbe(targetNetId);
            var baseline = Capture(probe);
            progress?.Invoke(Describe(probe, baseline));
            activate();
            return Wait(probe, baseline, ActivationWaitOptions.FromEnvironment(), progress);
        }

        public static string Describe(IActivationProbe probe, ActivationBaseline baseline)
        {
            var parts = new List<string>();
            parts.Add(baseline.SystemState != null ? $"system state {baseline.SystemState}" : "system state unreadable");
            parts.Add(probe.BootDirectory != null ? $"local boot folder {probe.BootDirectory}" : "no local boot folder (remote target)");
            return "Before activation: " + string.Join("; ", parts);
        }

        public static ActivationWaitResult Wait(
            IActivationProbe probe,
            ActivationBaseline baseline,
            ActivationWaitOptions options,
            Action<string>? progress = null,
            Func<TimeSpan>? clock = null,
            Action<TimeSpan>? sleep = null)
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
            bool stateTransitionSeen = false;
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
                if (state != null)
                {
                    stateEverReadable = true;
                    if (baseline.SystemState == null
                        || state.State != baseline.SystemState.State
                        || state.RestartIndex != baseline.SystemState.RestartIndex
                        || !IsSettled(state.State))
                    {
                        stateTransitionSeen = true;
                    }
                }

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

                bool bootDone = !bootAvailable || (boot != null && boot.Exists && boot.CreateTime != null
                    && BootChanged(baseline.BootConfig, boot)
                    && now - bootStableSince >= options.SettleTime);
                bool stateDone = !stateEverReadable || (state != null && stateTransitionSeen && IsSettled(state.State)
                    && now - stateStableSince >= options.SettleTime);

                if (!bootAvailable && !stateEverReadable)
                {
                    if (now - start >= options.UnverifiedDelay)
                    {
                        result.Completed = true;
                        result.Verified = false;
                        result.Signal = "none";
                        result.Warning = $"Activation completion could not be verified: the target system service (port 10000) was unreadable and no local boot folder is known. Waited a fixed {options.UnverifiedDelay.TotalSeconds:0} s.";
                        break;
                    }
                }
                else if (bootDone && stateDone)
                {
                    result.Completed = true;
                    result.Verified = true;
                    result.Signal = bootAvailable && stateEverReadable ? "bootConfig+systemState"
                        : bootAvailable ? "bootConfig" : "systemState";
                    if (!stateEverReadable)
                    {
                        result.Warning = "System service state (port 10000) was unreadable; completion was verified from the local boot configuration only.";
                    }
                    break;
                }

                if (now - start >= options.Timeout)
                {
                    result.Completed = false;
                    var pending = new List<string>();
                    if (bootAvailable && !bootDone)
                    {
                        pending.Add(boot == null || !boot.Exists || !BootChanged(baseline.BootConfig, boot)
                            ? $"Boot\\CurrentConfig.xml in {probe.BootDirectory} was not rewritten ({boot?.ToString() ?? "unreadable"})"
                            : $"Boot\\CurrentConfig.xml in {probe.BootDirectory} is still changing or incomplete");
                    }
                    if (stateEverReadable && !stateDone)
                    {
                        pending.Add(!stateTransitionSeen
                            ? $"system service never left {baseline.SystemState} after activation"
                            : $"system service did not settle (last {stateKey})");
                    }
                    result.ErrorMessage = $"Activation did not complete within {options.Timeout.TotalSeconds:0} s: {string.Join("; ", pending)}. TwinCAT was not restarted. Check the target and retry; raise {ActivationWaitOptions.TimeoutEnvironmentVariable} for slow targets.";
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
            result.SystemStateAfter = state?.ToString();
            result.BootConfigAfter = boot?.ToString();
            progress?.Invoke(result.Completed
                ? $"Activation landed after {result.ElapsedMs / 1000.0:0.0} s (signal: {result.Signal}{(result.Verified ? "" : ", unverified")})"
                : result.ErrorMessage!);
            return result;
        }

        public static bool BootChanged(BootConfigSample? before, BootConfigSample after)
        {
            if (!after.Exists) return false;
            if (before == null || !before.Exists) return true;
            return before.Stamp != after.Stamp || before.CreateTime != after.CreateTime;
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
            if (!config.Exists) return new BootConfigSample { Exists = false, Stamp = "missing" };

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
