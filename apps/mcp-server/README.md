# Tessera MCP Server

The bridge between **AI assistants** (Claude, ChatGPT, Gemini) and the
**Tessera platform**. It exposes platform capabilities as MCP *tools* to the
assistant, and delegates all authorization to the C# platform — the MCP server
never re-implements auth, tenant context or roles.

## Tools

| Tool | Backed by | What it does |
|------|-----------|--------------|
| `whoami` | `GET /tenant/me` | Acting user's identity, role, allowed actions |
| `list_widgets` | `GET /widgets` | List the tenant's widgets |

## Running

```bash
uv sync                                    # install deps (Python 3.13 via uv)
export TESSERA_BASE_URL=http://localhost:5000   # Docker API, or :5085 for dotnet run
export TESSERA_TENANT_ID=alpha-corp
export TESSERA_EMAIL=admin@tessera.com
export TESSERA_PASSWORD=Admin123!
uv run tessera-mcp                        # starts the stdio MCP server
```

Connect any MCP client (Claude Desktop, MCP Inspector) by pointing it at the
`tessera-mcp` executable.

## Layout

```
src/tessera_mcp/
├── client.py    # TesseraClient — platform auth (JWT + X-Tenant-Id), auto-refresh
├── tools.py     # tool implementations (plain async functions — unit-testable)
├── server.py    # MCP wiring (official MCP Python SDK, low-level API)
└── __main__.py  # CLI entry (stdio transport)
tests/           # pytest — mock-transport unit tests
```

Transport is a runtime choice in the official SDK: currently **stdio** for
local development; switching to **streamable HTTP** (for hosted, remote
assistants) is a small change in `__main__.py`.

## Checks

```bash
uv run ruff check .
uv run mypy src
uv run pytest
```
