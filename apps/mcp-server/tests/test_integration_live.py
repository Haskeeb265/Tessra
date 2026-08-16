"""Live integration test — the full agent → MCP → platform flow.

Spawns the real MCP server as a subprocess (stdio transport) with a real
TesseraClient, connects over the MCP protocol, and calls its tools against a
running platform instance.

Auto-skips when no platform is reachable. Point ``TESSERA_BASE_URL`` at the
running API (default: Docker API on :5000). Credentials come from env or the
seeded defaults (``admin@tessera.com`` / ``Admin123!`` in tenant
``alpha-corp`` — seeded on PostgreSQL/Docker, see ARCHITECTURE.md §10.3).
"""

from __future__ import annotations

import os
from collections.abc import AsyncIterator

import httpx
import pytest
from mcp import ClientSession
from mcp.client.stdio import StdioServerParameters, stdio_client

BASE_URL = os.getenv("TESSERA_BASE_URL", "http://localhost:5000")
TENANT_ID = os.getenv("TESSERA_TENANT_ID", "alpha-corp")
EMAIL = os.getenv("TESSERA_EMAIL", "admin@tessera.com")
PASSWORD = os.getenv("TESSERA_PASSWORD", "Admin123!")

def _platform_reachable() -> bool:
    try:
        response = httpx.get(f"{BASE_URL}/health", timeout=3.0)
        return response.status_code == 200
    except httpx.HTTPError:
        return False


pytestmark = [
    pytest.mark.integration,
    pytest.mark.skipif(
        not _platform_reachable(),
        reason=f"platform not reachable at {BASE_URL} — start Docker or dotnet run first",
    ),
]


@pytest.fixture
async def session() -> AsyncIterator[ClientSession]:
    params = StdioServerParameters(
        command="uv",
        args=["run", "tessera-mcp"],
        env={
            **os.environ,
            "TESSERA_BASE_URL": BASE_URL,
            "TESSERA_TENANT_ID": TENANT_ID,
            "TESSERA_EMAIL": EMAIL,
            "TESSERA_PASSWORD": PASSWORD,
        },
    )
    async with (
        stdio_client(params) as (read_stream, write_stream),
        ClientSession(read_stream, write_stream) as session,
    ):
        await session.initialize()
        yield session


async def test_lists_two_tools(session: ClientSession) -> None:
    tools = await session.list_tools()
    names = [tool.name for tool in tools.tools]
    assert names == ["whoami", "list_widgets"]


async def test_whoami_tool_returns_profile(session: ClientSession) -> None:
    result = await session.call_tool("whoami", {})
    text = "".join(part.text for part in result.content if hasattr(part, "text"))
    assert '"role"' in text
    assert '"tenantIdentifier"' in text


async def test_list_widgets_tool_returns_list(session: ClientSession) -> None:
    result = await session.call_tool("list_widgets", {})
    text = "".join(part.text for part in result.content if hasattr(part, "text"))
    assert text.startswith("[")


async def test_unknown_tool_returns_error(session: ClientSession) -> None:
    result = await session.call_tool("nope", {})
    assert result.is_error
