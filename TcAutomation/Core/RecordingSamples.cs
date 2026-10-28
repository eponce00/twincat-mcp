using System;
using System.Collections.Concurrent;
namespace TcAutomation.Core
{
    public sealed class RecordingSamples
    {
        readonly int channels;
        int count, receivedCount;
        readonly double[] latest;
        readonly bool[] received;
        readonly object gate = new object();
        public int MaxRows { get; }
        public bool LimitReached { get; private set; }
        public ConcurrentQueue<(DateTimeOffset Timestamp, double[] Values)> Rows { get; } = new ConcurrentQueue<(DateTimeOffset, double[])>();
        public RecordingSamples(int channels)
        {
            if (channels < 1 || channels > 500) throw new ArgumentException("Expected 1..500 channels.");
            this.channels = channels; latest = new double[channels]; received = new bool[channels]; MaxRows = Math.Min(100000, 1000000 / channels);
        }
        public static string CsvHeader(string name)
        {
            if (name.Length > 0 && "=+-@\t\r\n".IndexOf(name[0]) >= 0) name = "'" + name;
            return "\"" + name.Replace("\"", "\"\"") + "\"";
        }
        public bool Update(int index, double value, DateTimeOffset timestamp)
        {
            lock (gate)
            {
                latest[index] = value;
                if (!received[index]) { received[index] = true; receivedCount++; }
                return index != 0 || receivedCount != channels || Add(timestamp, latest);
            }
        }
        public bool Add(DateTimeOffset timestamp, double[] values)
        {
            if (values.Length != channels) throw new ArgumentException("Channel count mismatch.");
            lock (gate)
            {
                if (count >= MaxRows) { LimitReached = true; return false; }
                var copy = (double[])values.Clone();
                Rows.Enqueue((timestamp, copy)); count++; return true;
            }
        }
    }
}
