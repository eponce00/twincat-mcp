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
            WaitReadable(vs, TimeSpan.FromSeconds(30));
            return ReadActive(vs);
        }

        static (string configuration, string platform) ReadActive(VisualStudioInstance vs)
        {
            var active = vs.Dte.Solution.SolutionBuild.ActiveConfiguration as EnvDTE80.SolutionConfiguration2;
            return (active?.Name!, active?.PlatformName!);
        }

        public static List<string> Available(VisualStudioInstance vs)
        {
            var names = new List<string>();
            foreach (var value in Configurations(vs))
                if (value != null) names.Add(value.Name + "|" + value.PlatformName);
            return names;
        }

        /// <summary>
        /// The solution's configurations by index. Enumerating SolutionConfigurations with foreach intermittently
        /// yielded null entries (an intermittent NullReferenceException in deploy); an entry DTE does not return is
        /// null here, and <see cref="WaitReadable"/> retries.
        /// </summary>
        static List<EnvDTE80.SolutionConfiguration2?> Configurations(VisualStudioInstance vs)
        {
            var all = vs.Dte.Solution.SolutionBuild.SolutionConfigurations;
            var list = new List<EnvDTE80.SolutionConfiguration2?>();
            for (int i = 1; i <= all.Count; i++)
                list.Add(all.Item(i) as EnvDTE80.SolutionConfiguration2);
            return list;
        }

        /// <summary>
        /// Wait until the active configuration and every configuration have a name and platform, at most
        /// <paramref name="timeout"/>; then go on with what is readable (an unreadable entry never matches).
        /// </summary>
        static List<EnvDTE80.SolutionConfiguration2?> WaitReadable(VisualStudioInstance vs, TimeSpan timeout)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                var (name, current) = ReadActive(vs);
                var configurations = Configurations(vs);
                bool readable = name != null && current != null
                    && configurations.TrueForAll(c => c != null && c.Name != null && c.PlatformName != null);
                if (readable) return configurations;
                if (clock.Elapsed >= timeout)
                {
                    Console.Error.WriteLine($"[DEBUG] Some solution configurations were still unreadable after {timeout.TotalSeconds:0} s");
                    return configurations;
                }
                Console.Error.WriteLine("[DEBUG] Solution configurations not readable yet; waiting");
                System.Threading.Thread.Sleep(500);
            }
        }
        /// <summary>Activate configuration|platform and verify it took; also align TwinCAT's target platform.</summary>
        public static void Select(VisualStudioInstance vs, AutomationInterface automation, string configuration, string platform)
        {
            var (name, current) = Active(vs);
            if (!Same(name, configuration) || !Same(current, platform))
            {
                EnvDTE80.SolutionConfiguration2? selected = null;
                foreach (var value in WaitReadable(vs, TimeSpan.FromSeconds(30)))
                    if (value != null && Same(value.Name, configuration) && Same(value.PlatformName, platform)) selected = value;
                if (selected == null)
                    throw new DeployRefusedException($"The solution has no configuration '{configuration}|{platform}'. Available: {string.Join(", ", Available(vs))}");
                selected.Activate();
            }
            (name, current) = ReadActive(vs);
            if (!Same(name, configuration) || !Same(current, platform))
                throw new InvalidOperationException($"Selecting '{configuration}|{platform}' was not verified: the solution is on '{name}|{current}'.");
            if (!Same(automation.ActiveTargetPlatform ?? "", platform))
            {
                automation.ActiveTargetPlatform = platform;
                if (!Same(automation.ActiveTargetPlatform ?? "", platform))
                    throw new InvalidOperationException($"TwinCAT's target platform stayed '{automation.ActiveTargetPlatform}' after selecting '{platform}'.");
            }
        }

        static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
