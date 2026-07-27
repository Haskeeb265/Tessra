# Tessra — Architecture

## Overview

Tessra is a multi-tenant SaaS platform with three components:

```
┌──────────────┐     ┌───────────────────┐     ┌──────────────┐
│  Next.js     │────▶│  C# Platform      │◀────│  Python MCP  │
│  Frontend    │     │  Service           │     │  Server      │
│  (apps/web)  │     │  (apps/platform)   │     │  (apps/mcp)  │
└──────────────┘     └───────────────────┘     └──────────────┘
                           │
                           ▼
              ┌───────────────────────┐
              │  PostgreSQL / Redis   │
              │  (Backing Services)   │
              └───────────────────────┘
```

**Key rule**: The C# platform is the **source of truth** for auth, authz, tenant context, billing, and rate limiting. Python and Next.js consume its APIs rather than implementing their own versions of these concerns.

---

## C# Platform Service Architecture

### Current Structure

```
apps/platform/
├── Middleware/
│   ├── ExceptionHandlingMiddleware.cs   — Global exception → HTTP status mapping
│   └── RequestLoggingMiddleware.cs      — Logs method/path/status/duration
├── Models/
│   └── ApiErrorResponse.cs              — Standardised JSON error response
├── appsettings.json                     — Production configuration
├── appsettings.Development.json         — Development overrides
├── platform.csproj                      — Project file (.NET 10)
├── platform.http                        — HTTP request scratchpad (VS Code)
├── Program.cs                           — Application entry point
└── Properties/
    └── launchSettings.json              — Dev server URLs and profiles
```

### Planned Future Structure

```
Tessra.Platform.Api              # ASP.NET Core host — thin wiring layer
Tessra.Platform.Auth             # Authentication
Tessra.Platform.Authz            # Authorization / permissions / tenant roles
Tessra.Platform.Billing          # Billing & entitlements
Tessra.Platform.Observability    # Logging + monitoring conventions
Tessra.Platform.RateLimiting     # Rate limiting middleware
Tessra.Platform.Domain           # Shared tenant/user/entitlement models
Tessra.Platform.Tests            # Unit & integration tests
```

This structure keeps boundaries clean and makes it easy to split into separate deployable services later if needed.

---

## Middleware Pipeline Order

Requests flow through middleware in this order (defined in `Program.cs`):

1. **Serilog** — request logging context
2. **HTTPS Redirection** — enforce HTTPS
3. **ExceptionHandlingMiddleware** — catches exceptions from all downstream middleware/endpoints
4. **RequestLoggingMiddleware** — logs method, path, status code, duration

The exception handler is intentionally placed early — it wraps everything after it, so even if an endpoint or middleware further down throws, we return a clean JSON error instead of a raw 500.

---

## Request Flow Example

```
HTTP GET /health
  ↓
HttpsRedirection middleware  (redirects if HTTP, if configured)
  ↓
ExceptionHandlingMiddleware.InvokeAsync()
  ↓  (wraps in try-catch)
  RequestLoggingMiddleware.InvokeAsync()
    ↓  (records start time)
    /health endpoint handler
    ↓  (returns Ok({ status, timestamp }))
    RequestLoggingMiddleware logs duration
  ↓  (no exception, passes through)
  Response sent to client
```

---

## Error Response Shape

Every error response has this shape (from `ApiErrorResponse`):

```json
{
  "statusCode": 404,
  "message": "The requested resource was not found.",
  "details": null,
  "traceId": "0HM8K3T6G1V7A",
  "timestamp": "2026-07-27T12:00:00Z"
}
```

- `details` is only populated in development (stack trace)
- `traceId` correlates errors with logs
- `timestamp` is UTC

---

## Decisions To Be Made (From readme.md §0)

These should be locked in before writing significant feature code:

1. **Multitenancy model**: shared DB + tenant_id column vs. schema-per-tenant vs. DB-per-tenant
2. **Auth provider**: roll your own vs. Auth0/Clerk/Azure AD B2C/WorkOS
3. **Cloud target**: Azure, AWS, or GCP
4. **Monorepo vs. polyrepo**: currently organized as monorepo (consistent with readme.md suggestion)

---

## Logging Conventions (To Be Standardized)

Goal: Every log line across C#/Python/Next.js should carry:
- `tenant_id` — which tenant does this request belong to?
- `request_id` / `correlation_id` — which request does this log belong to?
- `user_id` — which user performed the action?

These conventions should be defined once in the C# Observability library and documented so Python/Next.js sides emit compatible logs.
