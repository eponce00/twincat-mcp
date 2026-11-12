using System;
using System.Collections.Generic;
using System.Linq;
using TCatSysManagerLib;
using TcAutomation.Core;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Read-only: the project's variants with the target and build configuration each one deploys, the solution's
    /// current selection, and a deployment plan that visits each variant's target in turn. Only the context target
    /// is read over ADS unless probeTargets asks for every variant's target.
    /// </summary>
    public static class VariantsCommand
    {
        public static VariantsResult ExecuteInSession(VisualStudioInstance vsInstance, string solutionPath, string? contextTarget,
            IList<string>? order, bool probeTargets)
        {
            var result = new VariantsResult { SolutionPath = solutionPath, ContextTarget = contextTarget };
            try
            {
                var automation = new AutomationInterface(vsInstance);
                try { result.CurrentVariant = ((ITcSysManager14)automation.SystemManager).CurrentProjectVariant ?? ""; }
                catch (InvalidCastException) { result.CurrentVariant = ""; }
                var (configuration, platform) = SolutionPlatform.Active(vsInstance);
                result.ActiveConfiguration = configuration;
                result.ActivePlatform = platform;
                result.Variants = ProjectVariants.Read(TcFileUtilities.FindTwinCATProjectFile(solutionPath));

                var descriptions = new Dictionary<string, TargetDescription>();
                TargetDescription? Describe(string? netId)
                {
                    if (string.IsNullOrEmpty(netId)) return null;
                    if (netId != contextTarget && !probeTargets) return null;
                    if (!descriptions.TryGetValue(netId!, out var d)) descriptions[netId!] = d = TargetPlatform.Read(netId!);
                    return d;
                }

                // The plan: the requested order, else the context's own target first, then the rest as listed.
                var names = order != null && order.Count > 0
                    ? order.ToList()
                    : result.Variants.OrderBy(v => v.TargetNetId == contextTarget ? 0 : 1).Select(v => v.Name).ToList();
                foreach (var name in names)
                {
                    var v = result.Variants.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (v == null)
                    {
                        result.ErrorMessage = $"Variant '{name}' is not in the project. Variants: {string.Join(", ", result.Variants.Select(x => x.Name))}";
                        return result;
                    }
                    var d = Describe(v.TargetNetId);
                    string check = d == null ? "not read (pass probeTargets to read it)"
                        : !d.Read ? (d.ErrorMessage ?? "unreadable")
                        : d.ExpectedPlatform == null ? $"target type {d.TargetType}: platform not known"
                        : string.Equals(d.ExpectedPlatform, v.Platform, StringComparison.OrdinalIgnoreCase) ? $"matches {d.TargetType}"
                        : $"MISMATCH: {d.TargetType} needs {d.ExpectedPlatform}";
                    result.Plan.Add(new PlanStep
                    {
                        Order = result.Plan.Count + 1,
                        Variant = v.Name,
                        TargetNetId = v.TargetNetId,
                        Build = $"{v.Configuration}|{v.Platform}",
                        TargetType = d?.TargetType,
                        PlatformCheck = check,
                        Steps = new List<string>
                        {
                            $"context.open on {v.TargetNetId ?? "<variant has no target>"}",
                            $"workflow.deploy variant={v.Name} (dryRun first)",
                            "confirm the receipt shows Run on ports 10000 and 851",
                            "optional: hmi.preflight / hmi.publish in the same context",
                            "context.close",
                        },
                    });
                }
                result.Targets = descriptions.Values.ToList();
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }
            return result;
        }
    }

    public class PlanStep
    {
        public int Order { get; set; }
        public string Variant { get; set; } = "";
        public string? TargetNetId { get; set; }
        public string Build { get; set; } = "";
        public string? TargetType { get; set; }
        public string PlatformCheck { get; set; } = "";
        public List<string> Steps { get; set; } = new List<string>();
    }

    public class VariantsResult
    {
        public bool Success { get; set; }
        public string SolutionPath { get; set; } = "";
        public string? ContextTarget { get; set; }
        public string CurrentVariant { get; set; } = "";
        public string ActiveConfiguration { get; set; } = "";
        public string ActivePlatform { get; set; } = "";
        public List<VariantInfo> Variants { get; set; } = new List<VariantInfo>();
        public List<TargetDescription> Targets { get; set; } = new List<TargetDescription>();
        public List<PlanStep> Plan { get; set; } = new List<PlanStep>();
        public string? ErrorMessage { get; set; }
    }
}
