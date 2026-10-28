using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TcAutomation.Core
{
    /// <summary>
    /// Describes a persistent host session tied to a specific MCP server process.
    /// Serialized to %LOCALAPPDATA%\twincat-mcp\session-&lt;mcpPid&gt;.json.
    ///
    /// The session file is the out-of-band contract that lets us clean up after
    /// ANY combination of crashes:
    ///   - MCP dies → janitor kills hostPid + dtePid
    ///   - host dies but MCP alive → janitor kills dtePid
    ///   - MCP + host both die but DTE survives (COM-activated process) → janitor
    ///     kills dtePid based on the stale file
    ///
    /// Includes start-time fingerprints on the recorded PIDs so PID reuse (same
    /// PID assigned to a completely different process later) can't cause us to
    /// kill something innocent.
    /// </summary>
    public class SessionFile
    {
        [JsonPropertyName("mcpPid")]
        public int McpPid { get; set; }

        [JsonPropertyName("hostPid")]
        public int HostPid { get; set; }

        [JsonPropertyName("dtePid")]
        public int? DtePid { get; set; }

        [JsonPropertyName("mcpStartTimeUtc")]
        public string? McpStartTimeUtc { get; set; }

        [JsonPropertyName("hostStartTimeUtc")]
        public string? HostStartTimeUtc { get; set; }

        [JsonPropertyName("dteStartTimeUtc")]
        public string? DteStartTimeUtc { get; set; }

        [JsonPropertyName("sessionStartedUtc")]
        public string SessionStartedUtc { get; set; } = DateTime.UtcNow.ToString("O");

        [JsonPropertyName("solutionPath")]
        public string? SolutionPath { get; set; }

        [JsonPropertyName("tcVersion")]
        public string? TcVersion { get; set; }

        [JsonPropertyName("hostExecutable")]
        public string? HostExecutable { get; set; }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Directory holding all session files.
        /// </summary>
        public static string SessionDir
        {
            get
            {
                string basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(basePath))
                {
                    basePath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "AppData", "Local");
                }
                return Path.Combine(basePath, "twincat-mcp");
            }
        }

        public static string PathFor(int mcpPid) => Path.Combine(SessionDir, $"session-{mcpPid}.json");

        public void Save()
        {
            Directory.CreateDirectory(SessionDir);
            string path = PathFor(McpPid);
            // Per-writer temp names stop writers for one MCP PID clobbering each other.
            string tmp = path + "." + Process.GetCurrentProcess().Id
                       + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";

            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));

                // File.Replace swaps content atomically, but a concurrent
                // File.Exists can see the file absent mid-swap (measured). Readers
                // must treat "missing" as "look again later", never as "removed".
                // Replace also fails while a reader lacks delete sharing: retry.
                IOException? last = null;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        if (File.Exists(path))
                            File.Replace(tmp, path, null);
                        else
                            File.Move(tmp, path);
                        return;
                    }
                    catch (FileNotFoundException)
                    {
                        // Probably the destination vanished after Exists. Check again.
                        last = null;
                    }
                    catch (IOException ex)
                    {
                        last = ex;
                    }
                    System.Threading.Thread.Sleep(20 * (attempt + 1));
                }
                throw last ?? new IOException($"Could not publish {path}.");
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        public static SessionFile? TryLoad(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<SessionFile>(json);
            }
            catch
            {
                return null;
            }
        }

        public enum LoadOutcome
        {
            /// <summary>Read and deserialized.</summary>
            Ok,
            /// <summary>Not found, possibly mid-Replace. Nothing to act on.</summary>
            Missing,
            /// <summary>Open or read failed: sharing violation, ACL, IO error.</summary>
            Unreadable,
            /// <summary>Not valid JSON for this type, or JSON null.</summary>
            Malformed,
        }

        /// <summary>
        /// Like TryLoad, but says why a load failed. An unparseable record can
        /// still name live processes, so the sweep must not delete it.
        /// </summary>
        public static SessionFile? TryLoadStrict(string path, out LoadOutcome outcome)
        {
            // A concurrent Save() briefly blocks reads (~0.5% measured). Retry first.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var sf = TryLoadOnce(path, out outcome);
                if (outcome != LoadOutcome.Unreadable) return sf;
                System.Threading.Thread.Sleep(20);
            }
            return TryLoadOnce(path, out outcome);
        }

        private static SessionFile? TryLoadOnce(string path, out LoadOutcome outcome)
        {
            try
            {
                // Not File.ReadAllText: it omits FileShare.Delete, so a concurrent
                // Save() can fail its Replace and leave the DTE PID unrecorded.
                string json;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(fs))
                {
                    json = reader.ReadToEnd();
                }
                var sf = JsonSerializer.Deserialize<SessionFile>(json);
                outcome = sf == null ? LoadOutcome.Malformed : LoadOutcome.Ok;
                return sf;
            }
            catch (JsonException)
            {
                outcome = LoadOutcome.Malformed;
                return null;
            }
            catch (FileNotFoundException)
            {
                // Removed by its owner, or mid-Replace. Not evidence of anything.
                outcome = LoadOutcome.Missing;
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                outcome = LoadOutcome.Missing;
                return null;
            }
            catch
            {
                outcome = LoadOutcome.Unreadable;
                return null;
            }
        }

        private static bool TryDelete(string path, ReapReport report)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception ex)
            {
                // Not Unresolved: the host and DTE are gone, only the file is left.
                report.DeleteFailed++;
                report.Notes.Add($"{path}: delete failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static bool SameProcesses(SessionFile a, SessionFile b)
            => a.McpPid == b.McpPid
               && a.HostPid == b.HostPid
               && a.DtePid == b.DtePid
               && string.Equals(a.McpStartTimeUtc, b.McpStartTimeUtc, StringComparison.Ordinal)
               && string.Equals(a.HostStartTimeUtc, b.HostStartTimeUtc, StringComparison.Ordinal)
               && string.Equals(a.DteStartTimeUtc, b.DteStartTimeUtc, StringComparison.Ordinal);

        public static void Delete(int mcpPid)
        {
            try
            {
                string path = PathFor(mcpPid);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        /// <summary>
        /// Capture the current UTC start time of a process as an ISO-8601 string,
        /// so we can later verify the PID hasn't been reassigned.
        /// </summary>
        public static string? TryGetProcessStartTime(int pid)
        {
            try
            {
                var p = Process.GetProcessById(pid);
                return p.StartTime.ToUniversalTime().ToString("O");
            }
            catch
            {
                return null;
            }
        }

        public enum ProcessIdentity
        {
            /// <summary>Our process: the PID exists and the fingerprint matches.</summary>
            Same,
            /// <summary>The recorded process is gone: no such PID, or it exited.</summary>
            Gone,
            /// <summary>The fingerprint differs: another process owns the PID.</summary>
            PidReused,
            /// <summary>Identity unverified. Never act on it.</summary>
            Unverified,
        }

        /// <summary>
        /// Parse a fingerprint written by TryGetProcessStartTime ("O" format).
        /// Never add AssumeUniversal or AssumeLocal: with RoundtripKind, .NET
        /// throws ArgumentException, which aborts the sweep.
        /// </summary>
        private static bool TryParseFingerprint(string recorded, out DateTime utc)
        {
            utc = default;
            // Exact. TryParse reads a bare "08:00:00" as today, which can make
            // a live parent look reused and license killing its children.
            if (DateTime.TryParseExact(recorded, "O",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var parsed))
            {
                utc = parsed.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
                    : parsed.ToUniversalTime();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Classify a recorded PID. When identity cannot be settled either way,
        /// the result is Unverified, never a guess.
        /// </summary>
        public static ProcessIdentity ClassifyProcess(int pid, string? recordedStartTimeUtc)
        {
            if (pid <= 0) return ProcessIdentity.Gone;

            Process p;
            try
            {
                p = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                // Documented for "no process with that Id". The PID is free.
                return ProcessIdentity.Gone;
            }
            catch
            {
                return ProcessIdentity.Unverified;
            }

            // HasExited needs more rights than StartTime. If it is denied, a
            // different start time still proves the PID was reused.
            bool exitedUnknown = false;
            try
            {
                if (p.HasExited) return ProcessIdentity.Gone;
            }
            catch
            {
                exitedUnknown = true;
            }

            if (string.IsNullOrEmpty(recordedStartTimeUtc)) return ProcessIdentity.Unverified;
            if (!TryParseFingerprint(recordedStartTimeUtc!, out var recorded))
                return ProcessIdentity.Unverified;

            try
            {
                var actual = p.StartTime.ToUniversalTime();
                // Exact: both come from the same FILETIME, lossless through "O".
                if (actual != recorded) return ProcessIdentity.PidReused;
                return exitedUnknown ? ProcessIdentity.Unverified : ProcessIdentity.Same;
            }
            catch
            {
                // StartTime unreadable. Identity unproven.
                return ProcessIdentity.Unverified;
            }
        }

        public static bool IsProcessAliveAndSame(int pid, string? recordedStartTimeUtc)
            => ClassifyProcess(pid, recordedStartTimeUtc) == ProcessIdentity.Same;

        public enum KillOutcome
        {
            /// <summary>Already gone. Nothing terminated.</summary>
            AlreadyGone,
            /// <summary>Terminated, exit confirmed.</summary>
            Killed,
            /// <summary>PID reused. Left alone.</summary>
            SkippedPidReused,
            /// <summary>Identity unverified. Left alone.</summary>
            SkippedUnverified,
            /// <summary>Exit unconfirmed: open, kill or wait failed. It can still run.</summary>
            Failed,
        }

        /// <summary>The kill outcome for a non-Same identity. Never pass Same.</summary>
        private static KillOutcome OutcomeFor(ProcessIdentity identity)
        {
            switch (identity)
            {
                case ProcessIdentity.Gone: return KillOutcome.AlreadyGone;
                case ProcessIdentity.PidReused: return KillOutcome.SkippedPidReused;
                default: return KillOutcome.SkippedUnverified;
            }
        }

        /// <summary>
        /// Kill a process only when its fingerprint proves it is the recorded
        /// one. Killed, AlreadyGone and SkippedPidReused mean the recorded
        /// process is gone. Failed and SkippedUnverified do not.
        /// </summary>
        public static KillOutcome SafeKill(int pid, string? recordedStartTimeUtc, string why)
        {
            var identity = ClassifyProcess(pid, recordedStartTimeUtc);
            if (identity != ProcessIdentity.Same)
            {
                if (identity != ProcessIdentity.Gone)
                    Console.Error.WriteLine($"[DEBUG] janitor: PID {pid} is {identity}; leaving it alone");
                return OutcomeFor(identity);
            }

            // One native handle across check, kill and wait. An open handle
            // pins the PID, so it cannot be reused mid-kill. Process on .NET
            // Framework reopens by PID for HasExited, StartTime and Kill.
            if (!TryParseFingerprint(recordedStartTimeUtc!, out var recorded))
                return KillOutcome.SkippedUnverified;

            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE | SYNCHRONIZE,
                false, (uint)pid);
            if (handle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                // ERROR_INVALID_PARAMETER: no process holds this PID now.
                if (error == ERROR_INVALID_PARAMETER) return KillOutcome.AlreadyGone;
                Console.Error.WriteLine($"[DEBUG] janitor: cannot open PID {pid} (error {error}); nothing was stopped");
                return KillOutcome.Failed;
            }

            try
            {
                if (WaitForSingleObject(handle, 0) == WAIT_OBJECT_0) return KillOutcome.AlreadyGone;

                if (!GetProcessTimes(handle, out long created, out _, out _, out _))
                {
                    Console.Error.WriteLine($"[DEBUG] janitor: PID {pid} start time unreadable at the kill; leaving it alone");
                    return KillOutcome.SkippedUnverified;
                }
                // Same conversion as Process.StartTime, so it compares exactly.
                var actual = DateTime.FromFileTime(created).ToUniversalTime();
                if (actual != recorded)
                {
                    Console.Error.WriteLine($"[DEBUG] janitor: PID {pid} is PidReused at the kill; leaving it alone");
                    return KillOutcome.SkippedPidReused;
                }

                Console.Error.WriteLine($"[DEBUG] janitor: killing PID {pid} - {why}");
                if (!TerminateProcess(handle, 1))
                {
                    // It can have exited on its own after the checks above.
                    if (WaitForSingleObject(handle, 0) == WAIT_OBJECT_0) return KillOutcome.AlreadyGone;
                    Console.Error.WriteLine($"[DEBUG] janitor: TerminateProcess PID {pid} failed (error {Marshal.GetLastWin32Error()})");
                    return KillOutcome.Failed;
                }
                if (WaitForSingleObject(handle, 5000) == WAIT_OBJECT_0) return KillOutcome.Killed;
                Console.Error.WriteLine($"[DEBUG] janitor: PID {pid} did not exit within 5s");
                return KillOutcome.Failed;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private const uint PROCESS_TERMINATE = 0x0001;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint SYNCHRONIZE = 0x00100000;
        private const uint WAIT_OBJECT_0 = 0x00000000;
        private const int ERROR_INVALID_PARAMETER = 87;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(IntPtr hProcess, out long lpCreationTime, out long lpExitTime,
            out long lpKernelTime, out long lpUserTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        public class ReapReport
        {
            /// <summary>Records this sweep deleted: their host and DTE are gone.</summary>
            public int Cleaned { get; set; }
            /// <summary>Processes this sweep terminated and confirmed exited.</summary>
            public int Stopped { get; set; }
            /// <summary>
            /// Records kept for a later sweep: a process not confirmed gone, or a
            /// record that is unreadable, invalid or changed mid-sweep.
            /// </summary>
            public int Unresolved { get; set; }
            /// <summary>Records whose delete threw. Their host and DTE are gone.</summary>
            public int DeleteFailed { get; set; }
            /// <summary>The sweep threw. The counts are partial.</summary>
            public bool Aborted { get; set; }
            public string? AbortReason { get; set; }
            /// <summary>
            /// One path-prefixed line per event: a kept record and why, a failed
            /// delete, a record changed or gone mid-sweep. One more line on abort.
            /// </summary>
            public List<string> Notes { get; set; } = new List<string>();
        }

        /// <summary>
        /// Scan the session directory and clean up orphaned sessions.
        ///   - If mcpPid is not alive, the host and the DTE are orphans.
        ///   - If mcpPid is alive but hostPid is not, the DTE is parentless.
        ///
        /// A record is deleted only when its host and DTE are confirmed gone (a
        /// reused PID counts as gone) and a re-read after the kills still names
        /// the same PIDs and fingerprints. Anything else keeps it: it is the
        /// only thing marking those processes as ours.
        ///
        /// Safe to call from any startup path.
        /// </summary>
        public static ReapReport ReapOrphansDetailed()
        {
            var report = new ReapReport();
            try
            {
                if (!Directory.Exists(SessionDir)) return report;

                foreach (string path in Directory.GetFiles(SessionDir, "session-*.json"))
                {
                    var sf = TryLoadStrict(path, out var outcome);
                    if (sf == null)
                    {
                        // An unreadable or unparseable record can still name
                        // live processes. Keep it and report it.
                        if (outcome == LoadOutcome.Missing) continue;
                        report.Unresolved++;
                        report.Notes.Add(
                            $"{path}: {outcome}; left in place, no process was checked");
                        continue;
                    }

                    // JSON defaults a missing mcpPid or hostPid to 0, which classifies
                    // as Gone and would kill a healthy session's children.
                    if (sf.McpPid <= 0 || sf.HostPid <= 0
                        || (sf.DtePid.HasValue && sf.DtePid.Value <= 0))
                    {
                        string dte = sf.DtePid.HasValue ? sf.DtePid.Value.ToString() : "none";
                        report.Unresolved++;
                        report.Notes.Add(
                            $"{path}: records a non-positive PID (mcp={sf.McpPid}, "
                            + $"host={sf.HostPid}, dte={dte}); left in place, nothing was stopped");
                        continue;
                    }

                    var mcp = ClassifyProcess(sf.McpPid, sf.McpStartTimeUtc);
                    var host = ClassifyProcess(sf.HostPid, sf.HostStartTimeUtc);

                    // Only Gone and PidReused prove an owner gone. Acting on
                    // Unverified can kill a live MCP's children.
                    if (mcp == ProcessIdentity.Unverified)
                    {
                        report.Unresolved++;
                        report.Notes.Add($"{path}: MCP PID {sf.McpPid} identity unverified; nothing was stopped");
                        continue;
                    }

                    string? why = null;
                    if (mcp != ProcessIdentity.Same)
                        why = "orphan: MCP parent not alive";
                    else if (host == ProcessIdentity.Unverified)
                    {
                        report.Unresolved++;
                        report.Notes.Add($"{path}: host PID {sf.HostPid} identity unverified; nothing was stopped");
                        continue;
                    }
                    else if (host != ProcessIdentity.Same)
                        why = "orphan: host not alive but MCP alive";

                    if (why == null) continue;   // MCP and host live: not an orphan.

                    var outcomes = new List<(string Label, int Pid, KillOutcome Outcome)>();

                    // Kill the host only when its MCP is gone.
                    if (mcp != ProcessIdentity.Same)
                        outcomes.Add(("host", sf.HostPid, SafeKill(sf.HostPid, sf.HostStartTimeUtc, why)));

                    if (sf.DtePid.HasValue)
                        outcomes.Add(("dte", sf.DtePid.Value, SafeKill(sf.DtePid.Value, sf.DteStartTimeUtc, why)));

                    report.Stopped += outcomes.Count(o => o.Outcome == KillOutcome.Killed);

                    // A reused PID is not ours, so it does not block cleanup.
                    var blocking = outcomes
                        .Where(o => o.Outcome == KillOutcome.Failed || o.Outcome == KillOutcome.SkippedUnverified)
                        .ToList();

                    if (blocking.Count > 0)
                    {
                        report.Unresolved++;
                        foreach (var b in blocking)
                            report.Notes.Add($"{path}: {b.Label} PID {b.Pid} {b.Outcome}");
                        continue;
                    }

                    // Each kill waits up to 5 s. Meanwhile a new host can replace
                    // this record, or the host can add its new DTE before its kill
                    // lands. Re-read, and delete only the record that was checked.
                    var current = TryLoadStrict(path, out var reread);
                    if (current == null)
                    {
                        // Missing: removed by another process, or mid-Replace.
                        // Not ours to delete or count.
                        if (reread != LoadOutcome.Missing) report.Unresolved++;
                        report.Notes.Add($"{path}: {reread} on re-read; not deleted");
                        continue;
                    }
                    if (!SameProcesses(sf, current))
                    {
                        // A healthy replacement or a live orphan's record. The
                        // sweep cannot tell, so report it.
                        report.Unresolved++;
                        report.Notes.Add($"{path}: changed during the sweep; left for the next sweep");
                        continue;
                    }

                    // A short gap remains before the delete, with no kill or wait.
                    if (TryDelete(path, report)) report.Cleaned++;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DEBUG] janitor: ReapOrphans failed: {ex.Message}");
                report.Aborted = true;
                report.AbortReason = $"{ex.GetType().Name}: {ex.Message}";
                report.Notes.Add($"sweep aborted: {report.AbortReason}");
            }
            return report;
        }

        /// <summary>
        /// Backwards-compatible: records removed, not processes stopped. Use
        /// ReapOrphansDetailed to report to a user.
        /// </summary>
        public static int ReapOrphans() => ReapOrphansDetailed().Cleaned;

        /// <summary>
        /// List currently-active session files (those whose mcpPid is alive).
        /// Used by twincat_kill_stale to know which DTE/host PIDs are legitimate
        /// and must be left alone.
        /// </summary>
        public static List<SessionFile> ListActive()
        {
            var result = new List<SessionFile>();
            try
            {
                if (!Directory.Exists(SessionDir)) return result;
                foreach (string path in Directory.GetFiles(SessionDir, "session-*.json"))
                {
                    var sf = TryLoad(path);
                    if (sf == null) continue;
                    if (IsProcessAliveAndSame(sf.McpPid, sf.McpStartTimeUtc))
                    {
                        result.Add(sf);
                    }
                }
            }
            catch { }
            return result;
        }
    }
}
