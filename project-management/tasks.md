# Tessra — Tasks

> **Current sprint**: Authentication (JWT) ✅ Complete
> **Last updated**: 2026-07-27 (end of Session 7)

---

## In Progress

- [ ] **Next milestone**: Role-based access control, CI pipeline, or more endpoints

## Completed

### Project Scaffolding
- [x] Scaffold C# ASP.NET Core project
- [x] Configure Serilog structured logging
- [x] Implement `ExceptionHandlingMiddleware`
- [x] Implement `RequestLoggingMiddleware`
- [x] Create `ApiErrorResponse` model
- [x] Add `GET /health` endpoint
- [x] Enable OpenAPI in development
- [x] Builds with 0 errors

### Solution Structure
- [x] Multi-project solution (Api, Domain, Observability)
- [x] `Directory.Build.props` at repo root
- [x] `Tessra.Platform.slnx` solution file
- [x] Proper project references and namespaces

### Architecture Decisions (all locked in)
- [x] Cloud: **Fly.io**
- [x] Multitenancy: **Shared DB + tenant_id** with **Finbuckle.MultiTenant**
- [x] Auth: **Roll our own JWT**

### Finbuckle Integration
- [x] **Step 1**: Installed NuGet packages (Api: 3 Finbuckle packages, Domain: Abstractions)
- [x] **Step 2**: Created `Tenant` model implementing `ITenantInfo` in `Domain/Models/Tenant.cs`
- [x] **Step 3**: Created `Widget` entity in `Domain/Models/Widget.cs`
- [x] **Step 4**: Created `AppDbContext` inheriting `MultiTenantDbContext` in `Api/Data/AppDbContext.cs`
- [x] **Step 5**: Configured Finbuckle in `Program.cs`
- [x] **Step 6**: Added full CRUD endpoints for Widgets, tenant isolation verified

### Bug Fix: Missing Tenant Header → 400 (was 500)
- [x] **Session 4**: Created `TenantValidationMiddleware` that returns 400 for missing `X-Tenant-Id` header
- [x] Excludes `/health` and `/openapi` from validation

### Docker Compose + PostgreSQL
- [x] **Session 5**: Created `Dockerfile` (multi-stage .NET 10), `docker-compose.yml` (API + PostgreSQL 16)
- [x] Created `appsettings.Docker.json` with PostgreSQL connection string
- [x] Conditional PostgreSQL/InMemory in `Program.cs` based on connection string
- [x] Added `Npgsql.EntityFrameworkCore.PostgreSQL` package

### EF Core Migrations
- [x] **Session 6**: Switched from `EnsureCreated()` to `Migrate()` with `IsRelational()` guard
- [x] Created `AppDbContextFactory` (IDesignTimeDbContextFactory) for EF Core CLI tools
- [x] Created initial migration (`InitialCreate`)
- [x] Installed `dotnet-ef` CLI tool globally
- [x] Added `Microsoft.EntityFrameworkCore.Design` package

### Authentication (JWT) — Session 7
- [x] **User model** — `Domain/Models/User.cs` with multi-tenant support
- [x] **RefreshToken model** — `Domain/Models/RefreshToken.cs` with crypto-random tokens
- [x] **AppDbContext** — added `Users` and `RefreshTokens` DbSets with `IsMultiTenant()`
- [x] **AuthService** — `Services/AuthService.cs` with BCrypt hashing, JWT generation, refresh lifecycle
- [x] **AuthEndpoints** — `Endpoints/AuthEndpoints.cs` with register, login, refresh
- [x] **JWT config** — `AddAuthentication().AddJwtBearer()` in `Program.cs` with full validation
- [x] **Widget protection** — `.RequireAuthorization()` on widget endpoints (return 401 without token)
- [x] **JWT claims** — `tenant_id` and `tenant_identifier` in access tokens
- [x] **Packages added** — `BCrypt.Net-Next` 4.0.3, `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.10
- [x] **Migration** — `AddAuthTables` creates Users + RefreshTokens tables
- [x] **7 tests verified** — register, login, protected/unprotected widgets, refresh, duplicate email

### Project Management
- [x] Created `project-management/` with roadmap, tasks, decisions, learning-log, progress

---

## Blocked

Nothing currently blocked.

---

## 📌 Session Handoff — Start Here Next Time

Welcome back! Here's what you need to know:

1. **Authentication (JWT) is complete** — register, login, refresh all working
2. **Widget endpoints are protected** — all require `Authorization: Bearer <token>` header
3. **Auth flow**: Send `X-Tenant-Id: alpha-corp` header with every request. Register → get token pair → use `Authorization: Bearer <accessToken>` for protected endpoints → use `/auth/refresh` when token expires
4. **Server runs** at `http://localhost:5000` (Docker) or `http://localhost:5085` (local)
5. **Build passes** — run `dotnet build` from `apps/platform/`
6. **Next options**:
   - Add role-based authorization (Admin vs regular user roles)
   - Set up CI pipeline (GitHub Actions)
   - Add more REST endpoints following the `Endpoints/` pattern
   - Switch from development secret key to production-ready key management
7. **Need help?** Ask for next steps
