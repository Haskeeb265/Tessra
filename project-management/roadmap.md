# Tessera — Project Roadmap

> 📐 **Architecture**: see **[`docs/ARCHITECTURE.md`](../docs/ARCHITECTURE.md)** — the single source of truth for how the system fits together.

## Vision
A multi-tenant SaaS infrastructure platform that provides shared services (authentication, authorization, billing, monitoring, logging, rate limiting) for an AI-powered application. The C# platform service will be consumed by a Python MCP server and a Next.js frontend.

**The MCP product (clarified Session 11):** plug-and-play MCP server templates for SMBs. Tenants pick a template, integrate their own tools, and their end users reach the resulting service through AI assistants (ChatGPT, Claude, Gemini). The MCP server is the bridge; the C# platform remains the source of truth for auth/authz/tenant context.

## Major Milestones

### Phase 1: Foundation ✅ Complete
- [x] Scaffold C# platform service with ASP.NET Core
- [x] Configure Serilog for structured logging
- [x] Implement global exception handling middleware
- [x] Implement request logging middleware
- [x] Add health check endpoint
- [x] Lock in architectural decisions:
  - Cloud: **Fly.io**
  - Multitenancy: **Shared DB + tenant_id** with **Finbuckle.MultiTenant**
  - Auth: **Roll our own JWT**
- [x] Set up project management files (roadmap, tasks, decisions, learning log, progress)
- [x] Define the C# solution structure (separate class libraries by concern)

### Phase 2: Core Infrastructure ✅ Complete
- [x] Docker Compose for local development (API + PostgreSQL 16)
- [x] EF Core Migrations (replaced `EnsureCreated()`)
- [x] Multi-project solution structure (Api, Domain, Observability)

### Phase 3: Authentication & Authorization ✅ Complete
- [x] User registration / login
- [x] JWT token issuance (access + refresh tokens)
- [x] Tenant context in JWT claims (`tenant_id`, `tenant_identifier`)
- [x] Role-based access control (Admin vs regular user)

### Phase 4: Observability
- [ ] Structured logging conventions
- [ ] Metrics and monitoring
- [ ] Correlation IDs across services

### Phase 5: Rate Limiting & Billing
- [ ] Rate limiting middleware
- [ ] Billing / entitlement system

### Phase 6: Integration
- [x] Python MCP server setup — bridge scaffolded (Session 11): official MCP SDK, stdio transport, `TesseraClient` + `whoami` / `list_widgets` tools, 15 tests incl. live protocol tests
- [ ] MCP server → HTTP (streamable) transport for hosted access
- [ ] Action enforcement via MCP (platform checks `create_mcp` / `add_tools` / …)
- [ ] Real `McpServer` resource (replaces `Widget`) — tenants create MCP servers from templates
- [x] Next.js frontend setup
- [x] Two-portal split: business portal (`apps/web`) + superadmin portal (`apps/platform-portal`)
- [x] Envelopes, roles & actions (catalog only — enforcement deferred until MCP)
- [ ] Service-to-service communication contracts (OpenAPI)

### Phase 7: Production Readiness
- [ ] Cloud infrastructure provisioning
- [ ] CI pipeline (GitHub Actions)
- [ ] Deployment pipeline
- [ ] Dev container configuration
- [ ] Monitoring & alerting
- [ ] Onboarding documentation
