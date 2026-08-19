# Tessera — Full Code Dry Run (End-to-End User Journeys)

> **What this is:** a line-by-line, request-by-request dry run of the entire
> codebase as it stands today. Every user journey is traced from the moment a
> human clicks, through every middleware, service call, database write, token
> claim, HTTP status code, and frontend reaction — nothing is assumed, nothing
> is skipped.
>
> **How it was produced:** by reading **every** C# source file in
> `apps/platform/src` (Api, Domain, Observability, Tests), the EF Core
> migration, every frontend file in both Next.js apps (`apps/web`,
> `apps/platform-portal`), the Dockerfile, docker-compose, launchSettings,
> all appsettings files, the tsconfigs, the git/docker ignore files, the
> operational Guide, the root readme, the agent rules, and the design docs
> (ARCHITECTURE / TABLES / mcp / multitenant_mature). A **second pass** was
> run to confirm nothing is unmapped — every file is accounted for in §22–23.
>
> **Last updated:** 2026-08-18 · branch `tessera/mcp` (multitenancy codebase).

---

## Table of contents

1. [Topography — what exists and where](#1-topography)
2. [The request lifecycle — every HTTP request's path through the pipeline](#2-the-request-lifecycle)
3. [Data model — every table and what it stores](#3-data-model)
4. [Identity — token formats, claims, and lifetimes](#4-identity)
5. [Journey A — Platform startup & seeding](#5-journey-a-startup--seeding)
6. [Journey B — Superadmin logs in (platform portal)](#6-journey-b-superadmin-login)
7. [Journey C — Superadmin creates an envelope (predefined roles)](#7-journey-c-create-an-envelope)
8. [Journey D — Superadmin creates a tenant and assigns the envelope](#8-journey-d-create-a-tenant)
9. [Journey E — Superadmin invites the tenant's first user](#9-journey-e-platform-invite)
10. [Journey F — Invitee registers and redeems (becomes workspace Superadmin)](#10-journey-f-registration)
11. [Journey G — A tenant user logs in (incl. MFA path)](#11-journey-g-login)
12. [Journey H — The business portal loads: /tenant/me and the action pill](#12-journey-h-business-portal-load)
13. [Journey I — Widget CRUD as different roles](#13-journey-i-widget-crud)
14. [Journey J — Team management: invites, role changes, removal](#14-journey-j-team-management)
15. [Journey K — Tenant-side role editing](#15-journey-k-tenant-role-editing)
16. [Journey L — What happens when you try to break it (attack paths)](#16-journey-l-attack-paths)
17. [Journey M — Tenant lifecycle: suspend, reactivate, delete](#17-journey-m-tenant-lifecycle)
18. [Journey N — Sessions: refresh, logout, password, email verify, MFA enrollment](#18-journey-n-sessions-and-recovery)
19. [Permission matrix — who can do what](#19-permission-matrix)
20. [Findings — discrepancies, sharp edges, and stale code observed](#20-findings)
21. [Test coverage — what the integration tests verify](#21-test-coverage)
22. [File reference — every file that matters (complete map)](#22-file-reference-complete--second-pass)
23. [Coverage guarantee](#23-coverage-guarantee)

---

## 1. Topography

### Components

| Piece | Where | Port / URL | Role |
|---|---|---|---|
| **Platform API** | `apps/platform/src/Tessera.Platform.Api` | `http://localhost:5085` (local); `:5000` in docker | The C# modular monolith: all data, auth, authz, tenant logic |
| **Domain models** | `apps/platform/src/Tessera.Platform.Domain` | — | POCOs + `Roles` / `ActionCatalog` constants |
| **Observability** | `apps/platform/src/Tessera.Platform.Observability` | — | Request-logging middleware (Serilog) |
| **Tests** | `apps/platform/src/Tessera.Platform.Tests` | — | xUnit + `WebApplicationFactory` integration tests |
| **Business portal** | `apps/web` | `http://localhost:3000` | Next.js: tenant login/register, dashboard, team page |
| **Platform portal** | `apps/platform-portal` | `http://localhost:3001` | Next.js: superadmin console (tenants, envelopes) |
| **Database** | PostgreSQL 16 via docker-compose (or EF InMemory for dev/tests) | `:5432` | Persistence |

### Configuration defaults (`appsettings.json` + overrides)

- **JWT:** issuer `Tessera`, audience `Tessera`, access token **15 min**, refresh token **7 days**, admin access token **4 h**, MFA token **5 min**. Secret key comes from `Jwt:SecretKey` (dev key in `appsettings.Development.json`, docker key in compose; **the app throws at startup if it is missing**).
- **DB:** `ConnectionStrings:DefaultConnection` empty in `appsettings.json` → falls back to **EF InMemory** (named `TesseraPlatformDb`). Docker/Postgres sets the connection string. `Database:AutoMigrate: true` → migrations auto-apply on startup for relational DBs.
- **Auth:** `Auth:RequireEmailVerification: false` (email verification flow exists but is off by default).
- **Email:** `Email:WebBaseUrl: http://localhost:3000`; the only `IEmailSender` registered is `ConsoleEmailSender`, which **logs the email (with links) instead of sending**.
- **CORS:** `http://localhost:3000` and `:3001` (http+https).
- **Rate limiting:** no appsettings section → code defaults apply: `RateLimiting:Enabled: true`, **20 requests per 60 s per IP** for `/auth*` endpoints, 429 on excess, `QueueLimit: 0`.
- **Seeds:** tenant admin `admin@tessera.com` / `Admin123!` (per tenant, Postgres only), platform superadmin `superadmin@tessera.com` / `Admin123!` (both providers).
- `NEXT_PUBLIC_API_URL` defaults both portals to `http://localhost:5085`.

### Two kinds of actors, two kinds of tokens

- **Platform superadmin** (`AdminUser`, table `AdminUsers`): no tenant. Token has role claim `SuperAdmin`, **no tenant claims**. Authorized by the `SuperAdminOnly` policy on `/admin/**`. Token lifetime 4 h. No refresh token (`AdminAuthService` returns `string.Empty` refresh).
- **Tenant user** (`User`, table `Users`): belongs to exactly one tenant (`TenantId`). Token carries `tenant_id`, `tenant_identifier`, `role_id`, `token_version`. Authorized per-request by **action checks** against their tenant role.

---

## 2. The request lifecycle

Every HTTP request into the platform API passes through this exact pipeline
(in `Program.cs`, in this order). This is the single most important thing to
understand — every journey below is just this pipeline + endpoint logic.

```
1. UseHttpsRedirection()                 → 307 for http→https (dev usually serves http)
2. ExceptionHandlingMiddleware           → catches anything below; maps to status + JSON ApiErrorResponse
3. RequestLoggingMiddleware              → logs "HTTP {Method} {Path} responded {StatusCode} in {ms}"
4. UseCors("WebApp")                     → answers preflight OPTIONS (before tenant validation!)
5. UseRateLimiter()                      → enforces /auth rate-limit policies
6. TenantValidationMiddleware            → 400 if X-Tenant-Id missing (EXCEPT /health, /openapi, /admin, /tenants)
7. UseMultiTenant()                      → Finbuckle resolves tenant from X-Tenant-Id via DbTenantStore
8. TenantSuspensionMiddleware            → 403 if tenant.Status == Suspended (EXCEPT /health, /ready, /openapi, /admin, /tenants)
9. UseAuthentication()                   → JwtBearer decodes Bearer token into ClaimsPrincipal
10. TenantClaimValidationMiddleware      → 403 if token.tenant_identifier != X-Tenant-Id header
11. TokenVersionValidationMiddleware     → 401 if token.token_version != user.TokenVersion (stale session)
12. UseAuthorization()                   → enforces policies: SuperAdminOnly on /admin, RequireAuthorization elsewhere
13. Endpoint handler                      → action checks (ActionChecks.RequiresAsync) for privileged ops
```

### What each gate checks, precisely

**Gate 6 — TenantValidationMiddleware (400 on missing header).**
`X-Tenant-Id` is required on **every** path except prefixes `/health`, `/openapi`, `/admin`, `/tenants`. It runs *before* Finbuckle. A request to `/widgets` without the header dies here with
`{"statusCode":400,"message":"The X-Tenant-Id header is required. …"}`.
> ⚠️ Note: `/ready` is **not** in this exclusion list (it *is* in the suspension middleware's list) — see Finding F1.

**Gate 7 — Finbuckle `UseMultiTenant()`.** Resolves the tenant by matching the header value against `Tenants.Identifier` (via `DbTenantStore.GetByIdentifierAsync`, which queries `Tenants` where `Identifier == header && !IsDeleted`). The resolved `Tenant` (with `Status`, `EnvelopeId`) is stored in the multi-tenant context. If no tenant matches, Finbuckle leaves `TenantInfo` null — the request continues (no 404 at this layer) and endpoints that touch tenant-scoped tables simply see zero rows.

**Gate 8 — TenantSuspensionMiddleware (403 on suspended).** Uses the resolved tenant from Gate 7. If `Status == Suspended` → 403 `"This workspace is suspended. Contact support for help."` — this **includes `/auth/login` and `/auth/register`**, so a suspended tenant's users cannot even attempt to log in. Platform paths are exempt.

**Gate 10 — TenantClaimValidationMiddleware (403 on cross-tenant token).** Only runs when `context.User.Identity.IsAuthenticated`. If the token carries a `tenant_identifier` claim (tenant user tokens do; **superadmin tokens do not**), it is compared case-insensitively to the `X-Tenant-Id` header. Mismatch → 403 `"The tenant in your authentication token does not match the tenant specified in the X-Tenant-Id header."` This is the cross-tenant attack blocker: a token minted for `alpha-corp` can never be replayed with `X-Tenant-Id: beta-industries`.

**Gate 11 — TokenVersionValidationMiddleware (401 on stale session).** Every privilege-changing operation (role change, password change, MFA change, deletion) bumps `User.TokenVersion`, and the JWT carries the version at mint time. This gate compares them; mismatch → 401 `"Your session is no longer valid. Please log in again."` Exempt paths (so a stale token can't lock a user out of the very endpoint that fixes it): `/auth/login`, `/auth/register`, `/auth/refresh`, `/auth/mfa`, `/auth/logout`, `/auth/forgot-password`, `/auth/reset-password`, `/auth/verify-email`. Superadmin tokens have no `token_version` claim → skipped.
> ⚠️ `/auth/change-password`, `/auth/mfa/enroll|verify|disable`, `/auth/promote` are **not** exempt — see Finding F9.

**Gate 12 — Authorization.** `SuperAdminOnly` policy (on both `/admin/tenants` and `/admin/envelopes` groups): requires `IsInRole("SuperAdmin")` **and** no `tenant_identifier` claim. The second clause is deliberate: a tenant JWT whose role name happens to be `SuperAdmin` can never satisfy it. Everything else (`/tenant/**`, `/widgets/**`, `/auth/change-password`, MFA endpoints, `/auth/promote`) uses `RequireAuthorization()` = any authenticated principal; fine-grained permission is then done inside handlers via `ActionChecks`.

### The JSON error contract

`ApiErrorResponse` = `{ statusCode, message, details?, traceId, timestamp }`, produced by Gates 6, 8, 10, 11 and by `ExceptionHandlingMiddleware`. Exceptions map: cancelled → 499; not found → 404; `ArgumentException`/`InvalidOperationException` → 400; `UnauthorizedAccessException` → 403; not implemented → 501; `HttpRequestException` → 502; anything else → 500 (stack trace only in Development).

### What the client does on each status (apps/web `lib/api.ts`)

- **401 with a stored refresh token:** `apiFetch` transparently calls `POST /auth/refresh`, updates the stored access token, and retries the original request **once**. If refresh fails, the original 401 surfaces and the page's catch block calls `logout()` + redirects to `/login`.
- **403:** surfaces as an `ApiError`; pages show the server message (e.g. team page: "Only workspace admins can manage the team.").
- **429:** surfaces as `ApiError` with the rate-limit message.

---

## 3. Data model

### Platform-level tables (not tenant-scoped — no `TenantId`, no Finbuckle filter)

| Table | Columns | Notes |
|---|---|---|
| `AdminUsers` | Id (PK), Email (unique), PasswordHash (bcrypt), CreatedAt | Platform superadmin accounts |
| `Envelopes` | Id (PK), Name (unique), Description, CreatedAt | Role *templates* assigned to tenants |
| `AppRoles` | Id (PK), EnvelopeId (FK→Envelopes, cascade delete), Name, Actions (text[]) | Roles *inside* an envelope; unique (EnvelopeId, Name) |
| `Tenants` | Id (PK, string), Identifier (unique), Name, ConnectionString (unused), EnvelopeId (nullable → Envelopes), Status ('Active'/'Suspended'), IsDeleted, xmin (concurrency), CreatedAt | `Id` and `Identifier` differ only for seeded tenants (`alpha`/`alpha-corp`); new tenants get `Id == Identifier` |

### Tenant-scoped tables (multi-tenant — `TenantId` stamped by Finbuckle `EnforceMultiTenant` on insert, global query filter on read)

| Table | Columns | Notes |
|---|---|---|
| `TenantRoles` | Id (PK), TenantId, EnvelopeRoleId (nullable provenance), Name, Actions (text[]), IsSystem, xmin, CreatedAt | Unique (TenantId, Name). **Copies** of envelope roles — later envelope edits never cascade. `IsSystem=true` marks the platform-managed `Superadmin` role |
| `Users` | Id (PK), Email, PasswordHash (bcrypt), RoleId (→TenantRoles, nullable), TenantId, TokenVersion, IsDeleted, EmailVerified, VerificationToken(+ExpiresAt), PasswordResetToken(+ExpiresAt), MfaSecret, MfaEnabled, xmin, CreatedAt | Unique (Email, TenantId) **filtered** to `WHERE NOT IsDeleted` (raw SQL index — soft-deleted accounts don't block email reuse) |
| `RefreshTokens` | Id (PK), UserId, Token (512), ExpiresAt, CreatedAt, IsRevoked, FamilyId, TenantId | 64 random bytes, stored raw (unguessable, no need to hash); rotation via families |
| `Invitations` | Id (PK), TenantId, Email, RoleId, TokenHash (SHA-256), ExpiresAt, UsedAt, CreatedAt | Invite-only onboarding |
| `Widgets` | Id (PK), TenantId, Name, Description, CreatedAt | The demo CRUD resource |

There are **no foreign keys** between tenant-scoped tables and `Tenants` — `TenantId` is a plain string column on each.

---

## 4. Identity

### Tenant user access token (JWT, HS256, 15 min)

Claims: `sub` (user GUID), `email`, `role` (role **name**, informational only — authorization is action-based), `role_id`, `token_version` (int), `tenant_id` (internal id, e.g. `alpha`), `tenant_identifier` (external id, e.g. `alpha-corp` — taken from the **request header** at login), `jti`, `iat`. Issuer/audience `Tessera`. Validation: issuer+audience+lifetime+signing key, `ClockSkew = 0`.

### Superadmin access token (JWT, HS256, 4 h)

Claims: `sub` (AdminUser GUID), `email`, `role: SuperAdmin`, `jti`, `iat`. **No tenant claims.** `AdminAuthService.LoginAsync` returns an empty refresh token string.

### MFA token (JWT, HS256, 5 min)

Issued when a login hits `MfaEnabled`. Claims: `sub`, `email`, `mfa: true`, `jti`, `iat`. Validated by `TryReadMfaToken` (same key), must carry `mfa=true`, then the user is re-fetched.

### Refresh token

64 random bytes, Base64, stored raw. Expires 7 days. **Single use** — each `/auth/refresh` revokes the presented token and mints a successor in the same `FamilyId`. **Reuse detection:** replaying a revoked/expired token revokes the *entire family* (theft signal). Logout revokes the family too.

### Secrets handling

- Passwords: bcrypt (`BCrypt.Net`).
- Invite / verify-email / reset tokens: only the **SHA-256 hash** is stored (`TokenHash`); the raw token travels in the email link.
- MFA secrets: TOTP, RFC 6238 (HMAC-SHA1, 30 s, 6 digits, ±1 step window), base32; `otpauth://` URI for authenticator enrollment. No third-party package.

---

## 5. Journey A — Startup & seeding

**Trigger:** `docker compose up` (Postgres) or `dotnet run` (InMemory).

1. `Program.cs` top-level code configures Serilog → console.
2. Middleware/services registered (see §2).
3. After `builder.Build()`, a scope is created and:
   - **AutoMigrate** (relational only): `db.Database.Migrate()` applies `InitialCreate` (all 9 tables + the filtered unique index on `Users`).
   - **`SeedPlatformDataAsync` (both providers):**
     - If no `Envelopes` exist → creates **"Standard"** envelope:
       - `Admin`: `view_widgets, manage_users, create_widget, edit_widget, delete_widget`
       - `Manager`: `create_widget, edit_widget`
       - `User`: `view_widgets`
     - If no `Tenants` exist → creates `alpha`/`alpha-corp` ("Alpha Corp") and `beta`/`beta-industries` ("Beta Industries"), both `EnvelopeId = Standard`, `Status = Active`.
     - Else: assigns Standard to any active tenant with no envelope.
     - If no `AdminUsers` exist → creates `superadmin@tessera.com` (bcrypt of `Admin123!`, overridable via `SeedSuperAdmin`).
   - **`SeedTenantDataAsync` (relational ONLY):** per tenant, via raw SQL (EF can't save multi-tenant rows without a tenant context at startup):
     - Copies envelope roles into `TenantRoles` if the tenant has none.
     - If the tenant has **no envelope**: seeds built-in `Admin` (all five actions) + `User` (view) roles.
     - Always: inserts the **system `Superadmin` role** (all actions, `IsSystem=true`) if none exists.
     - If the tenant has no users: inserts `admin@tessera.com` / `Admin123!` with the tenant's `Admin` role.
4. `app.Run()` begins serving.

**Resulting state on Postgres:** both tenants have roles `Admin, Manager, User, Superadmin(IsSystem)`, one seeded admin user each, and the platform has a superadmin. **On InMemory:** the tenants exist with envelopes, but **no roles and no users** — the only way in is the platform-portal invite flow (this is why the login page's "register instead" hint is stale — Finding F8).

---

## 6. Journey B — Superadmin login

**Actor:** platform superadmin. **UI:** platform portal (`:3001`).

1. User opens `http://localhost:3001` → `app/page.tsx` redirects to `/tenants` if `tessera.admin` exists in localStorage, else `/login`.
2. `POST /admin/auth/login` `{email, password}` — **no `X-Tenant-Id` header** (gate 6 exempts `/admin`).
3. `AdminAuthService.LoginAsync`: looks up `AdminUsers` by email; bcrypt-verifies; on success mints the superadmin JWT (claims per §4). **No rate limit** (the `auth` limiter only attaches to tenant `/auth` endpoints), no refresh token, no MFA.
4. 200 `{"accessToken":"…","refreshToken":""}`; the portal stores `tessera.admin = {accessToken, email}` and redirects to `/tenants`.
5. Failures → 401 (the portal's `apiFetch` throws `ApiError(401)`; the login page shows the message).
6. `GET /admin/tenants` and `GET /admin/envelopes` (no tenant header): gates 6/8/10/11 all skip superadmin tokens; gate 12 policy `SuperAdminOnly` passes (role claim present, no `tenant_identifier`). Tenant list includes `EnvelopeName` (left-joined by name); envelope list includes roles+actions.
7. **Expiry behavior:** the portal has **no auto-refresh** — after 4 h the token expires, every call returns 401, and the page catches it and redirects to `/login`. (Finding F6.)

---

## 7. Journey C — Create an envelope (predefined roles)

**Actor:** superadmin. **UI:** platform portal → Envelopes → "+ New envelope".

1. `POST /admin/envelopes` with body e.g.:
   ```json
   {
     "name": "Clinic",
     "description": "Roles for a dental clinic workspace",
     "roles": [
       { "name": "Front Desk", "actions": ["view_widgets","create_widget","edit_widget","manage_users"] },
       { "name": "Dentist",    "actions": ["view_widgets"] },
       { "name": "Viewer",     "actions": ["view_widgets"] }
     ]
   }
   ```
2. `ValidateEnvelopeRequest` (400s): name required; ≥1 role; role names non-empty; no duplicate role names (case-insensitive); **role name `Superadmin` is reserved** (case-insensitive) — the platform-managed workspace-owner role can never be duplicated in a template.
3. Inserts `Envelopes` row + one `AppRoles` row per role (actions trimmed + deduped, `EnvelopeId` set via navigation).
4. 201 `Created` with the envelope object. The portal appends it to the list.
5. **Envelope edits later** (`PUT /admin/envelopes/{id:guid}`): replaces the entire `AppRoles` set (remove all + re-add). **This never touches already-assigned tenants' `TenantRoles`** — copies are immutable by design.
6. **Envelope delete** (`DELETE`): first sets `EnvelopeId = null` on every tenant using it, then deletes the envelope (cascades `AppRoles`). The tenants keep whatever roles they had copied.

---

## 8. Journey D — Create a tenant and assign the envelope

**Actor:** superadmin. **UI:** platform portal → Tenants → "Add a tenant".

1. `POST /admin/tenants` `{identifier: "acme-dental", name: "Acme Dental", envelopeId: "<Clinic guid>"}`.
2. Handler steps, in order:
   - Identifier trimmed + lowercased; validated: lowercase letters/digits/hyphens, ≤200 → else 400.
   - Duplicate `Identifier` check → 400.
   - `EnvelopeId` must exist in `Envelopes` → else 400.
   - Creates `Tenant { Id = identifier, Identifier = identifier, Name, EnvelopeId }` — **for new tenants Id == Identifier** (so the X-Tenant-Id header *is* the PK).
   - Saves the tenant row (non-tenant-scoped, plain EF).
   - **`CopyEnvelopeRolesToTenantAsync(db, http, tenantId, envelopeId)`** — the key mechanism:
     - Loads the envelope with roles (no-tracking).
     - Opens a **tenant-bound context** via `MultiTenantDbContext.Create<AppDbContext, Tenant>(new Tenant { Id = tenantId }, http.RequestServices)` — required because superadmin requests have no tenant context and Finbuckle's `EnforceMultiTenant` needs one to stamp `TenantId` on insert.
     - Reads the tenant's existing role names, then `TenantRoleSeeder.CopyFromEnvelope` copies **only roles whose name isn't already present** (merge semantics), each with `EnvelopeRoleId = <the AppRole.Id>` for provenance.
     - `TenantRoleSeeder.EnsureSuperadmin` adds the **system `Superadmin` role** (all five actions, `IsSystem=true`) if the tenant has none — it is never part of the template.
     - `SaveChanges` stamps `TenantId = tenant.Id` on all new rows.
3. 201 `Created` with the tenant.
4. **Result in DB:** `TenantRoles` for `acme-dental` = Front Desk, Dentist, Viewer (copied) + Superadmin (system).
5. `PUT /admin/tenants/{id}` (edit): validates like create; keeps the PK (`Id`) stable even if the identifier changes; **if the envelope assignment changed**, re-runs `CopyEnvelopeRolesToTenantAsync` (merge — existing tenant-owned roles are preserved, only new names are added). Concurrency: `xmin` row version → `DbUpdateConcurrencyException` → **409 Conflict** "modified by someone else".
6. `GET /tenants` (public, excluded from gate 6): now returns `acme-dental` too — the business portal's workspace picker will show it.

---

## 9. Journey E — Platform invite (designate the tenant's first Superadmin)

**Actor:** superadmin. **UI:** platform portal → Tenants → row → **Invite** (prompts for an email).

1. `POST /admin/tenants/{id}/invites` `{email: "jane@acmedental.com"}` — accepts `id` **or** `identifier` (seeded tenants differ).
2. Handler:
   - Email trimmed/lowercased + validated (`MailAddress` round-trip) → 400 if bad.
   - Opens a **tenant-bound context**.
   - `TenantRoleSeeder.EnsureTenantRolesAsync(bound, tenant.EnvelopeId)` — belt-and-braces: copies envelope roles / seeds built-ins / ensures system Superadmin if the tenant somehow has none.
   - **Role selection (the important logic):**
     - `superadminRole` = the `IsSystem` role.
     - `hasSuperadmin` = any live user currently holds that role.
     - If **no superadmin yet** → the invite's `RoleId` = the **system Superadmin role** (first invite in a workspace is destined to become its owner).
     - If a superadmin **exists** → the invite's `RoleId` = the tenant's **Admin** role.
     - Neither available → 400 "No assignable role is available in this workspace."
   - If a live user with that email **does not exist** → creates `Invitation { Email, RoleId, TokenHash = SHA-256(raw), ExpiresAt = +72 h }`; sends email with link
     `{WebBaseUrl}/register?invite={rawToken}&tenant={identifier}` (console-logged).
   - **Always returns 200** with `"If this email is not already a member, an invitation has been sent."` — never reveals whether the email is known (anti-enumeration).
3. If a superadmin already exists and the invitee is invited *again* via this endpoint, they get the Admin role — the workspace's Superadmin seat is granted once, to the first redeemer.

---

## 10. Journey F — Registration (invite redemption)

**Actor:** the invitee (Jane). **UI:** business portal (`:3000`).

1. Jane clicks the emailed link → `/register?invite={token}&tenant=acme-dental`.
2. `app/register/page.tsx` reads `invite` + `tenant` from the query string, sets the tenant picker to `acme-dental`, and shows the form. (Without an invite, the page shows an **"Registration is invite-only"** card — no form.)
3. `POST /auth/register` `{email, password, inviteToken}` **with `X-Tenant-Id: acme-dental`**.
4. `AuthService.RegisterAsync` step by step:
   - Normalizes email (trim+lower). Requires the `X-Tenant-Id` header → else "A tenant is required."
   - **Requires an invite token** — no token → 400 `"Registration failed."` (generic, anti-enumeration).
   - Looks up the invite by `TokenHash` (tenant-scoped query → only invites for *this* tenant match).
   - Validates: exists, `UsedAt` null, not expired, **invite.Email == submitted email** (case-insensitive) → else "This invitation is invalid or has expired."
   - Duplicate live-user check → "Registration failed."
   - **Role resolution (the ownership handoff):**
     - If the workspace has **no system Superadmin user yet** → the redeemer gets the **Superadmin** role regardless of what the invite said.
     - Else, if the invite pointed at Superadmin → falls back to **Admin** (the seat is taken).
     - Invite role must still exist → else "references a role that no longer exists."
   - Creates `User { Email, PasswordHash = bcrypt(password), RoleId }` (TenantId stamped by Finbuckle on save), marks `invite.UsedAt = now`.
   - If `Auth:RequireEmailVerification` (off by default): creates the user unverified with a hashed verification token (24 h), emails a verify link, returns 201 `{message}` (no tokens) → the register page shows the notice and stops.
   - Else: `EmailVerified = true`, saves, and `GenerateAuthResultAsync` returns access + refresh tokens.
5. 201 + tokens → `lib/api.ts` stores `tessera.auth = {accessToken, refreshToken, tenantId, email}` → redirect to `/dashboard`.
6. **Single-use:** the same invite token replayed → 400 (verified by test `Invitation_flow_redeems_role_and_rejects_wrong_email`, which also proves wrong-email redemption is rejected).
7. **Jane's token now** carries `tenant_identifier: acme-dental`, `tenant_id: acme-dental`, `role: Superadmin`, `role_id: <system role>`, `token_version: 0`.
8. > ⚠️ **Finding F10:** `RegisterAsync` does **not** validate password length server-side (client requires ≥8). A 1-character password succeeds if the invite is valid.

---

## 11. Journey G — Login (incl. MFA)

**Actor:** any tenant user. **UI:** business portal `/login`.

1. `POST /auth/login` `{email, password}` **with `X-Tenant-Id`** (from the workspace picker — `TenantPicker` loads the live list from public `GET /tenants`, falling back to the hardcoded `alpha-corp`/`beta-industries` defaults if the API is unreachable).
2. `AuthService.LoginAsync`:
   - Finds the user by email **within this tenant** (multi-tenant filter) and `!IsDeleted`.
   - bcrypt-verifies the password → else 401.
   - If `RequireEmailVerification` and not verified → "Please verify your email address before logging in."
   - If `MfaEnabled` → returns 200 `{mfaRequired: true, mfaToken}` (5-min MFA JWT) → the login page switches to a 6-digit code form; `POST /auth/mfa` `{mfaToken, code}` → `TryReadMfaToken` (validates signature/issuer/audience/lifetime + `mfa=true` claim, reloads the user) → `TotpService.Validate` (±1 step window) → tokens. Bad code → 400.
   - Else → access + refresh tokens.
3. 200 + tokens → stored → `/dashboard`.
4. **Rate limiting:** all `/auth/*` endpoints are limited to 20/min/IP → 429 beyond (test-verified with a 3/min limit).

---

## 12. Journey H — Business portal load (`/tenant/me`)

1. Dashboard mounts; `getStoredAuth()` must exist (else redirect `/login`). It fires `getWidgets()` and `getMe()` in parallel.
2. `GET /tenant/me` (authenticated; **no action check** — any tenant user may call it):
   - Resolves `sub` → user (must exist, not deleted) → 401 otherwise.
   - Loads the user's role by `RoleId`, then `GetActionsAsync`-style action resolution: **system role → `ActionCatalog.All`**; otherwise the role's stored `Actions` list.
   - Loads the tenant by `Identifier` (from header).
   - Returns `{id, email, role, roleId, actions[], tenantId, tenantIdentifier}`.
3. The dashboard renders **UI affordances from the same action list the server enforces** (A4): `canCreate = actions.includes("create_widget")`, etc. A role with `view_widgets` only sees a read-only list and the hint "Your role is view-only for widgets."
4. `PortalHeader` shows the role pill (decoded from the **JWT `role` claim**, informational) and the **Team nav item only when `role === "Admin"`** — see Finding F2 (a workspace Superadmin does not see Team in the nav).

---

## 13. Journey I — Widget CRUD

All four endpoints live under `/widgets` with `RequireAuthorization()`; each calls `ActionChecks.RequiresAsync(db, http, user, action)` first:

| Endpoint | Action required | Behavior |
|---|---|---|
| `GET /widgets`, `GET /widgets/{id}` | `view_widgets` | Lists/gets rows **scoped to this tenant** (global query filter) |
| `POST /widgets` | `create_widget` | Model-binds a `Widget` from the body; `TenantId` stamped on save; 201 |
| `PUT /widgets/{id}` | `edit_widget` | Updates Name/Description of the tenant's widget; 404 if not found (cross-tenant id simply doesn't match a row) |
| `DELETE /widgets/{id}` | `delete_widget` | Removes the row; 204 |

`ActionChecks.RequiresAsync` resolution order:
1. `sub` → user (else 401).
2. User + `RoleId` must exist (else 401).
3. **Role `IsSystem` → allowed (all actions).**
4. Otherwise: allowed iff the role's `Actions` contains the action (case-insensitive) → else **403** `{"error":"You are not authorized to perform this action."}`.

**Concrete reactions:**
- **Front Desk** (actions include create/edit/manage_users but **not delete**): create and edit succeed; DELETE → 403 → the button isn't even rendered (no `canDelete`).
- **Dentist / Viewer** (view only): GET succeeds, POST/PUT/DELETE → 403; UI hides the create card.
- **Superadmin (system role)**: everything passes the check (bypass).
- A plain `User` role seeded by the built-in fallback (view only) behaves like Dentist.
- **No role at all** (`RoleId` null, e.g. a deleted-then-reused account edge): 401 from `ActionChecks`.

---

## 14. Journey J — Team management

**Actor:** a tenant user whose role includes `manage_users` (Admin, Superadmin, or custom).

### List users — `GET /tenant/users`
`manage_users` required → else 403. Returns live users with role name + `roleIsSystem` + createdAt. The team page (if `canManage`) renders a table; the platform-managed Superadmin row is shown as a locked badge ("platform-managed"), everyone else gets a role dropdown + Remove.

### Add a teammate directly — `POST /tenant/users`
`manage_users` required. Validations in order: valid email; password **≥ 8 chars** (server-enforced here); no existing live user with that email in this tenant; role resolved by `RoleId` or `Role` name, must exist in this tenant; **`IsSystem` role rejected** ("managed by the platform and cannot be assigned here"). Creates the user with `EmailVerified = true` (no invite, no email needed — direct provisioning), 201.

### Invite a teammate — `POST /tenant/invites`
`manage_users` required. `AuthService.InviteUserAsync`: role must exist and **not be system**; if the email isn't already a live member, creates an `Invitation` (72 h) and emails the register link with the tenant identifier; **always** returns the same generic "If this email is not already a member…" message. `GET /tenant/invites` lists them (with `Expired` flag).

### Change a role — `PUT /tenant/users/{id}`
`manage_users` required. Guards: user must exist; **cannot change your own role** ("ask another admin"); target must not hold the system Superadmin role; destination role must exist and not be system. **If the role actually changes:** `RoleId` updated, `TokenVersion++`, and **every active refresh token for that user is revoked** → their next request 401s at gate 11, the frontend's auto-refresh fails (refresh token dead), and they're bounced to `/login`. **Permission changes apply instantly**, not at token expiry.

### Remove a user — `DELETE /tenant/users/{id}`
`manage_users` required. Guards: not yourself; not the system Superadmin. Then: `IsDeleted = true`, `TokenVersion++`, `RoleId = null`, revoke all refresh tokens → the account can no longer log in or hold a session; its email becomes reusable (filtered unique index + app-level check).

---

## 15. Journey K — Tenant-side role editing

`/tenant/roles` (GET is open to any authenticated user; POST/PUT/DELETE require `manage_users`):

- **GET** — the workspace's own role set (used by tests and the portal).
- **POST** — validates actions against `ActionCatalog.All` (unknown action → 400), no duplicate names, creates a `TenantRole`.
- **PUT** — same validation; **`IsSystem` roles cannot be edited** ("managed by the platform"); rename is safe because users reference roles by **Id**, not name.
- **DELETE** — `IsSystem` roles cannot be deleted; **deletion is blocked while users hold the role** ("Reassign them before deleting it") — no stranded users, no silent permission loss. Concurrency → 409.

`GET /tenant/envelope` returns the tenant's roles under a backwards-compatible "envelope" shape (`id: null, name: "{tenant name} roles"`) — the business portal's team page uses it to populate role pickers (system role filtered out).

---

## 16. Journey L — Attack paths (what actually happens)

| Attempt | Where it dies | Status | Result |
|---|---|---|---|
| Tenant A's JWT with `X-Tenant-Id: B` | Gate 10 | **403** | `"The tenant in your authentication token does not match…"` (test-verified) |
| No `X-Tenant-Id` on `/widgets` | Gate 6 | **400** | `"The X-Tenant-Id header is required."` |
| Tenant user token on `/admin/tenants` | Gate 12 | **403** | Policy fails: no `SuperAdmin` role claim, or has `tenant_identifier` |
| Superadmin token on `/widgets` | Endpoint's `ActionChecks` | **401** | `sub` is an AdminUser GUID — no matching `Users` row → Unauthorized |
| Role named "SuperAdmin" inside a tenant JWT on `/admin` | Gate 12 | **403** | Policy requires **absence** of `tenant_identifier`, not just the role name |
| Suspended tenant: any call incl. `/auth/login` | Gate 8 | **403** | `"This workspace is suspended…"` (test-verified) |
| Replay a used/expired refresh token | `RefreshAsync` | **400** | Whole **family** revoked (reuse detection) |
| Replay a used invite token / wrong email | `RegisterAsync` | **400** | `"This invitation is invalid or has expired."` |
| Stale access token after role/password change | Gate 11 | **401** | `"Your session is no longer valid…"` |
| Brute-force `/auth/login` | Rate limiter | **429** | 20/min/IP |
| Envelope role named "Superadmin" | `ValidateEnvelopeRequest` | **400** | Reserved name |
| Delete a role users still hold | `TenantDeleteRole` | **400** | `"This role is assigned to users…"` |
| Demote/delete the system Superadmin | `TenantUpdateUserRole` / `TenantDeleteUser` | **400** | Platform-managed, cannot be changed here |
| Concurrent tenant/envelope edit | `xmin` row version | **409** | `"This tenant was modified by someone else…"` |
| Register without an invite | `RegisterAsync` | **400** | Generic `"Registration failed."` |

---

## 17. Journey M — Tenant lifecycle

### Suspend — `PUT /admin/tenants/{id}/status` `{status:"Suspended"}`
Validates the enum (`Active`/`Suspended`), flips `Tenant.Status`, 409 on concurrent edit. From that instant, **every** tenant-scoped request (including `/auth/login` and `/auth/register`) returns 403 at gate 8 — even with a valid token. Reactivating flips it back; users can log in again (test-verified).

### Delete — `DELETE /admin/tenants/{id}`
Requires the tenant to exist and not already be deleted. Then:
- **PostgreSQL** (raw SQL, bypassing `EnforceMultiTenant`): hard-deletes `Widgets`, `RefreshTokens`, `Invitations` for the tenant; **soft-deletes** `Users` (`IsDeleted=true, RoleId=null`) and the `Tenants` row (`IsDeleted=true`). Returns 204. (B1/B4: nothing dangles, tenant stops resolving everywhere.)
- **InMemory**: a tenant-bound context (`MultiTenantDbContext.Create`) removes widgets/tokens/invites, soft-deletes users, then soft-deletes the tenant row on the main context.
- `TenantRoles` rows are **left behind** (orphaned, harmless because the tenant never resolves) — Finding F5.
- A deleted tenant: gone from `GET /tenants`, unresolvable by `DbTenantStore`, its users' tokens die at gate 10/11 (tenant_identifier no longer resolves; and their accounts are `IsDeleted`).

---

## 18. Journey N — Sessions and recovery

### Refresh — `POST /auth/refresh` `{refreshToken}` (+ X-Tenant-Id)
Tenant-scoped lookup; revoked/expired → **family revoked** + 400; live → user must exist & not be deleted; presented token revoked, successor minted in the same family (7 days again), new access token. The frontend calls this **automatically once** on 401.

### Logout — `POST /auth/logout` `{refreshToken}`
Revokes the whole family. Frontend just clears localStorage (does not call it — it clears the stored pair and redirects).

### Change password — `POST /auth/change-password` (authenticated)
Current password verified; new password ≥ 8; bumps `TokenVersion`, clears reset tokens, revokes all refresh tokens → **all sessions die immediately** (including the one you just used — you must log in again).

### Forgot / reset password
`POST /auth/forgot-password` always returns the same generic message (no enumeration). If the email exists: stores **SHA-256** of a reset token (1 h expiry) and emails `{WebBaseUrl}/reset-password?token=…`. `POST /auth/reset-password` validates hash + expiry, ≥ 8 chars, bumps version, revokes sessions.

### Verify email
`POST /auth/verify-email` — hashed token, 24 h expiry, sets `EmailVerified = true`. Only relevant when `Auth:RequireEmailVerification: true`.

### MFA enrollment
`POST /auth/mfa/enroll` (authenticated) → generates a secret, returns `{secret, otpauthUri}`; user scans into an authenticator; `POST /auth/mfa/verify` `{code}` → validates TOTP, sets `MfaEnabled = true`, bumps `TokenVersion`, revokes sessions; `POST /auth/mfa/disable` `{code}` → TOTP-verified removal. From then on, login requires the two-step flow (Journey G).

### Legacy promote — `POST /auth/promote`
Still present; now requires `manage_users` via `ActionChecks`; reassigns the user to the tenant's Admin role (bumps version, revokes sessions). Kept for backward compatibility.

---

## 19. Permission matrix

### Platform (superadmin) — policy `SuperAdminOnly`
| Capability | Endpoint |
|---|---|
| Manage tenants (list/create/edit/delete/suspend/invite) | `/admin/tenants*` |
| Manage envelopes (list/create/edit/delete) | `/admin/envelopes*` |
| Platform auth | `/admin/auth/login` |

### Tenant — by **action** (system Superadmin role bypasses all)
| Action | Endpoints it unlocks |
|---|---|
| `view_widgets` | `GET /widgets*` |
| `create_widget` | `POST /widgets` |
| `edit_widget` | `PUT /widgets/{id}` |
| `delete_widget` | `DELETE /widgets/{id}` |
| `manage_users` | `GET/POST/PUT/DELETE /tenant/users*`, `GET/POST /tenant/invites`, `POST/PUT/DELETE /tenant/roles`, `POST /auth/promote` |

| Open to any authenticated tenant user | `GET /tenant/me`, `GET /tenant/roles`, `GET /tenant/envelope`, password/MFA self-service, logout |
|---|---|
| Anonymous (no auth, but tenant header still required) | `POST /auth/login|register|refresh|mfa|forgot-password|reset-password|verify-email`, `GET /health`, `GET /ready`, `GET /tenants` |
| No auth + no tenant header | `/health`, `/openapi`, `/admin/auth/login`, `/admin/**` (with superadmin token), `/tenants` |

### Standard seeded roles (Postgres)
| Role | Actions |
|---|---|
| `Superadmin` (system) | All five |
| `Admin` | All five |
| `Manager` | `create_widget`, `edit_widget` |
| `User` | `view_widgets` |

---

## 20. Findings

Discrepancies, sharp edges, and stale code observed during the dry run — each with the file and line of code that causes it.

- **F1 — `/ready` isn't exempt from tenant-header validation.** `TenantValidationMiddleware.ExcludedPaths` = `/health, /openapi, /admin, /tenants`; `TenantSuspensionMiddleware.ExcludedPaths` includes `/ready`. Result: `GET /ready` without `X-Tenant-Id` → **400** from gate 6, so the readiness probe as documented ("can reach its database") can't be used by a plain healthcheck. Fix: add `/ready` to the first list.
- **F2 — Workspace Superadmin doesn't see the "Team" nav item.** `PortalHeader` shows Team only when the decoded JWT role claim `=== "Admin"`. The first user in a workspace holds the role **`Superadmin`** (system), so the nav hides Team for the most-privileged user; the page is still reachable manually and works (its gating is action-based). Fix: treat system/Superadmin as admin, or drop the claim-based check in favor of `/tenant/me` actions.
- **F3 — Dashboard shows a blank workspace name for non-seeded tenants.** `dashboard/page.tsx` (and the header's tenant lookup) uses the **hardcoded** `TENANTS` fallback array (`alpha-corp`/`beta-industries`); a tenant created by superadmin (e.g. `acme-dental`) isn't in it, so `tenant?.name` is `undefined` and the subtitle renders "workspace " with nothing after it. The portal-header falls back to showing the tenant id, so the header is fine.
- **F4 — The business-portal login page's dev hint is stale.** It tells users to "register an account instead (the first user in a workspace becomes its admin)." Registration is now **invite-only** — a direct register without an invite returns 400 "Registration failed." The only working self-serve path is the platform-portal invite flow. The register page itself is correct ("Registration is invite-only").
- **F5 — Tenant delete leaves orphaned `TenantRoles` rows.** The raw SQL deletes widgets/refresh tokens/invitations and soft-deletes users + tenant, but `TenantRoles` rows are untouched. Harmless today (tenant never resolves, no FKs), but they accumulate forever.
- **F6 — Platform portal has no token refresh.** Superadmin tokens expire after 4 h; `platform-portal/lib/api.ts` has no 401-retry, so the user is dumped to `/login` mid-session. Acceptable, but worth an auto-refresh or longer lifetime.
- **F7 — Stale comment in `AppRole.cs`.** "actions are tracked as a catalog for now — enforcement is on the backlog." Enforcement via `ActionChecks` is implemented and wired to every privileged endpoint.
- **F8 — Default admin token lifetime mismatch.** `AdminAuthService` falls back to 12 h if unset, but `appsettings.json` sets `AdminAccessTokenExpirationHours: 4` — the effective lifetime is 4 h.
- **F9 — TokenVersion gate isn't skipped for password/MFA self-service endpoints.** `/auth/change-password`, `/auth/mfa/enroll|verify|disable` are not in `TokenVersionValidationMiddleware.SkippedPaths`, so after a password or MFA change the *old* token gets 401 on those endpoints (the refresh token is dead, so the client can't self-heal). Deliberate per A5 ("reject stale sessions") but awkward UX — the user must log in again.
- **F10 — `RegisterAsync` doesn't validate password length server-side.** Client requires ≥8, and `TenantCreateUser`/`ChangePassword`/`ResetPassword` enforce ≥8, but invite redemption accepts any non-empty password.
- **F11 — `POST /widgets` accepts a `Widget` body with no validation.** An empty/whitespace `Name` saves fine (max lengths are enforced by the DB/EF, but not server-side validation). Minor for the demo resource; worth a validation layer before manifests arrive.
- **F12 — In-memory dev has no seeded tenant users/roles.** On InMemory (no docker), `SeedTenantDataAsync` never runs, so `alpha-corp`/`beta-industries` have **no roles or users** until the platform superadmin invites someone (the first invite also triggers `EnsureTenantRolesAsync`). Not a bug, but easy to trip over when demoing locally without Postgres.
- **F13 — Register page flash.** `inviteSeen` starts `false`, so the invite-only card/form states flash briefly before the `useEffect` reads the query string.
- **F14 — JWT `role` claim is display-only but the UI trusts it.** The server authorizes by actions; the business portal uses the role claim for the header pill and the Team nav (F2). Since role names are tenant-editable, a renamed role would change the pill but not actual permissions — consistent, but the nav bug (F2) shows the cost of mixing the two sources.
- **F15 — `apps/platform/Guide.md` is stale: its curl quick-test registers without an invite.** Guide step 4 does `POST /auth/register` with no `inviteToken` and claims "first user of gamma-corp becomes its Admin" — but registration is invite-only (B2), so that call returns 400 `"Registration failed."` today. The working sequence is: create envelope → create tenant → platform-invite → redeem the emailed link (as the doc's own InMemory section correctly says). `docs/ARCHITECTURE.md` §8 has a matching stale claim ("register page reads `?invite=` … to prefill email" — the page sets `inviteToken` + `tenantId`, **not** the email).
- **F16 — `docs/mcp.md` and `readme.md` reference projects that don't exist.** mcp.md §6's layout lists `Tessera.Platform.RateLimiting/` and §3.7/§2.5 call out "the existing `RateLimiting` module"; readme.md's scaffolding lists planned `Tessera.Platform.Auth/Authz/Billing/RateLimiting` class libraries. The solution has exactly **four** projects (Api, Domain, Observability, Tests — verified via `*.csproj` glob): rate limiting is implemented **inline in `Program.cs`**, and auth/authz live in the Api project's `Services/`. Relevant when planning MCP work against "the platform" — there is no separate module to slot into.
- **F17 — `docs/ARCHITECTURE.md` drifts from the code in two places (its own rule says code wins).** (a) §5.3's middleware table lists `/ready` as exempt from `TenantValidationMiddleware` — the code's `ExcludedPaths` has only `/health, /openapi, /admin, /tenants`, so `/ready` without a header 400s (same root cause as F1); (b) §6.1/§6.3 say the superadmin token lasts **12 h** — the effective value is **4 h** (`Jwt:AdminAccessTokenExpirationHours` in appsettings.json; the 12 h figure is only `AdminAuthService`'s fallback default).
- **F18 — Minor cosmetic/UX notes from the second pass.** (a) `apps/web` `Alert` supports `error|info|success`, the platform portal's `Alert` only `error|info` — harmless divergence; (b) `HealthBadge` calls `getHealth()` which still attaches `X-Tenant-Id: alpha-corp` (the default) to a path that's exempt — harmless; (c) the register page briefly renders one state before the `useEffect` reads the query string (F13).

---

## 21. Test coverage

`Tessera.Platform.Tests` (xUnit + `WebApplicationFactory<Program>`, EF InMemory, per-factory unique DB name via `Database:InMemoryName`, `RecordingEmailSender` captures emailed links, rate limiting off by default, `Auth:RequireEmailVerification` off by default, fixed test signing key). Every test is mapped to the behavior it pins:

**AuthFlowTests**
- `Platform_invite_bootstraps_only_one_superadmin` — the platform bootstrap invite redeems into the system `Superadmin` role (all actions); a second pending/after-owner platform invite is rejected; tenant-side invites create `Admin`/`User` members.
- `Registration_without_invite_is_rejected_and_generic` — no invite → 400 `"Registration failed."`; re-registering an existing email (no fresh invite) → same generic 400 (no enumeration).
- `Refresh_rotates_and_reuse_revokes_the_family` — refresh rotates the token; replaying the rotated token 400s and **kills the successor too** (family revocation).
- `Logout_revokes_refresh_tokens` — logout → refresh of that token 400s.
- `Change_password_revokes_all_sessions` — old access token 401s immediately (token_version bump); old password fails, new one works.
- `Password_reset_flow` — forgot-password → emailed token → reset → login with the new password.
- `Email_verification_is_required_when_enabled` — with `RequireEmailVerification: true`, registration returns a message (no tokens), login blocked until the emailed verify token is redeemed.
- `Mfa_enroll_and_login_flow` — enroll → compute a real TOTP → verify enables MFA → login returns `{mfaRequired, mfaToken}` → completing with the code returns a token pair.

**HierarchyTests**
- `Superadmin_role_is_protected_from_tenant_edits` — system role: rename 400, edit actions 400, delete 400.
- `Superadmin_role_cannot_be_assigned_or_demoted_tenant_side` — tenant invite with Superadmin role 400; direct user create with role `"Superadmin"` 400; demote 400; remove 400.
- `Envelope_cannot_declare_a_reserved_superadmin_role` — envelope role named `Superadmin` → 400.
- `Superadmin_can_do_everything_and_admin_invites_work` — system role exposes all five actions; Superadmin invites an `Admin` assistant who has `manage_users` but never the `Superadmin` role.

**LifecycleTests**
- `Tenant_delete_cleans_widgets_and_soft_deletes` — delete → widgets gone (hard), user + tenant soft-deleted, tenant absent from public list.
- `Suspended_tenant_is_blocked_until_reactivated` — suspend → login **and** API calls 403; reactivate → login works.
- `Invitation_flow_redeems_role_and_rejects_wrong_email` — wrong-email redemption 400; correct redemption gets the invited role; invite is single-use (replay 400).
- `Auth_endpoints_are_rate_limited` — with a 3/min limit, excess login attempts → 429.

**TenantIsolationTests**
- `Cross_tenant_token_returns_403` — tenant A's token + `X-Tenant-Id: B` → 403.
- `Widgets_are_isolated_between_tenants` — tenant A's widget invisible to tenant B; `TenantId` stamped correctly.
- `Widget_actions_are_enforced_server_side` — User role: view 200, create/edit/delete 403; Admin: create 201, delete 204.
- `Role_rename_preserves_permissions_by_id` — renaming `Admin`→`Owner` keeps actions (`manage_users`, `delete_widget`) and authorization works.
- `Role_delete_is_blocked_while_users_are_assigned` — delete 400 "assigned to users"; after reassignment it succeeds; the reassigned user's old token 401s immediately.
- `Role_management_requires_manage_users_action` — User role: create role 403, invite 403, list users 403.

**TotpServiceTests**
- RFC 6238 Appendix B SHA-1 vectors (6 known counter/code pairs); generated secrets are 32 base32 chars; `Validate` rejects wrong/empty/short codes.

**Shared helpers** (`TestAppFactory` / `ApiTestHelpers`) — `RegisterUserAsync` (platform invite → redeem), `InviteAndRegisterAsync` (tenant invite with a named role), `RegisterTeamAsync` (Superadmin + User), `LoginSuperAdminAsync`, `InviteTokenFromEmail` (regex from the recorded email), `Auth`/`Tenant` header helpers.

---

## 22. File reference (complete — second pass)

### Platform API (`apps/platform/src/Tessera.Platform.Api`)

| File | Mapped in |
|---|---|
| `Program.cs` | §2, §5, §7, §11 (pipeline, seeding, rate limiter, CORS, JWT) |
| `Data/AppDbContext.cs` | §3 (model, multi-tenant flags, indexes) |
| `Data/DbTenantStore.cs` | §2 gate 7, §5, §17 |
| `Data/AppDbContextFactory.cs` | §22 note — design-time factory for `dotnet ef` (never runs at runtime) |
| `Endpoints/AdminEndpoints.cs` | §6–§9, §17 (tenant/envelope CRUD, copy semantics, invites, status) |
| `Endpoints/TenantEndpoints.cs` | §12, §14, §15 (`/tenant/me|envelope|roles|invites|users`) |
| `Endpoints/AuthEndpoints.cs` | §10, §11, §18 (all `/auth/*`, DTOs, rate-limit attach) |
| `Endpoints/WidgetEndpoints.cs` | §13 (action-gated CRUD) |
| `Middleware/TenantValidationMiddleware.cs` | §2 gate 6 (+ F1/F17) |
| `Middleware/TenantClaimValidationMiddleware.cs` | §2 gate 10, §16 |
| `Middleware/TenantSuspensionMiddleware.cs` | §2 gate 8, §17 |
| `Middleware/TokenVersionValidationMiddleware.cs` | §2 gate 11, §18 (exempt paths, F9) |
| `Middleware/ExceptionHandlingMiddleware.cs` | §2 (error contract, exception map) |
| `Services/AuthService.cs` | §10, §11, §18 (register/login/MFA/refresh/passwords/invites/tokens) |
| `Services/AdminAuthService.cs` | §6 (superadmin login + token) |
| `Services/ActionChecks.cs` | §13, §19 (action authz incl. system-role bypass) |
| `Services/AuthHelpers.cs` | §2, §4 (claims, SHA-256, random tokens) |
| `Services/TenantRoleSeeder.cs` | §8, §9, §10 (copy semantics, built-ins, system role) |
| `Services/TotpService.cs` | §11, §18 (RFC 6238) |
| `Services/EmailSender.cs` | §5, §9, §18 (console implementation) |
| `Properties/launchSettings.json` | §1 (`:5085`) |
| `appsettings.json` / `.Development` / `.Docker` | §1 (config defaults, F8) |
| `Migrations/20260817035321_InitialCreate.cs` | §3, §5 (full schema + filtered unique index) |
| `Tessera.Platform.Api.csproj` | §1 (packages: Finbuckle 10.1.2, JwtBearer, Npgsql, Serilog, InMemory; **no separate Auth/Authz/RateLimiting projects** — F16) |

### Platform Domain (`…/Domain/Models`)

| File | Mapped in |
|---|---|
| `Tenant.cs` (`Tenant`, `TenantStatus`) | §1, §3, §17 |
| `User.cs` (`User`, `Roles`, `ActionCatalog`) | §3, §4, §19 (F7 — stale "on the backlog" comment lives in `AppRole.cs`, not here) |
| `TenantRole.cs` | §3, §15, §19 |
| `Envelope.cs` / `AppRole.cs` | §3, §7 (template semantics; F7 stale comment) |
| `AdminUser.cs` | §3, §6 |
| `Widget.cs` | §3, §13 |
| `Invitation.cs` / `RefreshToken.cs` | §3, §9, §10, §18 |
| `ApiErrorResponse.cs` | §2 (error contract shape; `Details` omitted when null) |

### Observability (`…/Tessera.Platform.Observability`)

| File | Mapped in |
|---|---|
| `Middleware/RequestLoggingMiddleware.cs` | §2 gate 3 (logs method/path/status/duration) |

### Tests (`…/Tessera.Platform.Tests`)

| File | Mapped in |
|---|---|
| `TestAppFactory.cs` (+ `ApiTestHelpers`) | §21 (harness, config overrides, helpers) |
| `AuthFlowTests.cs`, `HierarchyTests.cs`, `LifecycleTests.cs`, `TenantIsolationTests.cs`, `TotpServiceTests.cs` | §21 (every test listed) |

### Business portal (`apps/web`)

| File | Mapped in |
|---|---|
| `lib/api.ts` | §2 (client: X-Tenant-Id, localStorage, 401 auto-refresh, JWT decode), §11, §13 |
| `app/layout.tsx` | §1 (root layout, fonts, metadata) |
| `app/page.tsx` | §1 (landing + health badge + CTAs) |
| `app/login/page.tsx` | §11 (tenant picker, MFA step; F4 stale hint) |
| `app/register/page.tsx` | §10 (invite redemption, invite-only gate; F13 flash) |
| `app/dashboard/page.tsx` | §12, §13 (widget CRUD, action pills; F3 blank tenant name) |
| `app/dashboard/users/page.tsx` | §14 (team page, role pickers, system-role badge) |
| `components/portal-header.tsx` | §12 (role pill; F2 Team nav) |
| `components/tenant-picker.tsx` | §11 (live `/tenants` list + fallback) |
| `components/health-badge.tsx` | §5 (§22 F18b — sends a stray default tenant header, harmless) |
| `components/ui.tsx` | §12–14 (Field/TextInput/TextArea/Button/Card/Alert) |
| `tsconfig.json` | §1 (`strict: true`) |

### Platform portal (`apps/platform-portal`)

| File | Mapped in |
|---|---|
| `lib/api.ts` | §6 (no tenant header, no auto-refresh — F6) |
| `app/layout.tsx` | §1 |
| `app/page.tsx` | §6 (redirect by stored auth) |
| `app/login/page.tsx` | §6 (superadmin login) |
| `app/tenants/page.tsx` | §8, §9, §17 (tenant CRUD, envelope select, invite, delete) |
| `app/envelopes/page.tsx` | §7 (envelope + role/actions editor) |
| `components/admin-header.tsx` | §6 (nav, logout) |
| `components/ui.tsx` | §6–9 (same primitives; `Alert` lacks `success` kind — F18a) |
| `tsconfig.json` | §1 (`strict: true`) |

### Infra / config / docs

| File | Mapped in |
|---|---|
| `apps/platform/docker-compose.yml` | §1, §5 (api :5000→8080, postgres 16, pgdata volume, dev secret) |
| `apps/platform/Dockerfile` | §1 (multi-stage .NET 10 SDK→publish→aspnet runtime, non-root `USER app`, :8080) |
| `apps/platform/dotnet-tools.json` | §5 (pins `dotnet-ef` 10.0.11) |
| `apps/platform/Guide.md` | §1, §5 (ops guide; **F15** — stale curl register step) |
| `Directory.Build.props` | §1 (net10.0, nullable, implicit usings) |
| `.gitignore` / `.dockerignore` | §1 (excludes bin/obj, .env, project-management in docker) |
| `readme.md` | §22 note — scaffolding checklist (aspirational; F16 lists never-built class libs) |
| `.claude/AGENTS.md` / `apps/web/AGENTS.md` | §22 note — agent rules (mentor-first workflow; Next.js-version warning block) |
| `docs/ARCHITECTURE.md` | cross-checked throughout (**F17** — `/ready` exemption + 12 h claim drift) |
| `docs/TABLES.md` | §3 (schema reference) |
| `docs/multitenant_mature.md` | §5, §17 (prod-readiness items B1–B4, C1–C6, E3–E6 referenced in code comments) |
| `docs/mcp.md` | §22 note (**F16** — layout lists non-existent `Tessera.Platform.RateLimiting`) |
| `infra/`, `scripts/` | **Empty placeholders** — nothing to map |

---

## 23. Coverage guarantee

- **Every `.cs` file** in `apps/platform/src` (40 files) has been read and mapped: 21 Api, 9 Domain, 1 Observability, 9 Tests.
- **Every frontend file** in both apps (11 in `apps/web`, 8 in `apps/platform-portal`) has been read and mapped.
- **Every HTTP endpoint** in the codebase appears in §19's matrix or the journeys — including the rarely-hit ones (`/auth/promote`, `/auth/mfa/*`, `/ready`, `/openapi`).
- **Every middleware, service method, seed path, migration artifact, and config key** is traced to at least one journey or gate.
- **No dead code was found** (everything is referenced), but three pieces of *stale documentation* and one *unreachable-at-runtime path* exist: the `AppDbContextFactory` (used only by `dotnet ef` tooling), the `AppRole` "enforcement on the backlog" comment (F7), and the doc drifts in F15/F16/F17.

---

*End of dry run (two passes). Every claim above was read directly from the current source; where the code and docs disagree, the code wins and the discrepancy is listed in §20.*
