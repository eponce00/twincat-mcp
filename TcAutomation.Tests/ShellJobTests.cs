using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using TcAutomation.Core;

static class ShellJobTests
{
    public static void Run()
    {
        int cases = 0;
        void Check(bool ok) { cases++; if (!ok) throw new Exception("Shell job assertion " + cases); }
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        ProcessStartInfo Info(string args)
        {
            var start = new ProcessStartInfo(cmd, args)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath(),
            };
            start.Environment["SHELLJOB_PROBE"] = "yes";
            return start;
        }

        // The environment and the command line reach the process.
        using (var process = ShellJob.Start(Info("/c if \"%SHELLJOB_PROBE%\"==\"yes\" (exit 7) else (exit 3)"), out var job))
        {
            Check(process.WaitForExit(10000) && process.ExitCode == 7);
            Check(job.End(5000));
        }

        // A grandchild is a job member, and ending the job ends it.
        using (var process = ShellJob.Start(Info("/c ping -n 30 127.0.0.1 >nul"), out var job))
        {
            Check(job.Contains(process));
            var clock = Stopwatch.StartNew();
            while (job.ActiveProcesses() < 2 && clock.ElapsedMilliseconds < 5000) Thread.Sleep(50);
            Check(job.ActiveProcesses() >= 2);
            Check(job.End(5000));
            Check(process.WaitForExit(5000));
            Check(job.ActiveProcesses() == 0);
        }

        // Closing the only job handle without End, as when the worker dies, ends every member.
        using (var process = ShellJob.Start(Info("/c ping -n 30 127.0.0.1 >nul"), out var job))
        {
            var clock = Stopwatch.StartNew();
            while (job.ActiveProcesses() < 2 && clock.ElapsedMilliseconds < 5000) Thread.Sleep(50);
            Check(job.ActiveProcesses() >= 2);
            job.Dispose();
            Check(process.WaitForExit(5000));
        }
        Console.WriteLine("PASS: " + cases + " shell job cases; cmd and ping only, no XAE.");
    }
}
