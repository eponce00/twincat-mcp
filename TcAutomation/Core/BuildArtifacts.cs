using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace TcAutomation.Core
{
    public sealed class ArtifactRecord
    {
        public string PlcName { get; set; }
        public string ProjectPath { get; set; }
        public string Path { get; set; }
        public bool Exists { get; set; }
        public string Sha256 { get; set; }
        public string SourceSha256 { get; set; }
        public DateTime? ModifiedUtc { get; set; }
        public string Status { get; set; } = "unverified_existing";
        public string Error { get; set; }
        public string BuildId { get; set; }
        public bool? CompilerSuccess { get; set; }
        public int SymbolCount { get; set; }
        public int TypeCount { get; set; }
        public int AddedSymbolCount { get; set; }
        public int RemovedSymbolCount { get; set; }
        public string[] MissingSymbols { get; set; } = new string[0];
        public string[] MissingTypes { get; set; } = new string[0];
        public bool VerificationRequested { get; set; }
        public bool VerificationPassed { get; set; }
        public bool? MatchesObservedBuildArtifact { get; set; }
        public bool? SourceMatchesObservedBuild { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public string[] Symbols { get; set; } = new string[0];
        [System.Text.Json.Serialization.JsonIgnore] public string[] Types { get; set; } = new string[0];
    }

    public static class BuildArtifacts
    {
        const long MaxBytes = 64 * 1024 * 1024;
        public static List<ArtifactRecord> Capture(VisualStudioInstance vs, string selectedPlc = null)
        {
            string tcProject = vs.TwinCATProjectFilePath;
            var xml = Load(tcProject);
            var result = new List<ArtifactRecord>();
            foreach (var node in xml.Descendants().Where(e => e.Name.LocalName == "Project" && e.Attribute("PrjFilePath") != null))
            {
                string name = (string)node.Attribute("Name");
                if (selectedPlc != null && !name.Equals(selectedPlc, StringComparison.OrdinalIgnoreCase)) continue;
                string project = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(tcProject), (string)node.Attribute("PrjFilePath")));
                string tmc = (string)node.Attribute("TmcFilePath");
                tmc = string.IsNullOrWhiteSpace(tmc) ? System.IO.Path.ChangeExtension(project, ".tmc")
                    : System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(tcProject), tmc));
                var record = Read(tmc);
                record.PlcName = name;
                record.ProjectPath = project;
                try { record.SourceSha256 = SourceHash(project); }
                catch (Exception ex) { record.Error = "Source manifest unavailable: " + ex.Message; }
                result.Add(record);
            }
            if (selectedPlc != null && result.Count != 1) throw new ArgumentException("Selected PLC project was not found uniquely in the loaded TwinCAT project.");
            return result;
        }

        public static ArtifactRecord Read(string file)
        {
            var result = new ArtifactRecord { Path = file, Exists = File.Exists(file) };
            if (!result.Exists) { result.Status = "missing"; return result; }
            try
            {
                var bytes = ReadBytes(file);
                using var stream = new MemoryStream(bytes);
                using var reader = XmlReader.Create(stream, Settings());
                var xml = XDocument.Load(reader);
                if (xml.Root?.Name.LocalName != "TcModuleClass") throw new InvalidDataException("Not a TMC module class file.");
                result.Sha256 = Hash(bytes);
                result.ModifiedUtc = File.GetLastWriteTimeUtc(file);
                result.Symbols = xml.Descendants().Where(e => e.Name.LocalName == "Symbol")
                    .Select(e => e.Elements().FirstOrDefault(n => n.Name.LocalName == "Name")?.Value)
                    .Where(n => !string.IsNullOrEmpty(n)).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
                result.Types = xml.Descendants().Where(e => e.Name.LocalName == "DataType")
                    .Select(e => e.Elements().FirstOrDefault(n => n.Name.LocalName == "Name")?.Value)
                    .Where(n => !string.IsNullOrEmpty(n)).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
                result.SymbolCount = result.Symbols.Length; result.TypeCount = result.Types.Length;
            }
            catch (Exception ex) { result.Status = "unreadable"; result.Error = ex.Message; }
            return result;
        }

        public static void Compare(ArtifactRecord before, ArtifactRecord after, string buildId, bool compilerSuccess,
            string[] expectedSymbols = null, string[] expectedTypes = null)
        {
            after.BuildId = buildId; after.CompilerSuccess = compilerSuccess;
            after.AddedSymbolCount = after.Symbols.Except(before?.Symbols ?? new string[0]).Count();
            after.RemovedSymbolCount = (before?.Symbols ?? new string[0]).Except(after.Symbols).Count();
            if (after.Exists && after.Sha256 != null)
                after.Status = !compilerSuccess ? (before?.Sha256 == after.Sha256 ? "retained_after_failed_build" : "changed_during_failed_build")
                    : before?.Exists != true ? "created"
                    : after.Sha256 != before.Sha256 ? "changed"
                    : after.ModifiedUtc != before.ModifiedUtc ? "refreshed" : "unchanged";
            after.VerificationRequested = (expectedSymbols?.Length ?? 0) + (expectedTypes?.Length ?? 0) > 0;
            after.MissingSymbols = (expectedSymbols ?? new string[0]).Where(n => !Matches(after.Symbols, n)).ToArray();
            after.MissingTypes = (expectedTypes ?? new string[0]).Where(n => !Matches(after.Types, n)).ToArray();
            after.VerificationPassed = compilerSuccess && after.Exists && after.Sha256 != null &&
                after.MissingSymbols.Length == 0 && after.MissingTypes.Length == 0;
        }

        public static object Query(VisualStudioInstance vs, string plcName, string kind, string query, int offset, int limit)
        {
            if (offset < 0 || limit < 1 || limit > 200 || (kind != "symbols" && kind != "types")) throw new ArgumentException("Invalid symbol query.");
            var current = Capture(vs, plcName).Single();
            if (vs.LastBuildArtifacts.TryGetValue(plcName, out var previous))
            {
                current.BuildId = previous.BuildId; current.CompilerSuccess = previous.CompilerSuccess;
                current.MatchesObservedBuildArtifact = current.Sha256 == null || previous.Sha256 == null
                    ? (bool?)null : current.Sha256 == previous.Sha256;
                current.SourceMatchesObservedBuild = current.SourceSha256 == null || previous.SourceSha256 == null
                    ? (bool?)null : current.SourceSha256 == previous.SourceSha256;
                if (current.Exists && current.Sha256 != null)
                    current.Status = current.MatchesObservedBuildArtifact == false ? "changed_since_observed_build"
                        : current.SourceMatchesObservedBuild == false ? "source_changed_since_observed_build" : previous.Status;
            }
            var names = (kind == "types" ? current.Types : current.Symbols)
                .Where(n => string.IsNullOrEmpty(query) || n.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            if (offset > names.Length) throw new ArgumentException("Offset exceeds result count.");
            return new { success = current.Exists && current.Sha256 != null, artifact = current,
                kind, names = names.Skip(offset).Take(limit).ToArray(), total = names.Length,
                nextOffset = offset + limit < names.Length ? (int?)(offset + limit) : null,
                note = "Runtime symbols and data types are distinct. Absence does not establish an object reference count. Artifact provenance is limited to builds observed in this context." };
        }

        static bool Matches(string[] names, string pattern) => pattern.EndsWith("*", StringComparison.Ordinal)
            ? names.Any(n => n.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.Ordinal)) : names.Contains(pattern, StringComparer.Ordinal);
        static string SourceHash(string project)
        {
            var xml = Load(project);
            var files = xml.Descendants().Where(n => n.Name.LocalName == "Compile").Select(n => (string)n.Attribute("Include"))
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(project), n)))
                .Concat(new[] { project }).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            if (files.Length > 4096) throw new InvalidDataException("Source manifest exceeds 4096 files.");
            var text = new StringBuilder(); long total = 0;
            foreach (var file in files) { var bytes = ReadBytes(file); total += bytes.Length;
                if (total > MaxBytes) throw new InvalidDataException("Source manifest exceeds 64 MiB.");
                text.Append(file.Length).Append(':').Append(file).Append(Hash(bytes)); }
            return Hash(Encoding.UTF8.GetBytes(text.ToString()));
        }
        static XDocument Load(string file) { using var reader = XmlReader.Create(file, Settings()); return XDocument.Load(reader); }
        static XmlReaderSettings Settings() => new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes };
        static byte[] ReadBytes(string file) { var info = new FileInfo(file); if (info.Length > MaxBytes) throw new InvalidDataException("Artifact exceeds 64 MiB."); return File.ReadAllBytes(file); }
        static string Hash(byte[] bytes) { using var hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    }
}
