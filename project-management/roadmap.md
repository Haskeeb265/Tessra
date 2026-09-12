# Tessera — Project Roadmap

> 📐 **Architecture**: see **[`docs/README.md`](../docs/README.md)** — the single source of truth for how the system fits together.

## Vision
A multi-tenant SaaS infrastructure platform that provides shared services (authentication, authorization, billing, monitoring, logging, rate limiting) for an AI-powered application. The C# platform service will be consumed by the Next.js frontends.

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

### Phase 3.5: MCP Gateway & OAuth ✅ Complete (live-verified 2026-09-12)
- [x] Tool manifests as the tenant contract (`ToolManifests` CRUD + seeded Acme Dental fixture)
- [x] MCP OAuth authorization server — OpenIddict 7.7: PKCE, CIMD client registration, JWKS, login/consent
- [x] Gateway-facing manifest endpoint (server-to-server, `X-Gateway-Api-Key`)
- [x] Python MCP gateway — per-tenant `tools/list` + `tools/call`, RFC 9728 PRM, HTTP executor
- [x] Per-tenant token binding (RFC 8707 `resource` → token `aud`)
- [x] Local TLS (Caddy) + public tunnel path; **Claude web operates the SMB end-to-end**
- [ ] End-user (Jane) identity + per-tool scopes (the remaining product gap)

### Phase 4: Observability
- [ ] Structured logging conventions
- [ ] Metrics and monitoring
- [ ] Correlation IDs across services

### Phase 5: Rate Limiting & Billing
- [x] Rate limiting middleware (fixed-window per-IP on `/auth/*`)
- [ ] Billing / entitlement system

### Phase 6: Integration
- [x] Next.js frontend setup
- [x] Two-portal split: business portal (`apps/web`) + superadmin portal (`apps/platform-portal`)
- [x] Envelopes, roles & actions (enforced server-side via `ActionChecks`)
- [ ] Service-to-service communication contracts (OpenAPI)

### Phase 7: Production Readiness
- [ ] Cloud infrastructure provisioning
- [ ] CI pipeline (GitHub Actions)
- [ ] Deployment pipeline
- [ ] Dev container configuration
- [ ] Monitoring & alerting
- [ ] Onboarding documentation
