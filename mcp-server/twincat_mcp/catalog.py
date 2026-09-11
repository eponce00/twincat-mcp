"""Fixed public tools over a searchable, validated operation catalog."""

import copy
import json
import re
from pathlib import Path

from jsonschema import Draft202012Validator

from .errors import OperationError

HANDLE = {"type": "string", "minLength": 1, "maxLength": 256}
TARGET = {"type": "string", "pattern": r"^(?:\d{1,3}\.){5}\d{1,3}$"}


def schema(properties, required=()):
    return {
        "type": "object",
        "properties": properties,
        "required": list(required),
        "additionalProperties": False,
    }


def operation(id, description, properties, required=(), read_only=False, kind="control"):
    return dict(
        id=id,
        description=description,
        inputSchema=schema(properties, required),
        kind=kind,
        readOnly=read_only,
        requiresGrant=False,
        engineering=False,
    )


class Catalog:
    def __init__(self):
        entries = json.loads(
            Path(__file__).with_name("operations.json").read_text(encoding="utf-8")
        )
        entries += [
            operation(
                "context.open",
                "Reserve an exclusive engineering context for a solution, PLC and target. Shell opens lazily. Close when finished.",
                {
                    "solutionPath": {"type": "string", "minLength": 1},
                    "amsNetId": TARGET,
                    "plcName": {"type": "string", "minLength": 1},
                    "tcVersion": {"type": "string", "minLength": 1},
                },
                ("solutionPath", "amsNetId", "plcName"),
            ),
            operation(
                "context.close",
                "Release this context and its owned shell when prior queued work finishes. Later calls using its handle fail.",
                {"contextHandle": HANDLE},
                ("contextHandle",),
            ),
            operation(
                "context.status",
                "Inspect a context without starting TwinCAT.",
                {"contextHandle": HANDLE},
                ("contextHandle",),
                True,
            ),
            operation(
                "safety.grant",
                "Grant specific operations on exactly one target, context or Scope config for a fixed lifetime. Calling host must obtain user authorization; a grant is not authentication.",
                {
                    "operations": {
                        "type": "array",
                        "items": {"type": "string"},
                        "minItems": 1,
                        "maxItems": 50,
                        "uniqueItems": True,
                    },
                    "contextHandle": HANDLE,
                    "amsNetId": TARGET,
                    "configPath": {"type": "string", "minLength": 1},
                    "reason": {"type": "string", "minLength": 1, "maxLength": 2000},
                    "ttlSeconds": {
                        "type": "integer",
                        "minimum": 1,
                        "maximum": 3600,
                        "default": 900,
                    },
                },
                ("operations", "reason"),
            ),
            operation(
                "safety.revoke",
                "Revoke a grant immediately without a request key. Subsequent workflow steps recheck it; already dispatched effects are not undone.",
                {"grantHandle": HANDLE},
                ("grantHandle",),
            ),
            operation(
                "job.get",
                "Poll receipt and progress without repeating work. Use job.result for output.",
                {"jobHandle": HANDLE},
                ("jobHandle",),
                True,
            ),
            operation(
                "job.result",
                "Read a bounded JSON text page. Concatenate pages by offset to reconstruct full output.",
                {
                    "jobHandle": HANDLE,
                    "offset": {"type": "integer", "minimum": 0, "default": 0},
                    "limit": {"type": "integer", "minimum": 256, "maximum": 16000, "default": 8000},
                },
                ("jobHandle",),
                True,
            ),
            operation(
                "job.cancel",
                "Cancel queued work or stop before the next workflow step. Dispatched commands may finish; poll receipt.",
                {"jobHandle": HANDLE},
                ("jobHandle",),
            ),
            operation(
                "system.status",
                "Application version and queue status. Does not start TwinCAT.",
                {},
                read_only=True,
            ),
            operation(
                "system.reap_orphans",
                "Clean dead-parent workers recorded in session files using PID start-time verification.",
                {},
            ),
            operation(
                "workflow.sequence",
                "Run up to 32 validated catalog operations in order, stopping on first failure. Each step includes its handles. No rollback, nesting or replay.",
                {
                    "steps": {
                        "type": "array",
                        "minItems": 1,
                        "maxItems": 32,
                        "items": schema(
                            {"operation": {"type": "string"}, "arguments": {"type": "object"}},
                            ("operation", "arguments"),
                        ),
                    }
                },
                ("steps",),
                kind="workflow",
            ),
            operation(
                "workflow.wait_state",
                "Poll runtime state until expectedState or timeout; returns final observation and attempt count.",
                {
                    "amsNetId": TARGET,
                    "port": {"type": "integer", "minimum": 1, "maximum": 65535, "default": 851},
                    "expectedState": {"type": "string", "enum": ["Run", "Stop", "Config"]},
                    "timeoutSeconds": {
                        "type": "integer",
                        "minimum": 1,
                        "maximum": 600,
                        "default": 60,
                    },
                    "intervalSeconds": {
                        "type": "number",
                        "minimum": 0.1,
                        "maximum": 30,
                        "default": 1,
                    },
                },
                ("amsNetId", "expectedState"),
                True,
                "workflow",
            ),
        ]
        self.entries = {e["id"]: e for e in sorted(entries, key=lambda e: e["id"])}
        if len(entries) != len(self.entries):
            raise RuntimeError("Duplicate catalog operation")
        for e in entries:
            Draft202012Validator.check_schema(e["inputSchema"])
            e["prerequisites"] = (
                ["context.open: retain contextHandle"] if e["engineering"] else []
            ) + (
                ["safety.grant: scope grantHandle to this operation and target/context"]
                if e["requiresGrant"]
                else []
            )
            immediate = e["id"] in {
                "system.status",
                "context.status",
                "safety.revoke",
                "job.get",
                "job.result",
                "job.cancel",
            }
            e["result"] = (
                "Returns immediately; no requestKey is required."
                if immediate
                else "Submitted work returns a durable job receipt; small final results are inline, larger results use job.result pages."
            )
            example = example_for(e["inputSchema"])
            if e["id"] == "safety.grant":
                example = dict(
                    operations=["runtime.write_var"],
                    amsNetId="1.2.3.4.1.1",
                    reason="User approved test target write",
                )
            if e["id"] == "workflow.sequence":
                example = dict(
                    steps=[
                        dict(operation="runtime.get_state", arguments=dict(amsNetId="1.2.3.4.1.1"))
                    ]
                )
            self.validate(e["id"], example)
            e["example"] = dict(operation=e["id"], arguments=example)
            if not immediate:
                e["example"]["requestKey"] = "replace-with-unique-request-key"

    def get(self, id):
        if id not in self.entries:
            raise OperationError(
                "unknown_operation", f"Unknown operation {id!r}; use twincat_search."
            )
        return self.entries[id]

    def validate(self, id, arguments):
        entry = self.get(id)
        value = copy.deepcopy(arguments)
        errors = sorted(
            Draft202012Validator(entry["inputSchema"]).iter_errors(value), key=lambda e: str(e.path)
        )
        if errors:
            raise OperationError("invalid_arguments", errors[0].message)
        for key, prop in entry["inputSchema"]["properties"].items():
            if key not in value and "default" in prop:
                value[key] = copy.deepcopy(prop["default"])
        if "amsNetId" in value and any(int(p) > 255 for p in value["amsNetId"].split(".")):
            raise OperationError("invalid_target", "AMS Net ID octets must be in 0..255.")
        if id == "workflow.sequence":
            for step in value["steps"]:
                child = self.get(step["operation"])
                if child["kind"] == "control" or step["operation"] == "workflow.sequence":
                    raise OperationError(
                        "invalid_step", "Sequence cannot contain controls or nested sequences."
                    )
                step["arguments"] = self.validate(step["operation"], step["arguments"])
        return value

    def search(self, query="", category=None, limit=8, offset=0):
        terms = set(re.findall(r"[a-z0-9]+", query.lower().replace("_", " ")))
        synonyms = {
            "compile": "build",
            "tests": "test",
            "plc": "runtime",
            "recording": "record",
            "variables": "var",
            "session": "context",
            "batch": "sequence",
            "arm": "grant",
        }
        terms |= {synonyms[t] for t in list(terms) if t in synonyms}
        ranked = []
        for e in self.entries.values():
            if category and e["id"].split(".")[0] != category:
                continue
            name = e["id"].replace("_", " ")
            text = name + " " + e["description"].lower()
            score = sum(5 if t in name else 1 for t in terms if t in text)
            if terms and not score:
                continue
            ranked.append((-score, e["id"], e))
        ranked.sort(key=lambda row: (row[0], row[1]))
        return {
            "matches": [
                dict(
                    operation=e["id"],
                    summary=e["description"].split(". ")[0],
                    kind=e["kind"],
                    requiresGrant=e["requiresGrant"],
                )
                for _, _, e in ranked[offset : offset + limit]
            ],
            "total": len(ranked),
            "nextOffset": offset + limit if offset + limit < len(ranked) else None,
        }

    def describe(self, ids):
        return {
            "operations": [copy.deepcopy(self.get(id)) for id in ids],
            "usage": "Call twincat_execute with operation and arguments. Submitted work requires a unique requestKey; reuse exactly that key and arguments after a lost response. Poll job.get and read job.result.",
        }


def example_for(spec):
    examples = {
        "solutionPath": "C:/Projects/Machine/Machine.sln",
        "amsNetId": "1.2.3.4.1.1",
        "plcName": "PLC",
        "contextHandle": "<context handle>",
        "grantHandle": "<grant handle>",
        "recordingHandle": "<recording handle>",
        "jobHandle": "<job handle>",
        "expectedSha256": "0" * 64,
        "cycleSymbol": "MAIN.cycles",
        "symbol": "MAIN.x",
        "inputPath": "C:/Traces/recording.svdx",
        "configPath": "C:/Traces/recording.tcscopex",
        "expectedState": "Run",
        "configuration": "Release",
        "platform": "TwinCAT RT (x64)",
        "path": "TIPC^PLC^PLC Project^POUs^MAIN",
    }
    result = {}
    for key in spec.get("required", []):
        prop = spec["properties"][key]
        if key in examples:
            value = examples[key]
        elif "const" in prop:
            value = prop["const"]
        elif "enum" in prop:
            value = prop["enum"][0]
        elif prop.get("type") == "integer":
            value = prop.get("default", prop.get("minimum", 0))
        elif prop.get("type") == "number":
            value = prop.get("default", prop.get("minimum", 1))
        elif prop.get("type") == "boolean":
            value = True
        elif prop.get("type") == "array":
            value = (
                [{"operation": "runtime.get_state", "arguments": {"amsNetId": "1.2.3.4.1.1"}}]
                if key == "steps"
                else ["MAIN.x"]
            )
        elif prop.get("type") == "object":
            value = {"MAIN.x": "1"}
        else:
            value = "example"
        result[key] = value
    return result
