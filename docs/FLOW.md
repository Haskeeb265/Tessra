# Tessera — End-to-End Flow (Mermaid)

> **What this is:** the complete flow of Tessera as it exists today — the
> platform (C#), the MCP gateway (Python), the web portals (Next.js), the OAuth
> authorization server, and the SMB backend the tools actually operate on.
>
> **How it's organised:** §1 is one system overview you can read in a minute;
> §2–§6 break the same system into its modules. Every diagram is generated
> directly from the code in this repo.
>
> **Last verified against the code:** 2026-09-12 (live-verified end-to-end:
> Claude web → tunnel → Caddy → gateway → C# AS → Acme Dental stub).
>
> **Related:** [`docs/README.md`](README.md) (architecture index),
> [`docs/platform/README.md`](platform/README.md) (C# service),
> [`docs/mcp/README.md`](mcp/README.md) (MCP product model + gateway),
> [`docs/web/README.md`](web/README.md) (portals),
> [`docs/LIVE_TESTING_GUIDE.md`](LIVE_TESTING_GUIDE.md) (how to run it live).

## Diagram index

| # | Module | Diagram | Answers |
|---|---|---|---|
| 1 | **Overview** | System map | Who talks to whom, over what |
| 2 | **Platform** | Composition, middleware pipeline, tenant resolution, first-party auth, manifest admin | What the C# service does |
| 3 | **OAuth AS** | Discovery, CIMD registration, PKCE authorize + token exchange | How an AI assistant gets a token |
| 4 | **Gateway** | Request lifecycle, `tools/list`, `tools/call` execution | How a tool actually runs |
| 5 | **Web** | Portal routes, OAuth login + consent pages | What the human sees |
| 6 | **End-to-end** | The proven Claude web journey | The whole thing, start to finish |
| 7 | **Topology** | Local/Docker deployment, ports, routing | Where everything runs |

---

## 1. System overview

Four moving parts: the **C# platform** (system of record + OAuth authorization
server), the **Python MCP gateway** (protocol adapter), the **Next.js portals**
(human UI incl. the OAuth login/consent pages), and the **SMB backend** (the
real service the tools operate on — a stub in the fixture).

```mermaid
flowchart TB
    subgraph actors["Actors"]
        Jane["Jane — SMB customer / end user"]
        Claude["AI assistant — Claude web or Claude Code"]
        SmbAdmin["SMB admin — Acme Dental"]
        SuperAdmin["Platform superadmin"]
    end

    subgraph web["Web module — Next.js (docs/web)"]
        Portal["apps/web :3000<br/>dashboard · team · /oauth/login · /oauth/consent"]
        AdminPortal["apps/platform-portal :3001<br/>tenants · envelopes"]
    end

    subgraph platform["Platform module — C# (docs/platform)"]
        Api["Tessera.Platform.Api<br/>authz · tenants · roles · manifests"]
        AS["MCP OAuth authorization server<br/>OpenIddict 7.7 · /connect/* · JWKS"]
        DB[("PostgreSQL 16<br/>shared DB + tenant_id")]
    end

    subgraph mcp["MCP module — Python (docs/mcp)"]
        Gateway["MCP gateway :8000<br/>/t/{tenant}/mcp · PRM"]
    end

    subgraph smb["SMB backend — fixture"]
        Backend["Acme Dental API<br/>stub :9100 in dev"]
    end

    Claude -->|"MCP over HTTPS<br/>Bearer token"| Gateway
    Claude -.->|"OAuth 2.1 + PKCE<br/>login + consent"| AS
    Jane -.->|"browser popup"| Portal

    Portal -->|"JWT + X-Tenant-Id"| Api
    AdminPortal -->|"SuperAdmin JWT"| Api
    Jane --> Portal
    SmbAdmin --> Portal
    SuperAdmin --> AdminPortal

    Gateway -->|"manifests (server-to-server)"| Api
    Gateway -->|"token validation via JWKS"| AS
    Gateway -->|"tool execution"| Backend

    Api --- DB
    AS --- DB
```

**The one rule that explains the whole design:** the C# platform is the
**system of record**. The gateway is a dumb protocol adapter — it never touches
the database and holds no business logic. Add a tool for a tenant by adding a
manifest row, never by deploying code.

---

## 2. Platform module (C#) — `apps/platform`

### 2.1 Composition

Three class libraries, one ASP.NET host. The host owns the composition root;
`Domain` has no ASP.NET dependency.

```mermaid
flowchart LR
    Api["Tessera.Platform.Api<br/>ASP.NET Core host"]
    Domain["Tessera.Platform.Domain<br/>models + ActionCatalog"]
    Obs["Tessera.Platform.Observability<br/>RequestLoggingMiddleware"]

    Api --> Domain
    Api --> Obs
```

### 2.2 Middleware pipeline

Order is load-bearing. `/connect` and `/.well-known` are excluded from tenant
validation because on the MCP surface the tenant travels in the RFC 8707
`resource` parameter, not an `X-Tenant-Id` header.

```mermaid
flowchart LR
    Req["HTTP request"] --> Https["UseHttpsRedirection"]
    Https --> Ex["1 · ExceptionHandlingMiddleware"]
    Ex --> Log["2 · RequestLoggingMiddleware"]
    Log --> Cors["3 · CORS WebApp"]
    Cors --> Rate["4 · RateLimiter auth (429)"]
    Rate --> TV["5 · TenantValidationMiddleware (400)"]
    TV --> MT["6 · UseMultiTenant — Finbuckle"]
    MT --> Sus["7 · TenantSuspensionMiddleware (403)"]
    Sus --> AuthN["8 · UseAuthentication (401)"]
    AuthN --> TC["9 · TenantClaimValidationMiddleware (403)"]
    TC --> TVer["10 · TokenVersionValidationMiddleware (401)"]
    TVer --> AuthZ["11 · UseAuthorization (403)"]
    AuthZ --> Ep["Endpoint handler<br/>+ ActionChecks"]
```

### 2.3 Tenant resolution and data isolation

```mermaid
sequenceDiagram
    autonumber
    participant B as Browser (apps/web)
    participant A as Platform API
    participant S as DbTenantStore
    participant DB as PostgreSQL

    B->>A: GET /widgets · X-Tenant-Id: alpha-corp · Bearer JWT
    Note over A: middleware 5–7 pass (header present, tenant active)
    A->>S: GetByIdentifierAsync("alpha-corp")
    S->>DB: SELECT * FROM Tenants WHERE Identifier = 'alpha-corp'
    S-->>A: Tenant { Id: "alpha", Identifier: "alpha-corp" }
    Note over A: tenant context bound to the request (AsyncLocal)
    A->>A: compare JWT tenant_identifier claim to header (403 on mismatch)
    A->>DB: SELECT * FROM Widgets WHERE TenantId = 'alpha'
    DB-->>A: alpha's rows only
    A-->>B: 200 [widgets]
```

| Isolation layer | Mechanism |
|---|---|
| Tenant resolution | `X-Tenant-Id` → `DbTenantStore` → Finbuckle context |
| Data isolation | `IsMultiTenant()` → global query filters + `EnforceMultiTenant` on save |
| Token isolation | JWT `tenant_identifier` claim must equal the header |
| Permission isolation | Actions resolved live from `TenantRole` at request time |
| Session freshness | JWT `token_version` must equal the user's current version |

### 2.4 First-party auth (dashboards — unchanged by MCP)

```mermaid
sequenceDiagram
    autonumber
    participant U as Tenant user
    participant W as apps/web
    participant A as Platform API
    participant DB as PostgreSQL

    U->>W: Open emailed invite link (/register?invite=…&tenant=…)
    W->>A: POST /auth/register + X-Tenant-Id
    A->>DB: validate invite (email match, unused, unexpired)
    A->>DB: INSERT User (bcrypt, role from invite)<br/>first redemption → workspace Superadmin
    A->>DB: INSERT RefreshToken (family, 7d)
    A-->>W: 201 { accessToken 15m, refreshToken }
    W->>W: store in localStorage, redirect /dashboard

    U->>W: Log in
    W->>A: POST /auth/login + X-Tenant-Id
    alt MFA enabled
        A-->>W: 200 { mfaRequired, mfaToken 5m }
        W->>A: POST /auth/mfa { mfaToken, code }
    end
    A-->>W: 200 { accessToken, refreshToken }
    Note over W,A: any 401 → POST /auth/refresh (rotate family) → retry once
```

### 2.5 Manifest administration (the tool catalog's source of truth)

An SMB admin manages their tenant's tools through `manage_tools`-gated CRUD.
The `ToolManifests` table is the contract the gateway reads.

```mermaid
flowchart LR
    Admin["SMB admin (apps/web)"] -->|"manage_tools"| Crud["/tenant/manifests<br/>GET · POST · PUT · DELETE"]
    Crud --> Table[("ToolManifests<br/>TenantId · ToolName · InputSchema<br/>Execution · RequiredScopes")]
    Seed["SampleSmbSeeder<br/>(startup, idempotent)"] --> Table
    Table -->|"GET /internal/gateway/manifests<br/>X-Gateway-Api-Key"| Gateway["MCP gateway"]

    subgraph seeded["Seeded Acme Dental tools"]
        T1["book_appointment"]
        T2["cancel_appointment"]
        T3["list_appointments"]
    end
    Table --- seeded
```

---

## 3. OAuth module — the authorization server (`/connect/*` + `/.well-known/*`)

The AI assistant is an OAuth **client**; the user is authenticated against the
SMB's identity; the token is bound to one tenant's MCP URL so it can never be
replayed against another tenant.

### 3.1 Discovery — how the assistant finds the AS

```mermaid
sequenceDiagram
    autonumber
    participant C as AI client (Claude)
    participant G as MCP gateway
    participant AS as C# authorization server

    C->>G: POST /t/acme-dental/mcp (no token)
    G-->>C: 401 + WWW-Authenticate: Bearer resource_metadata="…/.well-known/oauth-protected-resource/t/acme-dental/mcp"
    C->>G: GET /.well-known/oauth-protected-resource/t/acme-dental/mcp
    G-->>C: { resource: "…/t/acme-dental/mcp",<br/>authorization_servers: [issuer], scopes_supported: [tools] }
    C->>AS: GET /.well-known/openid-configuration
    AS-->>C: issuer · authorization_endpoint · token_endpoint · jwks_uri<br/>code_challenge_methods_supported: [S256]<br/>client_id_metadata_document_supported: true<br/>token_endpoint_auth_methods_supported: [none, …]<br/>scopes_supported: [tools, offline_access]
    Note over C: the two CIMD flags are what make Claude pick<br/>"Use Claude's published identity" over DCR
```

### 3.2 Client registration — CIMD

No dynamic registration endpoint is needed. The client's `client_id` **is** an
HTTPS URL pointing at a Client ID Metadata Document; the AS fetches and caches it.

```mermaid
flowchart TD
    Authorize["GET /connect/authorize<br/>client_id = https://claude.ai/…"] --> Intercept["CimdAuthorizationRequestHandler<br/>runs before OpenIddict's built-in client lookup"]
    Intercept --> Service["ClientIdMetadataService"]
    Service --> Cache{"cached?"}
    Cache -- yes --> Validate
    Cache -- no --> Fetch["fetch the metadata document over HTTPS"]
    Fetch --> Validate{"validate:<br/>client_id matches URL exactly<br/>https + path<br/>redirect_uris (loopback port-agnostic)"}
    Validate -- valid --> Ok["register client in memory → continue authorize"]
    Validate -- invalid --> Reject["invalid_client (never a 500)"]
```

### 3.3 Authorization code + PKCE, login, and consent

The interactive pages are served by the Next.js portal on the **same origin** as
the AS, which is why `/oauth/*` calls are relative and the OAuth cookie works.

```mermaid
sequenceDiagram
    autonumber
    participant C as AI client (Claude)
    participant AS as C# AS (/connect/*)
    participant P as OAuth pages
    participant U as User (browser)
    participant DB as PostgreSQL

    C->>AS: GET /connect/authorize?response_type=code<br/>&client_id=<CIMD url>&redirect_uri=…<br/>&code_challenge=…&code_challenge_method=S256<br/>&resource=…/t/acme-dental/mcp&scope=tools offline_access
    AS->>AS: resolve tenant from `resource` (RFC 8707) → acme-dental
    alt no interactive cookie
        AS-->>U: 302 → /oauth/login?tenant=acme-dental&returnUrl=…
        U->>P: email + password (FormData at submit — autofill-safe)
        P->>AS: POST /connect/login (+ /connect/login/mfa if MFA)
        AS->>DB: verify user inside acme-dental, set OAuth cookie
        P-->>U: navigate back to /connect/authorize
    end
    alt consent not yet granted
        AS-->>U: 302 → /oauth/consent?client_id=…&scope=…&returnUrl=…
        P->>AS: GET /connect/consent-info
        AS-->>P: client name · tenant name · scopes
        U->>P: Approve
        P->>AS: POST /connect/consent → INSERT McpConsent
        P-->>U: navigate back to /connect/authorize
    end
    AS-->>C: 302 redirect_uri?code=…&iss=<issuer>
    C->>AS: POST /connect/token (form-urlencoded)<br/>code + code_verifier
    AS-->>C: { access_token: RS256 JWT (at+jwt, kid mcp-signing-v1),<br/>refresh_token: encrypted JWT, expires_in: 900, scope }
    Note over C,AS: refresh tokens are single-use rotating<br/>(replay → 400 invalid_grant)
```

**Token shape** (the contract the gateway consumes):

| Claim | Value |
|---|---|
| `iss` | the public origin (`https://<origin>/`) |
| `aud` | `https://<origin>/t/{tenant}/mcp` — the RFC 8707 tenant binding |
| `scope` | `tools offline_access` (coarse, per tenant — v1) |
| `sub` | the tenant-scoped user id |
| header | `alg=RS256`, `typ=at+jwt`, `kid=mcp-signing-v1`, **no `enc`** (signed-only, gateway-friendly) |

The signing keys are published at `/.well-known/jwks`; refresh tokens stay
encrypted with a key the gateway never sees.

---

## 4. MCP gateway module (Python) — `apps/mcp-server`

### 4.1 Request lifecycle

One gateway, one low-level MCP `Server` per tenant, LRU-cached. Raw-ASGI
dispatch keeps the tenant app in the request path untouched.

```mermaid
flowchart TD
    Req["POST /t/{tenant}/mcp"] --> Health{"path = /health?"}
    Health -- yes --> Ok["200 healthy"]
    Health -- no --> Prm{"path = /.well-known/<br/>oauth-protected-resource/…?"}
    Prm -- yes --> PrmDoc["serve RFC 9728 metadata<br/>(resource + authorization_servers)"]
    Prm -- no --> Tenant["resolve tenant from URL<br/>+ LRU-cache its Server"]
    Tenant --> Mode{"MCP_AUTH_MODE"}
    Mode -- none --> Tools
    Mode -- oauth --> Verify["TokenVerifier:<br/>fetch JWKS (cache, refresh on unknown kid)<br/>verify RS256 · iss · exp<br/>aud == THIS tenant's resource URL"]
    Verify -- invalid --> Unauth["401 + RFC 9728 challenge"]
    Verify -- missing scope --> Forbidden["403 insufficient_scope"]
    Verify -- valid --> Method{"method"}
    Method -- tools/list --> List["map tenant manifests → Tools<br/>(cacheable, deterministic order)"]
    Method -- tools/call --> Call["resolve manifest → HttpExecutor"]
    Method -- other --> Err["405 / JSON-RPC error"]
```

### 4.2 `tools/call` — how a tool actually executes

```mermaid
sequenceDiagram
    autonumber
    participant C as AI client
    participant G as MCP gateway
    participant L as ManifestLoader
    participant P as C# platform
    participant X as HttpExecutor
    participant S as SMB backend (stub)

    C->>G: tools/call book_appointment {patient_name, date, time, service_type}
    G->>L: manifest for (tenant, tool)
    alt cache miss / TTL expired
        L->>P: GET /internal/gateway/manifests?tenant=acme-dental<br/>X-Gateway-Api-Key: …
        P-->>L: tenant catalog (or 404 unknown / 403 suspended)
    end
    L-->>G: execution { method, url, auth, body_template, response_mapping }
    G->>X: execute(execution, arguments)
    X->>X: template ${arg} into path/body/query<br/>resolve credential_ref (vault:// → api key)<br/>rewrite host via SMB_HOST_OVERRIDES
    X->>S: POST /v1/appointments · X-Api-Key: …
    S-->>X: 201 { data: { appointment: {…} } }
    X->>X: apply response_mapping JSONPath ($.data.appointment)
    X-->>G: mapped structuredContent
    G-->>C: { isError: false, structuredContent: {…} }
    Note over X,S: platform 4xx/5xx or executor errors surface as<br/>MCP isError: true — never a crashed request
```

---

## 5. Web module (Next.js) — `apps/web` + `apps/platform-portal`

```mermaid
flowchart TB
    subgraph web["apps/web — business portal :3000"]
        Landing["/ · landing + health badge"]
        Login["/login · workspace picker + credentials (+TOTP)"]
        Register["/register · redeems ?invite="]
        Dash["/dashboard · widget CRUD, action pills"]
        Team["/dashboard/users · team + roles"]
        OLogin["/oauth/login · MCP flow login"]
        OConsent["/oauth/consent · approve / deny connector"]
    end

    subgraph portal["apps/platform-portal — superadmin :3001"]
        PLogin["/login"]
        Tenants["/tenants · CRUD + envelope assignment + suspend"]
        Envelopes["/envelopes · roles + actions templates"]
    end

    Login --> Dash
    Register --> Dash
    Dash --> Team
    Login --> OLogin
    OLogin --> OConsent
    PLogin --> Tenants
    PLogin --> Envelopes
    Envelopes -->|"template copied into TenantRoles"| Tenants
```

**Two conventions worth remembering:**

- Dashboard API calls carry `X-Tenant-Id` + a tenant JWT and use
  `NEXT_PUBLIC_API_URL` (absolutely addressed).
- `/oauth/*` calls are **relative** — those pages are served from the same
  origin as the AS by Caddy/tunnel, so the OAuth cookie and the authorize
  redirect stay on one origin.

---

## 6. End-to-end — the proven live journey

This is the flow that was verified working on 2026-09-12 against real
infrastructure (a cloudflared tunnel, the dockerized stack, and claude.ai).

```mermaid
flowchart TD
    A["SMB admin writes manifests<br/>/tenant/manifests (manage_tools)"] --> B[("ToolManifests<br/>acme-dental · 3 tools")]

    C["Claude web — add connector<br/>ORIGIN/t/acme-dental/mcp"] --> D["GET MCP endpoint, no token"]
    D --> E["401 + resource_metadata (RFC 9728)"]
    E --> F["AS discovery: CIMD supported + auth method none"]
    F --> G["GET /connect/authorize<br/>client_id = Claude's CIMD URL"]
    G --> H["AS fetches + caches Claude's<br/>client metadata document"]
    H --> I["302 → /oauth/login<br/>sign in as admin@tessera.com"]
    I --> J["302 → /oauth/consent → Approve"]
    J --> K["McpConsent recorded"]
    K --> L["302 back to Claude with ?code="]
    L --> M["POST /connect/token<br/>code + PKCE verifier"]
    M --> N["Signed RS256 JWT<br/>aud = /t/acme-dental/mcp · scope: tools offline_access"]

    N --> O["Claude: tools/list (Bearer)"]
    O --> P["Gateway verifies via JWKS + aud → 3 tools"]
    P --> Q["Claude: tools/call book_appointment"]
    Q --> R["Gateway loads manifest → HttpExecutor"]
    R --> S["Acme Dental backend<br/>(stub :9100 in dev)"]
    S --> T["Appointment booked<br/>then list → shows it → cancel"]

    B -.-> R
```

**Live evidence:** `tools/list` returned all three tools;
`tools/call book_appointment` booked an appointment with the credential
injected and the response mapped; a follow-up `list_appointments` showed it; a
direct gateway call with a real AS-issued RS256 token confirmed
`aud = <origin>/t/acme-dental/mcp`. See
[`docs/LIVE_TESTING_GUIDE.md`](LIVE_TESTING_GUIDE.md) §6 for the checklist.

---

## 7. Deployment topology (local / Docker)

Everything is served from **one origin** so the browser-facing OAuth flow and
the gateway share a hostname. The origin is either `https://tessera.local`
(local) or a public tunnel hostname (Claude web).

```mermaid
flowchart TB
    subgraph internet["Public"]
        Claude["AI assistant / browser"]
    end

    Tunnel["cloudflared quick tunnel<br/>https://&lt;name&gt;.trycloudflare.com → localhost:80"]

    subgraph compose["docker compose (apps/platform)"]
        Caddy["caddy :80/:443<br/>single origin router"]
        Api["api :5000→8080<br/>AS + platform endpoints"]
        Gw["mcp-gateway :8000"]
        Stub["stub-backend :9100<br/>Acme Dental fixture"]
        Db[("db postgres:16 :5432")]
    end

    subgraph host["Host machine"]
        Portal["Next.js portal :3000<br/>dashboard + /oauth/*"]
        PP["platform-portal :3001"]
    end

    Claude --> Tunnel --> Caddy
    Caddy -->|"/t/* · /.well-known/oauth-protected-resource/*"| Gw
    Caddy -->|"/connect/* · /auth/* · /tenant/* · /admin/* · /health · /.well-known/openid-configuration · /.well-known/jwks"| Api
    Caddy -->|"everything else (/, /oauth/*)"| Portal
    Api --> Db
    Gw -->|"manifests"| Api
    Gw -->|"tool calls"| Stub
    PP --> Api
```

| Service | Local address | Notes |
|---|---|---|
| Business portal | `http://localhost:3000` | Runs on the host, not in compose (live rebuilds). |
| Platform portal | `http://localhost:3001` | Superadmin; no tenant header. |
| Platform API | `http://localhost:5000` (docker) / `:5085` (`dotnet run`) | Requires `--no-launch-profile` to honour `ASPNETCORE_URLS`. |
| MCP gateway | `http://localhost:8000` | `MCP_AUTH_MODE` defaults to `oauth`. |
| Acme Dental stub | `http://localhost:9100` | Fixture backend; in-memory, resets on restart. |
| PostgreSQL | `localhost:5432` | `tessera_platform`; `pgdata` volume. |

Driver script: `./scripts/start-claude-web.sh` starts the tunnel, writes
`ORIGIN`, brings the stack up, starts the portal, and verifies the whole OAuth
surface before printing the connector URL. `--stop` tears it all down.

---

## 8. What is deliberately not built yet

Kept here so the diagrams above are not mistaken for the finished product.

| Boundary | Current state |
|---|---|
| **End-user (Jane) identity** | The AS authenticates the **tenant admin** for their own tenant. Per-patient/per-user tool authorization is not modelled; `required_scopes` on manifests are not enforced (the coarse `tools` scope is). |
| **Per-tool scopes / step-up** | Coarse per-tenant `tools` + `offline_access` only. |
| **Credential vault** | Dev resolves `vault://` refs from an env map. Production needs the encrypted-at-rest store + KMS. |
| **Manifest versioning** | No defined behaviour yet for cached tools / issued tokens / consent when a manifest changes. |
| **Identity federation** | SMB-as-IdP (SAML/OAuth) is a later upgrade; today it's a Tessera-hosted mirror account. |
| **Production hosting** | Local TLS via Caddy + a tunnel; no deployed environment, CI, or observability sink. |
| **ChatGPT** | Requires a Business/Edu workspace for custom connectors; untested. |
