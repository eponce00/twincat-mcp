using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TcAutomation.Core;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Executes an ordered list of TwinCAT operations against a single shared
    /// Visual Studio / TcXaeShell instance. The shell is opened once up-front
    /// (only if any step actually needs it) and closed after all steps finish.
    ///
    /// This is the main "avoid paying the VS startup cost per call" entry point
    /// for the MCP server. ADS-only steps (read-var, write-var, get-state,
    /// set-state) do not open the shell; they run directly via ADS.
    ///
    /// Input JSON shape (supplied as a file path via --input, or via stdin when
    /// --input is "-"):
    /// {
    ///   "solutionPath": "C:/.../My.sln",     // required if any shell step is used
    ///   "tcVersion": "3.1.4026.17",           // optional
    ///   "stopOnError": true,                   // default true
    ///   "steps": [
    ///     { "id": "build",  "command": "build",     "args": { "clean": true } },
    ///     { "id": "target", "command": "set-target","args": { "amsNetId": "5.22.157.86.1.1" } },
    ///     { "id": "act",    "command": "activate",  "args": { "amsNetId": "5.22.157.86.1.1" } }
    ///   ]
    /// }
    /// </summary>
    public static class BatchCommand
    {
        private static readonly JsonSerializerOptions JsonReadOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private static readonly JsonSerializerOptions JsonWriteOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // Commands that need Visual Studio / TcXaeShell to be loaded with the solution.
        // Anything NOT in this set is treated as an ADS-only (direct) step.
        private static readonly HashSet<string> ShellCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "build", "info", "clean",
            "set-target", "activate", "restart",
            "list-plcs", "set-boot-project", "disable-io", "set-variant",
            "list-tasks", "configure-task", "configure-rt",
            "check-all-objects", "static-analysis",
            "generate-library", "get-error-list"
        };

        private static readonly HashSet<string> AdsCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "get-state", "set-state", "read-var", "write-var"
        };

        public static async Task<int> ExecuteAsync(string inputPath)
        {
            var overallStopwatch = Stopwatch.StartNew();
            var batchResult = new BatchResult();

            BatchInput input;
            try
            {
                input = LoadInput(inputPath);
            }
            catch (Exception ex)
            {
                batchResult.Success = false;
                batchResult.ErrorMessage = $"Failed to parse batch input: {ex.Message}";
                Console.WriteLine(JsonSerializer.Serialize(batchResult, JsonWriteOptions));
                return 1;
            }

            if (input.Steps == null || input.Steps.Count == 0)
            {
                batchResult.Success = false;
                batchResult.ErrorMessage = "Batch input has no steps";
                Console.WriteLine(JsonSerializer.Serialize(batchResult, JsonWriteOptions));
                return 1;
            }

            batchResult.TotalSteps = input.Steps.Count;

            bool stopOnError = input.StopOnError ?? true;
            bool needsShell = RequiresShell(input.Steps);

            if (needsShell && string.IsNullOrWhiteSpace(input.SolutionPath))
            {
                batchResult.Success = false;
                batchResult.ErrorMessage = "solutionPath is required because one or more steps need Visual Studio/TcXaeShell";
                Console.WriteLine(JsonSerializer.Serialize(batchResult, JsonWriteOptions));
                return 1;
            }

            VisualStudioInstance? vsInstance = null;
            bool messageFilterRegistered = false;
            Stopwatch? vsOpenStopwatch = null;

            try
            {
                if (needsShell)
                {
                    MessageFilter.Register();
                    messageFilterRegistered = true;

                    if (!File.Exists(input.SolutionPath))
                    {
                        batchResult.Success = false;
                        batchResult.ErrorMessage = $"Solution file not found: {input.SolutionPath}";
                        Console.WriteLine(JsonSerializer.Serialize(batchResult, JsonWriteOptions));
                        return 1;
                    }

                    string tcProjectPath = TcFileUtilities.FindTwinCATProjectFile(input.SolutionPath);
                    if (string.IsNullOrEmpty(tcProjectPath))
                    {
                        batchResult.Success = false;
                        batchResult.ErrorMessage = "No TwinCAT project (.tsproj) found in solution";
                        Console.WriteLine(JsonSerializer.Serialize(batchResult, JsonWriteOptions));
                        return 1;
                    }

                    string projectTcVersion = TcFileUtilities.GetTcVersion(tcProjectPath);
                    if (string.IsNullOrEmpty(projectTcVersion))
                    {
                        batchResult.Success = false;
                        batchResult.ErrorMessage = "Could not determine TwinCAT version from project";
                        Console.WriteLine(JsonSerializer.Serialize(batchResult, JsonWriteOptions));
                        return 1;
                    }

                    Console.Error.WriteLine("[PROGRESS] batch: Opening TwinCAT shell (shared across all steps)...");
                    vsOpenStopwatch = Stopwatch.StartNew();
                    vsInstance = new VisualStudioInstance(input.SolutionPath, projectTcVersion, input.TcVersion);
                    vsInstance.Load();
                    vsInstance.LoadSolution();
                    vsOpenStopwatch.Stop();
                    batchResult.VsOpenDurationMs = vsOpenStopwatch.Elapsed.TotalMilliseconds;
                    Console.Error.WriteLine($"[PROGRESS] batch: Shell ready ({batchResult.VsOpenDurationMs / 1000.0:F1}s). Starting steps.");
                }

                for (int i = 0; i < input.Steps.Count; i++)
                {
                    var step = input.Steps[i];
                    string stepId = string.IsNullOrWhiteSpace(step.Id) ? $"step{i + 1}" : step.Id!;
                    string command = step.Command ?? string.Empty;

                    Console.Error.WriteLine($"[PROGRESS] batch: [{i + 1}/{input.Steps.Count}] {stepId} -> {command}");

                    var stepResult = new BatchStepResult
                    {
                        Index = i,
                        Id = stepId,
                        Command = command
                    };

                    var stepStopwatch = Stopwatch.StartNew();
                    try
                    {
                        stepResult.Result = DispatchStep(command, step.Args, input, vsInstance);
                        stepResult.Success = IsResultSuccessful(stepResult.Result);
                        if (!stepResult.Success)
                        {
                            stepResult.Error = ExtractErrorFromResult(stepResult.Result);
                        }
                    }
                    catch (Exception ex)
                    {
                        stepResult.Success = false;
                        stepResult.Error = ex.Message;
                    }
                    stepStopwatch.Stop();
                    stepResult.DurationMs = stepStopwatch.Elapsed.TotalMilliseconds;

                    batchResult.Results.Add(stepResult);

                    if (stepResult.Success)
                    {
                        batchResult.CompletedSteps++;
                        Console.Error.WriteLine($"[PROGRESS] batch: [{i + 1}/{input.Steps.Count}] {stepId} OK ({stepResult.DurationMs / 1000.0:F1}s)");
                    }
                    else
                    {
                        batchResult.FailedStepIndex = i;
                        batchResult.StoppedAt = stepId;
                        Console.Error.WriteLine($"[PROGRESS] batch: [{i + 1}/{input.Steps.Count}] {stepId} FAILED: {stepResult.Error}");

                        if (stopOnError)
                        {
                            Console.Error.WriteLine("[PROGRESS] batch: stopOnError=true, aborting remaining steps");
                            break;
                        }
                    }
                }

                batchResult.Success = batchResult.FailedStepIndex < 0;
                if (!batchResult.Success && string.IsNullOrEmpty(batchResult.ErrorMessage))
                {
                    var failedStep = batchResult.Results.Count > 0 ? batchResult.Results[batchResult.Results.Count - 1] : null;
                    batchResult.ErrorMessage = failedStep?.Error ?? "Batch failed";
                }
            }
            catch (Exception ex)
            {
                batchResult.Success = false;
                batchResult.ErrorMessage = $"Batch aborted: {ex.Message}";
            }
            finally
            {
                try { vsInstance?.Close(); } catch { /* best effort */ }
                if (messageFilterRegistered)
                {
                    MessageFilter.Revoke();
                }
                overallStopwatch.Stop();
                batchResult.TotalDurationMs = overallStopwatch.Elapsed.TotalMilliseconds;
            }

            Console.WriteLine(JsonSerializer.Serialize(batchResult, JsonWriteOptions));
            return batchResult.Success ? 0 : 1;
        }

        private static BatchInput LoadInput(string inputPath)
        {
            string json;
            if (inputPath == "-")
            {
                json = Console.In.ReadToEnd();
            }
            else
            {
                if (!File.Exists(inputPath))
                {
                    throw new FileNotFoundException($"Batch input file not found: {inputPath}");
                }
                json = File.ReadAllText(inputPath);
            }

            var input = JsonSerializer.Deserialize<BatchInput>(json, JsonReadOptions);
            if (input == null)
            {
                throw new InvalidDataException("Batch input JSON was null");
            }
            return input;
        }

        private static bool RequiresShell(List<BatchStep> steps)
        {
            foreach (var step in steps)
            {
                if (!string.IsNullOrEmpty(step.Command) && ShellCommands.Contains(step.Command))
                {
                    return true;
                }
            }
            return false;
        }

        private static object DispatchStep(string command, JsonElement argsElement, BatchInput input, VisualStudioInstance? vsInstance)
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
                    throw new InvalidOperationException($"Step '{command}' requires Visual Studio but no shell was opened");
                }

                string solutionPath = input.SolutionPath ?? string.Empty;

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
                        string tcVersion = input.TcVersion ?? "";
                        return ListPlcsCommand.ExecuteInSession(vsInstance, solutionPath, tcVersion);
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

            throw new NotSupportedException($"Unsupported batch command: '{command}'. Supported shell commands: [{string.Join(", ", ShellCommands)}]. Supported ADS commands: [{string.Join(", ", AdsCommands)}].");
        }

        /// <summary>
        /// The existing ADS command entry points write JSON to stdout and return 0/1.
        /// For batching we want to capture their JSON output so the per-step result
        /// is structured. We redirect stdout temporarily, capture the JSON, and
        /// return it parsed back as a JsonElement so the caller can embed it.
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
                // Clone so it outlives the `using`.
                return JsonSerializer.Deserialize<JsonElement>(doc.RootElement.GetRawText());
            }
            catch
            {
                return new { success = exitCode == 0, raw };
            }
        }

        private static bool IsResultSuccessful(object? result)
        {
            if (result == null) return false;

            if (result is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("success", out var s))
                {
                    return s.ValueKind == JsonValueKind.True;
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

        private static string? ExtractErrorFromResult(object? result)
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

        private static string? GetString(JsonElement args, string name, bool hasArgs)
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

        private static bool? GetBool(JsonElement args, string name, bool hasArgs)
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

        private static int? GetInt(JsonElement args, string name, bool hasArgs)
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

        // ===== JSON DTOs =====

        public class BatchInput
        {
            public string? SolutionPath { get; set; }
            public string? TcVersion { get; set; }
            public bool? StopOnError { get; set; }
            public List<BatchStep> Steps { get; set; } = new List<BatchStep>();
        }

        public class BatchStep
        {
            public string? Id { get; set; }
            public string? Command { get; set; }
            public JsonElement Args { get; set; }
        }

        public class BatchStepResult
        {
            public int Index { get; set; }
            public string? Id { get; set; }
            public string? Command { get; set; }
            public bool Success { get; set; }
            public double DurationMs { get; set; }
            public string? Error { get; set; }
            public object? Result { get; set; }
        }

        public class BatchResult
        {
            public bool Success { get; set; }
            public string? ErrorMessage { get; set; }
            public int TotalSteps { get; set; }
            public int CompletedSteps { get; set; }
            public int FailedStepIndex { get; set; } = -1;
            public string? StoppedAt { get; set; }
            public double TotalDurationMs { get; set; }
            public double VsOpenDurationMs { get; set; }
            public List<BatchStepResult> Results { get; set; } = new List<BatchStepResult>();
        }
    }
}
