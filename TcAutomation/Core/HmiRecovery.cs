#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TcAutomation.Core
{
    /// <summary>The original entry of a mapping a refresh unmaps, kept on disk until the mapping is confirmed
    /// present. It survives a killed shell or worker, so recover mode can write it back.</summary>
    public static class HmiRecovery
    {
        public sealed class Record
        {
            public int Version { get; set; } = 1;
            public string ProjectPath { get; set; } = "";
            public string ConfigPath { get; set; } = "";
            public string Symbol { get; set; } = "";
            public string OriginalEntry { get; set; } = "";
            public DateTime CreatedUtc { get; set; }
            // The worker that wrote it. Its shell and servers die with it (kill-on-close job).
            public int WorkerPid { get; set; }
            public DateTime WorkerStartUtc { get; set; }
        }

        /// <summary>True while another live worker owns the record. A call it started can still change the mapping, so
        /// nobody else may clear or recover it. The current worker's own calls have returned.</summary>
        public static bool OwnerRunning(Record record)
        {
            if (record.WorkerPid == 0) return false;
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            if (record.WorkerPid == self.Id) return false;
            try
            {
                using var owner = System.Diagnostics.Process.GetProcessById(record.WorkerPid);
                return Math.Abs((owner.StartTime.ToUniversalTime() - record.WorkerStartUtc).TotalSeconds) < 1;
            }
            // A reused PID with another start time is not the owner. Gone: not the owner. Not ours to read: assume it is.
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return true; }
        }

        public enum Verdict { Restore, AlreadyPresent, Reconcile, Conflict }

        /// <summary>Serializes refresh and recovery of one configuration across workers. Null when another
        /// holder has it. The OS releases it when the worker dies.</summary>
        public static FileStream? Lock(string stateDirectory, string configPath)
        {
            var path = Path.ChangeExtension(PathFor(stateDirectory, configPath), ".lock");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { return null; }
        }

        public static string Folder(string stateDirectory) => Path.Combine(stateDirectory, "hmi-recovery");

        /// <summary>One record per configuration file, named by a hash of its full path.</summary>
        public static string PathFor(string stateDirectory, string configPath)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(configPath).ToUpperInvariant()));
            return Path.Combine(Folder(stateDirectory), BitConverter.ToString(hash, 0, 12).Replace("-", "").ToLowerInvariant() + ".json");
        }

        /// <summary>Write through a temporary file and a rename, so a reader never sees a partial record.</summary>
        public static void Write(string stateDirectory, Record record)
        {
            using (var self = System.Diagnostics.Process.GetCurrentProcess())
                if (record.WorkerPid == 0) { record.WorkerPid = self.Id; record.WorkerStartUtc = self.StartTime.ToUniversalTime(); }
            var path = PathFor(stateDirectory, record.ConfigPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(record), new UTF8Encoding(false));
            try
            {
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        /// <summary>The record for this configuration. Null if there is none. Throws if it cannot be read or does not match.</summary>
        public static Record? Read(string stateDirectory, string configPath)
        {
            var path = PathFor(stateDirectory, configPath);
            if (!File.Exists(path)) return null;
            Record? record;
            try { record = JsonSerializer.Deserialize<Record>(File.ReadAllText(path)); }
            catch (JsonException) { throw new InvalidOperationException("hmi_recovery_unreadable"); }
            if (record == null || record.Version != 1 || string.IsNullOrEmpty(record.Symbol) || !IsObject(record.OriginalEntry)
                || !string.Equals(Path.GetFullPath(record.ConfigPath), Path.GetFullPath(configPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("hmi_recovery_unreadable");
            return record;
        }

        static bool IsObject(string json)
        {
            try { using var doc = JsonDocument.Parse(json ?? ""); return doc.RootElement.ValueKind == JsonValueKind.Object; }
            catch (JsonException) { return false; }
        }

        public static void Clear(string stateDirectory, string configPath)
        {
            var path = PathFor(stateDirectory, configPath);
            if (File.Exists(path)) File.Delete(path);
        }

        /// <summary>What recover mode does with the current entry (null: absent). An entry on the original DOMAIN
        /// and MAPPING is a finished remap: reconcile it by setting the preserved fields back.</summary>
        public static Verdict Decide(string originalEntry, string? currentEntry)
        {
            if (currentEntry == null) return Verdict.Restore;
            if (HmiMappings.Same(originalEntry, currentEntry)) return Verdict.AlreadyPresent;
            string? domain = HmiMappings.Field(originalEntry, "DOMAIN"), target = HmiMappings.Field(originalEntry, "MAPPING");
            return domain != null && target != null && HmiMappings.SameTarget(currentEntry, domain, target) ? Verdict.Reconcile : Verdict.Conflict;
        }

        /// <summary>A stopped refresh may clear its record only when no call can still be running, and the mapping
        /// is present as it was or as the refresh verified it.</summary>
        public static bool CanClear(string originalEntry, string? currentEntry, string? verifiedEntry, bool outcomeCertain) =>
            outcomeCertain && currentEntry != null
            && (HmiMappings.Same(originalEntry, currentEntry) || verifiedEntry != null && HmiMappings.Same(verifiedEntry, currentEntry));

        /// <summary>Server definitions the entry's SCHEMA needs that the configuration lacks, following references
        /// between definitions ("#/definitions/X" inside DEFINITIONS).</summary>
        public static string[] MissingDefinitions(string entry, JsonElement config)
        {
            using var doc = JsonDocument.Parse(entry);
            if (!doc.RootElement.TryGetProperty("SCHEMA", out var schema)) return Array.Empty<string>();
            var definitions = config.TryGetProperty("DEFINITIONS", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
            var missing = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            var queue = new System.Collections.Generic.Queue<string>(References(schema, ServerDefinitions));
            while (queue.Count > 0)
            {
                var name = queue.Dequeue();
                if (!seen.Add(name)) continue;
                if (definitions.ValueKind != JsonValueKind.Object || !definitions.TryGetProperty(name, out var body)) { missing.Add(name); continue; }
                foreach (var next in References(body, ServerDefinitions).Concat(References(body, LocalDefinitions))) queue.Enqueue(next);
            }
            return missing.ToArray();
        }

        const string ServerDefinitions = "tchmi:server#/definitions/", LocalDefinitions = "#/definitions/";

        static System.Collections.Generic.IEnumerable<string> References(JsonElement e, string prefix)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                    {
                        if (p.Name == "$ref" && p.Value.ValueKind == JsonValueKind.String
                            && p.Value.GetString()!.StartsWith(prefix, StringComparison.Ordinal))
                            yield return p.Value.GetString()!.Substring(prefix.Length);
                        foreach (var r in References(p.Value, prefix)) yield return r;
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray())
                        foreach (var r in References(item, prefix)) yield return r;
                    break;
            }
        }
    }
}
