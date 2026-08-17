# Tessera — Architecture

> **Last verified against the code**: 2026-08-17
> **Read this first.** This document is the single source of truth for how the
> system fits together. If something here disagrees with what you see in the
> code, the code is newer — update this file.

---

## 1. Overview

Tessera is a **multi-tenant SaaS platform**: a C# "platform" service owns the
cross-cutting concerns (authentication, authorization, tenant context, roles &
actions) and exposes them through a JSON API consumed by **two Next.js
frontends**.

```mermaid
flowchart LR
    BU[Business users] -->|register / login / widgets / team| WEB["apps/web · Business portal :3000"]
    SA[Platform admins] -->|manage tenants / envelopes| PP["apps/platform-portal · Superadmin portal :3001"]
    WEB -->|HTTP + X-Tenant-Id + JWT| API["Tessera.Platform.Api"]
    PP -->|HTTP + SuperAdmin JWT| API
    API --> DB[("PostgreSQL 16<br/>shared DB + tenant_id column")]
```

**Key rule**: the C# platform is the **source of truth** for auth, authz,
tenant context, roles, and (eventually) action enforcement. Frontends consume
its APIs; they never re-implement these concerns.

### The two portals

| Portal | App | Port | Audience | Auth | What it does |
|---|---|---|---|---|---|
| **Business portal** | `apps/web` | 3000 | Workspace users | Tenant JWT + `X-Tenant-Id` header | Register/login per workspace, widget CRUD, Team management (admins) |
| **Platform portal** | `apps/platform-portal` | 3001 | Platform admins (superadmins) | `SuperAdmin` JWT, **no** tenant header | Create/edit/delete tenants, create/edit/delete envelopes (roles + actions) |

---

## 2. Repository layout

```
Tessera/
├── Directory.Build.props            # Shared MSBuild props (net10.0, Nullable, ImplicitUsings)
├── .dockerignore                    # Excludes bin/obj, project-management/, etc. from Docker builds
├── .gitignore                       # dotnet gitignore (+ .env)
├── readme.md                        # Repo scaffolding checklist (roadmap for the repo itself)
├── docs/
│   ├── ARCHITECTURE.md              # ← you are here (single source of truth)
│   └── TABLES.md                    # database schema reference — every table, column, index
├── project-management/              # roadmap · tasks · decisions · learning-log · progress
├── infra/                           # placeholder (cloud provisioning — not yet used)
├── scripts/                         # placeholder (not yet used)
├── .claude/AGENTS.md                # Claude Code agent rules
└── apps/
    ├── web/                         # Next.js 16 business (tenant) portal — :3000
    ├── platform-portal/             # Next.js 16 superadmin portal — :3001
    └── platform/                    # C# platform service (this doc's focus)
        ├── Dockerfile               # Multi-stage .NET 10 build (SDK → publish → aspnet runtime)
        ├── docker-compose.yml       # api (:5000) + PostgreSQL 16 (:5432), pgdata volume
        ├── Tessera.Platform.slnx    # Solution (Api, Domain, Observability)
        ├── Guide.md                 # Operational guide: run + test instructions
        └── src/
            ├── Tessera.Platform.Api/           # ASP.NET Core host (endpoints, middleware, services, data)
            ├── Tessera.Platform.Domain/        # Plain class library: domain models + constants
            └── Tessera.Platform.Observability/ # Class library: shared middleware (RequestLogging)
```

---

## 3. Glossary — read this to avoid blunders

These are the concepts that have caused confusion before. Get them right.

| Term | Meaning |
|---|---|
| **Tenant** | A workspace/company that uses the platform. A row in the `Tenants` table. |
| **`Tenant.Id`** | **Internal** database key, e.g. `"alpha"`. Stored on every tenant-scoped row (`Users.TenantId`, `Widgets.TenantId`, ...). Chosen by Finbuckle seed for built-ins; for superadmin-created tenants, `Id == Identifier`. |
| **`Tenant.Identifier`** | **External** lookup key, e.g. `"alpha-corp"`. This is what clients send in the `X-Tenant-Id` header. **`Id` and `Identifier` are different values for the seeded tenants.** |
| **`X-Tenant-Id` header** | Required on every tenant-scoped request (except `/health`, `/openapi`, `/admin`, `/tenants`). Contains the **identifier**, e.g. `alpha-corp`. |
| **Widget** | **Placeholder demo resource** — a "todo" equivalent used to prove tenant isolation, auth, and RBAC work end-to-end. **Not a product feature.** Its entire tenant-isolation mechanism is Finbuckle (see §5). |
| **Envelope** | A bundle of **roles + their actions**, created by a superadmin and assigned to a tenant. **Template** semantics: assigning an envelope *copies* its roles into the tenant's own role set; later envelope edits don't cascade. |
| **AppRole** | A role **inside an envelope** (`Envelopes 1—n AppRoles`) — the template definition. Has a `Name` and an `Actions` list (`text[]`). |
| **TenantRole** | A **tenant-owned copy** of a template role (`TenantRoles` table). Tenants CRUD these and assign them to users via `User.RoleId`. Copy happens at assignment; no auto-sync. |
| **Action** | A named Tessera capability (e.g. `view_widgets`, `create_widget`, `edit_widget`, `delete_widget`, `manage_users`). **Enforced server-side** by `ActionChecks` (see §12). |
| **User** | A tenant-scoped user (`Users` table): bcrypt-hashed password, `RoleId` (FK → `TenantRoles`), `TenantId`. Auth via `/auth/*`. |
| **AdminUser** | A **platform-level superadmin** (`AdminUsers` table): no tenant binding, bcrypt hash. Auth via `/admin/auth/login`. `admin@…` (tenant) vs `superadmin@…` (platform) are **different tables and accounts**. |
| **Roles constants** | Built-ins in `User.cs`: `Admin`, `User`, `SuperAdmin`. Only `SuperAdmin` is used as a claim-based policy (`SuperAdminOnly`); tenant authorization is **action-based**, not name-based. |
| **Envelope roles** | Roles defined per-envelope (e.g. `Manager`, `Editor`). Copied into a tenant's `TenantRoles` at assignment (or the built-ins `Admin`/`User` when no envelope is assigned); the tenant owns the copies. |
| **Tenant hierarchy** | Three tiers: **Superadmin** (platform-managed role — all actions, not renameable/editable/deletable, granted to the first invite redeemed in a workspace), **Admin** (tenant-created assistants, `manage_users`), and **User**/custom roles. Onboarding is **invite-only** — open self-registration was removed. |
| **Tenant admin** | A `User` whose role includes the `manage_users` action. Can manage roles, users, and invitations via `/tenant/*`. |
| **Superadmin** | An `AdminUser`. Can manage tenants + envelopes via `/admin/*`. |

---

## 4. Tech stack

| Layer | Tech | Version |
|---|---|---|
| Backend runtime | .NET (ASP.NET Core, minimal APIs) | `net10.0` / SDK 10.0.302 |
| ORM | EF Core (`Microsoft.EntityFrameworkCore`) + Npgsql | 10.0.10 / 10.0.0 |
| Multi-tenancy | **Finbuckle.MultiTenant** (+ AspNetCore + EntityFrameworkCore + Abstractions) | 10.1.2 |
| Auth | JWT (`Microsoft.AspNetCore.Authentication.JwtBearer`), BCrypt (`BCrypt.Net-Next`) | 10.0.10 / 4.0.3 |
| Logging | Serilog (console sink) | 10.0.0 |
| Database (prod/dev) | PostgreSQL | 16 (`postgres:16-alpine`) |
| Database (fallback dev) | EF Core InMemory provider | 10.0.10 |
| Frontends | Next.js (App Router, Turbopack) · React · TypeScript strict · Tailwind CSS v4 | 16.3.0 / 19.2.8 / 5 / 4 |

---

## 5. The platform service (`apps/platform`)

### 5.1 Project structure

```mermaid
flowchart LR
    API["Tessera.Platform.Api<br/>(ASP.NET Core host)"] --> DOM["Tessera.Platform.Domain<br/>(models + constants)"]
    API --> OBS["Tessera.Platform.Observability<br/>(RequestLoggingMiddleware)"]
```

- **`Tessera.Platform.Domain`** — plain class library, zero ASP.NET dependencies. Holds every model and the `Roles` / `ActionCatalog` constants. The API host references it.
- **`Tessera.Platform.Observability`** — class library using a `FrameworkReference` to `Microsoft.AspNetCore.App` (so it can use `HttpContext` without the Web SDK). Currently contains only `RequestLoggingMiddleware`.
- **`Tessera.Platform.Api`** — the host: `Program.cs` wiring, `Endpoints/`, `Middleware/`, `Services/`, `Data/`, `Migrations/`, config files.

### 5.2 Data model (PostgreSQL schema)

> 📄 **Full table-by-table reference** (columns, types, indexes, FKs): see
> **[`docs/TABLES.md`](TABLES.md)**.

Nine tables. Tenant-scoped tables (`Users`, `Widgets`, `RefreshTokens`, `TenantRoles`, `Invitations`) carry a
`TenantId` and are marked `IsMultiTenant()` → Finbuckle adds a global query
filter. Platform-level tables (`Tenants`, `Envelopes`, `AppRoles`, `AdminUsers`)
are **not** multi-tenant.

```mermaid
erDiagram
    Tenant ||--o| Envelope : "assigned"
    Envelope ||--o{ AppRole : "contains"
    Tenant ||--o{ TenantRole : "owns (copied from envelope)"
    TenantRole ||--o{ User : "assigned (RoleId)"
    Tenant ||--o{ User : "has"
    Tenant ||--o{ Widget : "owns"
    Tenant ||--o{ RefreshToken : "has"
    User ||--o{ RefreshToken : "issued to"
    Tenant ||--o{ Invitation : "sent"
    Invitation }o--|| TenantRole : "grants"

    Tenant {
        string Id PK "internal key, e.g. 'alpha'"
        string Identifier UK "external, e.g. 'alpha-corp' (X-Tenant-Id)"
        string Name
        string ConnectionString "unused"
        guid EnvelopeId FK "assigned template (nullable)"
        string Status "Active | Suspended"
        bool IsDeleted "soft delete"
        datetime CreatedAt
    }
    Envelope {
        guid Id PK
        string Name UK
        string Description
        datetime CreatedAt
    }
    AppRole {
        guid Id PK
        guid EnvelopeId FK "cascade delete"
        string Name "unique per envelope"
        string Actions "text[] — template, copied to tenants"
    }
    TenantRole {
        guid Id PK
        string TenantId FK "multi-tenant filter"
        guid EnvelopeRoleId "source AppRole (nullable, informational)"
        string Name "unique per tenant"
        string Actions "text[] — enforced server-side"
        datetime CreatedAt
    }
    User {
        guid Id PK
        string Email
        string PasswordHash "bcrypt"
        guid RoleId FK "→ TenantRoles.Id (action-based authz)"
        string TenantId FK "multi-tenant filter"
        int TokenVersion "JWT token_version claim (A5)"
        bool IsDeleted "soft delete"
        bool EmailVerified "C2"
        string MfaSecret "TOTP, nullable"
        datetime CreatedAt
    }
    AdminUser {
        guid Id PK
        string Email UK
        string PasswordHash "bcrypt"
        datetime CreatedAt
    }
    Widget {
        guid Id PK
        string TenantId FK "multi-tenant filter"
        string Name
        string Description
        datetime CreatedAt
    }
    RefreshToken {
        guid Id PK
        guid UserId FK
        string Token "64 random bytes, single use"
        guid FamilyId "rotation family — reuse detection (C1)"
        datetime ExpiresAt "7 days"
        bool IsRevoked
        string TenantId FK "multi-tenant filter"
        datetime CreatedAt
    }
    Invitation {
        guid Id PK
        string TenantId FK "multi-tenant filter"
        string Email "invitee — token binds to it"
        guid RoleId FK "→ TenantRoles.Id (role granted)"
        string TokenHash "SHA-256 of raw token (C2)"
        datetime ExpiresAt "72 h"
        datetime UsedAt "single-use"
        datetime CreatedAt
    }
```

**Deletion rules** (admin endpoints):
- **Delete tenant** → hard-deletes the tenant's `Widgets`, `RefreshTokens`, and `Invitations`; **soft-deletes** its `Users` (`IsDeleted`, `RoleId = NULL`) and the `Tenant` row itself (B1/B4). PostgreSQL uses raw SQL (bypasses `EnforceMultiTenant`); InMemory uses `MultiTenantDbContext.Create<TContext,TTenantInfo>` bound to the deleted tenant.
- **Delete envelope** → first **unassigns** it from all tenants (`Tenant.EnvelopeId = null`), then deletes the envelope and its roles (cascade). Tenant role copies are untouched.

Migrations: a single `InitialCreate` migration holds the entire current schema (the dev migration history was squashed). Applied on startup gated by `Database:AutoMigrate` and `IsRelational()`.

### 5.3 Middleware pipeline (exact order)

Registered in `Program.cs` in this order — order is load-bearing:

```mermaid
flowchart LR
    REQ[HTTP request] --> HTTPS[HttpsRedirection]
    HTTPS --> EX[1. ExceptionHandlingMiddleware]
    EX --> RL[2. RequestLoggingMiddleware]
    RL --> CORS[3. CORS 'WebApp']
    CORS --> RATE[4. RateLimiter 'auth']
    RATE --> TV[5. TenantValidationMiddleware]
    TV --> MT[6. UseMultiTenant]
    MT --> SUS[7. TenantSuspensionMiddleware]
    SUS --> AU[8. UseAuthentication]
    AU --> TC[9. TenantClaimValidationMiddleware]
    TC --> TVM[10. TokenVersionValidationMiddleware]
    TVM --> AZ[11. UseAuthorization]
    AZ --> EP[Endpoint]
```

| # | Middleware | What it does | Fails with |
|---|---|---|---|
| 0 | `UseHttpsRedirection` | Redirects HTTP → HTTPS when configured (dev: no-op over plain http). | — |
| 1 | `ExceptionHandlingMiddleware` | Wraps everything after it in try/catch; maps exceptions → HTTP status + `ApiErrorResponse` JSON (see §11). | 400/404/403/499/500/502/501 |
| 2 | `RequestLoggingMiddleware` (Observability) | Logs method, path, status, duration for every request. | — |
| 3 | CORS `WebApp` | Origins come from `Cors:AllowedOrigins` (defaults: `http(s)://localhost:3000` and `:3001`). Runs **before** tenant validation so browser preflights (OPTIONS, no custom headers) aren't rejected. | — |
| 4 | `UseRateLimiter()` | Fixed-window per-IP rate limiter on `/auth/*` endpoints (C4). Limits live in `RateLimiting:*`; 429 when exceeded. | **429** |
| 5 | `TenantValidationMiddleware` | Requires `X-Tenant-Id` header on all paths **except** `/health`, `/ready`, `/openapi`, `/admin`, `/tenants`. | **400** (missing header) |
| 6 | `UseMultiTenant()` | Finbuckle: resolves the tenant from the header via the **`DbTenantStore`** and sets the per-request tenant context. | 500 if store fails |
| 7 | `TenantSuspensionMiddleware` | Blocks tenant-scoped requests when the resolved tenant's `Status` is `Suspended` (B3). Platform paths are exempt even with a stray header. | **403** (suspended) |
| 8 | `UseAuthentication()` | Validates the Bearer JWT (issuer, audience, lifetime, HMAC-SHA256 signature, zero clock skew). | **401** (missing/invalid token) |
| 9 | `TenantClaimValidationMiddleware` | For authenticated requests: compares the JWT's **`tenant_identifier`** claim to the `X-Tenant-Id` header. Blocks cross-tenant token reuse. Superadmin JWTs have no tenant claim → skipped. | **403** (mismatch) |
| 10 | `TokenVersionValidationMiddleware` | For authenticated requests: compares the JWT's **`token_version`** claim to the user's current `TokenVersion` (A5). Demoted/deleted users lose access immediately. Superadmin JWTs carry no claim → skipped; auth/recovery endpoints (`/auth/login`, `/auth/refresh`, …) are exempt. | **401** (stale token) |
| 11 | `UseAuthorization()` | Enforces the `SuperAdminOnly` policy (role `SuperAdmin`) on `/admin/*`. Tenant authorization is **action-based** inside handlers (`ActionChecks`). | **403** (insufficient) |

### 5.4 Tenant resolution — how a request gets its tenant

```mermaid
sequenceDiagram
    participant B as Browser (apps/web)
    participant P as Platform API
    participant S as DbTenantStore
    participant DB as PostgreSQL

    B->>P: GET /widgets<br/>X-Tenant-Id: alpha-corp, Bearer <JWT>
    Note over P: Middleware 1–4 pass (header present)
    P->>S: UseMultiTenant → GetByIdentifierAsync("alpha-corp")
    S->>DB: SELECT * FROM "Tenants" WHERE "Identifier" = 'alpha-corp'
    S-->>P: Tenant { Id: "alpha", Identifier: "alpha-corp", EnvelopeId, ... }
    Note over P: Tenant context bound to this request (AsyncLocal)
    P->>DB: SELECT * FROM "Widgets" WHERE "TenantId" = 'alpha'  ← global query filter added automatically
    DB-->>P: Alpha's widgets only
    P-->>B: 200 [widgets]
```

**Why the database-backed store?** The first Finbuckle setup used
`WithInMemoryStore` with hardcoded tenants — superadmins couldn't add tenants at
runtime. `DbTenantStore` (`Data/DbTenantStore.cs`) implements
`IMultiTenantStore<Tenant>` against the `Tenants` table
(`GetAsync`/`GetByIdentifierAsync`/`GetByIdAsync`/`GetAllAsync`/`AddAsync`/`UpdateAsync`/`RemoveAsync`)
using scoped `AppDbContext` instances. Registered via
`.WithStore<DbTenantStore>(ServiceLifetime.Singleton)`.

**Why `MultiTenantDbContext`?** `AppDbContext` inherits Finbuckle's
`MultiTenantDbContext`:
- entities configured with `entity.IsMultiTenant()` get a `TenantId` column + a **global query filter** scoped to the request's tenant (SQL: `WHERE "TenantId" = @current`);
- on `SaveChanges`, Finbuckle's **`EnforceMultiTenant`** auto-sets `TenantId` on inserts and throws on mismatches. ⚠️ It requires a tenant context — see §14 gotchas.

---

## 6. Authentication & authorization

### 6.1 Two token kinds

| | Tenant user JWT | Superadmin JWT |
|---|---|---|
| Issued by | `POST /auth/register`, `/auth/login` (+ MFA step), `/auth/refresh` | `POST /admin/auth/login` |
| Claims | `sub`, `email`, `ClaimTypes.Role` (role **name**, informational), `role_id` (→ `TenantRoles.Id`), `token_version`, `tenant_id` (internal, e.g. `"alpha"`), `tenant_identifier` (external, e.g. `"alpha-corp"`), `jti`, `iat` | `sub`, `email`, `role = SuperAdmin`, `jti`, `iat` — **no tenant claims** |
| Lifetime | 15 minutes (`Jwt:AccessTokenExpirationMinutes`) | 4 hours (`Jwt:AdminAccessTokenExpirationHours`) |
| Refresh | Yes — 64 random bytes, 7 days, stored in `RefreshTokens`, rotated per use with **family reuse detection** (C1) | **No** — portal re-logins on expiry |
| Signing | HMAC-SHA256, symmetric key `Jwt:SecretKey` (env `Jwt__SecretKey`; startup fails if unset — C5) | same |
| Enforced by | Action-based checks (`ActionChecks`) + `TenantClaimValidationMiddleware` + `TokenVersionValidationMiddleware` | `SuperAdminOnly` policy |

Also issued: a short-lived **MFA token** (5 min, claim `mfa=true`) after the password step of an MFA-protected login; it is exchanged at `POST /auth/mfa` for a normal token pair.

The client keeps tokens in `localStorage` (`tessera.auth` for business portal,
`tessera.admin` for platform portal) — **not** httpOnly cookies.

### 6.2 Tenant user flow: register → login → refresh

```mermaid
sequenceDiagram
    participant U as Tenant user
    participant W as apps/web (business portal)
    participant P as Platform API
    participant DB as PostgreSQL

    U->>W: Open emailed invite link (register?invite=…&tenant=…)
    W->>P: POST /auth/register + X-Tenant-Id (invite-only)
    P->>DB: invite valid? (email match, unused, unexpired)
    P->>DB: INSERT User (bcrypt; RoleId from invite — first redemption<br/>in the workspace becomes its platform-managed Superadmin)
    P->>DB: INSERT RefreshToken (64 random bytes, 7d, new FamilyId)
    P-->>W: 201 { accessToken (15m), refreshToken }
    W->>W: save tokens + tenantId to localStorage, redirect /dashboard

    U->>W: Log in
    W->>P: POST /auth/login + X-Tenant-Id
    P->>DB: find user by email (tenant-scoped), bcrypt verify
    alt MFA enabled
        P-->>W: 200 { mfaRequired: true, mfaToken (5m) }
        U->>W: Enter TOTP code
        W->>P: POST /auth/mfa { mfaToken, code }
        P-->>W: 200 { accessToken, refreshToken }
    else
        P-->>W: 200 { accessToken, refreshToken }
    end

    Note over W,P: Any API call returning 401 → POST /auth/refresh (rotates family)<br/>→ new pair → retry original call once. (lib/api.ts)
```

### 6.3 Superadmin flow: envelopes → tenants

```mermaid
sequenceDiagram
    participant SA as Platform admin
    participant AP as apps/platform-portal
    participant P as Platform API
    participant DB as PostgreSQL

    SA->>AP: Log in
    AP->>P: POST /admin/auth/login (no X-Tenant-Id)
    P->>DB: verify AdminUser (bcrypt)
    P-->>AP: 200 { accessToken (SuperAdmin, 12h) }

    SA->>AP: Create envelope (name, roles[], each with actions[])
    AP->>P: POST /admin/envelopes (Bearer)
    P->>DB: INSERT Envelope + AppRoles (cascade)
    P-->>AP: 201

    SA->>AP: Create tenant + assign envelope
    AP->>P: POST /admin/tenants { identifier, name, envelopeId }
    P->>DB: INSERT Tenant (Id = identifier)
    P-->>AP: 201

    SA->>AP: Assign envelope later
    AP->>P: PUT /admin/tenants/{id} { envelopeId }
    P-->>AP: 200
```

### 6.4 Cross-tenant token attack (blocked)

A valid JWT for tenant A is useless against tenant B: `TenantClaimValidationMiddleware`
compares the JWT's **`tenant_identifier`** claim (external name, e.g.
`alpha-corp`) with the header. ⚠️ It deliberately does **not** compare the
`tenant_id` claim (internal id, e.g. `alpha`) — that was a real bug fixed in
Session 8; the two never matched.

```mermaid
flowchart TD
    REQ[Request: Bearer &lt;Alpha JWT&gt;, X-Tenant-Id: beta-industries] --> AUTHN{Authenticated?}
    AUTHN -- no --> PASS[pass through → authz returns 401 on protected routes]
    AUTHN -- yes --> CLAIM{token 'tenant_identifier'<br/>== header 'X-Tenant-Id'?}
    CLAIM -- equal --> PASS2[continue to authorization]
    CLAIM -- different --> FORBIDDEN[403 Forbidden<br/>'tenant does not match']
```

---

## 7. API surface (complete)

All tenant-scoped endpoints require `X-Tenant-Id`. All responses use JSON.
Errors use `ApiErrorResponse` (see §11).

| Method | Path | Tenant header | Auth | Purpose |
|---|---|---|---|---|
| GET | `/health` | no | public | Liveness: `{ status: "healthy", timestamp }` (HealthBadge) |
| GET | `/ready` | no | public | Readiness: checks DB (`CanConnectAsync`), 503 when unreachable |
| GET | `/tenants` | no | public | `[{ id: identifier, name }]` for the workspace picker (non-deleted only) |
| GET | `/openapi` | no | public | OpenAPI JSON (dev only) |
| POST | `/auth/register` | **yes** | public | Redeem an invitation (invite-only): validates the emailed token (email match, unused, unexpired) and creates the user with the invite's role — the first redemption in a workspace becomes its Superadmin. Rate-limited |
| POST | `/auth/login` | **yes** | public | Verify credentials → token pair, or `{ mfaRequired, mfaToken }` when MFA is enabled. Generic errors (C3). Rate-limited |
| POST | `/auth/mfa` | **yes** | public | Complete MFA login with TOTP `code` → token pair. Rate-limited |
| POST | `/auth/refresh` | **yes** | public | Rotate refresh token → new pair; replay of a used token revokes the family (C1). Rate-limited |
| POST | `/auth/logout` | **yes** | public | Server-side revoke of the presented refresh token's family. Rate-limited |
| POST | `/auth/change-password` | yes | tenant user | Change password; bumps `token_version`, revokes all sessions |
| POST | `/auth/forgot-password` | **yes** | public | Email a reset link (message-only reply). Rate-limited |
| POST | `/auth/reset-password` | **yes** | public | Set a new password with the emailed token. Rate-limited |
| POST | `/auth/verify-email` | **yes** | public | Verify an email address with the emailed token (C2). Rate-limited |
| POST | `/auth/mfa/enroll` | yes | tenant user | Generate a TOTP secret + otpauth URI |
| POST | `/auth/mfa/verify` | yes | tenant user | Confirm a code → enable MFA (bumps `token_version`) |
| POST | `/auth/mfa/disable` | yes | tenant user | Disable MFA with a current code |
| POST | `/auth/promote` | yes | `manage_users` | Legacy: set a user's role to Admin |
| GET | `/widgets` | yes | `view_widgets` | List tenant's widgets (filtered) |
| GET | `/widgets/{id:guid}` | yes | `view_widgets` | Get one widget |
| POST | `/widgets` | yes | `create_widget` | Create widget |
| PUT | `/widgets/{id:guid}` | yes | `edit_widget` | Update widget |
| DELETE | `/widgets/{id:guid}` | yes | `delete_widget` | Delete widget |
| GET | `/tenant/me` | yes | tenant user | `{ id, email, role, roleId, actions[], tenantId, tenantIdentifier }` — actions resolved live from `RoleId` |
| GET | `/tenant/envelope` | yes | tenant user | The workspace's role set (kept as "envelope" for backward compatibility) |
| GET/POST | `/tenant/roles` | yes | list: any · write: `manage_users` | List / create tenant roles (actions validated against `ActionCatalog`) |
| PUT/DELETE | `/tenant/roles/{id:guid}` | yes | `manage_users` | Update / delete a role; delete is **blocked while users are assigned** (400) |
| GET/POST | `/tenant/invites` | yes | `manage_users` | List / create role-bound invitations (emailed link, 72 h, single-use) |
| GET | `/tenant/users` | yes | `manage_users` | List workspace users (no password hash) |
| POST | `/tenant/users` | yes | `manage_users` | Add user `{ email, password, role }` — role must exist in the tenant's role set |
| PUT | `/tenant/users/{id:guid}` | yes | `manage_users` | Change a user's role (cannot change your own; bumps `token_version`, revokes sessions) |
| DELETE | `/tenant/users/{id:guid}` | yes | `manage_users` | Remove a user (cannot remove yourself; soft delete) |
| POST | `/admin/auth/login` | no | public | Superadmin login → `SuperAdmin` JWT (4 h) |
| GET/POST | `/admin/tenants` | no | `SuperAdminOnly` | List / create tenants (envelope assignment copies template roles into `TenantRoles`) |
| POST | `/admin/tenants/{id}/invites` | no | `SuperAdminOnly` | Invite by email; the first redemption in a workspace becomes its Superadmin, later platform invites default to Admin |
| PUT/DELETE | `/admin/tenants/{id}` | no | `SuperAdminOnly` | Update / delete tenant (delete hard-deletes widgets/tokens/invites, soft-deletes users + tenant) |
| PUT | `/admin/tenants/{id}/status` | no | `SuperAdminOnly` | Suspend / reactivate a tenant (`Active` / `Suspended`) |
| GET/POST | `/admin/envelopes` | no | `SuperAdminOnly` | List / create envelope templates (roles + actions) |
| PUT/DELETE | `/admin/envelopes/{id:guid}` | no | `SuperAdminOnly` | Update / delete envelope (delete unassigns tenants first; tenant role copies untouched) |

**Guards worth knowing** (return 400 `{ error }`):
- Tenant identifier must match `^[a-z0-9-]+$` and be unique.
- Envelope must have ≥ 1 role; role names unique within the envelope.
- Role names unique per tenant; actions must come from `ActionCatalog`.
- `POST /tenant/users` validates email format, password ≥ 8 chars, role exists in the workspace.

---

## 8. Business portal (`apps/web`) — :3000

Next.js 16 App Router, TypeScript strict, Tailwind v4, coffee theme
(`globals.css` defines the `cream/beige/latte/caramel/mocha/roast/espresso`
palette).

| Route | File | Purpose |
|---|---|---|
| `/` | `app/page.tsx` | Landing page with `HealthBadge` (polls `GET /health`) + login/register CTAs |
| `/login` | `app/login/page.tsx` | Workspace login (tenant picker + credentials; TOTP code step when MFA is enabled) |
| `/register` | `app/register/page.tsx` | Workspace signup; reads the `?invite=` token from the email link to prefill email and redeem the invite |
| `/dashboard` | `app/dashboard/page.tsx` | Widget CRUD + role badge + "your role allows" action pills (`GET /tenant/me`) |
| `/dashboard/users` | `app/dashboard/users/page.tsx` | **Team** (Admin-only UI): list users, add user (role from envelope), change role, remove |

Shared pieces:
- `lib/api.ts` — single API client. Sends `X-Tenant-Id` on every call, reads tokens from `localStorage["tessera.auth"]`, decodes the JWT role (`roleFromToken`), and **auto-refreshes on 401** (retry once via `POST /auth/refresh`). `getTenants()` populates the workspace picker dynamically with a built-in fallback (`TENANTS`).
- `components/ui.tsx` — `Button`, `Card`, `Field`, `TextInput`, `TextArea`, `Alert`.
- `components/portal-header.tsx` — brand + nav (Widgets / Team) + user info + logout.
- `components/tenant-picker.tsx` — workspace `<select>` fed by `GET /tenants`.
- `components/health-badge.tsx` — "API online/offline" indicator.

---

## 9. Platform portal (`apps/platform-portal`) — :3001

Same stack/theme as the business portal, but for superadmins.

| Route | File | Purpose |
|---|---|---|
| `/` | `app/page.tsx` | Redirects to `/tenants` (logged in) or `/login` |
| `/login` | `app/login/page.tsx` | Superadmin login |
| `/tenants` | `app/tenants/page.tsx` | List/create/edit/delete tenants, assign envelope |
| `/envelopes` | `app/envelopes/page.tsx` | List/create/edit/delete envelopes; role + comma-separated actions editor |

`lib/api.ts` differs from the business portal: **no `X-Tenant-Id` header**,
tokens in `localStorage["tessera.admin"]`, and **no auto-refresh** (12 h token;
on 401 the pages redirect to `/login`).

---

## 10. Running it

### 10.1 Docker Compose (the canonical way — PostgreSQL)

```mermaid
flowchart LR
    subgraph localhost
        W["apps/web :3000<br/>NEXT_PUBLIC_API_URL=http://localhost:5000"] --> API
        P["apps/platform-portal :3001<br/>NEXT_PUBLIC_API_URL=http://localhost:5000"] --> API
    end
    subgraph compose["docker compose (apps/platform)"]
        API["api :5000 → container :8080<br/>ASPNETCORE_ENVIRONMENT=Docker"]
        API --> DB[("db postgres:16 :5432<br/>database tessera_platform")]
    end
```

```bash
cd apps/platform
docker compose up -d --build    # builds .NET image, starts api + db (pgdata volume persists)
```

Then, in two terminals:
```bash
cd apps/web && npm run dev            # business portal → http://localhost:3000
cd apps/platform-portal && npm run dev  # platform portal → http://localhost:3001
```

The `.env.local` files in both apps currently set `NEXT_PUBLIC_API_URL=http://localhost:5000`
(the Docker API). Without them, both apps default to `http://localhost:5085`.

### 10.2 Local without Docker (InMemory fallback)

```bash
cd apps/platform
dotnet run --project src/Tessera.Platform.Api   # http://localhost:5085
```

With no connection string configured (`appsettings.json` has `""`), the API
uses the **EF Core InMemory provider** — data is ephemeral (resets on restart)
and behavior differs slightly (see gotchas).

### 10.3 Seeded data

| Seed | On InMemory | On PostgreSQL | Source |
|---|---|---|---|
| `Standard` envelope (roles `Admin`/`Manager`/`User` + actions) | ✅ | ✅ | `SeedPlatformDataAsync` (`Program.cs`) |
| Tenants `alpha-corp` (Id `alpha`), `beta-industries` (Id `beta`) | ✅ | ✅ | `SeedPlatformDataAsync` |
| Standard envelope assigned to tenants without one | ✅ | ✅ | `SeedPlatformDataAsync` |
| Superadmin `superadmin@tessera.com` / `Admin123!` (`AdminUsers`) | ✅ | ✅ | `SeedPlatformDataAsync` |
| Tenant admin `admin@tessera.com` / `Admin123!` per tenant (`Users`, Admin role) | ❌ | ✅ | Raw SQL in `Program.cs` (Postgres only — see gotcha #4) |
| Tenant `Superadmin` system role (all actions, `IsSystem`) | ✅ (lazily on first invite) | ✅ | `TenantRoleSeeder.EnsureSuperadmin` + raw SQL / migration backfill |

Config lives in `appsettings.json` (`SeedAdmin`, `SeedSuperAdmin`, `Jwt`
issuer/audience/expiries, `Cors:AllowedOrigins`, `RateLimiting:*`, `Database:AutoMigrate`)
plus `appsettings.Development.json` (dev `Jwt:SecretKey`). Production sets
`Jwt__SecretKey` (startup fails fast if unset — C5) and overrides the
connection string via `ConnectionStrings__DefaultConnection`; the Docker
environment does the latter through `appsettings.Docker.json`.

---

## 11. Error contract

Every error response has this shape (from `ApiErrorResponse`):

```json
{
  "statusCode": 404,
  "message": "The requested resource was not found.",
  "details": null,
  "traceId": "0HM8K3T6G1V7A",
  "timestamp": "2026-07-27T12:00:00Z"
}
```

- `details` populated with the stack trace **only in Development**.
- `traceId` = `Activity.Current?.Id ?? context.TraceIdentifier` — correlates error ↔ log line.
- Exception → status mapping (`ExceptionHandlingMiddleware.MapException`):

| Exception | HTTP |
|---|---|
| `OperationCanceledException` / `TaskCanceledException` | 499 (client disconnected) |
| `KeyNotFoundException` / `FileNotFoundException` | 404 |
| `ArgumentException` / `InvalidOperationException` | 400 (message echoed) |
| `UnauthorizedAccessException` | 403 |
| `NotImplementedException` | 501 |
| `HttpRequestException` | 502 |
| anything else | 500 |

---

## 12. Security model — the four isolation layers

| Layer | Mechanism | Enforced where |
|---|---|---|
| 🆔 **Tenant resolution** | `X-Tenant-Id` header → `DbTenantStore` → Finbuckle context | `UseMultiTenant()` |
| 🗄️ **Data isolation** | `IsMultiTenant()` on `User`/`Widget`/`RefreshToken`/`TenantRole`/`Invitation` → global query filters (`WHERE TenantId = …`) | DB queries; also `EnforceMultiTenant` on save |
| 🔐 **Token isolation** | JWT `tenant_identifier` claim must equal header | `TenantClaimValidationMiddleware` |
| 👑 **Permission isolation** | Actions resolved from the user's `TenantRole` at request time (plus the `SuperAdminOnly` policy for `/admin/*`) | `ActionChecks` in handlers · `UseAuthorization()` |
| ⏱️ **Session freshness** | JWT `token_version` must equal the user's current `TokenVersion` | `TokenVersionValidationMiddleware` |

Endpoints always use `FirstOrDefaultAsync`/`ToListAsync` (never `FindAsync`,
which bypasses query filters). The tenant admin seed uses `IgnoreQueryFilters()`
deliberately at startup.

---

## 13. Known issues & backlog

| Item | Status |
|---|---|
| **Audit trail** | No record of who changed which envelope/tenant/role when (D4) — P2. |
| **Envelope versioning** | Template history for platform-level audit (A6) — P2. |
| **Subdomain tenant resolution** | Only the `X-Tenant-Id` header strategy exists (B5) — P2. |
| **Superadmin MFA** | Tenant MFA is done; platform accounts still use password only (C6). |
| **Backups / PITR** | Postgres runs on a Docker volume with no backup story (E6). |
| **Observability sink** | Correlation IDs exist; no metrics/tracing sink wired (E5) — Phase 4. |
| **CI/CD** | No pipeline yet (E2) — Phase 7. |
| **Billing** | No plans/entitlements; suspension-on-failed-payment can reuse B3 (F1). |
| `NU1903` — `Microsoft.OpenApi` 2.0.0 vuln | Transitive; resolves with SDK/package update. |
| Tenant picker | Populated from public `GET /tenants` — reveals tenant names to anyone (acceptable at this stage). |

---

## 14. Gotchas — the blunder list

Read these before changing anything. Each one caused a real bug or confusion.

1. **`tenant_id` ≠ `tenant_identifier`.** Internal id (`alpha`) vs external identifier (`alpha-corp`). `TenantClaimValidationMiddleware` compares the **identifier** claim against the header. Users store the **id** in `User.TenantId`.
2. **Widgets are demo data**, not a product feature. They exist to prove Finbuckle's tenant isolation. Don't model product requirements on them.
3. **`FindAsync` bypasses query filters** — always use `FirstOrDefaultAsync` on tenant-scoped queries.
4. **`EnforceMultiTenant` needs a tenant context.** Saving `IsMultiTenant` entities (User/Widget/RefreshToken/TenantRole/Invitation) with no tenant context throws — that's why the tenant-admin/role seeds use raw SQL (Postgres) and why there's no seeded tenant admin on InMemory. Superadmin actions that must touch tenant data (envelope copy, tenant delete) use `MultiTenantDbContext.Create` bound to the target tenant.
5. **InMemory provider limitations**: no raw SQL (`ExecuteSqlRawAsync`), no `ExecuteDelete/ExecuteUpdate`, no database indexes/constraints. Tenant deletion therefore branches: raw SQL on Postgres, `MultiTenantDbContext.Create` bound-context on InMemory.
6. **`admin@tessera.com` ≠ `superadmin@tessera.com`.** Different tables (`Users` vs `AdminUsers`). The tenant admin only exists on Postgres.
7. **Ports**: `5000` = Docker API · `5085` = local `dotnet run` · `3000` = business portal · `3001` = platform portal. Both `.env.local` files currently point to `:5000`.
8. **Middleware order is load-bearing** (see §5.3): exception → logging → CORS → rate limiter → tenant validation → multi-tenant → suspension → authN → tenant-claim check → token-version check → authZ. CORS must run before tenant validation so preflights pass; `TokenVersionValidationMiddleware` must run after `UseAuthentication()`.
9. **CORS origins are config-driven** (`Cors:AllowedOrigins`) with `localhost:3000/:3001` defaults — a new frontend port only needs config, not a `Program.cs` change.
10. **Actions are enforced** (`ActionChecks`) for widgets, users, roles, invites, and promote — no endpoint trusts the role claim alone.
11. **`token_version` stale-token rejection is real.** Password changes, MFA changes, role changes, and deletion bump the version; old JWTs 401 on the next request. Auth/recovery endpoints are exempt so re-login always works.
12. **Test factories need isolated InMemory stores.** `WebApplicationFactory` merges config after `Program`'s top-level code, so provider choice, JWT options, and rate limits must read config lazily (or via `IConfigureOptions`); the InMemory store is keyed by `Database:InMemoryName`, which each test factory sets uniquely.
11. **The `Tenant` entity doubles as the Finbuckle `ITenantInfo`** and a DB row. Its `ConnectionString` property is currently unused (single shared DB).
12. **`.env.local` files are gitignored** — if you move machines, recreate them (see `README.md` / `Guide.md`).

---

## 15. Recorded decisions (see `project-management/decisions.md` for detail)

- **Multitenancy**: shared DB + `tenant_id` column via Finbuckle.MultiTenant.
- **Auth**: roll-our-own JWT (bcrypt + access/refresh tokens) — swap to WorkOS/Auth0 possible later.
- **Cloud target**: Fly.io (not yet deployed; `fly.toml` doesn't exist).
- **Solution structure**: one service, three class libraries by concern (Api / Domain / Observability).
- **Logging**: Serilog, console sink.
- **Exceptions**: global middleware (not `IExceptionHandler`).
- **Monorepo**: all apps + platform in one repo.
- **Tenant onboarding**: invite-only. The first invitation redeemed in a workspace becomes its platform-managed Superadmin; the Superadmin creates Admins, who invite members.
- **Actions**: enforced server-side via `ActionChecks`; the envelope is a template copied into tenant-owned roles (no cascade).

---

## 16. Where to go next (see `project-management/roadmap.md`)

1. **Observability** (Phase 4) — correlation IDs across C#/TS, structured log conventions in `Tessera.Platform.Observability`.
2. **CI pipeline** (Phase 7) — GitHub Actions split by path (ci-web / ci-platform / ci-infra).
3. **Deploy** — Fly.io + PostgreSQL, secrets management, dev container, onboarding doc.
