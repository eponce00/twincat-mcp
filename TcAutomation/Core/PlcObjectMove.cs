using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace TcAutomation.Core
{
    public sealed class MoveFileRecord
    {
        public string RelativePath { get; set; }
        public string Sha256 { get; set; }
    }

    // File and project edits run only while the owning solution is closed.
    // No import/delete cycle introduces a duplicate or replacement object GUID.
    public sealed class PlcObjectMove
    {
        const long MaxBytes = 64 * 1024 * 1024;
        readonly byte[] before, after;
        readonly bool folder, sourceExisted;
        public string ProjectFile { get; }
        public string Source { get; }
        public string Destination { get; }
        public string RecoveryPath { get; private set; }
        public MoveFileRecord[] Files { get; }
        public int UpdatedEntries { get; }
        public string Phase { get; private set; } = "prepared";
        bool moved, projectWritten;

        public PlcObjectMove(string projectFile, string sourceRelative, string parentRelative, bool folder)
        {
            ProjectFile = Path.GetFullPath(projectFile);
            string directory = Path.GetDirectoryName(ProjectFile);
            if (!ProjectFile.EndsWith(".plcproj", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Expected a PLC project file.");
            CheckPath(directory, ProjectFile);
            Source = Inside(directory, sourceRelative);
            string parent = string.IsNullOrEmpty(parentRelative) ? directory : Inside(directory, parentRelative);
            Destination = Path.Combine(parent, Path.GetFileName(Source));
            this.folder = folder;
            if (Source.Equals(Destination, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Already in this parent; use rename_object to change its name.");
            if (parent.Equals(Source, StringComparison.OrdinalIgnoreCase) || parent.StartsWith(Source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Cannot move an object into itself or its descendants.");
            CheckPath(directory, Source); CheckPath(directory, Destination);
            if (File.Exists(Destination) || Directory.Exists(Destination)) throw new IOException("Destination already exists on disk; overwrite is prohibited.");
            if (folder && File.Exists(Source)) throw new IOException("Source folder is a file on disk.");
            if (!folder && (!File.Exists(Source) || Directory.Exists(Source))) throw new IOException("Source file is missing.");
            sourceExisted = folder ? Directory.Exists(Source) : File.Exists(Source);
            Files = Snapshot(Source, folder);
            before = Read(ProjectFile);
            var xml = Parse(before);
            if (xml.Root?.Name.LocalName != "Project") throw new InvalidDataException("Expected MSBuild PLC project XML.");
            string oldRelative = Relative(directory, Source), newRelative = Relative(directory, Destination);
            int changed = 0, compileFiles = 0;
            foreach (var item in xml.Root.Elements().Where(e => e.Name.LocalName == "ItemGroup").Elements())
            {
                var include = item.Attribute("Include");
                if (include == null) continue;
                string value = include.Value.Replace('/', '\\').TrimEnd('\\');
                if (item.Name.LocalName == "Compile" && (Path.IsPathRooted(value) ||
                    value.Split('\\').Any(p => p == "." || p == "..") || value.IndexOfAny(new[] { '*', '?', '$', '%' }) >= 0 ||
                    item.Attribute("Condition") != null || item.Parent.Attribute("Condition") != null || item.Elements().Any(e => e.Name.LocalName == "Link")))
                    throw new InvalidDataException("Move requires explicit, unconditional project-relative Compile items without links or aliases.");
                bool selected = value.Equals(oldRelative, StringComparison.OrdinalIgnoreCase) ||
                    (folder && value.StartsWith(oldRelative + "\\", StringComparison.OrdinalIgnoreCase));
                if (!selected) continue;
                if (!new[] { "Compile", "Folder", "None", "Content", "EmbeddedResource" }.Contains(item.Name.LocalName) ||
                    item.Attribute("Condition") != null || item.Parent.Attribute("Condition") != null ||
                    item.Elements().Any(e => e.Name.LocalName == "Link") || value.IndexOfAny(new[] { '*', '?', '$', '%' }) >= 0)
                    throw new InvalidDataException("Conditional, linked or nonliteral project items cannot be moved.");
                string absolute = Inside(directory, value);
                CheckPath(directory, absolute);
                if (item.Name.LocalName == "Compile")
                {
                    if (!Files.Any(f => PathFor(Source, folder, f.RelativePath).Equals(absolute, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("Compile item has no matching saved file.");
                    compileFiles++;
                }
                include.Value = newRelative + value.Substring(oldRelative.Length);
                changed++;
            }
            if (changed == 0 || (!folder && compileFiles != 1)) throw new InvalidDataException("Source does not map uniquely to a saved project item.");
            // Existing logical entries must not alias the new filesystem location.
            var paths = xml.Root.Elements().Where(e => e.Name.LocalName == "ItemGroup").Elements()
                .Attributes("Include").Select(a => a.Value.Replace('/', '\\').TrimEnd('\\')).ToArray();
            if (paths.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1 &&
                (g.Key.Equals(newRelative, StringComparison.OrdinalIgnoreCase) || g.Key.StartsWith(newRelative + "\\", StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException("Destination conflicts with an existing project entry.");
            UpdatedEntries = changed;
            using var buffer = new MemoryStream();
            using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, NewLineHandling = NewLineHandling.None })) xml.Save(writer);
            after = buffer.ToArray();
        }

        public void Stage(string stateDirectory, string originalTreePath, string destinationTreePath)
        {
            RecoveryPath = Path.Combine(Path.GetFullPath(stateDirectory), "moves", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RecoveryPath);
            DurableWrite(Path.Combine(RecoveryPath, "project.before.plcproj"), before);
            DurableWrite(Path.Combine(RecoveryPath, "project.after.plcproj"), after);
            DurableWrite(Path.Combine(RecoveryPath, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1, createdUtc = DateTime.UtcNow, projectFile = ProjectFile, source = Source, destination = Destination,
                originalTreePath, destinationTreePath, folder, sourceExisted, projectBeforeSha256 = Hash(before),
                projectAfterSha256 = Hash(after), files = Files
            }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            Record("prepared");
        }

        public void Record(string phase)
        {
            Phase = phase;
            if (RecoveryPath != null) AtomicWrite(Path.Combine(RecoveryPath, "state.json"),
                JsonSerializer.SerializeToUtf8Bytes(new { phase, updatedUtc = DateTime.UtcNow }));
        }

        public void Apply()
        {
            if (RecoveryPath == null) throw new InvalidOperationException("A durable recovery record is required before moving.");
            RequireBytes(ProjectFile, before);
            RequireSnapshot(Source);
            CheckPath(Path.GetDirectoryName(ProjectFile), Destination);
            if (File.Exists(Destination) || Directory.Exists(Destination)) throw new IOException("Destination appeared before dispatch.");
            Record("moving_files");
            if (sourceExisted)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Destination));
                if (folder) Directory.Move(Source, Destination); else File.Move(Source, Destination);
                moved = true;
            }
            Record("writing_project");
            RequireBytes(ProjectFile, before);
            AtomicWrite(ProjectFile, after);
            projectWritten = true;
            Record("applied");
        }

        public void Verify()
        {
            RequireSnapshot(Destination);
            if (File.Exists(Source) || Directory.Exists(Source)) throw new IOException("Source still exists after relocation.");
            RequireBytes(ProjectFile, after);
        }

        public void Rollback()
        {
            Record("rolling_back");
            // Refuse to overwrite external edits or an independently recreated source.
            RequireBytes(ProjectFile, projectWritten ? after : before);
            if (moved)
            {
                RequireSnapshot(Destination);
                if (File.Exists(Source) || Directory.Exists(Source)) throw new IOException("Original location was recreated; automatic rollback is unsafe.");
                if (folder) Directory.Move(Destination, Source); else File.Move(Destination, Source);
                moved = false;
            }
            if (projectWritten) { AtomicWrite(ProjectFile, before); projectWritten = false; }
            Record("rolled_back");
        }

        void RequireSnapshot(string location)
        {
            bool exists = folder ? Directory.Exists(location) : File.Exists(location);
            if (!sourceExisted)
            {
                if (File.Exists(location) || Directory.Exists(location)) throw new IOException("A previously absent source location appeared.");
                return;
            }
            if (!exists) throw new IOException("Saved move location disappeared.");
            var current = Snapshot(location, folder);
            if (current.Length != Files.Length || current.Where((f, i) => f.RelativePath != Files[i].RelativePath || f.Sha256 != Files[i].Sha256).Any())
                throw new IOException("Saved move files changed; recovery requires inspection.");
        }
        static MoveFileRecord[] Snapshot(string path, bool folder)
        {
            var names = new List<string>();
            string boundary = folder ? path : Path.GetDirectoryName(path);
            if (folder && Directory.Exists(path))
            {
                var pending = new Queue<string>(); pending.Enqueue(path); int count = 0;
                while (pending.Count > 0)
                {
                    string current = pending.Dequeue();
                    CheckPath(boundary, current);
                    foreach (string entry in Directory.EnumerateFileSystemEntries(current))
                    {
                        if (++count > 4096) throw new IOException("Move exceeds 4096 filesystem entries.");
                        CheckPath(boundary, entry);
                        if (Directory.Exists(entry)) pending.Enqueue(entry); else names.Add(entry);
                    }
                }
            }
            else if (!folder) { CheckPath(boundary, path); names.Add(path); }
            else if (!Directory.Exists(path)) return new MoveFileRecord[0];
            long total = 0;
            return names.OrderBy(n => n, StringComparer.Ordinal).Select(n =>
            {
                var bytes = Read(n); total += bytes.Length;
                if (total > MaxBytes) throw new IOException("Move exceeds 64 MiB.");
                return new MoveFileRecord { RelativePath = folder ? Relative(path, n) : "", Sha256 = Hash(bytes) };
            }).ToArray();
        }
        static string PathFor(string location, bool folder, string relative) => folder ? Path.Combine(location, relative) : location;
        public static void CheckPath(string directory, string path)
        {
            directory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            path = Path.GetFullPath(path);
            if (!path.Equals(directory, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Move path escapes the PLC project directory.");
            for (string current = path; current != null && current.Length >= directory.Length; current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked files and reparse points cannot be moved.");
        }
        static string Inside(string directory, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Split('\\', '/').Any(p => p == "." || p == ".." || p.Contains(":")))
                throw new ArgumentException("Expected a literal project-relative path.");
            string path = Path.GetFullPath(Path.Combine(directory, relative)); CheckPath(directory, path); return path;
        }
        static string Relative(string directory, string path) => path.Substring(directory.TrimEnd(Path.DirectorySeparatorChar).Length + 1);
        static XDocument Parse(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes });
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        static byte[] Read(string file)
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxBytes) throw new IOException("Move input exceeds 64 MiB.");
            using var buffer = new MemoryStream();
            var block = new byte[65536]; int count;
            while ((count = stream.Read(block, 0, block.Length)) != 0)
            {
                if (buffer.Length + count > MaxBytes) throw new IOException("Move input exceeds 64 MiB.");
                buffer.Write(block, 0, count);
            }
            return buffer.ToArray();
        }
        static void RequireBytes(string file, byte[] expected) { if (Hash(Read(file)) != Hash(expected)) throw new IOException("Project file changed outside this move."); }
        public static string Hash(byte[] bytes) { using var h = SHA256.Create(); return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static void DurableWrite(string path, byte[] bytes)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
        }
        static void AtomicWrite(string path, byte[] bytes)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { DurableWrite(temp, bytes); if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
