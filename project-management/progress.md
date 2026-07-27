# Tessra — Project Progress

> **Last updated**: 2026-07-27 (end of Session 7)
> **Purpose**: Single source of truth for project state across sessions.
> **How to use**: Start here every session. Read this file first, then open `tasks.md` for the checklist.

---

## 🗺️ Session Handoff — Quick Summary

| Item | Status |
|------|--------|
| **Current milestone** | Authentication (JWT) ✅ Complete |
| **Last completed step** | Full JWT auth: register, login, refresh tokens, widget endpoints protected |
| **Next step** | Choose next milestone (Role-based auth / CI pipeline / more endpoints) |
| **Build status** | ✅ Builds with 0 errors |
| **Docker status** | ✅ Up and running at `http://localhost:5000` with PostgreSQL |
| **Blockers** | None |

---

## 🏗️ What We've Built

### C# Platform Service (`apps/platform/`)

**Infrastructure:**
- ✅ ASP.NET Core minimal API targeting .NET 10
- ✅ Serilog structured logging with console sink
- ✅ `ExceptionHandlingMiddleware` — global exception → HTTP status
- ✅ `RequestLoggingMiddleware` — logs method, path, status, duration
- ✅ `TenantValidationMiddleware` — validates `X-Tenant-Id` header, returns 400 if missing
- ✅ `ApiErrorResponse` — standardised JSON error shape
- ✅ `GET /health` — returns `{ status: "healthy", timestamp }`
- ✅ OpenAPI enabled in development
- ✅ **EF Core Migrations** — `InitialCreate` + `AddAuthTables` with `Migrate()` on startup

**Authentication (JWT):**
- ✅ `User` entity with BCrypt password hashing
- ✅ `RefreshToken` entity with crypto-random tokens, stored in database
- ✅ `POST /auth/register` — creates user, returns access + refresh tokens
- ✅ `POST /auth/login` — verifies credentials, returns token pair
- ✅ `POST /auth/refresh` — issues new token pair from refresh token
- ✅ **JWT access tokens** (15min) with `tenant_id` and `tenant_identifier` claims
- ✅ **All widget endpoints protected** with `[Authorize]` (return 401 without token)
- ✅ **Middleware order**: Exception → Logging → TenantValidation → MultiTenant → Authentication → Authorization → Endpoints

**Project Structure:**
- ✅ Multi-project solution with 3 class libraries
- ✅ `Tessra.Platform.slnx` solution file
- ✅ `Directory.Build.props` at repo root (shared settings)
- ✅ **`Tessra.Platform.Api`** — ASP.NET Core host (Program.cs, middleware, config, data, services, endpoints)
- ✅ **`Tessra.Platform.Domain`** — shared models (`ApiErrorResponse`, `Tenant`, `Widget`, `User`, `RefreshToken`)
- ✅ **`Tessra.Platform.Observability`** — logging middleware (`RequestLoggingMiddleware`)
- ✅ Proper project references between all projects

### Finbuckle Multi-Tenant Integration (Complete 🎉)

| Step | What | File | Status |
|------|------|------|--------|
| 1 | Install NuGet packages | `Api.csproj` | ✅ Done |
| 2 | Create `Tenant` model (`ITenantInfo`) | `Domain/Models/Tenant.cs` | ✅ Done |
| 3 | Create `Widget` entity | `Domain/Models/Widget.cs` | ✅ Done |
| 4 | Create `AppDbContext` (MultiTenantDbContext) | `Api/Data/AppDbContext.cs` | ✅ Done |
| 5 | Configure Finbuckle in `Program.cs` | `Api/Program.cs` | ✅ Done |
| 6 | Add full widget CRUD + refactor to `Endpoints/` | `Api/Endpoints/WidgetEndpoints.cs` | ✅ Done |

### Architecture Decisions Locked In

| Decision | Choice |
|----------|--------|
| ☁️ Cloud | **Fly.io** |
| 🏢 Multitenancy | **Shared DB + tenant_id** with **Finbuckle.MultiTenant** |
| 🔐 Authentication | **Roll our own JWT** — BCrypt + access/refresh tokens + tenant claims |
| 🐳 Local Dev | **Docker Compose** with .NET API + PostgreSQL 16 |

---

## 📋 What We Built This Session

### Authentication (JWT)

**Goal:** Implement user registration, login, JWT token issuance, and protect API endpoints.

**New models (`Domain/Models/`):**
- **`User`** — Id, Email, PasswordHash, TenantId, CreatedAt. Multi-tenant via `IsMultiTenant()`.
- **`RefreshToken`** — Id, UserId, Token (64-byte crypto-random), ExpiresAt, IsRevoked, TenantId. Multi-tenant via `IsMultiTenant()`.

**Auth endpoints (`Endpoints/AuthEndpoints.cs`):**
| Endpoint | Method | Description |
|----------|--------|-------------|
| `/auth/register` | POST | Create user with email + password (BCrypt hashed), returns token pair |
| `/auth/login` | POST | Verify credentials, returns token pair |
| `/auth/refresh` | POST | Exchange refresh token for new token pair |

**Auth service (`Services/AuthService.cs`):**
- **Password hashing:** BCrypt.Net-Next v4.0.3 — `BCrypt.HashPassword()` with automatic salting
- **JWT generation:** `System.IdentityModel.Tokens.Jwt` — HMAC-SHA256 symmetric key, 15min expiry
- **Token claims:** `sub` (userId), `email`, `tenant_id`, `tenant_identifier`, `jti`, `iat`
- **Refresh tokens:** 64 cryptographically random bytes from `RandomNumberGenerator`, 7-day expiry, stored in database
- **Input validation:** Duplicate email rejected per-tenant via global query filter
- **AuthResult model:** Standardised success/failure response with `IsSuccess`, `AccessToken`, `RefreshToken`, `ErrorMessage`

**JWT configuration (`Program.cs`):**
- `AddAuthentication().AddJwtBearer()` with full `TokenValidationParameters` (issuer, audience, lifetime, signing key, zero clock skew)
- `UseAuthentication()` / `UseAuthorization()` middleware placed after `UseMultiTenant()` and before endpoints
- `ClockSkew = TimeSpan.Zero` — no token replay window

**Widget protection (`WidgetEndpoints.cs`):**
- `.RequireAuthorization()` added to `/widgets` MapGroup
- Requests without valid `Authorization: Bearer <token>` header return **401 Unauthorized**

**New packages:**
| Package | Version | Purpose |
|---------|---------|---------|
| `BCrypt.Net-Next` | 4.0.3 | BCrypt password hashing |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | 10.0.10 | JWT bearer token authentication |

**Migration:** `AddAuthTables` — creates `Users` and `RefreshTokens` tables

**End-to-end verified:**
| Test | Result |
|------|--------|
| Register user | ✅ 201 with token pair |
| Login | ✅ 200 with token pair |
| List widgets with valid token | ✅ 200 |
| Widgets WITHOUT token | ✅ **401 Unauthorized** |
| Create widget with token | ✅ 201 |
| Refresh token | ✅ 200 with new token pair |
| Duplicate email | ✅ 400 |

---

## ⚠️ Known Issues

| Issue | Severity | Notes |
|-------|----------|-------|
| NU1903 — Microsoft.OpenApi vulnerability | Low | Transitive dependency from Microsoft.AspNetCore.OpenApi 10.0.10. Will resolve with SDK update. |

---

## 📁 Project File Tree (for quick reference)

```
Tessra/
├── Directory.Build.props
├── .dockerignore
├── project-management/
│   ├── roadmap.md
│   ├── tasks.md
│   ├── decisions.md
│   ├── learning-log.md
│   └── progress.md            ← Start here each session
├── apps/platform/
│   ├── Dockerfile
│   ├── docker-compose.yml
│   ├── Tessra.Platform.slnx
│   ├── Guide.md
│   └── src/
│       ├── Tessra.Platform.Api/
│       │   ├── Data/
│       │   │   ├── AppDbContext.cs
│       │   │   └── AppDbContextFactory.cs
│       │   ├── Migrations/
│       │   │   ├── *_InitialCreate.cs
│       │   │   ├── *_AddAuthTables.cs
│       │   │   └── AppDbContextModelSnapshot.cs
│       │   ├── Endpoints/
│       │   │   ├── WidgetEndpoints.cs
│       │   │   └── AuthEndpoints.cs          ← NEW
│       │   ├── Middleware/
│       │   │   ├── ExceptionHandlingMiddleware.cs
│       │   │   └── TenantValidationMiddleware.cs
│       │   ├── Services/
│       │   │   └── AuthService.cs            ← NEW
│       │   ├── Program.cs
│       │   ├── appsettings.json
│       │   ├── appsettings.Development.json
│       │   ├── appsettings.Docker.json
│       │   ├── Tessra.Platform.Api.csproj
│       │   └── Properties/launchSettings.json
│       ├── Tessra.Platform.Domain/
│       │   ├── Models/
│       │   │   ├── ApiErrorResponse.cs
│       │   │   ├── Tenant.cs
│       │   │   ├── Widget.cs
│       │   │   ├── User.cs                  ← NEW
│       │   │   └── RefreshToken.cs          ← NEW
│       │   └── Tessra.Platform.Domain.csproj
│       └── Tessra.Platform.Observability/
│           ├── Middleware/RequestLoggingMiddleware.cs
│           └── Tessra.Platform.Observability.csproj
```
