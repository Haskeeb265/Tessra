# Tessera — Tasks

> **Current sprint**: Two portals + envelopes/roles/actions ✅
> **Last updated**: 2026-08-13 (end of Session 10)

---

## In Progress

- [ ] **Next milestone**: Choose next feature (MCP server / action enforcement / Observability / CI pipeline)

## Completed

### Two-Portal Architecture — Session 10
- [x] **Superadmin auth** — `AdminUser` (platform-level, no tenant), `POST /admin/auth/login`, `SuperAdminOnly` policy, `/admin` excluded from tenant validation
- [x] **DB-backed tenant store** — `DbTenantStore` replaces the hardcoded in-memory store; superadmin-created tenants resolve at runtime
- [x] **Envelope model** — `Envelope` bundles roles + their actions; `AppRole` + `ActionCatalog` (actions tracked but not enforced — backlog until MCP)
- [x] **Tenant CRUD** — `GET/POST/PUT/DELETE /admin/tenants` (incl. envelope assignment)
- [x] **Envelope CRUD** — `GET/POST/PUT/DELETE /admin/envelopes` (roles + actions editor)
- [x] **Tenant user management** — `GET/POST/PUT/DELETE /tenant/users` (admin-only, roles validated against the tenant's envelope)
- [x] **Tenant bootstrap** — first user to register in an empty workspace becomes Admin
- [x] **Business portal Team page** — `apps/web/dashboard/users` (add users, change roles, remove) + role-aware action pills on the dashboard
- [x] **Platform portal** — `apps/platform-portal` on :3001 (login, tenants, envelopes)
- [x] **Public `GET /tenants`** — powers the workspace picker; dynamic tenant list with fallback
- [x] **Migration `AddPlatformAdmin`** — Tenants, Envelopes, Roles, AdminUsers tables + `Tenant.EnvelopeId`
- [x] **Verified** — `dotnet build` 0 errors, both portals build/lint, full curl smoke tests pass

### Tessra → Tessera Rename — Session 9
- [x] **Renamed every project/namespace** from `Tessra` to `Tessera` — `Tessera.Platform.Api`, `Tessera.Platform.Domain`, `Tessera.Platform.Observability`, `Tessera.Platform.slnx`
- [x] **Docker assets updated** — `Dockerfile`, `docker-compose.yml` (image names, project paths, `tessera_platform` DB)
- [x] **Docs updated** — Guide, ARCHITECTURE, readme, project-management (seed admin now `admin@tessera.com`)
- [x] **Verified** — `dotnet build` passes with 0 errors, zero `Tessra` references remain

### Next.js Frontend (apps/web) — Session 9
- [x] **Landing page** (`/`) with live API health badge
- [x] **Register / Login** against the platform's auth endpoints
- [x] **Workspace (tenant) picker** — sends `X-Tenant-Id` header
- [x] **Dashboard** (`/dashboard`) — tenant-scoped widget CRUD
- [x] **Auto token refresh** on 401 via `POST /auth/refresh`
- [x] **Role-aware UI** — delete only shown for Admin users
- [x] **CORS policy** (`WebApp`) added to the platform for `http://localhost:3000`
- [x] **Verified** — `npm run build` and `npm run lint` pass

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

1. **Two portals**: business portal at `http://localhost:3000` (`apps/web`), superadmin portal at `http://localhost:3001` (`apps/platform-portal`), both against the API on `http://localhost:5085`
2. **Superadmin**: `superadmin@tessera.com` / `Admin123!` (seeded on both providers). Creates tenants + envelopes (roles/actions)
3. **Tenant users**: first user to register in a workspace becomes its Admin; admins manage the team from the business portal (roles come from the tenant's envelope)
4. **Builds pass** — `dotnet build` from `apps/platform/` · `npm run build` in `apps/web` and `apps/platform-portal`
5. **Actions are NOT enforced yet** — they're a catalog on roles until the MCP feature lands (decided: keep actions on the backlog)
6. **Docker**: `docker compose up -d --build` from `apps/platform/` (PostgreSQL: seeded tenant admins `admin@tessera.com`)
7. **Next**: Decide what to build — MCP server, action enforcement, Observability, or CI pipeline
