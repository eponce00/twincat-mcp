using System;
using System.Globalization;
namespace TcAutomation.Core
{
    public static class AdsValues
    {
        public static void ValidateSize(int size) { if (size < 1 || size > 1024 * 1024) throw new ArgumentException("ADS symbol size must be 1 byte to 1 MiB."); }
        public static bool Boolean(string value)
        {
            if (string.Equals(value, "TRUE", StringComparison.OrdinalIgnoreCase) || value == "1") return true;
            if (string.Equals(value, "FALSE", StringComparison.OrdinalIgnoreCase) || value == "0") return false;
            throw new ArgumentException("BOOL values must be TRUE, FALSE, 1 or 0.");
        }
        public static object Parse(string type, int size, string value)
        {
            ValidateSize(size);
            switch (type.ToUpperInvariant())
            {
                case "BOOL": return Boolean(value);
                case "BYTE": case "USINT": return byte.Parse(value, CultureInfo.InvariantCulture);
                case "SINT": return sbyte.Parse(value, CultureInfo.InvariantCulture);
                case "WORD": case "UINT": return ushort.Parse(value, CultureInfo.InvariantCulture);
                case "INT": return short.Parse(value, CultureInfo.InvariantCulture);
                case "DWORD": case "UDINT": return uint.Parse(value, CultureInfo.InvariantCulture);
                case "DINT": return int.Parse(value, CultureInfo.InvariantCulture);
                case "LWORD": case "ULINT": return ulong.Parse(value, CultureInfo.InvariantCulture);
                case "LINT": return long.Parse(value, CultureInfo.InvariantCulture);
                case "REAL": return float.Parse(value, CultureInfo.InvariantCulture);
                case "LREAL": return double.Parse(value, CultureInfo.InvariantCulture);
                default:
                    if (type.StartsWith("STRING", StringComparison.OrdinalIgnoreCase))
                    { if (value == null || value.Length >= size) throw new ArgumentException("STRING value exceeds its symbol capacity."); return value; }
                    throw new ArgumentException("Unsupported ADS write type: " + type);
            }
        }
        public static int NumericSize(string type)
        {
            switch (type)
            {
                case "BOOL": case "BYTE": case "USINT": case "SINT": return 1;
                case "WORD": case "UINT": case "INT": return 2;
                case "DWORD": case "UDINT": case "DINT": case "REAL": return 4;
                case "LWORD": case "ULINT": case "LINT": case "LREAL": return 8;
                default: throw new ArgumentException("Recording supports numeric scalar types only: " + type);
            }
        }
    }
}
