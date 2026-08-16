"""MCP server wiring — exposes Tessera tools to AI assistants.

Uses the official Model Context Protocol Python SDK (``mcp`` 2.x, low-level
``mcp.server.Server`` — not FastMCP). The server registers two tools and
delegates each call to the plain functions in ``tools.py``.

Transport note: this entry runs over **stdio** for local development. Because
the SDK takes the transport as a runtime choice, switching to **streamable
HTTP** later (so ChatGPT/Gemini/remote Claude can connect to a hosted URL) is
a small change confined to ``__main__.py`` — the tool definitions here are
transport-agnostic.
"""

from __future__ import annotations

from typing import Any

from mcp.server import Server, ServerRequestContext
from mcp.types import (
    CallToolRequestParams,
    CallToolResult,
    ListToolsResult,
    PaginatedRequestParams,
    TextContent,
    Tool,
)

from tessera_mcp.client import TesseraClient, TesseraError
from tessera_mcp.tools import list_widgets, whoami

_EMPTY_OBJECT_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {},
    "additionalProperties": False,
}


def build_server(client: TesseraClient) -> Server[Any]:
    """Build the MCP server bound to an authenticated platform client."""

    async def on_list_tools(
        ctx: ServerRequestContext[Any],
        params: PaginatedRequestParams | None,
    ) -> ListToolsResult:
        del ctx, params  # no pagination or context needed yet
        return ListToolsResult(
            tools=[
                Tool(
                    name="whoami",
                    description=(
                        "Return the acting user's identity, role and allowed actions "
                        "from the Tessera platform (GET /tenant/me)."
                    ),
                    input_schema=_EMPTY_OBJECT_SCHEMA,
                ),
                Tool(
                    name="list_widgets",
                    description=(
                        "List the current tenant's widgets from the Tessera platform "
                        "(GET /widgets)."
                    ),
                    input_schema=_EMPTY_OBJECT_SCHEMA,
                ),
            ]
        )

    async def on_call_tool(
        ctx: ServerRequestContext[Any],
        params: CallToolRequestParams,
    ) -> CallToolResult:
        del ctx

        # Failures (unknown tool, bad args, platform rejects the acting user)
        # are returned as *structured errors* rather than raised: an AI
        # assistant should receive a readable message, and a platform 403
        # (action enforcement) must not kill the request.
        try:
            result = await _dispatch(client, params.name, params.arguments or {})
        except (ValueError, TesseraError) as exc:
            return CallToolResult(
                content=[TextContent(type="text", text=str(exc))],
                is_error=True,
            )

        return CallToolResult(content=[TextContent(type="text", text=result)])

    return Server(
        name="tessera-mcp",
        version="0.1.0",
        on_list_tools=on_list_tools,
        on_call_tool=on_call_tool,
    )


async def _dispatch(client: TesseraClient, name: str, arguments: dict[str, Any]) -> str:
    """Route a tool call to its implementation, validating arguments."""
    if arguments:
        raise ValueError(f"Tool '{name}' takes no arguments, got: {arguments!r}")

    if name == "whoami":
        return await whoami(client)
    if name == "list_widgets":
        return await list_widgets(client)
    raise ValueError(f"Unknown tool: {name}")
