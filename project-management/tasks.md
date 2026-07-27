# Tessra — Tasks

> **Current sprint**: Finbuckle Multi-Tenant Integration (Step-by-Step)
> **Last updated**: 2026-07-27 (end of Session 1)

---

## In Progress

- [ ] **Step 4**: Create `AppDbContext` inheriting `MultiTenantDbContext` in `Api/Data/AppDbContext.cs`
  - Constructor takes `IMultiTenantContextAccessor` (not `ITenantInfo` — Finbuckle 10.x change)
  - Expose `DbSet<Widget> Widgets`
  - Use `entity.IsMultiTenant()` fluent API in `OnModelCreating` (alternative to `[MultiTenant]` attribute)
- [ ] **Step 5**: Configure Finbuckle services + middleware + endpoints in `Program.cs`
  - `builder.Services.AddMultiTenant<Tenant>()`
  - `.WithHeaderStrategy("X-Tenant-Id")`
  - `.WithInMemoryStore(...)` with seeded tenants (Alpha Corp, Beta Industries)
  - `app.UseMultiTenant()` after logging, before endpoints
- [ ] **Step 6**: Build, run, and verify tenant isolation works via endpoints

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
- [x] **Step 3**: Created `Widget` entity with `[MultiTenant]` attribute in `Domain/Models/Widget.cs`
  - Properties: Id, TenantId, Name, Description (nullable), CreatedAt
  - Learned: `[MultiTenant]` attribute marks an entity for automatic tenant isolation

### Project Management
- [x] Created `project-management/` with roadmap, tasks, decisions, learning-log, progress

---

## Blocked

Nothing currently blocked.

---

## 📌 Session Handoff — Start Here Next Time

Welcome back! Here's what you need to know:

1. **We're on Step 4** — create `Api/Data/AppDbContext.cs`
2. **Build passes** — `dotnet build` from `apps/platform/`
3. **All Finbuckle packages are installed** — you don't need to add them again
4. **Key Finbuckle 10.x API difference**: `MultiTenantDbContext` constructor takes `IMultiTenantContextAccessor`, NOT `ITenantInfo`
5. **The `[MultiTenant]` attribute vs fluent API**: Currently on Widget.cs. Can switch to `entity.IsMultiTenant()` in DbContext later
6. **Need help?** Ask for Step 4 details
