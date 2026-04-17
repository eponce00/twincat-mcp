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
    /// Heavy lifting for per-step dispatch now lives in Core.StepDispatcher so
    /// that HostCommand (persistent host) can share the exact same logic.
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
                        stepResult.Result = StepDispatcher.Dispatch(
                            command,
                            step.Args,
                            input.SolutionPath ?? string.Empty,
                            input.TcVersion,
                            vsInstance);
                        stepResult.Success = StepDispatcher.IsResultSuccessful(stepResult.Result);
                        if (!stepResult.Success)
                        {
                            stepResult.Error = StepDispatcher.ExtractErrorFromResult(stepResult.Result);
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
                if (!string.IsNullOrEmpty(step.Command) && StepDispatcher.IsShellCommand(step.Command))
                {
                    return true;
                }
            }
            return false;
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
