# Tessra — Platform Service Guide

## Purpose

This is the **C# platform / core services layer** for Tessra — a multi-tenant SaaS infrastructure service. It provides shared cross-cutting concerns that every tenant-facing request flows through:

- **Authentication** — who is this user?
- **Authorization** — what are they allowed to do?
- **Billing** — what plan are they on, what entitlements do they have?
- **Observability** — logging, monitoring, metrics
- **Rate Limiting** — protect against abuse

The Python MCP server and Next.js frontend are **consumers** of these services, not owners of them.

---

## Current State (2026-07-28)

### ✅ Implemented

**Infrastructure:**
- **Serilog structured logging** configured on startup (`Program.cs`)
- **`ExceptionHandlingMiddleware`** — global exception handler mapping exceptions to proper HTTP status codes with consistent JSON error responses
- **`RequestLoggingMiddleware`** — logs HTTP method, path, status code, and duration for every request
- **`TenantValidationMiddleware`** — validates `X-Tenant-Id` header, returns 400 if missing (excludes `/health`, `/openapi`)
- **`TenantClaimValidationMiddleware`** — validates JWT `tenant_identifier` claim matches `X-Tenant-Id` header, returns 403 on mismatch
- **`ApiErrorResponse`** model — standardised error shape: status code, message, optional details, trace ID, timestamp
- **`GET /health`** — returns `{ status: "healthy", timestamp }` (no tenant header or auth required)
- **OpenAPI support** — enabled in development mode

**Multi-Tenancy (Finbuckle):**
- **Finbuckle Multi-Tenant** with header strategy (`X-Tenant-Id`)
- **2 seeded tenants**: Alpha Corp (`alpha-corp`), Beta Industries (`beta-industries`)
- **`AppDbContext`** — inherits `MultiTenantDbContext` with `DbSet<Widget>`, `DbSet<User>`, `DbSet<RefreshToken>`
- **Full Widget CRUD** with tenant isolation via `entity.IsMultiTenant()`
- **Tenant-scoped global query filters** on ALL entities — no accidental cross-tenant data leaks
- **All DB queries use `FirstOrDefaultAsync`** (not `FindAsync`) to respect query filters
- **Seed admin via raw SQL** — bypasses `EnforceMultiTenant` during startup
- **In-memory tenant store** (for development)

**Authentication & Authorization:**
- **`POST /auth/register`** — creates user with BCrypt-hashed password, returns access + refresh token pair
- **`POST /auth/login`** — verifies credentials, returns token pair
- **`POST /auth/refresh`** — exchanges refresh token for new token pair
- **`POST /auth/promote`** — admin-only endpoint to promote a user to Admin role
- **JWT access tokens** (15min expiry) with `sub`, `email`, `tenant_id`, `tenant_identifier`, `role` claims
- **Refresh tokens** (7-day expiry, 64-byte crypto-random, stored in database)
- **Cross-tenant token reuse blocked** (403 Forbidden)
- **RBAC**: `Admin` / `User` roles, `AdminOnly` authorization policy
- **DELETE widget requires `AdminOnly`** policy
- **All widget endpoints protected** via `.RequireAuthorization()`

**Middleware pipeline (in order):**
```
Exception → Logging → TenantValidation → MultiTenant → Authentication → TenantClaimValidation → Authorization → Endpoints
```

**Docker & Database:**
- **Docker Compose** with .NET 10 API + PostgreSQL 16
- **Multi-stage Dockerfile** with layer caching and non-root user
- **EF Core Migrations** — `InitialCreate` (Widgets) + `AddAuthTables` (Users, RefreshTokens) + `AddUserRole` (Role column)
- **Dual-provider support**: PostgreSQL via Docker, InMemory fallback for local dev
- **Migration on startup** with `IsRelational()` guard for InMemory safety

**Project Structure:**
- **Multi-project solution**: `Tessra.Platform.slnx`
- **`Tessra.Platform.Api`** — ASP.NET Core host (entry point, configuration, pipeline, data, services, endpoints)
- **`Tessra.Platform.Domain`** — shared domain models (`ApiErrorResponse`, `Tenant`, `Widget`, `User`, `RefreshToken`)
- **`Tessra.Platform.Observability`** — logging middleware (`RequestLoggingMiddleware`)
- **`Directory.Build.props`** at repo root for shared MSBuild properties
- **`/project-management/`** — roadmap, tasks, decisions, learning log, progress

---

### 📋 Running the API

**With Docker (recommended):**
```bash
cd apps/platform
docker compose up -d --build
# API at http://localhost:5000
```

**Without Docker (local dev):**
```bash
cd apps/platform
dotnet run --project src/Tessra.Platform.Api
# API at http://localhost:5085 (uses InMemory database)
```

### 📋 Testing the Full Auth + RBAC Flow

```bash
# 1. Register a user
curl -X POST -H "Content-Type: application/json" \
  -H "X-Tenant-Id: alpha-corp" \
  -d '{"email":"alice@test.com","password":"Password123!"}' \
  http://localhost:5000/auth/register

# 2. Login as seed admin (PostgreSQL only)
curl -X POST -H "Content-Type: application/json" \
  -H "X-Tenant-Id: alpha-corp" \
  -d '{"email":"admin@tessra.com","password":"Admin123!"}' \
  http://localhost:5000/auth/login

# 3. Access protected widgets with the token
curl -H "Authorization: Bearer <adminToken>" \
  -H "X-Tenant-Id: alpha-corp" \
  http://localhost:5000/widgets

# 4. Promote a user to Admin (admin-only)
curl -X POST -H "Content-Type: application/json" \
  -H "Authorization: Bearer <adminToken>" \
  -H "X-Tenant-Id: alpha-corp" \
  -d '{"email":"alice@test.com"}' \
  http://localhost:5000/auth/promote

# 5. Cross-tenant token test (should return 403)
curl -H "Authorization: Bearer <alphaToken>" \
  -H "X-Tenant-Id: beta-industries" \
  http://localhost:5000/widgets
```

---

## Project Structure

```
apps/platform/
├── Dockerfile                        ← Multi-stage .NET 10 build
├── docker-compose.yml                ← API + PostgreSQL 16
├── Tessra.Platform.slnx              ← Solution file
├── Guide.md
└── src/
    ├── Tessra.Platform.Api/          ← ASP.NET Core host
    │   ├── Data/
    │   │   ├── AppDbContext.cs
    │   │   └── AppDbContextFactory.cs
    │   ├── Endpoints/
    │   │   ├── WidgetEndpoints.cs     ← CRUD: GET/POST/PUT/DELETE /widgets
    │   │   └── AuthEndpoints.cs       ← Auth: register|login|refresh|promote
    │   ├── Middleware/
    │   │   ├── ExceptionHandlingMiddleware.cs
    │   │   ├── TenantValidationMiddleware.cs
    │   │   └── TenantClaimValidationMiddleware.cs  ← NEW
    │   ├── Services/
    │   │   └── AuthService.cs
    │   ├── Migrations/
    │   │   ├── *_InitialCreate.cs
    │   │   ├── *_AddAuthTables.cs
    │   │   ├── *_AddUserRole.cs                    ← NEW
    │   │   └── AppDbContextModelSnapshot.cs
    │   ├── Program.cs
    │   ├── appsettings.json
    │   ├── appsettings.Development.json
    │   ├── appsettings.Docker.json
    │   ├── Tessra.Platform.Api.csproj
    │   └── Properties/launchSettings.json
    ├── Tessra.Platform.Domain/
    │   └── Models/
    │       ├── ApiErrorResponse.cs
    │       ├── Tenant.cs
    │       ├── Widget.cs
    │       ├── User.cs
    │       └── RefreshToken.cs
    └── Tessra.Platform.Observability/
        └── Middleware/RequestLoggingMiddleware.cs
```

### Project Management

All roadmap, tasks, decisions, learning log, and progress are tracked in:
```
/project-management/
├── roadmap.md
├── tasks.md
├── decisions.md
├── learning-log.md
└── progress.md
```

Updated after meaningful progress.

---

## Architecture

### Service Architecture
```
[SMB User] → [Next.js UI]
                        ↘
                         [C# Platform API]  ← Auth, Multi-Tenant, RBAC
                        ↙
[ChatGPT / Claude / Gemini] → [MCP Server] → [Company A Database]
                                                [Company B Database]
```

The C# platform is the identity and tenant gateway. It authenticates users, resolves which SMB tenant they belong to, validates they're not crossing tenants, and forwards the tenant context downstream to the MCP server. The MCP server uses this tenant identity to connect to the correct company database.

### Middleware Pipeline
```
1. ExceptionHandlingMiddleware     ← Catches all unhandled exceptions
2. RequestLoggingMiddleware        ← Logs method, path, status, duration
3. TenantValidationMiddleware      ← Returns 400 if X-Tenant-Id missing
4. UseMultiTenant()                ← Resolves tenant from header
5. UseAuthentication()             ← Validates JWT
6. TenantClaimValidationMiddleware ← Returns 403 if JWT tenant ≠ header tenant
7. UseAuthorization()              ← Enforces role policies
8. Endpoints                       ← Business logic
```

## Design Patterns

### Endpoints Pattern

To add a new resource:
1. Create `Endpoints/YourResourceEndpoints.cs`
2. Create a static class with `public static void MapYourResourceEndpoints(this WebApplication app)`
3. Use `app.MapGroup("/your-resource")` to group routes under a common prefix
4. Add `.RequireAuthorization()` if the resource should be protected
5. Call `app.MapYourResourceEndpoints();` in `Program.cs`

### Service Layer Pattern

Business logic lives in `Services/` classes (e.g., `AuthService.cs`). Endpoints remain thin — they validate input, call the service, and format the response. Services receive their dependencies via constructor injection.

---

## Learning-First Approach

Every feature we build is also a learning opportunity. New C# syntax, .NET concepts, OOP principles, and design patterns will be explained as we go. See `project-management/learning-log.md` for what we've covered so far.
