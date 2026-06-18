using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TwinCAT.Ads;
using TwinCAT.TypeSystem;
using TcAutomation.Models;

namespace TcAutomation.Commands
{
    public static class AdsRecordCommand
    {
        private struct TriggerCondition
        {
            public string Variable;
            public string Operator;
            public double Threshold;
            public int Size;
            public string TypeName;
            public uint NotificationHandle;
        }

        public static AdsRecordResult Execute(string amsNetId, int port, string[] variables,
                                                int sampleTimeMs, double durationSec, string outputPath,
                                                string startTrigger = null, string stopTrigger = null,
                                                double maxTimeSec = 60.0)
        {
            var result = new AdsRecordResult
            {
                Variables = new List<string>(variables),
                SampleTimeMs = sampleTimeMs,
                ChannelCount = variables.Length
            };

            // Note: durationSec == 0 is valid and means "record until stop trigger or maxTimeSec fallback"

            var sw = Stopwatch.StartNew();

            try
            {
                using (var client = new AdsClient())
                {
                    client.Connect(new AmsAddress(amsNetId, port));

                    // Verify connection is alive (some ports like NC 501 report non-Run states but still work)
                    var deviceState = client.ReadState();
                    if (deviceState.AdsState == AdsState.Error || deviceState.AdsState == AdsState.Config)
                    {
                        result.Success = false;
                        result.ErrorMessage = $"Target is in {deviceState.AdsState} state — cannot record";
                        return result;
                    }

                    // Read symbol info for each variable
                    var symbolInfos = new List<(string Name, int Size, string TypeName)>();
                    foreach (var varName in variables)
                    {
                        try
                        {
                            var symbol = (IValueSymbol)client.ReadSymbol(varName);
                            var typeName = symbol.DataType?.Name?.ToUpperInvariant() ?? "LREAL";
                            var size = symbol.Size;
                            symbolInfos.Add((varName, size, typeName));
                        }
                        catch (Exception ex)
                        {
                            result.Success = false;
                            result.ErrorMessage = $"Failed to read symbol '{varName}': {ex.Message}";
                            return result;
                        }
                    }

                    // Parse triggers
                    TriggerCondition? parsedStartTrigger = null;
                    TriggerCondition? parsedStopTrigger = null;

                    if (!string.IsNullOrEmpty(startTrigger))
                    {
                        parsedStartTrigger = ParseTrigger(client, startTrigger, out string startErr);
                        if (startErr != null)
                        {
                            result.Success = false;
                            result.ErrorMessage = $"Invalid start trigger: {startErr}";
                            return result;
                        }
                    }

                    if (!string.IsNullOrEmpty(stopTrigger))
                    {
                        parsedStopTrigger = ParseTrigger(client, stopTrigger, out string stopErr);
                        if (stopErr != null)
                        {
                            result.Success = false;
                            result.ErrorMessage = $"Invalid stop trigger: {stopErr}";
                            return result;
                        }
                    }

                    // Shared state for notification handling
                    var latestValues = new double[variables.Length];
                    var latestTimestamp = DateTimeOffset.MinValue;
                    var valuesLock = new object();
                    var rows = new ConcurrentQueue<(DateTimeOffset Timestamp, double[] Values)>();
                    var handles = new uint[variables.Length];
                    var handleToIndex = new ConcurrentDictionary<uint, int>();

                    // Trigger state
                    var recording = parsedStartTrigger == null ? 1 : 0; // 1 = recording, 0 = waiting
                    var stopSignal = new ManualResetEventSlim(false);
                    uint startTriggerHandle = 0;
                    uint stopTriggerHandle = 0;

                    // Create notification settings
                    var settings = new NotificationSettings(AdsTransMode.Cyclic, sampleTimeMs, 0);

                    // Event handler for data channels
                    EventHandler<AdsNotificationEventArgs> handler = (sender, e) =>
                    {
                        // Check start trigger
                        if (parsedStartTrigger != null && e.Handle == parsedStartTrigger.Value.NotificationHandle)
                        {
                            var bytes = e.Data.ToArray();
                            var val = ConvertToDouble(bytes, parsedStartTrigger.Value.TypeName);
                            if (EvaluateCondition(val, parsedStartTrigger.Value.Operator, parsedStartTrigger.Value.Threshold))
                            {
                                Interlocked.CompareExchange(ref recording, 1, 0);
                            }
                            return;
                        }

                        // Check stop trigger
                        if (parsedStopTrigger != null && e.Handle == parsedStopTrigger.Value.NotificationHandle)
                        {
                            if (Volatile.Read(ref recording) == 1)
                            {
                                var bytes = e.Data.ToArray();
                                var val = ConvertToDouble(bytes, parsedStopTrigger.Value.TypeName);
                                if (EvaluateCondition(val, parsedStopTrigger.Value.Operator, parsedStopTrigger.Value.Threshold))
                                {
                                    stopSignal.Set();
                                }
                            }
                            return;
                        }

                        // Data channel
                        if (Volatile.Read(ref recording) != 1)
                            return;

                        int idx;
                        lock (valuesLock)
                        {
                            if (!handleToIndex.TryGetValue(e.Handle, out idx))
                                return;

                            var bytes = e.Data.ToArray();
                            latestValues[idx] = ConvertToDouble(bytes, symbolInfos[idx].TypeName);
                            latestTimestamp = e.TimeStamp;

                            // When channel 0 fires (or single variable), snapshot a row
                            if (idx == 0 || variables.Length == 1)
                            {
                                var snapshot = new double[variables.Length];
                                Array.Copy(latestValues, snapshot, variables.Length);
                                rows.Enqueue((latestTimestamp, snapshot));
                            }
                        }
                    };

                    client.AdsNotification += handler;

                    try
                    {
                        // Register trigger notifications first
                        if (parsedStartTrigger != null)
                        {
                            var t = parsedStartTrigger.Value;
                            t.NotificationHandle = client.AddDeviceNotification(
                                t.Variable, t.Size, settings, null);
                            parsedStartTrigger = t;
                            startTriggerHandle = t.NotificationHandle;
                        }

                        if (parsedStopTrigger != null)
                        {
                            var t = parsedStopTrigger.Value;
                            t.NotificationHandle = client.AddDeviceNotification(
                                t.Variable, t.Size, settings, null);
                            parsedStopTrigger = t;
                            stopTriggerHandle = t.NotificationHandle;
                        }

                        // Register data channel notifications
                        for (int i = 0; i < variables.Length; i++)
                        {
                            var handle = client.AddDeviceNotification(
                                variables[i],
                                symbolInfos[i].Size,
                                settings,
                                null);
                            handles[i] = handle;
                            handleToIndex.TryAdd(handle, i);
                        }

                        // === Trigger-wait phase ===
                        // maxTimeSec caps only how long we wait for the start trigger.
                        if (parsedStartTrigger != null)
                        {
                            result.StartTrigger = startTrigger;
                            using (var waitCts = new CancellationTokenSource())
                            {
                                waitCts.CancelAfter(TimeSpan.FromSeconds(maxTimeSec));
                                while (Volatile.Read(ref recording) == 0)
                                {
                                    if (waitCts.Token.WaitHandle.WaitOne(100))
                                    {
                                        // maxTime expired while waiting for trigger
                                        result.Success = true;
                                        result.TriggerStatus = "start_trigger_timeout";
                                        result.ErrorMessage = $"Start trigger not reached within {maxTimeSec}s";
                                        goto WriteOutput;
                                    }
                                }
                                result.TriggerStatus = "start_triggered";
                            }
                        }

                        // === Recording phase ===
                        // Cap:
                        //   - If durationSec > 0: stop after durationSec (or on stop trigger). maxTimeSec is NOT applied here.
                        //   - If durationSec == 0 (only possible with stopTrigger): use maxTimeSec as safety fallback.
                        double recordCapSec = durationSec > 0 ? durationSec : maxTimeSec;

                        using (var recordCts = new CancellationTokenSource())
                        {
                            recordCts.CancelAfter(TimeSpan.FromSeconds(recordCapSec));

                            while (!recordCts.IsCancellationRequested && !stopSignal.IsSet)
                            {
                                try { stopSignal.Wait(100, recordCts.Token); }
                                catch (OperationCanceledException) { break; }
                            }

                            if (stopSignal.IsSet)
                            {
                                result.StopTrigger = stopTrigger;
                                result.TriggerStatus = result.TriggerStatus == "start_triggered"
                                    ? "both_triggered" : "stop_triggered";
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Normal — timeout or linked token cancelled
                    }
                    finally
                    {
                        // Cleanup trigger notifications
                        if (startTriggerHandle != 0)
                        {
                            try { client.DeleteDeviceNotification(startTriggerHandle); }
                            catch { }
                        }
                        if (stopTriggerHandle != 0)
                        {
                            try { client.DeleteDeviceNotification(stopTriggerHandle); }
                            catch { }
                        }

                        // Cleanup data notifications
                        for (int i = 0; i < handles.Length; i++)
                        {
                            if (handles[i] != 0)
                            {
                                try { client.DeleteDeviceNotification(handles[i]); }
                                catch { /* ignore cleanup errors */ }
                            }
                        }

                        client.AdsNotification -= handler;
                    }

                    WriteOutput:
                    sw.Stop();

                    // Write CSV
                    var dir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    using (var writer = new StreamWriter(outputPath, false, Encoding.UTF8))
                    {
                        // Header
                        var header = "Timestamp," + string.Join(",", variables);
                        writer.WriteLine(header);

                        // Data rows
                        int sampleCount = 0;
                        while (rows.TryDequeue(out var row))
                        {
                            var sb = new StringBuilder();
                            sb.Append(row.Timestamp.ToString("o"));
                            for (int i = 0; i < row.Values.Length; i++)
                            {
                                sb.Append(',');
                                sb.Append(row.Values[i].ToString("G", CultureInfo.InvariantCulture));
                            }
                            writer.WriteLine(sb.ToString());
                            sampleCount++;
                        }

                        result.SamplesCollected = sampleCount;
                    }

                    var fileInfo = new FileInfo(outputPath);
                    result.FileSizeKB = fileInfo.Length / 1024;
                    result.OutputPath = outputPath;
                    result.DurationSeconds = sw.Elapsed.TotalSeconds;
                    if (result.TriggerStatus != "start_trigger_timeout")
                        result.Success = true;
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.Success = false;
                result.DurationSeconds = sw.Elapsed.TotalSeconds;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        private static TriggerCondition? ParseTrigger(AdsClient client, string trigger, out string error)
        {
            error = null;
            var operators = new[] { ">=", "<=", "!=", "==", ">", "<" };
            string foundOp = null;
            int opIndex = -1;

            foreach (var op in operators)
            {
                var idx = trigger.IndexOf(op);
                if (idx > 0)
                {
                    foundOp = op;
                    opIndex = idx;
                    break;
                }
            }

            if (foundOp == null)
            {
                error = $"No operator found in '{trigger}'. Use format: 'Variable > 10.0' (operators: >, <, >=, <=, ==, !=)";
                return null;
            }

            var varName = trigger.Substring(0, opIndex).Trim();
            var valueStr = trigger.Substring(opIndex + foundOp.Length).Trim();

            if (string.IsNullOrEmpty(varName))
            {
                error = "Variable name is empty in trigger expression";
                return null;
            }

            if (!double.TryParse(valueStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double threshold))
            {
                error = $"Cannot parse threshold value '{valueStr}' as a number";
                return null;
            }

            try
            {
                var symbol = (IValueSymbol)client.ReadSymbol(varName);
                var typeName = symbol.DataType?.Name?.ToUpperInvariant() ?? "LREAL";
                return new TriggerCondition
                {
                    Variable = varName,
                    Operator = foundOp,
                    Threshold = threshold,
                    Size = symbol.Size,
                    TypeName = typeName
                };
            }
            catch (Exception ex)
            {
                error = $"Failed to read trigger symbol '{varName}': {ex.Message}";
                return null;
            }
        }

        private static bool EvaluateCondition(double value, string op, double threshold)
        {
            switch (op)
            {
                case ">":  return value > threshold;
                case "<":  return value < threshold;
                case ">=": return value >= threshold;
                case "<=": return value <= threshold;
                case "==": return Math.Abs(value - threshold) < 1e-10;
                case "!=": return Math.Abs(value - threshold) >= 1e-10;
                default:   return false;
            }
        }

        private static double ConvertToDouble(byte[] bytes, string typeName)
        {
            switch (typeName)
            {
                case "LREAL":
                    return BitConverter.ToDouble(bytes, 0);
                case "REAL":
                    return (double)BitConverter.ToSingle(bytes, 0);
                case "LINT":
                    return (double)BitConverter.ToInt64(bytes, 0);
                case "DINT":
                    return (double)BitConverter.ToInt32(bytes, 0);
                case "INT":
                    return (double)BitConverter.ToInt16(bytes, 0);
                case "SINT":
                    return (double)(sbyte)bytes[0];
                case "ULINT":
                    return (double)BitConverter.ToUInt64(bytes, 0);
                case "UDINT":
                    return (double)BitConverter.ToUInt32(bytes, 0);
                case "UINT":
                    return (double)BitConverter.ToUInt16(bytes, 0);
                case "USINT":
                case "BYTE":
                    return (double)bytes[0];
                case "BOOL":
                    return bytes[0] != 0 ? 1.0 : 0.0;
                case "WORD":
                    return (double)BitConverter.ToUInt16(bytes, 0);
                case "DWORD":
                    return (double)BitConverter.ToUInt32(bytes, 0);
                case "LWORD":
                    return (double)BitConverter.ToUInt64(bytes, 0);
                default:
                    if (bytes.Length >= 8)
                        return BitConverter.ToDouble(bytes, 0);
                    else if (bytes.Length >= 4)
                        return (double)BitConverter.ToSingle(bytes, 0);
                    else
                        return (double)bytes[0];
            }
        }
    }
}
