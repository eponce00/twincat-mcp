#nullable enable
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TcAutomation.Core
{
    /// <summary>
    /// A process started suspended inside a private job. Its descendants inherit the membership, except
    /// a child that requests breakaway and processes started for it by COM, WMI or a service.
    /// Measured (TE2000 1.12.762.57): an HMI shell's engineering server can start %TEMP%\TcHmiSrv.exe,
    /// which outlives the shell. It is a job member, so ending the job ends it.
    /// </summary>
    public sealed class ShellJob : IDisposable
    {
        IntPtr _job;

        ShellJob(IntPtr job) { _job = job; }

        /// <summary>Start <paramref name="start"/> suspended, assign it to a new job, then resume it.</summary>
        public static Process Start(ProcessStartInfo start, out ShellJob job)
        {
            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");
            job = new ShellJob(handle);
            try
            {
                // A child that asks to break away may; nothing is killed when the handle closes.
                var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = JobObjectLimitBreakawayOk } };
                if (!SetInformationJobObject(handle, 9, ref limits, Marshal.SizeOf(limits)))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed");

                // As Process.Start with UseShellExecute false: WindowStyle is not applied.
                var info = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
                var command = new StringBuilder("\"" + start.FileName + "\"" + (start.Arguments.Length > 0 ? " " + start.Arguments : ""));
                uint flags = CreateSuspended | CreateUnicodeEnvironment | (start.CreateNoWindow ? CreateNoWindow : 0);
                var directory = string.IsNullOrEmpty(start.WorkingDirectory) ? null : start.WorkingDirectory;
                var environment = GCHandle.Alloc(EnvironmentBlock(start).ToCharArray(), GCHandleType.Pinned);
                ProcessInformation created;
                try
                {
                    // Unlike Process.Start, no handles are inherited: the shell gets none of the worker's pipes.
                    if (!CreateProcess(start.FileName, command, IntPtr.Zero, IntPtr.Zero, false, flags,
                            environment.AddrOfPinnedObject(), directory, ref info, out created))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed");
                }
                finally { environment.Free(); }
                Process? process = null;
                try
                {
                    if (!AssignProcessToJobObject(handle, created.hProcess))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed");
                    // Opened while the process is suspended, so the handle cannot name a reused PID.
                    process = Process.GetProcessById(created.dwProcessId);
                    _ = process.Handle;
                    if (ResumeThread(created.hThread) == uint.MaxValue)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread failed");
                    return process;
                }
                catch
                {
                    // Nothing records this process yet: end it here, whatever failed.
                    TerminateProcess(created.hProcess, 1);
                    if (WaitForSingleObject(created.hProcess, 5000) != 0)
                        Console.Error.WriteLine($"[DEBUG] Unstarted HMI shell PID {created.dwProcessId} is not confirmed gone");
                    process?.Dispose();
                    throw;
                }
                finally
                {
                    CloseHandle(created.hThread);
                    CloseHandle(created.hProcess);
                }
            }
            catch
            {
                job.Dispose();
                job = null!;
                throw;
            }
        }

        /// <summary>Terminate every member, then wait until none is active. False keeps the job for a retry.</summary>
        public bool End(int timeoutMs)
        {
            if (_job == IntPtr.Zero) return true;
            TerminateJobObject(_job, 1);
            var clock = Stopwatch.StartNew();
            while (true)
            {
                if (ActiveProcesses() == 0) { Dispose(); return true; }
                if (clock.ElapsedMilliseconds >= timeoutMs) return false;
                Thread.Sleep(100);
            }
        }

        /// <summary>Active member count; -1 if it cannot be read.</summary>
        public int ActiveProcesses()
        {
            if (_job == IntPtr.Zero) return 0;
            if (!QueryInformationJobObject(_job, 1, out BasicAccounting accounting, Marshal.SizeOf<BasicAccounting>(), IntPtr.Zero))
                return -1;
            return (int)accounting.ActiveProcesses;
        }

        public bool Contains(Process process)
        {
            if (_job == IntPtr.Zero) return false;
            return IsProcessInJob(process.Handle, _job, out var member) && member;
        }

        public void Dispose()
        {
            if (_job == IntPtr.Zero) return;
            CloseHandle(_job);
            _job = IntPtr.Zero;
        }

        static string EnvironmentBlock(ProcessStartInfo start)
        {
            // The same Unicode block Process.Start builds: sorted, NUL-separated, double NUL at the end.
            var keys = start.Environment.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
            var block = new StringBuilder();
            foreach (var key in keys) block.Append(key).Append('=').Append(start.Environment[key]).Append('\0');
            return block.Append('\0').ToString();
        }

        const uint CreateSuspended = 0x4, CreateUnicodeEnvironment = 0x400, CreateNoWindow = 0x08000000;
        const uint JobObjectLimitBreakawayOk = 0x800;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct StartupInfo
        {
            public int cb;
            public string? lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimits
        {
            public BasicLimits Basic;
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BasicAccounting
        {
            public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, int length);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out BasicAccounting info, int length, IntPtr returned);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory,
            ref StartupInfo startupInfo, out ProcessInformation processInformation);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    }
}
