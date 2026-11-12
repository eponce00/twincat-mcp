using System;
using System.Collections.Generic;

namespace TcAutomation.Core
{
    /// <summary>
    /// The solution's active configuration and platform (XAE's toolbar), read and set through DTE. A shared shell
    /// keeps whatever the previous session chose, so every deployment selects its own before building.
    /// </summary>
    public static class SolutionPlatform
    {
        public static (string configuration, string platform) Active(VisualStudioInstance vs)
        {
            var active = (EnvDTE80.SolutionConfiguration2)vs.Dte.Solution.SolutionBuild.ActiveConfiguration;
            return (active.Name, active.PlatformName);
        }

        public static List<string> Available(VisualStudioInstance vs)
        {
            var names = new List<string>();
            foreach (EnvDTE.SolutionConfiguration candidate in vs.Dte.Solution.SolutionBuild.SolutionConfigurations)
            {
                var value = (EnvDTE80.SolutionConfiguration2)candidate;
                names.Add(value.Name + "|" + value.PlatformName);
            }
            return names;
        }

        /// <summary>Activate configuration|platform and verify it took; also align TwinCAT's target platform.</summary>
        public static void Select(VisualStudioInstance vs, AutomationInterface automation, string configuration, string platform)
        {
            var (name, current) = Active(vs);
            if (!Same(name, configuration) || !Same(current, platform))
            {
                EnvDTE80.SolutionConfiguration2? selected = null;
                foreach (EnvDTE.SolutionConfiguration candidate in vs.Dte.Solution.SolutionBuild.SolutionConfigurations)
                {
                    var value = (EnvDTE80.SolutionConfiguration2)candidate;
                    if (Same(value.Name, configuration) && Same(value.PlatformName, platform)) selected = value;
                }
                if (selected == null)
                    throw new DeployRefusedException($"The solution has no configuration '{configuration}|{platform}'. Available: {string.Join(", ", Available(vs))}");
                selected.Activate();
            }
            (name, current) = Active(vs);
            if (!Same(name, configuration) || !Same(current, platform))
                throw new InvalidOperationException($"Selecting '{configuration}|{platform}' was not verified: the solution is on '{name}|{current}'.");
            if (!Same(automation.ActiveTargetPlatform ?? "", platform))
            {
                automation.ActiveTargetPlatform = platform;
                if (!Same(automation.ActiveTargetPlatform ?? "", platform))
                    throw new InvalidOperationException($"TwinCAT's target platform stayed '{automation.ActiveTargetPlatform}' after selecting '{platform}'.");
            }
        }

        static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
