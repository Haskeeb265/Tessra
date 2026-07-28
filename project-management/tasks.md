# Tessra — Tasks

> **Current sprint**: Multi-Tenant Foundation Complete ✅
> **Last updated**: 2026-07-28 (end of Session 8)

---

## In Progress

- [ ] **Next milestone**: Choose next feature (MCP server / Observability / CI pipeline / more endpoints)

## Completed

### Role-Based Access Control (RBAC) — Session 8
- [x] **Roles constants** — `Domain/Models/User.cs` with `Roles.Admin` and `Roles.User` constants
- [x] **Role property on User model** — `User.Role` defaults to `Roles.User`
- [x] **Role claim in JWT** — `ClaimTypes.Role` added to access token claims in `AuthService.cs`
- [x] **Authorization policy** — `AdminOnly` policy in `Program.cs` using `RequireRole()`
- [x] **DELETE widget protected** — `.RequireAuthorization("AdminOnly")` on delete endpoint
- [x] **Admin promote endpoint** — `POST /auth/promote` (admin-only) promotes a user to admin
- [x] **Seed admin bootstrap** — Raw SQL seed on fresh PostgreSQL database
- [x] **Migration** — `AddUserRole` adds `Role` column to `Users` table

### Security Fix: Cross-Tenant Token Validation — Session 8
- [x] **Identified vulnerability**: JWT from Tenant A could be reused against Tenant B
- [x] **Created `TenantClaimValidationMiddleware`** — validates JWT's `tenant_identifier` claim matches `X-Tenant-Id` header
- [x] **Bug fix**: Changed from `tenant_id` (internal ID `"alpha"`) to `tenant_identifier` (external `"alpha-corp"`)
- [x] **Placed correctly**: Between `UseAuthentication()` and `UseAuthorization()`
- [x] **E2E verified**: Cross-tenant requests return 403 Forbidden

### Data Isolation Fix: FindAsync → FirstOrDefaultAsync — Session 8
- [x] **Identified**: `DbSet.FindAsync()` bypasses EF Core global query filters
- [x] **WidgetEndpoints.cs**: 3 calls replaced with `FirstOrDefaultAsync(w => w.Id == id)`
- [x] **AuthService.cs**: 1 call replaced with `FirstOrDefaultAsync(u => u.Id == userId)`

### PostgreSQL Seed Fix: Raw SQL — Session 8
- [x] **Problem**: `SaveChangesAsync()` triggers `EnforceMultiTenant` — crashes on startup
- [x] **Fix**: Replaced EF Core `Add`/`SaveChangesAsync` with parameterized `ExecuteSqlRawAsync`
- [x] **Guard**: Seed only runs inside `if (db.Database.IsRelational())`
- [x] **Verified**: Admin login works on fresh PostgreSQL database

### End-to-End Docker Test — Session 8
- [x] **Docker Compose**: Rebuilt with latest code, wiped volume, fresh PostgreSQL
- [x] **18 E2E tests**: All passing — health, auth, data isolation, RBAC, cross-tenant blocking, refresh
- [x] **All isolation layers solid**: Tenant resolution, data isolation, token isolation, permission isolation

### Project Scaffolding (Previous Sessions)
- [x] Scaffold C# ASP.NET Core project, Serilog, middleware, health check, OpenAPI
- [x] Multi-project solution (Api, Domain, Observability), Directory.Build.props
- [x] Finbuckle Multi-Tenant with header strategy, Widget CRUD, tenant isolation
- [x] Docker Compose + PostgreSQL, EF Core Migrations
- [x] JWT authentication (register, login, refresh), cross-tenant token validation

### Project Management
- [x] Created and maintained `project-management/` with roadmap, tasks, decisions, learning-log, progress

---

## 📌 Session Handoff — Start Here Next Time

1. **Multi-tenant foundation is complete** — tenant resolution, data isolation, token isolation, and RBAC all verified against PostgreSQL (18/18 E2E tests passing)
2. **Server runs** at `http://localhost:5000` (Docker) or `http://localhost:5085` (local InMemory)
3. **Seed admin**: `admin@tessra.com` / `Admin123!` available in both Alpha and Beta tenants (PostgreSQL only)
4. **Build passes** — run `dotnet build` from `apps/platform/`
5. **Docker**: `docker compose up -d --build` from `apps/platform/`
6. **Next**: Decide what to build for the MCP server product
