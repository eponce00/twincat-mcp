"""
Handler registry for tool dispatch.

Each tool is implemented as an async function registered with the
`@register("twincat_<name>")` decorator. `server.call_tool()` looks up
the name in `HANDLERS` and invokes the matching function.

Handler signature:

    async def handle_xxx(arguments: dict, tool_start_time: float) -> list[TextContent]

`tool_start_time` is `time.time()` captured right after the armed-mode
and confirmation gates pass; pass it straight to `add_timing_to_output`
at return time.
"""

from typing import Awaitable, Callable

from mcp.types import TextContent

Handler = Callable[[dict, float], Awaitable[list[TextContent]]]

# Global registry. Populated at import time by the side effects of
# importing every module in `twincat_mcp.handlers.*`.
HANDLERS: dict[str, Handler] = {}


def register(tool_name: str):
    """Decorator: register a handler under its MCP tool name."""

    def decorator(fn: Handler) -> Handler:
        if tool_name in HANDLERS:
            raise RuntimeError(f"duplicate handler registration: {tool_name}")
        HANDLERS[tool_name] = fn
        return fn

    return decorator
