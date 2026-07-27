# Tessra — Project Roadmap

## Vision
A multi-tenant SaaS infrastructure platform that provides shared services (authentication, authorization, billing, monitoring, logging, rate limiting) for an AI-powered application. The C# platform service will be consumed by a Python MCP server and a Next.js frontend.

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
- [ ] Role-based access control (Admin vs regular user)

### Phase 4: Observability
- [ ] Structured logging conventions
- [ ] Metrics and monitoring
- [ ] Correlation IDs across services

### Phase 5: Rate Limiting & Billing
- [ ] Rate limiting middleware
- [ ] Billing / entitlement system

### Phase 6: Integration
- [ ] Python MCP server setup
- [ ] Next.js frontend setup
- [ ] Service-to-service communication contracts (OpenAPI)

### Phase 7: Production Readiness
- [ ] Cloud infrastructure provisioning
- [ ] CI pipeline (GitHub Actions)
- [ ] Deployment pipeline
- [ ] Dev container configuration
- [ ] Monitoring & alerting
- [ ] Onboarding documentation
