# Tessera — Architecture Decisions

> 📐 **Architecture**: see **[`docs/ARCHITECTURE.md`](../docs/ARCHITECTURE.md)** — the single source of truth for how the system fits together (data model, middleware pipeline, auth flows, API surface, gotchas).

## Recorded Decisions

### 2026-07-27: Exception Handling Strategy
- **Decision**: Use a global middleware (`ExceptionHandlingMiddleware`) to catch unhandled exceptions and map them to appropriate HTTP status codes with a consistent JSON response shape (`ApiErrorResponse`).
- **Why**: Keeps endpoint code clean (no try-catch in every handler), provides a single place to log exceptions with correlation IDs, and returns consistent-structured errors to API consumers.
- **Alternatives considered**: `IExceptionHandler` (newer approach in .NET 8+), per-endpoint error handling.
- **Notes**: Chose middleware for explicit ordering control and familiarity. Could refactor to `IExceptionHandler` in the future if needed.

### 2026-07-27: Logging Framework
- **Decision**: Use Serilog with console sink for structured logging.
- **Why**: Serilog is the de-facto standard for structured logging in .NET. Console sink is sufficient for development; we can add file/seq/elasticsearch sinks later.
- **Alternatives considered**: Built-in `ILogger<T>`, NLog.
- **Notes**: Using Serilog's `UseSerilog()` on the host builder integrates with ASP.NET Core's logging pipeline.

### 2026-07-27: Project Management
- **Decision**: Maintain `project-management/` files for roadmap, tasks, decisions, learning log, and progress.
- **Why**: Ensures we have a clear direction, track learning, and remember why architectural decisions were made.
- **Alternatives considered**: GitHub Projects, Notion, linear.
- **Notes**: Keeping it in-repo so it's versioned alongside code and accessible to both teammates.

### 2026-07-27: Target Framework
- **Decision**: Use .NET 10.0 (`net10.0`).
- **Why**: The project was already scaffolded with the latest available framework.
- **Notes**: SDK version is locked in `global.json` (when we create one) to ensure consistent builds across environments.

### 2026-07-27: Solution Structure
- **Decision**: Restructure the single C# project into a multi-project solution with class libraries separated by concern.
- **Why**: 
  - Clear separation of concerns — each library has a single responsibility
  - Enforces boundaries — the API host can only reference what it explicitly depends on
  - Testability — class libraries are easier to unit test independently
  - Future-proofing — if we ever need to split into separate deployable services, the boundaries are already defined
  - Compile-time safety — changes to Domain or Observability that break the API host are caught at build time
- **Structure**:
  - `Tessera.Platform.Api` — ASP.NET Core host (entry point, configuration, pipeline)
  - `Tessera.Platform.Domain` — shared domain models (ApiErrorResponse, future tenant/user models)
  - `Tessera.Platform.Observability` — logging & monitoring conventions (RequestLoggingMiddleware)
- **Alternatives considered**:
  - Single project (simpler but harder to maintain as the codebase grows)
  - Vertical slice folders (too early — need more code before organizing by feature)
- **Notes**: 
  - Used `.slnx` format (new XML-based solution file in .NET 10)
  - Created `Directory.Build.props` at repo root for shared MSBuild properties (TargetFramework, Nullable, ImplicitUsings)
  - Observability library uses `FrameworkReference` to `Microsoft.AspNetCore.App` to access `HttpContext`/`RequestDelegate` without needing the full Web SDK

### 2026-07-27: Cloud Provider — Fly.io
- **Decision**: Use Fly.io for hosting the C# platform service and PostgreSQL.
- **Why**:
  - Free allowance (3x 256MB VMs) suitable for early stage
  - Native Docker support with automatic Dockerfile generation via `fly launch`
  - Managed PostgreSQL (`fly mpg`) with automatic connection string injection
  - No vendor lock-in — runs standard Docker containers
  - Simple deployment via `flyctl` CLI
- **Alternatives considered**: Azure (free tier available but more complex setup), Vercel (cannot host .NET), Railway, Hetzner
- **Notes**: 
  - No hard spending cap — need to monitor usage via dashboard
  - Persistent volumes and dedicated IPv4 addresses incur separate charges
  - Will create `Dockerfile` and `fly.toml` when ready to deploy

### 2026-07-27: Multitenancy Model
- **Decision**: Shared database + tenant_id column, using Finbuckle.MultiTenant library.
- **Why**:
  - Simplest and fastest to implement for early stage
  - Finbuckle provides automatic global query filters — can't accidentally leak data between tenants
  - Handles tenant resolution (subdomain/header/JWT), tenant context injection, and EF Core integration
  - Can migrate specific tenants to their own database later if needed (hybrid approach)
- **Alternatives considered**:
  - DIY implementation (more code to write and test, same result)
  - Schema-per-tenant (worst of both worlds — complex migrations, weak isolation)
  - Database-per-tenant (too operationally heavy for 2-person team at this stage)
- **Notes**: Will install `Finbuckle.MultiTenant` NuGet package when implementing

### 2026-07-27: Authentication Strategy
- **Decision**: Roll our own JWT authentication.
- **Why**:
  - Best way to learn JWT internals (token structure, signing, validation, refresh flows)
  - Full control over the auth model — can design tenant-aware tokens from day one
  - No external dependencies for auth — keeps costs at zero
  - Can swap to a provider (WorkOS, Auth0) later if needed
- **Alternatives considered**:
  - WorkOS (purpose-built for B2B SaaS, generous free tier — but adds dependency)
  - Azure AD B2C (tight Azure integration — but we chose Fly.io)
  - Clerk (JS-first, great UX — but adds frontend dependency)
- **Notes**: Will implement password hashing (bcrypt), access + refresh token flow, and tenant context in JWT claims

### 2026-08-14: MCP Product Vision (Session 11)
- **Decision**: The "MCP part" of Tessera is a **plug-and-play MCP platform for SMBs**.
- **Why**: Clarified the product with the user. SMBs (tenants) pick an **MCP server template**, integrate **their own tools** into it, and their end users reach the resulting service through AI assistants (ChatGPT, Claude, Gemini).
- **What it means for the code**:
  - `apps/mcp-server` is the **bridge**: it exposes Tessera capabilities as MCP *tools* to AI assistants. It is a **consumer of the C# platform** (JWT + `X-Tenant-Id`), never an owner of auth/tenant/roles.
  - The `create_mcp` / `add_tools` / `delete_mcp` actions become real: tenants with those actions can create/configure their own MCP server (replaces the `Widget` placeholder resource).
- **Notes**: Action enforcement lands with the MCP feature, as decided in Session 10.

### 2026-08-14: MCP Server Framework (Session 11)
- **Decision**: Use the **official MCP Python SDK** (`mcp` 2.0.0), low-level `mcp.server.Server` API (constructor `on_list_tools` / `on_call_tool` handlers). Not FastMCP.
- **Why**: Full control over the protocol + closer to the internals (better for learning). The user explicitly chose the official SDK.
- **Alternatives considered**: FastMCP (higher-level, less boilerplate — rejected by choice).
- **Notes**: The SDK API changed significantly between 1.x (decorator-based `@server.list_tools()`) and 2.x (constructor kwargs + `input_schema`, `ListToolsResult`, `CallToolResult`). `mcp.server.lowlevel.Server` is the same class as `mcp.server.Server`.

### 2026-08-14: MCP Transport — stdio now, HTTP later (Session 11)
- **Decision**: Build milestone 1 over **stdio**; structure the server so switching to **streamable HTTP** is a runtime choice confined to `__main__.py`.
- **Why**: stdio is the fastest way to test locally (Claude Desktop, MCP Inspector). The product (end users via ChatGPT/Gemini/remote Claude) ultimately needs **HTTP** — ChatGPT and Gemini cannot use stdio at all — so the SDK's transport-as-runtime-choice design is load-bearing.
- **Alternatives considered**: HTTP-only from day one (more setup before the first tool works); stdio-only (can't serve the product).
- **Notes**: Tool definitions in `server.py` are transport-agnostic; only `__main__.py` picks the transport.

### 2026-08-14: MCP Server Authenticates As a Tenant User (Session 11)
- **Decision**: The MCP server acts on behalf of a **tenant user**: it logs in via `POST /auth/login` (credentials from env: `TESSERA_TENANT_ID` / `TESSERA_EMAIL` / `TESSERA_PASSWORD`) and sends `X-Tenant-Id` + Bearer on every call, auto-refreshing on 401 — mirroring `apps/web/lib/api.ts`.
- **Why**: The C# platform stays the single source of truth for identity and permissions. Tool failures surface as structured `is_error` results so a platform 403 (future action enforcement) reaches the assistant as a readable message, not a crashed request.
- **Notes**: Who the server acts *as* (a service account vs. per-end-user identity) is open for a later session.

### 2026-08-14: First MCP Milestone — Scaffold the Bridge (Session 11)
- **Decision**: Milestone 1 = scaffold `apps/mcp-server` (uv, `src/tessera_mcp/` layout, ruff + mypy strict + pytest) with a working `TesseraClient` and two real tools: `whoami` (`GET /tenant/me`) and `list_widgets` (`GET /widgets`).
- **Why**: Proves the full agent → MCP → platform round trip end-to-end before building the real `McpServer` resource and action enforcement.
- **Notes**: Verified live: 15/15 tests (11 unit + 4 integration over the real stdio protocol).
