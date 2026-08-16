"""Tool implementations — the capabilities the MCP server exposes to AI assistants.

Each tool is a plain ``async`` function taking a :class:`TesseraClient`. Keeping
them separate from the MCP wiring (``server.py``) means they are trivially
unit-testable without standing up the MCP protocol.

Every tool enforces nothing itself — the C# platform is the source of truth
for authorization. If the acting user's role lacks a permission, the platform
rejects the call and the error surfaces as a ``TesseraError``.
"""

from __future__ import annotations

import json
from typing import Any

from tessera_mcp.client import TesseraClient


async def whoami(client: TesseraClient) -> str:
    """Return the acting user's identity, role and allowed actions.

    Maps to ``GET /tenant/me``. Useful for an assistant to learn *who* it is
    acting as and *what* that role is permitted to do.
    """
    me = await client.whoami()
    return _pretty(me)


async def list_widgets(client: TesseraClient) -> str:
    """List the tenant's widgets (the platform's placeholder resource).

    Maps to ``GET /widgets``. Demonstrates a full agent → MCP → platform round
    trip: tenant-scoped data flows back through the bridge.
    """
    widgets = await client.list_widgets()
    return _pretty(widgets)


def _pretty(data: Any) -> str:
    """Stable, readable JSON for MCP tool results (sorted keys, no weird spacing)."""
    return json.dumps(data, indent=2, sort_keys=True, default=str)
