# Tessera — Project Progress

> 📐 **Architecture**: see **[`docs/ARCHITECTURE.md`](../docs/ARCHITECTURE.md)** — the single source of truth for how the system fits together.

> **Last updated**: 2026-08-13 (end of Session 10)
> **Purpose**: Single source of truth for project state across sessions.
> **How to use**: Start here every session. Read this file first, then open `tasks.md` for the checklist.

---

## 🗺️ Session Handoff — Quick Summary

| Item | Status |
|------|--------|
| **Current milestone** | Two portals (business + superadmin) with envelopes/roles/actions ✅ |
| **Last completed step** | Full stack: superadmin auth + tenant/envelope/user management APIs, platform portal (apps/platform-portal), business portal Team page |
| **Next step** | Choose next feature (Observability / CI pipeline)
| **Build status** | ✅ Platform: `dotnet build` 0 errors · Web: `npm run build` + `npm run lint` pass |
| **Docker status** | ⚠️ Not running this session — verified against InMemory API on `:5085` (`dotnet run`) |
| **Blockers** | None |

---

## 🏗️ What We've Built

### Multi-Tenant Foundation (Complete)
| Layer | Mechanism | Status |
|-------|-----------|--------|
| 🆔 Tenant Resolution | `X-Tenant-Id` header → Finbuckle resolves | ✅ Solid |
| 🗄️ Data Isolation | `IsMultiTenant()` on ALL entities → global query filters | ✅ Solid |
| 🔐 Token Isolation | `TenantClaimValidationMiddleware` → 403 on cross-tenant JWT reuse | ✅ Solid |
| 👑 Permission Isolation | Role on User model → JWT role claims → `AdminOnly` policy | ✅ Solid |

### Infrastructure
- ✅ ASP.NET Core minimal API targeting .NET 10
- ✅ Serilog structured logging, `ExceptionHandlingMiddleware`, `RequestLoggingMiddleware`
- ✅ `TenantValidationMiddleware` — validates `X-Tenant-Id` header
- ✅ `TenantClaimValidationMiddleware` — validates JWT tenant matches header
- ✅ `ApiErrorResponse` — standardised JSON error shape
- ✅ `GET /health`, OpenAPI in development
- ✅ EF Core Migrations, Docker Compose (PostgreSQL 16 + API)

### Authentication & Authorization
- ✅ JWT with BCrypt password hashing, access + refresh token flow
- ✅ Multi-tenant aware: same email works in different tenants
- ✅ RBAC: `Admin` / `User` roles, `AdminOnly` policy, promote endpoint
- ✅ Cross-tenant token reuse blocked (403)
- ✅ Seed admin (`admin@tessera.com`) created via raw SQL on fresh PostgreSQL

### Project Structure
- ✅ `Tessera.Platform.Api` — ASP.NET Core host
- ✅ `Tessera.Platform.Domain` — shared models
- ✅ `Tessera.Platform.Observability` — logging middleware
- ✅ `apps/web` — Next.js frontend (register/login, tenant picker, dashboard)

---

## 📋 What We Built This Session (Session 8)

### Role-Based Access Control (RBAC)

**Goal:** Restrict certain endpoints to admin users only.

- `Roles` static class with `Admin` / `User` constants
- `User.Role` property (defaults to `User`)
- `ClaimTypes.Role` in JWT claims
- `AdminOnly` authorization policy via `RequireRole()`
- DELETE widget requires `AdminOnly`
- `POST /auth/promote` — admin-only user promotion

### Cross-Tenant Token Validation

**Problem:** A valid JWT from Tenant A could be reused against Tenant B's data.

**Fix:** `TenantClaimValidationMiddleware` placed after `UseAuthentication()` compares the JWT's `tenant_identifier` claim against the `X-Tenant-Id` header. Returns 403 Forbidden on mismatch.

**Bug discovered during E2E testing:** The middleware initially compared `tenant_id` (internal ID `"alpha"`) against the header (`"alpha-corp"`) — these never match. Fixed to use `tenant_identifier` which stores the external identifier.

### Data Isolation Fix: FindAsync → FirstOrDefaultAsync

**Problem:** `DbSet.FindAsync()` bypasses EF Core global query filters, potentially leaking data across tenants.

**Fix:** Replaced all 4 `FindAsync` calls with `FirstOrDefaultAsync` — now respects Finbuckle's tenant-scoped query filters.

### PostgreSQL Seed Fix: Raw SQL

**Problem:** `MultiTenantDbContext.SaveChangesAsync()` triggers `EnforceMultiTenant`, which requires a tenant context — crashes during startup (no HTTP request).

**Fix:** Replaced EF Core `Add`/`SaveChangesAsync` with parameterized `ExecuteSqlRawAsync` INSERT. Seed only runs inside `if (db.Database.IsRelational())`.

### E2E Testing (Docker + PostgreSQL)

Full test suite run against clean PostgreSQL database. All 18 tests passing:

| Test | Result |
|------|--------|
| Health endpoint | ✅ |
| Missing tenant → 400 | ✅ |
| Register (same email in Alpha + Beta) | ✅ |
| Duplicate email in same tenant → 400 | ✅ |
| Cross-tenant token → 403 (both directions) | ✅ |
| Widget CRUD within tenant | ✅ |
| Data isolation (Alpha sees 1, Beta sees 1) | ✅ |
| Cross-tenant widget access → 404 | ✅ |
| Non-admin delete → 403 | ✅ |
| Seed admin login | ✅ |
| Admin delete → 204 | ✅ |
| Admin promote (same tenant) → 200 | ✅ |
| Cross-tenant promote → 403 | ✅ |
| Refresh token → 200 | ✅ |

---

## 📋 What We Built This Session (Session 9)

### Tessra → Tessera Rename

- Renamed all project namespaces, projects, solution file, Docker assets, and docs from `Tessra` to `Tessera` (`apps/platform/src/Tessera.*`, `Tessera.Platform.slnx`, `dotnet run --project src/Tessera.Platform.Api`)
- Renamed seed admin email to `admin@tessera.com`
- Zero remaining `Tessra` references in the repo; `dotnet build` passes with 0 errors

### Next.js Frontend (`apps/web`)

**Goal:** A real user-facing client that exercises the platform API end-to-end.

- **Landing page** (`/`) with a live API health badge
- **Register / Login** (`/register`, `/login`) against `POST /auth/register` and `POST /auth/login`
- **Workspace (tenant) picker** — sends the `X-Tenant-Id` header the platform requires (Alpha Corp / Beta Industries)
- **Dashboard** (`/dashboard`) — tenant-scoped widget CRUD (`GET/POST/PUT/DELETE /widgets`)
- JWT access tokens stored in localStorage, auto-refreshed on 401 via `POST /auth/refresh`
- **Role-aware UI** — widget delete only shows for `Admin` users (role decoded from the JWT)
- CORS policy (`WebApp`) added in the platform for `http://localhost:3000`
- Stack: Next.js 16 (App Router) · TypeScript strict · Tailwind CSS v4
- `npm run build` and `npm run lint` both pass

---

## 📋 What We Built This Session (Session 10)

### Two-Portal Architecture

**Goal:** separate the superadmin (platform) experience from the business (tenant) experience, and let roles/actions be defined once and reused across workspaces.

- **`apps/platform-portal`** (port 3001) — superadmin portal: `superadmin@tessera.com` login, tenant management (create/edit/delete + envelope assignment), envelope management (roles + their actions)
- **`apps/web`** extended as the **business portal** (port 3000) — added a Team page (admin-only user management with roles from the envelope) and role-aware display of allowed actions

### Envelopes, Roles & Actions

- **Envelope** = bundle of roles + their allowed actions, assigned by a superadmin to a tenant
- **`AppRole`** = a role inside an envelope with a list of actions; **`ActionCatalog`** documents the capabilities (e.g. `create_widget`, `edit_widget`, `manage_users`)
- **Actions are a catalog only for now** — they display in the UI but are NOT enforced (enforcement is on the backlog)
- Default **Standard** envelope seeded: `Admin`, `Manager`, `User` roles; superadmin can create more
- **Tenant bootstrap**: the first user to register in an empty workspace becomes its `Admin`

### Superadmin Auth & DB-Backed Tenants

- **`AdminUser`** — platform-level account (no tenant); `POST /admin/auth/login` → `SuperAdmin` JWT (12h, no tenant claims)
- **`SuperAdminOnly` policy** + `/admin` excluded from `TenantValidationMiddleware`
- **`DbTenantStore`** — Finbuckle store backed by the database (replaces the hardcoded in-memory store) so tenants created by a superadmin resolve at runtime
- Platform data (tenants, envelopes, superadmin) seeds on both InMemory and PostgreSQL

### New API Surface

| Method | Path | Auth |
|--------|------|------|
| POST | `/admin/auth/login` | public |
| GET/POST/PUT/DELETE | `/admin/tenants` | SuperAdmin |
| GET/POST/PUT/DELETE | `/admin/envelopes` | SuperAdmin |
| GET | `/tenant/me` · `/tenant/envelope` | any tenant user |
| GET/POST/PUT/DELETE | `/tenant/users` | tenant Admin |
| GET | `/tenants` | public (workspace picker) |

- Migration `AddPlatformAdmin` adds `Tenants`, `Envelopes`, `Roles`, `AdminUsers` tables + `Tenant.EnvelopeId`
- All smoke-tested via curl: superadmin login/CRUD, envelope assignment, first-user-admin bootstrap, role validation, cross-tenant 403s, both portals return 200

---

## ⚠️ Known Issues

| Issue | Severity | Notes |
|-------|----------|-------|
| NU1903 — Microsoft.OpenApi vulnerability | Low | Transitive dependency. Will resolve with SDK update. |

---

## 📁 Project File Tree

```
Tessera/
├── Directory.Build.props
├── .dockerignore
├── project-management/
│   ├── roadmap.md
│   ├── tasks.md
│   ├── decisions.md
│   ├── learning-log.md
│   └── progress.md
├── apps/web/                         # Next.js frontend
├── apps/platform/
│   ├── Dockerfile
│   ├── docker-compose.yml
│   ├── Tessera.Platform.slnx
│   └── src/
│       ├── Tessera.Platform.Api/
│       │   ├── Data/
│       │   │   ├── AppDbContext.cs
│       │   │   └── AppDbContextFactory.cs
│       │   ├── Endpoints/
│       │   │   ├── WidgetEndpoints.cs
│       │   │   └── AuthEndpoints.cs
│       │   ├── Middleware/
│       │   │   ├── ExceptionHandlingMiddleware.cs
│       │   │   ├── RequestLoggingMiddleware.cs        (in Observability)
│       │   │   ├── TenantValidationMiddleware.cs
│       │   │   └── TenantClaimValidationMiddleware.cs  ← NEW
│       │   ├── Services/
│       │   │   └── AuthService.cs
│       │   ├── Migrations/
│       │   │   ├── *_InitialCreate.cs
│       │   │   ├── *_AddAuthTables.cs
│       │   │   ├── *_AddUserRole.cs                   ← NEW
│       │   │   └── AppDbContextModelSnapshot.cs
│       │   ├── Program.cs
│       │   ├── appsettings.json
│       │   ├── appsettings.Development.json
│       │   ├── appsettings.Docker.json
│       │   └── Properties/launchSettings.json
│       ├── Tessera.Platform.Domain/
│       │   └── Models/
│       │       ├── ApiErrorResponse.cs
│       │       ├── Tenant.cs
│       │       ├── Widget.cs
│       │       ├── User.cs
│       │       └── RefreshToken.cs
│       └── Tessera.Platform.Observability/
│           └── Middleware/RequestLoggingMiddleware.cs
```
