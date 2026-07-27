# Tessra — Project Progress

> **Last updated**: 2026-07-27 (end of Session 1)
> **Purpose**: Single source of truth for project state across sessions.
> **How to use**: Start here every session. Read this file first, then open `tasks.md` for the checklist.

---

## 🗺️ Session Handoff — Quick Summary

| Item | Status |
|------|--------|
| **Current milestone** | Finbuckle Multi-Tenant Integration |
| **Last completed step** | Step 3 — Created `Widget.cs` |
| **Next step** | Step 4 — Create `AppDbContext` in `Api/Data/AppDbContext.cs` |
| **Build status** | ✅ Builds with 0 errors |
| **Blockers** | None |

---

## 🏗️ What We've Built

### C# Platform Service (`apps/platform/`)

**Infrastructure:**
- ✅ ASP.NET Core minimal API targeting .NET 10
- ✅ Serilog structured logging with console sink
- ✅ `ExceptionHandlingMiddleware` — global exception → HTTP status
- ✅ `RequestLoggingMiddleware` — logs method, path, status, duration
- ✅ `ApiErrorResponse` — standardised JSON error shape
- ✅ `GET /health` — returns `{ status: "healthy", timestamp }`
- ✅ OpenAPI enabled in development

**Project Structure:**
- ✅ Multi-project solution with 3 class libraries
- ✅ `Tessra.Platform.slnx` solution file
- ✅ `Directory.Build.props` at repo root (shared settings)
- ✅ **`Tessra.Platform.Api`** — ASP.NET Core host (Program.cs, middleware, config)
- ✅ **`Tessra.Platform.Domain`** — shared models (`ApiErrorResponse`, `Tenant`, `Widget`)
- ✅ **`Tessra.Platform.Observability`** — logging middleware (`RequestLoggingMiddleware`)
- ✅ Proper project references between all projects

### Finbuckle Multi-Tenant Integration (In Progress)

| Step | What | File | Status |
|------|------|------|--------|
| 1 | Install NuGet packages | `Api.csproj` | ✅ Done |
| 2 | Create `Tenant` model (`ITenantInfo`) | `Domain/Models/Tenant.cs` | ✅ Done |
| 3 | Create `Widget` entity (`[MultiTenant]`) | `Domain/Models/Widget.cs` | ✅ Done |
| 4 | Create `AppDbContext` (MultiTenantDbContext) | `Api/Data/AppDbContext.cs` | ☐ **Next** |
| 5 | Configure Finbuckle in `Program.cs` | `Api/Program.cs` | ☐ Pending |
| 6 | Add tenant-aware endpoints | `Api/Program.cs` | ☐ Pending |

### Architecture Decisions Locked In

| Decision | Choice |
|----------|--------|
| ☁️ Cloud | **Fly.io** |
| 🏢 Multitenancy | **Shared DB + tenant_id** with **Finbuckle.MultiTenant** |
| 🔐 Authentication | **Roll our own JWT** |

---

## 📋 What's Next (Step 4 Details)

**Create `Api/Data/AppDbContext.cs`** — this file will:
1. Inherit from `MultiTenantDbContext` (from Finbuckle)
2. Take `IMultiTenantContextAccessor` in the constructor (Finbuckle 10.x API)
3. Expose `DbSet<Widget> Widgets` 
4. Configure the `Widget` entity mapping in `OnModelCreating`
5. Call `entity.IsMultiTenant()` in the fluent API (alternative to the `[MultiTenant]` attribute)

**Required NuGet packages already installed:**
- `Finbuckle.MultiTenant` 10.1.2
- `Finbuckle.MultiTenant.AspNetCore` 10.1.2
- `Finbuckle.MultiTenant.EntityFrameworkCore` 10.1.2

---

## ⚠️ Known Issues

| Issue | Severity | Notes |
|-------|----------|-------|
| NU1903 — Microsoft.OpenApi vulnerability | Low | Transitive dependency from Microsoft.AspNetCore.OpenApi 10.0.10. Will resolve with SDK update. |

---

## 💭 Technical Debt

| Item | Impact | Future Fix |
|------|--------|------------|
| `[MultiTenant]` attribute on Widget.cs brings EF Core concern into Domain project | Architectural impurity | Move to fluent API (`entity.IsMultiTenant()`) in AppDbContext when convenient |

---

## 📁 Project File Tree (for quick reference)

```
Tessra/
├── Directory.Build.props
├── project-management/
│   ├── roadmap.md
│   ├── tasks.md
│   ├── decisions.md
│   ├── learning-log.md
│   └── progress.md            ← Start here each session
├── apps/platform/
│   ├── Tessra.Platform.slnx
│   ├── Guide.md
│   └── src/
│       ├── Tessra.Platform.Api/
│       │   ├── Middleware/ExceptionHandlingMiddleware.cs
│       │   ├── Program.cs
│       │   ├── Tessra.Platform.Api.csproj
│       │   └── Properties/launchSettings.json
│       ├── Tessra.Platform.Domain/
│       │   ├── Models/
│       │   │   ├── ApiErrorResponse.cs
│       │   │   ├── Tenant.cs
│       │   │   └── Widget.cs
│       │   └── Tessra.Platform.Domain.csproj
│       └── Tessra.Platform.Observability/
│           ├── Middleware/RequestLoggingMiddleware.cs
│           └── Tessra.Platform.Observability.csproj
```
