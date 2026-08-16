# Tessera — Platform Service Guide (operations)

## Purpose

This is the **operational guide** for the C# platform service (`apps/platform`):
how to run it, test it, and what commands to use.

> 📐 **For how the system works** — the data model, middleware pipeline, auth
> flows, tenant isolation, API surface, user flows, and design decisions — read
> **[`docs/ARCHITECTURE.md`](../../docs/ARCHITECTURE.md)**. It is the single
> source of truth. This file deliberately does **not** duplicate it.

The platform service owns the cross-cutting concerns every tenant-facing request
flows through: authentication, authorization, multi-tenancy, roles & actions.
The Next.js frontends (`apps/web`, `apps/platform-portal`) are **consumers** of
these services, not owners.

---

## Quick start (Docker — the canonical setup)

1. Start the API + PostgreSQL:

   ```bash
   cd apps/platform
   docker compose up -d --build
   ```

2. Start the two portals (two terminals):

   ```bash
   cd apps/web && npm run dev            # business portal → http://localhost:3000
   cd apps/platform-portal && npm run dev  # platform portal → http://localhost:3001
   ```

3. Both portals read `NEXT_PUBLIC_API_URL` from their `.env.local` (currently
   `http://localhost:5000`, the Docker API). If you run the API elsewhere,
   update those files.

## Local dev without Docker (InMemory fallback)

```bash
cd apps/platform
dotnet run --project src/Tessera.Platform.Api   # → http://localhost:5085
```

With no connection string, the API uses the **in-memory database**:

- Data is **ephemeral** — it resets on every restart.
- There is **no seeded tenant admin** (`admin@tessera.com` is Postgres-only) —
  register an account instead; the **first user in a workspace becomes its Admin**.
- `superadmin@tessera.com` *is* seeded on both providers.

---

## Services & ports

| Service | Port | Notes |
|---|---|---|
| API (Docker) | `5000` | `platform-api-1`, PostgreSQL-backed, migrations run on startup |
| API (local `dotnet run`) | `5085` | InMemory fallback |
| PostgreSQL | `5432` | `platform-db-1`, db `tessera_platform`, `pgdata` volume persists |
| Business portal | `3000` | `apps/web` |
| Platform portal | `3001` | `apps/platform-portal` |

---

## Accounts

| Account | Password | Where it exists |
|---|---|---|
| `superadmin@tessera.com` | `Admin123!` | Platform portal — seeded on **both** providers |
| `admin@tessera.com` | `Admin123!` | Business portal tenant admin (Alpha + Beta) — seeded **PostgreSQL only** |

---

## Testing the API (curl)

```bash
API=http://localhost:5000

# 1. Health + public tenant list
curl $API/health
curl $API/tenants

# 2. Superadmin login (platform portal auth)
SA=$(curl -s -X POST -H "Content-Type: application/json" \
  -d '{"email":"superadmin@tessera.com","password":"Admin123!"}' $API/admin/auth/login)
SA_TOKEN=$(echo "$SA" | sed -n 's/.*"accessToken":"\([^"]*\)".*/\1/p')

# 3. Superadmin: create an envelope, then a tenant assigned to it
curl -X POST -H "Content-Type: application/json" -H "Authorization: Bearer $SA_TOKEN" \
  -d '{"name":"Pro","roles":[{"name":"Admin","actions":["manage_users","create_widget","edit_widget"]},{"name":"Editor","actions":["create_widget","edit_widget"]}]}' \
  $API/admin/envelopes
curl -X POST -H "Content-Type: application/json" -H "Authorization: Bearer $SA_TOKEN" \
  -d '{"identifier":"gamma-corp","name":"Gamma Corp","envelopeId":"<envelope-id>"}' \
  $API/admin/tenants

# 4. Tenant user: register (first user of gamma-corp becomes its Admin)
TOK=$(curl -s -X POST -H "Content-Type: application/json" -H "X-Tenant-Id: gamma-corp" \
  -d '{"email":"gina@gamma.com","password":"Password123!"}' $API/auth/register \
  | sed -n 's/.*"accessToken":"\([^"]*\)".*/\1/p')

# 5. Tenant endpoints
curl -H "Authorization: Bearer $TOK" -H "X-Tenant-Id: gamma-corp" $API/tenant/me
curl -H "Authorization: Bearer $TOK" -H "X-Tenant-Id: gamma-corp" $API/tenant/envelope
curl -H "Authorization: Bearer $TOK" -H "X-Tenant-Id: gamma-corp" $API/tenant/users

# 6. Widgets (placeholder demo resource — see ARCHITECTURE.md §3)
curl -H "Authorization: Bearer $TOK" -H "X-Tenant-Id: gamma-corp" $API/widgets

# 7. Cross-tenant token reuse → 403
curl -o /dev/null -w "%{http_code}\n" \
  -H "Authorization: Bearer $TOK" -H "X-Tenant-Id: alpha-corp" $API/widgets
# → 403 (token's tenant_identifier ≠ header)
```

The full endpoint table (every route + its auth requirement) is in
`docs/ARCHITECTURE.md` §7.

---

## Testing the portals

**Business portal** (`http://localhost:3000`):
1. Log in as `admin@tessera.com` (workspace **Alpha Corp**) — you're the workspace Admin.
2. Create/edit widgets on the dashboard; note the "your role allows" action pills.
3. Open **Team** → add a user with a role from the workspace's envelope, change roles, remove a user.
4. Register a second account in a different workspace (e.g. Beta Industries) and confirm you can't see Alpha's widgets.

**Platform portal** (`http://localhost:3001`):
1. Log in as `superadmin@tessera.com`.
2. **Envelopes** → create one with roles + actions.
3. **Tenants** → create a tenant, assign the envelope; edit/delete.

---

## Build & verify commands

```bash
# C# platform
cd apps/platform
dotnet build                                          # build all 3 projects
dotnet ef migrations add <Name> --project src/Tessera.Platform.Api   # new migration
dotnet run --project src/Tessera.Platform.Api        # local InMemory dev

# Frontends (each app)
cd apps/web && npm run build && npm run lint
cd apps/platform-portal && npm run build && npm run lint
```

---

## Where things live (details in ARCHITECTURE.md)

| Concern | Location |
|---|---|
| Endpoints (widgets / auth / tenant / admin) | `src/Tessera.Platform.Api/Endpoints/` |
| Middleware (exception / tenant validation / tenant-claim) | `src/Tessera.Platform.Api/Middleware/` |
| Services (AuthService, AdminAuthService) | `src/Tessera.Platform.Api/Services/` |
| Data (AppDbContext, DbTenantStore, migrations) | `src/Tessera.Platform.Api/Data/` |
| Domain models + constants | `src/Tessera.Platform.Domain/Models/` |
| Shared middleware | `src/Tessera.Platform.Observability/` |
| Wiring / pipeline / seeding | `src/Tessera.Platform.Api/Program.cs` |
| Config | `appsettings.json` (+ `.Development`, `.Docker`) |

---

## Project management

Roadmap, tasks, decisions, learning log, and progress live in
`/project-management/` (updated after meaningful progress).

---

## Development conventions (short version)

- **Endpoints**: static extension classes (`Map<Resource>Endpoints`) grouping
  routes under `MapGroup("/<resource>")`, `.RequireAuthorization()` where needed.
- **Business logic**: in `Services/` classes (endpoints stay thin).
- **Tenant-scoped queries**: always `FirstOrDefaultAsync`/`ToListAsync` — never
  `FindAsync` (it bypasses Finbuckle's global query filters).
- **Errors**: throw or return `Results.*`; the global exception middleware
  shapes every failure into `ApiErrorResponse` (see ARCHITECTURE.md §11).
- New resources / middleware ordering / gotchas: see `docs/ARCHITECTURE.md`.
