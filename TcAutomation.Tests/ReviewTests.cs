using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TcAutomation.Commands;
using TcAutomation.Core;

static class ReviewTests
{
    static int cases;
    static void Check(bool ok, string message) { cases++; if (!ok) throw new Exception(message); }
    static void Reject(Action action, string message) { try { action(); } catch (ArgumentException) { cases++; return; } catch (OverflowException) { cases++; return; } catch (FormatException) { cases++; return; } catch (IOException) { cases++; return; } throw new Exception(message); }
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "twincat-review-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var stages = new List<string>();
        string previous = Environment.GetEnvironmentVariable("TWINCAT_MCP_STATE_DIR");
        Environment.SetEnvironmentVariable("TWINCAT_MCP_STATE_DIR", directory);
        try
        {
            int builds = 0, calls = 0; bool? install = null;
            string library = Path.Combine(directory, "PLC.library");
            var preview = GenerateLibraryCommand.Export(library, (_, flag) => { calls++; install=flag; }, () => builds++, true);
            Check(preview.Success && calls == 0 && builds == 0 && !File.Exists(library), "Preview never builds, exports or writes");
            var exported = GenerateLibraryCommand.Export(library, (file, flag) => { calls++; install=flag; File.WriteAllText(file, "library bytes"); }, () => builds++, false);
            stages.Add(exported.StagePath);
            Check(exported.Success && install == false && calls == 1 && builds == 1 && File.ReadAllText(library) == "library bytes", "Exactly one documented export without repository installation");
            Check(exported.Sha256 == ArtifactFiles.Sha256(library), "Export has byte evidence");
            var existing = GenerateLibraryCommand.Export(library, (_, flag) => calls++, () => builds++, false);
            Check(!existing.Success && calls == 1 && builds == 1 && File.ReadAllText(library) == "library bytes", "Existing artifacts untouched before build/export");
            var failed = GenerateLibraryCommand.Export(Path.Combine(directory, "failed.library"), (_, flag) => { calls++; throw new IOException("native rejected"); }, null, false);
            stages.Add(failed.StagePath);
            Check(!failed.Success && failed.OutcomeUnknown && calls == 2 && !File.Exists(failed.OutputLibraryPath), "Native export is not retried or guessed");
            var empty = GenerateLibraryCommand.Export(Path.Combine(directory,"empty.library"), (file, flag) => File.WriteAllText(file,""), null, false);
            stages.Add(empty.StagePath);
            Check(!empty.Success && !File.Exists(empty.OutputLibraryPath), "Empty exports cannot succeed");
            Reject(() => ArtifactFiles.CreateNew(library, ".library"), "Atomic create must refuse overwrite");
            Reject(() => ArtifactFiles.CreateNew(Path.Combine(directory,"server.py"), ".csv"), "Output extension gate");
            Reject(() => ArtifactFiles.ValidateNew("relative.csv", ".csv"), "Absolute output required");
            Check(AdsValues.Boolean("FALSE") == false && AdsValues.Boolean("1"), "Strict valid BOOLs");
            Reject(() => AdsValues.Boolean("FALSE_typo"), "Unknown BOOL must not silently write false");
            Reject(() => AdsValues.Parse("INT",2,"32768"), "Numeric overflow rejected");
            Reject(() => AdsValues.ValidateSize(int.MaxValue), "PLC-supplied sizes bounded");
            Reject(() => AdsValues.NumericSize("ARRAY OF REAL"), "Unrecognized recording types are not guessed");
            var read = new ReadVariableListResult { SymbolCount=2, SuccessCount=1, ErrorCount=1 };
            read.Results["MAIN.OK"] = new ReadVariableItemResult { Success=true };
            read.Results["MAIN.Bad"] = new ReadVariableItemResult { Success=false };
            Check(!read.Success && !StepDispatcher.IsResultSuccessful(read), "Partial batch reads fail workflow dispatch");
            var write = new WriteVariableListResult { SymbolCount=1, ErrorCount=1 };
            write.Results["MAIN.Bad"] = new WriteVariableItemResult { Success=false };
            Check(!write.Success && !StepDispatcher.IsResultSuccessful(write), "Failed writes cannot advance workflows");
            var samples = new RecordingSamples(500); var row = new double[500]; row[0]=42;
            for(int i=0;i<samples.MaxRows;i++) if(!samples.Add(DateTimeOffset.UtcNow,row)) throw new Exception("early recording limit");
            row[0]=99;
            Check(!samples.Add(DateTimeOffset.UtcNow,row) && samples.LimitReached && samples.Rows.Count==2000, "Recording rows/cells are bounded under sustained input");
            Check(samples.Rows.TryDequeue(out var first) && first.Values[0]==42, "Captured rows preserve their snapshot");
            Check(RecordingSamples.CsvHeader("=SUM(A1)").StartsWith("\"'="), "CSV headers cannot introduce spreadsheet formulas");
            var startup = new RecordingSamples(2);
            startup.Update(0, 7, DateTimeOffset.UtcNow);
            Check(startup.Rows.IsEmpty, "Incomplete initial notifications never fabricate zero values");
            startup.Update(1, 8, DateTimeOffset.UtcNow); startup.Update(0, 9, DateTimeOffset.UtcNow);
            Check(startup.Rows.TryDequeue(out var initialized) && initialized.Values.SequenceEqual(new double[]{9,8}), "Snapshots begin after every channel has a real value");
            string csv=Path.Combine(directory,"trace.csv");File.WriteAllText(csv,"existing data");
            var record=AdsRecordCommand.Execute("1.2.3.4.1.1",851,new[]{"MAIN.X"},10,1,csv);
            Check(!record.Success && File.ReadAllText(csv)=="existing data", "Recording rejects overwrite before ADS access");
            var saved=Console.Out; using var captured=new StringWriter();
            try { Console.SetOut(captured); Check(ScopeSessionCommand.Execute()==1,"Scope capture is unavailable");Check(ScopeExportCommand.Execute(csv,csv,"csv")==1,"Scope export is unavailable"); }
            finally{ Console.SetOut(saved); }
            Check(captured.ToString().Contains("unsupported_integration") && File.ReadAllText(csv)=="existing data", "Disabled Scope emits no fake data and modifies no output");
            foreach(var item in new[]{("B1E792BE-AA5F-4E3C-8C82-674BF9C0715B","tsproj"),("DFBE7525-6864-4E62-8B2E-D530D69D9D96","tspproj")})
            {
                string sln=Path.Combine(directory,"Test.sln");
                File.WriteAllText(sln,"Project(\"{"+item.Item1+"}\") = \"PLC\", \"PLC."+item.Item2+"\", \"{A}\"");
                Check(TcFileUtilities.FindTwinCATProjectFile(sln)==Path.Combine(directory,"PLC."+item.Item2),"Both documented project extensions resolve");
            }
            using var self=Process.GetCurrentProcess();string fingerprint=self.StartTime.ToUniversalTime().ToString("O");
            using var lease=DialogWatchdog.LeaseProcess(self.Id,self.StartTime.ToUniversalTime());
            Check(lease!=null,"Dialog callback pins matching process identity");
            Check(DialogWatchdog.LeaseProcess(self.Id,DateTime.UtcNow.AddYears(-1))==null,"Dialog callback refuses mismatched identity");
            var old=new SessionFile{McpPid=self.Id,HostPid=123,McpStartTimeUtc=fingerprint,HostStartTimeUtc=fingerprint};
            var fresh=new SessionFile{McpPid=self.Id,HostPid=456,McpStartTimeUtc=fingerprint,HostStartTimeUtc=fingerprint};
            old.Save();fresh.Save();var report=new SessionFile.ReapReport();
            Check(!SessionFile.DeleteUnchanged(SessionFile.PathFor(self.Id),old,report) && File.Exists(SessionFile.PathFor(self.Id)),"Replacement session survives conditional deletion");
            Check(SessionFile.DeleteUnchanged(SessionFile.PathFor(self.Id),fresh,report) && !File.Exists(SessionFile.PathFor(self.Id)),"Only the matching pinned record is deleted");
            for(int i=0;i<12;i++)
            {
                old.Save();var writer=Task.Run(()=>fresh.Save());
                SessionFile.DeleteUnchanged(SessionFile.PathFor(self.Id),old,new SessionFile.ReapReport());writer.Wait();
                Check(SessionFile.TryLoadStrict(SessionFile.PathFor(self.Id),out _)?.HostPid==456,"Concurrent replacement publication is never lost");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("TWINCAT_MCP_STATE_DIR",previous);
            foreach(string stage in stages.Where(s=>s!=null))
            {
                string parent=Path.GetDirectoryName(stage);
                if(!parent.StartsWith(Path.Combine(Path.GetTempPath(),"twincat-library-"),StringComparison.OrdinalIgnoreCase))throw new Exception("Unexpected stage location");
                if(File.Exists(stage))File.Delete(stage);Directory.Delete(parent);
            }
            Directory.Delete(directory,true);
        }
        Console.WriteLine("PASS: "+cases+" contributor-review safety, artifact, batch, recording and ownership checks; no PLC writes.");
    }
}
