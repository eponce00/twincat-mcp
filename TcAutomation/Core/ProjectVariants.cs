using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace TcAutomation.Core
{
    /// <summary>A project variant with the target and build configuration the .tsproj stores for it.</summary>
    public class VariantInfo
    {
        public string Name { get; set; } = "";
        public string? TargetNetId { get; set; }
        public string? Configuration { get; set; }
        public string? Platform { get; set; }
    }

    /// <summary>
    /// Project variants as the .tsproj records them. XAE applies a variant's TargetNetId and BuildConfig when the
    /// variant is chosen in its toolbar; the automation interface's CurrentProjectVariant does not, so the
    /// workflows read them here and apply them explicitly.
    /// </summary>
    public static class ProjectVariants
    {
        public static List<VariantInfo> Read(string tsprojPath) => Parse(File.ReadAllText(tsprojPath));

        public static List<VariantInfo> Parse(string xml)
        {
            var root = XElement.Parse(xml);
            var variants = new List<VariantInfo>();
            VariantInfo Get(string name)
            {
                var v = variants.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (v == null) variants.Add(v = new VariantInfo { Name = name });
                return v;
            }

            // The variant list (Project/Variants), then the per-variant parameters wherever they are recorded.
            foreach (var variant in root.Descendants("Variant"))
            {
                string? name = variant.Element("Name")?.Value.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                bool listed = variant.Parent?.Name.LocalName == "Variants";
                var target = variant.Element("TargetNetId")?.Value.Trim();
                var build = variant.Element("BuildConfig")?.Value.Trim();
                if (!listed && target == null && build == null) continue; // Per-object variant settings (Disable, ...)
                var info = Get(name!);
                if (!string.IsNullOrEmpty(target)) info.TargetNetId = target;
                if (!string.IsNullOrEmpty(build))
                {
                    var (configuration, platform) = SplitBuildConfig(build!);
                    info.Configuration = configuration;
                    info.Platform = platform;
                }
            }
            return variants;
        }

        /// <summary>The variant the .tsproj was saved with (TcSmProject/@TcProjectVariant), if any.</summary>
        public static string? SavedVariant(string xml) => (string?)XElement.Parse(xml).Attribute("TcProjectVariant");

        /// <summary>"Debug|TwinCAT RT (x64)" → ("Debug", "TwinCAT RT (x64)").</summary>
        public static (string? configuration, string? platform) SplitBuildConfig(string buildConfig)
        {
            int bar = buildConfig.IndexOf('|');
            if (bar < 0) return (buildConfig.Trim().Length > 0 ? buildConfig.Trim() : null, null);
            string configuration = buildConfig.Substring(0, bar).Trim();
            string platform = buildConfig.Substring(bar + 1).Trim();
            return (configuration.Length > 0 ? configuration : null, platform.Length > 0 ? platform : null);
        }
    }

    /// <summary>The configuration and platform a deployment builds, and where they came from.</summary>
    public class BuildSelection
    {
        public string? Variant { get; set; }
        public string Configuration { get; set; } = "";
        public string Platform { get; set; } = "";
        /// <summary>"arguments", "variant", "target" or "active" (the solution's current setting).</summary>
        public string Source { get; set; } = "";
        public bool PlatformVerified { get; set; }
        public string PlatformStatus { get; set; } = "";
    }

    /// <summary>
    /// Decides what a deployment builds before anything is built or downloaded. Pure: the caller supplies the
    /// variants, the solution's active configuration and the target description.
    /// </summary>
    public static class BuildPlanner
    {
        public static BuildSelection Resolve(
            IList<VariantInfo> variants,
            string? variantName,
            string? configuration,
            string? platform,
            string activeConfiguration,
            string activePlatform,
            string contextTarget,
            TargetDescription target,
            bool allowUnverifiedPlatform)
        {
            var selection = new BuildSelection();
            VariantInfo? variant = null;
            if (!string.IsNullOrWhiteSpace(variantName))
            {
                variant = variants.FirstOrDefault(v => string.Equals(v.Name, variantName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new DeployRefusedException($"Variant '{variantName}' is not in the project. Variants: {Names(variants)}");
                selection.Variant = variant.Name;
                if (variant.TargetNetId != null && variant.TargetNetId != contextTarget)
                    throw new DeployRefusedException($"Variant '{variant.Name}' targets {variant.TargetNetId}, but this context targets {contextTarget}. Open a context on {variant.TargetNetId} to deploy this variant.");
                if (configuration != null && variant.Configuration != null && !Same(configuration, variant.Configuration))
                    throw new DeployRefusedException($"Configuration '{configuration}' conflicts with variant '{variant.Name}' ({variant.Configuration}).");
                if (platform != null && variant.Platform != null && !Same(platform, variant.Platform))
                    throw new DeployRefusedException($"Platform '{platform}' conflicts with variant '{variant.Name}' ({variant.Platform}).");
            }

            selection.Configuration = configuration ?? variant?.Configuration ?? activeConfiguration;
            if (platform != null) { selection.Platform = platform; selection.Source = "arguments"; }
            else if (variant?.Platform != null) { selection.Platform = variant.Platform; selection.Source = "variant"; }
            else if (target.ExpectedPlatform != null) { selection.Platform = target.ExpectedPlatform; selection.Source = "target"; }
            else { selection.Platform = activePlatform; selection.Source = "active"; }

            if (target.Read && target.ExpectedPlatform != null)
            {
                if (!Same(selection.Platform, target.ExpectedPlatform))
                    throw new DeployRefusedException(
                        $"Build platform '{selection.Platform}' ({selection.Source}) does not match target {contextTarget}: " +
                        $"{target.TargetType ?? "unknown type"}{(target.UserModeRuntime ? ", user-mode runtime" : "")}, {target.OsName ?? "unknown OS"}, " +
                        $"which needs '{target.ExpectedPlatform}'. Nothing was built or downloaded.");
                selection.PlatformVerified = true;
                selection.PlatformStatus = $"verified: target {target.TargetType} needs {target.ExpectedPlatform}";
                return selection;
            }

            string why = target.Read
                ? $"the platform of target type '{target.TargetType}' ({target.OsName}, CPU architecture {target.CpuArchitecture}) is not known"
                : target.ErrorMessage ?? "the target description could not be read";
            if (target.Read || !allowUnverifiedPlatform)
            {
                // A readable target of unknown kind is refused too: allowUnverifiedPlatform covers only an unreadable target.
                if (!target.Read)
                    throw new DeployRefusedException($"The target platform could not be checked: {why}. Pass allowUnverifiedPlatform only when the target cannot be read and the build platform is known to be right. Nothing was built or downloaded.");
                throw new DeployRefusedException($"The target platform could not be checked: {why}. Nothing was built or downloaded.");
            }
            selection.PlatformStatus = $"platform not verified: {why}";
            return selection;
        }

        static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        static string Names(IEnumerable<VariantInfo> variants) => string.Join(", ", variants.Select(v => v.Name));
    }

    public class DeployRefusedException : Exception
    {
        public DeployRefusedException(string message) : base(message) { }
    }
}
