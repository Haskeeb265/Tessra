# Tessera MCP Gateway (`apps/mcp-server`)

The **thin protocol adapter** between AI assistants (Claude, ChatGPT, MCP
Inspector, Cursor, …) and the SMB tools configured in the Tessera platform.

It speaks MCP over Streamable HTTP, resolves each tenant's tool catalog from the
C# platform, and executes tool calls against the SMB's own backend. It holds
**no business logic and no database access** — the C# platform
(`apps/platform`) is the system of record. See
[`docs/mcp/README.md`](../../docs/mcp/README.md) for the product model and
[`docs/FLOW.md`](../../docs/FLOW.md) for the end-to-end diagrams.

---

## What it does

| Route | Purpose |
|---|---|
| `POST /t/{tenant}/mcp` | The MCP endpoint for one tenant (stateless Streamable HTTP, 2026-07-28 spec; also serves 2025-era clients). |
| `GET /.well-known/oauth-protected-resource/t/{tenant}/mcp` | RFC 9728 protected-resource metadata (points clients at the C# authorization server). |
| `GET /health` | Liveness. |

Per request it:

1. resolves the tenant from the URL,
2. validates the bearer token (when `MCP_AUTH_MODE=oauth`) — RS256 signature via
   the platform's JWKS, `iss`, `exp`, and `aud` **bound to that tenant's MCP URL**
   (RFC 8707), so a token for one tenant can never be replayed against another,
3. serves `tools/list` from the tenant's manifests (TTL-cached),
4. serves `tools/call` through the HTTP executor — `${arg}` templating into
   path/body/query, credential injection, JSONPath response mapping.

## Layout

```
apps/mcp-server/
├── pyproject.toml / uv.lock      # uv project (Python 3.13)
├── Dockerfile                    # gateway + stub backend image (compose reuses it)
├── src/tessera_mcp/
│   ├── config.py                 # env-driven Settings (all fields have dev defaults)
│   ├── main.py                   # uvicorn entrypoint (tessera_mcp.main:app)
│   ├── core/gateway.py           # per-tenant low-level Server + raw-ASGI dispatch, LRU cache
│   ├── manifest/
│   │   ├── models.py             # pydantic mirror of the C# manifest contract
│   │   └── loader.py             # TTL cache over GET /internal/gateway/manifests
│   ├── executors/http_executor.py# templated HTTP execution + response mapping
│   ├── auth/token_verifier.py    # JWKS fetch/cache, RS256, iss/aud/exp/scope
│   └── utils/jsonpath.py         # minimal JSONPath subset
├── stub_backend/app.py           # local Acme Dental SMB (book/list/cancel + reset)
└── tests/                        # pytest (39 tests)
```

## Run it

```bash
# Platform first (system of record) — see apps/platform/Guide.md
cd apps/platform && dotnet run --project src/Tessera.Platform.Api --no-launch-profile

# Local Acme Dental backend stub (the "SMB server" being operated)
cd apps/mcp-server
uv run uvicorn stub_backend.app:app --host 127.0.0.1 --port 9100

# Gateway (authless, for local protocol testing)
PLATFORM_API_URL=http://127.0.0.1:5085 GATEWAY_API_KEY=dev-gateway-key \
MCP_AUTH_MODE=none \
  uv run uvicorn tessera_mcp.main:app --host 127.0.0.1 --port 8000
```

`MCP_AUTH_MODE` defaults to **`oauth`**, so an unauthenticated call correctly
gets a 401 + RFC 9728 challenge. Use `none` only for loopback development.

For the full OAuth stack (Caddy TLS + React portal + Claude web over a public
tunnel) use the driver script instead — it starts everything and verifies the
OAuth surface before printing the connector URL:

```bash
./scripts/start-claude-web.sh          # from the repo root
./scripts/start-claude-web.sh --stop
```

## Configuration

| Env var | Default | Purpose |
|---|---|---|
| `PLATFORM_API_URL` | `http://localhost:5000` | C# platform base URL (manifest reads). |
| `GATEWAY_API_KEY` | `dev-gateway-key` | Sent as `X-Gateway-Api-Key` to `/internal/gateway/manifests`. |
| `MCP_BASE_URL` | `http://localhost:8000` | Public base URL; per-tenant resource = `{base}/t/{slug}/mcp`. **Must match the URL clients connect to**, because tokens are audience-bound to it. |
| `ISSUER_URL` | `https://tessera.local` | Expected token `iss` and where the JWKS lives. |
| `MCP_AUTH_MODE` | `oauth` | `oauth` \| `none` (authless loopback dev only). |
| `JWKS_URL` | `{ISSUER_URL}/.well-known/jwks` | Override so a container can fetch the API's JWKS internally over http. |
| `MANIFEST_TTL_SECONDS` | `60` | Manifest cache TTL (a platform outage degrades to cached reads). |
| `SMB_HOST_OVERRIDES` | `{"api.acmedental.test":"http://127.0.0.1:9100"}` | JSON map rewriting manifest target hosts to local backends. |
| `CREDENTIALS` | `{"acme-dental/booking-api-key":"dev-acme-api-key-123"}` | JSON map resolving `vault://tenant/ref` → secret (dev only; production resolves from the platform's encrypted store). |
| `EXECUTOR_TIMEOUT_SECONDS` | `30` | Per-call HTTP timeout. |
| `MAX_TENANT_SERVERS` | `100` | LRU cap on cached per-tenant MCP servers. |

## Test it

```bash
cd apps/mcp-server
uv run pytest -q          # 39 tests
```

Covers the manifest loader (incl. platform-error handling), the JSONPath
subset, the HTTP executor (templating, credentials, mapping), token
verification (401 challenge → PRM → valid-token 200; wrong `aud` → 401;
missing scope → 403), the stub backend, and the gateway end-to-end.

Manual smoke test and the full assistant ladder (curl → Inspector → Cursor →
Claude web) are in [`docs/LIVE_TESTING_GUIDE.md`](../../docs/LIVE_TESTING_GUIDE.md).
