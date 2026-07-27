# Tessra — Project Roadmap

## Vision
A multi-tenant SaaS infrastructure platform that provides shared services (authentication, authorization, billing, monitoring, logging, rate limiting) for an AI-powered application. The C# platform service will be consumed by a Python MCP server and a Next.js frontend.

## Major Milestones

### Phase 1: Foundation (In Progress)
- [x] Scaffold C# platform service with ASP.NET Core
- [x] Configure Serilog for structured logging
- [x] Implement global exception handling middleware
- [x] Implement request logging middleware
- [x] Add health check endpoint
- [x] Lock in architectural decisions:
  - Cloud: **Fly.io**
  - Multitenancy: **Shared DB + tenant_id** with **Finbuckle.MultiTenant**
  - Auth: **Roll our own JWT**
- [ ] Set up project management files (roadmap, tasks, decisions, learning log)
- [ ] Define the C# solution structure (separate class libraries by concern)

### Phase 2: Core Infrastructure
- [ ] Docker Compose for local development
- [ ] Dev container configuration
- [ ] CI pipeline (GitHub Actions)
- [ ] Multi-project solution structure

### Phase 3: Authentication & Authorization
- [ ] User registration / login
- [ ] JWT token issuance
- [ ] Tenant context middleware
- [ ] Role-based access control

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
- [ ] Deployment pipeline
- [ ] Monitoring & alerting
- [ ] Onboarding documentation
