using System;
using System.Text.Json;
using System.Xml;
using TcAutomation.Core;
using TCatSysManagerLib;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Configures a real-time task (enable/disable, autostart).
    /// Based on TcUnit-Runner's task configuration logic.
    /// </summary>
    public static class ConfigureTaskCommand
    {
        private const string REAL_TIME_TASKS_SHORTCUT = "TIRT";

        public static int Execute(string solutionPath, string taskName, bool? enable, bool? autoStart, string? tcVersion)
        {
            VisualStudioInstance? vsInstance = null;
            var result = new ConfigureTaskResult();

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
                result.TaskName = taskName;

                // Get the real-time tasks tree item
                ITcSmTreeItem tasksTreeItem;
                try
                {
                    tasksTreeItem = automation.SystemManager.LookupTreeItem(REAL_TIME_TASKS_SHORTCUT);
                }
                catch
                {
                    result.ErrorMessage = "Real-time tasks tree not found in project";
                    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                    return 1;
                }

                // Find the specified task
                ITcSmTreeItem? targetTask = null;
                for (int i = 1; i <= tasksTreeItem.ChildCount; i++)
                {
                    var taskItem = tasksTreeItem.Child[i];
                    string xml = taskItem.ProduceXml();
                    string itemName = GetItemNameFromXml(xml);
                    
                    if (taskItem.Name.Equals(taskName, StringComparison.OrdinalIgnoreCase) ||
                        itemName.Equals(taskName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetTask = taskItem;
                        break;
                    }
                }

                if (targetTask == null)
                {
                    result.ErrorMessage = $"Task '{taskName}' not found. Available tasks: ";
                    for (int i = 1; i <= tasksTreeItem.ChildCount; i++)
                    {
                        result.ErrorMessage += tasksTreeItem.Child[i].Name;
                        if (i < tasksTreeItem.ChildCount) result.ErrorMessage += ", ";
                    }
                    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                    return 1;
                }

                // Read current XML
                string currentXml = targetTask.ProduceXml();
                
                // Get previous state
                ParseTaskState(currentXml, out bool wasDisabled, out bool wasAutoStart);
                result.PreviousDisabled = wasDisabled;
                result.PreviousAutoStart = wasAutoStart;

                // Apply changes
                bool newDisabled = enable.HasValue ? !enable.Value : wasDisabled;
                bool newAutoStart = autoStart.HasValue ? autoStart.Value : wasAutoStart;

                string newXml = SetDisabledAndAutoStart(currentXml, newDisabled, newAutoStart);
                
                if (string.IsNullOrEmpty(newXml))
                {
                    result.ErrorMessage = "Failed to modify task XML";
                    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                    return 1;
                }

                // Apply the changes
                targetTask.ConsumeXml(newXml);
                
                // Wait a moment for changes to take effect
                System.Threading.Thread.Sleep(1000);

                result.NewDisabled = newDisabled;
                result.NewAutoStart = newAutoStart;
                result.Success = true;
                result.Message = $"Task '{taskName}' configured: Disabled={newDisabled}, AutoStart={newAutoStart}";

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

        private static string GetItemNameFromXml(string xml)
        {
            var xmlDoc = new XmlDocument();
            xmlDoc.LoadXml(xml);
            var itemNameNode = xmlDoc.SelectSingleNode("/TreeItem/ItemName");
            return itemNameNode?.InnerText ?? "";
        }

        private static void ParseTaskState(string xml, out bool disabled, out bool autoStart)
        {
            disabled = false;
            autoStart = false;

            var xmlDoc = new XmlDocument();
            xmlDoc.LoadXml(xml);

            var disabledNode = xmlDoc.SelectSingleNode("/TreeItem/Disabled");
            if (disabledNode != null)
            {
                disabled = disabledNode.InnerText.Equals("true", StringComparison.OrdinalIgnoreCase);
            }

            var autoStartNode = xmlDoc.SelectSingleNode("/TreeItem/TaskDef/AutoStart");
            if (autoStartNode != null)
            {
                autoStart = autoStartNode.InnerText.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string SetDisabledAndAutoStart(string xml, bool disabled, bool autoStart)
        {
            var xmlDoc = new XmlDocument();
            xmlDoc.LoadXml(xml);

            var disabledNode = xmlDoc.SelectSingleNode("/TreeItem/Disabled");
            if (disabledNode != null)
            {
                disabledNode.InnerText = disabled.ToString().ToLower();
            }
            else
            {
                return "";
            }

            var autoStartNode = xmlDoc.SelectSingleNode("/TreeItem/TaskDef/AutoStart");
            if (autoStartNode != null)
            {
                autoStartNode.InnerText = autoStart.ToString().ToLower();
            }
            else
            {
                return "";
            }

            return xmlDoc.OuterXml;
        }
    }

    public class ConfigureTaskResult
    {
        public string SolutionPath { get; set; } = "";
        public string TaskName { get; set; } = "";
        public bool Success { get; set; }
        public string? Message { get; set; }
        public bool PreviousDisabled { get; set; }
        public bool PreviousAutoStart { get; set; }
        public bool NewDisabled { get; set; }
        public bool NewAutoStart { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
