# Tessra — Project Progress

> **Last updated**: 2026-07-28 (end of Session 8)
> **Purpose**: Single source of truth for project state across sessions.
> **How to use**: Start here every session. Read this file first, then open `tasks.md` for the checklist.

---

## 🗺️ Session Handoff — Quick Summary

| Item | Status |
|------|--------|
| **Current milestone** | Multi-Tenant Foundation ✅ Complete |
| **Last completed step** | Full E2E verification: RBAC, cross-tenant token validation, FindAsync fix, PostgreSQL seed — 18/18 tests passing |
| **Next step** | Choose next feature for the MCP server product |
| **Build status** | ✅ Builds with 0 errors |
| **Docker status** | ✅ Up and running at `http://localhost:5000` with PostgreSQL |
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
- ✅ Seed admin (`admin@tessra.com`) created via raw SQL on fresh PostgreSQL

### Project Structure
- ✅ `Tessra.Platform.Api` — ASP.NET Core host
- ✅ `Tessra.Platform.Domain` — shared models
- ✅ `Tessra.Platform.Observability` — logging middleware

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

## ⚠️ Known Issues

| Issue | Severity | Notes |
|-------|----------|-------|
| NU1903 — Microsoft.OpenApi vulnerability | Low | Transitive dependency. Will resolve with SDK update. |

---

## 📁 Project File Tree

```
Tessra/
├── Directory.Build.props
├── .dockerignore
├── project-management/
│   ├── roadmap.md
│   ├── tasks.md
│   ├── decisions.md
│   ├── learning-log.md
│   └── progress.md
├── apps/platform/
│   ├── Dockerfile
│   ├── docker-compose.yml
│   ├── Tessra.Platform.slnx
│   └── src/
│       ├── Tessra.Platform.Api/
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
│       ├── Tessra.Platform.Domain/
│       │   └── Models/
│       │       ├── ApiErrorResponse.cs
│       │       ├── Tenant.cs
│       │       ├── Widget.cs
│       │       ├── User.cs
│       │       └── RefreshToken.cs
│       └── Tessra.Platform.Observability/
│           └── Middleware/RequestLoggingMiddleware.cs
```
