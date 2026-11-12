using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TcAutomation.Core
{
    /// <summary>Pure HMI publish policy, shared by native commands and offline tests.</summary>
    public static class HmiPolicy
    {
        static readonly Regex EngineeringPort = new Regex(@"<(HmiCommunicationServerPort|HmiCommunicationServerAuthPort)>\d+</\1>");
        static readonly Regex SolutionHmiProject = new Regex(@"""([^""]+\.hmiproj)""", RegexOptions.IgnoreCase);

        // One char per byte, so no byte change can hide in a decoding. UTF-16 never matches: no restore.
        static readonly Encoding Bytes = Encoding.GetEncoding(28591);

        /// <summary>True when the two files differ, and only in engineering port values.</summary>
        public static bool OnlyEngineeringPortsChanged(byte[] original, byte[] current)
        {
            string a = Bytes.GetString(original), b = Bytes.GetString(current);
            return !string.Equals(a, b, StringComparison.Ordinal)
                && string.Equals(MaskPorts(a), MaskPorts(b), StringComparison.Ordinal);
        }

        static string MaskPorts(string text) => EngineeringPort.Replace(text, "<$1>#</$1>");

        static readonly Regex LoopbackEndpoint = new Regex(@"^""http://127\.0\.0\.1:[0-9]+""$");

        /// <summary>The server configuration whose ENDPOINTS the engineering server rewrites.</summary>
        public static string ServerConfig(string hmiProject) =>
            Path.Combine(Path.GetDirectoryName(hmiProject), "Server", "TcHmiSrv", "TcHmiSrv.Config.default.json");

        /// <summary>
        /// Exclusive handles on every engineering server lock of the solution, held while ports are restored.
        /// Measured: a running server holds .engineering_servers\*\TcHmiSrv.lock, and none is left after a
        /// clean close. Null if any lock is still held, or the locks cannot be listed: restore nothing.
        /// </summary>
        public static List<FileStream> HoldServerLocks(string solutionPath, IEnumerable<string> files)
        {
            var held = new List<FileStream>();
            try
            {
                var roots = files.Where(f => f.EndsWith(".hmiproj", StringComparison.OrdinalIgnoreCase))
                    .Select(p => Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(p))))
                    .Append(Path.GetDirectoryName(Path.GetFullPath(solutionPath)))
                    .Where(r => r != null).Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var folder in roots.Select(r => Path.Combine(r, ".engineering_servers")).Where(Directory.Exists))
                    foreach (var path in Directory.GetFiles(folder, "TcHmiSrv.lock", SearchOption.AllDirectories))
                    {
                        try { held.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)); }
                        catch (FileNotFoundException) { }
                    }
                return held;
            }
            catch
            {
                foreach (var stream in held) stream.Dispose();
                return null;
            }
        }

        /// <summary>
        /// The bytes to write back once the shell is gone, or null to leave the file. A .hmiproj comes back
        /// whole if only its engineering ports changed. A server configuration gets only its original
        /// ENDPOINTS array back, if only the ports of "http://127.0.0.1:port" elements changed, so other
        /// edits (a refresh) stay.
        /// </summary>
        public static byte[] PortRestore(string path, byte[] original, byte[] current)
        {
            if (path.EndsWith(".hmiproj", StringComparison.OrdinalIgnoreCase))
                return OnlyEngineeringPortsChanged(original, current) ? original : null;
            if (!EndpointsSpan(original, out int os, out int oe) || !EndpointsSpan(current, out int cs, out int ce)) return null;
            if (Bytes.GetString(original, os, oe - os) == Bytes.GetString(current, cs, ce - cs)) return null;
            var x = EndpointTokens(original, os, oe);
            var y = EndpointTokens(current, cs, ce);
            if (x == null || y == null || !x.SequenceEqual(y, StringComparer.Ordinal)) return null;
            var result = new byte[current.Length - (ce - cs) + (oe - os)];
            Buffer.BlockCopy(current, 0, result, 0, cs);
            Buffer.BlockCopy(original, os, result, cs, oe - os);
            Buffer.BlockCopy(current, ce, result, cs + (oe - os), current.Length - ce);
            return result;
        }

        /// <summary>
        /// The array as raw tokens and the bytes between them, with the port of each direct
        /// "http://127.0.0.1:port" element masked. Null if it holds a comment.
        /// </summary>
        static List<string> EndpointTokens(byte[] json, int start, int end)
        {
            var parts = new List<string>();
            try
            {
                var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(json, start, end - start),
                    new JsonReaderOptions { CommentHandling = JsonCommentHandling.Allow });
                int previous = 0;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.Comment) return null;
                    int at = (int)reader.TokenStartIndex, after = (int)reader.BytesConsumed;
                    parts.Add(Bytes.GetString(json, start + previous, at - previous));
                    var token = Bytes.GetString(json, start + at, after - at);
                    bool port = reader.TokenType == JsonTokenType.String && reader.CurrentDepth == 1 && LoopbackEndpoint.IsMatch(token);
                    parts.Add(port ? "\"http://127.0.0.1:#\"" : token);
                    previous = after;
                }
                parts.Add(Bytes.GetString(json, start + previous, end - start - previous));
                return parts;
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException) { return null; }
        }

        /// <summary>Byte range of the one top-level ENDPOINTS array. False for anything else, UTF-16 included.</summary>
        static bool EndpointsSpan(byte[] json, out int start, out int end)
        {
            start = end = -1;
            int offset = json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF ? 3 : 0;
            int found = 0;
            try
            {
                var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(json, offset, json.Length - offset),
                    new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    bool match = reader.ValueTextEquals("ENDPOINTS");
                    if (!reader.Read()) return false;
                    if (match)
                    {
                        if (reader.TokenType != JsonTokenType.StartArray) return false;
                        start = offset + (int)reader.TokenStartIndex;
                        reader.Skip();
                        end = offset + (int)reader.BytesConsumed;
                        found++;
                    }
                    else reader.Skip();
                }
                if (reader.TokenType != JsonTokenType.EndObject || reader.Read()) return false;
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException) { return false; }
            return found == 1;
        }

        /// <summary>Full paths of the .hmiproj files a solution names.</summary>
        public static string[] HmiProjects(string solutionPath)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(solutionPath));
            return SolutionHmiProject.Matches(File.ReadAllText(solutionPath)).Cast<Match>()
                .Select(m => Path.GetFullPath(Path.Combine(directory, m.Groups[1].Value)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public static string Target(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
                throw new InvalidOperationException("invalid_hmi_target");
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0
                || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
                throw new InvalidOperationException("invalid_hmi_target");
            return uri.AbsoluteUri;
        }

        public static void Profile(string actual, string expected, bool deleteFiles, bool deleteData, bool abortOnErrors)
        {
            if (!string.Equals(Target(actual), Target(expected), StringComparison.Ordinal))
                throw new InvalidOperationException("hmi_target_mismatch");
            if (deleteFiles || deleteData) throw new InvalidOperationException("destructive_hmi_profile");
            if (!abortOnErrors) throw new InvalidOperationException("hmi_abort_on_errors_required");
        }

        public static string[] Profiles(string projectDirectory)
        {
            var path = Path.Combine(projectDirectory, "Properties", "tchmipublish.config.json");
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 4 * 1024 * 1024) throw new InvalidOperationException("invalid_hmi_profiles");
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            if (json.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("invalid_hmi_profiles");
            var names = json.RootElement.EnumerateArray().Select(p => p.GetProperty("profileName").GetString()).ToArray();
            if (names.Length > 128 || names.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 128)
                || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
                throw new InvalidOperationException("invalid_hmi_profiles");
            return names;
        }

        private static readonly HashSet<string> Ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "bin", "obj", ".vs", ".git", ".HMI", "Packages", "node_modules", "logs" };

        /// <summary>Bounded source/config fingerprint. Returns hashes only, never file contents or secrets.</summary>
        public static string Fingerprint(string projectPath, string target, string profile, string sdkPath = null)
        {
            var root = Path.GetDirectoryName(Path.GetFullPath(projectPath));
            // Linked inputs outside this root need a separate closure policy; refuse them for now.
            var xml = XDocument.Load(projectPath);
            foreach (var input in xml.Descendants().Where(e => e.Name.LocalName == "Content" || e.Name.LocalName == "ProjectReference"))
            {
                var include = (string)input.Attribute("Include");
                if (string.IsNullOrEmpty(include)) continue;
                if (include.IndexOfAny(new[] { '$', '*', '?' }) >= 0
                    || !Path.GetFullPath(Path.Combine(root, include)).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("hmi_external_or_dynamic_input");
            }
            var files = new List<string>();
            int entries = 0;
            void Walk(string folder, int depth)
            {
                if (depth > 32) throw new InvalidOperationException("hmi_source_limit");
                foreach (var item in Directory.GetFileSystemEntries(folder))
                {
                    if (++entries > 20000) throw new InvalidOperationException("hmi_source_limit");
                    var name = Path.GetFileName(item);
                    if (Ignored.Contains(name) || name.EndsWith(".user", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".suo", StringComparison.OrdinalIgnoreCase)) continue;
                    var attrs = File.GetAttributes(item);
                    if ((attrs & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("hmi_source_reparse_point");
                    if ((attrs & FileAttributes.Directory) != 0) Walk(item, depth + 1);
                    else files.Add(item);
                    if (files.Count > 20000) throw new InvalidOperationException("hmi_source_limit");
                }
            }
            Walk(root, 0);
            using var hash = SHA256.Create();
            void Add(byte[] bytes) { hash.TransformBlock(bytes, 0, bytes.Length, null, 0); }
            Add(Encoding.UTF8.GetBytes(Path.GetFullPath(projectPath) + "\0" + Target(target) + "\0" + profile + "\0"));
            long total = 0;
            foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
            {
                var length = new FileInfo(file).Length;
                total += length;
                if (length > 32 * 1024 * 1024 || total > 256 * 1024 * 1024) throw new InvalidOperationException("hmi_source_limit");
                Add(Encoding.UTF8.GetBytes(file.Substring(root.Length) + "\0" + length + "\0"));
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                var buffer = new byte[65536];
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0) hash.TransformBlock(buffer, 0, n, null, 0);
            }
            if (sdkPath != null)
                foreach (var name in new[] { "TcHmiAutomation.dll", "TcHmiMSBuild.Publish.dll", "Beckhoff.TwinCAT.HMI.targets" })
                {
                    var tool = Path.Combine(Path.GetDirectoryName(sdkPath), name);
                    Add(Encoding.UTF8.GetBytes(name + "\0"));
                    if (!File.Exists(tool)) throw new InvalidOperationException("hmi_sdk_missing");
                    using var toolHash = SHA256.Create();
                    using var toolStream = File.OpenRead(tool);
                    Add(toolHash.ComputeHash(toolStream));
                }
            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
        }
    }
}
