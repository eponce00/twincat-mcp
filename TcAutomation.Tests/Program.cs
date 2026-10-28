using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TcAutomation.Commands;
using TcAutomation.Core;

class Node : IPlcObjectNode
{
    public string Name { get; set; }
    public Node Parent;
    public string Path => Parent == null ? "TIPC^PLC^PLC Project" : Parent.Path + "^" + Name;
    public int SubType { get; set; }
    public List<Node> Items = new List<Node>();
    public IPlcObjectNode[] Children => Items.ToArray();
    public string Declaration { get; set; } = "";
    public string Implementation { get; set; } = "";
    public bool RejectImplementation;
    public byte[] Native;
    string IPlcObjectNode.Implementation { get => Implementation; set { if (RejectImplementation) throw new IOException("setter failed"); Implementation = value; } }
    public IPlcObjectNode Create(string name, int type, object info)
    {
        var node = new Node { Name = name, Parent = this, SubType = type };
        Items.Add(node);
        return node;
    }
    public void Delete(string name) => Items.RemoveAll(n => n.Name == name);
    public byte[] ReadNativeFile() => Native;
}
class Session : IPlcObjectSession
{
    public Node RootNode = new Node { Name = "PLC Project", SubType = 600 };
    public IPlcObjectNode Root => RootNode;
    public bool LoggedIn { get; set; }
    public bool Busy, SaveFails;
    public int Saves, Imports;
    public void EnsureExclusive() { if (Busy) throw new IOException("another IDE"); }
    public void Save() { Saves++; if (SaveFails) throw new IOException("save failed"); }
    public void ImportPlcOpen(string file, bool folders) { Imports++; RootNode.Create("Imported", 604, null); }
    public void ExportPlcOpen(string file, string selection) => File.WriteAllText(file, "<project xmlns='http://www.plcopen.org/xml/tc6_0200' />");
}
static class Program
{
    static int cases;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static Dictionary<string, object> Call(Session s, string command, object args, bool success = true)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(args));
        var result = PlcObjectsCommand.Execute(s, command, json.RootElement);
        Check((bool)result["success"] == success, command + ": " + JsonSerializer.Serialize(result));
        cases++;
        return result;
    }
    static string Sha(Session s, Node node) => (string)((Dictionary<string, object>)Call(s, "read-plc-object", new { path = node.Path })["object"])["sha256"];
    static string Hash(byte[] bytes) { using var h = SHA256.Create(); return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    static void Main()
    {
        var s = new Session();
        var root = s.RootNode;
        Call(s, "create-plc-folder", new { parentPath = root.Path, name = "Motion" });
        var folder = root.Items.Single();
        Call(s, "create-plc-folder", new { parentPath = folder.Path, name = "Internal" });
        Call(s, "create-plc-object", new { parentPath = folder.Path, name = "FB_Axis", objectType = "functionBlock", declaration = "FUNCTION_BLOCK FB_Axis", implementation = "// ST" });
        var fb = folder.Items.Single(n => n.Name == "FB_Axis");
        Call(s, "create-plc-object", new { parentPath = folder.Path, name = "InvalidMethod", objectType = "method", declaration = "METHOD InvalidMethod" }, false);
        Call(s, "create-plc-object", new { parentPath = fb.Path, name = "InvalidStruct", objectType = "struct", declaration = "TYPE InvalidStruct : STRUCT END_STRUCT END_TYPE" }, false);
        Call(s, "create-plc-object", new { parentPath = fb.Path, name = "Run", objectType = "method", declaration = "METHOD Run", implementation = "// Run" });
        var listed = Call(s, "list-plc-objects", new { path = folder.Path, limit = 1 });
        Check((int)listed["total"] == 2 && (int)listed["nextOffset"] == 1, "List paging");
        Call(s, "list-plc-objects", new { path = folder.Path, offset = 2, limit = 1 });
        Call(s, "list-plc-objects", new { path = folder.Path, offset = 3 }, false);
        Call(s, "create-plc-folder", new { parentPath = root.Path, name = "motion" }, false);
        Call(s, "create-plc-folder", new { parentPath = root.Path, name = "../escape" }, false);
        Call(s, "read-plc-object", new { path = "TIPC^OTHER^OTHER Project" }, false);
        Call(s, "create-plc-object", new { parentPath = root.Path, name = "F_Test", objectType = "function", declaration = "FUNCTION F_Test" }, false);
        Call(s, "create-plc-object", new { parentPath = root.Path, name = "ST_Test", objectType = "struct", declaration = "TYPE ST_Test: STRUCT END_STRUCT END_TYPE", implementation = "bad" }, false);
        string hash = Sha(s, fb);
        Call(s, "update-plc-object", new { path = fb.Path, expectedSha256 = hash, implementation = "// changed" });
        Call(s, "update-plc-object", new { path = fb.Path, expectedSha256 = hash, implementation = "// stale" }, false);
        Call(s, "update-plc-object", new { path = fb.Path, expectedSha256 = Sha(s, fb) }, false);
        s.LoggedIn = true;
        Call(s, "create-plc-folder", new { parentPath = root.Path, name = "Online" }, false);
        Call(s, "read-plc-object", new { path = fb.Path });
        s.LoggedIn = false;
        s.Busy = true;
        Call(s, "create-plc-folder", new { parentPath = root.Path, name = "Busy" }, false);
        s.Busy = false;
        Call(s, "create-plc-object", new { parentPath = folder.Path, name = "I_Test", objectType = "interface", declaration = "INTERFACE I_Test" });
        var itf = folder.Items.Single(n => n.Name == "I_Test");
        Call(s, "create-plc-object", new { parentPath = itf.Path, name = "Value", objectType = "interfaceProperty", returnType = "INT", declaration = "PROPERTY Value : INT" });
        var prop = itf.Items.Single();
        Call(s, "create-plc-object", new { parentPath = prop.Path, name = "Wrong", objectType = "interfacePropertyGet" }, false);
        Call(s, "create-plc-object", new { parentPath = prop.Path, name = "Get", objectType = "interfacePropertyGet" });
        Call(s, "create-plc-object", new { parentPath = prop.Path, name = "Set", objectType = "interfacePropertySet", implementation = "not allowed" }, false);
        Call(s, "create-plc-object", new { parentPath = prop.Path, name = "Set", objectType = "interfacePropertySet" });
        var getter = prop.Items.Single(n => n.Name == "Get");
        Call(s, "rename-plc-object", new { path = getter.Path, expectedSha256 = Sha(s, getter), name = "Other" }, false);
        string folderHash = Sha(s, folder);
        fb.Implementation += " external";
        Call(s, "delete-plc-object", new { path = folder.Path, expectedSha256 = folderHash, confirm = "CONFIRM", recursive = true, recursiveConfirm = "DELETE_CONTENTS" }, false);
        Call(s, "delete-plc-object", new { path = folder.Path, expectedSha256 = Sha(s, folder), confirm = "CONFIRM" }, false);
        Call(s, "delete-plc-object", new { path = folder.Path, expectedSha256 = Sha(s, folder), confirm = "CONFIRM", recursive = true }, false);
        s.SaveFails = true;
        var failed = Call(s, "update-plc-object", new { path = fb.Path, expectedSha256 = Sha(s, fb), implementation = "// saved?" }, false);
        Check((bool)failed["mutationAttempted"] && (bool)failed["outcomeUnknown"] && fb.Implementation == "// saved?", "Partial edit receipt");
        s.SaveFails = false;
        fb.RejectImplementation = true;
        failed = Call(s, "update-plc-object", new { path = fb.Path, expectedSha256 = Sha(s, fb), declaration = "FUNCTION_BLOCK FB_Axis // updated", implementation = "fail" }, false);
        Check((bool)failed["outcomeUnknown"] && fb.Declaration.EndsWith("updated"), "Partial setter receipt");
        fb.RejectImplementation = false;
        Call(s, "rename-plc-object", new { path = folder.Path, expectedSha256 = Sha(s, folder), name = "Axes" });
        Check(fb.Path.Contains("^Axes^"), "Nested paths after rename");
        var standard = (Node)root.Create("POUs", 601, null);
        Call(s, "delete-plc-object", new { path = standard.Path, expectedSha256 = Sha(s, standard), confirm = "CONFIRM" }, false);
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "twincat-object-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string import = System.IO.Path.Combine(dir, "source.xml"), output = System.IO.Path.Combine(dir, "export.TcPOU");
            File.WriteAllText(import, "<!DOCTYPE project [<!ENTITY x SYSTEM 'file:///unread'>]><project xmlns='http://www.plcopen.org/xml/tc6_0200'>&x;</project>");
            Call(s, "import-plc-objects", new { format = "plcopen", filePath = import, expectedFileSha256 = Hash(File.ReadAllBytes(import)) }, false);
            Check(s.Imports == 0, "DTD rejection before import");
            File.WriteAllText(import, "<project xmlns='http://www.plcopen.org/xml/tc6_0200'><types><pous><pou name='Imported'/></pous></types></project>");
            Call(s, "import-plc-objects", new { format = "plcopen", filePath = import, expectedFileSha256 = new string('0', 64) }, false);
            Call(s, "import-plc-objects", new { format = "plcopen", filePath = import, expectedFileSha256 = Hash(File.ReadAllBytes(import)) });
            Call(s, "import-plc-objects", new { format = "plcopen", filePath = import, expectedFileSha256 = Hash(File.ReadAllBytes(import)) }, false);
            Check(s.Imports == 1, "Duplicate import must not dispatch");
            fb.Native = Encoding.UTF8.GetBytes("<TcPlcObject><POU Name='FB_Axis' /></TcPlcObject>");
            Call(s, "export-plc-objects", new { format = "native", filePath = output, paths = new[] { fb.Path } });
            Check(File.ReadAllBytes(output).SequenceEqual(fb.Native), "Native export bytes");
            Call(s, "export-plc-objects", new { format = "native", filePath = output, paths = new[] { fb.Path } }, false);
            Check(File.ReadAllBytes(output).SequenceEqual(fb.Native), "No destination overwrite");
            Check(!StepDispatcher.IsResultSuccessful(new Dictionary<string, object> { { "success", false } }), "Native dictionary failures must propagate");
            string id = Guid.NewGuid().ToString("B");
            fb.Native = Encoding.UTF8.GetBytes("<TcPlcObject><POU Name='FB_Axis' Id='" + id + "'/></TcPlcObject>");
            File.WriteAllText(import, "<TcPlcObject><POU Name='OtherName' Id='" + id + "'/></TcPlcObject>");
            Call(s, "import-plc-objects", new { format = "native", filePath = import, expectedFileSha256 = Hash(File.ReadAllBytes(import)), parentPath = root.Path }, false);
            Check(!root.Items.Any(n => n.Name == "OtherName"), "GUID collision must not mutate");
            File.WriteAllText(import, "<TcPlcObject><POU Name='OtherName' Id='{00000000-0000-0000-0000-000000000000}'/></TcPlcObject>");
            Call(s, "import-plc-objects", new { format = "native", filePath = import, expectedFileSha256 = Hash(File.ReadAllBytes(import)), parentPath = root.Path }, false);
            File.WriteAllText(import, "<TcPlcObject><POU Name='OtherName' Id='" + Guid.NewGuid().ToString("B") + "'/><LineIds><LineId Id='4' Count='1'/></LineIds></TcPlcObject>");
            // Numeric editor IDs must not be treated as invalid object GUIDs.
            Call(s, "import-plc-objects", new { format = "native", filePath = import, expectedFileSha256 = Hash(File.ReadAllBytes(import)), parentPath = root.Path });
        }
        finally { Directory.Delete(dir, true); }
        Call(s, "delete-plc-object", new { path = folder.Path, expectedSha256 = Sha(s, folder), confirm = "CONFIRM", recursive = true, recursiveConfirm = "DELETE_CONTENTS" });
        Check(!root.Items.Contains(folder), "Recursive delete");
        Console.WriteLine("PASS: " + cases + " PLC object policy cases; no XAE or PLC used.");
        FeedbackTests.Run();
        MoveTests.Run();
        ReviewTests.Run();
    }
}
