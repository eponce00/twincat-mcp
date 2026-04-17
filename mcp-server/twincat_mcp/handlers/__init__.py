"""
Tool handlers package.

Importing this package is the side-effect that populates
`_registry.HANDLERS`. Each submodule registers its tools via the
`@register("twincat_...")` decorator at module scope, so simply importing
them registers every handler.

`server.call_tool()` imports `HANDLERS` and dispatches by name.
"""

from . import ads, batch, deploy, safety, shell, tcunit  # noqa: F401  (side-effect: registration)
from ._registry import HANDLERS, register  # noqa: F401  (re-export)

__all__ = ["HANDLERS", "register"]
