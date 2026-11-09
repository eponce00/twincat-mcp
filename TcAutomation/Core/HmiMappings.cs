#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TcAutomation.Core
{
    /// <summary>Pure mapping-refresh policy over the HMI server configuration. No native calls.</summary>
    public static class HmiMappings
    {
        public const int MaxMappings = 2000;
        // AddSymbol rewrites only NAME/ACCESS/AUTOMAP/DOMAIN/MAPPING/USEMAPPING; these are restored after a remap.
        public static readonly string[] PreservedFields = { "ACCESS", "HIDDEN", "OPTIONS", "CUSTOMERDATA", "DYNAMIC", "USEMAPPING" };
        static readonly Regex Segment = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*(\[\d+(,\d+)*\])*$");

        public sealed class Runtime { public string NetId = ""; public int Port; }
        public sealed class Mapping
        {
            public string Name = "", Domain = "", Target = "", Runtime = "", AdsPath = "", Entry = "";
        }
        public sealed class Plan
        {
            public List<Mapping> Eligible = new List<Mapping>();
            public List<KeyValuePair<string, string>> Skipped = new List<KeyValuePair<string, string>>();
            public Dictionary<string, Runtime> Runtimes = new Dictionary<string, Runtime>(StringComparer.Ordinal);
        }

        public static string ConfigPath(string projectDirectory) =>
            Path.Combine(projectDirectory, "Server", "TcHmiSrv", "TcHmiSrv.Config.default.json");

        public static JsonDocument Read(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 32 * 1024 * 1024) throw new InvalidOperationException("hmi_mapping_config_invalid");
            // The server rewrites this file; share access and accept its BOM.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            JsonDocument doc;
            try { doc = JsonDocument.Parse(stream); }
            catch (JsonException) { throw new InvalidOperationException("hmi_mapping_config_invalid"); }
            // Lookups take the last of duplicated sections, so a root with duplicates is never trusted.
            var names = doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.EnumerateObject().Select(p => p.Name).ToList() : null;
            if (names == null || names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            {
                doc.Dispose();
                throw new InvalidOperationException("hmi_mapping_config_invalid");
            }
            return doc;
        }

        /// <summary>Selects directly mapped ADS PLC symbols. Function, TMC and sub-symbol mappings are skipped.</summary>
        public static Plan Select(string projectDirectory, JsonElement config, IReadOnlyCollection<string>? requested)
        {
            var plan = new Plan();
            if (!config.TryGetProperty("SYMBOLS", out var symbols) || symbols.ValueKind != JsonValueKind.Object
                || !config.TryGetProperty("EXTENSIONS", out var extensions) || extensions.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("hmi_mapping_config_invalid");
            var adsDomains = new Dictionary<string, Dictionary<string, Runtime>>(StringComparer.Ordinal);
            foreach (var ext in extensions.EnumerateObject())
                if (Str(ext.Value, "NAME") == "TcHmiAds" && Bool(ext.Value, "ENABLED") != false)
                    adsDomains[ext.Name] = Runtimes(projectDirectory, ext.Name);
            var names = requested ?? symbols.EnumerateObject().Select(p => p.Name).ToList();
            if (names.Count > MaxMappings) throw new InvalidOperationException("hmi_mapping_limit");
            foreach (var name in names)
            {
                if (!symbols.TryGetProperty(name, out var entry) || entry.ValueKind != JsonValueKind.Object)
                { plan.Skipped.Add(Skip(name, "not_mapped")); continue; }
                var domain = Str(entry, "DOMAIN") ?? "";
                var target = Str(entry, "MAPPING") ?? "";
                string? reason = null;
                if (!adsDomains.ContainsKey(domain)) reason = domain == "TMC" ? "tmc" : "not_ads";
                else if (!target.Contains("::")) reason = "not_plc_symbol";
                else if (entry.TryGetProperty("SUBSYMBOL_MAPPING", out _)) reason = "subsymbol";
                if (reason != null)
                {
                    if (requested != null) plan.Skipped.Add(Skip(name, reason));
                    continue;
                }
                var parts = target.Split(new[] { "::" }, StringSplitOptions.None);
                // Never touched, so skipping is safe; the result names each one.
                if (parts.Length < 2 || !parts.All(Segment.IsMatch) || parts[0].Contains("["))
                { plan.Skipped.Add(Skip(name, "unsupported_target")); continue; }
                if (!adsDomains[domain].TryGetValue(parts[0], out var runtime))
                { plan.Skipped.Add(Skip(name, "runtime_not_configured")); continue; }
                plan.Runtimes[domain + "\0" + parts[0]] = runtime;
                plan.Eligible.Add(new Mapping
                {
                    Name = name, Domain = domain, Target = target, Runtime = domain + "\0" + parts[0],
                    AdsPath = string.Join(".", parts.Skip(1)), Entry = entry.GetRawText()
                });
            }
            if (plan.Eligible.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count() != plan.Eligible.Count)
                throw new InvalidOperationException("hmi_mapping_duplicate");
            return plan;
        }

        static Dictionary<string, Runtime> Runtimes(string projectDirectory, string domain)
        {
            if (domain.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || domain.StartsWith("."))
                throw new InvalidOperationException("hmi_mapping_config_invalid");
            var result = new Dictionary<string, Runtime>(StringComparer.Ordinal);
            using var doc = Read(Path.Combine(projectDirectory, "Server", domain, domain + ".Config.default.json"));
            if (!doc.RootElement.TryGetProperty("RUNTIMES", out var runtimes) || runtimes.ValueKind != JsonValueKind.Object)
                return result;
            foreach (var rt in runtimes.EnumerateObject())
            {
                var netId = Str(rt.Value, "NETID");
                if (Bool(rt.Value, "ENABLED") == false || netId == null
                    || !rt.Value.TryGetProperty("PORT", out var port) || !port.TryGetInt32(out var p) || p <= 0 || p > 65535)
                    continue;
                result[rt.Name] = new Runtime { NetId = netId, Port = p };
            }
            return result;
        }

        /// <summary>The refreshed entry with each preserved field the original had set back to its original value.</summary>
        public static string Merge(string original, string refreshed)
        {
            using var o = JsonDocument.Parse(original);
            using var r = JsonDocument.Parse(refreshed);
            var kept = o.RootElement.EnumerateObject().Where(p => PreservedFields.Contains(p.Name, StringComparer.Ordinal)).ToList();
            var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                // A field the original lacked keeps the server's value: dropping a server default is not measured.
                foreach (var p in r.RootElement.EnumerateObject())
                    if (!kept.Any(k => k.Name == p.Name)) p.WriteTo(w);
                foreach (var p in kept) p.WriteTo(w);
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        public static bool Same(JsonElement a, JsonElement b)
        {
            if (a.ValueKind != b.ValueKind) return false;
            switch (a.ValueKind)
            {
                case JsonValueKind.Object:
                    var pa = a.EnumerateObject().ToList();
                    var lb = b.EnumerateObject().ToList();
                    var pb = lb.GroupBy(p => p.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
                    // Duplicate keys are ambiguous: never equal.
                    if (pb.Count != lb.Count || pa.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != pa.Count) return false;
                    return pa.Count == pb.Count && pa.All(p => pb.TryGetValue(p.Name, out var v) && Same(p.Value, v));
                case JsonValueKind.Array:
                    var ea = a.EnumerateArray().ToList();
                    var eb = b.EnumerateArray().ToList();
                    return ea.Count == eb.Count && ea.Zip(eb, Same).All(x => x);
                case JsonValueKind.String: return a.GetString() == b.GetString();
                // Exact: double or decimal conversion would equate distinct numbers.
                case JsonValueKind.Number: return Canonical(a.GetRawText()) == Canonical(b.GetRawText());
                default: return true;
            }
        }

        public static bool Same(string a, string b)
        {
            using var x = JsonDocument.Parse(a);
            using var y = JsonDocument.Parse(b);
            return Same(x.RootElement, y.RootElement);
        }

        /// <summary>Names of top-level members of <paramref name="key"/> that differ between two configurations.</summary>
        public static List<string> Changed(JsonElement before, JsonElement after, string key)
        {
            var a = Members(before, key);
            var b = Members(after, key);
            // A duplicated name is ambiguous, so it always counts as changed.
            return a.Keys.Union(b.Keys).Where(k => !a.TryGetValue(k, out var x) || !b.TryGetValue(k, out var y)
                    || x.Count != 1 || y.Count != 1 || !Same(x[0], y[0]))
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
        }

        static Dictionary<string, List<JsonElement>> Members(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object
                ? v.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Select(p => p.Value).ToList(), StringComparer.Ordinal)
                : new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);

        /// <summary>A JSON number as sign, significant digits and exponent, so equal values compare equal as text.</summary>
        public static string Canonical(string number)
        {
            var m = Regex.Match(number, @"^(-?)(\d+)(?:\.(\d+))?(?:[eE]([+-]?\d+))?$");
            if (!m.Success) return number;
            var digits = (m.Groups[2].Value + m.Groups[3].Value).TrimStart('0');
            long exponent = 0;
            if (m.Groups[4].Success && !long.TryParse(m.Groups[4].Value, out exponent)) return number;
            if (digits.Length == 0) return "0";
            var trimmed = digits.TrimEnd('0');
            // An exponent that overflows keeps its raw text, which never equates distinct values.
            try { return m.Groups[1].Value + trimmed + "e" + checked(exponent - m.Groups[3].Value.Length + digits.Length - trimmed.Length); }
            catch (OverflowException) { return number; }
        }

        public static bool SameTarget(string entry, string domain, string target)
        {
            using var doc = JsonDocument.Parse(entry);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && Str(doc.RootElement, "DOMAIN") == domain && Str(doc.RootElement, "MAPPING") == target;
        }

        public static JsonValueKind Kind(JsonElement config, string key) =>
            config.TryGetProperty(key, out var v) ? v.ValueKind : JsonValueKind.Undefined;

        public static int Occurrences(JsonElement config, string name) =>
            config.TryGetProperty("SYMBOLS", out var s) && s.ValueKind == JsonValueKind.Object
                ? s.EnumerateObject().Count(p => p.Name == name) : 0;

        /// <summary>The entry's JSON, or null when it is absent or its name occurs more than once.</summary>
        public static string? Entry(JsonElement config, string name)
        {
            if (!config.TryGetProperty("SYMBOLS", out var s) || s.ValueKind != JsonValueKind.Object) return null;
            var found = s.EnumerateObject().Where(p => p.Name == name).Take(2).ToList();
            return found.Count == 1 ? found[0].Value.GetRawText() : null;
        }

        static KeyValuePair<string, string> Skip(string name, string reason) => new KeyValuePair<string, string>(name, reason);
        static string? Str(JsonElement e, string key) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static bool? Bool(JsonElement e, string key) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
                ? v.GetBoolean() : (bool?)null;
    }
}
