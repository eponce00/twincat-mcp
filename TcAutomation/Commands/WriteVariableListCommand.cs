using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using TwinCAT.Ads;

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
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                return 1;
            }

            if (variables == null || variables.Count == 0)
            {
                result.ErrorMessage = "No variables specified";
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                return 1;
            }

            if (variables.Count > 500)
            {
                result.ErrorMessage = "Maximum 500 variables per batch";
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
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
                        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                        return 1;
                    }

                    var stateInfo = adsClient.ReadState();
                    if (stateInfo.AdsState != AdsState.Run)
                    {
                        result.ErrorMessage = $"PLC is not running (state: {stateInfo.AdsState}). Cannot write variables.";
                        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
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
                            itemResult.DataType = symbolInfo.TypeName;

                            handle = adsClient.CreateVariableHandle(symbolName);
                            handleCreated = true;

                            object previousValue = ReadTypedValue(adsClient, handle, symbolInfo.TypeName, symbolInfo.Size);
                            itemResult.PreviousValue = previousValue?.ToString() ?? "null";

                            WriteTypedValue(adsClient, handle, symbolInfo.TypeName, symbolInfo.Size, value);

                            object newValue = ReadTypedValue(adsClient, handle, symbolInfo.TypeName, symbolInfo.Size);
                            itemResult.NewValue = newValue?.ToString() ?? "null";

                            itemResult.Success = true;
                            result.SuccessCount++;
                        }
                        catch (AdsErrorException ex)
                        {
                            itemResult.ErrorMessage = $"ADS Error: {ex.ErrorCode} - {ex.Message}";
                            result.ErrorCount++;
                        }
                        catch (Exception ex)
                        {
                            itemResult.ErrorMessage = ex.Message;
                            result.ErrorCount++;
                        }
                        finally
                        {
                            if (handleCreated)
                                adsClient.DeleteVariableHandle(handle);
                        }

                        result.Results[symbolName] = itemResult;
                    }
                }

                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            catch (AdsErrorException ex)
            {
                result.ErrorMessage = $"ADS Error: {ex.ErrorCode} - {ex.Message}";
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                return 1;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
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

        private static void WriteTypedValue(AdsClient client, uint handle, string typeName, int size, string value)
        {
            string upperType = typeName.ToUpperInvariant();

            if (upperType == "BOOL")
            {
                bool boolValue = value.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("1", StringComparison.Ordinal);
                client.WriteAny(handle, boolValue);
            }
            else if (upperType == "BYTE" || upperType == "USINT")
            {
                client.WriteAny(handle, byte.Parse(value, CultureInfo.InvariantCulture));
            }
            else if (upperType == "SINT")
            {
                client.WriteAny(handle, sbyte.Parse(value, CultureInfo.InvariantCulture));
            }
            else if (upperType == "WORD" || upperType == "UINT")
            {
                client.WriteAny(handle, ushort.Parse(value, CultureInfo.InvariantCulture));
            }
            else if (upperType == "INT")
            {
                client.WriteAny(handle, short.Parse(value, CultureInfo.InvariantCulture));
            }
            else if (upperType == "DWORD" || upperType == "UDINT")
            {
                client.WriteAny(handle, uint.Parse(value, CultureInfo.InvariantCulture));
            }
            else if (upperType == "DINT")
            {
                client.WriteAny(handle, int.Parse(value, CultureInfo.InvariantCulture));
            }
            else if (upperType == "LWORD" || upperType == "ULINT")
            {
                client.WriteAny(handle, ulong.Parse(value, CultureInfo.InvariantCulture));
            }
            else if (upperType == "LINT")
            {
                client.WriteAny(handle, long.Parse(value, CultureInfo.InvariantCulture));
            }
            else if (upperType == "REAL")
            {
                client.WriteAny(handle, float.Parse(value, CultureInfo.InvariantCulture));
            }
            else if (upperType == "LREAL")
            {
                client.WriteAny(handle, double.Parse(value, CultureInfo.InvariantCulture));
            }
            else if (upperType.StartsWith("STRING"))
            {
                client.WriteAny(handle, value, new int[] { size });
            }
            else
            {
                throw new ArgumentException($"Unsupported type for writing: {typeName}");
            }
        }
    }

    public class WriteVariableListResult
    {
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
        public bool Success { get; set; }
        public string PreviousValue { get; set; } = "";
        public string NewValue { get; set; } = "";
        public string DataType { get; set; } = "";
        public string? ErrorMessage { get; set; }
    }
}
