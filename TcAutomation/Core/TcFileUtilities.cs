using System;
using System.IO;
using System.Text.RegularExpressions;

namespace TcAutomation.Core
{
    /// <summary>
    /// Utilities for parsing TwinCAT solution and project files.
    /// </summary>
    public static class TcFileUtilities
    {
        /// <summary>
        /// Find the TwinCAT project file (.tsproj or .tspproj) referenced in a Visual Studio solution.
        /// Supports both project GUIDs:
        ///   B1E792BE — classic TwinCAT XAE project (.tsproj, full hardware+PLC)
        ///   DFBE7525 — TwinCAT XAE Shell / library project (.tspproj, PLC-only)
        /// Both use identical TcSmProject XML internally.
        /// </summary>
        /// <param name="solutionFilePath">Path to the .sln file</param>
        /// <returns>Full path to .tsproj/.tspproj file, or empty string if not found</returns>
        public static string FindTwinCATProjectFile(string solutionFilePath)
        {
            if (!File.Exists(solutionFilePath))
                return string.Empty;

            string? tcProjectFile = null;

            // Match either TwinCAT project GUID:
            //   B1E792BE — classic XAE project (.tsproj)
            //   DFBE7525 — XAE Shell / library project (.tspproj)
            var pattern = @"Project\(""\{(?:B1E792BE-AA5F-4E3C-8C82-674BF9C0715B|DFBE7525-6864-4E62-8B2E-D530D69D9D96)\}""\)\s*=\s*""[^""]*"",\s*""(?<tsproj>[^""]+\.tspp?roj)""";

            foreach (var line in File.ReadLines(solutionFilePath))
            {
                var match = Regex.Match(line, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
                if (match.Success)
                {
                    tcProjectFile = match.Groups["tsproj"].Value;
                    break;
                }
            }

            if (string.IsNullOrEmpty(tcProjectFile))
                return string.Empty;

            // Convert relative path to absolute
            var solutionDir = Path.GetDirectoryName(solutionFilePath)!;
            return Path.GetFullPath(Path.Combine(solutionDir, tcProjectFile));
        }

        /// <summary>
        /// Get the TwinCAT version from a .tsproj file.
        /// </summary>
        /// <param name="projectFilePath">Path to the .tsproj file</param>
        /// <returns>TwinCAT version string (e.g., "3.1.4026.17"), or empty if not found</returns>
        public static string GetTcVersion(string projectFilePath)
        {
            if (!File.Exists(projectFilePath))
                return string.Empty;

            foreach (var line in File.ReadLines(projectFilePath))
            {
                if (line.Contains("TcVersion"))
                {
                    // Extract version from TcVersion="X.X.X.X"
                    int start = line.IndexOf("TcVersion=\"") + 11;
                    if (start > 10)
                    {
                        var substring = line.Substring(start);
                        int end = substring.IndexOf('"');
                        if (end > 0)
                        {
                            return substring.Substring(0, end);
                        }
                    }
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// Check if the TwinCAT project version is pinned.
        /// </summary>
        public static bool IsTwinCATProjectPinned(string projectFilePath)
        {
            if (!File.Exists(projectFilePath))
                return false;

            var content = File.ReadAllText(projectFilePath);
            // Look for TcVersionFixed="true" or similar
            return content.IndexOf("TcVersionFixed=\"true\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   content.IndexOf("TcVersionFixed=\"True\"", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Get Visual Studio version from solution file.
        /// </summary>
        /// <returns>Version string like "17.0" for VS2022, or null if not found</returns>
        public static string? GetVisualStudioVersion(string solutionFilePath)
        {
            if (!File.Exists(solutionFilePath))
                return null;

            foreach (var line in File.ReadLines(solutionFilePath))
            {
                var match = Regex.Match(line, @"^VisualStudioVersion\s*=\s*(?<major>\d+)\.", RegexOptions.Multiline);
                if (match.Success)
                {
                    return match.Groups["major"].Value + ".0";
                }
            }
            return null;
        }
    }
}
