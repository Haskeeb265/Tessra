# Live Testing Guide — Operating the Acme Dental SMB from an AI Assistant

> How to wire the Tessera MCP gateway to a real AI assistant and test it live.
> Companion docs: `docs/mcp/README.md` (architecture), `progress.md` (build
> status). Current state: **gateway live-verified through a public HTTPS tunnel
> over the real 2026-07-28 MCP protocol, with OAuth mode confirmed (401 + PRM
> metadata served)** — tested 2026-09-10 through cloudflared. This guide takes
> you the rest of the way, from a raw curl to Claude web.

---

## 0. The testing ladder (cheapest → target)

Do these **in order**. Each rung catches a class of bug the next one can't:

| Rung | Client | Auth | Needs internet/tunnel? | Validates |
|---|---|---|---|---|
| 1 | `curl` | none | No | Protocol shape, tool execution |
| 2 | MCP Inspector | OAuth (auto) | No | Discovery + PKCE flow, real protocol client |
| 3 | Cursor / VS Code | OAuth (auto) | No | First AI actually calling tools |
| 4 | **Claude web** | OAuth (auto) | **Yes — public HTTPS** | The real product journey |

The local rungs (1–3) work because those clients run on **your** machine and
can reach `localhost` directly. Claude web runs on Anthropic's servers, so it
can only reach a **public HTTPS URL** — that's the only reason for the tunnel.

---

## 1. Boot the stack (rung 0)

Three processes, three terminals (or background):

```bash
# 1) C# platform — port 5010, seeds the 3 Acme Dental manifests on startup
cd apps/platform/src/Tessera.Platform.Api
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://127.0.0.1:5010 \
  dotnet run --no-launch-profile

# 2) Local Acme Dental stub backend (the "SMB server" being operated)
cd apps/mcp-server
uv run uvicorn stub_backend.app:app --host 127.0.0.1 --port 9100

# 3) MCP gateway
cd apps/mcp-server
PLATFORM_API_URL=http://127.0.0.1:5010 \
GATEWAY_API_KEY=dev-gateway-key \
MCP_AUTH_MODE=none \
  uv run uvicorn tessera_mcp.main:app --host 127.0.0.1 --port 8000
```

Smoke-check each:

```bash
curl -s http://127.0.0.1:5010/health   # platform
curl -s http://127.0.0.1:9100/health   # stub backend
curl -s http://127.0.0.1:8000/health   # gateway
```

> **Gotchas (bit us already):**
> - `dotnet run` **ignores `ASPNETCORE_URLS`** unless you pass
>   `--no-launch-profile` (launchSettings wins). Port 5000 is often taken by
>   WSL/Docker relays — hence 5010.
> - The gateway **defaults to `MCP_AUTH_MODE=oauth`**. Unauthenticated calls
>   correctly get a 401 RFC 9728 challenge. Use `none` only for local dev.

### The MCP call shape (what every client below does under the hood)

The 2026-07-28 stateless protocol requires the `params._meta` envelope and
`Mcp-Method` / `Mcp-Name` headers:

```bash
curl -s -X POST http://127.0.0.1:8000/t/acme-dental/mcp \
  -H "MCP-Protocol-Version: 2026-07-28" \
  -H "Mcp-Method: tools/call" -H "Mcp-Name: book_appointment" \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{
        "_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28",
                 "io.modelcontextprotocol/clientCapabilities":{}},
        "name":"book_appointment",
        "arguments":{"patient_name":"Jane Doe","date":"2026-10-05",
                     "time":"14:30","service_type":"cleaning"}}}'
```

Expected: `"isError":false` and `structuredContent` containing the booked
appointment. This exact call was verified live on 2026-09-09.

---

## 2. MCP Inspector — the protocol harness (rung 2)

Anthropic's official dev tool. It behaves like a real MCP client: follows the
401 → RFC 9728 PRM → authorization-server discovery → PKCE flow by itself,
and gives you buttons for `tools/list` and `tools/call`.

```bash
npx @modelcontextprotocol/inspector
```

In the UI:
1. Transport: **Streamable HTTP**
2. URL: `http://127.0.0.1:8000/t/acme-dental/mcp`
3. **Connect** — it will discover the OAuth config and open a browser for the
   consent flow. Log in as the Acme Dental admin (`admin@tessera.com` /
   `Admin123!` — PostgreSQL runs only via the dockerized stack, see
   `docs/mcp/README.md` §13; for the in-memory platform see below).
4. **List Tools** → you should see `book_appointment`, `cancel_appointment`,
   `list_appointments`.
5. **Call a tool** → book an appointment; then list; then cancel.

### OAuth mode for local clients (no docker needed)

For rungs 2–3 with real OAuth, run the platform and gateway so the issuer and
resource URLs line up on `tessera.local` (see `docs/mcp/README.md` §13 for the
Caddy TLS stack):

```bash
# hosts file already has: 127.0.0.1 tessera.local
# dockerized platform + Caddy (AS behind TLS at https://tessera.local):
cd apps/platform && docker compose up -d --build api db caddy
# run the portal for login/consent pages:
cd apps/web && npm run dev
# gateway pointed at the dockerized platform, TLS-proxying Caddy:
PLATFORM_API_URL=http://localhost:5000 \
GATEWAY_API_KEY=dev-gateway-key \
MCP_AUTH_MODE=oauth \
MCP_BASE_URL=https://tessera.local \
ISSUER_URL=https://tessera.local \
  uv run uvicorn tessera_mcp.main:app --host 127.0.0.1 --port 8000
```

Then point the Inspector at `https://tessera.local/t/acme-dental/mcp` (or the tunnel URL for a full through-tunnel test)
(accept the dev TLS cert; for scripted clients use `NODE_TLS_REJECT_UNAUTHORIZED=0`
or export Caddy's root CA). The Inspector will do the full PKCE dance against
the real C# AS — this is the rung that proves the entire OAuth stack.

---

## 3. Cursor / VS Code — the first AI operator (rung 3)

These are free, run locally (reach `localhost`), and implement remote MCP +
OAuth exactly like Claude web does.

**Cursor:** Settings → MCP & Integrations → Add Custom MCP → paste:

```json
{
  "mcpServers": {
    "acme-dental": {
      "url": "http://127.0.0.1:8000/t/acme-dental/mcp"
    }
  }
}
```

**VS Code (Copilot agent mode):** add the same shape to `.vscode/mcp.json`
via `MCP: Add Server` → `HTTP` → paste the URL.

Then just **chat**: *"Book a cleaning for Jane Doe on October 5th at 14:30,
then show her appointments."* The AI should discover the tools, call
`book_appointment`, then `list_appointments`, and the stub's data will change.

What to watch for when something fails:
- `401 ... resource_metadata=` — the client didn't follow the OAuth
  challenge (Inspector/Cursor/VS Code all handle it; curl doesn't).
- Tools missing from `tools/list` — the manifest cache; manifests refresh on
  TTL (`MANIFEST_TTL_SECONDS`, default 60) or gateway restart.
- `ExecutionError: Missing required argument` — the AI sent bad arguments;
  check `inputSchema` in the manifest, not the gateway.

---

## 4. Claude web — the real journey (rung 4)

### 4.1 Why a tunnel

Claude.ai runs on Anthropic's servers. It cannot reach `127.0.0.1` or
`tessera.local` (unless your machine is publicly reachable). The gateway must
sit behind a **public HTTPS URL** with a valid certificate. Anything that
terminates TLS and forwards to `:8000` works.

> **One gotcha that bit us:** the MCP Python SDK (v2.2.0) auto-enables DNS
> rebinding protection when the server host is `127.0.0.1` / `localhost` /
> `::1`, and it rejects any request whose `Host` header doesn't match the
> allowed list with a `421 Invalid Host header`. A free cloudflared/ngrok
> tunnel sends the tunnel's hostname as the `Host` header, so the SDK's own
> middleware was returning 421 **before** our code ever saw the request —
> which is why `/health` (our own route) worked but `/t/acme-dental/mcp`
> (routed by the SDK) failed through the tunnel.
>
> **Fix (already applied):** each tenant server is now created with
> `transport_security=TransportSecuritySettings(
>     enable_dns_rebinding_protection=False)` so any Host header is accepted.
> The actual tenant/resource binding is still enforced by the TokenVerifier —
> this only disables the Host-header check. No code changes needed on your end.

### 4.2 Expose the gateway (pick one)

**Option A — cloudflared (quick, free, no signup for temp URLs):**

```bash
cloudflared tunnel --url http://127.0.0.1:8000
# → prints https://<random>.trycloudflare.com
```

**Option B — ngrok:**

```bash
ngrok http 8000
# → prints https://<random>.ngrok-free.app
```

**Option C — a VPS / any PaaS** (Fly.io, Render, Railway): deploy
`apps/mcp-server` (it's a standard uvicorn app; a Dockerfile is a 10-line
`uv` image) and set the env vars. This is the production shape.

### 4.3 Point the gateway at the public URL

The gateway's issuer/resource URLs must match what Claude sees, so restart it
with the public base URL:

```bash
PLATFORM_API_URL=http://127.0.0.1:5010 \
GATEWAY_API_KEY=dev-gateway-key \
MCP_AUTH_MODE=oauth \
MCP_BASE_URL=https://<your-tunnel-domain> \
ISSUER_URL=https://tessera.local \
  uv run uvicorn tessera_mcp.main:app --host 127.0.0.1 --port 8000
```

> **Important:** `ISSUER_URL` must be reachable **by your browser** during the
> consent popup (Claude web's OAuth flow opens the consent page in *your*
> browser). For a quick test you can keep the AS on `tessera.local` — your
> browser resolves that host via your hosts file (`127.0.0.1 tessera.local`).
> For a durable setup, put the AS behind public TLS too (same tunnel provider;
> `apps/platform/caddy/Caddyfile` already routes it).

### 4.4 Connect in Claude web

1. Go to **claude.ai → Settings → Connectors → Add custom connector**
   (browse to `claude.ai/settings/connectors`).
2. Paste the remote MCP URL:
   `https://<your-tunnel-domain>/t/acme-dental/mcp`
3. Claude fetches the RFC 9728 protected-resource metadata, discovers the C#
   AS, and **opens a consent popup**. Log in with the tenant admin
   (`admin@tessera.com` / `Admin123!`) and approve.
4. Claude stores the token (+ refresh token — the AS issues
   `offline_access`; refresh rotation is enforced).
5. In any chat, click the **tools/connector icon** and pick Acme Dental —
   Claude lists the tools and can call them.

**Test prompts:**
- *"Book a whitening appointment for Sam Taylor on 2026-12-01 at 10:00."*
- *"Show all upcoming appointments for Sam Taylor."*
- *"Cancel that appointment."*
- *"What tools do you have from Acme Dental?"* → should list the 3.

### 4.5 Claude-specific notes (verified against Anthropic docs, §8.6)

- Auth support: `oauth_cimd` ✅ (our `ClientIdMetadataService` exists for
  exactly this), `oauth_dcr`, `none`; PKCE S256 always.
- Claude web's `client_id` is an HTTPS URL (`https://claude.ai/...`) — the CIMD
  handler fetches and validates its metadata document on first authorize.
- Egress ranges start at `160.79.104.0/21` — allowlist if you firewall.
- If the connector errors after working, the tunnel URL changed (free tunnels
  rotate) — reconnect with the new URL.

---

## 5. Troubleshooting table

| Symptom | Cause | Fix |
|---|---|---|
| Gateway `503`/`ManifestSourceError` | Platform down or wrong `PLATFORM_API_URL` | Check `curl :5010/health`; restart gateway |
| `401` + `WWW-Authenticate: Bearer ... resource_metadata=` | OAuth mode, no/invalid token | Expected for curl; use Inspector/Cursor, or `MCP_AUTH_MODE=none` for dev |
| `403 insufficient_scope` | Token lacks the `tools` scope | Re-authenticate; check AS scope config |
| `404` on `/t/{tenant}/mcp` | Unknown/suspended tenant | Tenant must exist in the platform (`acme-dental` is seeded) |
| `400 mcp-method header does not match` | Client sends legacy/incorrect routing headers | Client must be 2026-07-28-capable (Inspector/Cursor are) |
| `ExecutionError: ... 401` from the *stub* | `CREDENTIALS` env doesn't match `vault://` ref | Gateway env: `CREDENTIALS={"acme-dental/booking-api-key":"dev-acme-api-key-123"}` |
| Booking succeeds but data resets | Stub is in-memory (process-local) | Expected; restart clears. Production SMBs have real backends |
| Claude can't connect at all | Tunnel down / URL rotated / cert invalid | Reopen tunnel, re-add connector; check TLS is valid (not self-signed) |
| Consent popup fails | `ISSUER_URL` unreachable from your browser or Claude | Both need to resolve the issuer; put AS behind public TLS for durable setups |

---

## 6. What "pass" looks like

- [ ] **Rung 1 (curl, local):** `tools/list` returns the 3 seeded tools;
      `book_appointment` returns `isError:false` with a mapped appointment
      object. Verified 2026-09-09.
- [ ] **Rung 1b (curl, through tunnel):** same two calls against the tunnel URL
      return the same results. Verified 2026-09-10 — `tools/list` returns the
      3 tools and a live `book_appointment` returned a confirmed booking
      (`isError:false`, `structuredContent` with the appointment). Also
      confirmed: `/.well-known/oauth-protected-resource/t/acme-dental/mcp`
      serves correct RFC 9728 metadata, and an unauthenticated `tools/list` in
      OAuth mode returns `{"error":"invalid_token",...}`. The tunnel Host
      header is accepted (SDK DNS-rebinding protection disabled).
- [ ] **Rung 2 (MCP Inspector):** Inspector completes the OAuth popup and calls
      tools with a real AS-issued token (aud = `.../t/acme-dental/mcp`).
- [ ] **Rung 3 (Cursor / VS Code):** the AI **spontaneously** picks the right
      tool from a natural-language request and the stub's state changes.
- [ ] **Rung 4 (Claude web):** connector shows "connected", lists 3 tools, and
      books/cancels appointments from chat — the journey from
      `USER_JOURNEYS.md` is live end-to-end.

When all four pass, the plug-and-play story is proven: **manifest in the C#
platform → tools in any MCP-capable AI assistant, no gateway code changes.**
