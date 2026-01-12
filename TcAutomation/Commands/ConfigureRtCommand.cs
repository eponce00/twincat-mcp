using System;
using System.Text.Json;
using System.Xml.Linq;
using TcAutomation.Core;
using TCatSysManagerLib;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Configures real-time CPU settings.
    /// Based on TcUnit-Runner's AssignCPUCores logic.
    /// </summary>
    public static class ConfigureRtCommand
    {
        private const string REAL_TIME_CONFIG_SHORTCUT = "TIRS"; // Real-Time Configuration > Settings

        public static int Execute(string solutionPath, int? maxCpus, int? loadLimit, string? tcVersion)
        {
            VisualStudioInstance? vsInstance = null;
            var result = new ConfigureRtResult();

            try
            {
                // Find TwinCAT project and version
                string tsprojPath = TcFileUtilities.FindTwinCATProjectFile(solutionPath);
                if (string.IsNullOrEmpty(tsprojPath))
                {
                    result.ErrorMessage = "Could not find TwinCAT project file (.tsproj) in solution";
                    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                    return 1;
                }

                string projectTcVersion = TcFileUtilities.GetTcVersion(tsprojPath);

                // Load Visual Studio
                vsInstance = new VisualStudioInstance(solutionPath, projectTcVersion, tcVersion);
                vsInstance.Load();
                vsInstance.LoadSolution();

                var automation = new AutomationInterface(vsInstance);
                
                result.SolutionPath = solutionPath;

                // Get the real-time configuration tree item
                ITcSmTreeItem rtConfigTreeItem;
                try
                {
                    rtConfigTreeItem = automation.SystemManager.LookupTreeItem(REAL_TIME_CONFIG_SHORTCUT);
                }
                catch
                {
                    result.ErrorMessage = "Real-time configuration tree not found in project";
                    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                    return 1;
                }

                // Read current configuration
                string currentXml = rtConfigTreeItem.ProduceXml();
                var doc = XDocument.Parse(currentXml);

                var rtimeSetDef = doc.Root?.Element("RTimeSetDef");
                if (rtimeSetDef == null)
                {
                    result.ErrorMessage = "Could not read RTimeSetDef from real-time configuration";
                    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                    return 1;
                }

                // Read current values
                result.PreviousMaxCpus = rtimeSetDef.Element("MaxCPUs")?.Value ?? "unknown";
                result.PreviousAffinity = rtimeSetDef.Element("Affinity")?.Value ?? "unknown";

                // Get system info
                result.SystemCpuCount = Environment.ProcessorCount;

                // Apply changes if specified
                if (maxCpus.HasValue)
                {
                    var maxCpusElement = rtimeSetDef.Element("MaxCPUs");
                    if (maxCpusElement != null)
                    {
                        maxCpusElement.Value = maxCpus.Value.ToString();
                        maxCpusElement.RemoveAttributes(); // Remove NonWindowsCPUs attribute for shared cores
                    }
                    else
                    {
                        rtimeSetDef.Add(new XElement("MaxCPUs", maxCpus.Value.ToString()));
                    }

                    // Set affinity to use first N CPUs
                    ulong affinityMask = (1UL << maxCpus.Value) - 1;
                    string affinityHex = $"#x{affinityMask:X16}";
                    
                    var affinityElement = rtimeSetDef.Element("Affinity");
                    if (affinityElement != null)
                    {
                        affinityElement.Value = affinityHex;
                    }
                    else
                    {
                        rtimeSetDef.Add(new XElement("Affinity", affinityHex));
                    }
                }

                // Apply load limit if specified
                if (loadLimit.HasValue)
                {
                    var cpusElement = rtimeSetDef.Element("CPUs");
                    if (cpusElement == null)
                    {
                        cpusElement = new XElement("CPUs");
                        rtimeSetDef.Add(cpusElement);
                    }

                    // Configure CPU 0 (or all configured CPUs)
                    int cpuCount = maxCpus ?? 1;
                    cpusElement.RemoveAll();
                    
                    for (int i = 0; i < cpuCount; i++)
                    {
                        cpusElement.Add(new XElement("CPU",
                            new XAttribute("id", i.ToString()),
                            new XElement("LoadLimit", loadLimit.Value.ToString()),
                            new XElement("BaseTime", "10000"),  // 10ms default base time
                            new XElement("LatencyWarning", "500")
                        ));
                    }
                }

                // Apply the configuration
                rtConfigTreeItem.ConsumeXml(doc.ToString());

                // Re-read to confirm
                string newXml = rtConfigTreeItem.ProduceXml();
                var newDoc = XDocument.Parse(newXml);
                var newRtimeSetDef = newDoc.Root?.Element("RTimeSetDef");

                result.NewMaxCpus = newRtimeSetDef?.Element("MaxCPUs")?.Value ?? "unknown";
                result.NewAffinity = newRtimeSetDef?.Element("Affinity")?.Value ?? "unknown";
                result.Success = true;
                result.Message = $"RT configuration updated: MaxCPUs={result.NewMaxCpus}, LoadLimit={loadLimit ?? 80}%";

                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                return 1;
            }
            finally
            {
                vsInstance?.Close();
            }
        }
    }

    public class ConfigureRtResult
    {
        public string SolutionPath { get; set; } = "";
        public bool Success { get; set; }
        public string? Message { get; set; }
        public int SystemCpuCount { get; set; }
        public string PreviousMaxCpus { get; set; } = "";
        public string PreviousAffinity { get; set; } = "";
        public string NewMaxCpus { get; set; } = "";
        public string NewAffinity { get; set; } = "";
        public string? ErrorMessage { get; set; }
    }
}
