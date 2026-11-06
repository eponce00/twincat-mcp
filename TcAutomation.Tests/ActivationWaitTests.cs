#nullable enable
using System;
using System.IO;
using System.Linq;
using TcAutomation.Core;
using TwinCAT.Ads;

/// <summary>
/// Activation completion wait. Scripted probes and a fake clock replace the
/// target; no ADS connection, XAE or PLC is used.
/// </summary>
static class ActivationWaitTests
{
    static int cases;
    static void Check(bool value, string message) { cases++; if (!value) throw new Exception(message); }

    sealed class Clock
    {
        public TimeSpan Now;
        public TimeSpan Read() => Now;
        public void Sleep(TimeSpan d) => Now += d;
    }

    /// <summary>Probe whose readings are functions of the fake time in seconds.</summary>
    sealed class ScriptedProbe : IActivationProbe
    {
        public Clock Clock = new Clock();
        public string? BootDirectory { get; set; }
        public Func<double, SystemStateSample?> State = _ => null;
        public Func<double, BootConfigSample?> Boot = _ => null;
        public SystemStateSample? ReadSystemState() => State(Clock.Now.TotalSeconds);
        public BootConfigSample? ReadBootConfig() => BootDirectory == null ? null : Boot(Clock.Now.TotalSeconds);
    }

    static SystemStateSample S(AdsState state, int? restart = 60) => new SystemStateSample { State = state, RestartIndex = restart };
    static BootConfigSample B(string stamp, string? createTime = "2026-11-05T18:32:12") =>
        new BootConfigSample { Exists = true, Stamp = stamp, CreateTime = createTime, LastWriteUtc = new DateTime(2026, 11, 5) };

    static ActivationWaitOptions Options(double timeout = 120) => new ActivationWaitOptions
    {
        Timeout = TimeSpan.FromSeconds(timeout),
        PollInterval = TimeSpan.FromMilliseconds(500),
        SettleTime = TimeSpan.FromSeconds(3),
        UnverifiedDelay = TimeSpan.FromSeconds(20),
    };

    static ActivationWaitResult Run(ScriptedProbe probe, ActivationWaitOptions? options = null)
    {
        var baseline = ActivationWait.Capture(probe);
        return ActivationWait.Wait(probe, baseline, options ?? Options(), null, probe.Clock.Read, probe.Clock.Sleep);
    }

    public static void Run()
    {
        // The reported race: the runtime is already in Config after ~5 s, but the
        // UmRT rewrites CurrentConfig.xml only at ~12 s. The waiter must not
        // return (and the caller must not restart) before the rewrite has settled.
        var local = new ScriptedProbe { BootDirectory = @"C:\Boot" };
        local.State = t => t < 2 ? S(AdsState.Run) : t < 5 ? S(AdsState.Reconfig) : S(AdsState.Config, 61);
        local.Boot = t => t < 12 ? B("old") : t < 13 ? B("new", null) : B("new", "2026-11-05T19:00:00");
        var result = Run(local);
        Check(result.Completed && result.Verified && result.Signal == "bootConfig+systemState", "Local activation verified by both signals: " + result.ErrorMessage);
        Check(result.ElapsedMs >= 16000, "No completion before CurrentConfig.xml is rewritten, complete and settled: " + result.ElapsedMs);
        Check(result.ElapsedMs < 17000, "Completion follows the settle time without a fixed long sleep: " + result.ElapsedMs);
        Check(result.Observations.Any(o => o.Contains("Reconfig")) && result.SystemStateAfter!.StartsWith("Config"), "State transitions are recorded");

        // Boot configuration never rewritten: time out, report it, do not complete.
        var stale = new ScriptedProbe { BootDirectory = @"C:\Boot" };
        stale.State = t => t < 3 ? S(AdsState.Run) : S(AdsState.Config, 61);
        stale.Boot = _ => B("old");
        result = Run(stale, Options(30));
        Check(!result.Completed && result.ErrorMessage!.Contains("was not rewritten") && result.ErrorMessage.Contains("not restarted"), "Stale boot configuration times out: " + result.ErrorMessage);
        Check(result.ElapsedMs >= 30000 && result.ElapsedMs < 31000, "Timeout is honoured");

        // A rewrite that keeps changing (still being written) is not complete.
        var churn = new ScriptedProbe { BootDirectory = @"C:\Boot" };
        churn.State = t => S(AdsState.Config, t < 1 ? 60 : 61);
        churn.Boot = t => t < 1 ? B("old") : B("write-" + (int)(t * 2));
        result = Run(churn, Options(20));
        Check(!result.Completed && result.ErrorMessage!.Contains("still changing"), "Continuously changing boot files never settle: " + result.ErrorMessage);

        // System state that never settles (stuck in Reconfig) blocks completion.
        var stuck = new ScriptedProbe { BootDirectory = @"C:\Boot" };
        stuck.State = t => t < 1 ? S(AdsState.Run) : S(AdsState.Reconfig);
        stuck.Boot = t => t < 2 ? B("old") : B("new");
        result = Run(stuck, Options(20));
        Check(!result.Completed && result.ErrorMessage!.Contains("did not settle"), "Unsettled system state times out: " + result.ErrorMessage);

        // Remote target: no boot folder. The restart index and Config state verify it.
        var remote = new ScriptedProbe();
        remote.State = t => t < 4 ? S(AdsState.Run, 7) : t < 9 ? S(AdsState.Reconfig, 7) : S(AdsState.Config, 8);
        result = Run(remote);
        Check(result.Completed && result.Verified && result.Signal == "systemState" && result.ElapsedMs >= 12000, "Remote activation verified by system state: " + result.ElapsedMs);

        // Remote Config -> Config: the restart index alone proves the reconfiguration.
        var configToConfig = new ScriptedProbe();
        configToConfig.State = t => S(AdsState.Config, t < 6 ? 7 : 8);
        result = Run(configToConfig);
        Check(result.Completed && result.ElapsedMs >= 9000, "Config to Config detected by restart index: " + result.ElapsedMs);

        // Remote target that never reacts: fail with a clear message, no restart.
        var idle = new ScriptedProbe();
        idle.State = _ => S(AdsState.Config, 7);
        result = Run(idle, Options(25));
        Check(!result.Completed && result.ErrorMessage!.Contains("never left Config"), "No reaction on the target times out: " + result.ErrorMessage);

        // Older targets answer only ReadState (no restart index): a state change still counts.
        var legacy = new ScriptedProbe();
        legacy.State = t => t < 3 ? S(AdsState.Run, null) : S(AdsState.Config, null);
        result = Run(legacy);
        Check(result.Completed && result.Signal == "systemState", "ReadState-only targets are supported");

        // ADS drops while the system service reconfigures: unreadable is not settled.
        var drop = new ScriptedProbe();
        drop.State = t => t < 2 ? S(AdsState.Run, 7) : t < 8 ? null : S(AdsState.Config, 8);
        result = Run(drop);
        Check(result.Completed && result.ElapsedMs >= 11000 && result.Observations.Any(o => o.Contains("unreadable")), "Connection loss delays completion: " + result.ElapsedMs);

        // Probe exceptions are treated like unreadable signals.
        var throwing = new ScriptedProbe();
        throwing.State = t => t < 3 ? throw new InvalidOperationException("router") : S(AdsState.Config, 9);
        result = Run(throwing);
        Check(result.Completed && result.Verified, "Probe exceptions do not escape");

        // Local folder but system service unreadable: boot configuration alone verifies, with a warning.
        var bootOnly = new ScriptedProbe { BootDirectory = @"C:\Boot" };
        bootOnly.Boot = t => t < 6 ? B("old") : B("new");
        result = Run(bootOnly);
        Check(result.Completed && result.Verified && result.Signal == "bootConfig" && result.Warning != null && result.ElapsedMs >= 9000, "Boot-only verification");

        // Nothing readable at all: graceful fixed-delay fallback, flagged unverified.
        var blind = new ScriptedProbe();
        result = Run(blind);
        Check(result.Completed && !result.Verified && result.Signal == "none" && result.Warning!.Contains("could not be verified"), "Unverified fallback");
        Check(result.ElapsedMs >= 20000 && result.ElapsedMs < 21000, "Unverified fallback waits the fixed delay");

        CheckBootFiles();
        CheckDiscovery();
        CheckOptions();
        Console.WriteLine("PASS: " + cases + " activation wait cases; no ADS, XAE or PLC used.");
    }

    static void CheckBootFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "twincat-activation-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Plc"));
        try
        {
            Check(!ActivationWait.ReadBootConfig(dir).Exists, "Missing CurrentConfig.xml");
            string config = Path.Combine(dir, "CurrentConfig.xml");
            File.WriteAllText(config, "<?xml version=\"1.0\"?><TcBootProject CreateTime=\"2026-11-05T18:32:12\"><System/></TcBootProject>");
            File.WriteAllText(Path.Combine(dir, "Plc", "Port_851.app"), "app");
            var before = ActivationWait.ReadBootConfig(dir);
            Check(before.Exists && before.CreateTime == "2026-11-05T18:32:12" && before.Stamp.Contains("Port_851.app"), "Boot fingerprint and CreateTime");
            Check(!ActivationWait.BootChanged(before, ActivationWait.ReadBootConfig(dir)), "Unchanged boot configuration");

            File.WriteAllText(config, "<?xml version=\"1.0\"?><TcBootProject CreateTime=\"2026-11-05T19:00:00\"><System>");
            Check(ActivationWait.ReadBootConfig(dir).CreateTime == null, "Partially written CurrentConfig.xml is incomplete");
            File.WriteAllText(config, "<!DOCTYPE TcBootProject [<!ENTITY x SYSTEM 'file:///unread'>]><TcBootProject CreateTime=\"x\">&x;</TcBootProject>");
            Check(ActivationWait.ReadBootConfig(dir).CreateTime == null, "DTD rejected");
            File.WriteAllText(config, "<Other CreateTime=\"x\"/>");
            Check(ActivationWait.ReadBootConfig(dir).CreateTime == null, "Only a TcBootProject counts");

            File.WriteAllText(config, "<?xml version=\"1.0\"?><TcBootProject CreateTime=\"2026-11-05T19:00:00\"><System/></TcBootProject>");
            var after = ActivationWait.ReadBootConfig(dir);
            Check(ActivationWait.BootChanged(before, after) && after.CreateTime == "2026-11-05T19:00:00", "Rewritten boot configuration detected");
            var sameContent = new BootConfigSample { Exists = true, Stamp = before.Stamp, CreateTime = "2026-11-05T19:00:00" };
            Check(ActivationWait.BootChanged(before, sameContent), "A new CreateTime alone counts as rewritten");
            Check(ActivationWait.BootChanged(null, after) && ActivationWait.BootChanged(new BootConfigSample { Exists = false }, after), "First boot configuration counts as rewritten");
        }
        finally { Directory.Delete(dir, true); }
    }

    static void CheckDiscovery()
    {
        Check(ActivationWait.FormatNetId("C0A8016C0101") == "192.168.1.108.1.1", "Hex AMS Net ID");
        Check(ActivationWait.FormatNetId("C0A8016C01") == null && ActivationWait.FormatNetId("ZZA8016C0101") == null, "Malformed AMS Net ID");

        string root = Path.Combine(Path.GetTempPath(), "twincat-runtimes-tests-" + Guid.NewGuid().ToString("N"));
        string boot = Path.Combine(root, "UmRT_Default", "3.1", "Boot");
        Directory.CreateDirectory(boot);
        try
        {
            File.WriteAllText(Path.Combine(root, "UmRT_Default", "3.1", "TcRegistry.xml"),
                "<?xml version=\"1.0\"?><TcRegistry><Key Name=\"HKLM\"><Key Name=\"Software\"><Key Name=\"Beckhoff\"><Key Name=\"TwinCAT3\">" +
                "<Key Name=\"System\"><Value Name=\"AmsNetId\" Type=\"BIN\">C0A8016C0101</Value></Key>" +
                "<Key Name=\"3.1\"><Value Name=\"BootDir\" Type=\"SZ\">" + Path.Combine(root, "UmRT_Default", "3.1") + "\\\\Boot\\</Value></Key>" +
                "</Key></Key></Key></Key></TcRegistry>");
            (string?, string?) none() => (null, null);
            string? found = ActivationWait.FindLocalBootDirectory("192.168.1.108.1.1", root, none);
            Check(found != null && Path.GetFullPath(found).TrimEnd('\\') == Path.GetFullPath(boot).TrimEnd('\\'), "User-mode runtime boot folder: " + found);
            Check(ActivationWait.FindLocalBootDirectory("5.22.157.86.1.1", root, none) == null, "Remote target has no local boot folder");
            Check(ActivationWait.FindLocalBootDirectory(null, root, none) == null, "No target, no boot folder");

            (string?, string?) xar() => ("10.0.0.5.1.1", boot);
            Check(ActivationWait.FindLocalBootDirectory("10.0.0.5.1.1", root, xar) == boot, "Local XAR boot folder");
            Check(ActivationWait.FindLocalBootDirectory("127.0.0.1.1.1", root, xar) == boot, "Loopback target maps to the local XAR");
            (string?, string?) missing() => ("10.0.0.5.1.1", Path.Combine(root, "nope"));
            Check(ActivationWait.FindLocalBootDirectory("10.0.0.5.1.1", root, missing) == null, "Missing boot folder is not used");
            Check(ActivationWait.FindLocalBootDirectory("10.0.0.5.1.1", root, () => throw new UnauthorizedAccessException()) == null, "Registry errors mean remote");

            File.WriteAllText(Path.Combine(root, "UmRT_Default", "3.1", "TcRegistry.xml"), "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///unread'>]><TcRegistry>&e;</TcRegistry>");
            Check(ActivationWait.FindLocalBootDirectory("192.168.1.108.1.1", root, none) == null, "Unreadable runtime registry is ignored");
        }
        finally { Directory.Delete(root, true); }
    }

    static void CheckOptions()
    {
        string? previous = Environment.GetEnvironmentVariable(ActivationWaitOptions.TimeoutEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ActivationWaitOptions.TimeoutEnvironmentVariable, null);
            Check(ActivationWaitOptions.FromEnvironment().Timeout == TimeSpan.FromSeconds(180), "Default timeout");
            Environment.SetEnvironmentVariable(ActivationWaitOptions.TimeoutEnvironmentVariable, "300");
            Check(ActivationWaitOptions.FromEnvironment().Timeout == TimeSpan.FromSeconds(300), "Timeout override");
            Environment.SetEnvironmentVariable(ActivationWaitOptions.TimeoutEnvironmentVariable, "1");
            Check(ActivationWaitOptions.FromEnvironment().Timeout == TimeSpan.FromSeconds(10), "Timeout lower bound");
            Environment.SetEnvironmentVariable(ActivationWaitOptions.TimeoutEnvironmentVariable, "999999");
            Check(ActivationWaitOptions.FromEnvironment().Timeout == TimeSpan.FromSeconds(1800), "Timeout upper bound");
            Environment.SetEnvironmentVariable(ActivationWaitOptions.TimeoutEnvironmentVariable, "abc");
            Check(ActivationWaitOptions.FromEnvironment().Timeout == TimeSpan.FromSeconds(180), "Invalid override ignored");
        }
        finally { Environment.SetEnvironmentVariable(ActivationWaitOptions.TimeoutEnvironmentVariable, previous); }
    }
}
