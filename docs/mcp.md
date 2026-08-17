# Tessera MCP Template — Source of Truth

> **Status:** working design doc — the authoritative record of what we've decided, what's still open, and what's deferred. Update it whenever a decision lands. Last updated 2026-08-17 (pre-implementation lock-down).
>
> Related: [`ARCHITECTURE.md`](ARCHITECTURE.md) (platform), [`multitenant_mature.md`](multitenant_mature.md) (prod-readiness gaps).

---

## 0. TL;DR

**The product:** SMBs get a "plug and play" integration layer — they plug their existing tools/services into Tessera, and *their* customers use those services through AI assistants (ChatGPT, Claude, …) via a hosted MCP server.

**The core idea:** SMBs never write MCP server code. They configure a **tool manifest** (a JSON document describing their tools + how to execute them); one generic MCP server reads that manifest per-tenant and exposes the tools to AI assistants.

**Stack (locked):** C# modular monolith = system of record (data, authz, manifests, auth). Python = thin MCP gateway (protocol adapter only, no business logic, no direct DB access). Next.js = admin dashboard + end-user login/consent pages.

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
- Manifest format (shared contract across all integration tiers):

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

**Sequencing (locked):** wizard + manual manifest editing **first** (it's a CRUD app) → pre-built connectors **second** (only after the wedge vertical is chosen) → OpenAPI import + SDK **later**.

### 2.3 Stack & the Python ↔ C# boundary
- **C# = system of record.** All business logic, data, authz, manifests, credentials, usage accounting live here.
- **Python gateway = protocol adapter.** Speaks MCP to AI assistants; **must never** touch the database or hold business logic. All reads go through the C# API (manifest fetch, token validation, authz check, usage events), with Redis as a manifest cache so a Platform hiccup doesn't kill every tenant's MCP calls.
- This boundary is what keeps Python thin and swappable.

### 2.4 Tenant addressing
- **Path-based for v1:** `https://mcp.tessera.io/t/{tenant-slug}/mcp` — one DNS record, one cert, no infra work per tenant.
- Subdomain per tenant (`acme.mcp.tessera.io`) deferred until white-labeling is a real requirement.

### 2.5 The manifest in the existing platform
- `ToolManifests` is just another `IsMultiTenant()` entity — same pattern as `Widgets` / `TenantRoles`.
- Managing manifests slots into the existing **action-based authz** (`ActionChecks`) as a new action, e.g. `manage_tools`. SMB superadmin manages the tenant's tools; platform superadmin governs manifests/credentials.
- Reuses: Finbuckle isolation, `TenantClaimValidationMiddleware`, rate limiting module, JWT infra.

### 2.6 OAuth: OpenIddict first
- Use **OpenIddict** (open-source .NET AS library) inside the C# host. Free, C#, composes with the existing Users table and JWT signing.
- Fallback: if the redirect/consent/security surface eats more than ~2 weeks, buy **WorkOS / Auth0 / Clerk** (hosted AS) fronting our token issuance.
- Login/consent **pages live in the Next.js app** either way (per-tenant branding, unified with the admin dashboard).
- The existing first-party auth stays as-is; OpenIddict composes with it (see §3.6 "why the current auth isn't enough").

### 2.7 Credential vault
- v1: **encrypted-at-rest DB columns** — AES-GCM with an env-var master key + a rotation plan. Credentials decrypted in memory only at call time, never logged.
- Later (production): move to a **KMS** (AWS KMS / Azure Key Vault) or HashiCorp Vault. Envelope encryption: KMS holds the master key, app never sees it.

### 2.8 Scopes
- Keep **coarse** for v1: one scope per tenant or per tool category. Per-tool `required_scopes` (as in the manifest example above) is aspirational, not v1.

---

## 3. Answered questions (FAQ — decided)

### 3.1 How do SMBs integrate their tools?
Via the **manifest + tiers** (§2.2), never by writing MCP server code. Difficulty for non-technical SMBs: Tier 1 and the wizard path are genuinely no-code; the OpenAPI-import path needs *someone* (often the SMB's existing web vendor) to hand over a spec file once — a one-time lift, not ongoing MCP expertise.

### 3.2 Where do end-users log in?
**Through a browser popup/redirect the AI assistant triggers — never by typing credentials into chat.** This is standard OAuth 2.1 (the same UX as ChatGPT's Gmail/Asana connectors). The MCP endpoint is an **OAuth 2.1 resource server** and must expose `/.well-known/oauth-protected-resource` (RFC 9728) for client discovery.

### 3.3 Same login as the SMB account, or a separate one?
**Same identity, via delegation.** Jane authenticates against the SMB's own system; Tessera trusts the answer. Concretely:
- **Federated SSO** (SMB system is the IdP, OAuth/SAML): Jane logs in with her existing Acme credentials via redirect; we receive "Jane = patient #4821" and link it to a local record.
- **v1 fallback — Tessera-hosted mirror:** a lightweight account on our platform *tied to* her Acme record (verified by email match or an admin-created link). Same identity, different login. Simpler to build first.

The token ChatGPT ends up holding is **scoped + tenant-bound + expiring** — it carries *which client, which user, which tenant*, and is short-lived (refresh for renewal). It is not a permanent grant for "all of Jane's activities."

### 3.4 What's the code structure?
Manifest-driven gateway + narrow Python↔C# boundary (§2.3), tenant addressing (§2.4), OpenIddict in the C# host (§2.6). See §6 for the layout.

### 3.5 Should the MCP server be C# or Python?
**Python — decided.** This is deliberate: C# is the system of record, Python is the protocol adapter (most mature MCP SDK ecosystem), Next.js is the UI. The gateway must stay dumb so C# remains the single source of truth.

### 3.6 Can't we just reuse the current C# auth?
The current auth is a **first-party login** (username/password → JWT → our APIs) — fine for our dashboards. MCP requires an **OAuth 2.1 authorization server**, which needs capabilities we don't have: authorization-code + PKCE redirect flow, discovery endpoints, **client registration** (ChatGPT/Claude are clients, not users), **consent UI**, scoped/audience-bound tokens (RFC 8707 — a token for tenant A can't hit tenant B), and issuer validation (`iss` / CIMD). We keep our JWT infra and add the AS layer via OpenIddict.

### 3.7 What about rate limiting / abuse?
The existing `RateLimiting` module covers it; MCP calls get per-tenant, per-user limits (configurable, and per-tool overrides in the manifest later). Token validation + tenant-claim checks are the same pattern as `TenantClaimValidationMiddleware`.

---

## 4. Open questions & spikes (need answers before/while building)

| # | Question | Why it matters | Default / next step |
|---|---|---|---|
| Q1 | **Which wedge vertical first?** (dental, salons, legal, …) | Decides which Tier-1 connectors to build and the first real manifests | **Pick one before building connectors**; wizard works for any vertical |
| Q2 | **Customer-identity model details** — hosted mirror vs federation; how an identity maps to SMB records (email match? admin mapping? pass-through user ID?) | The product moat; without it tool calls are anonymous | Design in §3.3 direction; pick mirror for v1, keep federation as an interface |
| Q3 | **Per-user visibility rules** — what can a user see/do (Jane only her appointments; front desk everything) | Maps onto our role/action system; needed for real SMBs | Reuse `ActionChecks`; design per-SMB mapping during v1 |
| Q4 | **OpenIddict spike outcome** | Build-vs-buy decision point | Time-box ~2 weeks; then commit or switch to WorkOS |
| Q5 | **Which Python MCP SDK** (official SDK vs FastMCP) | Gateway ergonomics; stateless-core support per 2026-07-28 spec | Evaluate when starting the gateway |
| Q6 | **Client registration practicalities** — how ChatGPT/Claude register (CIMD vs DCR); pre-register our endpoint | Needed for the popup flow to work end-to-end in real assistants | Verify against the 2026-07-28 spec when implementing |
| Q7 | **Consent/scope granularity for v1** | UX + security balance | Default: one coarse scope per tenant |
| Q8 | **Usage metering design** (per-user/per-tool events → billing) | Feeds F1 billing later; the gateway must emit events now | Emit usage events from the gateway to C# from day one; billing later |

---

## 5. Backlog — deferred ("implement later")

| Item | Defer until | Trigger / notes |
|---|---|---|
| Tier-1 pre-built connectors | Q1 answered (wedge chosen) | Build per vertical need; don't build generic connectors blind |
| OpenAPI import path | After wizard ships | Nice-to-have for SMBs with documented APIs |
| Tier-3 custom SDK (`@tessera.tool`) | After real demand | Escape hatch for complex logic; opt-in |
| Subdomain addressing / white-labeling | A customer asks for it | Path-based works until then |
| KMS / Vault for credentials | Production | v1 = env-var key + AES-GCM columns |
| Per-tool scopes | Post-v1 | Coarse scopes first |
| Billing & entitlements (F1) | Post-v1 | Suspension mechanism (B3) is already ready to hook into failed-payment |
| Audit trail (D4) + envelope versioning (A6) | P2 | Platform-level "who changed what" |
| Monitoring sink (E5) / CI-CD (E2) | Roadmap phases 4/7 | Serilog + TraceId already exist; no sink wired |

---

## 6. Architecture & code structure (target)

```
tessera/
  services/
    mcp-gateway/                  # Python — generic, tenant-aware MCP server
      src/
        core/                    # MCP protocol: list_tools/call_tool, stateless
                                  # request handling, transport
        manifest/                # pydantic models; cache-aside loader (Redis
                                  # in front of the Platform API)
        executors/
          http_executor.py       # generic HTTP-mapped tool execution (v1)
          custom_fn/             # Tier-3 SDK-registered executors (later)
        auth/                    # bearer validation (JWKS / introspection via C#)
        telemetry/               # OpenTelemetry export to a collector
        tests/
      pyproject.toml

    platform/                     # C# modular monolith (existing)
      Tessera.Platform.Api/       # host + composition root; internal API surface
                                  # consumed by mcp-gateway; ToolManifests CRUD;
                                  # authz via ActionChecks; OpenIddict AS
      Tessera.Platform.Observability/
      Tessera.Platform.RateLimiting/
      Tessera.Platform.Tests/

  apps/
    web/                          # Next.js — SMB admin dashboard (manifest
                                  # wizard, connector setup) + end-user OAuth
                                  # login/consent pages
    platform-portal/              # Next.js — platform superadmin UI

  packages/
    contracts/                    # shared JSON Schema for manifests; OpenAPI
                                  # spec for the internal Platform API

  infra/
    docker-compose.yml
  docs/
```

**Rules that keep this healthy:**
- Gateway never touches the DB, never holds business logic.
- Manifests cached in Redis; a Platform outage degrades to cached reads, not total failure.
- Credentials fetched by the executor only at call time, never logged.
- OpenTelemetry emitted from the gateway directly to a collector (not round-tripped through C#).

---

## 7. Concerns register (everything raised so far)

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
| 11 | Rate limiting / abuse | ✅ Answered | Existing module + per-tenant limits (§3.7) |
| 12 | Build vs buy the OAuth AS | ⏳ **Open** (spike) | Default OpenIddict; switch to WorkOS if spike runs long (Q4) |
| 13 | Which wedge vertical first | ⏳ **Open** | Decides connectors (Q1) |
| 14 | Customer-identity model (who Jane is to Acme) | ⏳ **Open** | Mirror v1; federation interface (Q2) |
| 15 | Per-user visibility rules | ⏳ **Open** | Reuse ActionChecks (Q3) |
| 16 | Python MCP SDK choice | ⏳ **Open** | Official vs FastMCP (Q5) |
| 17 | Client registration practicalities | ⏳ **Open** | Verify against spec when building (Q6) |
| 18 | Usage metering → billing | ⏳ Deferred | Emit events from day one; billing later (Q8, §5) |
| 19 | Audit trail (D4) / envelope versioning (A6) | ⏳ Deferred | P2 (§5) |
| 20 | Monitoring sink (E5) / CI-CD (E2) | ⏳ Deferred | Roadmap phases 4/7 (§5) |
| 21 | Billing & entitlements (F1) | ⏳ Deferred | Suspension mechanism ready (§5) |

**Legend:** ✅ answered/decided · ⏳ open (needs a decision or spike) · deferred = decided to do later.

---

## 8. Spec references (verified 2026-08-17)

- **MCP 2026-07-28 revision** — current protocol version. Introduces the **stateless core** (no session handshake) and formalizes **OAuth 2.1**: MCP servers are resource servers; clients use authorization-code + PKCE; authorization responses carry `iss` (mix-up prevention); **CIMD** (Client ID Metadata Documents) is the client-registration standard, DCR kept for backward compatibility.
- **RFC 9728** — OAuth 2.0 Protected Resource Metadata (`/.well-known/oauth-protected-resource`) — MCP servers must publish it.
- **RFC 8707** — Resource Indicators — bind tokens to a specific tenant endpoint so they can't be replayed elsewhere.

*Verify details against the live spec (modelcontextprotocol.io) at implementation time — spec claims in older versions of this doc proved accurate, but re-check before building.*
