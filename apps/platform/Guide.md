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

## Current State (2026-07-27)

### ✅ Implemented

**Infrastructure:**
- **Serilog structured logging** configured on startup (`Program.cs`)
- **`ExceptionHandlingMiddleware`** — global exception handler mapping exceptions to proper HTTP status codes with consistent JSON error responses
- **`RequestLoggingMiddleware`** — logs HTTP method, path, status code, and duration for every request
- **`TenantValidationMiddleware`** — validates `X-Tenant-Id` header, returns 400 if missing (excludes `/health`, `/openapi`)
- **`ApiErrorResponse`** model — standardised error shape: status code, message, optional details (stack trace in dev), trace ID, timestamp
- **`GET /health`** — returns `{ status: "healthy", timestamp }` (no tenant header required)
- **OpenAPI support** — enabled in development mode

**Multi-Tenancy (Finbuckle):**
- **Finbuckle Multi-Tenant** with header strategy (`X-Tenant-Id`)
- **2 seeded tenants**: Alpha Corp (`alpha-corp`), Beta Industries (`beta-industries`)
- **`AppDbContext`** — inherits `MultiTenantDbContext` with `DbSet<Widget>`, `DbSet<User>`, `DbSet<RefreshToken>`
- **Full Widget CRUD** with tenant isolation via `entity.IsMultiTenant()`
- **In-memory tenant store** (for development)

**Authentication (JWT):**
- **`POST /auth/register`** — creates user with BCrypt-hashed password, returns access + refresh token pair
- **`POST /auth/login`** — verifies credentials, returns token pair
- **`POST /auth/refresh`** — exchanges refresh token for new token pair
- **JWT access tokens** (15min expiry) with `tenant_id` and `tenant_identifier` claims
- **Refresh tokens** (7-day expiry, 64-byte crypto-random, stored in database)
- **All widget endpoints protected** via `.RequireAuthorization()` — 401 without valid JWT
- **Middleware pipeline**: Exception → Logging → TenantValidation → MultiTenant → Authentication → Authorization → Endpoints

**Docker & Database:**
- **Docker Compose** with .NET 10 API + PostgreSQL 16
- **Multi-stage Dockerfile** with layer caching and non-root user
- **EF Core Migrations** — `InitialCreate` (Widgets) + `AddAuthTables` (Users, RefreshTokens)
- **Dual-provider support**: PostgreSQL via Docker, InMemory fallback for local dev
- **Migration on startup** with `IsRelational()` guard for InMemory safety

**Project Structure:**
- **Multi-project solution**: `Tessra.Platform.slnx`
- **`Tessra.Platform.Api`** — ASP.NET Core host (entry point, configuration, pipeline, data, services)
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

### 📋 Testing the Auth Flow

```bash
# 1. Register a user
curl -X POST -H "Content-Type: application/json" \
  -H "X-Tenant-Id: alpha-corp" \
  -d '{"email":"alice@test.com","password":"Password123!"}' \
  http://localhost:5000/auth/register

# 2. Login (save the tokens from the response)
curl -X POST -H "Content-Type: application/json" \
  -H "X-Tenant-Id: alpha-corp" \
  -d '{"email":"alice@test.com","password":"Password123!"}' \
  http://localhost:5000/auth/login

# 3. Access protected endpoints with the token
curl -H "Authorization: Bearer <accessToken>" \
  -H "X-Tenant-Id: alpha-corp" \
  http://localhost:5000/widgets

# 4. Refresh the token
curl -X POST -H "Content-Type: application/json" \
  -H "X-Tenant-Id: alpha-corp" \
  -d '{"refreshToken":"<refreshToken>"}' \
  http://localhost:5000/auth/refresh
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
    │   │   └── AppDbContextFactory.cs ← Design-time factory for migrations
    │   ├── Endpoints/
    │   │   ├── WidgetEndpoints.cs     ← CRUD: GET/POST/PUT/DELETE /widgets
    │   │   └── AuthEndpoints.cs       ← Auth: POST /auth/register|login|refresh
    │   ├── Middleware/
    │   │   ├── ExceptionHandlingMiddleware.cs
    │   │   └── TenantValidationMiddleware.cs
    │   ├── Services/
    │   │   └── AuthService.cs         ← BCrypt, JWT generation, refresh tokens
    │   ├── Migrations/                ← EF Core migration files
    │   ├── Program.cs                 ← Service registration, middleware pipeline
    │   ├── appsettings.json
    │   ├── appsettings.Development.json
    │   ├── appsettings.Docker.json
    │   ├── Tessra.Platform.Api.csproj
    │   └── Properties/launchSettings.json
    ├── Tessra.Platform.Domain/       ← Shared domain models
    │   ├── Models/
    │   │   ├── ApiErrorResponse.cs
    │   │   ├── Tenant.cs
    │   │   ├── Widget.cs
    │   │   ├── User.cs
    │   │   └── RefreshToken.cs
    │   └── Tessra.Platform.Domain.csproj
    └── Tessra.Platform.Observability/ ← Logging & monitoring
        ├── Middleware/RequestLoggingMiddleware.cs
        └── Tessra.Platform.Observability.csproj
```

### Project Management

All roadmap, tasks, decisions, learning log, and progress are tracked in:
``` 
/project-management/
├── roadmap.md         — Overall vision, milestones, phases
├── tasks.md           — Current sprint, completed tasks, next priorities
├── decisions.md       — Architecture Decision Records (why we chose what we chose)
├── learning-log.md    — C#, .NET, OOP concepts learned along the way
└── progress.md        — Running summary of what we've built and what's next
```

This is updated after meaningful progress.

---

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
