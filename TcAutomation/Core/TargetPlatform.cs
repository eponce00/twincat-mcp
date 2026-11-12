using System;
using System.Text;
using System.Xml.Linq;
using TwinCAT.Ads;

namespace TcAutomation.Core
{
    /// <summary>
    /// What a target is, read from its TwinCAT system service (port 10000, index group 700, offset 1: the
    /// TcTargetDesc XML XAE uses), and the solution platform a build for it must use.
    /// </summary>
    public class TargetDescription
    {
        public string AmsNetId { get; set; } = "";
        public bool Read { get; set; }
        public string? TargetType { get; set; }
        public bool UserModeRuntime { get; set; }
        public string? OsName { get; set; }
        public int? CpuArchitecture { get; set; }
        public string? TwinCATVersion { get; set; }
        /// <summary>The platform a build for this target must use, or null when it cannot be told.</summary>
        public string? ExpectedPlatform { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public static class TargetPlatform
    {
        private const uint SystemServicePort = 10000;
        private const uint TargetDescriptionGroup = 700;
        private const uint TargetDescriptionOffset = 1;

        public static TargetDescription Read(string amsNetId, int timeoutMs = 5000)
        {
            var description = new TargetDescription { AmsNetId = amsNetId };
            try
            {
                using var client = new AdsClient { Timeout = timeoutMs };
                client.Connect(AmsNetId.Parse(amsNetId), (int)SystemServicePort);
                var buffer = new byte[16384];
                int read = client.Read(TargetDescriptionGroup, TargetDescriptionOffset, buffer.AsMemory());
                return Parse(amsNetId, Encoding.UTF8.GetString(buffer, 0, read).TrimEnd('\0'));
            }
            catch (Exception ex)
            {
                description.ErrorMessage = $"The target description could not be read: {ex.Message}";
                return description;
            }
        }

        /// <summary>Parse TcTargetDesc XML and decide the platform. Public for the offline harness.</summary>
        public static TargetDescription Parse(string amsNetId, string xml)
        {
            var description = new TargetDescription { AmsNetId = amsNetId };
            try
            {
                var root = XElement.Parse(xml);
                var type = root.Element("TargetType");
                description.TargetType = type?.Value.Trim();
                description.UserModeRuntime = string.Equals((string?)type?.Attribute("IsUsermodeRuntime"), "true", StringComparison.OrdinalIgnoreCase);
                description.OsName = root.Element("OsImage")?.Element("OsName")?.Value.Trim();
                var arch = root.Element("Hardware")?.Element("CPUArchitecture")?.Value.Trim();
                description.CpuArchitecture = int.TryParse(arch, out int a) ? a : (int?)null;
                var version = root.Element("TargetVersion");
                if (version != null)
                {
                    description.TwinCATVersion = $"{version.Element("Version")?.Value}.{version.Element("Revision")?.Value}.{version.Element("Build")?.Value}";
                }
                description.Read = true;
                description.ExpectedPlatform = ExpectedPlatform(description);
            }
            catch (Exception ex)
            {
                description.ErrorMessage = $"The target description is not valid XML: {ex.Message}";
            }
            return description;
        }

        /// <summary>
        /// The build platform for a target. CPU architecture codes are Windows' PROCESSOR_ARCHITECTURE values
        /// (0 x86, 9 x64, 5 ARM, 12 ARM64). A user-mode runtime and TwinCAT/BSD build for "TwinCAT OS"; the
        /// Windows real-time kernel builds for "TwinCAT RT". Anything else is not told (null).
        /// </summary>
        public static string? ExpectedPlatform(TargetDescription d)
        {
            bool windows = d.OsName?.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0;
            bool bsd = d.OsName?.IndexOf("BSD", StringComparison.OrdinalIgnoreCase) >= 0;
            switch (d.CpuArchitecture)
            {
                case 9:
                    if (d.UserModeRuntime || bsd) return "TwinCAT OS (x64)";
                    if (windows) return "TwinCAT RT (x64)";
                    return null;
                case 0:
                    return windows && !d.UserModeRuntime ? "TwinCAT RT (x86)" : null;
                default:
                    return null;
            }
        }
    }
}
