#nullable enable
using System;
using System.Collections.Generic;
using TcAutomation.Commands;
using TcAutomation.Core;
using TwinCAT.Ads;

/// <summary>
/// The wait for Run after a restart. A scripted target and a fake clock replace ADS; no XAE or PLC is used.
/// </summary>
static class RunWaitTests
{
    static int cases;
    static void Check(bool value, string message) { cases++; if (!value) throw new Exception(message); }

    sealed class Clock
    {
        public TimeSpan Now;
        public TimeSpan Read() => Now;
        public void Sleep(TimeSpan d) => Now += d;
    }

    /// <summary>Port states as functions of the fake time in seconds; null = not answering.</summary>
    sealed class Script : IRunProbe
    {
        public Clock Clock = new Clock();
        public Func<double, AdsState?> System = _ => AdsState.Run;
        public Func<double, AdsState?> Plc = _ => AdsState.Run;
        public AdsState? ReadState(int port) => port == RunWait.SystemPort ? System(Clock.Now.TotalSeconds) : Plc(Clock.Now.TotalSeconds);
    }

    static RunWaitResult Wait(Script s, params int[] ports) =>
        RunWait.Wait(s, ports, new RunWaitOptions(), s.Clock.Read, s.Clock.Sleep);

    public static void Run()
    {
        // A normal restart: unreachable, then Init, then Run on both ports
        var s = new Script
        {
            System = t => t < 4 ? (AdsState?)null : t < 8 ? AdsState.Init : AdsState.Run,
            Plc = t => t < 10 ? (AdsState?)null : AdsState.Run,
        };
        var r = Wait(s, 851);
        Check(r.ReachedRun && r.SystemState == "Run" && r.PlcStates[0].State == "Run", "reaches Run on both ports");
        Check(r.ElapsedMs >= 12000 && r.ElapsedMs < 14000, "Run must hold 2 s after the last port arrives: " + r.ElapsedMs);
        Check(r.Transitions.Count == 4 && r.Transitions[0].StartsWith("0s system unreadable"), "each distinct observation is recorded once");

        // The station incident: the system service comes back in Config and stays there
        s = new Script { System = t => t < 5 ? (AdsState?)null : AdsState.Config, Plc = _ => null };
        r = Wait(s, 851);
        Check(!r.ReachedRun && r.SystemState == "Config" && r.Message.Contains("stayed there for 30 s"), "Config after the restart is a failure, not success");
        Check(r.ElapsedMs <= 36000, "stops once Config has held 30 s, not at the full timeout");

        // A brief Config during the restart is not a failure
        s = new Script { System = t => t < 6 ? AdsState.Config : AdsState.Run };
        Check(Wait(s, 851).ReachedRun, "transient Config while restarting");

        // The PLC never starts although the system is in Run
        s = new Script { System = _ => AdsState.Run, Plc = _ => AdsState.Stop };
        r = Wait(s, 851);
        Check(!r.ReachedRun && r.Message.Contains("within 120 s") && r.PlcStates[0].State == "Stop", "PLC in Stop times out with its state");

        // Run that does not hold is not Run
        s = new Script { System = t => ((int)t % 2 == 0) ? AdsState.Run : AdsState.Init };
        Check(!Wait(s).ReachedRun, "flickering Run never settles");

        // No PLC ports: the system service alone
        s = new Script { System = t => t < 3 ? (AdsState?)null : AdsState.Run, Plc = _ => throw new Exception("PLC port must not be read") };
        Check(Wait(s).ReachedRun, "system service only");

        // Run from before the restart does not count: the restart command returned before the target went down
        s = new Script { System = t => t < 0.6 || t >= 3 ? AdsState.Run : (AdsState?)null, Plc = t => t < 0.6 || t >= 4 ? AdsState.Run : AdsState.Stop };
        r = Wait(s, 851);
        Check(r.ReachedRun && r.ElapsedMs >= 6000 && r.Transitions[1].Contains("unreadable"), "Run counts only after the target left Run: " + r.ElapsedMs);

        // A target that never leaves Run did not restart
        s = new Script();
        r = Wait(s, 851);
        Check(!r.ReachedRun && r.Message.Contains("restart was not observed") && r.ElapsedMs <= 31000, "a restart that never happened is a failure: " + r.Message);

        // The summary shows the target's own warnings and errors before anything else
        r = new RunWaitResult { SystemState = "Config", PlcStates = new List<PortState> { new PortState { Port = 851, State = "unreadable" } } };
        r.TargetMessages.Add(new ReadPlcLogEntry { Severity = "Info", Text = "TwinCAT System starting" });
        r.TargetMessages.Add(new ReadPlcLogEntry { Severity = "Error", Text = "Init4\\Hardware Config failed: the platform of the boot project does not match" });
        string summary = RunWait.Summary(r);
        Check(summary.Contains("system Config, port 851 unreadable") && summary.Contains("[Error] Init4") && !summary.Contains("starting"), "summary: states, then errors only");
        r.TargetMessages.Clear();
        r.TargetLogError = "The target event log could not be read: timeout";
        Check(RunWait.Summary(r).Contains("could not be read"), "summary says when the log was unreadable");
        r.TargetLogError = null;
        Check(RunWait.Summary(r).Contains("logged nothing"), "summary says when the log was empty");

        Console.WriteLine("PASS: " + cases + " run wait cases; no ADS, XAE or PLC used.");
    }
}
