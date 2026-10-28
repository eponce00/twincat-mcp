using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TcAutomation.Commands;
using TcAutomation.Core;

static class FeedbackTests
{
    static int cases;
    static void Check(bool value, string message) { cases++; if (!value) throw new Exception(message); }
    static Process Child() => Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command Start-Sleep -Seconds 30")
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "twincat-feedback-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string previous = Environment.GetEnvironmentVariable("TWINCAT_MCP_STATE_DIR");
        Environment.SetEnvironmentVariable("TWINCAT_MCP_STATE_DIR", directory);
        try
        {
            Check(BuildRun.OutputDelta("old", "oldnew") == "new", "Append-only output delta");
            Check(BuildRun.OutputDelta("old", "replacement") == "replacement", "Cleared pane output");
            Check(BuildRun.OutputDelta("same", "same") == "", "Unchanged output is not repeated");
            string file = Path.Combine(directory, "PLC.tmc");
            Check(!BuildArtifacts.Read(file).Exists, "Missing TMC");
            File.WriteAllText(file, "<TcModuleClass><DataTypes><DataType><Name>ST_Test</Name></DataType></DataTypes><Modules><Module><DataAreas><DataArea><Symbol><Name>GVL_Test.n</Name></Symbol></DataArea></DataAreas></Module></Modules></TcModuleClass>");
            var before = BuildArtifacts.Read(file);
            Check(before.SymbolCount == 1 && before.TypeCount == 1, "Symbols and types indexed separately");
            var after = BuildArtifacts.Read(file);
            BuildArtifacts.Compare(before, after, "build-1", false, new[] { "GVL_Test.*" });
            Check(after.Status == "retained_after_failed_build" && !after.VerificationPassed, "Old TMC cannot verify a failed build");
            after = BuildArtifacts.Read(file);
            BuildArtifacts.Compare(before, after, "build-2", true, new[] { "Missing.n" }, new[] { "ST_Test" });
            Check(!after.VerificationPassed && after.MissingSymbols.Single() == "Missing.n" && after.MissingTypes.Length == 0, "Explicit symbol verification");
            BuildArtifacts.Compare(before, after, "build-3", true, new[] { "GVL_Test.*" }, new[] { "ST_Test" });
            Check(after.VerificationPassed, "Prefix expectations and type verification");
            File.WriteAllText(file, "<!DOCTYPE TcModuleClass [<!ENTITY x SYSTEM 'file:///unread'>]><TcModuleClass>&x;</TcModuleClass>");
            Check(BuildArtifacts.Read(file).Status == "unreadable", "DTD rejected");
            string source = "VAR_GLOBAL\n n : INT;\nEND_VAR";
            string linked = PlcObjectsCommand.Linking(source, 615, "always");
            Check(linked.StartsWith("{attribute 'linkalways'}"), "Opt-in linking policy");
            Check(PlcObjectsCommand.Linking(linked, 615, "always") == linked, "Link policy is idempotent");
            Check(PlcObjectsCommand.Linking(linked, 615, "default") == source, "Explicitly restore default linking");
            Check(PlcObjectsCommand.Linking(source, 615, "preserve") == source, "Default preserves source");
            try { PlcObjectsCommand.Linking(source, 606, "always"); throw new Exception("DUT policy should reject"); }
            catch (ArgumentException) { cases++; }

            using var self = Process.GetCurrentProcess();
            string fingerprint = self.StartTime.ToUniversalTime().ToString("O");
            Check(SessionFile.ClassifyProcess(self.Id, fingerprint) == SessionFile.ProcessIdentity.Same, "Live fingerprint");
            Check(SessionFile.ClassifyProcess(self.Id, null) == SessionFile.ProcessIdentity.Unverified, "Missing fingerprint cannot authorize a kill");
            Check(SessionFile.ClassifyProcess(self.Id, "08:00:00") == SessionFile.ProcessIdentity.Unverified, "Reject partial timestamps");
            Check(SessionFile.ClassifyProcess(self.Id, fingerprint.TrimEnd('Z')) == SessionFile.ProcessIdentity.Unverified, "Reject timezone-less timestamps");
            using var child = Child();
            string childFingerprint = child.StartTime.ToUniversalTime().ToString("O");
            try
            {
                var record = new SessionFile { McpPid = self.Id, McpStartTimeUtc = fingerprint, HostPid = child.Id, HostStartTimeUtc = childFingerprint };
                record.Save();
                Check(SessionFile.TryLoadStrict(SessionFile.PathFor(self.Id), out var outcome) != null && outcome == SessionFile.LoadOutcome.Ok, "Atomic session publication");
                var report = SessionFile.ReapOrphansDetailed();
                Check(report.Stopped == 0 && report.Cleaned == 0 && !child.HasExited, "Healthy owned child untouched");
                File.Delete(SessionFile.PathFor(self.Id));
                File.WriteAllText(Path.Combine(directory, "session-malformed.json"), "broken");
                report = SessionFile.ReapOrphansDetailed();
                Check(report.Unresolved == 1 && report.Stopped == 0 && !child.HasExited, "Malformed record retained without killing");
                File.Delete(Path.Combine(directory, "session-malformed.json"));
                record.McpPid = 0; record.Save();
                report = SessionFile.ReapOrphansDetailed();
                Check(report.Unresolved == 1 && report.Stopped == 0 && !child.HasExited, "Missing/nonpositive owner identity cannot kill");
                File.Delete(SessionFile.PathFor(0));
                using var gone = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
                    { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                gone.WaitForExit();
                record.McpPid = gone.Id; record.McpStartTimeUtc = fingerprint;
                record.HostStartTimeUtc = DateTime.UtcNow.AddYears(-2).ToString("O"); record.Save();
                report = SessionFile.ReapOrphansDetailed();
                Check(report.Cleaned == 1 && report.Stopped == 0 && !child.HasExited, "Mismatched child identity is never killed");
                record.HostStartTimeUtc = childFingerprint; record.Save();
                report = SessionFile.ReapOrphansDetailed();
                Check(report.Cleaned == 1 && report.Stopped == 1 && child.WaitForExit(1000), "Verified orphan stopped and record removed");
            }
            finally { if (!child.HasExited) child.Kill(); child.WaitForExit(5000); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("TWINCAT_MCP_STATE_DIR", previous);
            Directory.Delete(directory, true);
        }
        Console.WriteLine("PASS: " + cases + " build/artifact/linking/janitor checks; only test-owned child processes used.");
    }
}
