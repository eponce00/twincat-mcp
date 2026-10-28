using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using TCatSysManagerLib;
using TcAutomation.Core;

namespace TcAutomation.Commands
{
    // These small interfaces allow the object policy to be tested without XAE.
    public interface IPlcObjectNode
    {
        string Name { get; set; }
        string Path { get; }
        int SubType { get; }
        IPlcObjectNode[] Children { get; }
        string Declaration { get; set; }
        string Implementation { get; set; }
        IPlcObjectNode Create(string name, int subType, object info);
        void Delete(string name);
        byte[] ReadNativeFile();
    }

    public interface IPlcObjectSession
    {
        IPlcObjectNode Root { get; }
        bool LoggedIn { get; }
        void EnsureExclusive();
        void Save();
        void ImportPlcOpen(string file, bool folders);
        void ExportPlcOpen(string file, string selection);
    }

    public interface IPlcMoveSession
    {
        string ProjectFilePath { get; }
        string FilePath(IPlcObjectNode node);
        void CloseForMove();
        void ReopenAfterMove();
        void AbortMove();
    }

    public static class PlcObjectsCommand
    {
        const int MaxNodes = 4096;
        const int MaxText = 4 * 1024 * 1024;
        static readonly Dictionary<string, int> Types = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            {"folder", 601}, {"program", 602}, {"function", 603}, {"functionBlock", 604},
            {"enum", 605}, {"struct", 606}, {"union", 607}, {"action", 608},
            {"method", 609}, {"interfaceMethod", 610}, {"property", 611},
            {"interfaceProperty", 612}, {"propertyGet", 613}, {"propertySet", 614},
            {"gvl", 615}, {"interface", 618}, {"alias", 623},
            {"interfacePropertyGet", 654}, {"interfacePropertySet", 655}
        };

        public static object ExecuteInSession(VisualStudioInstance vs, string target, string plcName,
            string command, JsonElement args)
        {
            try
            {
                var automation = new AutomationInterface(vs);
                if (string.IsNullOrWhiteSpace(target) || automation.TargetNetId != target ||
                    string.IsNullOrWhiteSpace(plcName) || plcName.Contains("^"))
                    throw new ArgumentException("Explicit matching target and PLC required.");
                var root = ((ITcProjectRoot)automation.SystemManager.LookupTreeItem("TIPC^" + plcName)).NestedProject;
                // Serialize other MCP workers operating on the same solution. Human IDE
                // conflicts are checked separately; no foreign DTE is adopted or closed.
                using var mutex = new Mutex(false, "Local\\TwinCAT-MCP-Objects-" + Hash(
                    System.IO.Path.GetFullPath(vs.SolutionFilePath).ToUpperInvariant()));
                bool acquired;
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new InvalidOperationException("Another worker is editing this solution.");
                try
                {
                    var result = Execute(new ComSession(vs, plcName), command, args);
                    if (!vs.IsOwnedProcessRunning)
                    {
                        result["success"] = false;
                        result["outcomeUnknown"] = true;
                        result["code"] = "worker_lost";
                        if (!result.ContainsKey("errorMessage")) result["errorMessage"] = "The owned shell was lost; close the expired context before inspecting recovery state.";
                    }
                    return result;
                }
                finally { mutex.ReleaseMutex(); }
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { {"success", false}, {"mutationAttempted", false}, {"errorMessage", ex.Message} };
            }
        }

        public static Dictionary<string, object> Execute(IPlcObjectSession session, string command, JsonElement args)
        {
            var result = new Dictionary<string, object> { {"success", false}, {"mutationAttempted", false}, {"saved", false} };
            bool mutating = command != "list-plc-objects" && command != "read-plc-object" && command != "export-plc-objects";
            try
            {
                if (mutating)
                {
                    session.EnsureExclusive();
                    if (session.LoggedIn) throw new InvalidOperationException("Object authoring requires an offline PLC project. Use the existing matching-login source operations for online change.");
                }
                var root = session.Root;
                if (command == "list-plc-objects")
                {
                    var parent = Resolve(root, Str(args, "path") ?? root.Path, true);
                    int offset = Int(args, "offset", 0), limit = Int(args, "limit", 100);
                    if (offset < 0 || limit < 1 || limit > 200) throw new ArgumentException("Invalid list page.");
                    var children = Children(parent);
                    if (offset > children.Length) throw new ArgumentException("Offset exceeds child count.");
                    result["rootPath"] = root.Path;
                    result["path"] = parent.Path;
                    result["objects"] = children.Skip(offset).Take(limit).Select(Metadata).ToArray();
                    result["total"] = children.Length;
                    result["nextOffset"] = offset + limit < children.Length ? (object)(offset + limit) : null;
                }
                else if (command == "read-plc-object")
                {
                    var node = Resolve(root, Required(args, "path"), true);
                    result["object"] = Describe(node);
                }
                else if (command == "create-plc-object" || command == "create-plc-folder")
                {
                    var parent = Resolve(root, Required(args, "parentPath"), true);
                    string kind = command == "create-plc-folder" ? "folder" : Required(args, "objectType");
                    if (!Types.TryGetValue(kind, out int subType)) throw new ArgumentException("Unsupported objectType.");
                    string name = Required(args, "name");
                    ValidateName(name, kind == "folder");
                    if (((kind == "propertyGet" || kind == "interfacePropertyGet") && name != "Get") ||
                        ((kind == "propertySet" || kind == "interfacePropertySet") && name != "Set"))
                        throw new ArgumentException("Property accessors must be named Get or Set.");
                    ValidateParent(root, parent, subType);
                    NoConflict(parent, name);
                    string declaration = Str(args, "declaration"), implementation = Str(args, "implementation");
                    declaration = Linking(declaration, subType, Str(args, "linkingPolicy"));
                    if (kind == "folder" && (declaration != null || implementation != null))
                        throw new ArgumentException("Folders have no source sections.");
                    if (HasDeclaration(subType) && kind != "propertyGet" && kind != "propertySet" && declaration == null)
                        throw new ArgumentException("Declaration is required for this object type.");
                    if (implementation != null && !HasImplementation(subType)) throw new ArgumentException("This type has no implementation section.");
                    if (declaration != null && !HasDeclaration(subType)) throw new ArgumentException("This type has no declaration section.");
                    object info = CreationInfo(kind, args, declaration);
                    result["requestedPath"] = parent.Path + "^" + name;
                    result["mutationAttempted"] = true;
                    var created = parent.Create(name, subType, info);
                    result["createdPath"] = created.Path;
                    SetSource(created, declaration, implementation);
                    Save(session, result);
                    result["object"] = Describe(created);
                }
                else if (command == "update-plc-object")
                {
                    var node = Resolve(root, Required(args, "path"), false);
                    RequireEditable(root, node);
                    Match(node, Required(args, "expectedSha256"));
                    string declaration = Str(args, "declaration"), implementation = Str(args, "implementation");
                    if (Str(args, "linkingPolicy") != null && Str(args, "linkingPolicy") != "preserve")
                        declaration = Linking(declaration ?? node.Declaration, node.SubType, Str(args, "linkingPolicy"));
                    if (declaration == null && implementation == null) throw new ArgumentException("Supply declaration or implementation.");
                    if (declaration != null && !HasDeclaration(node.SubType)) throw new ArgumentException("This type has no declaration section.");
                    if (implementation != null && !HasImplementation(node.SubType)) throw new ArgumentException("This type has no implementation section.");
                    result["path"] = node.Path;
                    result["mutationAttempted"] = true;
                    SetSource(node, declaration, implementation);
                    Save(session, result);
                    result["object"] = Describe(node);
                }
                else if (command == "rename-plc-object")
                {
                    var node = Resolve(root, Required(args, "path"), false);
                    RequireEditable(root, node);
                    if (new[] { 613, 614, 654, 655 }.Contains(node.SubType))
                        throw new ArgumentException("Property accessors have fixed Get/Set names.");
                    Match(node, Required(args, "expectedSha256"));
                    string name = Required(args, "name");
                    ValidateName(name, node.SubType == 601);
                    var parent = Resolve(root, node.Path.Substring(0, node.Path.LastIndexOf('^')), true);
                    NoConflict(parent, name);
                    result["originalPath"] = node.Path;
                    result["mutationAttempted"] = true;
                    node.Name = name;
                    result["renamedPath"] = node.Path;
                    Save(session, result);
                    result["object"] = Describe(node);
                }
                else if (command == "delete-plc-object")
                {
                    if (Required(args, "confirm") != "CONFIRM") throw new ArgumentException("Confirmation required.");
                    var node = Resolve(root, Required(args, "path"), false);
                    RequireEditable(root, node);
                    Match(node, Required(args, "expectedSha256"));
                    if (node.SubType == 601 && Children(node).Length > 0 && !Bool(args, "recursive", false))
                        throw new ArgumentException("Nonempty folders require recursive=true.");
                    if (node.SubType == 601 && Bool(args, "recursive", false) && Str(args, "recursiveConfirm") != "DELETE_CONTENTS")
                        throw new ArgumentException("Recursive folder deletion requires recursiveConfirm=DELETE_CONTENTS.");
                    var parent = Resolve(root, node.Path.Substring(0, node.Path.LastIndexOf('^')), true);
                    string deletedName = node.Name;
                    result["deletedPath"] = node.Path;
                    result["mutationAttempted"] = true;
                    parent.Delete(deletedName);
                    result["deleted"] = true;
                    Save(session, result);
                    if (Children(parent).Any(n => n.Name.Equals(deletedName, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("Deleted object still appears in the project tree.");
                }
                else if (command == "move-plc-object") Move(session, args, result);
                else if (command == "import-plc-objects") Import(session, args, result);
                else if (command == "export-plc-objects") Export(session, args, result);
                else throw new ArgumentException("Unknown object command.");
                result["success"] = true;
            }
            catch (Exception ex)
            {
                result["errorMessage"] = ex.Message;
                // COM setters and save failures can leave changes applied. Never replay.
                result["outcomeUnknown"] = (bool)result["mutationAttempted"] || result.ContainsKey("fileWriteAttempted");
            }
            return result;
        }

        static void Move(IPlcObjectSession session, JsonElement args, Dictionary<string, object> result)
        {
            if (Required(args, "confirm") != "CONFIRM") throw new ArgumentException("Confirmation required.");
            var mover = session as IPlcMoveSession ?? throw new InvalidOperationException("Session cannot relocate saved project files.");
            var root = session.Root;
            var node = Resolve(root, Required(args, "path"), false);
            RequireEditable(root, node);
            var parent = Resolve(root, Required(args, "newParentPath"), true);
            bool ProjectFolder(IPlcObjectNode item) => item.Path == root.Path ||
                (item.SubType == 601 && ProjectFolder(Resolve(root, item.Path.Substring(0, item.Path.LastIndexOf('^')), true)));
            var oldParent = Resolve(root, node.Path.Substring(0, node.Path.LastIndexOf('^')), true);
            if ((!NativeFileType(node.SubType) && node.SubType != 601) || !ProjectFolder(oldParent) || !ProjectFolder(parent))
                throw new ArgumentException("Move supports top-level PLC objects and project folders; members move with their containing native object.");
            if (oldParent.Path == parent.Path) throw new ArgumentException("Already in this parent; use rename_object to change its name.");
            if (parent.Path == node.Path || parent.Path.StartsWith(node.Path + "^", StringComparison.Ordinal))
                throw new ArgumentException("Cannot move a folder into itself or its descendants.");
            NoConflict(parent, node.Name);
            Match(node, Required(args, "expectedSha256"));
            using var deadline = new OwnedDeadline(mover.AbortMove, Int(args, "timeoutSeconds", 180));
            session.Save();
            Match(node, Required(args, "expectedSha256"));
            string originalPath = node.Path, movedPath = parent.Path + "^" + node.Name;
            string content = ContentFingerprint(node);
            long nativeBytes = 0;
            var native = Walk(node).Where(n => NativeFileType(n.SubType)).Select(n =>
            {
                var bytes = n.ReadNativeFile();
                nativeBytes += bytes.Length;
                if (nativeBytes > 64 * 1024 * 1024) throw new InvalidOperationException("Native move snapshots exceed 64 MiB.");
                return new { suffix = n.Path.Substring(originalPath.Length), bytes, path = mover.FilePath(n) };
            }).ToArray();
            string relative = originalPath.Substring(root.Path.Length + 1).Replace('^', Path.DirectorySeparatorChar);
            if (node.SubType != 601) relative += System.IO.Path.GetExtension(mover.FilePath(node));
            string destinationParent = parent.Path == root.Path ? "" : parent.Path.Substring(root.Path.Length + 1).Replace('^', Path.DirectorySeparatorChar);
            var plan = new PlcObjectMove(mover.ProjectFilePath, relative, destinationParent, node.SubType == 601);
            foreach (var file in native)
            {
                string expected = node.SubType == 601 ? System.IO.Path.Combine(plan.Source,
                    file.suffix.TrimStart('^').Replace('^', Path.DirectorySeparatorChar) + System.IO.Path.GetExtension(file.path)) : plan.Source;
                if (!System.IO.Path.GetFullPath(file.path).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Linked or nonstandard source-file layouts cannot be moved.");
                if (PlcObjectMove.Hash(ReadBounded(file.path)) != PlcObjectMove.Hash(file.bytes))
                    throw new InvalidOperationException("Saved source differs from its native object snapshot.");
            }
            plan.Stage(SessionFile.SessionDir, originalPath, movedPath);
            result["recoveryPath"] = plan.RecoveryPath;
            result["originalPath"] = originalPath;
            result["originalSha256"] = Required(args, "expectedSha256");
            result["requestedPath"] = movedPath;
            result["filesMoved"] = plan.Files.Length;
            result["updatedProjectEntries"] = plan.UpdatedEntries;
            result["saveAttempted"] = true;
            bool closed = false;
            try
            {
                plan.Record("closing_solution");
                result["mutationAttempted"] = true;
                mover.CloseForMove(); closed = true;
                plan.Apply(); result["saved"] = true;
                plan.Record("reopening_solution");
                mover.ReopenAfterMove(); closed = false;
                plan.Verify();
                var moved = Resolve(session.Root, movedPath, false);
                if (ContentFingerprint(moved) != content) throw new InvalidOperationException("Relocated source/subtree differs from the original.");
                foreach (var file in native)
                    if (PlcObjectMove.Hash(Resolve(session.Root, movedPath + file.suffix, false).ReadNativeFile()) != PlcObjectMove.Hash(file.bytes))
                        throw new InvalidOperationException("Relocated native file differs; identity preservation could not be verified.");
                if (Walk(session.Root).Any(n => n.Path == originalPath)) throw new InvalidOperationException("Original object remains registered.");
                var description = Describe(moved);
                plan.Record("verified");
                deadline.Complete();
                result["movedObjectPath"] = movedPath;
                result["identityPreserved"] = true;
                result["solutionReloaded"] = true;
                result["object"] = description;
            }
            catch
            {
                result["phase"] = plan.Phase;
                result["rollbackAttempted"] = false;
                if (!deadline.Expired)
                {
                    try
                    {
                        result["rollbackAttempted"] = true;
                        if (!closed) mover.CloseForMove();
                        plan.Rollback();
                        mover.ReopenAfterMove();
                        result["rolledBack"] = true;
                        result["saved"] = false;
                    }
                    catch (Exception ex) { result["rollbackError"] = ex.Message; }
                }
                throw;
            }
        }

        static string ContentFingerprint(IPlcObjectNode node)
        {
            var text = new StringBuilder();
            foreach (var item in Walk(node))
            {
                foreach (string part in new[] { item.Path.Substring(node.Path.Length), item.SubType.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    HasDeclaration(item.SubType) ? item.Declaration : "", HasImplementation(item.SubType) ? item.Implementation : "" })
                    text.Append(part.Length).Append(':').Append(part);
                if (text.Length > MaxText) throw new InvalidOperationException("Source snapshot exceeds 4 MiB.");
            }
            return Hash(text.ToString());
        }

        static void Import(IPlcObjectSession session, JsonElement args, Dictionary<string, object> result)
        {
            string format = Required(args, "format"), source = Required(args, "filePath");
            if (!System.IO.Path.IsPathRooted(source)) throw new ArgumentException("filePath must be absolute.");
            var bytes = ReadBounded(source);
            if (!Hash(bytes).Equals(Required(args, "expectedFileSha256"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Import file changed; SHA256 mismatch.");
            var doc = ParseXml(bytes);
            var existing = Walk(session.Root).ToArray();
            string[] names;
            IPlcObjectNode parent = session.Root;
            int nativeType = 0;
            if (format == "native")
            {
                if (doc.Root?.Name.LocalName != "TcPlcObject")
                    throw new ArgumentException("Expected one native TcPlcObject object.");
                var objects = doc.Root.Elements().Where(e => e.Name.LocalName != "LineIds").ToArray();
                if (objects.Length != 1) throw new ArgumentException("Expected one native TcPlcObject object.");
                var item = objects.Single();
                string tag = item.Name.LocalName;
                nativeType = tag == "GVL" ? 615 : tag == "DUT" ? 606 : tag == "POU" ? 604 : tag == "Itf" ? 618 : 0;
                if (nativeType == 0) throw new ArgumentException("Native import supports TcGVL, TcDUT, TcPOU and TcIO files.");
                var ids = NativeIds(doc);
                if (item.Attribute("Id") == null || ids.Length == 0 || ids.Distinct().Count() != ids.Length)
                    throw new ArgumentException("Native object IDs must be nonempty unique GUIDs.");
                foreach (var node in existing.Where(n => NativeFileType(n.SubType)))
                {
                    var savedBytes = node.ReadNativeFile();
                    if (savedBytes != null && NativeIds(ParseXml(savedBytes)).Intersect(ids).Any())
                        throw new InvalidOperationException("Native GUID conflict with an existing PLC object; create a new object instead of overwriting its identity.");
                }
                names = new[] { (string)item.Attribute("Name") ?? "" };
                ValidateName(names[0], false);
                parent = Resolve(session.Root, Required(args, "parentPath"), true);
                ValidateParent(session.Root, parent, nativeType);
                NoConflict(parent, names[0]);
            }
            else if (format == "plcopen")
            {
                if (doc.Root?.Name.LocalName != "project" || doc.Root.Name.NamespaceName.IndexOf("plcopen.org", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new ArgumentException("Expected a PLCopen XML project, not native TcPlcObject XML.");
                if (Str(args, "parentPath") != null && Str(args, "parentPath") != session.Root.Path)
                    throw new ArgumentException("PLCopen import uses its exported folder structure at the PLC root.");
                names = doc.Descendants().Where(e => e.Name.Namespace == doc.Root.Name.Namespace &&
                    (e.Name.LocalName == "pou" || e.Name.LocalName == "dataType" || e.Name.LocalName == "globalVars"))
                    .Select(e => (string)e.Attribute("name")).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
                if (names.Length == 0) throw new ArgumentException("PLCopen file contains no named POUs, DUTs or GVLs.");
                foreach (var name in names) ValidateName(name, false);
            }
            else throw new ArgumentException("format must be native or plcopen.");
            // Never pass rename/replace options to XAE. Check project-wide PLC names
            // before importing; silently renamed imports would invalidate agent paths.
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length ||
                existing.Any(n => NativeFileType(n.SubType) && names.Contains(n.Name, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Import name conflict; no overwrite or automatic rename is permitted.");
            // Operate on an immutable, validated copy to close the file hash race.
            string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "twincat-object-" + Guid.NewGuid().ToString("N") +
                (format == "plcopen" ? ".xml" : nativeType == 615 ? ".TcGVL" : nativeType == 606 ? ".TcDUT" : nativeType == 618 ? ".TcIO" : ".TcPOU"));
            File.WriteAllBytes(temp, bytes);
            var before = new HashSet<string>(existing.Select(n => n.Path), StringComparer.Ordinal);
            result["importedPaths"] = new string[0];
            try
            {
                result["mutationAttempted"] = true;
                if (format == "native") parent.Create(names[0], 58, temp);
                else session.ImportPlcOpen(temp, Bool(args, "preserveFolders", true));
                Save(session, result);
                var after = Walk(session.Root).ToArray();
                result["importedPaths"] = after.Where(n => !before.Contains(n.Path)).Select(n => n.Path).ToArray();
                if (names.Any(name => !after.Any(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase))))
                    throw new InvalidOperationException("Imported object was not found in the project tree.");
            }
            finally
            {
                if ((bool)result["mutationAttempted"])
                {
                    try { result["importedPaths"] = Walk(session.Root).Where(n => !before.Contains(n.Path)).Select(n => n.Path).ToArray(); }
                    catch { /* Keep the earlier receipt if the COM connection was lost. */ }
                }
                try { File.Delete(temp); } catch { }
            }
        }

        static void Export(IPlcObjectSession session, JsonElement args, Dictionary<string, object> result)
        {
            string format = Required(args, "format"), output = Required(args, "filePath");
            if (!System.IO.Path.IsPathRooted(output) || !Directory.Exists(System.IO.Path.GetDirectoryName(output)))
                throw new ArgumentException("Export requires an absolute path in an existing directory.");
            if (File.Exists(output)) throw new IOException("Export destination exists; overwrite is prohibited.");
            string[] paths = args.GetProperty("paths").EnumerateArray().Select(e => e.GetString()).ToArray();
            if (paths.Length < 1 || paths.Length > 32) throw new ArgumentException("Select 1..32 object paths.");
            var nodes = paths.Select(p => Resolve(session.Root, p, false)).ToArray();
            foreach (var node in nodes) RequireEditable(session.Root, node);
            byte[] bytes;
            if (format == "native")
            {
                if (nodes.Length != 1 || !NativeFileType(nodes[0].SubType))
                    throw new ArgumentException("Native export selects one top-level POU, DUT, GVL or interface including its nested members.");
                bytes = nodes[0].ReadNativeFile();
                if (ParseXml(bytes).Root?.Name.LocalName != "TcPlcObject") throw new InvalidOperationException("Saved native file is not TcPlcObject XML.");
            }
            else if (format == "plcopen")
            {
                string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "twincat-export-" + Guid.NewGuid().ToString("N") + ".xml");
                try
                {
                    string prefix = session.Root.Path + "^";
                    session.ExportPlcOpen(temp, string.Join(";", paths.Select(p => p.Substring(prefix.Length).Replace('^', '.'))));
                    bytes = ReadBounded(temp);
                    if (ParseXml(bytes).Root?.Name.LocalName != "project") throw new InvalidOperationException("XAE did not export PLCopen XML.");
                }
                finally { try { File.Delete(temp); } catch { } }
            }
            else throw new ArgumentException("format must be native or plcopen.");
            result["filePath"] = output;
            result["fileWriteAttempted"] = true;
            using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(bytes, 0, bytes.Length);
            result["sha256"] = Hash(bytes);
            result["bytes"] = bytes.Length;
        }

        static void ValidateParent(IPlcObjectNode root, IPlcObjectNode parent, int type)
        {
            var owner = parent;
            while (owner.SubType == 601 && owner.Path != root.Path)
                owner = Resolve(root, owner.Path.Substring(0, owner.Path.LastIndexOf('^')), true);
            bool member = type == 608 || type == 609 || type == 611;
            bool itfMember = type == 610 || type == 612;
            bool accessor = type == 613 || type == 614 || type == 654 || type == 655;
            if (member && owner.SubType != 602 && owner.SubType != 604)
                throw new ArgumentException("Members require a program/function block or its folder.");
            if (itfMember && parent.SubType != 618) throw new ArgumentException("Interface member requires an interface parent.");
            if (accessor && parent.SubType != (type >= 654 ? 612 : 611)) throw new ArgumentException("Accessor requires the corresponding property parent.");
            if (!member && !itfMember && !accessor && type != 601 && owner.Path != root.Path)
                throw new ArgumentException("Top-level PLC objects require a PLC project or folder parent.");
            if (type == 601 && parent.Path != root.Path && parent.SubType != 601 && parent.SubType != 602 && parent.SubType != 604)
                throw new ArgumentException("Folder parent must be a project, folder, program or function block.");
        }

        static object CreationInfo(string kind, JsonElement args, string declaration)
        {
            string returnType = Str(args, "returnType"), access = Str(args, "access") ?? "PUBLIC";
            if (!new[] { "PUBLIC", "PRIVATE", "PROTECTED", "INTERNAL" }.Contains(access)) throw new ArgumentException("Invalid access modifier.");
            if (new[] { "function", "property", "interfaceMethod", "interfaceProperty" }.Contains(kind) && string.IsNullOrWhiteSpace(returnType))
                throw new ArgumentException("returnType is required for this object type.");
            if (kind == "function" || kind == "property") return new object[] { "ST", returnType };
            if (kind == "method") return new object[] { "ST", returnType ?? "", access };
            if (kind == "interfaceMethod" || kind == "interfaceProperty") return new object[] { returnType };
            if (kind == "propertyGet" || kind == "propertySet") return new object[] { "ST", access };
            if (kind == "program" || kind == "functionBlock" || kind == "action") return "ST";
            if (kind == "gvl" || kind == "struct" || kind == "enum" || kind == "union" || kind == "alias") return declaration;
            return null;
        }

        static void RequireEditable(IPlcObjectNode root, IPlcObjectNode node)
        {
            if (node.Path == root.Path || !Types.Values.Contains(node.SubType))
                throw new ArgumentException("Only PLC source objects and folders may be changed.");
            string relative = node.Path.Substring(root.Path.Length + 1);
            if (!relative.Contains("^") && node.SubType == 601 && new[] { "POUs", "DUTs", "GVLs" }.Contains(node.Name))
                throw new ArgumentException("Standard PLC category folders cannot be renamed or deleted.");
        }

        static IPlcObjectNode Resolve(IPlcObjectNode root, string path, bool allowRoot)
        {
            if (path == root.Path && allowRoot) return root;
            if (path == null || !path.StartsWith(root.Path + "^", StringComparison.Ordinal))
                throw new ArgumentException("Path must be inside the selected PLC project.");
            var node = root;
            foreach (string part in path.Substring(root.Path.Length + 1).Split('^'))
            {
                if (string.IsNullOrWhiteSpace(part) || part == "." || part == "..") throw new ArgumentException("Invalid project path.");
                node = Children(node).SingleOrDefault(n => n.Name.Equals(part, StringComparison.Ordinal))
                    ?? throw new ArgumentException("PLC object not found: " + path);
            }
            return node;
        }

        static IPlcObjectNode[] Children(IPlcObjectNode node) => node.Children.OrderBy(n => n.Name, StringComparer.Ordinal).ToArray();
        static IEnumerable<IPlcObjectNode> Walk(IPlcObjectNode root)
        {
            var queue = new Queue<IPlcObjectNode>();
            queue.Enqueue(root);
            int count = 0;
            while (queue.Count > 0)
            {
                if (++count > MaxNodes) throw new InvalidOperationException("Object tree exceeds 4096-node safety bound. Select a smaller subtree.");
                var node = queue.Dequeue();
                yield return node;
                foreach (var child in Children(node)) queue.Enqueue(child);
            }
        }
        static Dictionary<string, object> Metadata(IPlcObjectNode node) => new Dictionary<string, object>
        {
            {"name", node.Name}, {"path", node.Path}, {"typeId", node.SubType},
            {"objectType", Types.FirstOrDefault(p => p.Value == node.SubType).Key ?? "container"},
            {"childCount", node.Children.Length}, {"hasDeclaration", HasDeclaration(node.SubType)},
            {"hasImplementation", HasImplementation(node.SubType)}
        };
        static Dictionary<string, object> Describe(IPlcObjectNode node)
        {
            var data = Metadata(node);
            if (HasDeclaration(node.SubType)) data["declaration"] = node.Declaration;
            if (HasImplementation(node.SubType)) data["implementation"] = node.Implementation;
            data["sha256"] = Fingerprint(node);
            return data;
        }
        static string Fingerprint(IPlcObjectNode node)
        {
            var text = new StringBuilder();
            foreach (var item in Walk(node))
            {
                foreach (string part in new[] { item.Path, item.SubType.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    HasDeclaration(item.SubType) ? item.Declaration : "", HasImplementation(item.SubType) ? item.Implementation : "" })
                    text.Append(part.Length).Append(':').Append(part);
                if (text.Length > MaxText) throw new InvalidOperationException("Source snapshot exceeds 4 MiB; select a smaller subtree.");
            }
            return Hash(text.ToString());
        }
        static void Match(IPlcObjectNode node, string expected)
        {
            if (!Fingerprint(node).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Object changed since read; expected SHA256 mismatch.");
        }
        static void NoConflict(IPlcObjectNode parent, string name)
        {
            if (Children(parent).Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("An object with this name already exists; overwrite is prohibited.");
        }
        static void ValidateName(string name, bool folder)
        {
            string pattern = folder ? @"^[A-Za-z_][A-Za-z0-9_ -]{0,127}$" : @"^[A-Za-z_][A-Za-z0-9_]{0,127}$";
            if (!Regex.IsMatch(name ?? "", pattern) || name.EndsWith(" ")) throw new ArgumentException("Invalid PLC object name.");
        }
        static void SetSource(IPlcObjectNode node, string declaration, string implementation)
        {
            if (declaration != null) node.Declaration = declaration;
            if (implementation != null) node.Implementation = implementation;
            if ((declaration != null && node.Declaration != declaration) || (implementation != null && node.Implementation != implementation))
                throw new InvalidOperationException("Source readback differs from the requested text.");
        }
        public static string Linking(string declaration, int type, string policy)
        {
            if (policy == null || policy == "preserve") return declaration;
            if (policy != "always" && policy != "default") throw new ArgumentException("Invalid linkingPolicy.");
            if (!new[] { 602, 603, 604, 615 }.Contains(type) || declaration == null)
                throw new ArgumentException("Explicit linking policy requires a program, function, FB or GVL declaration.");
            var pragma = new Regex(@"(?im)^\s*\{\s*attribute\s+'linkalways'\s*\}\s*\r?\n?", RegexOptions.CultureInvariant);
            string text = pragma.Replace(declaration, "");
            return policy == "always" ? "{attribute 'linkalways'}\n" + text : text;
        }
        static void Save(IPlcObjectSession session, Dictionary<string, object> result)
        {
            result["saveAttempted"] = true;
            session.Save();
            result["saved"] = true;
        }
        static bool HasDeclaration(int type) => Types.Values.Contains(type) && type != 601 && type != 608 && type != 654 && type != 655;
        static bool HasImplementation(int type) => new[] { 602, 603, 604, 608, 609, 613, 614 }.Contains(type);
        static byte[] ReadBounded(string path)
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaxText) throw new ArgumentException("File exceeds 4 MiB limit.");
            using var buffer = new MemoryStream();
            input.CopyTo(buffer);
            if (buffer.Length > MaxText) throw new ArgumentException("File exceeds 4 MiB limit.");
            return buffer.ToArray();
        }
        static XDocument ParseXml(byte[] bytes)
        {
            using var input = new MemoryStream(bytes);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxText });
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        static bool NativeFileType(int type) => new[] { 602, 603, 604, 605, 606, 607, 615, 618, 623 }.Contains(type);
        static Guid[] NativeIds(XDocument document)
        {
            // Numeric LineId values are source-editor metadata, not object GUIDs.
            return document.Descendants().Where(e => new[] { "POU", "DUT", "GVL", "Itf", "Method", "Action", "Property", "Get", "Set", "Folder" }.Contains(e.Name.LocalName))
                .Attributes("Id").Select(a => Guid.TryParse(a.Value, out var id) && id != Guid.Empty
                    ? id : throw new ArgumentException("Native object IDs must be nonempty GUIDs.")).ToArray();
        }
        static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));
        static string Hash(byte[] bytes)
        {
            using var hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        static string Str(JsonElement args, string key) => args.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
        static string Required(JsonElement args, string key) => Str(args, key) ?? throw new ArgumentException(key + " is required.");
        static int Int(JsonElement args, string key, int fallback) => args.TryGetProperty(key, out var value) ? value.GetInt32() : fallback;
        static bool Bool(JsonElement args, string key, bool fallback) => args.TryGetProperty(key, out var value) ? value.GetBoolean() : fallback;

        sealed class ComSession : IPlcObjectSession, IPlcMoveSession
        {
            readonly VisualStudioInstance vs;
            readonly string plcName;
            ITcSmTreeItem root => ((ITcProjectRoot)vs.GetSystemManager().LookupTreeItem("TIPC^" + plcName)).NestedProject;
            public ComSession(VisualStudioInstance vs, string plcName) { this.vs = vs; this.plcName = plcName; }
            public IPlcObjectNode Root => new ComNode(root);
            public bool LoggedIn => MatchingLoginCommand.IsLoggedIn(root);
            public void EnsureExclusive() => vs.EnsureExclusiveSolution();
            public void Save() { vs.RecordSourceMutation(); vs.Dte.ExecuteCommand("File.SaveAll", ""); }
            public void ImportPlcOpen(string file, bool folders) => ((ITcPlcIECProject)root).PlcOpenImport(file, 0, "", folders);
            public void ExportPlcOpen(string file, string selection) => ((ITcPlcIECProject)root).PlcOpenExport(file, selection);
            public string ProjectFilePath => BuildArtifacts.Capture(vs, plcName).Single().ProjectPath;
            public string FilePath(IPlcObjectNode node) => ((ComNode)node).FilePath;
            public void CloseForMove() => vs.CloseSolutionForMove();
            public void ReopenAfterMove() { vs.LoadSolution(); vs.RecordSourceMutation(); }
            public void AbortMove() => vs.KillOwnedProcess("object move deadline");
        }
        sealed class ComNode : IPlcObjectNode
        {
            readonly ITcSmTreeItem item;
            public ComNode(ITcSmTreeItem item) { this.item = item; }
            public string Name { get => item.Name; set => item.Name = value; }
            public string Path => item.PathName;
            // CreateChild calls these numbers SubType, but PLC nodes expose
            // their 600-series object kind as ItemType (ItemSubType is zero).
            public int SubType => (int)item.ItemType;
            public IPlcObjectNode[] Children => Enumerable.Range(1, item.ChildCount).Select(i => (IPlcObjectNode)new ComNode(item.Child[i])).ToArray();
            public string Declaration { get => ((ITcPlcDeclaration)item).DeclarationText; set => ((ITcPlcDeclaration)item).DeclarationText = value; }
            public string Implementation { get => ((ITcPlcImplementation)item).ImplementationText; set => ((ITcPlcImplementation)item).ImplementationText = value; }
            public IPlcObjectNode Create(string name, int type, object info) => new ComNode(item.CreateChild(name, type, "", info));
            public void Delete(string name) => item.DeleteChild(name);
            public byte[] ReadNativeFile()
            {
                return ReadBounded(FilePath);
            }
            public string FilePath
            {
                get
                {
                    var projectItem = ((ITcSmTreeItem8)item).VSProjectItem as EnvDTE.ProjectItem;
                    if (projectItem == null || projectItem.FileCount < 1) throw new InvalidOperationException("XAE did not expose the saved native object file.");
                    return projectItem.FileNames[1];
                }
            }
        }
    }
}
