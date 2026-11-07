using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace TcAutomation.Core
{
    /// <summary>Pure HMI publish policy, shared by native commands and offline tests.</summary>
    public static class HmiPolicy
    {
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
