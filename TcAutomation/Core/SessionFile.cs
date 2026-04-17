using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
            string tmp = path + ".tmp";

            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            // Atomic swap so readers never see a half-written file.
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
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

        /// <summary>
        /// True if a process with the given PID exists AND its StartTime matches
        /// the recorded fingerprint (within 1 second tolerance for clock jitter).
        /// This is how we avoid PID-reuse accidents.
        /// </summary>
        public static bool IsProcessAliveAndSame(int pid, string? recordedStartTimeUtc)
        {
            if (pid <= 0) return false;
            try
            {
                var p = Process.GetProcessById(pid);

                if (!string.IsNullOrEmpty(recordedStartTimeUtc))
                {
                    if (DateTime.TryParse(recordedStartTimeUtc, out var recorded))
                    {
                        var actual = p.StartTime.ToUniversalTime();
                        var diff = Math.Abs((actual - recorded.ToUniversalTime()).TotalSeconds);
                        if (diff > 1.0)
                        {
                            // Same PID, different process — PID was reused.
                            return false;
                        }
                    }
                }

                try
                {
                    if (p.HasExited) return false;
                }
                catch { }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Force-kill a process by PID, but only if StartTime matches the
        /// recorded fingerprint. Silently no-ops on failure. Used by the janitor
        /// to reap orphaned DTE / host processes.
        /// </summary>
        public static void SafeKill(int pid, string? recordedStartTimeUtc, string why)
        {
            if (pid <= 0) return;
            if (!IsProcessAliveAndSame(pid, recordedStartTimeUtc)) return;

            try
            {
                var p = Process.GetProcessById(pid);
                Console.Error.WriteLine($"[DEBUG] janitor: killing PID {pid} ({p.ProcessName}) - {why}");
                p.Kill();
                try { p.WaitForExit(5000); } catch { }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DEBUG] janitor: kill PID {pid} failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Scan the session directory and clean up orphaned session files:
        ///   - If mcpPid is dead: kill hostPid + dtePid, delete file.
        ///   - If hostPid is dead (mcp still alive): kill dtePid only, delete file.
        ///
        /// Safe to call from any startup path (MCP server startup, host startup,
        /// even plain CLI invocations if desired).
        /// </summary>
        /// <returns>Number of session files reaped.</returns>
        public static int ReapOrphans()
        {
            int reaped = 0;
            try
            {
                if (!Directory.Exists(SessionDir)) return 0;

                foreach (string path in Directory.GetFiles(SessionDir, "session-*.json"))
                {
                    var sf = TryLoad(path);
                    if (sf == null)
                    {
                        try { File.Delete(path); } catch { }
                        continue;
                    }

                    bool mcpAlive = IsProcessAliveAndSame(sf.McpPid, sf.McpStartTimeUtc);
                    bool hostAlive = IsProcessAliveAndSame(sf.HostPid, sf.HostStartTimeUtc);

                    if (!mcpAlive)
                    {
                        // MCP parent is gone — kill host + DTE.
                        if (hostAlive)
                            SafeKill(sf.HostPid, sf.HostStartTimeUtc, "orphan: MCP parent dead");
                        if (sf.DtePid.HasValue)
                            SafeKill(sf.DtePid.Value, sf.DteStartTimeUtc, "orphan: MCP parent dead");
                        try { File.Delete(path); } catch { }
                        reaped++;
                        continue;
                    }

                    if (!hostAlive)
                    {
                        // MCP is alive, but host died without cleaning up — stale
                        // DTE is now parentless. Kill it and delete the file so
                        // a new host session can start fresh.
                        if (sf.DtePid.HasValue)
                            SafeKill(sf.DtePid.Value, sf.DteStartTimeUtc, "orphan: host dead but MCP alive");
                        try { File.Delete(path); } catch { }
                        reaped++;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DEBUG] janitor: ReapOrphans failed: {ex.Message}");
            }
            return reaped;
        }

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
