# Tessra — Learning Log

> Last updated: 2026-07-27 (end of Session 1)
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
| **Solution files (.sln/.slnx)** | `Tessra.Platform.slnx` — container referencing multiple projects | 2026-07-27 |
| **Class libraries** | Domain and Observability projects — produce .dll, not executable | 2026-07-27 |
| **Project references** | Api → Domain & Observability via `<ProjectReference>` in .csproj | 2026-07-27 |
| **Directory.Build.props** | Shared MSBuild properties at repo root — applies to all child projects | 2026-07-27 |
| **FrameworkReference** | Observability uses `Microsoft.AspNetCore.App` to access ASP.NET Core types | 2026-07-27 |
| **.slnx format** | New XML-based solution file format in .NET 10 (replaces .sln) | 2026-07-27 |
| **ITenantInfo** | Interface for tenant data — `Id` (internal key) vs `Identifier` (lookup key) vs `Name` | 2026-07-27 |
| **[MultiTenant] attribute** | Marks an entity for automatic tenant isolation — auto-sets TenantId and adds global query filter | 2026-07-27 |

### Upcoming (will be covered in Steps 4-6)
| Concept | Expected In |
|---------|-------------|
| `MultiTenantDbContext` | Step 4 |
| `IMultiTenantContextAccessor` | Step 4 |
| `entity.IsMultiTenant()` fluent API | Step 4 |
| `AddMultiTenant<T>()` service registration | Step 5 |
| Header strategy, in-memory store | Step 5 |
| `UseMultiTenant()` middleware | Step 5 |
| Global query filters at runtime | Step 6 |

## OOP Concepts

### Covered
| Concept | Where | Date |
|---------|-------|------|
| **Encapsulation** | Middleware classes encapsulate their logic behind a public `InvokeAsync` method | 2026-07-27 |
| **Single Responsibility** | Each middleware does one thing (exception handling OR request logging, not both) | 2026-07-27 |
| **Constructor Injection** | Dependencies passed via constructor rather than created internally | 2026-07-27 |
| **Interfaces** | `ITenantInfo` — a contract that `Tenant` must fulfill (Id, Identifier, Name, ConnectionString) | 2026-07-27 |

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
