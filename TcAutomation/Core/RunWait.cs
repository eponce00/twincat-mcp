using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TcAutomation.Commands;
using TcEventLoggerAdsProxyLib;
using TwinCAT.Ads;

namespace TcAutomation.Core
{
    /// <summary>Reads ADS states; null when the port does not answer (yet).</summary>
    public interface IRunProbe
    {
        AdsState? ReadState(int port);
    }

    public sealed class RunWaitOptions
    {
        /// <summary>How long the target may take to reach Run.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(120);
        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);
        /// <summary>Run must hold this long on every port before it counts.</summary>
        public TimeSpan Settle { get; set; } = TimeSpan.FromSeconds(2);
        /// <summary>A system service that stays in Config this long after answering has finished starting in Config.</summary>
        public TimeSpan ConfigGrace { get; set; } = TimeSpan.FromSeconds(30);
    }

    public sealed class PortState
    {
        public int Port { get; set; }
        public string State { get; set; } = "unreadable";
    }

    public sealed class RunWaitResult
    {
        public bool ReachedRun { get; set; }
        public string SystemState { get; set; } = "unreadable";
        public List<PortState> PlcStates { get; set; } = new List<PortState>();
        public long ElapsedMs { get; set; }
        public int Attempts { get; set; }
        /// <summary>Each distinct observation, e.g. "3s system Config, 851 unreadable".</summary>
        public List<string> Transitions { get; set; } = new List<string>();
        /// <summary>The target's own event log during the restart: what XAE shows in its dialog.</summary>
        public List<ReadPlcLogEntry> TargetMessages { get; set; } = new List<ReadPlcLogEntry>();
        public string? TargetLogError { get; set; }
        public string Message { get; set; } = "";
    }

    /// <summary>
    /// After a restart, wait until the system service (port 10000) and every PLC port given are in Run, or say
    /// why not. Stops early when the system service settles in Config.
    /// </summary>
    public static class RunWait
    {
        public const int SystemPort = 10000;

        public static RunWaitResult Wait(IRunProbe probe, IList<int> plcPorts, RunWaitOptions options,
            Func<TimeSpan> now, Action<TimeSpan> sleep, Action<string>? progress = null)
        {
            var result = new RunWaitResult();
            var start = now();
            TimeSpan? runSince = null, configSince = null;
            string? last = null;
            while (true)
            {
                result.Attempts++;
                var elapsed = now() - start;
                var system = probe.ReadState(SystemPort);
                var plcs = plcPorts.Select(p => (port: p, state: probe.ReadState(p))).ToList();
                result.SystemState = Name(system);
                result.PlcStates = plcs.Select(p => new PortState { Port = p.port, State = Name(p.state) }).ToList();
                string observed = $"system {result.SystemState}" + string.Concat(plcs.Select(p => $", {p.port} {Name(p.state)}"));
                if (observed != last)
                {
                    result.Transitions.Add($"{elapsed.TotalSeconds:0}s {observed}");
                    progress?.Invoke(observed);
                    last = observed;
                }

                bool allRun = system == AdsState.Run && plcs.All(p => p.state == AdsState.Run);
                runSince = allRun ? runSince ?? elapsed : (TimeSpan?)null;
                configSince = system == AdsState.Config ? configSince ?? elapsed : (TimeSpan?)null;
                result.ElapsedMs = (long)elapsed.TotalMilliseconds;

                if (runSince != null && elapsed - runSince.Value >= options.Settle)
                {
                    result.ReachedRun = true;
                    result.Message = $"Target in Run after {elapsed.TotalSeconds:0} s (system service and PLC ports {string.Join(", ", plcPorts)}).";
                    return result;
                }
                if (configSince != null && elapsed - configSince.Value >= options.ConfigGrace)
                {
                    result.Message = $"The target started in Config and stayed there for {options.ConfigGrace.TotalSeconds:0} s; it did not reach Run.";
                    return result;
                }
                if (elapsed >= options.Timeout)
                {
                    result.Message = $"The target did not reach Run within {options.Timeout.TotalSeconds:0} s: {observed}.";
                    return result;
                }
                sleep(options.Interval);
            }
        }

        /// <summary>Restart the target through the given action and wait for Run, capturing its event log meanwhile.</summary>
        public static RunWaitResult RestartAndWait(string amsNetId, IList<int> plcPorts, Action restart, RunWaitOptions options,
            Action<string>? progress = null)
        {
            using var log = new TargetLogWindow(amsNetId);
            using var probe = new TargetRunProbe(amsNetId);
            var since = DateTime.UtcNow.AddSeconds(-10); // Margin for the target's clock
            restart();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var result = Wait(probe, plcPorts, options, () => clock.Elapsed, d => System.Threading.Thread.Sleep(d), progress);
            // The target's logger restarts with TwinCAT, so the live subscription misses the restart itself: read
            // back what the target logged since just before the restart.
            log.CollectLogged(amsNetId, since);
            result.TargetMessages = log.Messages();
            result.TargetLogError = log.Error;
            return result;
        }

        /// <summary>
        /// Why a target did not reach Run, for the error message: the last states, then the target's own warnings
        /// and errors (at most five), else its last messages.
        /// </summary>
        public static string Summary(RunWaitResult run)
        {
            var parts = new List<string> { $" Last state: system {run.SystemState}" + string.Concat(run.PlcStates.Select(p => $", port {p.Port} {p.State}")) + "." };
            var serious = run.TargetMessages.Where(m => m.Severity != null && !m.Severity.StartsWith("Info", StringComparison.OrdinalIgnoreCase)
                && !m.Severity.StartsWith("Verbose", StringComparison.OrdinalIgnoreCase)).ToList();
            var shown = (serious.Count > 0 ? serious : run.TargetMessages.Skip(Math.Max(0, run.TargetMessages.Count - 5)).ToList()).Take(5).ToList();
            if (shown.Count > 0)
                parts.Add(" Target messages: " + string.Join(" | ", shown.Select(m => $"[{m.Severity}] {m.Text}")));
            else if (run.TargetLogError != null)
                parts.Add(" " + run.TargetLogError);
            else
                parts.Add(" The target logged nothing during the restart.");
            return string.Concat(parts);
        }

        static string Name(AdsState? state) => state?.ToString() ?? "unreadable";
    }

    public static class PlcPorts
    {
        /// <summary>A PLC project's configured ADS port (PlcProjectDef/AdsPort), or null when not set.</summary>
        public static int? Of(TCatSysManagerLib.ITcSmTreeItem plc)
        {
            try
            {
                var port = System.Xml.Linq.XDocument.Parse(plc.ProduceXml(false)).Root?.Element("PlcProjectDef")?.Element("AdsPort")?.Value;
                return int.TryParse(port, out int value) ? value : (int?)null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Reads each port with a short timeout; any failure is "not answering yet".</summary>
    public sealed class TargetRunProbe : IRunProbe, IDisposable
    {
        private readonly string _netId;
        private readonly Dictionary<int, AdsClient> _clients = new Dictionary<int, AdsClient>();

        public TargetRunProbe(string netId) => _netId = netId;

        public AdsState? ReadState(int port)
        {
            try
            {
                if (!_clients.TryGetValue(port, out var client))
                {
                    client = new AdsClient { Timeout = 1000 };
                    client.Connect(_netId, port);
                    _clients[port] = client;
                }
                return client.ReadState().AdsState;
            }
            catch
            {
                if (_clients.TryGetValue(port, out var stale)) { stale.Dispose(); _clients.Remove(port); }
                return null;
            }
        }

        public void Dispose()
        {
            foreach (var c in _clients.Values) c.Dispose();
            _clients.Clear();
        }
    }

    /// <summary>
    /// The target's TcEventLogger (ADS port 110) for the duration of a restart, as XAE's dialog shows it. A logger
    /// that cannot be reached leaves an error, not a failure.
    /// </summary>
    public sealed class TargetLogWindow : IDisposable
    {
        private const int MaxMessages = 200;
        private readonly object _lock = new object();
        private readonly List<ReadPlcLogEntry> _messages = new List<ReadPlcLogEntry>();
        private TcEventLogger? _logger;

        public string? Error { get; private set; }

        public TargetLogWindow(string amsNetId)
        {
            int lcid = CultureInfo.CurrentCulture.LCID;
            try
            {
                _logger = new TcEventLogger();
                _logger.MessageSent += m => Add("Message", () => m.GetText(lcid), () => m.SeverityLevel.ToString().Replace("TCEVENTSEVERITY_", ""), () => m.EventClass.ToString(), () => m.EventId);
                _logger.AlarmRaised += a => Add("AlarmRaised", () => a.GetText(lcid), () => a.SeverityLevel.ToString().Replace("TCEVENTSEVERITY_", ""), () => a.EventClass.ToString(), () => a.EventId);
                _logger.Connect(amsNetId);
            }
            catch (Exception ex)
            {
                Error = $"The target event log could not be read: {ex.Message}";
            }
        }

        private void Add(string kind, Func<string> text, Func<string> severity, Func<string> eventClass, Func<uint> eventId)
        {
            try
            {
                var entry = new ReadPlcLogEntry
                {
                    Timestamp = DateTime.UtcNow.ToString("O"),
                    Kind = kind,
                    Text = text() ?? "",
                    Severity = severity(),
                    EventClass = eventClass(),
                    EventId = eventId(),
                };
                lock (_lock)
                {
                    if (_messages.Count < MaxMessages) _messages.Add(entry);
                }
            }
            catch { }
        }

        public List<ReadPlcLogEntry> Messages()
        {
            lock (_lock) return _messages.OrderBy(m => m.Timestamp, StringComparer.Ordinal).ToList();
        }

        /// <summary>Add the events the target logged since the given time (UTC), skipping ones already seen live.</summary>
        public void CollectLogged(string amsNetId, DateTime sinceUtc)
        {
            int lcid = CultureInfo.CurrentCulture.LCID;
            TcEventLogger? reader = null;
            try
            {
                reader = new TcEventLogger();
                reader.Connect(amsNetId);
                var events = reader.GetLoggedEvents(500);
                long since = sinceUtc.ToFileTimeUtc();
                int count = events.Count;
                int first = 0;
                try { _ = events[0]; } catch { first = 1; } // Index base differs between proxy versions
                var logged = new List<ReadPlcLogEntry>();
                for (int i = first; i < count + first; i++)
                {
                    var e = events[i];
                    if (e.FileTimeRaised < since) continue;
                    logged.Add(new ReadPlcLogEntry
                    {
                        Timestamp = DateTime.FromFileTimeUtc(e.FileTimeRaised).ToString("O"),
                        Kind = "Logged" + e.EventType,
                        Text = e.GetText(lcid) ?? "",
                        Severity = e.SeverityLevel.ToString().Replace("TCEVENTSEVERITY_", ""),
                        EventClass = e.EventClass.ToString(),
                        EventId = e.EventId,
                    });
                }
                lock (_lock)
                {
                    var seen = new HashSet<string>(_messages.Select(m => m.EventClass + "/" + m.EventId + "/" + m.Text));
                    foreach (var entry in logged)
                    {
                        if (_messages.Count >= MaxMessages) break;
                        if (seen.Add(entry.EventClass + "/" + entry.EventId + "/" + entry.Text)) _messages.Add(entry);
                    }
                }
            }
            catch (Exception ex)
            {
                Error ??= $"The target's logged events could not be read: {ex.Message}";
            }
            finally
            {
                try { reader?.Disconnect(); } catch { }
            }
        }

        public void Dispose()
        {
            try { _logger?.Disconnect(); } catch { }
            _logger = null;
        }
    }
}
