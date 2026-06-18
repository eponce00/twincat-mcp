using System;
using System.Collections.Generic;
using System.Text.Json;
using TwinCAT.Ads;

namespace TcAutomation.Commands
{
    public static class ReadVariableListCommand
    {
        public static int Execute(string amsNetId, int port, string[] symbols)
        {
            var result = new ReadVariableListResult
            {
                AmsNetId = amsNetId,
                Port = port
            };

            if (symbols == null || symbols.Length == 0)
            {
                result.ErrorMessage = "No symbols specified";
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                return 1;
            }

            if (symbols.Length > 500)
            {
                result.ErrorMessage = "Maximum 500 symbols per batch";
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                return 1;
            }

            result.SymbolCount = symbols.Length;

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
                        result.ErrorMessage = $"PLC is not running (state: {stateInfo.AdsState}). Cannot read variables.";
                        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                        return 1;
                    }

                    foreach (var symbolName in symbols)
                    {
                        var itemResult = new ReadVariableItemResult();
                        uint handle = 0;
                        bool handleCreated = false;

                        try
                        {
                            var symbolInfo = adsClient.ReadSymbol(symbolName);
                            itemResult.DataType = symbolInfo.TypeName;
                            itemResult.Size = symbolInfo.Size;

                            handle = adsClient.CreateVariableHandle(symbolName);
                            handleCreated = true;

                            object value = ReadTypedValue(adsClient, handle, symbolInfo.TypeName, symbolInfo.Size);
                            itemResult.Value = value?.ToString() ?? "null";
                            itemResult.RawValue = value;
                            itemResult.Success = true;
                        }
                        catch (AdsErrorException ex)
                        {
                            itemResult.Success = false;
                            itemResult.ErrorMessage = $"ADS Error: {ex.ErrorCode} - {ex.Message}";
                        }
                        catch (Exception ex)
                        {
                            itemResult.Success = false;
                            itemResult.ErrorMessage = ex.Message;
                        }
                        finally
                        {
                            if (handleCreated)
                                adsClient.DeleteVariableHandle(handle);
                        }

                        result.Results[symbolName] = itemResult;
                    }
                }

                foreach (var item in result.Results.Values)
                {
                    if (item.Success) result.SuccessCount++;
                    else result.ErrorCount++;
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

            byte[] data = new byte[size];
            client.Read(handle, data.AsMemory());
            return BitConverter.ToString(data).Replace("-", " ");
        }
    }

    public class ReadVariableListResult
    {
        public string AmsNetId { get; set; } = "";
        public int Port { get; set; }
        public int SymbolCount { get; set; }
        public int SuccessCount { get; set; }
        public int ErrorCount { get; set; }
        public Dictionary<string, ReadVariableItemResult> Results { get; set; } = new Dictionary<string, ReadVariableItemResult>();
        public string? ErrorMessage { get; set; }
    }

    public class ReadVariableItemResult
    {
        public bool Success { get; set; }
        public string Value { get; set; } = "";
        public object? RawValue { get; set; }
        public string DataType { get; set; } = "";
        public int Size { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
