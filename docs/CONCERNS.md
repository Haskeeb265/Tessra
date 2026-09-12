1. The most important gaps
🔴 G1 — First-workspace-Superadmin creation is race-prone

This is the biggest behavioral gap I see.

Your ownership rule is:

"The first redemption in a workspace becomes its Superadmin."

But that is an application-level check, not a database invariant.

The flow is effectively:

Request A                         Request B


check: no Superadmin              check: no Superadmin
        ↓                                ↓
assign Superadmin                 assign Superadmin
        ↓                                ↓
create user                        create user

Two invitations could be redeemed concurrently and both observe:

no existing Superadmin

You could therefore end up with:

Jane    → Superadmin
John    → Superadmin

The same race exists around the platform invitation decision:

hasSuperadmin = false
→ create invitation for Superadmin

Two platform admins/invocations could create two outstanding Superadmin invitations.

Your current test:

First_invite_becomes_superadmin_second_is_a_member

proves sequential correctness, not concurrent correctness.

Why this matters

The "exactly one workspace owner" rule is architectural, not cosmetic.

You need an invariant such as:

At most one live User per Tenant may hold IsSystem Superadmin.

Ideally the database should enforce this, not just C#.

For PostgreSQL, a partial unique index involving the system role is awkward because IsSystem lives on TenantRoles, but there are several ways to solve it:

introduce a dedicated IsWorkspaceOwner/role invariant,
use a transaction + locking strategy,
or redesign ownership as a tenant-level OwnerUserId.

I'd seriously consider the last one if "Superadmin" is really ownership, rather than merely a permission role.

2. 🔴 Role permission changes have a session-freshness hole

You correctly use TokenVersion for:

password changes
MFA changes
role changes
deletion.

But there is another permission-changing operation:

PUT /tenant/roles/{id}

A role's Actions can change.

Suppose:

Alice → Manager
Manager actions:
    create_widget
    edit_widget

Alice's current JWT contains:

role_id = ManagerRoleId
token_version = 0

An admin changes Manager:

Manager actions:
    view_widgets

Alice's existing token remains valid.

Now, your server-side action check does re-read the role, so Alice doesn't retain the old permission. That's good.

But there is an architectural inconsistency:

TokenVersion is described as session freshness, but some permission changes don't change it.

This isn't a security vulnerability under the current live-action authorization model. It is a semantic inconsistency.

You currently have two models simultaneously:

JWT role_id
      +
live database Actions
      +
TokenVersion

The clean rule should be explicit:

Option A — live authorization

Role changes take effect immediately and do not require token invalidation.

Then document:

JWT contains role identity only; permissions are always resolved from the current role.

This is actually what your current implementation effectively does.

Option B — snapshot authorization

JWT contains authorization state and permission changes bump TokenVersion.

That's more complicated and I wouldn't recommend it here.

I'd choose A and make TokenVersion explicitly a credential/session invalidation mechanism, not a general authorization-version mechanism.

3. 🔴 Tenant identifier changes invalidate sessions in a surprising way

This one isn't in your findings.

You explicitly support:

PUT /admin/tenants/{id}

where the identifier can change, while the internal Tenant.Id remains stable.

But tenant JWTs contain:

tenant_identifier

and Gate 10 requires:

JWT tenant_identifier == X-Tenant-Id

So:

Before:


Tenant.Identifier = acme-dental


JWT:
tenant_identifier = acme-dental

Superadmin changes it:

acme-dental → acme-health

The existing JWT now says:

tenant_identifier = acme-dental

The frontend eventually uses:

X-Tenant-Id: acme-health

Result:

403 tenant mismatch

And because refresh is also tied to the new header, the client can't simply recover using its refresh token.

So changing a tenant identifier effectively logs every tenant user out, although the code never explicitly says "invalidate sessions."

That's potentially fine, but it should be intentional.

I'd either:

document identifier changes as a breaking session event, or
make the JWT use the immutable internal tenant ID as the identity anchor, or
bump all tenant users' token versions when the identifier changes.

I'd prefer immutable tenant identifiers anyway. Treat them like slugs/keys, not display properties.

4. 🔴 The database model contradicts itself about foreign keys

> ⚠️ **DEFERRED — revisit after MCP implementation.** See note below.

The architecture diagrams describe:

User.RoleId → TenantRole
Invitation.RoleId → TenantRole

and TABLES describes them as FKs in places.

But your dry run says:

There are no foreign keys between tenant-scoped tables and Tenants.

That's fine for TenantId, but it's a different question from:

Users.RoleId → TenantRoles.Id
Invitations.RoleId → TenantRoles.Id
RefreshTokens.UserId → Users.Id

TABLES explicitly describes RefreshTokens.UserId as:

indexed — FK to Users.Id (by convention, no FK constraint)

So the docs currently blur:

logical relationship

with:

database-enforced FK

Those aren't equivalent.

Why this matters

Without actual FK enforcement, your application has to prevent:

Invitation → deleted role
User → deleted role
RefreshToken → deleted user

You already manually protect role deletion while users are assigned.

That's a symptom of the database not enforcing the relationship.

### Current relationship matrix (explicit — as of 2026-08-31)

| Relationship | Logical | DB FK | Enforced by |
|---|---|---|---|
| User → Tenant | yes | no | Finbuckle query filter |
| TenantRole → Tenant | yes | no | Finbuckle query filter |
| Widget → Tenant | yes | no | Finbuckle query filter |
| RefreshToken → Tenant | yes | no | Finbuckle query filter |
| Invitation → Tenant | yes | no | Finbuckle query filter |
| User → TenantRole | yes | no | Application: blocked delete while assigned |
| Invitation → TenantRole | yes | no | Application: validates role exists on redemption |
| RefreshToken → User | yes | no | Application: cascade on user delete |
| AppRole → Envelope | yes | **yes** | DB FK with cascade delete |

### Why this is deferred

Designing the FK strategy now means designing it twice — once for the current 9 tables, then again when MCP adds `ToolManifests`, `OAuthClients`, `Credentials`, etc. Better to wait until the full entity graph is known (post-MCP) and design a coherent strategy that covers everything at once.

**Action for AI assistant:** When MCP implementation begins, revisit this section. Design FK constraints for the complete entity graph (existing + MCP tables). Update this matrix with the final decisions.

5. 🔴 Tenant deletion is not fully atomic

Your tenant deletion performs several operations:

delete widgets
delete refresh tokens
delete invitations
soft-delete users
soft-delete tenant

and PostgreSQL uses raw SQL.

That's okay for bypassing Finbuckle, but the critical question is:

Are all of those operations inside one transaction?

If not, this is a partial-deletion risk.

For example:

widgets deleted
refresh tokens deleted
invitations deleted
→ database/network failure
→ users still live
→ tenant still exists

You've now created a partially deleted tenant.

This is especially important because deletion is one of the few places where you're deliberately bypassing normal EF/Finbuckle behavior.

Recommendation: make tenant deletion one explicit database transaction.

And I'd add a test that deliberately causes failure midway if you can.

6. 🟠 Tenant deletion leaves role garbage

You already found F5.

But I'd raise its priority slightly because of your ownership model.

You delete:

Tenant
Users
Widgets
RefreshTokens
Invitations

but retain:

TenantRoles

That means:

TenantRole.TenantId = deleted tenant

forever.

Today that's harmless because the tenant can never resolve again.

But it creates an awkward invariant:

TenantRoles contains roles belonging to tenants that don't exist anymore.

As Tessera starts acquiring manifests/tools/configuration, these orphan patterns will multiply.

I'd make deletion semantics explicit:

tenant deletion
    ↓
hard-delete tenant-owned data
    ↓
soft-delete Tenant

or:

tenant deletion
    ↓
soft-delete EVERYTHING

but don't leave miscellaneous tenant-owned records floating around indefinitely.

7. 🟠 Invitation lifecycle is under-specified

There is a subtle gap here.

Suppose:

invite Jane → Manager

Then an admin changes/deletes the Manager role.

Jane still has the invitation.

You already handle redemption failure if the role no longer exists, which is good.

But consider:

invite Jane → Manager
admin changes Manager actions
Jane redeems

She receives the current role permissions, not a snapshot of the permissions at invitation time.

That's probably correct.

But your invitation is therefore really:

"Give this person whatever role currently exists under this ID."

rather than:

"Give this person the role configuration that existed when invited."

That distinction should be explicit.

More importantly, outstanding invitations should probably be revocable.

Right now the lifecycle is essentially:

created
   ↓
redeemed
   OR
expired

Missing:

revoked

For a production onboarding system, an admin should be able to say:

"That invitation was sent to the wrong person. Kill it."

8. 🟠 Multiple outstanding invitations aren't controlled

Your anti-enumeration behavior is good.

But nothing in the described model appears to prevent:

invite jane@example.com
invite jane@example.com
invite jane@example.com
invite jane@example.com

Now Jane has four valid invitation tokens.

Only one can ultimately be redeemed because of the live-user check, but the others remain valid until expiry.

Better behavior:

new invite for same tenant + email
        ↓
invalidate previous pending invites
        ↓
create new invitation

Or explicitly allow multiple invitations but make that a conscious design choice.

9. 🟠 MFA needs a little more threat-modeling

Your TOTP implementation is solid enough for this stage, and you have RFC 6238 tests.

But I see three missing controls.

A. MFA verification attempt throttling

You rate-limit /auth/mfa, which is good. But your 20/min/IP global limiter isn't the same as:

per account
per MFA challenge

An attacker can distribute requests across IPs.

For production, consider:

MFA challenge ID
    ↓
maximum attempts
    ↓
challenge invalidated after N failures
B. MFA enrollment secret lifecycle

Enrollment returns the secret before MFA is actually enabled.

You want a clear state transition:

not enrolled
    ↓
secret generated
    ↓
code verified
    ↓
MFA enabled

If the user abandons enrollment, what happens to the generated secret?

I'd make this explicit.

C. MFA disable semantics

You're requiring the TOTP code, which is good.

But there is no mention of requiring the current password or a recovery factor.

That may be acceptable, but it's worth explicitly deciding because:

authenticated session + TOTP

is enough to disable MFA.

10. 🔴 Platform Superadmin authentication is substantially weaker than tenant authentication

Your own backlog mentions this:

Superadmin MFA — tenant MFA is done; platform accounts still use password only.

But there's a second issue:

Tenant login
    password
    + optional MFA
    + refresh
    + token version


Platform login
    password only
    4h access token
    no refresh

The platform Superadmin is actually the highest-value identity in the system.

Compromise of:

tenant account

→ one workspace.

Compromise of:

AdminUser

→ all workspaces + envelopes.

So the security hierarchy is inverted.

I'd prioritize platform MFA before adding more tenant-side auth features.

11. 🔴 /admin/auth/login isn't rate-limited

You explicitly identified this, but I would promote it.

Your tenant authentication is protected by the rate limiter:

/auth/*

but:

/admin/auth/login

isn't under that policy.

That's the password gateway to the highest-privilege account.

At minimum:

/admin/auth/login

needs its own stricter limiter.

Ideally:

per-IP
+
per-account
+
progressive backoff
12. 🟠 Public tenant enumeration is a deliberate information leak

Your architecture explicitly acknowledges:

Tenant picker is populated from public GET /tenants — reveals tenant names to anyone.

That's okay for your current workspace-picker UX.

But there is a deeper architectural consequence.

Anyone can discover:

tenant identifier
tenant display name

and therefore potentially enumerate customers.

If Tessera is eventually serving real SMBs, I'd reconsider this.

Possible alternatives:

tenant slug in URL

or:

customer-specific login link

or:

tenant discovery endpoint returning only identifiers

or simply accepting the disclosure as an explicit product decision.

Don't accidentally let a development convenience become your production tenancy-discovery protocol.

13. 🟠 X-Tenant-Id is currently both routing and security identity

This is one of the architectural things I'd revisit before MCP.

Your request effectively says:

X-Tenant-Id: acme-dental
Authorization: Bearer <token>

The server then checks:

token tenant == header tenant

That's good.

But the header itself is completely attacker-controlled.

Your security therefore depends on two independent checks:

header → tenant resolution
token → identity
token/header equality → isolation

That's workable for the dashboard.

But once you introduce the MCP gateway, don't blindly carry this model across.

The MCP request should derive tenant context from the authenticated OAuth/client relationship, not trust a caller-provided tenant header.

This is especially important given your MCP design: the generic gateway serves every tenant and treats the manifest as the contract.

14. 🔴 The MCP architecture currently has no obvious authorization boundary for tool execution

This is the biggest future gap relative to where Tessera is heading.

Your current RBAC answers:

Can Jane manage users?
Can Jane create widgets?

But MCP will need:

Can Jane call book_appointment?
Can Jane cancel_appointment?
Can Jane see patient information?

Those are end-user permissions, not SMB-admin permissions.

Your current ActionCatalog:

view_widgets
create_widget
edit_widget
delete_widget
manage_users

isn't going to scale naturally to:

mcp.tool.book_appointment
mcp.tool.cancel_appointment
...

And your mcp.md explicitly makes Jane, the AI client, and the SMB distinct actors.

So before MCP implementation, I'd define two authorization planes:

PLATFORM / WORKSPACE AUTHZ


Who can configure Tessera?
    ↓
TenantRole / ActionChecks




END-USER / TOOL AUTHZ


Who may execute an SMB's tool?
    ↓
OAuth identity + manifest/tool policy

Do not make MCP tool execution reuse manage_users-style dashboard roles unless that's explicitly the product decision.

15. 🟠 Manifest versioning needs to meet authorization versioning

Your architecture already calls envelope versioning a backlog item.

For MCP, you'll eventually have:

Manifest v1
    ↓
tool definitions
    ↓
OAuth authorization
    ↓
AI client caches tools

Then:

Manifest v2
    ↓
tool removed
tool scope changed
input schema changed
execution URL changed

You need a clear rule for what happens to:

cached tool definitions,
issued OAuth tokens,
consent,
tool scopes,
in-flight calls.

This isn't a problem for today's widget demo, but it's an architectural gap before the MCP gateway becomes real.

16. 🟠 Logout semantics are inconsistent between client and server

You describe:

POST /auth/logout

as server-side family revocation.

But the frontend doesn't call it:

Frontend just clears localStorage.

So clicking "Logout" means:

browser:
    forget tokens


server:
    refresh token remains valid

The refresh token could therefore still be used by whoever possesses it.

The access token will expire in 15 minutes, but the refresh credential survives.

This isn't necessarily catastrophic because the token is in localStorage and presumably inaccessible after normal browser use, but semantically your UI says:

Logout

while the server session remains active.

I'd make the frontend call logout before clearing credentials.

And handle failure like:

try server logout
finally clear local storage
17. 🟠 localStorage is an explicit security tradeoff

Your architecture documents:

tokens are stored in localStorage, not httpOnly cookies.

That's fine for a prototype.

But as you move toward an actual SaaS/MCP product, this becomes important.

Any XSS in the business portal can potentially obtain:

access token
refresh token

and the refresh token is the valuable one.

For a browser-facing production application, I'd seriously consider:

httpOnly Secure SameSite cookies

with an appropriate CSRF strategy.

This isn't an immediate refactor I'd force into the current demo, but it should be on the security architecture roadmap.

18. 🟠 Your error model leaks business-rule messages inconsistently

You have two different styles:

{
  "statusCode": 400,
  "message": "..."
}

versus some endpoint-level errors like:

{
  "error": "You are not authorized..."
}

Your dry run explicitly notes this for ActionChecks.

That means clients cannot assume one error contract despite the architecture saying:

"Every error response has this shape."

This is a genuine contract inconsistency.

I'd standardize it now.

19. 🟠 400 vs 401 vs 403 semantics need tightening

You currently have:

401 = authentication/session problem
403 = authorization problem

mostly correctly.

But middleware and endpoint exceptions can produce different semantics depending on where the failure happens.

For example:

missing user in ActionChecks → 401
missing RoleId → 401
role lacks action → 403

That's reasonable.

But you should define the invariant:

401 means "the caller cannot establish a valid authenticated identity."

403 means "the caller is authenticated but forbidden."

Then audit every endpoint against that rule.

That will matter enormously once the MCP gateway consumes these APIs.

20. 🟠 Optimistic concurrency isn't consistently covering child resources

You have xmin on:

Tenant
TenantRole

and concurrency handling on tenant/role updates.

But your dry run doesn't indicate comparable concurrency protection for:

User
Invitation
Widget
RefreshToken

Some of those don't need it.

But role/user operations definitely deserve thought.

For example:

Admin A changes Alice → Manager
Admin B changes Alice → Viewer

If both start from the same state, who wins?

You currently appear to have last-write-wins semantics.

That's not necessarily wrong, but it should be deliberate.

21. 🟠 Role deletion and assignment have a TOCTOU race

You do:

check whether users have role
        ↓
delete role

But another request can assign that role between those operations.

Likewise:

check role exists
        ↓
assign role

can race with:

delete role

This is another reason actual FK constraints or transaction/locking become valuable.

Your sequential tests prove the business rule, but not the concurrent rule.

22. 🟠 Invitation redemption has a similar concurrency race

Same pattern:

load invitation
check UsedAt == null
        ↓
create user
mark invitation used

Two concurrent requests with the same token could potentially both observe:

UsedAt = null

before either writes.

You need the single-use property to be atomic.

Possible solutions:

transaction + row lock,

conditional update:

UPDATE Invitations
SET UsedAt = ...
WHERE Id = ... AND UsedAt IS NULL

and require exactly one affected row,

or a database-level state transition mechanism.

Your current "replay after successful redemption" test doesn't prove concurrent single-use.

23. 🟡 InMemory vs PostgreSQL is becoming too behaviorally different

You already know:

InMemory has no indexes/constraints/raw SQL and doesn't seed tenant users.

The deeper problem is that you're using InMemory as a test provider for a system whose correctness increasingly depends on PostgreSQL behavior.

Examples:

filtered unique indexes
xmin concurrency
transactions
raw SQL
database constraints

Those cannot be meaningfully validated with EF InMemory.

So your test suite can say:

"Everything passes"

while PostgreSQL behaves differently.

For the next stage, I'd strongly consider:

PostgreSQL Testcontainer

for integration tests.

Keep InMemory for fast unit-ish endpoint tests if you want, but make PostgreSQL the authoritative integration environment.

24. 🟡 Your seed credentials are dangerous if this escapes development

You have:

superadmin@tessera.com
Admin123!

and:

admin@tessera.com
Admin123!

The architecture says these are seeds, so I assume this is intentional development scaffolding.

But the system should make it impossible to accidentally deploy these credentials.

For example:

if Production && SeedSuperAdminPassword == known default:
    throw

Or better:

production:
    no default superadmin
    bootstrap via one-time setup flow

This should happen before deployment, not after.

25. 🟡 Your docs are now starting to fight the code

You found F15–F17, and I found another important pattern:

ARCHITECTURE currently still says:

platform token 12h

in its sequence diagram, while its actual auth table says 4h.

More importantly, the architecture claims /ready is excluded from tenant validation.

Your dry run says the code doesn't do that.

This matters because your architecture document explicitly calls itself the source of truth.

I'd establish:

CODE
  ↓
generated API contract/tests
  ↓
ARCHITECTURE.md

rather than manually maintaining behavioral claims in prose.

What I would fix first

If I were driving this codebase, I would not start with F1/F2/F3 UX fixes.

I'd do this order:

Phase 1 — correctness/security
Make first-Superadmin creation atomic.
Make invitation redemption single-use atomically.
Put tenant deletion in a transaction.
Resolve actual FK vs logical-FK strategy.
Decide tenant identifier immutability.
Rate-limit /admin/auth/login.
Add platform Superadmin MFA.
Make logout actually revoke the refresh family.
Phase 2 — concurrency/data integrity
Role assignment/deletion race.
User/role concurrency semantics.
Multiple outstanding invitations.
Invitation revocation.
Tenant-role cleanup on tenant deletion.
PostgreSQL integration tests.
Phase 3 — consistency/UX
Fix /ready.
Fix Superadmin Team navigation.
Fix tenant-name lookup.
Fix stale registration copy.
Standardize error responses.
Fix platform portal refresh/session behavior.
Phase 4 — MCP boundary

Before implementing the gateway:

Define workspace-admin authorization vs end-user tool authorization.
Define tool scopes.
Define manifest versioning.
Define OAuth token → tenant → end-user binding.
Define what happens when a tool is removed/changed while tokens exist.
Define audit requirements for tool execution.
The architectural principle I'd lock in now

Your current system is actually converging on a nice separation:

                    TESSERA
                       │
          ┌────────────┴────────────┐
          │                         │
   Workspace control          End-user execution
          │                         │
   TenantRole / Actions        OAuth identity
          │                         │
   "Who can configure?"        "Who can use?"
          │                         │
      Dashboard                  MCP

Don't collapse those two authorization systems.

The dashboard's manage_users / edit_widget model is proving that your backend authorization infrastructure works. But MCP introduces a fundamentally different question: what is Jane allowed to do on behalf of the SMB?

That's where I think the next architectural pressure point will be.

The uploaded architecture already explicitly distinguishes Jane, the AI client, and the SMB, which is exactly the right direction.

Bottom line

Your dry run is very good. I wouldn't throw it away or redo the whole architecture. But I would add roughly 10–12 findings to §20, with the highest priority being:

atomic ownership assignment, atomic invitation redemption, transactional tenant deletion, database integrity, admin-auth hardening, and the future separation between workspace RBAC and MCP end-user authorization.

Those are the places where the code can currently be locally correct in a sequential dry run while still producing the wrong system behavior under concurrency, failure, or the next MCP layer.