using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using TcAutomation.Commands;
using TcAutomation.Core;

static class MoveTests
{
    static int cases;
    static void Check(bool value, string message) { cases++; if (!value) throw new Exception(message); }
    static void Reject(Action action, string message)
    {
        try { action(); } catch (ArgumentException) { cases++; return; } catch (InvalidDataException) { cases++; return; } catch (IOException) { cases++; return; }
        catch (UnauthorizedAccessException) { cases++; return; } catch (System.Xml.XmlException) { cases++; return; }
        throw new Exception(message);
    }
    sealed class Fixture : Session, IPlcMoveSession, IDisposable
    {
        public string DirectoryPath = Path.Combine(Path.GetTempPath(), "twincat-move-tests-" + Guid.NewGuid().ToString("N"));
        public string ProjectFilePath => Path.Combine(DirectoryPath, "PLC.plcproj");
        public Node From, To, Fb;
        public int Opens, Closes;
        public bool Closed, FailOpen, ExternalEdit;
        public Fixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            From = (Node)RootNode.Create("Source", 601, null); To = (Node)RootNode.Create("Destination", 601, null);
            Fb = (Node)From.Create("FB_Test", 604, null); Fb.Declaration = "FUNCTION_BLOCK FB_Test"; Fb.Implementation = "// unchanged";
            var method = (Node)Fb.Create("Run", 609, null); method.Declaration = "METHOD Run"; method.Implementation = "// member";
            Fb.Native = Encoding.UTF8.GetBytes("<TcPlcObject><POU Name='FB_Test' Id='" + Guid.NewGuid().ToString("B") + "'><Method Name='Run' Id='" + Guid.NewGuid().ToString("B") + "'/></POU></TcPlcObject>");
            Directory.CreateDirectory(Path.Combine(DirectoryPath, "Source"));
            File.WriteAllBytes(FilePath(Fb), Fb.Native);
            File.WriteAllText(ProjectFilePath, "<Project xmlns='http://schemas.microsoft.com/developer/msbuild/2003'><ItemGroup><Folder Include='Source'/><Folder Include='Destination'/><Compile Include='Source\\FB_Test.TcPOU'><SubType>Code</SubType></Compile></ItemGroup></Project>");
        }
        public string FilePath(IPlcObjectNode node) => Path.Combine(DirectoryPath, node.Path.Substring(Root.Path.Length + 1).Replace('^', Path.DirectorySeparatorChar) + ".TcPOU");
        public void CloseForMove() { Closes++; Closed = true; }
        public void ReopenAfterMove()
        {
            Check(Closed, "Solution must be closed during disk edits"); Opens++;
            if (FailOpen && Opens == 1) throw new IOException("Injected reopen failure");
            if (ExternalEdit && Opens == 1) { File.AppendAllText(ProjectFilePath, "<!-- external edit -->"); throw new IOException("External edit"); }
            Fb.Parent.Items.Remove(Fb);
            Fb.Parent = File.Exists(Path.Combine(DirectoryPath, "Destination", "FB_Test.TcPOU")) ? To : From;
            Fb.Parent.Items.Add(Fb); Closed = false;
        }
        public void AbortMove() { }
        public void Dispose() { Directory.Delete(DirectoryPath, true); }
    }
    static Dictionary<string, object> Call(Fixture f, string command, object args)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(args));
        return PlcObjectsCommand.Execute(f, command, doc.RootElement);
    }
    static string Sha(Fixture f, Node node) => (string)((Dictionary<string, object>)Call(f, "read-plc-object", new { path = node.Path })["object"])["sha256"];
    public static void Run()
    {
        string state = Path.Combine(Path.GetTempPath(), "twincat-move-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        string previous = Environment.GetEnvironmentVariable("TWINCAT_MCP_STATE_DIR");
        Environment.SetEnvironmentVariable("TWINCAT_MCP_STATE_DIR", state);
        try
        {
            using (var f = new Fixture())
            {
                byte[] bytes = f.Fb.Native;
                string old = f.Fb.Path;
                var result = Call(f, "move-plc-object", new { path = old, newParentPath = f.To.Path, expectedSha256 = Sha(f, f.Fb), confirm = "CONFIRM" });
                Check((bool)result["success"] && (bool)result["identityPreserved"], "Move succeeds and verifies identity");
                Check(!File.Exists(Path.Combine(f.DirectoryPath, "Source", "FB_Test.TcPOU")) && File.ReadAllBytes(f.FilePath(f.Fb)).SequenceEqual(bytes), "Native bytes physically relocated");
                Check(f.Fb.Children.Single().Path == f.Fb.Path + "^Run", "Nested members move with their owner");
                Check(f.Closes == 1 && f.Opens == 1 && !f.Closed, "One owned solution close/reopen");
                string recovery = (string)result["recoveryPath"];
                Check(File.Exists(Path.Combine(recovery, "manifest.json")) && File.ReadAllText(Path.Combine(recovery, "state.json")).Contains("verified"), "Durable successful move evidence");
                Check(!File.ReadAllText(f.ProjectFilePath).Contains("Source\\FB_Test.TcPOU") && File.ReadAllText(f.ProjectFilePath).Contains("Destination\\FB_Test.TcPOU"), "Project paths updated");
            }
            using (var f = new Fixture())
            {
                string hash = Sha(f, f.Fb);
                foreach (var args in new object[] {
                    new { path=f.Fb.Path, newParentPath=f.To.Path, expectedSha256="bad", confirm="CONFIRM" },
                    new { path=f.Fb.Path, newParentPath=f.From.Path, expectedSha256=hash, confirm="CONFIRM" },
                    new { path=f.Fb.Path, newParentPath=f.To.Path, expectedSha256=hash, confirm="no" },
                    new { path=f.Fb.Path, newParentPath="TIPC^Other^Other Project", expectedSha256=hash, confirm="CONFIRM" },
                    new { path=f.Fb.Path+"^Run", newParentPath=f.To.Path, expectedSha256=hash, confirm="CONFIRM" } })
                {
                    var rejected = Call(f, "move-plc-object", args);
                    Check(!(bool)rejected["success"] && !(bool)rejected["mutationAttempted"] && f.Closes == 0, "Invalid request rejected before mutation");
                }
                f.LoggedIn = true;
                Check(!(bool)Call(f, "move-plc-object", new { path=f.Fb.Path, newParentPath=f.To.Path, expectedSha256=hash, confirm="CONFIRM" })["success"], "Online move rejected");
                f.LoggedIn = false;
                var category = (Node)f.RootNode.Create("POUs", 601, null);
                Check(!(bool)Call(f, "move-plc-object", new { path=category.Path, newParentPath=f.To.Path, expectedSha256=Sha(f, category), confirm="CONFIRM" })["success"], "Standard category cannot be moved");
                Check(!(bool)Call(f, "move-plc-object", new { path=f.From.Path, newParentPath=f.Fb.Path, expectedSha256=Sha(f, f.From), confirm="CONFIRM" })["success"], "Descendant destination rejected");
                f.To.Create("FB_Test", 604, null);
                Check(!(bool)Call(f, "move-plc-object", new { path=f.Fb.Path, newParentPath=f.To.Path, expectedSha256=hash, confirm="CONFIRM" })["success"], "Tree collision rejected");
            }
            using (var f = new Fixture())
            {
                var original = File.ReadAllBytes(f.ProjectFilePath);
                f.FailOpen = true;
                var result = Call(f, "move-plc-object", new { path=f.Fb.Path, newParentPath=f.To.Path, expectedSha256=Sha(f, f.Fb), confirm="CONFIRM" });
                Check(!(bool)result["success"] && (bool)result["rolledBack"], "Reopen failure triggers rollback");
                Check(File.ReadAllBytes(f.ProjectFilePath).SequenceEqual(original) && File.Exists(f.FilePath(f.Fb)) && f.Fb.Parent == f.From, "Rollback restores exact project and source location");
                Check((bool)result["outcomeUnknown"] && f.Opens == 2, "Failure retains conservative uncertain receipt and restores XAE");
            }
            using (var f = new Fixture())
            {
                f.ExternalEdit = true;
                var result = Call(f, "move-plc-object", new { path=f.Fb.Path, newParentPath=f.To.Path, expectedSha256=Sha(f, f.Fb), confirm="CONFIRM" });
                Check(!(bool)result["success"] && result.ContainsKey("rollbackError") && File.ReadAllText(f.ProjectFilePath).Contains("external edit"), "Rollback never overwrites an external project edit");
                Check(File.Exists(Path.Combine((string)result["recoveryPath"], "project.before.plcproj")), "Recovery copy retained after refused rollback");
            }
            using (var f = new Fixture())
            {
                var plan = new PlcObjectMove(f.ProjectFilePath, "Source", "Destination", true);
                plan.Stage(state, "old", "new"); plan.Apply(); plan.Verify();
                Check(File.Exists(Path.Combine(f.DirectoryPath, "Destination", "Source", "FB_Test.TcPOU")), "Folder contents moved together");
                plan.Rollback(); Check(File.Exists(Path.Combine(f.DirectoryPath, "Source", "FB_Test.TcPOU")), "Folder rollback");
                Reject(() => new PlcObjectMove(f.ProjectFilePath, "..\\outside", "Destination", true), "Traversal must fail");
                Reject(() => new PlcObjectMove(f.ProjectFilePath, "Source", "Source", true), "Self destination must fail");
                Directory.CreateDirectory(Path.Combine(f.DirectoryPath, "Destination", "Source"));
                Reject(() => new PlcObjectMove(f.ProjectFilePath, "Source", "Destination", true), "Disk destination conflict must fail");
            }
            using (var f = new Fixture())
            {
                var plan = new PlcObjectMove(f.ProjectFilePath, "Source", "Destination", true);
                plan.Stage(state, "old", "new");
                File.AppendAllText(f.FilePath(f.Fb), "changed");
                Reject(plan.Apply, "External source edit must abort before rename");
                Check(Directory.Exists(plan.Source) && !Directory.Exists(plan.Destination), "Source edit leaves locations unchanged");
            }
            using (var f = new Fixture())
            {
                var plan = new PlcObjectMove(f.ProjectFilePath, "Source", "Destination", true);
                plan.Stage(state, "old", "new"); File.AppendAllText(f.ProjectFilePath, "<!-- changed -->");
                Reject(plan.Apply, "Project edit must abort before rename");
                File.WriteAllText(f.ProjectFilePath, "<!DOCTYPE Project [<!ENTITY x SYSTEM 'file:///unread'>]><Project>&x;</Project>");
                Reject(() => new PlcObjectMove(f.ProjectFilePath, "Source", "Destination", true), "DTD must fail");
            }
            using (var f = new Fixture())
            {
                var original = File.ReadAllBytes(f.ProjectFilePath);
                var plan = new PlcObjectMove(f.ProjectFilePath, "Source", "Destination", true);
                plan.Stage(state, "old", "new");
                try
                {
                    File.SetAttributes(f.ProjectFilePath, FileAttributes.ReadOnly);
                    Reject(plan.Apply, "Project replacement failure must be reported");
                }
                finally { File.SetAttributes(f.ProjectFilePath, FileAttributes.Normal); }
                plan.Rollback();
                Check(Directory.Exists(plan.Source) && !Directory.Exists(plan.Destination) && File.ReadAllBytes(f.ProjectFilePath).SequenceEqual(original), "Failure between rename and project replacement rolls back");
            }
            using (var f = new Fixture())
            {
                File.WriteAllText(f.ProjectFilePath, "<Project><ItemGroup><Folder Include='Virtual'/><Folder Include='Destination'/></ItemGroup></Project>");
                var plan = new PlcObjectMove(f.ProjectFilePath, "Virtual", "Destination", true);
                plan.Stage(state, "old", "new"); plan.Apply(); plan.Verify(); plan.Rollback();
                Check(File.ReadAllText(f.ProjectFilePath).Contains("Include='Virtual'"), "A project-only empty folder can move without a physical directory");
                foreach (string item in new[] {
                    "<Compile Include='Source\\FB_Test.TcPOU' Condition='true'/>",
                    "<Compile Include='Source\\FB_Test.TcPOU'><Link>Other</Link></Compile>",
                    "<Compile Include='Source\\*.TcPOU'/>",
                    "<Compile Include='.\\Source\\FB_Test.TcPOU'/>" })
                {
                    File.WriteAllText(f.ProjectFilePath, "<Project><ItemGroup><Folder Include='Source'/>" + item + "</ItemGroup></Project>");
                    Reject(() => new PlcObjectMove(f.ProjectFilePath, "Source", "Destination", true), "Dynamic or linked item layouts must be rejected");
                }
            }
        }
        finally { Environment.SetEnvironmentVariable("TWINCAT_MCP_STATE_DIR", previous); Directory.Delete(state, true); }
        Console.WriteLine("PASS: " + cases + " move policy, identity, file transaction and recovery checks; no XAE or PLC used.");
    }
}
