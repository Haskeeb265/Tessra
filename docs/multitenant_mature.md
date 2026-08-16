# Tessera — Multitenant Maturity Checklist (Prod Readiness)

> 📐 **Architecture**: see **[`docs/ARCHITECTURE.md`](ARCHITECTURE.md)** — the single source of truth for how the system fits together. This document is a **gap analysis**: everything that must be implemented or hardened before the multitenant platform can be considered production-ready. It supplements the roadmap (`project-management/roadmap.md`), which tracks *planned phases*; this tracks *what's actually missing*.

## How to read this document

- **Status** — `✅` implemented and solid · `⚠️` implemented but partial/has edge cases · `❌` missing
- **Priority** — **P0** blocks a production launch · **P1** should ship before real customers · **P2** later, once the core is solid
- Each item notes where it lives in the code today and a suggested approach.

---

## A. Roles, actions & the envelope cascade (P0)

**The core problem:** a user's `Role` is stored as a **string** (`User.Role`), but roles and actions now live in `Envelopes` → `AppRoles`. When a superadmin edits or deletes an envelope, nothing propagates to existing tenant users.

| # | Status | Gap | Why it matters / suggested fix |
|---|---|---|---|
| A1 | ❌ | **Envelope role rename doesn't cascade** | A renamed `AppRole` leaves existing users with the old role string. `/tenant/me` finds no matching role and returns an **empty actions list** — the UI silently loses permissions. Fix: resolve at request time with a defined fallback (see A3), or store `RoleId` (FK to `AppRole`) instead of the name. |
| A2 | ❌ | **Envelope role deletion strands users** | Deleting a role from an envelope breaks existing users (same empty-actions symptom) while `GetAllowedRolesAsync` already prevents *new* assignments to it. Same fix as A1; deleted-role users need an explicit fallback. |
| A3 | ⚠️ | **No defined fallback when a user's role no longer matches the envelope** | `/tenant/me` (`TenantEndpoints`) does a live lookup (`role?.Actions`) and returns empty actions on mismatch. Never return *silently empty*: deny-by-default with a clear signal, or fall back to a built-in minimal role. |
| A4 | ❌ | **Actions are not enforced server-side** | `ActionCatalog` is display-only. Nothing checks the acting user's actions before privileged operations. Fix (per ARCHITECTURE.md §13): platform checks actions (e.g. `delete_widget` required to delete) before the op; UI driven by `/tenant/me` actions (the business portal already displays them). |
| A5 | ⚠️ | **Role change propagation is delayed by token lifetime** | `role` is a JWT claim. A demoted user keeps Admin claims until the access token expires (15 min). Acceptable at 15 min; options: shorter tokens or a `token_version` claim bumped on privilege change. |
| A6 | ⚠️ | **No envelope versioning / audit of role changes** | If you need history or safe migration of existing users, snapshot envelopes (keep old version for existing users, new version for new assignments). P2 unless audit is required. |

---

## B. Tenant lifecycle & isolation (P0–P1)

| # | Status | Gap | Why it matters / suggested fix |
|---|---|---|---|
| B1 | ⚠️ | **Tenant deletion orphans `Widgets`** | `AdminDeleteTenant` (`AdminEndpoints`) deletes `Users`, `RefreshTokens`, and the tenant — but **not `Widgets`**. Orphaned rows keep a dangling `TenantId`. Fix: delete widgets too (raw SQL on Postgres / bound-context on InMemory), or make `Widget.TenantId` a real FK with `ON DELETE CASCADE`. |
| B2 | ⚠️ | **First-user-becomes-admin is a prod risk** | Anyone who registers first in an empty workspace becomes its Admin. Fine for a demo; dangerous in prod (squatting / privilege escalation). Fix: **invitations or verified-domain onboarding** (`AuthService.RegisterAsync` bootstrap rule). |
| B3 | ❌ | **No tenant suspension** | Prod needs a "suspended" / "trial-expired" state that blocks logins and API calls. Suggest a `Status` field on `Tenant` checked in middleware. Ties into billing (F1). |
| B4 | ⚠️ | **Hard delete vs soft delete** | Tenants/users are hard-deleted. Prod usually soft-deletes (or archives) for audit and accident recovery. Consider `IsDeleted` flags before real customer data exists. |
| B5 | ⚠️ | **Header-based tenant resolution is the only strategy** | `X-Tenant-Id` header (Finbuckle header strategy) is fine server-to-server. A per-tenant public web UX would want **subdomain resolution** (`acme.tessera.app`) — noted in `readme.md` §6. |
| B6 | ✅ | **Cross-tenant data isolation** | Solid: Finbuckle global query filters + `TenantClaimValidationMiddleware` (403 on claim/header mismatch). Keep it covered by tests (E1). |

---

## C. AuthN/AuthZ hardening (P0–P1)

| # | Status | Gap | Why it matters / suggested fix |
|---|---|---|---|
| C1 | ⚠️ | **Refresh token revocation is incomplete** | Single-use rotation exists (`AuthService.RefreshAsync` revokes old token), but there's **no server-side logout/revoke endpoint** and **no reuse detection** (a replayed rotated token should revoke the whole token family). Also revoke all tokens on password change. |
| C2 | ⚠️ | **No password reset / email verification / MFA** | Register creates a verified user immediately. P0 for any real product. |
| C3 | ⚠️ | **Account enumeration** | `/auth/register` returns "user already exists"; `/auth/login` is generic. The asymmetry lets attackers probe emails. Fix: generic messages + rate limiting. |
| C4 | ❌ | **No rate limiting on auth endpoints** | Brute-force protection. Phase 5 on the roadmap, but P0 for a public login endpoint. |
| C5 | ⚠️ | **JWT secret committed to the repo** | `appsettings.json` holds the dev secret. Prod must pull it from env/secrets manager, with a key **rotation** plan. Symmetric HS256 is fine while only one service verifies (switch to RS256 if more services need to verify). |
| C6 | ⚠️ | **Superadmin token exposure window** | 12 h, no refresh. If leaked, an attacker has a 12-hour window. Consider 2–4 h + MFA. |

---

## D. Data integrity & concurrency (P1)

| # | Status | Gap | Why it matters / suggested fix |
|---|---|---|---|
| D1 | ⚠️ | **Race condition on email uniqueness** | Same-email-in-same-tenant is checked with `AnyAsync` (app-level) but the DB index on `Users.Email` is **non-unique**. Two concurrent registrations can both pass → duplicates. Fix: composite unique index `(Email, TenantId)` — the unique indexes on `AdminUsers.Email` and `Tenants.Identifier` are already correct. |
| D2 | ⚠️ | **Last-write-wins on shared edits** | Two superadmins editing the same envelope/user silently overwrite each other. Fix: optimistic concurrency (EF `IsConcurrencyToken()` / `rowversion`). |
| D3 | ⚠️ | **Provider divergence (InMemory vs Postgres)** | Raw-SQL branches for seed/delete mean InMemory and Postgres behave differently. Fine for dev; a trap in prod. Keep both covered by tests, or standardize on Postgres. |
| D4 | ❌ | **No audit trail** | No record of who changed which envelope/tenant when. P2 unless compliance requires it. |

---

## E. Reliability & operations (P0)

| # | Status | Gap | Why it matters / suggested fix |
|---|---|---|---|
| E1 | ❌ | **No automated test project** | The four isolation layers are only smoke-tested by curl (documented in `Guide.md` / `progress.md`). Add unit tests (auth service, claim validation) + integration tests (cross-tenant 403, tenant isolation) — would have caught B1 instantly. |
| E2 | ❌ | **No CI/CD or deployment** | Phase 7 on the roadmap. Fly.io + Postgres chosen; nothing ships yet. |
| E3 | ⚠️ | **Migrations auto-apply on startup** | `Program.cs` runs `db.Database.Migrate()` on boot. Risky in prod (no rollback, no review gate). Run migrations as an explicit deploy step instead. |
| E4 | ⚠️ | **`/health` is a bare 200** | Doesn't verify DB connectivity. Add liveness + readiness (readiness checks Postgres). |
| E5 | ❌ | **No structured observability** | Phase 4 on the roadmap. Correlation IDs across platform + portals, metrics, tracing. `ApiErrorResponse.TraceId` and Serilog exist — the scaffolding is there; wire a real monitoring sink. |
| E6 | ❌ | **Backups / PITR, secret rotation, prod CORS** | Postgres runs on a Docker volume with no backup story. CORS allows only localhost origins. |

---

## F. Product-level multitenant features (P2)

| # | Status | Gap | Why it matters / suggested fix |
|---|---|---|---|
| F1 | ❌ | **Billing & entitlements** | Phase 5 on the roadmap: plan limits (users, storage), trial, and **suspension on failed payment** (ties to B3). |
| F2 | ❌ | **Tenant provisioning workflow** | New tenant → seed defaults → invite admin → billing state. |
| F3 | ✅ | **Envelope/roles model** | A genuinely good foundation — the right abstraction; it just needs the enforcement + cascade semantics from section A. |

---

## Top 5 priorities before "production"

1. **Action enforcement + envelope-cascade semantics** (A1–A4) — design the role-resolution contract now.
2. **Widgets cleanup on tenant delete** (B1) — one-line bug fix.
3. **Composite unique index `(Email, TenantId)` + generic auth error messages** (D1, C3).
4. **A real test project covering the four isolation layers** (E1).
5. **Replace first-user-becomes-admin with invites + move the JWT secret out of the repo** (B2, C5).

---

## Status summary

| Area | ✅ Solid | ⚠️ Partial | ❌ Missing |
|---|---|---|---|
| A. Roles / actions / envelope cascade | — | 3 (A3, A5, A6) | 3 (A1, A2, A4) |
| B. Tenant lifecycle & isolation | 1 (B6) | 4 (B1, B2, B4, B5) | 1 (B3) |
| C. AuthN / AuthZ | — | 5 (C1, C2, C3, C5, C6) | 1 (C4) |
| D. Data integrity & concurrency | — | 3 (D1, D2, D3) | 1 (D4) |
| E. Reliability & operations | — | 3 (E3, E4, E6) | 3 (E1, E2, E5) |
| F. Product features | 1 (F3) | — | 2 (F1, F2) |
