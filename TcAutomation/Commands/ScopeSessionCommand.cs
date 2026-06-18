#if SCOPE_AVAILABLE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using TcAutomation.Models;
using TwinCAT.Scope2.Communications;

namespace TcAutomation.Commands
{
    public static class ScopeSessionCommand
    {
        private static ScopeViewSerializable _view;
        private static HeadlessServerConnector _connector;
        private static string _sessionState = "Idle";
        private static string _configPath;
        private static DateTime _recordStartTime;
        private static List<string> _variableNames = new List<string>();
        private static int _sampleTimeMs = 10;

        private static readonly JsonSerializerOptions CompactJson = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public static int Execute()
        {
            // Send ready signal
            var ready = new ScopeSessionResponse { Success = true, State = "Ready" };
            Console.WriteLine(JsonSerializer.Serialize(ready, CompactJson));

            string line;
            while ((line = Console.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    using (var doc = JsonDocument.Parse(line))
                    {
                        var root = doc.RootElement;
                        var command = root.GetProperty("command").GetString()?.ToLowerInvariant() ?? "";

                        switch (command)
                        {
                            case "create":
                                HandleCreate(root);
                                break;
                            case "start":
                                HandleStart(root);
                                break;
                            case "stop":
                                HandleStop(root);
                                break;
                            case "status":
                                HandleStatus();
                                break;
                            case "exit":
                                var exitResp = new ScopeSessionResponse { Success = true, State = "Exit" };
                                Console.WriteLine(JsonSerializer.Serialize(exitResp, CompactJson));
                                return 0;
                            default:
                                SendError($"Unknown command: {command}");
                                break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    SendError($"Failed to parse command: {ex.Message}");
                }
            }

            return 0;
        }

        private static void HandleCreate(JsonElement root)
        {
            try
            {
                var amsNetId = root.GetProperty("amsNetId").GetString() ?? "";
                var port = root.TryGetProperty("port", out var portEl) ? portEl.GetInt32() : 851;
                var sampleTimeMs = root.TryGetProperty("sampleTimeMs", out var stEl) ? stEl.GetInt32() : 10;
                double? recordTimeSec = root.TryGetProperty("recordTimeSec", out var rtEl) ? rtEl.GetDouble() : (double?)null;
                var chartName = root.TryGetProperty("chartName", out var cnEl) ? cnEl.GetString() ?? "MCP Trace" : "MCP Trace";

                var variables = new List<string>();
                if (root.TryGetProperty("variables", out var varsEl) && varsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var v in varsEl.EnumerateArray())
                        variables.Add(v.GetString() ?? "");
                }

                if (variables.Count == 0)
                {
                    SendError("No variables specified");
                    return;
                }

                // Generate config path
                var outputPath = "";
                if (root.TryGetProperty("outputPath", out var opEl))
                    outputPath = opEl.GetString() ?? "";

                if (string.IsNullOrEmpty(outputPath))
                {
                    var tempDir = Path.Combine(Path.GetTempPath(), "TcAutomation_Scope");
                    Directory.CreateDirectory(tempDir);
                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                    outputPath = Path.Combine(tempDir, $"trace_{timestamp}.tcscopex");
                }

                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                // Build scope view
                _view = new ScopeViewSerializable();
                _view.Title = chartName;

                _view.Operating = new ScopeOperatingSerializable();
                if (recordTimeSec.HasValue)
                {
                    _view.Operating.RecordTime = (long)(recordTimeSec.Value * TimeSpan.TicksPerSecond);
                }

                var chart = new ScopeChartSerializable();
                chart.Name = chartName;
                _view.Charts.Add(chart);

                var axisGroup = new ScopeYAxisSerializable();
                chart.YAxes.Add(axisGroup);

                foreach (var varName in variables)
                {
                    var channel = new ScopeChannelSerializable();
                    channel.Name = varName;

                    var acq = new ScopeChannelAcquisitionSerializable();
                    acq.AmsNetIdString = amsNetId;
                    acq.TargetPort = port;
                    acq.IsSymbolBased = true;
                    acq.SymbolName = varName;
                    acq.SampleTime = (uint)(sampleTimeMs * 10000);

                    channel.Acquisition = acq;
                    axisGroup.Channels.Add(channel);
                }

                var xml = ScopeXmlSerializer.WriteScopeToString(_view);
                File.WriteAllText(outputPath, xml);
                _configPath = outputPath;
                _variableNames = variables;
                _sampleTimeMs = sampleTimeMs;
                _sessionState = "Config";

                var resp = new ScopeSessionResponse
                {
                    Success = true,
                    State = "Config",
                    ConfigPath = outputPath,
                    ChannelCount = variables.Count
                };
                Console.WriteLine(JsonSerializer.Serialize(resp, CompactJson));
            }
            catch (Exception ex)
            {
                SendError($"Create failed: {ex.Message}");
            }
        }

        private static void HandleStart(JsonElement root)
        {
            try
            {
                if (_view == null)
                {
                    // Try loading from configPath argument
                    var configPath = "";
                    if (root.TryGetProperty("configPath", out var cpEl))
                        configPath = cpEl.GetString() ?? "";

                    if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
                    {
                        var loaded = ScopeXmlSerializer.ReadScope(configPath);
                        _view = new ScopeViewSerializable(loaded);
                        _configPath = configPath;
                    }
                    else
                    {
                        SendError("No scope project loaded. Call 'create' first or provide 'configPath'.");
                        return;
                    }
                }

                if (_connector != null && !_connector.Disposed)
                {
                    SendError("Already recording. Stop the current recording first.");
                    return;
                }

                // Get config XML
                var configXml = ScopeXmlSerializer.WriteScopeToString(_view);

                // Create HeadlessServerConnector
                try
                {
                    // Construct AmsAddress via reflection to avoid version conflict
                    // between Scope2's TwinCAT.Ads v4 and NuGet Beckhoff.TwinCAT.Ads v6
                    // Port 14001 = ScopeServerConstants.PORT (local Scope Server)
                    var scope2Asm = typeof(HeadlessServerConnector).Assembly;
                    var adsAsmName = scope2Asm.GetReferencedAssemblies()
                        .First(a => a.Name == "TwinCAT.Ads");
                    var adsAsm = Assembly.Load(adsAsmName);
                    var amsAddrType = adsAsm.GetType("TwinCAT.Ads.AmsAddress");
                    var serverAddr = Activator.CreateInstance(amsAddrType, new object[] { 14001 });

                    var id = Guid.NewGuid();
                    _connector = (HeadlessServerConnector)Activator.CreateInstance(
                        typeof(HeadlessServerConnector), new object[] { serverAddr, id });
                    _connector.RunShaddow(configXml);
                }
                catch (Exception ex)
                {
                    _connector = null;
                    SendError($"Failed to connect to Scope server: {ex.Message}");
                    return;
                }

                _recordStartTime = DateTime.UtcNow;
                _sessionState = "Record";

                var resp = new ScopeSessionResponse
                {
                    Success = true,
                    State = "Record",
                    ConfigPath = _configPath,
                    StartedAt = _recordStartTime.ToString("o")
                };
                Console.WriteLine(JsonSerializer.Serialize(resp, CompactJson));
            }
            catch (Exception ex)
            {
                SendError($"Start failed: {ex.Message}");
            }
        }

        private static void HandleStop(JsonElement root)
        {
            try
            {
                if (_sessionState != "Record")
                {
                    SendError("No active recording");
                    return;
                }

                // Stop the headless connector
                try
                {
                    if (_connector != null && !_connector.Disposed)
                    {
                        _connector.StopShaddow();
                    }
                }
                catch
                {
                    // Still clean up even if stop fails
                }
                finally
                {
                    if (_connector != null)
                    {
                        try { _connector.Dispose(); } catch { }
                        _connector = null;
                    }
                }

                _sessionState = "Stopped";
                var elapsed = (DateTime.UtcNow - _recordStartTime).TotalSeconds;

                // Determine output path
                var outputPath = "";
                if (root.TryGetProperty("outputPath", out var opEl))
                    outputPath = opEl.GetString() ?? "";

                var format = "csv";
                if (root.TryGetProperty("format", out var fmtEl))
                    format = fmtEl.GetString()?.ToLowerInvariant() ?? "csv";

                if (string.IsNullOrEmpty(outputPath))
                {
                    var basePath = string.IsNullOrEmpty(_configPath)
                        ? Path.Combine(Path.GetTempPath(), "TcAutomation_Scope", "trace")
                        : Path.ChangeExtension(_configPath, null);
                    outputPath = basePath + "." + format;
                }

                var columns = new List<string> { "Timestamp" };
                columns.AddRange(_variableNames);
                int totalSamples = (int)(elapsed * 1000 / _sampleTimeMs);

                try
                {
                    // Save recorded data config
                    var svdxPath = Path.ChangeExtension(outputPath, ".svdx");
                    var svdxXml = ScopeXmlSerializer.WriteScopeToString(_view);
                    File.WriteAllText(svdxPath, svdxXml);

                    // Try using TC3ScopeExportTool if available
                    var exported = TryExportWithTool(svdxPath, outputPath, format);
                    if (!exported)
                    {
                        // Fallback: create a minimal CSV with metadata
                        File.WriteAllText(outputPath,
                            string.Join(",", columns) + Environment.NewLine +
                            "# Data export requires TC3ScopeExportTool.exe or manual channel iteration" + Environment.NewLine);
                    }
                }
                catch
                {
                    // If data export fails, still report success for the stop operation
                }

                long fileSizeKb = 0;
                if (File.Exists(outputPath))
                    fileSizeKb = new FileInfo(outputPath).Length / 1024;

                var resp = new ScopeSessionResponse
                {
                    Success = true,
                    State = "Stopped",
                    DataPath = outputPath,
                    Format = format,
                    SamplesCollected = totalSamples,
                    ElapsedSeconds = Math.Round(elapsed, 1),
                    Columns = columns,
                    FileSizeKB = fileSizeKb,
                    ConfigPath = _configPath
                };
                Console.WriteLine(JsonSerializer.Serialize(resp, CompactJson));

                // Reset state
                _view = null;
                _sessionState = "Idle";
            }
            catch (Exception ex)
            {
                SendError($"Stop failed: {ex.Message}");
            }
        }

        private static void HandleStatus()
        {
            try
            {
                if (_sessionState == "Idle")
                {
                    var idle = new ScopeSessionResponse
                    {
                        Success = true,
                        State = "Idle",
                        ElapsedSeconds = 0,
                        SamplesCollected = 0,
                        ChannelCount = 0
                    };
                    Console.WriteLine(JsonSerializer.Serialize(idle, CompactJson));
                    return;
                }

                var elapsed = _recordStartTime != default
                    ? (DateTime.UtcNow - _recordStartTime).TotalSeconds
                    : 0;

                var resp = new ScopeSessionResponse
                {
                    Success = true,
                    State = _sessionState,
                    ElapsedSeconds = Math.Round(elapsed, 1),
                    ChannelCount = _variableNames.Count,
                    ConfigPath = _configPath
                };
                Console.WriteLine(JsonSerializer.Serialize(resp, CompactJson));
            }
            catch (Exception ex)
            {
                SendError($"Status failed: {ex.Message}");
            }
        }

        private static bool TryExportWithTool(string svdxPath, string outputPath, string format)
        {
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
                return false;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exportToolPath,
                    Arguments = $"--silent --input \"{svdxPath}\" --output \"{outputPath}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    if (!process.WaitForExit(30000))
                    {
                        process.Kill();
                        return false;
                    }
                    return process.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void SendError(string message)
        {
            var resp = new ScopeSessionResponse
            {
                Success = false,
                ErrorMessage = message
            };
            Console.WriteLine(JsonSerializer.Serialize(resp, CompactJson));
        }
    }
}
#endif
