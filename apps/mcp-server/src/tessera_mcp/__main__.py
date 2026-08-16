"""Entry point: ``uv run tessera-mcp`` (or ``python -m tessera_mcp``).

Builds a :class:`TesseraClient` from environment variables, logs into the
platform, and runs the MCP server over stdio. MCP clients (Claude Desktop,
MCP Inspector, ``mcp.client.stdio``) launch this as a subprocess.
"""

from __future__ import annotations

import asyncio

from mcp.server.stdio import stdio_server

from tessera_mcp.client import TesseraClient
from tessera_mcp.server import build_server


async def _run() -> None:
    client = TesseraClient.from_env()
    try:
        await client.login()
        server = build_server(client)
        async with stdio_server() as (read_stream, write_stream):
            await server.run(read_stream, write_stream, server.create_initialization_options())
    finally:
        await client.aclose()


def main() -> None:
    asyncio.run(_run())


if __name__ == "__main__":
    main()
