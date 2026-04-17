using System;
using System.Text.Json;
using System.Xml;
using TcAutomation.Core;
using TCatSysManagerLib;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Lists all real-time tasks in a TwinCAT solution.
    /// </summary>
    public static class ListTasksCommand
    {
        private const string REAL_TIME_TASKS_SHORTCUT = "TIRT"; // Real-Time Configuration > Additional Tasks

        public static int Execute(string solutionPath, string? tcVersion)
        {
            VisualStudioInstance? vsInstance = null;
            var result = new ListTasksResult();

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

                result = ExecuteInSession(vsInstance, solutionPath);
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                return result.Success ? 0 : 1;
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

        /// <summary>
        /// List real-time tasks using an already-open VS instance. Used by batch mode.
        /// </summary>
        public static ListTasksResult ExecuteInSession(VisualStudioInstance vsInstance, string solutionPath)
        {
            var result = new ListTasksResult { SolutionPath = solutionPath };

            try
            {
                var automation = new AutomationInterface(vsInstance);

                ITcSmTreeItem tasksTreeItem;
                try
                {
                    tasksTreeItem = automation.SystemManager.LookupTreeItem(REAL_TIME_TASKS_SHORTCUT);
                }
                catch
                {
                    result.ErrorMessage = "Real-time tasks tree not found in project";
                    return result;
                }

                result.TaskCount = tasksTreeItem.ChildCount;

                for (int i = 1; i <= tasksTreeItem.ChildCount; i++)
                {
                    var taskItem = tasksTreeItem.Child[i];
                    var taskInfo = new TaskInfo
                    {
                        Name = taskItem.Name,
                        Index = i
                    };

                    try
                    {
                        string xml = taskItem.ProduceXml();
                        ParseTaskXml(xml, taskInfo);
                    }
                    catch (Exception ex)
                    {
                        taskInfo.Error = ex.Message;
                    }

                    result.Tasks.Add(taskInfo);
                }

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        private static void ParseTaskXml(string xml, TaskInfo taskInfo)
        {
            var xmlDoc = new XmlDocument();
            xmlDoc.LoadXml(xml);

            // Get ItemName (actual task name)
            var itemNameNode = xmlDoc.SelectSingleNode("/TreeItem/ItemName");
            if (itemNameNode != null)
            {
                taskInfo.ItemName = itemNameNode.InnerText;
            }

            // Get Disabled state
            var disabledNode = xmlDoc.SelectSingleNode("/TreeItem/Disabled");
            if (disabledNode != null)
            {
                taskInfo.Disabled = disabledNode.InnerText.Equals("true", StringComparison.OrdinalIgnoreCase);
            }

            // Get AutoStart
            var autoStartNode = xmlDoc.SelectSingleNode("/TreeItem/TaskDef/AutoStart");
            if (autoStartNode != null)
            {
                taskInfo.AutoStart = autoStartNode.InnerText.Equals("true", StringComparison.OrdinalIgnoreCase);
            }

            // Get Priority
            var priorityNode = xmlDoc.SelectSingleNode("/TreeItem/TaskDef/Priority");
            if (priorityNode != null && int.TryParse(priorityNode.InnerText, out int priority))
            {
                taskInfo.Priority = priority;
            }

            // Get CycleTime
            var cycleTimeNode = xmlDoc.SelectSingleNode("/TreeItem/TaskDef/CycleTime");
            if (cycleTimeNode != null && int.TryParse(cycleTimeNode.InnerText, out int cycleTime))
            {
                taskInfo.CycleTimeUs = cycleTime / 10; // Convert from 100ns units to microseconds
            }
        }
    }

    public class ListTasksResult
    {
        public string SolutionPath { get; set; } = "";
        public bool Success { get; set; }
        public int TaskCount { get; set; }
        public System.Collections.Generic.List<TaskInfo> Tasks { get; set; } = new System.Collections.Generic.List<TaskInfo>();
        public string? ErrorMessage { get; set; }
    }

    public class TaskInfo
    {
        public string Name { get; set; } = "";
        public string ItemName { get; set; } = "";
        public int Index { get; set; }
        public bool Disabled { get; set; }
        public bool AutoStart { get; set; }
        public int Priority { get; set; }
        public int CycleTimeUs { get; set; }
        public string? Error { get; set; }
    }
}
