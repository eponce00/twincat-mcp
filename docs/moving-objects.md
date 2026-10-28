# Move PLC objects and project folders

`engineering.move_object` reorganizes an existing PLC project while preserving
native file bytes, object GUIDs and nested members. A move is useful when an
object's identity, methods, properties, attributes and source history should
survive a layout change. For new independent objects, create/delete remain
appropriate operations.

## Contract

Open an engineering context, obtain a context-scoped grant for
`engineering.move_object`, and read the source with `engineering.read_object`
to obtain its current subtree hash. The destination must already exist in the
same PLC project. Execute:

```json
{
  "operation": "engineering.move_object",
  "arguments": {
    "contextHandle": "<context handle>",
    "grantHandle": "<grant handle>",
    "path": "TIPC^PLC^PLC Project^POUs^FB_Axis",
    "newParentPath": "TIPC^PLC^PLC Project^Motion",
    "expectedSha256": "<hash from read_object>",
    "confirm": "CONFIRM",
    "timeoutSeconds": 180
  },
  "requestKey": "<new UUID>",
  "waitSeconds": 120
}
```

The operation keeps the object's name. A move to its existing parent is
rejected with a hint to use `engineering.rename_object`. There is no implicit
folder creation, overwrite, automatic rename, cross-PLC move or runtime action.
The three public MCP tools remain unchanged.

Supported sources are project folders and top-level programs, functions,
function blocks, GVLs, interfaces and DUTs. Nested methods, actions, properties,
accessors and member folders travel inside their native owner file. Moving an
individual member or member folder is not supported. Standard root category
folders (`POUs`, `DUTs`, `GVLs`), the PLC root and infrastructure nodes cannot be
moved. A folder cannot be moved into itself or its descendants.

## Execution and verification

The documented [Automation Interface tree API](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/21841647883.html)
does not expose a general reparent operation. This implementation uses the
Automation Interface for tree validation and source readback, with a controlled
filesystem relocation while the owned solution is closed:

1. Check the context target, source subtree hash, destination, offline state
   and exclusive editing access. Save pending source and recheck the hash.
2. Prepare a bounded move plan from the saved `.plcproj`, source files and
   native object snapshots. Persist a recovery manifest and before/after
   project copies before closing the solution.
3. Close the owned solution. All PLC projects in that solution must be offline;
   an active build prevents the close. Move the existing file or directory
   within the PLC project and replace its project entries using an atomic file
   replacement.
4. Reopen the same solution in the same owned XAE shell. Verify the updated
   project bytes, relocated file hashes, source subtree and native file bytes.
   Confirm that the original tree path is absent.

An object is not recreated through import, and its native XML GUIDs are not
rewritten. Folder moves update matching project-relative `Compile`, `Folder`,
`None`, `Content` and `EmbeddedResource` paths and relocate physical contents
together. Project-only empty folders are supported. Reopening resets the
shell's observed build/artifact and compiler-check state; subsequent reads use
the refreshed project tree. Existing path-based source snapshots must be read
again after a move.

Success returns `movedObjectPath`, `originalPath`, `originalSha256`, a fresh
`object.sha256`, `identityPreserved:true`, `solutionReloaded:true`,
`filesMoved`, `updatedProjectEntries` and `recoveryPath`. Identity verification
uses exact native bytes, which include nested object GUIDs and metadata.

The operation does not build or prove application behavior. Run
`engineering.check_all_objects` and, where applicable, `workflow.build_verify`
after reorganizing code. Object names and references inside source are not
rewritten. See [build feedback](build-feedback.md).

## Failures and recovery

A move is one durable job and request key, with several native/filesystem
steps. It is not a crash-atomic transaction. Reuse the original request key
after a lost reply; never resubmit an uncertain move with a new key without
inspecting its outcome.

Detected source/project changes, destination conflicts, linked files, reparse
points and unsupported layouts fail before relocation. If a later step fails,
the worker attempts to close the owned solution, restore the files and exact
original project bytes, and reopen the original layout. Rollback refuses to
overwrite detected external edits or a recreated original location. Receipts
report `phase`, `mutationAttempted`, `saved`, `rollbackAttempted`, `rolledBack`
and `rollbackError` where applicable. A failed move after dispatch remains
conservatively `outcomeUnknown:true`, even when rollback succeeds; close the
expired context before opening a replacement.

`timeoutSeconds` is 1–600, default 180, after initial shell selection. An expired
deadline terminates only the owned shell and leaves the recovery record for
inspection. Automatic rollback is skipped after deadline expiry. Files can
already have reached the destination; an uncertain receipt does not mean the
move was undone.

Recovery records live under `%LOCALAPPDATA%/twincat-mcp/moves/<id>` or the
configured `TWINCAT_MCP_STATE_DIR`. They contain:

- `manifest.json`: original/destination tree and filesystem paths, project
  hashes, file hashes and source existence.
- `project.before.plcproj` and `project.after.plcproj`: exact prepared project
  versions, for inspection and recovery at the original project location.
- `state.json`: the last persisted phase.

Native source files remain at their original or relocated location; the
recovery directory does not contain an additional source backup. After a crash,
stop the owning server, inspect both locations and compare their bytes with the
manifest before recovering. Keep external edits intact. No automatic replay or
restart-time recovery changes project files. Recovery records contain project
metadata and are retained until explicitly removed while servers are stopped.

## Bounds and qualification

Use exclusive editing access; the IDE conflict check and file hash checks do
not lock out every external editor for the entire operation. Supported projects
use explicit, unconditional, project-relative `Compile` entries with source
files in the matching physical folders. Linked source layouts, conditional
Compile items, wildcard/macro paths and path aliases are rejected. A move is
bounded to 4,096 tree/filesystem entries, 4 MiB for source subtree/native
snapshots and 64 MiB of relocated file data. External file references embedded
inside source or resource contents are not rewritten.

The native harness covers source/destination guards, nested-member and byte
preservation, folder moves, project-only folders, source/project edit detection,
rollback after project-write/reopen failures and retention of external edits.
Real stdio MCP qualification on XAE 3.1.4026.26 covers an FB with nested members,
a folder containing multiple native object kinds, empty folders, move-back and
successful compilation after reloading. No PLC login, activation or download
is performed. Other XAE versions and custom project layouts need separate
qualification.
