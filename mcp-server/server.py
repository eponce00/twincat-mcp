"""TwinCAT MCP v2: discover operations, inspect contracts, execute durable jobs."""

import asyncio
import json
from contextlib import asynccontextmanager

from jsonschema import Draft202012Validator
from mcp.server import Server
from mcp.server.stdio import stdio_server
from mcp.types import CallToolResult, ListToolsResult, Tool

from twincat_mcp.catalog import schema
from twincat_mcp.engine import VERSION, Engine
from twincat_mcp.errors import OperationError

OUTPUT_SCHEMA = {"type": "object"}
TOOLS = [
    Tool(
        name="twincat_search",
        description="Find TwinCAT operations and workflows by task. Returns compact summaries; browse with an empty query. Use describe for contracts.",
        input_schema=schema(
            {
                "query": {"type": "string", "maxLength": 1000, "default": ""},
                "category": {
                    "type": "string",
                    "enum": [
                        "context",
                        "engineering",
                        "runtime",
                        "scope",
                        "workflow",
                        "safety",
                        "system",
                        "job",
                    ],
                },
                "limit": {"type": "integer", "minimum": 1, "maximum": 20, "default": 8},
                "offset": {"type": "integer", "minimum": 0, "default": 0},
            }
        ),
        output_schema=OUTPUT_SCHEMA,
        annotations={"readOnlyHint": True, "idempotentHint": True, "openWorldHint": False},
    ),
    Tool(
        name="twincat_describe",
        description="Read full input schemas, prerequisites and examples for up to five operation IDs found by search.",
        input_schema=schema(
            {
                "operations": {
                    "type": "array",
                    "items": {"type": "string"},
                    "minItems": 1,
                    "maxItems": 5,
                }
            },
            ("operations",),
        ),
        output_schema=OUTPUT_SCHEMA,
        annotations={"readOnlyHint": True, "idempotentHint": True, "openWorldHint": False},
    ),
    Tool(
        name="twincat_execute",
        description="Execute a catalog operation with schema-validated arguments. May modify PLCs/projects. Work is queued; provide a unique requestKey and reuse it unchanged after response loss. Poll job.get/read job.result. context.open establishes engineering scope; safety.grant authorizes named operations after host user approval.",
        input_schema=schema(
            {
                "operation": {"type": "string", "minLength": 1, "maxLength": 128},
                "arguments": {"type": "object"},
                "requestKey": {"type": "string", "minLength": 8, "maxLength": 128},
                "waitSeconds": {"type": "number", "minimum": 0, "maximum": 2, "default": 1},
            },
            ("operation", "arguments"),
        ),
        output_schema=OUTPUT_SCHEMA,
        annotations={
            "readOnlyHint": False,
            "destructiveHint": True,
            "idempotentHint": False,
            "openWorldHint": True,
        },
    ),
]
TOOL_MAP = {t.name: t for t in TOOLS}


def result(payload, error=False):
    return CallToolResult(
        content=[{"type": "text", "text": json.dumps(payload, ensure_ascii=False)}],
        structured_content=payload,
        is_error=error,
    )


def create_server(engine_factory=Engine):
    @asynccontextmanager
    async def lifespan(_server):
        engine = engine_factory()
        try:
            yield engine
        finally:
            await asyncio.to_thread(engine.close)

    async def list_tools(ctx, params):
        if params and params.cursor:
            from mcp import MCPError

            raise MCPError(-32602, "Tool list fits on one page.")
        return ListToolsResult(tools=TOOLS, ttl_ms=3600000, cache_scope="public")

    async def call_tool(ctx, params):
        try:
            if params.name not in TOOL_MAP:
                from mcp import MCPError

                raise MCPError(-32602, "Unknown tool.")
            args = params.arguments or {}
            errors = list(
                Draft202012Validator(TOOL_MAP[params.name].input_schema).iter_errors(args)
            )
            if errors:
                raise OperationError("invalid_arguments", errors[0].message)
            engine = ctx.lifespan_context
            if params.name == "twincat_search":
                return result(engine.catalog.search(**args))
            if params.name == "twincat_describe":
                return result(engine.catalog.describe(args["operations"]))
            receipt = await asyncio.to_thread(
                engine.execute, args["operation"], args["arguments"], args.get("requestKey")
            )
            if "jobHandle" in receipt and args["operation"] not in {
                "job.get",
                "job.result",
                "job.cancel",
            }:
                deadline = asyncio.get_running_loop().time() + args.get("waitSeconds", 1)
                while (
                    receipt["state"] in {"queued", "running"}
                    and asyncio.get_running_loop().time() < deadline
                ):
                    await asyncio.sleep(0.05)
                    receipt = engine.jobs.get(receipt["jobHandle"])
            error = (
                receipt.get("state") in {"failed", "outcome_unknown", "cancelled"}
                or receipt.get("success") is False
            )
            return result(receipt, error)
        except OperationError as exc:
            return result(dict(success=False, code=exc.code, message=str(exc)), True)

    return Server(
        "twincat-mcp",
        version=VERSION,
        instructions="Search by task, describe selected IDs, then execute. Prefer workflow.deploy, workflow.test, workflow.sequence and workflow.wait_state for repeated procedures. All submitted operations require requestKey. Retain returned context, grant, recording and job handles explicitly.",
        lifespan=lifespan,
        on_list_tools=list_tools,
        on_call_tool=call_tool,
    )


server = create_server()


async def main():
    async with stdio_server() as (read, write):
        await server.run(read, write, server.create_initialization_options())


if __name__ == "__main__":
    asyncio.run(main())
