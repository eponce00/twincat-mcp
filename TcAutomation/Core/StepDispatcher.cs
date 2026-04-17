using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using TcAutomation.Commands;

namespace TcAutomation.Core
{
    /// <summary>
    /// Routes a single "step" (command + args) to the correct Command.ExecuteInSession
    /// (for shell-based commands) or Command.Execute (for ADS-only commands) while
    /// sharing a single VisualStudioInstance.
    ///
    /// Used by both BatchCommand (one shell open per batch) and HostCommand
    /// (one shell open per MCP server lifetime).
    /// </summary>
    public static class StepDispatcher
    {
        /// <summary>
        /// Commands that require a loaded Visual Studio / TcXaeShell.
        /// </summary>
        public static readonly HashSet<string> ShellCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "build", "info", "clean",
            "set-target", "activate", "restart",
            "list-plcs", "set-boot-project", "disable-io", "set-variant",
            "list-tasks", "configure-task", "configure-rt",
            "check-all-objects", "static-analysis",
            "generate-library", "get-error-list",
            "deploy", "run-tcunit"
        };

        /// <summary>
        /// Commands that talk to the PLC directly via ADS (no VS needed).
        /// Their result is captured by redirecting stdout.
        /// </summary>
        public static readonly HashSet<string> AdsCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "get-state", "set-state", "read-var", "write-var"
        };

        public static bool IsShellCommand(string command)
        {
            return !string.IsNullOrEmpty(command) && ShellCommands.Contains(command);
        }

        public static bool IsAdsCommand(string command)
        {
            return !string.IsNullOrEmpty(command) && AdsCommands.Contains(command);
        }

        public static bool IsSupported(string command)
        {
            return IsShellCommand(command) || IsAdsCommand(command);
        }

        /// <summary>
        /// Dispatch one step to the appropriate command implementation.
        /// For shell commands, vsInstance must be non-null and already have a solution loaded.
        /// For ADS commands, vsInstance is ignored.
        /// Returns the command's result object (JSON-serializable).
        /// </summary>
        public static object Dispatch(
            string command,
            JsonElement argsElement,
            string solutionPath,
            string? tcVersion,
            VisualStudioInstance? vsInstance)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                throw new ArgumentException("Step command is required");
            }

            bool hasArgs = argsElement.ValueKind == JsonValueKind.Object;

            if (ShellCommands.Contains(command))
            {
                if (vsInstance == null)
                {
                    throw new InvalidOperationException(
                        $"Step '{command}' requires Visual Studio but no shell was opened");
                }

                switch (command.ToLowerInvariant())
                {
                    case "build":
                    {
                        bool clean = GetBool(argsElement, "clean", hasArgs) ?? true;
                        return BuildCommand.ExecuteInSession(vsInstance, clean);
                    }
                    case "info":
                    {
                        return InfoCommand.ExecuteInSession(vsInstance, solutionPath);
                    }
                    case "clean":
                    {
                        return CleanCommand.ExecuteInSession(vsInstance, solutionPath);
                    }
                    case "set-target":
                    {
                        string amsNetId = GetString(argsElement, "amsNetId", hasArgs)
                            ?? throw new ArgumentException("set-target requires args.amsNetId");
                        return SetTargetCommand.ExecuteInSession(vsInstance, solutionPath, amsNetId);
                    }
                    case "activate":
                    {
                        string? amsNetId = GetString(argsElement, "amsNetId", hasArgs);
                        return ActivateCommand.ExecuteInSession(vsInstance, solutionPath, amsNetId);
                    }
                    case "restart":
                    {
                        string? amsNetId = GetString(argsElement, "amsNetId", hasArgs);
                        return RestartCommand.ExecuteInSession(vsInstance, solutionPath, amsNetId);
                    }
                    case "list-plcs":
                    {
                        string tcVer = tcVersion ?? "";
                        return ListPlcsCommand.ExecuteInSession(vsInstance, solutionPath, tcVer);
                    }
                    case "set-boot-project":
                    {
                        string? plcName = GetString(argsElement, "plcName", hasArgs) ?? GetString(argsElement, "plc", hasArgs);
                        bool autostart = GetBool(argsElement, "autostart", hasArgs) ?? true;
                        bool generate = GetBool(argsElement, "generate", hasArgs) ?? true;
                        return SetBootProjectCommand.ExecuteInSession(vsInstance, solutionPath, plcName, autostart, generate);
                    }
                    case "disable-io":
                    {
                        bool enable = GetBool(argsElement, "enable", hasArgs) ?? false;
                        return DisableIoCommand.ExecuteInSession(vsInstance, solutionPath, enable);
                    }
                    case "set-variant":
                    {
                        string? variantName = GetString(argsElement, "variantName", hasArgs) ?? GetString(argsElement, "variant", hasArgs);
                        bool getOnly = GetBool(argsElement, "getOnly", hasArgs) ?? GetBool(argsElement, "get", hasArgs) ?? false;
                        return SetVariantCommand.ExecuteInSession(vsInstance, solutionPath, variantName, getOnly);
                    }
                    case "list-tasks":
                    {
                        return ListTasksCommand.ExecuteInSession(vsInstance, solutionPath);
                    }
                    case "configure-task":
                    {
                        string taskName = GetString(argsElement, "taskName", hasArgs) ?? GetString(argsElement, "task", hasArgs)
                            ?? throw new ArgumentException("configure-task requires args.taskName");
                        bool? enable = GetBool(argsElement, "enable", hasArgs);
                        bool? autoStart = GetBool(argsElement, "autoStart", hasArgs) ?? GetBool(argsElement, "autostart", hasArgs);
                        return ConfigureTaskCommand.ExecuteInSession(vsInstance, solutionPath, taskName, enable, autoStart);
                    }
                    case "configure-rt":
                    {
                        int? maxCpus = GetInt(argsElement, "maxCpus", hasArgs);
                        int? loadLimit = GetInt(argsElement, "loadLimit", hasArgs);
                        return ConfigureRtCommand.ExecuteInSession(vsInstance, solutionPath, maxCpus, loadLimit);
                    }
                    case "check-all-objects":
                    {
                        string? plcName = GetString(argsElement, "plcName", hasArgs) ?? GetString(argsElement, "plc", hasArgs);
                        return CheckAllObjectsCommand.ExecuteInSession(vsInstance, plcName);
                    }
                    case "static-analysis":
                    {
                        bool checkAll = GetBool(argsElement, "checkAll", hasArgs) ?? true;
                        string? plcName = GetString(argsElement, "plcName", hasArgs) ?? GetString(argsElement, "plc", hasArgs);
                        return StaticAnalysisCommand.ExecuteInSession(vsInstance, checkAll, plcName);
                    }
                    case "generate-library":
                    {
                        string plcName = GetString(argsElement, "plcName", hasArgs) ?? GetString(argsElement, "plc", hasArgs)
                            ?? throw new ArgumentException("generate-library requires args.plcName");
                        string? libraryLocation = GetString(argsElement, "libraryLocation", hasArgs);
                        bool skipBuild = GetBool(argsElement, "skipBuild", hasArgs) ?? false;
                        bool dryRun = GetBool(argsElement, "dryRun", hasArgs) ?? false;
                        return GenerateLibraryCommand.ExecuteInSession(vsInstance, solutionPath, plcName, libraryLocation, skipBuild, dryRun);
                    }
                    case "get-error-list":
                    {
                        bool includeMessages = GetBool(argsElement, "includeMessages", hasArgs) ?? true;
                        bool includeWarnings = GetBool(argsElement, "includeWarnings", hasArgs) ?? true;
                        bool includeErrors = GetBool(argsElement, "includeErrors", hasArgs) ?? true;
                        int waitSeconds = GetInt(argsElement, "waitSeconds", hasArgs) ?? 0;
                        return GetErrorListCommand.ExecuteInSession(vsInstance, includeMessages, includeWarnings, includeErrors, waitSeconds);
                    }
                    case "deploy":
                    {
                        string amsNetId = GetString(argsElement, "amsNetId", hasArgs)
                            ?? throw new ArgumentException("deploy requires args.amsNetId");
                        string? plcName = GetString(argsElement, "plcName", hasArgs) ?? GetString(argsElement, "plc", hasArgs);
                        bool skipBuild = GetBool(argsElement, "skipBuild", hasArgs) ?? false;
                        bool dryRun = GetBool(argsElement, "dryRun", hasArgs) ?? false;
                        return DeployCommand.ExecuteInSession(vsInstance, solutionPath, amsNetId, plcName, skipBuild, dryRun);
                    }
                    case "run-tcunit":
                    {
                        string? amsNetId = GetString(argsElement, "amsNetId", hasArgs);
                        string? taskName = GetString(argsElement, "taskName", hasArgs) ?? GetString(argsElement, "task", hasArgs);
                        string? plcName = GetString(argsElement, "plcName", hasArgs) ?? GetString(argsElement, "plc", hasArgs);
                        int timeoutMinutes = GetInt(argsElement, "timeoutMinutes", hasArgs) ?? GetInt(argsElement, "timeout", hasArgs) ?? 10;
                        bool disableIo = GetBool(argsElement, "disableIo", hasArgs) ?? false;
                        bool skipBuild = GetBool(argsElement, "skipBuild", hasArgs) ?? false;
                        return RunTcUnitCommand.ExecuteInSession(
                            vsInstance, solutionPath,
                            amsNetId, taskName, plcName,
                            timeoutMinutes, disableIo, skipBuild);
                    }
                }
            }

            if (AdsCommands.Contains(command))
            {
                switch (command.ToLowerInvariant())
                {
                    case "get-state":
                    {
                        string amsNetId = GetString(argsElement, "amsNetId", hasArgs)
                            ?? throw new ArgumentException("get-state requires args.amsNetId");
                        int port = GetInt(argsElement, "port", hasArgs) ?? 851;
                        return ExecuteAdsStep(() => GetStateCommand.Execute(amsNetId, port));
                    }
                    case "set-state":
                    {
                        string amsNetId = GetString(argsElement, "amsNetId", hasArgs)
                            ?? throw new ArgumentException("set-state requires args.amsNetId");
                        int port = GetInt(argsElement, "port", hasArgs) ?? 851;
                        string state = GetString(argsElement, "state", hasArgs)
                            ?? throw new ArgumentException("set-state requires args.state");
                        return ExecuteAdsStep(() => SetStateCommand.Execute(amsNetId, port, state));
                    }
                    case "read-var":
                    {
                        string amsNetId = GetString(argsElement, "amsNetId", hasArgs)
                            ?? throw new ArgumentException("read-var requires args.amsNetId");
                        int port = GetInt(argsElement, "port", hasArgs) ?? 851;
                        string symbol = GetString(argsElement, "symbol", hasArgs) ?? GetString(argsElement, "var", hasArgs)
                            ?? throw new ArgumentException("read-var requires args.symbol");
                        return ExecuteAdsStep(() => ReadVariableCommand.Execute(amsNetId, port, symbol));
                    }
                    case "write-var":
                    {
                        string amsNetId = GetString(argsElement, "amsNetId", hasArgs)
                            ?? throw new ArgumentException("write-var requires args.amsNetId");
                        int port = GetInt(argsElement, "port", hasArgs) ?? 851;
                        string symbol = GetString(argsElement, "symbol", hasArgs) ?? GetString(argsElement, "var", hasArgs)
                            ?? throw new ArgumentException("write-var requires args.symbol");
                        string value = GetString(argsElement, "value", hasArgs)
                            ?? throw new ArgumentException("write-var requires args.value");
                        return ExecuteAdsStep(() => WriteVariableCommand.Execute(amsNetId, port, symbol, value));
                    }
                }
            }

            throw new NotSupportedException(
                $"Unsupported command: '{command}'. " +
                $"Shell: [{string.Join(", ", ShellCommands)}]. " +
                $"ADS: [{string.Join(", ", AdsCommands)}].");
        }

        /// <summary>
        /// Returns true if the result dict indicates success (best-effort inspection).
        /// Handles both JsonElement and POCO results.
        /// </summary>
        public static bool IsResultSuccessful(object? result)
        {
            if (result == null) return false;

            if (result is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("success", out var s))
                {
                    return s.ValueKind == JsonValueKind.True;
                }
                if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("Success", out var s2))
                {
                    return s2.ValueKind == JsonValueKind.True;
                }
                return true;
            }

            var successProp = result.GetType().GetProperty("Success");
            if (successProp != null && successProp.PropertyType == typeof(bool))
            {
                return (bool)successProp.GetValue(result)!;
            }

            return true;
        }

        public static string? ExtractErrorFromResult(object? result)
        {
            if (result == null) return null;

            if (result is JsonElement element && element.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "errorMessage", "ErrorMessage", "error" })
                {
                    if (element.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String)
                    {
                        return e.GetString();
                    }
                }
                return null;
            }

            foreach (var name in new[] { "ErrorMessage", "Error" })
            {
                var prop = result.GetType().GetProperty(name);
                if (prop != null && prop.PropertyType == typeof(string))
                {
                    return prop.GetValue(result) as string;
                }
            }

            return null;
        }

        /// <summary>
        /// The existing ADS command entry points write JSON to stdout and return 0/1.
        /// For batching/host we redirect stdout temporarily, capture the JSON, and
        /// return it parsed back as a JsonElement.
        /// </summary>
        private static object ExecuteAdsStep(Func<int> invoke)
        {
            var originalOut = Console.Out;
            var captured = new StringWriter();
            int exitCode;
            try
            {
                Console.SetOut(captured);
                exitCode = invoke();
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            string raw = captured.ToString().Trim();
            if (string.IsNullOrEmpty(raw))
            {
                return new { success = exitCode == 0 };
            }

            try
            {
                using var doc = JsonDocument.Parse(raw);
                return JsonSerializer.Deserialize<JsonElement>(doc.RootElement.GetRawText());
            }
            catch
            {
                return new { success = exitCode == 0, raw };
            }
        }

        // ===== JSON arg accessors (case-insensitive) =====

        public static string? GetString(JsonElement args, string name, bool hasArgs)
        {
            if (!hasArgs) return null;
            foreach (var property in args.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                        return property.Value.GetString();
                    if (property.Value.ValueKind == JsonValueKind.Null)
                        return null;
                    return property.Value.ToString();
                }
            }
            return null;
        }

        public static bool? GetBool(JsonElement args, string name, bool hasArgs)
        {
            if (!hasArgs) return null;
            foreach (var property in args.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    switch (property.Value.ValueKind)
                    {
                        case JsonValueKind.True: return true;
                        case JsonValueKind.False: return false;
                        case JsonValueKind.Null: return null;
                        case JsonValueKind.String:
                            if (bool.TryParse(property.Value.GetString(), out bool b)) return b;
                            return null;
                        default: return null;
                    }
                }
            }
            return null;
        }

        public static int? GetInt(JsonElement args, string name, bool hasArgs)
        {
            if (!hasArgs) return null;
            foreach (var property in args.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind == JsonValueKind.Number &&
                        property.Value.TryGetInt32(out int value))
                        return value;
                    if (property.Value.ValueKind == JsonValueKind.String &&
                        int.TryParse(property.Value.GetString(), out int parsed))
                        return parsed;
                    return null;
                }
            }
            return null;
        }
    }
}
