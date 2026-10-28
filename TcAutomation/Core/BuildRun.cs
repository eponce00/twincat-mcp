using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using EnvDTE;

namespace TcAutomation.Core
{
    public sealed class OwnedDeadline : IDisposable
    {
        readonly System.Threading.Timer timer;
        int state;
        public bool Expired => Volatile.Read(ref state) == 2;
        public OwnedDeadline(Action terminateOwned, int seconds)
        {
            if (seconds < 1 || seconds > 600) throw new ArgumentOutOfRangeException(nameof(seconds));
            timer = new System.Threading.Timer(_ =>
            {
                if (Interlocked.CompareExchange(ref state, 2, 0) == 0) terminateOwned();
            }, null, seconds * 1000, Timeout.Infinite);
        }
        public void Complete()
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) == 2) throw new TimeoutException("Operation deadline expired; outcome is unknown.");
            timer.Dispose();
        }
        public void Dispose() { Interlocked.CompareExchange(ref state, 1, 0); timer.Dispose(); }
    }

    public sealed class BuildRun
    {
        public string BuildId { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime StartedUtc { get; set; }
        public DateTime CompletedUtc { get; set; }
        public long SourceRevision { get; set; }
        public int FailedProjects { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public string Output { get; set; } = "";
        public string Configuration { get; set; }
        public string Platform { get; set; }

        // Kept separate from COM for deterministic completion/output tests.
        public static string OutputDelta(string before, string after)
        {
            if (after.StartsWith(before, StringComparison.Ordinal)) return after.Substring(before.Length);
            return after; // The IDE cleared/replaced the pane during this build.
        }

        public static BuildRun Execute(VisualStudioInstance vs, int timeoutSeconds)
        {
            if (timeoutSeconds < 1 || timeoutSeconds > 600) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
            var solution = vs.Dte.Solution.SolutionBuild;
            if (solution.BuildState == vsBuildState.vsBuildStateInProgress)
                throw new InvalidOperationException("An earlier build is still running; no second build was dispatched.");
            var configuration = (EnvDTE80.SolutionConfiguration2)solution.ActiveConfiguration;
            var run = new BuildRun { SourceRevision = vs.SourceRevision, Configuration = configuration.Name, Platform = configuration.PlatformName };
            var events = vs.Dte.Events.BuildEvents;
            bool started = false, done = false;
            _dispBuildEvents_OnBuildBeginEventHandler begin = (scope, action) => { if (action == vsBuildAction.vsBuildActionBuild) started = true; };
            _dispBuildEvents_OnBuildDoneEventHandler end = (scope, action) => { if (started && action == vsBuildAction.vsBuildActionBuild) done = true; };
            var before = CapturePanes(vs, true);
            var elapsed = Stopwatch.StartNew();
            bool deadlineExpired = false;
            int completionState = 0;
            // Also bounds a COM call that never returns to the STA loop.
            using var deadline = new System.Threading.Timer(_ =>
            {
                if (Interlocked.CompareExchange(ref completionState, 2, 0) != 0) return;
                deadlineExpired = true;
                vs.KillOwnedProcess("build deadline expired");
            }, null, timeoutSeconds * 1000, Timeout.Infinite);
            events.OnBuildBegin += begin;
            events.OnBuildDone += end;
            try
            {
                run.StartedUtc = DateTime.UtcNow;
                solution.Build(false);
                while (!done)
                {
                    if (deadlineExpired || elapsed.Elapsed.TotalSeconds >= timeoutSeconds)
                        throw new TimeoutException("Build completion was not observed before the deadline; outcome is unknown.");
                    // DTE events are delivered to this STA. SpinWait alone does
                    // not pump them, and Done may still describe an earlier build.
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(25);
                }
                if (Interlocked.CompareExchange(ref completionState, 1, 0) != 0)
                    throw new TimeoutException("Build deadline expired; outcome is unknown.");
                run.CompletedUtc = DateTime.UtcNow;
                run.FailedProjects = solution.LastBuildInfo;
                var after = CapturePanes(vs);
                run.Output = string.Join("\n", after.Select(p => new { p.Key, Text = OutputDelta(before.TryGetValue(p.Key, out var old) ? old : "", p.Value) })
                    .Where(p => !string.IsNullOrWhiteSpace(p.Text)).Select(p => "[" + p.Key + "]\n" + p.Text));
                return run;
            }
            catch (Exception ex) when (deadlineExpired)
            {
                throw new TimeoutException("Build deadline expired; the owned shell was terminated and the outcome is unknown.", ex);
            }
            finally
            {
                Interlocked.CompareExchange(ref completionState, 1, 0);
                try { events.OnBuildBegin -= begin; events.OnBuildDone -= end; } catch { }
                GC.KeepAlive(events);
            }
        }

        static Dictionary<string, string> CapturePanes(VisualStudioInstance vs, bool clear = false)
        {
            var result = new Dictionary<string, string>();
            foreach (OutputWindowPane pane in vs.Dte.ToolWindows.OutputWindow.OutputWindowPanes)
            {
                try
                {
                    if (clear) pane.Clear();
                    var document = pane.TextDocument;
                    result[pane.Name + " " + pane.Guid] = document.StartPoint.CreateEditPoint().GetText(document.EndPoint);
                }
                catch (System.Runtime.InteropServices.COMException) { }
            }
            return result;
        }
    }
}
