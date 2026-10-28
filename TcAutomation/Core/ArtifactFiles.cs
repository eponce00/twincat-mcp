using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace TcAutomation.Core
{
    public static class ArtifactFiles
    {
        public static string ValidateNew(string path, params string[] extensions)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new ArgumentException("Artifact output must be an absolute path.");
            path = Path.GetFullPath(path);
            if (!extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Unexpected artifact extension.");
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Artifact destination exists; overwrite is prohibited.");
            for (string p = Path.GetDirectoryName(path); p != null; p = Path.GetDirectoryName(p))
                if (Directory.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("Artifact output cannot traverse reparse points.");
            return path;
        }
        public static FileStream CreateNew(string path, params string[] extensions)
        {
            path = ValidateNew(path, extensions);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        public static void Publish(string source, string destination, string extension)
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length < 1 || input.Length > 512L * 1024 * 1024) throw new IOException("Artifact is empty or exceeds 512 MiB.");
            using var output = CreateNew(destination, extension);
            input.CopyTo(output); output.Flush(true);
        }
        public static string Sha256(string path)
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        }
    }
}
