# Tessera MCP — Product Model, Architecture & Engineering Reference

> **Status:** working design + engineering reference — the authoritative record of what we've decided, what's built, what's still open, and what's deferred. Update it whenever a decision lands.
>
> **Related:** `docs/README.md` (overall system — index), `docs/platform/README.md` (C# platform service — auth, data, middleware, endpoints, MCP OAuth AS), `docs/web/README.md` (portals + OAuth login/consent pages), `docs/TABLES.md` (database schema reference), `docs/sample-smb/*.json` (Acme Dental test fixture), `docs/CONCERNS.md` (gaps + decisions), `docs/FLOW.md` (end-to-end diagrams).
>
> The original standalone MCP R&D/spec artifacts (`docs/mcp.md`, `docs/mcp-rnd-2026-09.md`, `docs/mcp-auth-platform.md`, `docs/mcp-auth-platform-local-run.md`, `docs/sample_smb.md`) were consolidated into this doc, `docs/platform/README.md`, and `docs/sample-smb/` on 2026-09-07 — any remaining references below to them mean this doc.

---

## 0. TL;DR

**The product:** SMBs get a "plug and play" integration layer — they plug their existing tools/services into Tessera, and *their* customers use those services through AI assistants (ChatGPT, Claude, …) via a hosted MCP server.

**The core idea:** SMBs never write MCP server code. They configure a **tool manifest** (a JSON document describing their tools + how to execute them); one generic MCP server reads that manifest per-tenant and exposes the tools to AI assistants.

**Stack (locked):** C# modular monolith = system of record (data, authz, manifests, auth, OAuth AS). Python = thin MCP gateway (protocol adapter only, no business logic, no direct DB access). Next.js = admin dashboard + end-user login/consent pages.

**V1 scope (locked):** manifest CRUD + generic gateway with HTTP executor + OAuth login flow + minimal end-user accounts + manifest wizard UI. Everything else is backlogged (see §5).

---

## 1. Product model & the three actors

Every interaction has three actors — keeping them straight resolves most design confusion:

| Actor | What they are | Their auth |
|---|---|---|
| **Jane** | The end user; the SMB's customer (e.g. a patient) | Authenticates against the **SMB's identity** (delegation) — see §3.3 |
| **ChatGPT / Claude** | The AI assistant = OAuth **client** | Registered as a client; holds tokens *on Jane's behalf* |
| **Acme Dental** (the SMB) | The service provider; owns the tools | Admin-managed via the dashboard |

The AI assistant's own login is irrelevant to the SMB. What matters is **who Jane is to the SMB** — that's the customer-identity problem (§3.3), the product's moat.

---

## 2. Locked decisions (do this way)

### 2.1 Manifest-driven server (the central decision)

- One generic MCP server serves **every tenant**. `list_tools` / `call_tool` are pure lookups against the tenant's manifest. The server never hardcodes a tenant's tools.
- The **manifest is the contract, not code.** Adding a tool for an SMB = adding a manifest entry, not deploying code.

Manifest format (shared contract across all integration tiers — illustrative; the real shared schema lives in the eventual `packages/contracts` artifact):

```json
{
  "tenant_id": "acme-dental",
  "tool_name": "book_appointment",
  "description": "Books a dental appointment for a given date/time and service type.",
  "input_schema": { "...": "JSON Schema" },
  "execution": {
    "type": "http",
    "method": "POST",
    "url": "https://api.acmedental.com/v1/appointments",
    "auth": { "type": "api_key", "credential_ref": "vault://acme-dental/booking-api-key" },
    "body_template": { "...": "..." },
    "response_mapping": "$.data.appointment"
  },
  "required_scopes": ["appointments:write"],
  "rate_limit_override": null
}
```

### 2.2 Tiered integration (how SMBs plug in)

All tiers converge on the same manifest format; the server never knows which tier created it.

| Tier | What the SMB does | Effort | Who it's for |
|---|---|---|---|
| **1. Pre-built connector** | Picks "Shopify" from a list, OAuth-connects it | Zero code | Least technical |
| **2. Wizard / OpenAPI import** | Fills a form (name, URL, params, auth) **or** uploads an OpenAPI spec → auto-generated draft manifest | Low code | Has a backend (maybe a web vendor does it) |
| **3. Custom function (SDK)** | Writes a small Python `@tessera.tool` function, deployed as tenant-scoped executor | Code, opt-in | Has a developer; multi-step logic an HTTP call can't express |

**Sequencing (locked):** wizard + manual manifest editing **first** (it's a CRUD app) → pre-built connectors **second** (demand-driven, per vertical) → OpenAPI import + SDK **later**.

**Wedge vertical (decided):** there is **no single wedge vertical**. The generic tool-manifest schema *is* the vertical-agnostic contract; each vertical (dental, salons, legal, …) is just a **vertical-specific manifest** built on that schema. Verticals are data, not decisions — a new vertical never requires code, only a new manifest. Connectors are therefore demand-driven, not gated on a chosen wedge.

### 2.3 Stack & the Python ↔ C# boundary

- **C# = system of record.** All business logic, data, authz, manifests, credentials, usage accounting live here.
- **Python gateway = protocol adapter.** Speaks MCP to AI assistants; **must never** touch the database or hold business logic. All reads go through the C# API (manifest fetch, token validation, authz check, usage events), with Redis as a manifest cache so a Platform hiccup doesn't kill every tenant's MCP calls.
- This boundary is what keeps Python thin and swappable.

### 2.4 Tenant addressing

- **Path-based for v1:** `https://mcp.tessera.io/t/{tenant-slug}/mcp` — one DNS record, one cert, no infra work per tenant.
- Subdomain per tenant (`acme.mcp.tessera.io`) deferred until white-labeling is a real requirement.

Local/dev variant: `https://tessera.local/t/{tenant-slug}/mcp` behind the Caddy TLS proxy (see local run).

### 2.5 The manifest in the existing platform

- `ToolManifests` is a tenant-scoped `IsMultiTenant()` entity — same pattern as `Widgets` / `TenantRoles`.
- Managing manifests slots into the existing **action-based authz** (`ActionChecks`) as a new action, `manage_tools`. SMB superadmin manages the tenant's tools; platform superadmin governs manifests/credentials.
- Reuses: Finbuckle isolation, `TenantClaimValidationMiddleware`, rate limiting, JWT infra.

### 2.6 OAuth: OpenIddict first (built — 2026-09)

- Use **OpenIddict** (open-source .NET AS library) inside the C# host. Free, C#, composes with the existing Users table and JWT signing.
- **Decided (2026-08-18): build it ourselves.** OpenIddict it is — no hosted-AS fallback. The spike stayed as scope validation, and we built the AS core.
- Login/consent **pages live in the Next.js app** (per-tenant branding, unified with the admin dashboard) — built.
- The existing first-party auth stays as-is; OpenIddict composes with it (see §3.6 "why the current auth isn't enough").

**What's built (2026-09-07):** OpenIddict 7.7 on `/connect/authorize` + `/connect/token`; JSON `/connect/login` (+ `/login/mfa` reusing `TotpService`); `GET /connect/consent-info` + `POST /connect/consent`; deny via `?deny=1`; `POST /connect/logout`; discovery at `/.well-known/openid-configuration`; PKCE S256; `iss`; `tools` + `offline_access` scopes; **signed RS256 JWT access tokens** (JWS, `at+jwt`, kid `mcp-signing-v1`, published at `/.well-known/jwks`) — the Python gateway validates them via JWKS signature verification + audience + expiry + issuer + scopes checks; encrypted JWT refresh tokens with strict single-use rotation; separate non-tenant `OpenIddictDbContext` + migrations; pre-registered dev client `tessera-local-dev` (loopback `http://127.0.0.1:9876/callback`); tenant resolution from the RFC 8707 `resource` param; suspension + email-verification + `token_version` checks; middleware exclusions extended; **46/46 tests pass** including a full auth-code + PKCE integration test. Added in the 2026-09-09 → 09-12 sessions: CIMD client registration (`ClientIdMetadataService` + `CimdAuthorizationRequestHandler`), the AS discovery amendment that advertises `client_id_metadata_document_supported: true` and `none` in `token_endpoint_auth_methods_supported` (OpenIddict 7.7 emits neither), the relative-endpoint-URI + trusted-forwarded-headers fix that made the flow work behind Caddy/tunnels, and the gateway-facing manifest endpoint.

**Access token format note (why this matters for the gateway):** The AS issues signed-only (not encrypted) JWT access tokens by calling `options.DisableAccessTokenEncryption()` in `Program.cs` while still keeping an encryption key for refresh tokens. The Python gateway validates these tokens via `/.well-known/jwks` — it needs only the public signing key, not the C# AES encryption key. This is the textbook remote-resource-server design and unblocks the gateway without sharing secrets across the service boundary.

### 2.7 Credential vault

- v1: **encrypted-at-rest DB columns** — AES-GCM with an env-var master key + a rotation plan. Credentials decrypted in memory only at call time, never logged.
- Later (production): move to a **KMS** (AWS KMS / Azure Key Vault) or HashiCorp Vault. Envelope encryption: KMS holds the master key, app never sees it.

### 2.8 Scopes

- Keep **coarse** for v1: one scope per tenant or per tool category. Per-tool `required_scopes` (as in the manifest example above) is aspirational for now, not v1. When tokens start carrying them, they become enforceable. The built scope is `tools` (per-tenant, "access this workspace's MCP tools") + `offline_access` (for refresh tokens).

---

## 3. Answered questions (FAQ — decided)

### 3.1 How do SMBs integrate their tools?

Via the **manifest + tiers** (§2.2), never by writing MCP server code. Tier 1 and the wizard path are genuinely no-code; the OpenAPI-import path needs *someone* (often the SMB's existing web vendor) to hand over a spec file once — a one-time lift, not ongoing MCP expertise.

### 3.2 Where do end-users log in?

**Through a browser popup/redirect the AI assistant triggers — never by typing credentials into chat.** This is standard OAuth 2.1 (the same UX as ChatGPT's Gmail/Asana connectors). The MCP endpoint is an **OAuth 2.1 resource server** and must expose `/.well-known/oauth-protected-resource` (RFC 9728) for client discovery.

### 3.3 Same login as the SMB account, or a separate one?

**Same identity, via delegation.** Jane authenticates against the SMB's own system; Tessera trusts the answer. Concretely:

- **Federated SSO** (SMB system is the IdP, OAuth/SAML): Jane logs in with her existing Acme credentials via redirect; we receive "Jane = patient #4821" and link it to a local record.
- **v1 — Tessera-hosted mirror (decided):** a lightweight account on our platform *tied to* her Acme record. The link is **admin-created**: Acme's admin adds Jane as a user in the Acme workspace — that's how Acme hands us her identity (provisioning); an email match can auto-verify/link when available. Same identity, different login. Federation (Acme's own IdP) is the later upgrade, designed as an interface now.

The token ChatGPT ends up holding is **scoped + tenant-bound + expiring** — it carries *which client, which user, which tenant*, and is short-lived (refresh for renewal). It is not a permanent grant for "all of Jane's activities."

### 3.4 What's the code structure?

Manifest-driven gateway + narrow Python↔C# boundary (§2.3), tenant addressing (§2.4), OpenIddict in the C# host (§2.6). See §6 for the target layout.

### 3.5 Should the MCP server be C# or Python?

**Python — decided.** This is deliberate: C# is the system of record, Python is the protocol adapter (most mature MCP SDK ecosystem), Next.js is the UI. The gateway must stay dumb so C# remains the single source of truth.

### 3.6 Can't we just reuse the current C# auth?

The current auth is a **first-party login** (username/password → JWT → our APIs) — fine for our dashboards. MCP requires an **OAuth 2.1 authorization server**, which needs capabilities we didn't have: authorization-code + PKCE redirect flow, discovery endpoints, **client registration** (ChatGPT/Claude are clients, not users), **consent UI**, scoped/audience-bound tokens (RFC 8707 — a token for tenant A can't hit tenant B), and issuer validation (`iss` / CIMD). We keep our JWT infra and add the AS layer via OpenIddict.

### 3.7 What about rate limiting / abuse?

The existing `RateLimiting` module covers it; MCP calls get per-tenant, per-user limits (configurable, and per-tool overrides in the manifest later). Token validation + tenant-claim checks are the same pattern as `TenantClaimValidationMiddleware`.

---

## 4. Open questions & spikes (need answers before/while building)

| # | Question | Why it matters | Status |
|---|---|---|---|
| Q1 | **Which wedge vertical first?** (dental, salons, legal, …) | Decides which Tier-1 connectors to build and the first real manifests | **Answered — no single wedge.** One generic tool-manifest schema; each vertical is a vertical-specific manifest built on it. Connectors are demand-driven (§2.2) |
| Q2 | **Customer-identity model details** — hosted mirror vs federation; how an identity maps to SMB records | The product moat; without it tool calls are anonymous | **Answered.** Mirror for v1 with an **admin-created link**: Acme's admin adds Jane as a user in the Acme workspace (Acme provides identity by provisioning); optional email-match auto-verification. Federation stays as a later interface (§3.3) |
| Q3 | **Per-user visibility rules** — what can a user see/do (Jane only her appointments; front desk everything) | Maps onto our role/action system; needed for real SMBs | Reuse `ActionChecks`; design per-SMB mapping during v1 |
| Q4 | **OpenIddict spike outcome** | Build-vs-buy decision point | **Answered — build it ourselves.** Commit to OpenIddict; no hosted-AS fallback. The spike became the AS core (§2.6) |
| Q5 | **Which Python MCP SDK** (official SDK vs FastMCP) | Gateway ergonomics; stateless-core support per 2026-07-28 spec | **Answered — official MCP SDK v2** (`mcp>=2`). Closest to the 2026-07-28 spec (stateless core); ecosystem converged on one base (FastMCP 4 is built on the rewritten SDK v2) |
| Q6 | **Client registration practicalities** — how ChatGPT/Claude register (CIMD vs DCR); pre-register our endpoint | Needed for the popup flow to work end-to-end in real assistants | **Answered — CIMD built.** The AS fetches/caches the client's metadata document; the discovery amendment makes Claude pick its published identity. DCR is not needed |
| Q7 | **Consent/scope granularity for v1** | UX + security balance | Default: one coarse scope per tenant (`tools` + `offline_access`) — built |
| Q8 | **Usage metering design** (per-user/per-tool events → billing) | Feeds F1 billing later; the gateway must emit events now | Emit usage events from the gateway to C# from day one; billing later |
| Q9 | **Gateway-facing manifest read endpoint** in C# | Server-to-server auth (gateway client credential or signed header) distinct from the admin `manage_tools` dashboard endpoints | **Not yet built** — part of the gateway milestone |

---

## 5. Backlog — deferred ("implement later")

> Items marked **✅ shipped 2026-09** were on this list and are now built — they are kept here so the original plan stays auditable.

| Item | Defer until | Trigger / notes |
|---|---|---|
| Tier-1 pre-built connectors | Demand (a customer asks, or a vertical shows traction) | Each vertical is a manifest on the generic schema; don't build generic connectors blind |
| OpenAPI import path | After wizard ships | Nice-to-have for SMBs with documented APIs |
| Tier-3 custom SDK (`@tessera.tool`) | After real demand | Escape hatch for complex logic; opt-in |
| Subdomain addressing / white-labeling | A customer asks for it | Path-based works until then |
| KMS / Vault for credentials | Production | v1 = env-var key + AES-GCM columns |
| Per-tool scopes | Post-v1 | Coarse scopes first |
| Billing & entitlements (F1) | Post-v1 | Suspension mechanism (B3) is already ready to hook into failed-payment |
| Audit trail (D4) + envelope versioning (A6) | P2 | Platform-level "who changed what" |
| Monitoring sink (E5) / CI-CD (E2) | Roadmap phases 4/7 | Serilog + TraceId already exist; no sink wired |
| CIMD client registration + Claude redirect-URI handling | ✅ Shipped 2026-09 | OpenIddict has no built-in CIMD; custom fetch/cache + validate |
| RS256 JWKS-based access-token verification in the **Python gateway** | ✅ Shipped 2026-09 | The C# AS already issues signed RS256 JWTs and serves `/.well-known/jwks`; what's left is the Python `TokenVerifier` consuming them |
| Gateway-facing manifest read endpoint (server-to-server) | ✅ Shipped 2026-09 | Distinct from admin `manage_tools` endpoints |
| Local Acme Dental backend stub (mock `api.acmedental.test`) | ✅ Shipped 2026-09 | So `call_tool` executes end-to-end against a real-looking backend |
| Tunnel/TLS story for hosted-assistant testing | ✅ Shipped 2026-09 | cloudflared/ngrok/Secure MCP Tunnel per target client |
| End-user (Jane) identity model | Gateway milestone | Admin-created mirror user per tenant; changes who the subject is, not the OAuth plumbing |

---

## 6. Architecture & code structure (target)

```
tessera/
  apps/
    web/                          # Next.js — SMB admin dashboard (manifest
                                  # wizard, connector setup) + end-user OAuth
                                  # login/consent pages  (BUILT)
    platform-portal/              # Next.js — platform superadmin UI
                                # C# modular monolith (existing + OAuth AS)
    platform/                     # C# = system of record
      src/
        Tessera.Platform.Api/     # host + composition root; internal API surface
                                  # consumed by mcp-gateway; ToolManifests CRUD;
                                  # authz via ActionChecks; OpenIddict AS  (BUILT)
        Tessera.Platform.Domain/  # models + constants
        Tessera.Platform.Observability/
        Tessera.Platform.Tests/   # 46 tests pass (as of 2026-09-12)

  apps/mcp-server/               # Python — generic, tenant-aware MCP server (BUILT)
                                  # per-tenant low-level Server, stateless Streamable HTTP
      src/
        core/gateway.py          # MCP protocol: list_tools/call_tool, stateless
                                  # request handling, transport
        manifest/                # pydantic models; TTL cache-aside loader over
                                  # GET /internal/gateway/manifests
                                  # (in-process TTL cache for local/demo)
        executors/
          http_executor.py       # generic HTTP-mapped tool execution (v1)
          custom_fn/             # Tier-3 SDK-registered executors (later)
        auth/token_verifier.py   # bearer validation (RS256 via the C# AS JWKS)
        utils/jsonpath.py        # response-mapping JSONPath subset
        tests/                   # 39 tests pass (as of 2026-09-12)
      stub_backend/app.py        # local Acme Dental backend (fixture)
      pyproject.toml / uv.lock

  packages/
    contracts/                    # shared JSON Schema for manifests; OpenAPI
                                  # spec for the internal Platform API
                                  # (not yet created)

  infra/
    docker-compose.yml            # api + db + caddy (TLS) + (future: gateway + stub)
  docs/
```

**Rules that keep this healthy:**
- Gateway never touches the DB, never holds business logic.
- Manifests cached in Redis (or in-process TTL cache for local/demo); a Platform outage degrades to cached reads, not total failure.
- Credentials fetched by the executor only at call time, never logged.
- OpenTelemetry emitted from the gateway directly to a collector (not round-tripped through C#).

**Repo home note:** the original MCP doc said `services/mcp-gateway`, but the repo already has an empty `apps/mcp-server`. Build in `apps/mcp-server`.

---

## 7. The MCP protocol we are implementing (2026-07-28, stateless core)

Sources: R&D notes with per-claim links were captured in the earlier standalone spec (consolidated into this doc on 2026-09-07). Current spec version: **2026-07-28** (RC 2026-05-21 → GA 2026-07-28). Previous revision: 2025-11-25.

What the current spec means for us:

| Spec change | Consequence for Tessera |
|---|---|
| No `initialize`/`initialized`, no sessions, no `Mcp-Session-Id` | One HTTP POST endpoint per tenant can be load balanced freely; no shared session store. Version + capabilities travel per request in `_meta`. |
| New optional `server/discover` RPC | Clients *may* probe versions/capabilities up front. SDK handles it. |
| Streamable HTTP is the transport; HTTP+SSE formally **deprecated** (12-month window) | Serve Streamable HTTP only. Requests: `POST <endpoint>`; replies are JSON or a request-scoped SSE stream. New required headers `Mcp-Method`, `Mcp-Name` mirror the JSON-RPC method for routing. |
| `tools/list` results are cacheable (`ttlMs`, `cacheScope`) and should be deterministic-order | Advertise the cached tool catalog with a sane `ttlMs`; deterministic order (we already order by tool name server-side). Clients cache → fewer hits on the platform. |
| Multi Round-Trip Requests (MRTR): servers needing info mid-call return `resultType: "input_required"` + `inputRequests`; client retries with `inputResponses` | Replaces server-initiated `elicitation/create`, `sampling/createMessage`, `roots/list`. Nice future capability (e.g., confirm-before-booking) without any open stream. Not v1. |
| Deprecations: Roots, Sampling, Logging; `includeContext` thisServer/allServers | Do not build Roots/Sampling/Logging support. Progress/logging: per-request via `_meta` or request-scoped streams only. |
| Tools/prompts/resources surface unchanged in spirit; JSON Schema 2020-12 fixed dialect; `$ref`/composition bounds clarified | Manifest `input_schema` is already JSON Schema — keep it 2020-12 compatible. |
| Tasks → official `io.modelcontextprotocol/tasks` extension; subscriptions via `subscriptions/listen` | Ignore for v1. |
| Extension framework formalized | Don't build on MCP Apps / Skills / EMA yet. |

Versioning/back-compat: **clients still on 2025-11-25 (or older) must keep working.** The Tier-1 SDKs (Python v2 included) negotiate both eras; "Serving legacy clients" is a documented SDK topic. Plan a test pass with a 2025-era client (older Claude Desktop, etc.).

---

## 8. Auth: the exact contract a hosted assistant expects

Synthesis of the MCP authorization spec (2026-07-28), Claude connector docs, and ChatGPT connector docs. This is the contract the **C# AS** must satisfy. The Python gateway is only the **resource server** half (served by SDK v2 from `AuthSettings` + `token_verifier`).

### 8.1 Discovery handshake (how a client finds us)

1. Client calls our MCP URL with no token → we answer **401** (never 200) with:
   `WWW-Authenticate: Bearer resource_metadata="https://…/.well-known/oauth-protected-resource[/mcp]"`.
2. `resource_metadata` document (RFC 9728) contains:
   `resource` (**must equal the exact MCP server URL the user entered**, including path),
   `authorization_servers: [issuer]` (first entry wins — list primary first),
   `scopes_supported` (least privilege; coarse tenant scopes).
   → The SDK generates this for the Python RS from `AuthSettings`.
3. Client goes to the AS issuer → RFC 8414 (`/.well-known/oauth-authorization-server`) **and/or** OIDC discovery (`/.well-known/openid-configuration`) — clients support both; we serve both. Metadata must advertise:
   - `code_challenge_methods_supported: ["S256"]` (PKCE is always used) — **built**,
   - `client_id_metadata_document_supported: true` **and** `token_endpoint_auth_methods_supported` containing `"none"` → this pair is what makes Claude (and spec-compliant clients) pick **CIMD** over DCR — **built** (OpenIddict ships no CIMD, so the discovery payload is amended from an `ApplyConfigurationResponseContext` handler),
   - `scopes_supported` including `offline_access` (clients append it to get refresh tokens; ChatGPT requires refresh tokens to keep a connector alive) — **built**,
   - `authorization_response_iss_parameter_supported: true` (RFC 9207 `iss`) — **built**,
   - `issuer`, `authorization_endpoint`, `token_endpoint`, `jwks_uri` — **built** (JWKS URI serves signing key `mcp-signing-v1`, RS256). Access tokens are **signed RS256 JWTs** (not opaque reference tokens; see §2.6 and `docs/platform/README.md` §6.2); the Python gateway validates them via JWKS once its `TokenVerifier` exists.

### 8.2 The live access-token contract (verified 2026-09-07)

The live token issued by the AS has this shape (decoded from a real token exchange through Caddy):

- Header: `alg=RS256`, `typ=at+jwt`, `kid=mcp-signing-v1`, **no `enc` claim** (signed-only, gateway-friendly).
- Claims: `iss=https://tessera.local/`, `aud=https://tessera.local/t/{tenant-slug}/mcp`, `exp/iat` bounded (15 min access), `sub` = user id, `tenant_id`/`tenant_identifier` = tenant, `scope=openid offline_access tools`, `client_id=tessera-local-dev`.
- Refresh token: encrypted JWT (`oi_reft+jwt`), single-use rotation enforced (`SetRefreshTokenReuseLeeway(0)`); replay yields `400 invalid_grant` ("already been redeemed").

This is the exact contract the Python gateway's `TokenVerifier` will consume: fetch `/.well-known/jwks`, verify the RS256 signature + `iss`/`aud`/`exp`/`scope`, never touch the C# AES key.

### 8.3 Authorization-code + PKCE flow (C# AS responsibilities)

- Endpoints: `/authorize`, `/token` (+ discovery above). `/token` must accept `application/x-www-form-urlencoded` (RFC 6749 §4.1.3) — clients send this content type; JSON-only parsers 415 and break. **Built** (form-urlencoded).
- `resource` (RFC 8707) **required** in authorize + token requests, must identify the tenant MCP endpoint; tokens are audience-bound to it. **Built** (resource → tenant via `McpOAuthService.TenantSlugFromResource`; token identity carries `resource` as audience).
- `iss` in authorization responses; validate redirect URIs against client metadata. **Built** (iss); redirect-URI validation against the CIMD doc is **built** (`ClientIdMetadataService`: https-only client_id, exact-URL match, loopback port-agnostic redirect URIs).
- Refresh tokens: **rotate for public clients** (return the new refresh token in the same response that invalidates the old one); errors must be `invalid_grant` (not `invalid_request`). **Built** (strict single-use rotation via `SetRefreshTokenReuseLeeway(0)`; replay → rejected).
- Timeouts clients apply: discovery/registration/token ≈ **10 s**, refresh ≈ **30 s**. Keep the AS fast; don't buffer token responses behind slow upstream work.
- TLS required for everything except loopback flows. **Built** (Caddy TLS proxy; dev-over-http allowed outside Production via `DisableTransportSecurityRequirement()`).

### 8.4 Client registration (CIMD — built)

- Client sends a `client_id` that is an **HTTPS URL** pointing at a Client ID Metadata Document (`client_id`, `client_name`, `redirect_uris`, `token_endpoint_auth_method`, …). AS **fetches** the doc, validates `client_id` matches the URL exactly, validates presented redirect URIs against it, caches per HTTP cache headers.
- Claude Code hosts its own CIMD (URL client_id). Claude's hosted surfaces (Claude.ai web/Desktop/mobile) have documented behaviors; callback URL is `https://claude.ai/api/mcp/auth_callback`; Claude Code uses loopback (`http://localhost:<port>/callback` and `http://127.0.0.1:<port>/callback`) — the AS **must match port-agnostically**.
- DCR (RFC 7591) is deprecated by the spec but still used as a fallback by some clients; supporting a `/register` endpoint (JSON body) is optional but cheap and improves interop. Decision: implement CIMD (fetch + cache) first, add DCR if a target client needs it.
- Scopes: 401 challenge should carry `scope="…"` for the operation so the client asks for the minimal set. Step-up (403 `insufficient_scope` + `scope`) exists in the spec; not v1.

### 8.5 Identity model (v1 scope)

- The AS authenticates against the existing C# `Users`/MFA/roles store. The first working milestone grants access to the tenant admin for their own tenant (coarse per-tenant scope). Jane-the-patient (admin-created mirror user, per §3.3 of this doc) is a **later** milestone and only changes who the subject is — the OAuth plumbing is identical.
- Consent screen must show the redirect URI host (loopback warning encouraged). Where it lives: Next.js portal (built) — per §2.6 of this doc.

### 8.6 Client-specific notes (verified July–Sept 2026 docs)

- **Claude (all surfaces + Claude Code):** auth types supported out of the box: `oauth_dcr`, `oauth_cimd`, `none`; `static_headers` (API key) in beta; Anthropic-held credentials by arrangement. PKCE S256 always. Connector discovery latency and egress ranges are documented (Anthropic egress starts at 160.79.104.0/21 — for allowlisting).
- **ChatGPT:** "custom connectors" live under **developer mode**, which currently requires a **Business/Enterprise/Edu** workspace (admin-enable + per-user toggle). Pro can connect read/fetch-permission MCPs in dev mode; full write/modify MCP is Business/Edu. ChatGPT is remote-only (no direct localhost): local dev machines use OpenAI's **Secure MCP Tunnel** or a public HTTPS URL. Search/fetch tools are **no longer required**. Published apps use a **frozen tool snapshot** — tool additions/edits need an admin "refresh" (re-publish) — relevant to our iterate-on-manifests loop. OAuth connectors must issue refresh tokens and advertise `offline_access`.
- **No hosted assistant works over plain `http://` from a public host.** Local testing stays on loopback (Inspector, Claude Code, desktop) until we add a TLS tunnel.

### 8.7 Runtime (gateway side — Python, built)

| # | Requirement | Where | Impact |
|---|---|---|---|
| R1 | Validate bearer JWT on **every** request: signature (RS256, cached JWKS from C#), `exp`, `iss`, and **`aud` == this tenant endpoint URL** | Python | `TokenVerifier` impl in gateway; per-tenant `AuthSettings(validate_token_resource=True)` |
| R2 | 401 on invalid/expired; 403 `insufficient_scope` + `WWW-Authenticate` scope on missing scope | Python | SDK handles 401; scope enforcement is ours in the verifier/handlers |
| R3 | Per-request caller identity available to handlers (`get_access_token()` → client_id/scopes/subject) | Python | Use `subject`/claims for per-user rules later (Jane model) |

---

## 9. Multi-tenancy on the OAuth surface (the crux)

**Today** tenant identity = `X-Tenant-Id` header + matching JWT claim. **On MCP, tenant identity = the URL and the token audience.** The gateway never sees `X-Tenant-Id`.

- The **canonical MCP URL** `https://…/t/{slug}/mcp` is the RFC 8707 `resource` the client requests and the token's `aud`. Two tenants ⇒ two distinct audiences ⇒ tokens can't be replayed across tenants (this is the tenant-isolation boundary on MCP).
- The **AS maps `resource` → tenant slug** at authorize/token time. It must validate the authenticated user actually belongs to that tenant before consenting (our model: a `Users` row scoped to that tenant — same email can exist in many tenants as separate rows; login must authenticate **inside the requested tenant**, mirroring today's tenant-header login).
- **Scopes are per-resource (per-tenant):** each tenant's PRM document lists only that tenant's scopes. v1 default: one coarse scope per tenant (`tools`) + `offline_access`. Per-tool `required_scopes` from manifests stay **aspirational** for now — they become enforceable later once tokens carry them.
- Suspended tenants: AS must refuse authorize/token (and the gateway should 401/403 on stale cached manifests) — reuse `TenantStatus`.
- **Don't carry `X-Tenant-Id` semantics onto the MCP path.** The token's audience/resource claim is the tenant identity on the MCP surface.

---

## 10. Two flows, concretely

### Flow 1 — First connect (Claude Code example)

```
Claude Code --POST /t/acme-dental/mcp--> Gateway          (no token)
Gateway: 401 + WWW-Authenticate: resource_metadata=…PRM
Claude Code --GET PRM doc--> Gateway /.well-known/oauth-protected-resource/…
              (resource=https://…/t/acme-dental/mcp, authorization_servers=[C# issuer])
Claude Code --GET AS metadata (RFC 8414 + OIDC)--> C# /.well-known/…
              (PKCE S256, CIMD supported, offline_access, iss param)
Claude Code --GET /connect/authorize?client_id=<its CIMD URL>&resource=…t/acme-dental/mcp…--> C#
  1. C# fetches Claude Code's CIMD doc (client_id URL) → validates redirect URIs   [built]
  2. Login (admin@tessera.com … against acme-dental's Users row; MFA if enabled)   [built]
  3. Consent: "Allow Claude Code to act as you in Acme Dental?" → code              [built]
Claude Code --POST /connect/token (code + PKCE verifier, form-urlencoded)--> C#
  → { access_token, refresh_token }
Claude Code --POST /t/acme-dental/mcp (Bearer)--> Gateway
  Gateway TokenVerifier: signature via cached JWKS; aud == own URL; exp; scope ⇒ tools/list   [built]
```

### Flow 2 — Every later call

```
Client --POST /t/acme-dental/mcp, Authorization: Bearer <JWT>--> Gateway
  Gateway: verify JWT (JWKS cache, aud=own URL, exp, scopes) → resolve tenant from URL
           → tools/call → HTTP executor → local Acme Dental stub
  401 (expired) → client refreshes proactively (~5 min before expiry) via POST /connect/token
  403 insufficient_scope → client re-authorizes with step-up scopes (not v1)
```

---

## 11. Target topology (local + demo)

```
AI assistant (Claude Code / Desktop / ChatGPT connector)
   │  OAuth 2.1 (PKCE) ── discovery → RFC 9728 PRM → C# AS (RFC 8414/OIDC, /connect/authorize,
   │                       /connect/token, CIMD fetch [BUILT], refresh rotation)
   ▼  Bearer <token aud=tenant MCP URL> on every POST
Python gateway (apps/mcp-server)  ── one ASGI app, routes:   [BUILT 2026-09-09 — live-verified]
   POST /t/{tenant}/mcp                      (Streamable HTTP; low-level Server per tenant)
   /.well-known/oauth-protected-resource/…   (SDK-generated PRM docs, per-tenant)
   ── TokenVerifier: RS256 JWT validation via C# JWKS   (BUILT; async verify_token, per-tenant aud binding)
   ── tools/list  ← tenant manifests (cached w/ TTL) from C# GET /internal/gateway/manifests
   ── tools/call  ← resolve manifest.execution → HTTP executor
   ── executors/http_executor.py → local SMB backend (Acme Dental stub; api.acmedental.test does not exist)
C# platform (apps/platform) — system of record:
   tool-manifest CRUD (BUILT) · Users/roles/action checks (BUILT)
   OpenIddict AS endpoints + discovery + signed RS256 JWTs + JWKS (BUILT)
   CIMD client registration (ClientIdMetadataService, BUILT 2026-09 — OpenIddict event handler)
   gateway-facing manifest read endpoint (GatewayEndpoints.cs, BUILT 2026-09 — X-Gateway-Api-Key)
   encrypted credential store resolution for vault://refs [deferred — dev uses env CREDENTIALS map]
```

- Gateway never touches the DB; it calls the C# API for manifests and validates tokens itself (JWKS/introspection). Keep `X-Tenant-Id` semantics out of the MCP path — the **token's audience/resource claim is the tenant identity** on the MCP surface.
- Platform middleware exclusions must grow to let the OAuth endpoints through (`TenantValidationMiddleware` currently requires `X-Tenant-Id` except for `/health /ready /openapi /admin /tenants /connect /.well-known` — already extended for `/connect` + `/.well-known`).

---

## 12. Gap analysis: platform today vs needed (build list)

Already there (local SMB fixture): acme-dental tenant + 3 seeded manifests + CRUD (verified live on :5000), Users/roles/action authz, JWT + MFA + refresh, rate limiting, tenant suspension, Serilog, **MCP OAuth AS core** (discovery, /connect/authorize/token, login, consent, PKCE, refresh rotation, portal pages, Caddy TLS).

Missing → remaining work items (status after 2026-09-09 live boot):

~~1. CIMD client fetch/cache + redirect-URI validation~~ — **BUILT** (`McpOAuth/ClientIdMetadataService.cs` + tests): https-only client_id, path handling, loopback any-port redirects; wired as an OpenIddict `ValidateAuthorizationRequest` handler ordered before built-in client validation.
~~2. Gateway-facing manifest read endpoint~~ — **BUILT** (`Endpoints/GatewayEndpoints.cs`): `GET /internal/gateway/manifests?tenant={slug}`, `X-Gateway-Api-Key` guarded, 404 unknown / 403 suspended; middleware exclusions added.
~~3. Python gateway~~ — **BUILT** (`apps/mcp-server`, uv/Python 3.13): manifest→Tool mapping, TTL loader (sentinel-caches unknown/suspended), HTTP executor (templates, `X-Api-Key` credential injection from env `CREDENTIALS` map, JSONPath response mapping, host overrides), per-tenant low-level `Server` + stateless Streamable HTTP, raw-ASGI dispatch. **39/39 pytest green; live three-service boot verified 2026-09-09** (platform :5010 → gateway :8000 → stub :9100): tools/list + book/list/call against the stub all worked over real HTTP. Production vault resolution for `vault://` refs remains deferred (dev: env map).
~~4. Local Acme Dental backend stub~~ — **BUILT** (`apps/mcp-server/stub_backend/app.py`): book/list/cancel + reset, API-key protected; runs standalone (`uvicorn stub_backend.app:app --port 9100`) and in tests via ASGI transport.
~~6. Python gateway `TokenVerifier`~~ — **BUILT** (`auth/token_verifier.py`): JWKS fetch/cache/refresh-on-unknown-kid, RS256, iss/aud(=tenant resource)/exp validation; async per SDK contract. Verified in-process (401 challenge → PRM → valid-token 200; wrong-aud 401; missing-scope 403 via SDK middleware). The live AS round-trip over Caddy TLS is the remaining E2E item.
~~5. Tunnel/TLS story for hosted-assistant testing + docker/Caddy wiring for the gateway (`/t/*` + PRM paths).~~ — **DONE** (`scripts/start-claude-web.sh`, Caddy routing in compose, cloudflared tunnel; **Claude web live-verified 2026-09-12**).
7. **End-user (Jane) identity model** (admin-created mirror user per tenant). [deferred]
8. **Per-tool scopes** (manifest `required_scopes` enforceable once tokens carry them). [deferred]

---

## 13. Local run (OAuth + TLS)

The local run steps below are the canonical reference (the earlier standalone `docs/mcp-auth-platform-local-run.md` was consolidated into this section on 2026-09-07). **The one-command path — tunnel + stack + verification — is `./scripts/start-claude-web.sh` (§13.1).** The manual steps are:

1. Add `127.0.0.1 tessera.local` to your hosts file (`/etc/hosts` or `C:\Windows\System32\drivers\etc\hosts`).
2. Rebuild + start the stack: `cd apps/platform && docker compose up -d --build` (runs migrations + seeds the `tessera-local-dev` client). The Caddy proxy routes the API, the MCP gateway (`mcp-gateway`), the stub backend (`stub-backend`), and the Next.js portal (running on the host at :3000).
3. Run the portal: `cd apps/web && npm run dev` (host :3000, reached by Caddy via `host.docker.internal`).
4. TLS: Caddy uses an internal CA on `tessera.local`. Browsers: open https://tessera.local, accept the dev warning once. Scripted clients: use `--cacert` from the `apps/platform/caddy-data` volume (or ignore TLS in dev).
5. Verification: discovery at `https://tessera.local/.well-known/openid-configuration`; the MCP gateway is reachable at `https://tessera.local/t/acme-dental/mcp` and advertises RFC 9728 PRM at `https://tessera.local/.well-known/oauth-protected-resource/t/acme-dental/mcp`; scripted PKCE flow (same shape as `McpOAuthTests`); manual login/consent via the Next.js portal pages at `https://tessera.local/oauth/login` and `/oauth/consent`.

### 13.1 Claude web (hosted MCP connector)

Claude web calls your stack from its own servers, so the whole stack must be
reachable on one public HTTPS origin (the AS + portal + MCP gateway all on the
same hostname). The short container-free path is a cloudflared quick tunnel in
front of Caddy.

**One command does all of it** (verified end-to-end 2026-09-12):

```bash
./scripts/start-claude-web.sh          # prints the URL to paste into Claude
./scripts/start-claude-web.sh --stop   # tears down portal, tunnel and stack
```

It checks Docker is running, starts the tunnel and **leaves it running**
(pidfile: `apps/platform/.tunnel.pid`), writes `ORIGIN=<tunnel url>` into
`apps/platform/.env`, runs `docker compose up -d --build`, starts the Next.js
portal on :3000 (the portal is deliberately **not** in compose, so you keep live
rebuilds), then verifies the whole OAuth surface and only prints the URL if
every check passes:

| Check | Expected |
|---|---|
| discovery `issuer` | `https://<tunnel>/` |
| discovery `jwks_uri` | `https://<tunnel>/.well-known/jwks` (absolute) |
| `/.well-known/jwks` | 200 |
| PRM `resource` | `https://<tunnel>/t/acme-dental/mcp` |
| `/t/acme-dental/mcp` unauthenticated | 401 (RFC 9728 challenge) |
| `/_next/*` with `Origin: <tunnel>` | not 403 (portal can hydrate) |

Then in claude.ai → Settings → Connectors → Add custom connector:

- URL: `<tunnel url>/t/acme-dental/mcp`
- Auth: **Sign in now (Detected)**
- OAuth client: **Use Claude's published identity (Recommended)**

Delete any connector created against an earlier tunnel — it carries a stale
issuer. Log in as `admin@tessera.com` / `Admin123!` (Postgres-only: the
per-tenant admin is only seeded on a relational DB) and approve the consent.

If the published-identity option isn't offered, the AS isn't advertising the two
CIMD flags — check the running API's discovery doc for
`client_id_metadata_document_supported: true` and `none` in
`token_endpoint_auth_methods_supported`. A stale api image won't have them.

#### Gotchas that cost real debugging time (all fixed)

- **Endpoint URIs must stay relative.** `SetAuthorizationEndpointUris`,
  `SetTokenEndpointUris` and `SetJsonWebKeySetEndpointUris` are matched against
  the *inbound* request, which behind Caddy is always `http://<host>/...`.
  Absolute `https://` URIs therefore never match and the passthrough handler
  throws `InvalidOperationException: The OpenID Connect request cannot be
  retrieved`. The public `https://` URLs come from trusted forwarded headers.
- **The API must trust `X-Forwarded-Proto`.** TLS ends at the tunnel/edge, so the
  process only ever sees plain HTTP. Without `UseForwardedHeaders()` and Caddy's
  `header_up X-Forwarded-Proto https` (the `(tls_hop)` snippet), every advertised
  URL comes out as `http://`.
- **`allowedDevOrigins` must cover the tunnel host.** Next.js dev rejects
  `/_next/*` requests carrying a non-localhost `Origin` with 403. The login page
  then still renders (SSR) but never hydrates, so the form falls back to a
  native submit and drops `?tenant=` / `?returnUrl=` — surfacing as
  "No workspace was specified." `apps/web/next.config.ts` allows
  `*.trycloudflare.com`.
- **Never kill the tunnel after reading its URL.** With the tunnel down, every
  request from Claude is a Cloudflare **530**.
- **Don't read credentials from React state alone.** Password-manager autofill
  (and anything typed before hydration) updates the DOM without firing
  `onChange`, so the login POST carried `{"email":"","password":""}` and got a
  401. The portal's login/MFA forms read `new FormData(e.currentTarget)` at
  submit time.

If you have a stable public hostname (a domain behind Caddy rather than a temp
tunnel), set `ORIGIN` to it, bring the stack up, skip the tunnel, and connect
with that hostname.

---

## 14. The Acme Dental sample SMB (test fixture)

See `docs/sample-smb/acme-dental-manifest.json` + `acme-dental-book-appointment.json` for the machine-readable fixture. Summary:

- **Tenant:** `acme-dental` (seeded at startup on both Postgres and InMemory).
- **Seeded manifests (3):** `book_appointment`, `cancel_appointment`, `list_appointments` — each with full `inputSchema`, `execution` (HTTP config, `credential_ref`), and `requiredScopes`. Stored as `ToolManifests` records via `SampleSmbSeeder`.
- **Credentials:** tenant admin `admin@tessera.com` / `Admin123!` (per tenant, PostgreSQL only); platform superadmin `superadmin@tessera.com` / `Admin123!` (both providers).
- **Target backend:** `https://api.acmedental.test` does **not** exist. In dev the gateway rewrites that host to the local stub backend (`apps/mcp-server/stub_backend/app.py`, :9100) via `SMB_HOST_OVERRIDES`; in compose it points at the `stub-backend` container. Tests use an in-process ASGI transport.
- **Sample manifest files:** `docs/sample-smb/acme-dental-manifest.json` and `docs/sample-smb/acme-dental-book-appointment.json` are the human-readable source of truth and are mirrored by `SampleSmbSeeder`.

The first three tools are the **seeded set**. `patient_inquiry` is a planned mock tool for future SDK-tier work and is **not** seeded yet.

## 15. Concerns register (everything raised so far)

See `docs/CONCERNS.md` — it's the shared concerns register for the whole product. The items most relevant to MCP:

| # | Concern | Status | Resolution |
|---|---|---|---|
| 1 | How do SMBs integrate their tools? | ✅ Answered | Manifest + 3 tiers (§2.1, §2.2) |
| 2 | Difficulty for non-technical SMBs | ✅ Answered | Tier 1 & wizard are no-code (§3.1) |
| 3 | Where do end-users log in? | ✅ Answered | Browser popup OAuth, not in-chat (§3.2) |
| 4 | Same login as SMB account or separate? | ✅ Answered | Delegation — SMB identity, mirror account v1 (§3.3) |
| 5 | Low-level code structure | ✅ Answered | Manifest-driven gateway + narrow boundary (§2.3, §6) |
| 6 | C# vs Python for the MCP server | ✅ Answered | Python gateway; C# = system of record (§2.3, §3.5) |
| 7 | Why not reuse current C# auth? | ✅ Answered | Needs OAuth AS capabilities (§3.6) |
| 8 | Path-based vs subdomain addressing | ✅ Answered | Path-based v1 (§2.4) |
| 9 | Credential storage | ✅ Answered (v1) / deferred (hardening) | Encrypted columns now; KMS later (§2.7) |
| 10 | Scopes granularity | ✅ Answered (default) | Coarse for v1 (§2.8) |
| 11 | Rate limiting / abuse | ✅ Answered | Existing module + per-tenant/per-user limits |
| 12 | MCP architecture has no obvious authorization boundary for tool execution | 🔴 Open — bigger future gap | End-user tool authz (Jane) is a separate plane from workspace RBAC — design before gateway (CONCERNS §14) |
| 13 | Manifest versioning vs authorization versioning | 🟠 Open | What happens to cached tools / issued tokens / consent when a manifest changes (CONCERNS §15) |
| 14 | Gateway-facing manifest read endpoint | ✅ Built 2026-09 (`GatewayEndpoints.cs`) | Server-to-server auth via `X-Gateway-Api-Key`, distinct from admin `manage_tools` endpoints |
| 15 | CIMD client registration | ✅ Built 2026-09 (`ClientIdMetadataService.cs`) | Custom OpenIddict event handler (no built-in CIMD); tests in `CimdTests.cs` |

---

## 16. Suggested implementation phases

- **Phase A — Gateway core (✅ DONE 2026-09-09):** `apps/mcp-server` scaffolded (uv/Python 3.13, official SDK v2 `mcp 2.2.0`); low-level `Server` per tenant; manifest fetch + TTL cache from C# via `GET /internal/gateway/manifests` (`X-Gateway-Api-Key`); `tools/list` + `tools/call` with HTTP executor → local Acme Dental stub; **39/39 pytest green** (in-process `Client(server)` + HTTP-level + OAuth-mode tests). **Live-verified end-to-end 2026-09-09:** platform (:5010) + gateway (:8000, `MCP_AUTH_MODE=none`) + stub (:9100) — tools/list returned the 3 seeded manifests; book_appointment booked, listed and cancelled a real appointment through the whole chain (see `docs/progress.md` §3 for the transcript and boot gotchas). Protocol notes baked into tests: 2026-07-28 stateless POSTs need the `params._meta` envelope (`io.modelcontextprotocol/protocolVersion` + `clientCapabilities`) and `Mcp-Method`/`Mcp-Name` headers.
- **Phase B — Local assistant end-to-end (✅ DONE 2026-09-12):** boot via `docker compose up -d --build` + Caddy TLS; connect MCP Inspector (protocol-level) then Cursor/VS Code (free, reaches localhost, full OAuth) and iterate on tool ergonomics; then Claude web via tunnel.

  Local stack commands (current):

      # 1. Start a public https tunnel and write ORIGIN (once per tunnel; temp
      #    tunnels get a new name each restart). Paste the printed URL into any
      #    client instructions you share.
      bash apps/platform/scripts/start-claude-web.sh

      # 2. Bring up the full stack (API + C# OAuth AS + MCP gateway + stub
      #    backend + Caddy proxy). Runs migrations + seeds the tessera-local-dev
      #    dev client.
      cd apps/platform && docker compose up -d --build

      # 3. Start the portal (host :3000). The MCP gateway and stub backend are
      #    started by docker compose in this mode, so don't also run uv sync
      #    uvicorn on the host unless you want to test them locally.
      cd apps/web && npm run dev

  Validation checkpoints before using claude.ai (so you don't debug through the
  UI):

      # Resource metadata names the AS.
      curl -s https://<origin>/.well-known/oauth-protected-resource/t/acme-dental/mcp

      # AS discovery: issuer must equal the origin, and the two CIMD flags must
      # be present (otherwise Claude web falls back to a non-CIMD client id and
      # the AS rejects it as invalid_client).
      curl -s https://<origin>/.well-known/openid-configuration | grep -E '"issuer"|client_id_metadata_document_supported|token_endpoint_auth_methods_supported'

      # Unauthenticated MCP call must 401 with a resource_metadata challenge.
      curl -si https://<origin>/t/acme-dental/mcp -X POST -H 'Content-Type: application/json' \
          -H 'Mcp-Method: tools/list' \
          -d '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}' | grep -i www-authenticate
- **Phase C — OAuth (AS ✅ built 2026-09-07; gateway half ✅ built 2026-09-09):** OpenIddict discovery + signed RS256 JWTs + JWKS (live); CIMD client registration via custom OpenIddict event handler (`ClientIdMetadataService`); Python `TokenVerifier` (JWKS, per-tenant aud). Remaining: the live discovery dance over Caddy TLS with a real AS-issued token (negative cases already covered in-process).
- **Phase D — Hosted assistants:** Claude web (Settings → Connectors → remote MCP URL) and ChatGPT connector (Business/Edu) via Secure MCP Tunnel/public URL; validate tool scan, refresh-token persistence, and the frozen-snapshot workflow.
- Cross-cutting: dual-era (2025 vs 2026 protocol) client test pass; keep the three engineering docs (`docs/platform`, `docs/web`, `docs/mcp`) in sync with the code; mock SMB backend gets a docker-compose service.
