# Tessera — Learning Log

> 📐 **Architecture**: see **[`docs/ARCHITECTURE.md`](../docs/ARCHITECTURE.md)** — the single source of truth for how the system fits together.

> Last updated: 2026-07-28 (end of Session 8)
> 
> Only concepts that have been **explained and discussed** with the user are listed here.
> Concepts from the initial (reverted) implementation are excluded until they are
> re-taught step by step.

## C# Concepts

### Covered
| Concept | Where | Date |
|---------|-------|------|
| **Top-level statements** | `Program.cs` — entry point without explicit `class Program` / `Main` method | 2026-07-27 |
| **Nullable reference types** | Enabled via `<Nullable>enable</Nullable>` — compiler warns about potential nulls | 2026-07-27 |
| **Implicit usings** | Enabled via `<ImplicitUsings>enable</ImplicitUsings>` — common namespaces auto-imported | 2026-07-27 |
| **Records / init-only properties** | `ApiErrorResponse` uses `{ get; init; }` for immutable response objects | 2026-07-27 |
| **Switch expressions** | `ExceptionHandlingMiddleware.MapException()` uses pattern matching `exception switch` | 2026-07-27 |
| **Tuple return types** | `MapException` returns `(int StatusCode, string Message, LogEventLevel)` | 2026-07-27 |
| **Nullable annotations** | `string? Details` in `ApiErrorResponse` — the `?` marks it as nullable | 2026-07-27 |
| **Records for DTOs** | `RegisterRequest`, `TokenResponse` — immutable data transfer objects with positional syntax | 2026-07-27 |

## .NET Concepts

### Covered
| Concept | Where | Date |
|---------|-------|------|
| **Middleware pipeline** | `ExceptionHandlingMiddleware` and `RequestLoggingMiddleware` — request delegates chained together | 2026-07-27 |
| **Minimal APIs** | `app.MapGet("/health", ...)` — lightweight endpoint definitions | 2026-07-27 |
| **Dependency Injection** | `IHostEnvironment` injected into `ExceptionHandlingMiddleware` constructor | 2026-07-27 |
| **Serilog integration** | `UseSerilog()` on host builder — replaces default .NET logging | 2026-07-27 |
| **OpenAPI integration** | `AddOpenApi()` + `MapOpenApi()` — auto-generates OpenAPI docs | 2026-07-27 |
| **Configuration / Options pattern** | `appsettings.json` and `appsettings.Development.json` — environment-based config | 2026-07-27 |
| **Solution files (.sln/.slnx)** | `Tessera.Platform.slnx` — container referencing multiple projects | 2026-07-27 |
| **Class libraries** | Domain and Observability projects — produce .dll, not executable | 2026-07-27 |
| **Project references** | Api → Domain & Observability via `<ProjectReference>` in .csproj | 2026-07-27 |
| **Directory.Build.props** | Shared MSBuild properties at repo root — applies to all child projects | 2026-07-27 |
| **FrameworkReference** | Observability uses `Microsoft.AspNetCore.App` to access ASP.NET Core types | 2026-07-27 |
| **.slnx format** | New XML-based solution file format in .NET 10 (replaces .sln) | 2026-07-27 |
| **ITenantInfo** | Interface for tenant data — `Id` (internal key) vs `Identifier` (lookup key) vs `Name` | 2026-07-27 |
| **[MultiTenant] attribute** | Marks an entity for automatic tenant isolation — auto-sets TenantId and adds global query filter | 2026-07-27 |
| **DbContextOptions<T>** | `AppDbContext.cs` — standard EF Core config (provider, connection string) | 2026-07-27 |
| **DbSet<T>** | `AppDbContext.cs` — property representing a database table for LINQ queries | 2026-07-27 |
| **OnModelCreating** | `AppDbContext.cs` — called once at startup to define database schema via fluent API | 2026-07-27 |
| **Fluent API** | `AppDbContext.cs` — chaining methods like `.HasKey()`, `.IsRequired()`, `.HasMaxLength()` | 2026-07-27 |
| **Global query filters** | `entity.IsMultiTenant()` — automatic `WHERE TenantId = @current` on every query | 2026-07-27 |
| **AddMultiTenant<T>() service registration** | `Program.cs` — registers Finbuckle core services | 2026-07-27 |
| **Header strategy** | `.WithHeaderStrategy("X-Tenant-Id")` — resolves tenant from HTTP header | 2026-07-27 |
| **In-memory store** | `.WithInMemoryStore(...)` — seeds tenants in memory for dev/testing | 2026-07-27 |
| **UseMultiTenant() middleware** | `Program.cs` — reads header, resolves tenant, sets `IMultiTenantContext` per-request | 2026-07-27 |
| **EnforceMultiTenant** | Auto-sets `TenantId` to current tenant on save when null; throws if mismatch | 2026-07-27 |
| **IMultiTenantContextAccessor** | Singleton using `AsyncLocal<T>` — flows tenant context per-request | 2026-07-27 |
| **InMemory database provider** | `UseInMemoryDatabase("TesseraPlatformDb")` — ephemeral, in-process RAM storage | 2026-07-27 |
| **MapGroup()** | `app.MapGroup("/widgets")` — groups routes under a common prefix | 2026-07-27 |
| **Extension methods for endpoint organization** | Static extension method on `WebApplication` to keep Program.cs clean | 2026-07-27 |
| **Route constraint `{id:guid}`** | Restricts route parameter to valid GUIDs | 2026-07-27 |
| **REST conventions** | `201 Created` for POST, `204 No Content` for DELETE, `404 NotFound` for missing resources | 2026-07-27 |
| **JWT (JSON Web Token)** | `AuthService.cs` — stateless auth token with header, payload (claims), and signature | 2026-07-27 |
| **JWT claims** | `sub`, `email`, `tenant_id`, `tenant_identifier`, `jti`, `iat` — key-value pairs in token payload | 2026-07-27 |
| **Symmetric signing (HMAC-SHA256)** | `AuthService.cs` — same key signs and verifies tokens (dev only; use asymmetric for production) | 2026-07-27 |
| **AddAuthentication / AddJwtBearer** | `Program.cs` — registers JWT bearer as the default auth scheme | 2026-07-27 |
| **TokenValidationParameters** | `Program.cs` — configures issuer, audience, lifetime, signing key validation | 2026-07-27 |
| **UseAuthentication / UseAuthorization** | `Program.cs` — middleware that validates tokens and enforces policies | 2026-07-27 |
| **RequireAuthorization()** | `WidgetEndpoints.cs` — protects route group, returns 401 if no valid token | 2026-07-27 |
| **ClaimTypes.Role** | `AuthService.cs` — standard JWT claim type for role-based authorization | 2026-07-28 |
| **Authorization policies** | `Program.cs` — named policies (e.g., `"AdminOnly"`) that encapsulate authorization rules | 2026-07-28 |
| **Seed data / bootstrapping** | `Program.cs` — creating initial admin users on startup to solve chicken-and-egg problem | 2026-07-28 |
| **Raw SQL for seed (ExecuteSqlRawAsync)** | `Program.cs` — bypasses EF Core tracking + EnforceMultiTenant during startup when no HTTP context exists | 2026-07-28 |
| **IgnoreQueryFilters()** | `Program.cs` — bypasses global query filters when checking for existing users during startup | 2026-07-28 |
| **Tenant claim validation** | `TenantClaimValidationMiddleware` — comparing JWT `tenant_identifier` claim against `X-Tenant-Id` header to prevent cross-tenant token reuse | 2026-07-28 |
| **FirstOrDefaultAsync vs FindAsync** | `WidgetEndpoints.cs`, `AuthService.cs` — `FindAsync` bypasses global query filters; `FirstOrDefaultAsync` respects them | 2026-07-28 |
| **BCrypt password hashing** | `AuthService.cs` — one-way hashing with automatic salting, computationally expensive | 2026-07-27 |
| **RandomNumberGenerator** | `AuthService.cs` — cryptographically secure random bytes for refresh tokens | 2026-07-27 |
| **IDesignTimeDbContextFactory<T>** | `AppDbContextFactory.cs` — tells EF Core tools how to create DbContext for migrations | 2026-07-27 |
| **Migrate() vs EnsureCreated()** | `Program.cs` — versioned vs. one-shot schema creation | 2026-07-27 |
| **IsRelational()** | `Program.cs` — checks if the database provider is relational (safe for InMemory) | 2026-07-27 |
| **Middleware pipeline order** | `Program.cs` — Exception → Logging → TenantValidation → MultiTenant → AuthN → AuthZ → Endpoints | 2026-07-27 |
| **Clock skew** | `Program.cs` — `ClockSkew = TimeSpan.Zero` eliminates the default 5-minute token window | 2026-07-27 |

## OOP Concepts

### Covered
| Concept | Where | Date |
|---------|-------|------|
| **Encapsulation** | Middleware classes encapsulate their logic behind a public `InvokeAsync` method | 2026-07-27 |
| **Single Responsibility** | Each middleware does one thing (exception handling OR request logging, not both) | 2026-07-27 |
| **Constructor Injection** | Dependencies passed via constructor rather than created internally | 2026-07-27 |
| **Interfaces** | `ITenantInfo` — a contract that `Tenant` must fulfill (Id, Identifier, Name, ConnectionString) | 2026-07-27 |
| **Inheritance** | `AppDbContext : MultiTenantDbContext` — child class inherits/reuses base class behavior | 2026-07-27 |
| **Constructor Chaining** | `: base(...)` — child constructor passes arguments to parent constructor | 2026-07-27 |
| **Service Layer Pattern** | `AuthService` — encapsulates business logic (auth), keeps endpoints thin | 2026-07-27 |
| **Factory Pattern** | `AppDbContextFactory` — creates DbContext instances for EF Core tooling | 2026-07-27 |
| **Result Object Pattern** | `AuthResult` — standardised success/failure response with properties | 2026-07-27 |
| **Static factory methods** | `AuthResult.SuccessMessage()` — clean object creation with descriptive method names | 2026-07-28 |
| **Static constants class** | `Roles` — grouping related constants in a dedicated static class for type-safety | 2026-07-28 |

## MCP & Python Concepts (Session 11)

### Covered
| Concept | Where | Date |
|---------|-------|------|
| **MCP (Model Context Protocol)** | `apps/mcp-server` — the open protocol for connecting AI assistants to external tools/services | 2026-08-14 |
| **MCP tools** | `server.py` — capabilities exposed to an assistant: `name` + `description` + JSON `input_schema`; calls return content blocks | 2026-08-14 |
| **MCP transports** | `__main__.py` — **stdio** (client launches the server as a local process) vs **HTTP/streamable** (hosted URL). ChatGPT/Gemini can only use HTTP — stdio is dev-only for us | 2026-08-14 |
| **Bridge pattern (product sense)** | The MCP server is a *consumer* of the C# platform (JWT + `X-Tenant-Id`) — the platform stays the single source of truth; the server never re-implements auth/authz | 2026-08-14 |
| **`mcp` 2.0 low-level API** | `Server(name, on_list_tools=…, on_call_tool=…)` constructor kwargs (the 1.x `@server.list_tools()` decorators are gone); `Tool(input_schema=…)`, `ListToolsResult`, `CallToolResult` | 2026-08-14 |
| **Structured tool errors** | `CallToolResult(is_error=True)` — a platform 403 (future action enforcement) must reach the assistant as a readable message, not a crashed request | 2026-08-14 |
| **uv** | `apps/mcp-server` — fast Python package/project manager (replaces pip + venv + pip-tools); `uv sync`, `uv run`, `.python-version` pinning | 2026-08-14 |
| **ruff / mypy strict / pytest** | `apps/mcp-server` — lint+format in one tool; `strict = true` typing from day one; pytest with `anyio_mode = "auto"` (anyio's pytest plugin only rewrites async tests when the ini option or a marker is present) | 2026-08-14 |
| **`src/` layout** | `src/tessera_mcp/` + `tests/` — installable package layout (vs flat scripts), standard for Python projects | 2026-08-14 |
| **httpx.MockTransport** | `tests/` — inject a fake HTTP transport into `httpx.AsyncClient` to unit-test the client with zero network | 2026-08-14 |
| **Live integration testing** | `tests/test_integration_live.py` — spawn the real server via `mcp.client.stdio.stdio_client`, skip automatically when the platform is down | 2026-08-14 |

## Libraries Introduced
| Library | Version | Purpose | Date |
|---------|---------|---------|------|
| `Serilog.AspNetCore` | 10.0.0 | Structured logging integration | 2026-07-27 |
| `Serilog.Sinks.Console` | 6.1.1 | Console output for logs | 2026-07-27 |
| `Microsoft.AspNetCore.OpenApi` | 10.0.10 | OpenAPI document generation | 2026-07-27 |
| `Finbuckle.MultiTenant` | 10.1.2 | Core multi-tenancy library | 2026-07-27 |
| `Finbuckle.MultiTenant.AspNetCore` | 10.1.2 | ASP.NET Core integration (middleware, strategies) | 2026-07-27 |
| `Finbuckle.MultiTenant.EntityFrameworkCore` | 10.1.2 | EF Core integration (MultiTenantDbContext) | 2026-07-27 |
| `Finbuckle.MultiTenant.Abstractions` | 10.1.2 | Interfaces and attributes (ITenantInfo, [MultiTenant]) | 2026-07-27 |
| `Microsoft.EntityFrameworkCore.InMemory` | 10.0.10 | InMemory database provider (dev/testing) | 2026-07-27 |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | 10.0.0 | PostgreSQL EF Core provider | 2026-07-27 |
| `Microsoft.EntityFrameworkCore.Design` | 10.0.10 | EF Core CLI tools support (migrations) | 2026-07-27 |
| `BCrypt.Net-Next` | 4.0.3 | BCrypt password hashing | 2026-07-27 |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | 10.0.10 | JWT bearer token authentication | 2026-07-27 |
| `mcp` (official MCP Python SDK) | 2.0.0 | MCP server (low-level `mcp.server.Server`) + client (`mcp.client.stdio`) | 2026-08-14 |
| `httpx` | 0.28.x | Async HTTP client for `TesseraClient` (+ `MockTransport` for tests) | 2026-08-14 |
| `uv` / `ruff` / `mypy` / `pytest` | — | Python toolchain in `apps/mcp-server` | 2026-08-14 |
