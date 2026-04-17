"""
Output-formatting helpers used by every tool handler.

Extracted verbatim from server.py so the handlers can keep their exact
output shape. Nothing here depends on any other twincat_mcp module.
"""

import time


def format_duration(seconds: float) -> str:
    """Format duration in human-readable format."""
    if seconds < 60:
        return f"{seconds:.1f}s"
    elif seconds < 3600:
        mins = int(seconds // 60)
        secs = seconds % 60
        return f"{mins}m {secs:.1f}s"
    else:
        hours = int(seconds // 3600)
        mins = int((seconds % 3600) // 60)
        return f"{hours}h {mins}m"


def add_timing_to_output(output: str, start_time: float) -> str:
    """Append execution timing to a tool's output block."""
    elapsed = time.time() - start_time
    return f"{output}\n\n⏱️ Execution time: {format_duration(elapsed)}"
