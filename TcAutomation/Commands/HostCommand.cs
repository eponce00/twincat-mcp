using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TcAutomation.Core;

namespace TcAutomation.Commands
{
    /// <summary>
    /// Long-lived "shell host" process. Holds ONE Visual Studio / TcXaeShell
    /// instance for the MCP server's entire lifetime, so per-call shell startup
    /// (~25-90s) is paid once per MCP server session instead of per tool call.
    ///
    /// Invoked as: TcAutomation.exe host --mcp-pid &lt;pid&gt;
    ///
    /// Protocol: newline-delimited JSON (NDJSON) over stdin/stdout.
    ///   Request:  {"id": 1, "method": "method-name", "params": {...}}
    ///   Response: {"id": 1, "ok": true, "result": {...}, "durationMs": 1234}
    ///             {"id": 1, "ok": false, "error": "..."}
    ///   Progress lines (stderr, not stdout): "[PROGRESS] ..." as before.
    ///   Initial handshake line (stdout): {"type": "ready", "hostPid": ..., ...}
    ///
    /// Methods:
    ///   ensure-solution  {solutionPath, tcVersion?}  → opens or reloads solution
    ///   execute-step     {command, args}             → runs a StepDispatcher step
    ///   status                                        → current host/DTE state
    ///   shutdown                                      → clean exit
    ///
    /// Lifecycle guarantees:
    ///   1. Parent-death watchdog thread kills DTE and exits if MCP server dies.
    ///   2. Session file (Core.SessionFile) records mcpPid/hostPid/dtePid with
    ///      start-time fingerprints so the janitor can reap on any crash combo.
    ///   3. Graceful shutdown tries DTE.Quit() first, then terminates the
    ///      owned shell through its launch handle, then deletes the session
    ///      file. The record is kept if the shell cannot be confirmed gone.
    /// </summary>
    public static class HostCommand
    {
        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        private static readonly object StdoutLock = new object();
        private static readonly CancellationTokenSource ShutdownCts = new CancellationTokenSource();
        private static VisualStudioInstance? _vsInstance;
        private static SessionFile? _sessionFile;
        private static string? _loadedTcVersion;
        private static bool _messageFilterRegistered;
        private static int _callsServed;
        private static readonly DateTime _startedUtc = DateTime.UtcNow;

        public static int Execute(int mcpPid, int? parentPollMs)
        {
            int hostPid = Process.GetCurrentProcess().Id;

            // Step 1 — reap any orphans from prior sessions before we register
            // ourselves. Safe: only kills processes whose recorded start-time
            // matches, never something with a reused PID.
            try { SessionFile.ReapOrphans(); } catch { }

            // Step 2 — establish session file (DTE PID fills in after we open it).
            _sessionFile = new SessionFile
            {
                McpPid = mcpPid,
                HostPid = hostPid,
                McpStartTimeUtc = SessionFile.TryGetProcessStartTime(mcpPid),
                HostStartTimeUtc = SessionFile.TryGetProcessStartTime(hostPid),
                HostExecutable = Process.GetCurrentProcess().MainModule?.FileName
            };
            try { _sessionFile.Save(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DEBUG] host: failed to save session file: {ex.Message}");
            }

            // Step 3 — parent-death watchdog. If we lose the MCP parent for ANY
            // reason (clean exit, crash, OOM, taskmgr kill), we race to tear
            // down the DTE and exit before anyone notices.
            StartParentDeathWatchdog(mcpPid, parentPollMs);

            // Step 4 — register COM MessageFilter once for the host's lifetime.
            try
            {
                MessageFilter.Register();
                _messageFilterRegistered = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DEBUG] host: MessageFilter.Register failed: {ex.Message}");
            }

            // Step 5 — emit ready line so Python knows we're alive.
            EmitStdout(new { type = "ready", hostPid, mcpPid, startedUtc = _startedUtc.ToString("O") });

            // Step 6 — run the read/dispatch loop on the STA thread.
            try
            {
                RunRequestLoop();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DEBUG] host: request loop crashed: {ex.Message}");
            }
            finally
            {
                Shutdown("loop exit");
            }

            return 0;
        }

        // ================ Request loop ================

        private static void RunRequestLoop()
        {
            // Stdin reader runs on its own thread and hands lines off via a
            // BlockingCollection so that the STA-affine main thread can poll
            // with a timeout and also react to the shutdown token.
            var lines = new BlockingCollection<string?>(boundedCapacity: 64);
            var readerThread = new Thread(() =>
            {
                try
                {
                    string? line;
                    while ((line = Console.In.ReadLine()) != null)
                    {
                        lines.Add(line);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[DEBUG] host: stdin reader error: {ex.Message}");
                }
                finally
                {
                    lines.CompleteAdding();
                }
            })
            {
                IsBackground = true,
                Name = "HostStdinReader"
            };
            readerThread.Start();

            var token = ShutdownCts.Token;
            while (!token.IsCancellationRequested)
            {
                string? line;
                try
                {
                    if (!lines.TryTake(out line, 250, token))
                    {
                        if (lines.IsCompleted) break;
                        continue;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                HandleRequestLine(line);
            }
        }

        private static void HandleRequestLine(string line)
        {
            int? requestId = null;
            string? method = null;
            var sw = Stopwatch.StartNew();

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                {
                    requestId = idEl.GetInt32();
                }
                if (root.TryGetProperty("method", out var methodEl) && methodEl.ValueKind == JsonValueKind.String)
                {
                    method = methodEl.GetString();
                }

                JsonElement paramsEl = default;
                bool hasParams = root.TryGetProperty("params", out paramsEl) && paramsEl.ValueKind == JsonValueKind.Object;

                if (string.IsNullOrEmpty(method))
                {
                    EmitError(requestId, "Missing 'method'", sw);
                    return;
                }

                switch (method)
                {
                    case "ensure-solution":
                        HandleEnsureSolution(requestId, hasParams ? paramsEl : default, sw);
                        break;

                    case "execute-step":
                        HandleExecuteStep(requestId, hasParams ? paramsEl : default, sw);
                        break;

                    case "status":
                        HandleStatus(requestId, sw);
                        break;

                    case "ping":
                        EmitOk(requestId, new { pong = true }, sw);
                        break;

                    case "shutdown":
                        EmitOk(requestId, new { goodbye = true }, sw);
                        ShutdownCts.Cancel();
                        break;

                    default:
                        EmitError(requestId, $"Unknown method: '{method}'", sw);
                        break;
                }
            }
            catch (JsonException ex)
            {
                EmitError(requestId, $"Invalid JSON: {ex.Message}", sw);
            }
            catch (Exception ex)
            {
                EmitError(requestId, $"{ex.GetType().Name}: {ex.Message}", sw);
            }
        }

        // ================ Method handlers ================

        private static void HandleEnsureSolution(int? requestId, JsonElement paramsEl, Stopwatch sw)
        {
            bool hasParams = paramsEl.ValueKind == JsonValueKind.Object;
            string? solutionPath = StepDispatcher.GetString(paramsEl, "solutionPath", hasParams);
            string? tcVersionOverride = StepDispatcher.GetString(paramsEl, "tcVersion", hasParams);
            // A blank override is absent, as in VisualStudioInstance. Otherwise
            // the shell loads the project version but the host records the blank.
            if (string.IsNullOrWhiteSpace(tcVersionOverride)) tcVersionOverride = null;

            if (string.IsNullOrWhiteSpace(solutionPath))
            {
                EmitError(requestId, "ensure-solution requires params.solutionPath", sw);
                return;
            }

            if (!File.Exists(solutionPath))
            {
                EmitError(requestId, $"Solution file not found: {solutionPath}", sw);
                return;
            }

            string tcProjectPath = TcFileUtilities.FindTwinCATProjectFile(solutionPath);
            if (string.IsNullOrEmpty(tcProjectPath))
            {
                EmitError(requestId, "No TwinCAT project (.tsproj) found in solution", sw);
                return;
            }
            string projectTcVersion = TcFileUtilities.GetTcVersion(tcProjectPath);
            if (string.IsNullOrEmpty(projectTcVersion))
            {
                EmitError(requestId, "Could not determine TwinCAT version from project", sw);
                return;
            }
            string effectiveTcVersion = string.IsNullOrEmpty(tcVersionOverride)
                ? projectTcVersion
                : tcVersionOverride;

            bool openedFresh = false;
            bool reloaded = false;

            if (_vsInstance == null)
            {
                OpenFreshShell(solutionPath, projectTcVersion, tcVersionOverride, effectiveTcVersion);
                openedFresh = true;
            }
            else if (!PathsEqual(_vsInstance.SolutionFilePath, solutionPath)
                || !string.Equals(_loadedTcVersion, effectiveTcVersion, StringComparison.OrdinalIgnoreCase))
            {
                EmitProgress($"host: switching solution -> {Path.GetFileName(solutionPath)} ...");
                var reloadSw = Stopwatch.StartNew();
                try
                {
                    _vsInstance.ReloadSolution(solutionPath, projectTcVersion, tcVersionOverride);
                    try { _vsInstance.CloseAllDocuments(); } catch { }
                    reloadSw.Stop();
                    reloaded = true;
                    EmitProgress($"host: solution reloaded ({reloadSw.Elapsed.TotalSeconds:F1}s)");

                    _loadedTcVersion = effectiveTcVersion;
                    UpdateSessionFileWithDte(_vsInstance, solutionPath, effectiveTcVersion);
                }
                catch (ShellRestartRequiredException ex)
                {
                    // Thrown before anything was closed. The running shell
                    // cannot change version, so replace it.
                    EmitProgress($"host: {ex.Message} Restarting the shell ...");
                    DiscardShell(_vsInstance, "version change");
                    OpenFreshShell(solutionPath, projectTcVersion, tcVersionOverride, effectiveTcVersion);
                    openedFresh = true;
                }
            }
            else
            {
                _loadedTcVersion = effectiveTcVersion;
            }

            EmitOk(requestId, new
            {
                loaded = _vsInstance != null && _vsInstance.IsSolutionLoaded,
                openedFresh,
                reloaded,
                solutionPath,
                tcVersion = effectiveTcVersion,
                dtePid = _vsInstance?.DteProcessId
            }, sw);
        }

        // ================ Shell lifecycle ================
        //
        // A started shell stays in _ownedShells until its exit is confirmed.
        // The record names one DTE PID, so no shell starts while an earlier
        // one is unconfirmed: the new record would hide it from the janitor.
        // Shutdown sets _launchesClosed under the lock that admits launches,
        // then retires every listed shell.

        private static readonly object LifecycleLock = new object();
        private static readonly System.Collections.Generic.List<VisualStudioInstance> _ownedShells =
            new System.Collections.Generic.List<VisualStudioInstance>();
        private static bool _launchesClosed;

        private static void AdmitShell(VisualStudioInstance vs)
        {
            lock (LifecycleLock)
            {
                if (_launchesClosed)
                    throw new OperationCanceledException("The host is shutting down. No shell is started.");
                _ownedShells.Add(vs);
            }
        }

        /// <summary>
        /// Kill a shell and unlist it once its exit is confirmed. False means
        /// it may still run, and it stays listed.
        /// </summary>
        private static bool ReleaseShell(VisualStudioInstance vs, string reason, bool retire)
        {
            bool gone = retire ? vs.Retire(reason) : vs.KillOwnedProcess(reason);
            if (gone)
            {
                lock (LifecycleLock) { _ownedShells.Remove(vs); }
            }
            return gone;
        }

        private static VisualStudioInstance[] ListedShells()
        {
            lock (LifecycleLock) { return _ownedShells.ToArray(); }
        }

        /// <summary>
        /// Close and drop the active shell (Close() also releases the dialog
        /// watchdog). Throws if the exit is unconfirmed, keeping the record.
        /// </summary>
        private static void DiscardShell(VisualStudioInstance vs, string reason)
        {
            if (ReferenceEquals(_vsInstance, vs)) _vsInstance = null;
            _loadedTcVersion = null;
            try { vs.Close(); } catch { }
            if (!ReleaseShell(vs, reason, retire: false))
                throw new InvalidOperationException(
                    $"The TwinCAT shell (PID {vs.DteProcessId?.ToString() ?? "unknown"}) did not exit. No new shell " +
                    "is started while it may be running. Its session record is kept for the janitor.");
            UpdateSessionFileWithDte(null, null, null);
        }

        /// <summary>
        /// Start a shell, select its version and open the solution. Any failure,
        /// including the solution load, discards the shell. If its exit is
        /// unconfirmed, it stays listed and blocks the next launch.
        /// </summary>
        private static void OpenFreshShell(string solutionPath, string projectTcVersion, string? tcVersionOverride,
            string effectiveTcVersion)
        {
            // Retry any shell whose earlier termination was not confirmed.
            foreach (var stale in ListedShells())
            {
                if (ReferenceEquals(stale, _vsInstance)) continue;
                if (!ReleaseShell(stale, "retry before a new launch", retire: false))
                    throw new InvalidOperationException(
                        $"An earlier TwinCAT shell (PID {stale.DteProcessId?.ToString() ?? "unknown"}) is still not " +
                        "confirmed gone. No new shell is started. Its session record is kept for the janitor.");
            }

            EmitProgress($"host: opening TwinCAT shell for {Path.GetFileName(solutionPath)} ...");
            var openSw = Stopwatch.StartNew();
            var vs = new VisualStudioInstance(solutionPath, projectTcVersion, tcVersionOverride)
            {
                // Record the shell at once. The DTE wait alone takes up to 2 min,
                // and a worker killed meanwhile must not leave an unrecorded shell.
                OwnedProcessStarted = started => UpdateSessionFileWithDte(started, solutionPath, effectiveTcVersion)
            };
            AdmitShell(vs);
            _vsInstance = vs;
            _loadedTcVersion = null;
            try
            {
                vs.Load();
                vs.LoadSolution();
            }
            catch
            {
                // Report the startup error. DiscardShell throws only for an
                // unconfirmed exit, and then keeps the record.
                try { DiscardShell(vs, "startup failed"); }
                catch (Exception ex) { Console.Error.WriteLine($"[DEBUG] host: {ex.Message}"); }
                throw;
            }
            try { vs.CloseAllDocuments(); } catch { }
            openSw.Stop();
            EmitProgress($"host: shell ready ({openSw.Elapsed.TotalSeconds:F1}s)");

            _loadedTcVersion = effectiveTcVersion;
            UpdateSessionFileWithDte(vs, solutionPath, effectiveTcVersion);
        }

        private static void HandleExecuteStep(int? requestId, JsonElement paramsEl, Stopwatch sw)
        {
            bool hasParams = paramsEl.ValueKind == JsonValueKind.Object;
            string? command = StepDispatcher.GetString(paramsEl, "command", hasParams);

            if (string.IsNullOrWhiteSpace(command))
            {
                EmitError(requestId, "execute-step requires params.command", sw);
                return;
            }

            JsonElement argsEl = default;
            if (hasParams)
            {
                foreach (var prop in paramsEl.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "args", StringComparison.OrdinalIgnoreCase))
                    {
                        argsEl = prop.Value;
                        break;
                    }
                }
            }

            string solutionPath = _vsInstance?.SolutionFilePath ?? string.Empty;
            string? tcVersion = _loadedTcVersion;

            bool isShell = StepDispatcher.IsShellCommand(command);
            bool isAds = StepDispatcher.IsAdsCommand(command);

            if (isShell && (_vsInstance == null || !_vsInstance.IsSolutionLoaded))
            {
                EmitError(requestId, $"Shell command '{command}' requires ensure-solution to run first", sw);
                return;
            }

            if (!isShell && !isAds)
            {
                EmitError(requestId, $"Unsupported command: '{command}'", sw);
                return;
            }

            _callsServed++;
            EmitProgress($"host: [{_callsServed}] {command} ...");

            try
            {
                object result = StepDispatcher.Dispatch(
                    command,
                    argsEl,
                    solutionPath,
                    tcVersion,
                    _vsInstance);

                bool success = StepDispatcher.IsResultSuccessful(result);
                string? err = success ? null : StepDispatcher.ExtractErrorFromResult(result);

                if (success)
                {
                    EmitProgress($"host: [{_callsServed}] {command} OK ({sw.Elapsed.TotalSeconds:F1}s)");
                    EmitOk(requestId, new { command, result }, sw);
                }
                else
                {
                    EmitProgress($"host: [{_callsServed}] {command} FAIL: {err}");
                    EmitResponse(new
                    {
                        id = requestId,
                        ok = false,
                        error = err ?? "Step reported failure",
                        command,
                        result,
                        durationMs = sw.Elapsed.TotalMilliseconds
                    });
                }
            }
            catch (Exception ex)
            {
                EmitProgress($"host: [{_callsServed}] {command} EXCEPTION: {ex.Message}");
                EmitError(requestId, $"{ex.GetType().Name}: {ex.Message}", sw);
            }
        }

        private static void HandleStatus(int? requestId, Stopwatch sw)
        {
            EmitOk(requestId, new
            {
                alive = true,
                hostPid = Process.GetCurrentProcess().Id,
                dtePid = _vsInstance?.DteProcessId,
                solutionLoaded = _vsInstance?.IsSolutionLoaded ?? false,
                solutionPath = _vsInstance?.SolutionFilePath,
                uptimeSeconds = (DateTime.UtcNow - _startedUtc).TotalSeconds,
                callsServed = _callsServed,
                startedUtc = _startedUtc.ToString("O")
            }, sw);
        }

        // ================ Session file helpers ================

        /// <summary>
        /// Record the owned shell, or clear the record when vs is null. PID and
        /// fingerprint come from the launch handle: reopening the PID here can
        /// fingerprint a newer process.
        /// </summary>
        private static void UpdateSessionFileWithDte(VisualStudioInstance? vs, string? solutionPath, string? tcVersion)
        {
            if (_sessionFile == null) return;
            try
            {
                _sessionFile.DtePid = vs?.DteProcessId;
                _sessionFile.DteStartTimeUtc = vs?.DteProcessId != null ? vs.DteProcessStartTimeUtc : null;
                _sessionFile.SolutionPath = solutionPath;
                _sessionFile.TcVersion = tcVersion;
                _sessionFile.Save();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DEBUG] host: failed to update session file: {ex.Message}");
            }
        }

        // ================ Shutdown / cleanup ================

        private static int _shutdownEntered; // 0 = not, 1 = entered

        private static void Shutdown(string reason)
        {
            if (Interlocked.Exchange(ref _shutdownEntered, 1) == 1) return;

            Console.Error.WriteLine($"[DEBUG] host: shutting down ({reason})");

            // Admit no new shell from here on.
            lock (LifecycleLock) { _launchesClosed = true; }

            // Close() tries Quit, then kills a survivor through the launch
            // handle. Never by PID: once a shell exits, the number can belong
            // to anything.
            var vs = _vsInstance;
            _vsInstance = null;
            try { vs?.Close(); } catch { }

            bool shellLeftRunning = !RetireAllShells("shutdown");

            if (_messageFilterRegistered)
            {
                try { MessageFilter.Revoke(); } catch { }
                _messageFilterRegistered = false;
            }

            // Keep the record if a shell survived: the janitor needs it to find that shell.
            if (_sessionFile != null && !shellLeftRunning)
            {
                try { SessionFile.Delete(_sessionFile.McpPid); } catch { }
            }
        }

        /// <summary>
        /// Retire every listed shell. True only when each is confirmed gone.
        /// </summary>
        private static bool RetireAllShells(string reason)
        {
            bool allGone = true;
            foreach (var shell in ListedShells())
            {
                bool gone;
                try { gone = ReleaseShell(shell, reason, retire: true); }
                catch { gone = false; }
                allGone &= gone;
            }
            return allGone;
        }

        // ================ Parent-death watchdog ================

        private const uint SYNCHRONIZE = 0x00100000;
        private const uint WAIT_OBJECT_0 = 0x00000000;
        private const uint WAIT_TIMEOUT = 0x00000102;
        private const uint INFINITE = 0xFFFFFFFF;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        private static void StartParentDeathWatchdog(int parentPid, int? parentPollMs)
        {
            var t = new Thread(() =>
            {
                IntPtr handle = IntPtr.Zero;
                try
                {
                    handle = OpenProcess(SYNCHRONIZE, false, (uint)parentPid);
                    if (handle == IntPtr.Zero)
                    {
                        Console.Error.WriteLine($"[DEBUG] host: watchdog could not OpenProcess({parentPid}); falling back to polling");
                        PollParent(parentPid, parentPollMs ?? 1000);
                        return;
                    }

                    uint result = WaitForSingleObject(handle, INFINITE);
                    if (result == WAIT_OBJECT_0)
                    {
                        Console.Error.WriteLine($"[DEBUG] host: parent PID {parentPid} died - triggering shutdown");
                        TriggerEmergencyShutdown();
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[DEBUG] host: watchdog crashed: {ex.Message}");
                }
                finally
                {
                    if (handle != IntPtr.Zero) CloseHandle(handle);
                }
            })
            {
                IsBackground = true,
                Name = "HostParentWatchdog"
            };
            t.Start();
        }

        private static void PollParent(int parentPid, int pollMs)
        {
            while (!ShutdownCts.IsCancellationRequested)
            {
                try { Process.GetProcessById(parentPid); }
                catch
                {
                    Console.Error.WriteLine($"[DEBUG] host: parent PID {parentPid} disappeared (polling)");
                    TriggerEmergencyShutdown();
                    return;
                }
                Thread.Sleep(pollMs);
            }
        }

        private static void TriggerEmergencyShutdown()
        {
            // The main STA thread may be deep inside a DTE call and unable to
            // exit gracefully. Give it a short window, then terminate the
            // owned shells and exit the process. The janitor will clean up the
            // session file if we didn't manage to.
            ShutdownCts.Cancel();

            // Close launches now, not after the grace period: a launch in progress
            // on the STA thread must not start a shell nothing would terminate.
            lock (LifecycleLock) { _launchesClosed = true; }

            // Short grace period for main loop to tear down cleanly.
            Task.Delay(3000).ContinueWith(_ =>
            {
                // Every shell this worker started, even one mid-close or
                // mid-launch, through its launch handle, never by PID. Retire
                // also blocks a launch not yet at Process.Start.
                bool shellLeftRunning = !RetireAllShells("parent died");

                if (_sessionFile != null && !shellLeftRunning)
                {
                    try { SessionFile.Delete(_sessionFile.McpPid); } catch { }
                }

                try { Environment.Exit(2); } catch { }
            });
        }

        // ================ Output helpers ================

        private static void EmitStdout(object payload)
        {
            lock (StdoutLock)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(payload, WriteOptions));
                Console.Out.Flush();
            }
        }

        private static void EmitOk(int? id, object resultPayload, Stopwatch sw)
        {
            EmitResponse(new
            {
                id,
                ok = true,
                result = resultPayload,
                durationMs = sw.Elapsed.TotalMilliseconds
            });
        }

        private static void EmitError(int? id, string error, Stopwatch sw)
        {
            EmitResponse(new
            {
                id,
                ok = false,
                error,
                durationMs = sw.Elapsed.TotalMilliseconds
            });
        }

        private static void EmitResponse(object response)
        {
            EmitStdout(response);
        }

        private static void EmitProgress(string message)
        {
            try
            {
                Console.Error.WriteLine("[PROGRESS] " + message);
                Console.Error.Flush();
            }
            catch { }
        }

        private static bool PathsEqual(string? a, string? b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(a).TrimEnd('\\', '/'),
                    Path.GetFullPath(b).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
