#if SCOPE_AVAILABLE
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using TcAutomation.Models;

namespace TcAutomation.Commands
{
    public static class ScopeExportCommand
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public static int Execute(string inputPath, string outputPath, string format)
        {
            var result = new ScopeExportResult
            {
                InputPath = inputPath,
                Format = format ?? "csv"
            };

            try
            {
                if (string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = $"Input file not found: {inputPath}";
                    Console.WriteLine(JsonSerializer.Serialize(result, JsonOpts));
                    return 1;
                }

                // Derive output path if not specified
                if (string.IsNullOrEmpty(outputPath))
                {
                    var ext = string.Equals(format, "tdms", StringComparison.OrdinalIgnoreCase) ? ".tdms" : ".csv";
                    outputPath = Path.ChangeExtension(inputPath, ext);
                }

                result.OutputPath = outputPath;

                // Search for TC3ScopeExportTool.exe
                var searchPaths = new[]
                {
                    @"C:\Program Files (x86)\Beckhoff\TwinCAT\Functions\TF3300-Scope-Server\TC3ScopeExportTool.exe",
                    @"C:\Program Files (x86)\Beckhoff\TwinCAT\Functions\TE130x-Scope-View\TC3ScopeExportTool.exe",
                    @"C:\Program Files\Beckhoff\TwinCAT\Functions\TF3300-Scope-Server\TC3ScopeExportTool.exe",
                    @"C:\Program Files\Beckhoff\TwinCAT\Functions\TE130x-Scope-View\TC3ScopeExportTool.exe"
                };

                string exportToolPath = null;
                foreach (var p in searchPaths)
                {
                    if (File.Exists(p))
                    {
                        exportToolPath = p;
                        break;
                    }
                }

                if (exportToolPath == null)
                {
                    result.Success = false;
                    result.ErrorMessage = "TC3ScopeExportTool.exe not found. Ensure TF3300 Scope Server or TE130x Scope View is installed.";
                    Console.WriteLine(JsonSerializer.Serialize(result, JsonOpts));
                    return 1;
                }

                // Ensure output directory exists
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                // Run the export tool
                var psi = new ProcessStartInfo
                {
                    FileName = exportToolPath,
                    Arguments = $"--silent --input \"{inputPath}\" --output \"{outputPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    var stdout = process.StandardOutput.ReadToEnd();
                    var stderr = process.StandardError.ReadToEnd();

                    if (!process.WaitForExit(60000))
                    {
                        process.Kill();
                        result.Success = false;
                        result.ErrorMessage = "Export tool timed out after 60 seconds";
                        Console.WriteLine(JsonSerializer.Serialize(result, JsonOpts));
                        return 1;
                    }

                    if (process.ExitCode != 0)
                    {
                        result.Success = false;
                        result.ErrorMessage = $"Export tool exited with code {process.ExitCode}: {stderr}".Trim();
                        Console.WriteLine(JsonSerializer.Serialize(result, JsonOpts));
                        return 1;
                    }
                }

                // Report success
                if (File.Exists(outputPath))
                {
                    result.FileSizeKB = new FileInfo(outputPath).Length / 1024;
                }

                result.Success = true;
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOpts));
                return 0;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOpts));
                return 1;
            }
        }
    }
}
#endif
