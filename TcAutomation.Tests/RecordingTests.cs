using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TcAutomation.Commands;
using TcAutomation.Core;

// In-memory samples only. Never constructs an ADS client or opens XAE.
static class RecordingTests
{
    static int checks;
    static readonly DateTimeOffset Stamp = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    static void Check(bool ok, string message)
    {
        checks++;
        if (!ok) throw new Exception(message);
    }

    static void Reject(Action action, string message)
    {
        try { action(); }
        catch (ArgumentException) { checks++; return; }
        throw new Exception(message);
    }

    public static void Run()
    {
        var samples = new RecordingSamples(2);
        int ready = 0;
        samples.FirstRowCaptured += timestamp =>
        {
            ready++;
            Check(timestamp == Stamp, "Readiness has the first complete row timestamp");
            Check(samples.Rows.Count == 1, "The complete row is captured before readiness");
            Check(samples.Rows.TryPeek(out var first) && first.Values.SequenceEqual(new[] { 12.0, 0.0 }),
                "Readiness refers to a complete snapshot with a genuine zero");
        };
        samples.Update(0, 11, Stamp);
        Check(ready == 0 && samples.Rows.IsEmpty, "A missing channel never reports ready");
        samples.Update(1, 0, Stamp);
        Check(ready == 0 && samples.Rows.IsEmpty, "All values alone are not a channel-zero snapshot");
        samples.Update(0, 12, Stamp);
        samples.Update(0, 13, Stamp.AddMilliseconds(10));
        Check(ready == 1 && samples.Rows.Count == 2, "Ready is emitted exactly once");
        Check(samples.Rows.TryDequeue(out var firstRow) && firstRow.Values[0] == 12,
            "Later notifications cannot change the first snapshot");

        var single = new RecordingSamples(1);
        int singleReady = 0;
        single.FirstRowCaptured += timestamp => singleReady++;
        single.Update(0, 0, Stamp);
        Check(singleReady == 1 && single.Rows.TryPeek(out var zero) && zero.Values[0] == 0,
            "A single-channel numeric zero is ready");

        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var missing = new RecordingSamples(2);
            int falseReady = 0;
            missing.FirstRowCaptured += timestamp => falseReady++;
            Reject(() => missing.Update(1, invalid, Stamp), "Invalid values must not initialize a channel");
            missing.Update(0, 7, Stamp);
            Check(falseReady == 0 && missing.Rows.IsEmpty, "No ready row with an invalid initial value");
            Reject(() => missing.Add(Stamp, new[] { 7.0, invalid }), "Direct snapshots require finite values");
            Check(falseReady == 0 && missing.Rows.IsEmpty, "Invalid snapshots never emit readiness");
        }
        Reject(() => single.Update(0, 1, DateTimeOffset.MinValue), "Invalid timestamps are rejected");
        Reject(() => single.Add(DateTimeOffset.MinValue, new[] { 1.0 }), "Direct rows require timestamps");
        Reject(() => single.Add(Stamp, new[] { 1.0, 2.0 }), "Channel count validation is retained");

        var bounded = new RecordingSamples(500);
        int boundedReady = 0;
        bounded.FirstRowCaptured += timestamp => boundedReady++;
        var row = new double[500];
        for (int i = 0; i < bounded.MaxRows; i++)
            if (!bounded.Add(Stamp, row)) throw new Exception("Premature recording limit");
        Check(!bounded.Add(Stamp, row) && bounded.LimitReached && bounded.Rows.Count == 2000,
            "Existing aggregate cell/row limits are unchanged");
        Check(boundedReady == 1, "Limit exhaustion cannot duplicate readiness");

        var concurrent = new RecordingSamples(1);
        int concurrentReady = 0;
        concurrent.FirstRowCaptured += timestamp => Interlocked.Increment(ref concurrentReady);
        Parallel.For(0, 100, i => concurrent.Update(0, i, Stamp));
        Check(concurrentReady == 1 && concurrent.Rows.Count == 100,
            "Concurrent notifications still emit readiness only once");

        // Exercise the actual recorder decoder without connecting to ADS.
        var decode = typeof(AdsRecordCommand).GetMethod("ConvertToDouble", BindingFlags.NonPublic | BindingFlags.Static);
        foreach (byte[] invalid in new[] { Array.Empty<byte>(), new byte[7], new byte[9] })
        {
            bool rejected = false;
            try { decode.Invoke(null, new object[] { invalid, "LREAL" }); }
            catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException) { rejected = true; }
            Check(rejected, "Malformed payloads cannot become valid initial values");
        }
        Check((double)decode.Invoke(null, new object[] { BitConverter.GetBytes(0.0), "LREAL" }) == 0,
            "Declared-size decoding retains real zeros");
        Console.WriteLine("PASS: " + checks + " recording readiness checks; no ADS or XAE used.");
    }
}
