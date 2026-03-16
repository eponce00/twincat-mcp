using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using EnvDTE80;
using TCatSysManagerLib;
using TcAutomation.Core;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Export a PLC project to a TwinCAT .library artifact.
    /// </summary>
    public class GenerateLibraryCommand
    {
        private const int MaxAttemptLogsPerMethod = 8;

        public class GenerateLibraryResult
        {
            public bool Success { get; set; }
            public string Message { get; set; }
            public string ErrorMessage { get; set; }
            public string SolutionPath { get; set; }
            public string PlcName { get; set; }
            public string OutputLibraryPath { get; set; }
            public bool BuildSkipped { get; set; }
            public bool DryRun { get; set; }
        }

        public static GenerateLibraryResult Execute(
            string solutionPath,
            string plcName,
            string libraryLocation = null,
            string tcVersion = null,
            bool skipBuild = false,
            bool dryRun = false)
        {
            var result = new GenerateLibraryResult
            {
                SolutionPath = solutionPath,
                PlcName = plcName,
                BuildSkipped = skipBuild,
                DryRun = dryRun
            };

            VisualStudioInstance vsInstance = null;

            try
            {
                if (string.IsNullOrWhiteSpace(solutionPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "Solution path is required";
                    return result;
                }

                if (!File.Exists(solutionPath))
                {
                    result.Success = false;
                    result.ErrorMessage = $"Solution file not found: {solutionPath}";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(plcName))
                {
                    result.Success = false;
                    result.ErrorMessage = "PLC project name is required";
                    return result;
                }

                MessageFilter.Register();

                var tcProjectPath = TcFileUtilities.FindTwinCATProjectFile(solutionPath);
                if (string.IsNullOrEmpty(tcProjectPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "No TwinCAT project (.tsproj) found in solution";
                    return result;
                }

                var projectTcVersion = TcFileUtilities.GetTcVersion(tcProjectPath);
                if (string.IsNullOrEmpty(projectTcVersion))
                {
                    result.Success = false;
                    result.ErrorMessage = "Could not determine TwinCAT version from project";
                    return result;
                }

                vsInstance = new VisualStudioInstance(solutionPath, projectTcVersion, tcVersion);
                vsInstance.Load();
                vsInstance.LoadSolution();

                var automation = new AutomationInterface(vsInstance);
                if (automation.PlcTreeItem.ChildCount <= 0)
                {
                    result.Success = false;
                    result.ErrorMessage = "No PLC projects found in TwinCAT solution";
                    return result;
                }

                if (!skipBuild)
                {
                    vsInstance.CleanSolution();
                    vsInstance.BuildSolution();

                    var errorItems = vsInstance.GetErrorItems();
                    int errorCount = CountBuildErrors(errorItems);
                    if (errorCount > 0)
                    {
                        result.Success = false;
                        result.ErrorMessage = $"Build failed with {errorCount} error(s)";
                        return result;
                    }
                }

                var plcProject = FindPlcProjectByName(automation.PlcTreeItem, plcName);
                if (plcProject == null)
                {
                    result.Success = false;
                    result.ErrorMessage = $"PLC project '{plcName}' not found";
                    return result;
                }

                string outputLibraryPath = ResolveOutputLibraryPath(solutionPath, plcName, libraryLocation);
                result.OutputLibraryPath = outputLibraryPath;

                if (dryRun)
                {
                    result.Success = true;
                    result.Message = $"Dry run successful. Library would be generated at: {outputLibraryPath}";
                    return result;
                }

                string outputDirectory = Path.GetDirectoryName(outputLibraryPath);
                if (string.IsNullOrEmpty(outputDirectory))
                {
                    result.Success = false;
                    result.ErrorMessage = $"Could not resolve output directory from path: {outputLibraryPath}";
                    return result;
                }

                Directory.CreateDirectory(outputDirectory);
                BackupExistingLibraryWithTimestamp(outputLibraryPath);

                bool exported = TryExportLibrary(plcProject, outputLibraryPath);
                if (!exported || !File.Exists(outputLibraryPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "Library export failed. No compatible TwinCAT export API was found for this project/version.";
                    return result;
                }

                result.Success = true;
                result.Message = $"Library generated successfully: {outputLibraryPath}";
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                return result;
            }
            finally
            {
                vsInstance?.Close();
                MessageFilter.Revoke();
            }
        }

        private static string ResolveOutputLibraryPath(string solutionPath, string plcName, string libraryLocation)
        {
            string solutionDirectory = Path.GetDirectoryName(Path.GetFullPath(solutionPath));
            if (string.IsNullOrWhiteSpace(solutionDirectory))
            {
                throw new InvalidOperationException("Could not determine solution directory.");
            }

            if (string.IsNullOrWhiteSpace(libraryLocation))
            {
                return Path.Combine(solutionDirectory, $"{plcName}.library");
            }

            string resolvedLocation = Path.GetFullPath(libraryLocation);

            if (Directory.Exists(resolvedLocation) ||
                resolvedLocation.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
                resolvedLocation.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                return Path.Combine(resolvedLocation, $"{plcName}.library");
            }

            string extension = Path.GetExtension(resolvedLocation);
            if (string.IsNullOrEmpty(extension))
            {
                return Path.Combine(resolvedLocation, $"{plcName}.library");
            }

            if (extension.Equals(".library", StringComparison.OrdinalIgnoreCase))
            {
                return resolvedLocation;
            }

            throw new ArgumentException(
                $"Unsupported libraryLocation '{libraryLocation}'. Provide a directory path or a .library file path.");
        }

        private static ITcSmTreeItem FindPlcProjectByName(ITcSmTreeItem plcRoot, string plcName)
        {
            for (int i = 1; i <= plcRoot.ChildCount; i++)
            {
                ITcSmTreeItem item = plcRoot.Child[i];
                if (item.Name.Equals(plcName, StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }

            return null;
        }

        private static bool TryExportLibrary(ITcSmTreeItem plcProject, string outputLibraryPath)
        {
            if (TryTypedIecProjectExport(plcProject, outputLibraryPath) && File.Exists(outputLibraryPath))
            {
                return true;
            }

            var exportTargets = GetExportTargets(plcProject);

            var candidateMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "SaveAsLibrary",
                "SaveAsLibrary2",
                "SaveLibrary",
                "ExportLibrary",
                "CreateLibrary",
                "GenerateLibrary",
                "SaveAsCompiledLibrary",
                "ExportCompiledLibrary",
                "SaveAsLibraryProject",
                "ExportToLibrary",
                "CreateLibraryFile",
                "Save",
                "Export"
            };

            foreach (var exportTarget in exportTargets)
            {
                WriteProgress($"Probing export methods on target type '{exportTarget.GetType().FullName}'");

                foreach (MethodInfo method in exportTarget.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (method.Name.IndexOf("lib", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        method.Name.IndexOf("export", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        method.Name.IndexOf("save", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        candidateMethods.Add(method.Name);
                    }
                }

                foreach (string methodName in candidateMethods)
                {
                    if (TryInvokeComMethod(exportTarget, methodName, outputLibraryPath))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryTypedIecProjectExport(ITcSmTreeItem plcProject, string outputLibraryPath)
        {
            object nestedProject = TryGetNestedProject(plcProject);
            if (nestedProject != null && TryTypedIecProjectExportOnObject(nestedProject, outputLibraryPath))
            {
                return true;
            }

            object rootNestedProject = TryGetRootNestedProject(plcProject);
            if (rootNestedProject != null && TryTypedIecProjectExportOnObject(rootNestedProject, outputLibraryPath))
            {
                return true;
            }

            return TryTypedIecProjectExportOnObject(plcProject, outputLibraryPath);
        }

        private static List<object> GetExportTargets(ITcSmTreeItem plcProject)
        {
            var targets = new List<object>();

            void AddTarget(object target)
            {
                if (target == null)
                {
                    return;
                }

                if (!targets.Any(existing => ReferenceEquals(existing, target)))
                {
                    targets.Add(target);
                }
            }

            AddTarget(plcProject);

            try
            {
                AddTarget((_ITcPlcProject)plcProject);
            }
            catch
            {
                // Best effort for version-dependent COM interface.
            }

            AddTarget(TryGetNestedProject(plcProject));
            AddTarget(TryGetRootNestedProject(plcProject));

            return targets;
        }

        private static object TryGetNestedProject(ITcSmTreeItem plcProject)
        {
            try
            {
                _ITcPlcProject plcDisp = (_ITcPlcProject)plcProject;
                return plcDisp.NestedProject;
            }
            catch
            {
                return null;
            }
        }

        private static object TryGetRootNestedProject(ITcSmTreeItem plcProject)
        {
            try
            {
                ITcProjectRoot root = (ITcProjectRoot)plcProject;
                return root.NestedProject;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryTypedIecProjectExportOnObject(object candidate, string outputLibraryPath)
        {
            string candidateType = candidate.GetType().FullName;

            try
            {
                _ITcPlcIECProject iecDisp = (_ITcPlcIECProject)candidate;
                WriteProgress($"Trying typed _ITcPlcIECProject.SaveAsLibrary on '{candidateType}' with overwrite=true");
                iecDisp.SaveAsLibrary(outputLibraryPath, true);
                if (WaitForLibraryFile(outputLibraryPath))
                {
                    WriteProgress($"Typed _ITcPlcIECProject.SaveAsLibrary succeeded on '{candidateType}'");
                    return true;
                }

                WriteProgress($"Typed _ITcPlcIECProject.SaveAsLibrary did not produce output on '{candidateType}'");
            }
            catch (Exception ex)
            {
                WriteProgress($"Typed _ITcPlcIECProject.SaveAsLibrary failed on '{candidateType}': {ShortException(ex)}");
            }

            try
            {
                ITcPlcIECProject iec = (ITcPlcIECProject)candidate;
                WriteProgress($"Trying typed ITcPlcIECProject.SaveAsLibrary on '{candidateType}' with overwrite=true");
                iec.SaveAsLibrary(outputLibraryPath, true);
                if (WaitForLibraryFile(outputLibraryPath))
                {
                    WriteProgress($"Typed ITcPlcIECProject.SaveAsLibrary succeeded on '{candidateType}'");
                    return true;
                }

                WriteProgress($"Typed ITcPlcIECProject.SaveAsLibrary did not produce output on '{candidateType}'");
            }
            catch (Exception ex)
            {
                WriteProgress($"Typed ITcPlcIECProject.SaveAsLibrary failed on '{candidateType}': {ShortException(ex)}");
            }

            return false;
        }

        private static bool TryInvokeComMethod(object target, string methodName, string outputLibraryPath)
        {
            MethodInfo[] methods = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (methods.Length == 0)
            {
                return false;
            }

            WriteProgress($"Trying method group '{methodName}' ({methods.Length} overload(s)) on '{target.GetType().FullName}'");

            foreach (MethodInfo method in methods)
            {
                object[][] argumentSets = BuildArgumentSets(method.GetParameters(), outputLibraryPath);
                string methodSignature = FormatMethodSignature(method);
                int attemptsLogged = 0;

                WriteProgress($"  Overload: {methodSignature}; argument variants: {argumentSets.Length}");

                foreach (object[] args in argumentSets)
                {
                    try
                    {
                        if (attemptsLogged < MaxAttemptLogsPerMethod)
                        {
                            WriteProgress($"    Invoking {method.Name} with args: {FormatArgs(args)}");
                            attemptsLogged++;
                        }

                        method.Invoke(target, args);
                        if (WaitForLibraryFile(outputLibraryPath))
                        {
                            WriteProgress($"    Success: {methodSignature} with args: {FormatArgs(args)}");
                            return true;
                        }

                        if (attemptsLogged < MaxAttemptLogsPerMethod)
                        {
                            WriteProgress($"    No output file produced after {method.Name} with args: {FormatArgs(args)}");
                            attemptsLogged++;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (attemptsLogged < MaxAttemptLogsPerMethod)
                        {
                            WriteProgress($"    Failed {method.Name} with args {FormatArgs(args)}: {ShortException(ex)}");
                            attemptsLogged++;
                        }
                    }
                }

                if (argumentSets.Length > MaxAttemptLogsPerMethod)
                {
                    WriteProgress($"    Additional attempts omitted for brevity ({argumentSets.Length - MaxAttemptLogsPerMethod} more)");
                }
            }

            return false;
        }

        private static object[][] BuildArgumentSets(ParameterInfo[] parameters, string outputLibraryPath)
        {
            if (parameters.Length == 0)
            {
                return new[] { Array.Empty<object>() };
            }

            string outputDir = Path.GetDirectoryName(outputLibraryPath) ?? string.Empty;
            string outputName = Path.GetFileName(outputLibraryPath);
            string outputNameNoExt = Path.GetFileNameWithoutExtension(outputLibraryPath);

            var defaultArgs = new List<object>();
            foreach (ParameterInfo parameter in parameters)
            {
                Type paramType = parameter.ParameterType;

                if (paramType == typeof(string))
                {
                    defaultArgs.Add(defaultArgs.Count == 0 ? outputLibraryPath : outputNameNoExt);
                }
                else if (paramType == typeof(bool))
                {
                    defaultArgs.Add(false);
                }
                else if (paramType == typeof(int) || paramType == typeof(short) || paramType == typeof(byte))
                {
                    defaultArgs.Add(0);
                }
                else if (paramType == typeof(uint) || paramType == typeof(ushort))
                {
                    defaultArgs.Add((uint)0);
                }
                else if (paramType.IsEnum)
                {
                    Array values = Enum.GetValues(paramType);
                    defaultArgs.Add(values.Length > 0 ? values.GetValue(0) : Activator.CreateInstance(paramType));
                }
                else if (parameter.IsOptional)
                {
                    defaultArgs.Add(Type.Missing);
                }
                else
                {
                    return Array.Empty<object[]>();
                }
            }

            var variants = new List<object[]>
            {
                defaultArgs.ToArray()
            };

            // Boolean-heavy COM methods can invert behavior based on flags.
            if (parameters.Any(p => p.ParameterType == typeof(bool)))
            {
                object[] boolTrue = (object[])variants[0].Clone();
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].ParameterType == typeof(bool))
                    {
                        boolTrue[i] = true;
                    }
                }
                variants.Add(boolTrue);
            }

            // Alternate single-string argument patterns.
            if (parameters.Length >= 1 && parameters[0].ParameterType == typeof(string))
            {
                object[] dirOnly = (object[])variants[0].Clone();
                dirOnly[0] = outputDir;
                variants.Add(dirOnly);

                object[] nameOnly = (object[])variants[0].Clone();
                nameOnly[0] = outputName;
                variants.Add(nameOnly);
            }

            if (parameters.Length >= 2 && parameters[0].ParameterType == typeof(string) && parameters[1].ParameterType == typeof(string))
            {
                object[] alt = (object[])variants[0].Clone();
                alt[0] = outputDir;
                alt[1] = outputName;
                variants.Add(alt);

                object[] altNameNoExt = (object[])variants[0].Clone();
                altNameNoExt[0] = outputDir;
                altNameNoExt[1] = outputNameNoExt;
                variants.Add(altNameNoExt);
            }

            return variants
                .GroupBy(v => string.Join("|", v.Select(value => value?.ToString() ?? "<null>")))
                .Select(g => g.First())
                .ToArray();
        }

        private static bool WaitForLibraryFile(string outputLibraryPath)
        {
            const int maxWaitMs = 3000;
            const int pollMs = 150;
            int waited = 0;

            while (waited <= maxWaitMs)
            {
                if (File.Exists(outputLibraryPath))
                {
                    return true;
                }

                Thread.Sleep(pollMs);
                waited += pollMs;
            }

            return false;
        }

        private static void BackupExistingLibraryWithTimestamp(string outputLibraryPath)
        {
            if (!File.Exists(outputLibraryPath))
            {
                return;
            }

            string directory = Path.GetDirectoryName(outputLibraryPath) ?? string.Empty;
            string fileNameNoExt = Path.GetFileNameWithoutExtension(outputLibraryPath);
            string extension = Path.GetExtension(outputLibraryPath);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            string backupPath = Path.Combine(directory, $"{fileNameNoExt}.backup_{timestamp}{extension}");
            int suffix = 1;

            while (File.Exists(backupPath))
            {
                backupPath = Path.Combine(directory, $"{fileNameNoExt}.backup_{timestamp}_{suffix:00}{extension}");
                suffix++;
            }

            WriteProgress($"Existing output found. Renaming '{outputLibraryPath}' to '{backupPath}'");
            File.Move(outputLibraryPath, backupPath);
        }

        private static void WriteProgress(string message)
        {
            Console.Error.WriteLine($"[PROGRESS] generate-library: {message}");
        }

        private static string FormatMethodSignature(MethodInfo method)
        {
            string parameters = string.Join(
                ", ",
                method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}{(p.IsOptional ? " = <optional>" : string.Empty)}"));

            return $"{method.Name}({parameters})";
        }

        private static string FormatArgs(object[] args)
        {
            if (args == null || args.Length == 0)
            {
                return "<no args>";
            }

            return string.Join(", ", args.Select(a => a == null ? "null" : a == Type.Missing ? "<missing>" : a.ToString()));
        }

        private static string ShortException(Exception ex)
        {
            Exception baseEx = ex.InnerException ?? ex;
            return baseEx.Message;
        }

        private static int CountBuildErrors(ErrorItems errorItems)
        {
            int errorCount = 0;
            for (int i = 1; i <= errorItems.Count; i++)
            {
                ErrorItem item = errorItems.Item(i);
                if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh)
                {
                    errorCount++;
                }
            }

            return errorCount;
        }
    }
}
