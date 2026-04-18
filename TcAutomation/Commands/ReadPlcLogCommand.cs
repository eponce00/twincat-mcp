using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using TcEventLoggerAdsProxyLib;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Tail the TwinCAT event log on a target PLC over ADS (no VS required).
    ///
    /// Subscribes to the TcEventLogger on the target for `waitSeconds`,
    /// collects every message and alarm it emits during that window,
    /// optionally filters by `contains`, and returns them. This is the
    /// agent's escape hatch when the VS Error List is unreachable (e.g.,
    /// no solution loaded, target just rebooted, or trying to diagnose a
    /// runtime/OS crash). AdsLogStr() output lands here, as do _Raise
    /// alarms and any framework-level TwinCAT event messages.
    ///
    /// Implementation note: uses the Beckhoff-provided
    /// `Beckhoff.TwinCAT.TcEventLoggerAdsProxy.Net` COM wrapper, which
    /// connects via ADS port 110 under the hood. Events fire on a
    /// background COM thread; we marshal them through a simple lock and
    /// a `ManualResetEventSlim` so the main thread can wait for the
    /// window to expire cleanly.
    /// </summary>
    public static class ReadPlcLogCommand
    {
        public static int Execute(string amsNetId, int waitSeconds, string? contains, int maxResults)
        {
            var result = new ReadPlcLogResult
            {
                AmsNetId = amsNetId,
                WaitSeconds = waitSeconds,
                Contains = contains ?? "",
                MaxResults = maxResults,
            };

            TcEventLogger? logger = null;
            var lockObj = new object();
            int lcid = CultureInfo.CurrentCulture.LCID;
            string containsLower = (contains ?? "").ToLowerInvariant();
            bool hasFilter = containsLower.Length > 0;

            try
            {
                logger = new TcEventLogger();

                void Append(LogEntryKind kind, string text, string? severity, string? eventClass, uint? eventId)
                {
                    if (string.IsNullOrEmpty(text)) return;
                    if (hasFilter && !text.ToLowerInvariant().Contains(containsLower)) return;

                    lock (lockObj)
                    {
                        if (result.Events.Count >= maxResults)
                        {
                            result.Truncated = true;
                            return;
                        }
                        result.Events.Add(new ReadPlcLogEntry
                        {
                            Timestamp = DateTime.UtcNow.ToString("O"),
                            Kind = kind.ToString(),
                            Severity = severity,
                            EventClass = eventClass,
                            EventId = eventId,
                            Text = text,
                        });
                    }
                }

                logger.MessageSent += (TcMessage m) =>
                {
                    try
                    {
                        Append(LogEntryKind.Message,
                            m.GetText(lcid) ?? "",
                            m.SeverityLevel.ToString(),
                            m.EventClass.ToString(),
                            m.EventId);
                    }
                    catch { }
                };

                logger.AlarmRaised += (TcAlarm a) =>
                {
                    try
                    {
                        Append(LogEntryKind.AlarmRaised,
                            a.GetText(lcid) ?? "",
                            a.SeverityLevel.ToString(),
                            a.EventClass.ToString(),
                            a.EventId);
                    }
                    catch { }
                };

                logger.AlarmCleared += (TcAlarm a, bool confirmed) =>
                {
                    try
                    {
                        Append(LogEntryKind.AlarmCleared,
                            a.GetText(lcid) ?? "",
                            a.SeverityLevel.ToString(),
                            a.EventClass.ToString(),
                            a.EventId);
                    }
                    catch { }
                };

                logger.AlarmConfirmed += (TcAlarm a, bool cleared) =>
                {
                    try
                    {
                        Append(LogEntryKind.AlarmConfirmed,
                            a.GetText(lcid) ?? "",
                            a.SeverityLevel.ToString(),
                            a.EventClass.ToString(),
                            a.EventId);
                    }
                    catch { }
                };

                // Connect — the AMS Net ID argument is optional in the
                // COM API (omitted = localhost); we always pass an
                // explicit target.
                logger.Connect(amsNetId);

                // Listen for the configured window. COM events fire on
                // their own thread, so a plain Sleep is fine here; we
                // don't need a signal.
                int totalMs = Math.Max(1, waitSeconds) * 1000;
                Thread.Sleep(totalMs);

                result.Success = true;
                result.TotalCaptured = result.Events.Count;
                Emit(result);
                return 0;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Failed to read PLC log: {ex.Message}";
                Emit(result);
                return 1;
            }
            finally
            {
                if (logger != null)
                {
                    try { logger.Disconnect(); } catch { }
                }
            }
        }

        private static void Emit(ReadPlcLogResult r)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                r, new JsonSerializerOptions { WriteIndented = true }));
        }

        private enum LogEntryKind
        {
            Message,
            AlarmRaised,
            AlarmCleared,
            AlarmConfirmed,
        }
    }

    public class ReadPlcLogResult
    {
        public string AmsNetId { get; set; } = "";
        public int WaitSeconds { get; set; }
        public string Contains { get; set; } = "";
        public int MaxResults { get; set; }
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public int TotalCaptured { get; set; }
        public bool Truncated { get; set; }
        public List<ReadPlcLogEntry> Events { get; set; } = new List<ReadPlcLogEntry>();
    }

    public class ReadPlcLogEntry
    {
        public string Timestamp { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? Severity { get; set; }
        public string? EventClass { get; set; }
        public uint? EventId { get; set; }
        public string Text { get; set; } = "";
    }
}
