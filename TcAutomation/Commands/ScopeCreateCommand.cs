#if SCOPE_AVAILABLE
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using TcAutomation.Models;
using TwinCAT.Scope2.Communications;

// ReSharper disable once CheckNamespace

namespace TcAutomation.Commands
{
    public static class ScopeCreateCommand
    {
        public static int Execute(string amsNetId, int port, string[] variables,
                                   int sampleTimeMs, double? recordTimeSec,
                                   string outputPath, string chartName)
        {
            var result = new ScopeCreateResult
            {
                SampleTimeMs = sampleTimeMs,
                RecordTimeSec = recordTimeSec,
                Variables = variables.ToList()
            };

            try
            {
                // Generate output path if not specified
                if (string.IsNullOrEmpty(outputPath))
                {
                    var tempDir = Path.Combine(Path.GetTempPath(), "TcAutomation_Scope");
                    Directory.CreateDirectory(tempDir);
                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                    outputPath = Path.Combine(tempDir, $"trace_{timestamp}.tcscopex");
                }

                // Ensure output directory exists
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                // Create ScopeViewSerializable
                var view = new ScopeViewSerializable();
                view.Title = string.IsNullOrEmpty(chartName) ? "MCP Trace" : chartName;

                // Set operating parameters
                view.Operating = new ScopeOperatingSerializable();
                if (recordTimeSec.HasValue)
                {
                    view.Operating.RecordTime = (long)(recordTimeSec.Value * TimeSpan.TicksPerSecond);
                }

                // Create chart
                var chart = new ScopeChartSerializable();
                chart.Name = string.IsNullOrEmpty(chartName) ? "MCP Trace" : chartName;
                view.Charts.Add(chart);

                // Create axis group
                var axisGroup = new ScopeYAxisSerializable();
                chart.YAxes.Add(axisGroup);

                // For each variable, create Channel + Acquisition
                foreach (var varName in variables)
                {
                    var channel = new ScopeChannelSerializable();
                    channel.Name = varName;

                    var acq = new ScopeChannelAcquisitionSerializable();
                    acq.AmsNetIdString = amsNetId;
                    acq.TargetPort = port;
                    acq.IsSymbolBased = true;
                    acq.SymbolName = varName;
                    acq.SampleTime = (uint)(sampleTimeMs * 10000); // Convert ms to 100ns ticks

                    channel.Acquisition = acq;
                    axisGroup.Channels.Add(channel);
                }

                // Save config — use WriteScopeToString + File.WriteAllText
                // because WriteScope(view, path) may fail with internal file access issues
                var xml = ScopeXmlSerializer.WriteScopeToString(view);
                File.WriteAllText(outputPath, xml);

                result.Success = true;
                result.ConfigPath = outputPath;
                result.ChannelCount = variables.Length;

                var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                Console.WriteLine(JsonSerializer.Serialize(result, jsonOpts));
                return 0;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                Console.WriteLine(JsonSerializer.Serialize(result, jsonOpts));
                return 1;
            }
        }
    }
}
#endif
