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
- **Serilog structured logging** configured on startup (`Program.cs`)
- **`ExceptionHandlingMiddleware`** — global exception handler mapping exceptions to proper HTTP status codes with consistent JSON error responses
- **`RequestLoggingMiddleware`** — logs HTTP method, path, status code, and duration for every request
- **`ApiErrorResponse`** model — standardised error shape: status code, message, optional details (stack trace in dev), trace ID, timestamp
- **`/health` endpoint** — returns `{ status: "healthy", timestamp }`
- **OpenAPI support** — enabled in development mode

### 🏗️ In Progress
- Solution restructured into class libraries by concern
- Project management files created

### 📋 Next Up
See `project-management/` files for detailed roadmap and tasks.

---

## Project Structure

```
apps/platform/
├── Tessra.Platform.slnx                   ← Solution file
├── src/
│   ├── Tessra.Platform.Api/               ← ASP.NET Core host (Program.cs, middleware)
│   │   ├── Middleware/
│   │   │   └── ExceptionHandlingMiddleware.cs
│   │   ├── Properties/
│   │   │   └── launchSettings.json
│   │   ├── appsettings.json
│   │   ├── appsettings.Development.json
│   │   └── Tessra.Platform.Api.csproj
│   ├── Tessra.Platform.Domain/            ← Shared domain models
│   │   ├── Models/
│   │   │   └── ApiErrorResponse.cs
│   │   └── Tessra.Platform.Domain.csproj
│   └── Tessra.Platform.Observability/      ← Logging & monitoring conventions
│       ├── Middleware/
│       │   └── RequestLoggingMiddleware.cs
│       └── Tessra.Platform.Observability.csproj
└── Guide.md
```

---

## Project Management

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

## Learning-First Approach

Every feature we build is also a learning opportunity. New C# syntax, .NET concepts, OOP principles, and design patterns will be explained as we go. See `project-management/learning-log.md` for what we've covered so far.
