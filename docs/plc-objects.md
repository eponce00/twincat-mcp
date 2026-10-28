# PLC objects and folders

Use `twincat_search` and `twincat_describe` to discover the `engineering.*`
operations below. Execute them through `twincat_execute`. Every operation is
submitted with a unique request key and returns a durable job receipt.

The worker creates objects in the loaded PLC project through Automation Interface
so XAE owns their registration, identity and persistence. Source authoring uses
Structured Text; native object files and PLCopen XML provide transfer formats.

## Scope and prerequisites

Open a context for the solution, target AMS Net ID and PLC name. The target
configured in the project must match the context. `engineering.list_objects`
with only `contextHandle` returns the selected PLC root and its direct children.
Subsequent calls use those exact Automation Interface paths, separated by `^`.
Listing is paged with `offset` and `limit` (default 100, maximum 200).

Obtain a context-scoped safety grant for each authoring/export operation needed.
Offline authoring rejects a logged-in PLC project and a detected second IDE with
the same solution open. A named mutex serializes object operations across local
MCP workers. IDE conflict detection cannot prevent another application from
opening or changing the project after the check; use exclusive editing access.
No command logs in, downloads code, activates a configuration or restarts a target.

## Supported operations

| Operation | Behavior |
| --- | --- |
| `engineering.list_objects` | Browse direct children, types, paths and source capabilities. |
| `engineering.read_object` | Read metadata, supported source sections and a SHA256 snapshot. |
| `engineering.create_folder` | Create and save one folder below a project, folder or supported POU. |
| `engineering.create_object` | Create a typed object, assign its source, save and read it back. |
| `engineering.update_object` | Update declaration, ST implementation or both with a matching snapshot. |
| `engineering.rename_object` | Rename an object or folder with a matching snapshot. |
| `engineering.delete_object` | Delete an object or folder with a matching snapshot and confirmation. |
| `engineering.import_objects` | Import a verified native object or PLCopen XML file. |
| `engineering.export_objects` | Export to a new file and return its SHA256. |

Object creation supports `program`, `function`, `functionBlock`, `gvl`, `struct`,
`enum`, `union`, `alias`, `method`, `action`, `interface`, `interfaceMethod`,
`property`, `interfaceProperty`, `propertyGet`, `propertySet`,
`interfacePropertyGet` and `interfacePropertySet`. Textual
implementations use Structured Text. Functions and properties require a return
type; interface methods and interface properties require one too. Use only the
source sections supported by the selected type. A declaration is required except
for actions and property accessors. Accessors are named `Get` and `Set`;
interface accessors have neither declaration nor implementation text. Creation
parameters and source declarations must describe the same object and signature.

Folders are project-tree objects created through Automation Interface, not only
directories on disk. `parentPath` selects their placement. Nested folders are
supported; no parent is created implicitly. Names are bounded ASCII identifiers;
folder names additionally allow spaces and hyphens. Existing names are never
overwritten or automatically renamed.

## Create and edit

After opening a context and granting `engineering.create_folder` and
`engineering.create_object`, execute:

```json
{
  "operation": "engineering.create_folder",
  "arguments": {
    "contextHandle": "<context>",
    "grantHandle": "<grant>",
    "parentPath": "TIPC^PLC1^PLC1 Project",
    "name": "Motion"
  },
  "requestKey": "<new UUID>"
}
```

Use the returned `object.path` as the parent of a new FB:

```json
{
  "operation": "engineering.create_object",
  "arguments": {
    "contextHandle": "<context>",
    "grantHandle": "<grant>",
    "parentPath": "TIPC^PLC1^PLC1 Project^Motion",
    "name": "FB_Axis",
    "objectType": "functionBlock",
    "declaration": "FUNCTION_BLOCK FB_Axis\nVAR_OUTPUT\n    nPosition : DINT;\nEND_VAR",
    "implementation": "nPosition := nPosition + 1;"
  },
  "requestKey": "<new UUID>"
}
```

`engineering.read_object` returns `object.sha256`. Pass that as `expectedSha256`
to updates, renames and deletion. The hash covers the path, supported source
sections and descendant objects; a folder or FB hash therefore changes when a
contained object changes. Refresh the snapshot after every change. Updates accept
either section or both. Declaration-only objects reject implementations.

Object hashes differ from the section hashes returned by
`engineering.read_plc_source`; they cannot be interchanged. The existing
`engineering.edit_plc_source` operation retains its matching-online-login rule.
Use that separate workflow for [online change](online-change.md).

Renaming uses XAE's object name setter. Source references are not rewritten by
this API; inspect them and compile afterward. Standard `POUs`, `DUTs` and `GVLs`
category folders cannot be renamed or deleted. Nonempty folder deletion requires
`recursive:true`, `confirm:"CONFIRM"` and `recursiveConfirm:"DELETE_CONTENTS"`.
Deleting a POU also removes its nested members.

## Import and export

Native TwinCAT XML (`format:"native"`) and PLCopen XML (`format:"plcopen"`) are
different formats. Native import accepts one POU, DUT, GVL or interface file and
requires `parentPath`. Native export selects one such object and includes its
nested methods/properties from the saved native file. It requires XAE's
`ITcSmTreeItem8.VSProjectItem` file access. Native object GUIDs are preserved;
empty, duplicate and conflicting object GUIDs are rejected. Numeric editor
line IDs are not object GUIDs.

PLCopen export accepts 1–32 selected paths. PLCopen import targets the selected
PLC root, preserves exported folder information by default, and returns actual
`importedPaths`. A native `parentPath` does not relocate PLCopen contents. Both
formats reject PLC object name conflicts rather than replacing or silently
renaming objects. Referenced objects and libraries must exist in the destination
or be included in the import.

Imports require an absolute `filePath` and `expectedFileSha256`. The worker reads
and verifies the file, rejects DTDs, and passes an immutable temporary copy to
XAE. Input and exported files are bounded to 4 MiB. Export requires a scoped
grant because it writes to the filesystem. Its absolute destination directory
must already exist, and an existing destination file is never overwritten.
Use native filenames such as `FB_Axis.TcPOU`, `ST_Axis.TcDUT`, `GVL_Axis.TcGVL`
or `I_Axis.TcIO`; use `.xml` for PLCopen exports.

## Completion, compilation and failures

Creation saves source and confirms tree/source readback. It does not prove the
object has compiled or appears in generated symbols. Reference new code from
the application as appropriate, run `engineering.check_all_objects`, build, and
inspect diagnostics and fresh output. Unreferenced objects may need an explicit
linking policy; missing TMC symbols alone do not establish registration failure.

`workflow.sequence` can compose known object paths and build/check operations;
each step needs its own required handles. It stops on failure and does not roll
back completed steps. Root/subtree snapshots have a 4,096-node and 4 MiB source
bound; browse smaller subtrees when those bounds are reached.

Failure receipts distinguish `mutationAttempted`, `createdPath`, `importedPaths`,
`saveAttempted`, `saved`, `deleted` and `outcomeUnknown` where applicable. A source
setter or save failure can leave a partially applied edit. Inspect the receipt
and project before submitting another request key. Reuse the original request
key after a lost response to retrieve the existing receipt without replay.

## Automation Interface references

- [Object/folder creation and native template import](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/21842468363.html)
- [Declaration and implementation interfaces](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/21842480395.html)
- [Automation Interface manual: PLCopen import/export](https://download.beckhoff.com/download/document/automation/twincat3/TC3_Automation_Interface_EN.pdf)
- [Compiler linking policy](https://infosys.beckhoff.com/content/1033/tc3_plc_intro/2529686923.html)

## Qualification

The native in-memory policy harness runs with `scripts/test-plc-objects.ps1`.
Python catalog, routing, orchestration and protocol tests run with
`scripts/test-mcp-automated.ps1`.

Offline integration on XAE 3.1.4026.26 in a disposable project copy verified all
listed creation types, nested folders and FB member folders, source read/write,
folder rename and recursive deletion. Native POU/interface/GVL/DUT exports were
reimported after deletion, followed by a PLCopen hierarchy round trip. Reopened
source and generated TMC symbols were checked; fresh builds completed with zero
errors or warnings. Standard-template dependencies, including `Tc3_Module`, are
required in the test project. No runtime login, activation or download is needed
for these checks. Other XAE versions and runtime deployment require their own
qualification.
