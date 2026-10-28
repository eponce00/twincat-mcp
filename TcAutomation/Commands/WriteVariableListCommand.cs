using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using TwinCAT.Ads;
using TcAutomation.Core;

namespace TcAutomation.Commands
{
    public static class WriteVariableListCommand
    {
        public static int Execute(string amsNetId, int port, string variablesJson)
        {
            var result = new WriteVariableListResult
            {
                AmsNetId = amsNetId,
                Port = port
            };

            // Parse input JSON
            Dictionary<string, string> variables;
            try
            {
                variables = JsonSerializer.Deserialize<Dictionary<string, string>>(variablesJson);
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Failed to parse variablesJson: {ex.Message}";
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
                return 1;
            }

            if (variables == null || variables.Count == 0)
            {
                result.ErrorMessage = "No variables specified";
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
                return 1;
            }

            if (variables.Count > 500)
            {
                result.ErrorMessage = "Maximum 500 variables per batch";
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
                return 1;
            }

            result.SymbolCount = variables.Count;

            try
            {
                using (var adsClient = new AdsClient())
                {
                    adsClient.Connect(amsNetId, port);

                    if (!adsClient.IsConnected)
                    {
                        result.ErrorMessage = $"Failed to connect to {amsNetId}:{port}";
                        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
                        return 1;
                    }

                    int remainingBytes = 8 * 1024 * 1024;
                    var stateInfo = adsClient.ReadState();
                    if (stateInfo.AdsState != AdsState.Run)
                    {
                        result.ErrorMessage = $"PLC is not running (state: {stateInfo.AdsState}). Cannot write variables.";
                        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
                        return 1;
                    }

                    foreach (var kvp in variables)
                    {
                        string symbolName = kvp.Key;
                        string value = kvp.Value;
                        var itemResult = new WriteVariableItemResult();
                        uint handle = 0;
                        bool handleCreated = false;

                        try
                        {
                            var symbolInfo = adsClient.ReadSymbol(symbolName);
                            AdsValues.ValidateSize(symbolInfo.Size);
                            if (symbolInfo.Size > remainingBytes) throw new ArgumentException("Batch symbol data exceeds 8 MiB.");
                            remainingBytes -= symbolInfo.Size;
                            itemResult.DataType = symbolInfo.TypeName;

                            handle = adsClient.CreateVariableHandle(symbolName);
                            handleCreated = true;

                            object previousValue = ReadTypedValue(adsClient, handle, symbolInfo.TypeName, symbolInfo.Size);
                            itemResult.PreviousValue = previousValue?.ToString() ?? "null";

                            object parsed = AdsValues.Parse(symbolInfo.TypeName, symbolInfo.Size, value);
                            itemResult.WriteDispatched = true;
                            if (parsed is string) adsClient.WriteAny(handle, parsed, new int[] { symbolInfo.Size });
                            else adsClient.WriteAny(handle, parsed);

                            object newValue = ReadTypedValue(adsClient, handle, symbolInfo.TypeName, symbolInfo.Size);
                            itemResult.NewValue = newValue?.ToString() ?? "null";

                            itemResult.Success = true;
                            result.SuccessCount++;
                        }
                        catch (AdsErrorException ex)
                        {
                            itemResult.ErrorMessage = $"ADS Error: {ex.ErrorCode} - {ex.Message}";
                            result.ErrorCount++;
                            result.OutcomeUnknown |= itemResult.WriteDispatched;
                        }
                        catch (Exception ex)
                        {
                            itemResult.ErrorMessage = ex.Message;
                            result.ErrorCount++;
                            result.OutcomeUnknown |= itemResult.WriteDispatched;
                        }
                        finally
                        {
                            if (handleCreated)
                                try { adsClient.DeleteVariableHandle(handle); }
                                catch (Exception cleanup) { itemResult.CleanupError = cleanup.Message; }
                        }

                        result.Results[symbolName] = itemResult;
                    }
                }

                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
                return result.Success ? 0 : 1;
            }
            catch (AdsErrorException ex)
            {
                result.ErrorMessage = $"ADS Error: {ex.ErrorCode} - {ex.Message}";
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
                return 1;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
                return 1;
            }
        }

        private static object ReadTypedValue(AdsClient client, uint handle, string typeName, int size)
        {
            string upperType = typeName.ToUpperInvariant();

            if (upperType == "BOOL")
                return client.ReadAny(handle, typeof(bool));
            if (upperType == "BYTE" || upperType == "USINT")
                return client.ReadAny(handle, typeof(byte));
            if (upperType == "SINT")
                return client.ReadAny(handle, typeof(sbyte));
            if (upperType == "WORD" || upperType == "UINT")
                return client.ReadAny(handle, typeof(ushort));
            if (upperType == "INT")
                return client.ReadAny(handle, typeof(short));
            if (upperType == "DWORD" || upperType == "UDINT")
                return client.ReadAny(handle, typeof(uint));
            if (upperType == "DINT")
                return client.ReadAny(handle, typeof(int));
            if (upperType == "LWORD" || upperType == "ULINT")
                return client.ReadAny(handle, typeof(ulong));
            if (upperType == "LINT")
                return client.ReadAny(handle, typeof(long));
            if (upperType == "REAL")
                return client.ReadAny(handle, typeof(float));
            if (upperType == "LREAL")
                return client.ReadAny(handle, typeof(double));
            if (upperType.StartsWith("STRING"))
                return client.ReadAny(handle, typeof(string), new int[] { size });

            // For arrays and structs, read as byte array
            byte[] data = new byte[size];
            client.Read(handle, data.AsMemory());
            return BitConverter.ToString(data).Replace("-", " ");
        }

    }

    public class WriteVariableListResult
    {
        public bool OutcomeUnknown { get; set; }
        public bool Success => ErrorMessage == null && SymbolCount > 0 && Results.Count == SymbolCount && ErrorCount == 0;
        public string AmsNetId { get; set; } = "";
        public int Port { get; set; }
        public int SymbolCount { get; set; }
        public int SuccessCount { get; set; }
        public int ErrorCount { get; set; }
        public Dictionary<string, WriteVariableItemResult> Results { get; set; } = new Dictionary<string, WriteVariableItemResult>();
        public string? ErrorMessage { get; set; }
    }

    public class WriteVariableItemResult
    {
        public bool WriteDispatched { get; set; }
        public string CleanupError { get; set; }
        public bool Success { get; set; }
        public string PreviousValue { get; set; } = "";
        public string NewValue { get; set; } = "";
        public string DataType { get; set; } = "";
        public string? ErrorMessage { get; set; }
    }
}
