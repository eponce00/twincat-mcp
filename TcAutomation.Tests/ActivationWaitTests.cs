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

    /// <summary>
    /// Target whose readings are functions of the fake time in seconds and of
    /// how many activations were dispatched (and when the last one was).
    /// </summary>
    sealed class ScriptedProbe : IActivationProbe
    {
        public Clock Clock = new Clock();
        public string? BootDirectory { get; set; }
        public int Activations;
        public double LastActivation;
        public Func<double, SystemStateSample?> State = _ => null;
        public Func<double, int, BootConfigSample?> Boot = (_, __) => null;
        public SystemStateSample? ReadSystemState() => State(Clock.Now.TotalSeconds);
        public BootConfigSample? ReadBootConfig() => BootDirectory == null ? null : Boot(Clock.Now.TotalSeconds, Activations);
        public void Activate() { Activations++; LastActivation = Clock.Now.TotalSeconds; }
    }

    static SystemStateSample S(AdsState state, int? restart = 60) => new SystemStateSample { State = state, RestartIndex = restart };

    /// <summary>Boot sample: config is the CurrentConfig.xml stamp, plc the PLC boot files.</summary>
    static BootConfigSample B(string config, string plc = "plc-old", string? createTime = null) => new BootConfigSample
    {
        Exists = true,
        ConfigStamp = config,
        Stamp = config + ";" + plc,
        CreateTime = createTime ?? (config == "old" ? "2026-11-05T18:32:12" : "2026-11-05T19:13:51"),
        LastWriteUtc = new DateTime(2026, 11, 5),
    };

    static ActivationWaitOptions Options(double timeout = 120, int attempts = 2) => new ActivationWaitOptions
    {
        Timeout = TimeSpan.FromSeconds(timeout),
        PollInterval = TimeSpan.FromMilliseconds(500),
        SettleTime = TimeSpan.FromSeconds(3),
        UnverifiedDelay = TimeSpan.FromSeconds(20),
        RetryAfter = TimeSpan.FromSeconds(30),
        MaxAttempts = attempts,
    };

    static ActivationWaitResult Run(ScriptedProbe probe, ActivationWaitOptions? options = null) =>
        ActivationWait.ActivateAndWait(probe, probe.Activate, options ?? Options(), null, probe.Clock.Read, probe.Clock.Sleep);

    public static void Run()
    {
        // Observed on the UmRT (2026-11-05): a landed activation rewrites
        // CurrentConfig.xml and leaves the system service in Run with the same
        // restart index. Completion follows the settle time, not a fixed sleep.
        var landed = new ScriptedProbe { BootDirectory = @"C:\Boot" };
        landed.State = _ => S(AdsState.Run);
        landed.Boot = (t, n) => t < 2 ? B("old") : t < 4 ? new BootConfigSample { Exists = true, ConfigStamp = "new", Stamp = "partial", CreateTime = null } : B("new", "plc-new");
        var result = Run(landed);
        Check(result.Completed && result.Verified && result.Signal == "bootConfig" && result.Attempts == 1 && landed.Activations == 1, "Landed activation verified: " + result.ErrorMessage);
        Check(result.ElapsedMs >= 7000 && result.ElapsedMs < 8000, "Completion waits for a complete, settled CurrentConfig.xml: " + result.ElapsedMs);
        Check(result.BootConfigRewritten == true && result.SystemStateAfter == "Run (restart index 60)", "Receipt records the rewrite and the unchanged state");

        // Observed regression: XAE replaces the PLC boot files even when the
        // activation never lands. That alone must not count as landed.
        var plcOnly = new ScriptedProbe { BootDirectory = @"C:\Boot" };
        plcOnly.State = _ => S(AdsState.Run);
        plcOnly.Boot = (t, n) => t < 2 ? B("old") : B("old", "plc-new");
        result = Run(plcOnly, Options(30, 1));
        Check(!result.Completed && result.BootConfigRewritten == false && result.ErrorMessage!.Contains("was not rewritten")
            && result.ErrorMessage.Contains("not restarted") && result.ErrorMessage.Contains("do not restart"), "PLC-only rewrite is not a landed activation: " + result.ErrorMessage);
        Check(result.ElapsedMs >= 30000 && result.ElapsedMs < 31000, "Timeout is honoured");

        // Observed pattern: the first activation after loading a solution does
        // not reach the target; the second does. The waiter re-activates once.
        var second = new ScriptedProbe { BootDirectory = @"C:\Boot" };
        second.State = _ => S(AdsState.Run);
        second.Boot = (t, n) => n < 2 ? B("old", t < 1 ? "plc-old" : "plc-new") : t - second.LastActivation < 6 ? B("old", "plc-new") : B("new", "plc-newer");
        result = Run(second);
        Check(result.Completed && result.Verified && result.Attempts == 2 && second.Activations == 2, "Second activation lands: " + result.ErrorMessage);
        Check(result.ElapsedMs >= 39000 && result.ElapsedMs < 40000, "Retry after 30 s, then verify: " + result.ElapsedMs);
        Check(result.Observations.Any(o => o.StartsWith("attempt 2:")), "Observations name the attempt");

        // Neither attempt lands: fail after two dispatches, never restart.
        var never = new ScriptedProbe { BootDirectory = @"C:\Boot" };
        never.State = _ => S(AdsState.Run);
        never.Boot = (t, n) => B("old", "plc-new");
        result = Run(never, Options(40));
        Check(!result.Completed && result.Attempts == 2 && never.Activations == 2 && result.ErrorMessage!.StartsWith("2 activation attempts:")
            && result.ErrorMessage.Contains("was not rewritten"), "Retries are bounded: " + result.ErrorMessage);
        Check(result.ElapsedMs >= 70000 && result.ElapsedMs < 71000, "Retry window plus final timeout: " + result.ElapsedMs);

        // A rewrite in progress is never re-dispatched; it gets the full timeout.
        var churn = new ScriptedProbe { BootDirectory = @"C:\Boot" };
        churn.State = _ => S(AdsState.Config);
        churn.Boot = (t, n) => t < 1 ? B("old") : B("write-" + (int)(t * 2));
        result = Run(churn, Options(45));
        Check(!result.Completed && churn.Activations == 1 && result.ErrorMessage!.Contains("still changing") && result.ElapsedMs >= 45000, "Continuously changing boot files never settle: " + result.ErrorMessage);

        // A local target whose system service is stuck transitioning is not done.
        var stuck = new ScriptedProbe { BootDirectory = @"C:\Boot" };
        stuck.State = t => t < 1 ? S(AdsState.Run) : S(AdsState.Reconfig);
        stuck.Boot = (t, n) => t < 2 ? B("old") : B("new");
        result = Run(stuck, Options(20));
        Check(!result.Completed && stuck.Activations == 1 && result.ErrorMessage!.Contains("did not settle"), "Unsettled system state times out: " + result.ErrorMessage);

        // Connection loss while the boot configuration lands delays completion.
        var drop = new ScriptedProbe { BootDirectory = @"C:\Boot" };
        drop.State = t => t < 2 ? S(AdsState.Run) : t < 8 ? null : S(AdsState.Run);
        drop.Boot = (t, n) => t < 3 ? B("old") : B("new");
        result = Run(drop);
        Check(result.Completed && result.ElapsedMs >= 11000 && result.Observations.Any(o => o.Contains("unreadable")), "Unreadable state is not settled: " + result.ElapsedMs);

        // Probe exceptions are treated like unreadable signals.
        var throwing = new ScriptedProbe { BootDirectory = @"C:\Boot" };
        throwing.State = t => t < 3 ? throw new InvalidOperationException("router") : S(AdsState.Run);
        throwing.Boot = (t, n) => t < 5 ? throw new IOException("locked") : B("new");
        result = Run(throwing);
        Check(result.Completed && result.Verified, "Probe exceptions do not escape");

        // Local folder but system service unreadable: boot configuration alone verifies, with a warning.
        var bootOnly = new ScriptedProbe { BootDirectory = @"C:\Boot" };
        bootOnly.Boot = (t, n) => t < 6 ? B("old") : B("new");
        result = Run(bootOnly);
        Check(result.Completed && result.Verified && result.Signal == "bootConfig" && result.Warning != null && result.ElapsedMs >= 9000, "Boot-only verification");

        // Remote target: a landed activation is invisible over ADS. Settle, then
        // report unverified; never re-dispatch without a way to verify.
        var remote = new ScriptedProbe();
        remote.State = _ => S(AdsState.Run, 7);
        result = Run(remote);
        Check(result.Completed && !result.Verified && result.Signal == "systemState" && remote.Activations == 1
            && result.Warning!.Contains("not visible over ADS") && result.ElapsedMs >= 3000 && result.ElapsedMs < 4000, "Remote activation reported unverified");
        Check(result.BootConfigRewritten == null, "Remote receipt has no boot verdict");

        var remoteDrop = new ScriptedProbe();
        remoteDrop.State = t => t < 2 ? S(AdsState.Run, 7) : t < 8 ? null : S(AdsState.Config, 8);
        result = Run(remoteDrop);
        Check(result.Completed && result.ElapsedMs >= 11000, "Remote waits for the system service to settle after a drop: " + result.ElapsedMs);

        var remoteStuck = new ScriptedProbe();
        remoteStuck.State = _ => S(AdsState.Reconfig, 7);
        result = Run(remoteStuck, Options(25));
        Check(!result.Completed && remoteStuck.Activations == 1 && result.ErrorMessage!.Contains("did not settle"), "Remote stuck in Reconfig times out");

        // Older targets answer only ReadState (no restart index).
        var legacy = new ScriptedProbe();
        legacy.State = _ => S(AdsState.Run, null);
        result = Run(legacy);
        Check(result.Completed && result.Signal == "systemState" && result.SystemStateAfter == "Run", "ReadState-only targets are supported");

        // Nothing readable at all: graceful fixed-delay fallback, flagged unverified.
        var blind = new ScriptedProbe();
        result = Run(blind);
        Check(result.Completed && !result.Verified && result.Signal == "none" && result.Warning!.Contains("could not be verified") && blind.Activations == 1, "Unverified fallback");
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
            System.Threading.Thread.Sleep(20);
            File.WriteAllText(Path.Combine(dir, "Plc", "Port_851.app"), "new app");
            var plcOnly = ActivationWait.ReadBootConfig(dir);
            Check(plcOnly.Stamp != before.Stamp && !ActivationWait.BootChanged(before, plcOnly), "PLC boot files alone do not count as rewritten");

            File.WriteAllText(config, "<?xml version=\"1.0\"?><TcBootProject CreateTime=\"2026-11-05T19:00:00\"><System>");
            Check(ActivationWait.ReadBootConfig(dir).CreateTime == null, "Partially written CurrentConfig.xml is incomplete");
            File.WriteAllText(config, "<!DOCTYPE TcBootProject [<!ENTITY x SYSTEM 'file:///unread'>]><TcBootProject CreateTime=\"x\">&x;</TcBootProject>");
            Check(ActivationWait.ReadBootConfig(dir).CreateTime == null, "DTD rejected");
            File.WriteAllText(config, "<Other CreateTime=\"x\"/>");
            Check(ActivationWait.ReadBootConfig(dir).CreateTime == null, "Only a TcBootProject counts");

            File.WriteAllText(config, "<?xml version=\"1.0\"?><TcBootProject CreateTime=\"2026-11-05T19:00:00\"><System/></TcBootProject>");
            var after = ActivationWait.ReadBootConfig(dir);
            Check(ActivationWait.BootChanged(before, after) && after.CreateTime == "2026-11-05T19:00:00", "Rewritten boot configuration detected");
            var sameContent = new BootConfigSample { Exists = true, ConfigStamp = before.ConfigStamp, Stamp = before.Stamp, CreateTime = "2026-11-05T19:00:00" };
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
            Check(ActivationWaitOptions.FromEnvironment().Timeout == TimeSpan.FromSeconds(120), "Default timeout");
            Environment.SetEnvironmentVariable(ActivationWaitOptions.TimeoutEnvironmentVariable, "300");
            Check(ActivationWaitOptions.FromEnvironment().Timeout == TimeSpan.FromSeconds(300), "Timeout override");
            Environment.SetEnvironmentVariable(ActivationWaitOptions.TimeoutEnvironmentVariable, "1");
            Check(ActivationWaitOptions.FromEnvironment().Timeout == TimeSpan.FromSeconds(10), "Timeout lower bound");
            Environment.SetEnvironmentVariable(ActivationWaitOptions.TimeoutEnvironmentVariable, "999999");
            Check(ActivationWaitOptions.FromEnvironment().Timeout == TimeSpan.FromSeconds(1800), "Timeout upper bound");
            Environment.SetEnvironmentVariable(ActivationWaitOptions.TimeoutEnvironmentVariable, "abc");
            Check(ActivationWaitOptions.FromEnvironment().Timeout == TimeSpan.FromSeconds(120), "Invalid override ignored");
        }
        finally { Environment.SetEnvironmentVariable(ActivationWaitOptions.TimeoutEnvironmentVariable, previous); }
    }
}
