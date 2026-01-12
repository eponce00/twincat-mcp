using System;
using System.Text;
using System.Text.Json;
using TwinCAT.Ads;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Reads a PLC variable value via ADS.
    /// Connects directly to the PLC without opening Visual Studio.
    /// Uses handle-based symbol access for compatibility.
    /// </summary>
    public static class ReadVariableCommand
    {
        public static int Execute(string amsNetId, int port, string symbolName)
        {
            var result = new ReadVariableResult
            {
                AmsNetId = amsNetId,
                Port = port,
                SymbolName = symbolName
            };

            try
            {
                using (var adsClient = new AdsClient())
                {
                    // Connect to the target
                    adsClient.Connect(amsNetId, port);
                    
                    if (!adsClient.IsConnected)
                    {
                        result.ErrorMessage = $"Failed to connect to {amsNetId}:{port}";
                        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                        return 1;
                    }

                    // Check state - must be running to read symbols
                    var stateInfo = adsClient.ReadState();
                    if (stateInfo.AdsState != AdsState.Run)
                    {
                        result.ErrorMessage = $"PLC is not running (state: {stateInfo.AdsState}). Cannot read variables.";
                        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                        return 1;
                    }

                    // Create a handle for the symbol
                    uint handle = adsClient.CreateVariableHandle(symbolName);
                    try
                    {
                        // Get symbol info to determine the type
                        var symbolInfo = adsClient.ReadSymbol(symbolName);
                        result.DataType = symbolInfo.TypeName;
                        result.Size = symbolInfo.Size;

                        // Read based on the type
                        object value = ReadTypedValue(adsClient, handle, symbolInfo.TypeName, symbolInfo.Size);
                        
                        result.Value = value?.ToString() ?? "null";
                        result.RawValue = value;
                        result.Success = true;
                    }
                    finally
                    {
                        // Always release the handle
                        adsClient.DeleteVariableHandle(handle);
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
            // Map TwinCAT types to .NET types
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
            {
                // STRING(n) - read as string, use size from symbol
                return client.ReadAny(handle, typeof(string), new int[] { size });
            }

            // For arrays and structs, read as byte array and return as hex
            byte[] data = new byte[size];
            client.Read(handle, data.AsMemory());
            return BitConverter.ToString(data).Replace("-", " ");
        }
    }

    public class ReadVariableResult
    {
        public string AmsNetId { get; set; } = "";
        public int Port { get; set; }
        public string SymbolName { get; set; } = "";
        public bool Success { get; set; }
        public string Value { get; set; } = "";
        public object? RawValue { get; set; }
        public string DataType { get; set; } = "";
        public int Size { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
