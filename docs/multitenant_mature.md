# Tessera — Multitenant Maturity Checklist (Prod Readiness)

> 📐 **Architecture**: see **[`docs/ARCHITECTURE.md`](ARCHITECTURE.md)** — the single source of truth for how the system fits together. This document is a **gap analysis**: everything that must be implemented or hardened before the multitenant platform can be considered production-ready. It supplements the roadmap (`project-management/roadmap.md`), which tracks *planned phases*; this tracks *what's actually missing*.

## How to read this document

- **Status** — `✅` implemented and solid · `⚠️` implemented but partial/has edge cases · `❌` missing
- **Priority** — **P0** blocks a production launch · **P1** should ship before real customers · **P2** later, once the core is solid
- Each item notes where it lives in the code today.

---

## A. Roles, actions & the envelope (P0)

**Design (implemented):** the envelope is now a **template**, not a live binding. When a superadmin assigns an envelope to a tenant, its roles are **copied** into the tenant's own role set (`TenantRoles`); the tenant owns and edits its copies. `User.Role` (a string) was replaced by `User.RoleId` (FK → `TenantRoles.Id`), and authorization is **action-based** (`ActionChecks`), resolved from the tenant role at request time — so renames never break permissions, and envelope edits never cascade into tenant data.

| # | Status | Gap | Why it matters / resolution |
|---|---|---|---|
| A1 | ✅ | Envelope role rename doesn't cascade | No longer a gap: tenants own their roles, so superadmin envelope edits intentionally don't cascade (copy semantics, no auto-sync). Renaming a **tenant** role never affects users, who reference `RoleId` — verified by the `Role_rename_preserves_permissions_by_id` test. |
| A2 | ✅ | Envelope role deletion strands users | Same design fix: deleting an envelope role leaves tenant copies untouched. Deleting a **tenant** role is blocked while users are assigned (`DELETE /tenant/roles/{id}` → 400) — no stranding is possible. |
| A3 | ✅ | No defined fallback when a user's role no longer matches | The silent-empty-actions state cannot occur: role deletion is blocked while assigned, and `/tenant/me` resolves actions live from `RoleId` (deny-by-default for role-less users). |
| A4 | ✅ | Actions are not enforced server-side | `ActionChecks` enforces `view_widgets` / `create_widget` / `edit_widget` / `delete_widget` on widget endpoints and `manage_users` on user/role/invite/promote endpoints. The UI is driven by the same source (`/tenant/me` actions). |
| A5 | ✅ | Role change propagation is delayed by token lifetime | A `token_version` claim is included in every tenant JWT and validated per request (`TokenVersionValidationMiddleware`). Role changes, password changes, MFA changes, and deletion bump the version and revoke refresh tokens, so the change takes effect immediately. |
| A6 | ❌ | No envelope versioning / audit of role changes | Not implemented. P2 — tenants own their role edits now, so template history is only needed for platform-level audit (see D4). |

---

## B. Tenant lifecycle & isolation (P0–P1)

| # | Status | Gap | Why it matters / resolution |
|---|---|---|---|
| B1 | ✅ | Tenant deletion orphans `Widgets` | `AdminDeleteTenant` now hard-deletes `Widgets`, `RefreshTokens`, and `Invitations` and soft-deletes `Users` and the tenant — nothing dangles. Postgres uses raw SQL; InMemory uses a tenant-bound context. |
| B2 | ✅ | First-user-becomes-admin is a prod risk | Onboarding is **invite-only** — open self-registration is removed. The platform superadmin invites the workspace owner (`POST /admin/tenants/{id}/invites`); the **first invite redeemed becomes the workspace Superadmin** — a platform-managed role (all actions) that cannot be renamed, edited, deleted, or assigned/demoted by the tenant. The Superadmin invites Admins (assistants) and members via `/tenant/invites`. |
| B3 | ✅ | No tenant suspension | `Tenant.Status` (`Active`/`Suspended`) + `TenantSuspensionMiddleware` block logins and API calls for suspended tenants. Platform paths (`/admin`, `/health`, …) are exempt even when a stray `X-Tenant-Id` is sent. |
| B4 | ✅ | Hard delete vs soft delete | Soft delete (`IsDeleted`) on `Tenants` and `Users`; deleted tenants are filtered from the store and the public list, and the filtered unique index lets an email be reused after its account is deleted. |
| B5 | ⚠️ | Header-based tenant resolution is the only strategy | Still `X-Tenant-Id` (Finbuckle header strategy). A per-tenant public web UX would want **subdomain resolution** (`acme.tessera.app`) — noted in `readme.md` §6. P2. |
| B6 | ✅ | Cross-tenant data isolation | Solid: Finbuckle global query filters + `TenantClaimValidationMiddleware` (403 on claim/header mismatch). Covered by integration tests (`Cross_tenant_token_returns_403`, `Widgets_are_isolated_between_tenants`). |

---

## C. AuthN/AuthZ hardening (P0–P1)

| # | Status | Gap | Why it matters / resolution |
|---|---|---|---|
| C1 | ✅ | Refresh token revocation is incomplete | Complete: rotation with **reuse detection** (replaying a rotated token revokes the whole family via `FamilyId`), server-side logout (`/auth/logout`), and all sessions revoked on password change, MFA change, or role change. |
| C2 | ✅ | No password reset / email verification / MFA | All implemented: forgot/reset password, opt-in email verification (`Auth:RequireEmailVerification`), and TOTP MFA (enroll / confirm / disable + second-factor login step). Emails go through a pluggable `IEmailSender` — console now; swap in Resend/Postmark/SES. |
| C3 | ✅ | Account enumeration | Closed: `/auth/register` returns a generic failure, verification-required registration returns a neutral message, and forgot-password / verify-email are message-only. Tokens are stored hashed (SHA-256), never raw. |
| C4 | ✅ | No rate limiting on auth endpoints | Fixed-window per-IP rate limiter on every `/auth/*` endpoint (429 on excess), configurable via `RateLimiting:*`. Covered by the `Auth_endpoints_are_rate_limited` test. |
| C5 | ✅ | JWT secret committed to the repo | Base `appsettings.json` holds **no** secret; the dev secret lives only in `appsettings.Development.json`. Startup fails fast when `Jwt:SecretKey` is unset, so production must set `Jwt__SecretKey`. A key-**rotation** runbook is still worth writing (see E6). |
| C6 | ✅ | Superadmin token exposure window | Reduced from 12 h to **4 h** (`Jwt:AdminAccessTokenExpirationHours`). Superadmin MFA is still future work. |

---

## D. Data integrity & concurrency (P1)

| # | Status | Gap | Why it matters / resolution |
|---|---|---|---|
| D1 | ✅ | Race condition on email uniqueness | Filtered unique index `(Email, TenantId) WHERE NOT IsDeleted` (created in the initial migration) closes the two-concurrent-registrations race; soft-deleted accounts don't block email reuse. |
| D2 | ✅ | Last-write-wins on shared edits | Optimistic concurrency via the Postgres `xmin` system column on `Users`, `Tenants`, `TenantRoles`, and `Envelopes`/`AppRoles`; `DbUpdateConcurrencyException` → **409 Conflict** on tenant/envelope/role edits. |
| D3 | ⚠️ | Provider divergence (InMemory vs Postgres) | Raw-SQL branches (seed/delete) still differ between providers. Both paths are exercised by integration tests where feasible; standardizing on Postgres for all environments remains a goal. |
| D4 | ❌ | No audit trail | Not implemented. P2 — nothing records who changed which envelope/tenant/role when. |

---

## E. Reliability & operations (P0)

| # | Status | Gap | Why it matters / resolution |
|---|---|---|---|
| E1 | ✅ | No automated test project | `apps/platform/src/Tessera.Platform.Tests` (xUnit + `WebApplicationFactory`): 26 tests covering auth flows, token rotation/reuse-detection, MFA, password reset, invitations, rate limiting, suspension, tenant isolation, action enforcement, and role rename/delete. |
| E2 | ❌ | No CI/CD or deployment | Phase 7 on the roadmap — deferred; nothing ships yet. |
| E3 | ✅ | Migrations auto-apply on startup | Gated by `Database:AutoMigrate` (default `true` for dev). Production should set `Database__AutoMigrate=false` and run `dotnet ef database update` as an explicit deploy step. |
| E4 | ✅ | `/health` is a bare 200 | Split: `/health` (liveness) and `/ready` (readiness — checks DB via `CanConnectAsync`, 503 when unreachable). |
| E5 | ❌ | No structured observability | Phase 4 on the roadmap — Serilog, request logging, and `TraceId` exist; a real monitoring sink (OTLP/Loki/…) is still not wired. |
| E6 | ⚠️ | Backups / PITR, secret rotation, prod CORS | CORS origins are now config-driven (`Cors:AllowedOrigins`), no longer hardcoded in `Program.cs`. Backups/PITR for the Postgres volume and a secret-rotation runbook remain. |

---

## F. Product-level multitenant features (P2)

| # | Status | Gap | Why it matters / resolution |
|---|---|---|---|
| F1 | ❌ | Billing & entitlements | Phase 5 on the roadmap: plan limits (users, storage), trial, and **suspension on failed payment** (the suspension mechanism from B3 is ready for it). |
| F2 | ⚠️ | Tenant provisioning workflow | Core flow exists: create tenant → roles copied from the assigned envelope → invite the admin via email link. Billing-state wiring (F1) is the missing piece. |
| F3 | ✅ | Envelope/roles model | Fully realized: envelope = template, tenant-owned role copies, action-based enforcement, no cascade hazards. |

---

## Remaining before "production"

1. **Audit trail** (D4) and **envelope/template versioning** (A6).
2. **Subdomain tenant resolution** (B5) if a per-tenant public web UX is planned.
3. **Backups/PITR + a secret-rotation runbook** (E6) and real prod CORS origins.
4. **CI/CD** (E2) with migrations as an explicit deploy step (E3) and a **monitoring sink** (E5).
5. **Billing & entitlements** (F1, F2).

---

## Status summary

| Area | ✅ Solid | ⚠️ Partial | ❌ Missing |
|---|---|---|---|
| A. Roles / actions / envelope | 5 (A1–A5) | — | 1 (A6) |
| B. Tenant lifecycle & isolation | 5 (B1–B4, B6) | 1 (B5) | — |
| C. AuthN / AuthZ | 6 (C1–C6) | — | — |
| D. Data integrity & concurrency | 2 (D1, D2) | 1 (D3) | 1 (D4) |
| E. Reliability & operations | 3 (E1, E3, E4) | 1 (E6) | 2 (E2, E5) |
| F. Product features | 1 (F3) | 1 (F2) | 1 (F1) |
