# Tessera Web Portals — Engineering Reference

> **Scope**: the two Next.js frontends that consume the C# platform API. For the platform service itself, see `docs/platform/README.md`. For the MCP product model and the OAuth flow these portals participate in, see `docs/mcp/README.md`. For the overall system (index), see `docs/README.md`.

---

## 1. What these apps are

| Portal | App | Port | Audience | Auth | What it does |
|---|---|---|---|---|---|
| **Business portal** | `apps/web` | 3000 | Workspace users | Tenant JWT + `X-Tenant-Id` header | Register/login per workspace (invite-only), widget CRUD, team/role management |
| **Platform portal** | `apps/platform-portal` | 3001 | Platform admins (superadmins) | `SuperAdmin` JWT, **no** tenant header | Create/edit/delete tenants, create/edit/delete envelopes (roles + actions), send workspace-owner invites |

Both share the same stack, theme, and component library; they differ in **who they authenticate**, **whether they send `X-Tenant-Id`**, and **how they handle token expiry**.

## 2. Tech stack

| Layer | Tech | Version |
|---|---|---|
| Framework | Next.js (App Router, Turbopack) | 16.3.0 |
| Language | React · TypeScript strict | 19.2.8 / 5 |
| Styling | Tailwind CSS v4 | 4 |
| Theme | Custom "coffee" palette | cream/beige/latte/caramel/mocha/roast/espresso (defined in `globals.css`) |

## 3. Repository layout

```
apps/web/                          # Business portal (:3000)
├── app/
│   ├── page.tsx                   # Landing page with HealthBadge + login/register CTAs
│   ├── login/page.tsx             # Workspace login (tenant picker + credentials; TOTP step when MFA)
│   ├── register/page.tsx          # Workspace signup; redeems ?invite= token from email link
│   ├── oauth/
│   │   ├── login/page.tsx         # OAuth login (MCP flow): email+password / TOTP → OAuth cookie
│   │   └── consent/page.tsx       # OAuth consent: show client+scopes → approve/deny connector
│   ├── dashboard/page.tsx         # Widget CRUD + role badge + "your role allows" action pills
│   └── dashboard/users/page.tsx   # Team (Admin-only UI): list users, add user, change role, remove
├── components/
│   ├── ui.tsx                     # Button, Card, Field, TextInput, TextArea, Alert
│   ├── portal-header.tsx          # brand + nav (Widgets / Team) + user info + logout
│   ├── tenant-picker.tsx          # workspace <select> fed by GET /tenants
│   └── health-badge.tsx           # "API online/offline" indicator
└── lib/
    ├── api.ts                     # single API client (X-Tenant-Id on every call, localStorage tokens, auto-refresh on 401)
    └── oauth.ts                   # /oauth pages' client → POST /connect/login(+/mfa), GET /connect/consent-info, POST /connect/consent

apps/platform-portal/              # Superadmin portal (:3001)
├── app/
│   ├── page.tsx                   # Redirects to /tenants (logged in) or /login
│   ├── login/page.tsx             # Superadmin login
│   ├── tenants/page.tsx           # List/create/edit/delete tenants, assign envelope
│   └── envelopes/page.tsx         # List/create/edit/delete envelopes; role + comma-separated actions editor
└── lib/
    └── api.ts                     # superadmin API client (no X-Tenant-Id, localStorage["tessera.admin"], no auto-refresh)
```

## 4. Shared design

- **Coffee theme**: `globals.css` defines the `cream/beige/latte/caramel/mocha/roast/espresso` palette used throughout both portals.
- **Component library**: `components/ui.tsx` provides `Button`, `Card`, `Field`, `TextInput`, `TextArea`, `Alert`. Portal-specific pieces: `portal-header.tsx`, `tenant-picker.tsx`, `health-badge.tsx`.
- **No recomputation of auth**: the C# platform is the source of truth for auth, authz, tenant context, roles, and actions. Frontends consume its APIs; they never re-implement these concerns.

## 5. API client (`lib/api.ts`) — the important differences

### 5.1 Business portal

- Sends `X-Tenant-Id` on **every** call.
- Reads tokens from `localStorage["tessera.auth"]`, decodes the JWT role (`roleFromToken`).
- **Auto-refreshes on 401**: retries the original call once via `POST /auth/refresh` → new pair → retry. (lib/api.ts)
- Tokens: access 15 min + refresh 7 day (rotation + family reuse detection on the server).

### 5.2 Platform portal

- **No `X-Tenant-Id` header** — superadmin requests are tenant-independent.
- Tokens in `localStorage["tessera.admin"]`.
- **No auto-refresh** — 4 h token; on 401 the pages redirect to `/login`.

### 5.3 Token storage tradeoff (known issue)

Tokens live in `localStorage`, not httpOnly cookies. Any XSS in the business portal can obtain the access + refresh token. Acceptable for now; see `docs/platform/README.md` §11 and CONCERNS for the production consideration (httpOnly Secure SameSite cookies + CSRF strategy).

## 6. Business portal routes (`apps/web`)

| Route | File | Purpose |
|---|---|---|
| `/` | `app/page.tsx` | Landing page with `HealthBadge` (polls `GET /health`) + login/register CTAs |
| `/login` | `app/login/page.tsx` | Workspace login (tenant picker + credentials; TOTP code step when MFA is enabled) |
| `/register` | `app/register/page.tsx` | Workspace signup; reads the `?invite=` token from the email link to prefill email and redeem the invite |
| `/dashboard` | `app/dashboard/page.tsx` | Widget CRUD + role badge + "your role allows" action pills (`GET /tenant/me`) |
| `/dashboard/users` | `app/dashboard/users/page.tsx` | **Team** (Admin-only UI): list users, add user (role from envelope), change role, remove |

Flows (companion diagrams in `USER_JOURNEYS.md`):

- **Tenant admin**: login (with optional MFA) → dashboard (parallel `GET /tenant/me` + `GET /widgets`) → widget CRUD (action-checked) or team management (if `manage_users`) → password/MFA self-service → auto-refresh on 401.
- **Tenant user**: same shape, role-based UI rendering (`view_widgets` / `create_widget` / `edit_widget` / `delete_widget` / `manage_users` pills), invite redemption path via `/register?invite=...&tenant=...`.

## 7. Platform portal routes (`apps/platform-portal`)

| Route | File | Purpose |
|---|---|---|
| `/` | `app/page.tsx` | Redirects to `/tenants` (logged in) or `/login` |
| `/login` | `app/login/page.tsx` | Superadmin login |
| `/tenants` | `app/tenants/page.tsx` | List/create/edit/delete tenants, assign envelope |
| `/envelopes` | `app/envelopes/page.tsx` | List/create/edit/delete envelopes; role + comma-separated actions editor |

Flow (companion diagram in `USER_JOURNEYS.md`): login → dashboard (tenant + envelope lists) → create/edit/delete envelopes → create/edit/delete tenants (+ envelope assignment, which copies template roles into `TenantRoles`) → suspend/reactivate/delete tenants → send the single workspace-owner bootstrap invite.

## 8. OAuth pages (new — 2026-09, `apps/web`)

The business portal now also hosts the **interactive OAuth login + consent pages** for the MCP authorization server. These live under `/oauth/*` and are served from the same origin as the API by the local TLS reverse proxy (Caddy), so they call the `/connect/*` endpoints with **relative URLs** (no `NEXT_PUBLIC_API_URL`) and the OAuth cookie flows naturally.

| Route | File | Purpose |
|---|---|---|
| `/oauth/login` | `app/oauth/login/page.tsx` | OAuth login: email + password (or TOTP code step when MFA enabled). The AS redirects here with `tenant` + `returnUrl` (the full `/connect/authorize` URL). On success the page navigates the browser back to `returnUrl` with the OAuth cookie set. |
| `/oauth/consent` | `app/oauth/consent/page.tsx` | OAuth consent: shows the client name, tenant name, and requested scopes. Approving records the grant (via `/connect/consent`) and navigates back to `returnUrl`; denying sends the browser back with `?deny=1` so the AS returns `access_denied` to the client. |

### 8.1 How they fit the flow

```
Claude Code --GET /connect/authorize?...resource=…t/acme-dental/mcp...--> API
API: user not authenticated → redirect to https://tessera.local/oauth/login?tenant=acme-dental&returnUrl=<authorize URL>
Next.js /oauth/login: email + password (MFA step if enabled)
  → POST /connect/login (+ /connect/login/mfa)
  → OAuth cookie set
  → navigate browser back to returnUrl (/connect/authorize)
API: consent required → redirect to https://tessera.local/oauth/consent?tenant=...&client_id=...&scope=...&returnUrl=<authorize URL>
Next.js /oauth/consent: show scopes → Approve (POST /connect/consent) → navigate back to returnUrl
API: authorize → redirect to client with code
```

### 8.2 `lib/oauth.ts`

OAuth interactive-flow client used by the two pages:

- `oauthLogin(email, password, tenant)` → `POST /connect/login` → `{ ok?, mfaRequired?, mfaToken? }`
- `oauthCompleteMfa(mfaToken, code, tenant)` → `POST /connect/login/mfa`
- `oauthConsentInfo(clientId, scope, tenant)` → `GET /connect/consent-info?client_id=...&scope=...` → `{ clientId, clientName, tenantId, tenantName, scopes: ScopeInfo[] }`
- `oauthConsent(clientId, scopes, tenant)` → `POST /connect/consent` → `{ ok: true }`
- Uses `oauthFetch(path, { method, body, tenant })` — sends `X-Tenant-Id` + `Content-Type: application/json` where appropriate.
- `OAuthError` thrown on non-ok responses (reads `error` from the JSON body when present).

Tenant is passed as `X-Tenant-Id`, mirroring the platform's existing API convention.

## 9. CORS & origins

CORS is configured in the platform (`Cors:AllowedOrigins`, defaults `http(s)://localhost:3000` and `:3001`). The business portal calls the API at `NEXT_PUBLIC_API_URL` (defaults `http://localhost:5085`; `.env.local` currently sets `http://localhost:5000` for Docker). Behind the local Caddy TLS proxy the whole stack is served on one origin (`https://tessera.local`), so the `/oauth/*` pages call `/connect/*` with relative URLs and don't depend on this variable. The platform portal uses the same variable.

## 10. Known gaps (web-specific)

- Business portal `Alert` lacks a `success` kind.
- `HealthBadge` sends a stray default `X-Tenant-Id` to `/health` (harmless — `/health` is excluded from tenant validation).
- Register page state flash.
- Platform portal's `Alert` lacks a `success` kind.
- Logout in the business portal clears `localStorage` but doesn't call `POST /auth/logout` (refresh token remains valid until expiry) — semantic inconsistency; see CONCERNS.
- `.env.local` files are gitignored — if you move machines, recreate them.

## 11. Where to go next

For the OAuth authorization server these pages talk to, see `docs/platform/README.md` §6 and `docs/mcp/README.md`. For the overall system (index), see `docs/README.md`.
