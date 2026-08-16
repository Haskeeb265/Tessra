# Tessera — Repo Scaffolding Checklist

Stack: C# (platform: auth/authz/billing/logging/monitoring/rate limiting) · Next.js (frontend) · Multitenant SaaS · Team of 2 (one non-technical-leaning)

---

## 0. Decisions to lock in before writing any code

These are cheap to change now, expensive to change in 6 months. Don't skip this step even though it's tempting to jump into `npx create-next-app`.

- [ ] **C# owns the platform/core services layer**: authentication, authorization, billing, logging, monitoring, rate limiting — the cross-cutting concerns every tenant-facing request flows through. The Next.js frontends are consumers of these services, not owners of them. Two follow-on decisions this forces:
  - **One C# service or several?** A single "platform" service exposing auth/authz/billing/etc. as modules is simpler to run and deploy at 2-person scale. Splitting into separate services (auth service, billing service, etc.) buys you independent scaling/deploys you almost certainly don't need yet. Default to one service, split later if a specific piece needs it.
  - **How does Next.js talk to it?** Likely an internal HTTP/gRPC API. Decide the contract format now (OpenAPI is a good default — see §7) so the TS side can codegen clients instead of hand-rolling HTTP calls.
  - Cloud infra provisioning (the actual "spin up a database/VM/cluster" IaC) is a separate concern from this — decide separately whether that's Pulumi/Terraform/Bicep, and where it lives (`infra/` in §1 either way).
- [ ] **Multitenancy model**: shared DB + `tenant_id` column vs. schema-per-tenant vs. DB-per-tenant. Write this down in `ARCHITECTURE.md` on day one — retrofitting is painful.
- [ ] **Identity/auth provider**: roll your own vs. Auth0/Clerk/Azure AD B2C/WorkOS. For multitenant SaaS, an off-the-shelf provider with org/tenant support (Clerk, WorkOS, Azure AD B2C) will save you weeks.
- [ ] **Cloud target**: Azure (pairs naturally with C#), AWS, or GCP. Pick before writing Pulumi/Terraform.
- [ ] **Monorepo vs. polyrepo**: given 2 languages + small team, I'd default to **monorepo**. One PR can touch frontend + backend together, one CI setup, one source of truth for your non-technical teammate to look at. Polyrepo only pays off with larger/more independent teams.

---

## 1. Repo structure (monorepo layout)

```
Tessera/
├── apps/
│   ├── web/                 # Next.js tenant portal (business users, :3000)
│   ├── platform-portal/     # Next.js superadmin portal (tenants/envelopes, :3001)
│   └── platform/            # C# service: auth, authz, billing, logging, monitoring, rate limiting
├── infra/                   # Cloud provisioning (Pulumi/Terraform/Bicep) — separate from apps/platform above
├── packages/                # Shared code (e.g. shared TS types, OpenAPI specs)
│   └── shared-types/
├── docs/
│   ├── ARCHITECTURE.md
│   ├── ONBOARDING.md
│   └── adr/                 # Architecture Decision Records
├── .devcontainer/           # Dev container config (see §4)
├── .github/
│   ├── workflows/
│   ├── ISSUE_TEMPLATE/
│   └── PULL_REQUEST_TEMPLATE.md
├── scripts/                 # setup.sh, one-off tooling scripts
├── Makefile                 # or justfile — single entrypoint for common commands
├── .editorconfig
├── .gitignore
├── CONTRIBUTING.md
└── README.md
```

- [ ] Create the skeleton above with placeholder READMEs in each folder explaining what belongs there (helps your teammate navigate without asking you every time).

---

## 2. Language-specific setup

### C# (platform service: auth, authz, billing, logging, monitoring, rate limiting)
- [ ] `.editorconfig` with C# conventions (analyzers will enforce style).
- [ ] Enable nullable reference types (`<Nullable>enable</Nullable>`) from the start.
- [ ] `Directory.Build.props` at the repo root for shared settings across projects (so you're not repeating config in every `.csproj`).
- [ ] Solution (`.sln`) organized **by concern as separate class libraries**, even inside one deployable service — this keeps the boundaries clean if you ever do split into separate services later:
  ```
  Tessera.Platform.Api          # ASP.NET Core host, thin — wires modules together
  Tessera.Platform.Auth         # authentication
  Tessera.Platform.Authz        # authorization / permissions / tenant roles
  Tessera.Platform.Billing
  Tessera.Platform.Observability  # logging + monitoring conventions
  Tessera.Platform.RateLimiting
  Tessera.Platform.Domain       # shared tenant/user/entitlement models
  Tessera.Platform.Tests
  ```
- [ ] Expose these as an internal API (REST or gRPC) with a published OpenAPI/proto contract — this is what Next.js will codegen clients against, so treat the contract as a first-class artifact, not an afterthought.
- [ ] `dotnet format` wired into pre-commit/CI.

### Next.js (frontend)
- [ ] TypeScript strict mode on.
- [ ] App Router (not Pages Router) unless you have a specific reason not to.
- [ ] ESLint + Prettier, and make sure they don't fight each other (`eslint-config-prettier`).
- [ ] Decide early: Tailwind vs. CSS Modules vs. component library (shadcn/ui is a good default — accessible, unopinionated, easy for a less technical teammate to tweak visually).
- [ ] Env var handling via `.env.local` + a documented `.env.example`.

---

## 3. Cross-cutting foundations

- [ ] **Single entrypoint for commands.** A `Makefile` or `justfile` at the root with targets like:
  ```
  make dev        # spins up frontend, platform, db all at once
  make test       # runs all test suites
  make lint       # runs all linters
  make db-migrate
  ```
  This matters *a lot* for a non-technical teammate — they shouldn't need to remember `uv run pytest` vs `dotnet test` vs `npm run test`. One command, one mental model.

- [ ] **Docker Compose** for local dev — Postgres (or whatever DB), and ideally each service. Even partial dockerization (just the DB) removes a huge class of "works on my machine" problems.

- [ ] **Pre-commit hooks** (via `pre-commit` framework, which supports multi-language repos) running the linters/formatters above on relevant files only.

- [ ] **CI (GitHub Actions)**, split by path so you're not running the C# test suite when only the frontend changed:
  ```
  .github/workflows/
    ci-web.yml       (triggers on apps/web/**)
    ci-platform.yml  (triggers on apps/platform/**)
    ci-infra.yml     (triggers on infra/**)
  ```
  Use `paths:` filters in each workflow trigger.

- [ ] **Secrets management.** Don't let secrets live in `.env` files that get copy-pasted over Slack. Use a secrets manager (Doppler, Azure Key Vault, 1Password CLI) even at 2-person scale — it's the same effort now and saves a painful migration later.

- [ ] **Dependency and secret scanning**: enable GitHub's Dependabot and secret scanning (free on public/private repos with GitHub Advanced Security on some plans, but Dependabot alerts are free everywhere).

---

## 4. Making onboarding painless (important given your teammate)

- [ ] **Dev Container** (`.devcontainer/devcontainer.json`) — if they use VS Code, this gets them a fully working environment (Python, .NET, Node, all installed correctly) with zero manual setup. This is probably the single highest-leverage thing you can do here.
- [ ] `docs/ONBOARDING.md` — "clone repo → run this one command → you have a working dev environment" with zero assumed knowledge. Screenshot-heavy if needed.
- [ ] Keep the root `README.md` short: what Tessera is, link to `ONBOARDING.md` and `ARCHITECTURE.md`, and how to run it. Long READMEs don't get read.
- [ ] `CODEOWNERS` file so PRs auto-request the right reviewer.
- [ ] PR template with a simple checklist (tests pass, no secrets committed, etc.) so review doesn't rely on tribal knowledge.

---

## 5. Git & workflow conventions

- [ ] **Branching**: trunk-based (`main` + short-lived feature branches) is usually right for a 2-person team — GitFlow's overhead isn't worth it at this size.
- [ ] **Commit convention**: Conventional Commits (`feat:`, `fix:`, `chore:`) — cheap to adopt now, makes changelogs/semver automatable later.
- [ ] Branch protection on `main`: require PR + passing CI before merge, even solo — it's your safety net.
- [ ] Decide on a versioning/release approach for the platform service if it'll be published/deployed independently (e.g. Changesets for the JS side).

---

## 6. Multitenancy-specific groundwork

- [ ] Document the tenant isolation strategy in `docs/ARCHITECTURE.md` (see §0).
- [ ] Decide how tenant context flows through the system: JWT claim, header, subdomain (`acme.tessera.app`)? Since C# owns auth/authz, it should be the single place that mints/validates tenant context, and the Next.js frontends should treat it as the source of truth rather than each doing their own checks.
- [ ] Because rate limiting lives in the C# platform layer, decide whether limits are enforced in the platform service itself, or at a shared gateway/reverse-proxy in front of everything (the latter is often cleaner — one enforcement point instead of every service needing to call out).
- [ ] Logging/monitoring conventions (structured log format, correlation IDs, metrics naming) should be defined once in `Tessera.Platform.Observability` and documented so the frontend emits logs in a compatible shape — otherwise you end up with different logging styles that don't correlate in your dashboards.
- [ ] Make sure every log line and trace across all components carries a `tenant_id` and `request_id` for correlation. Set this convention now while it's easy.

---

## 7. Nice-to-haves once the above is solid

- [ ] Architecture Decision Records (`docs/adr/0001-multitenancy-model.md`, etc.) — lightweight, but future-you will thank present-you.
- [ ] Storybook for the frontend if the UI grows complex (skip for now if it's early).
- [ ] OpenAPI spec shared between C# platform service and Next frontend (generate TS types from it) so the two don't drift.
- [ ] License file, if this will ever be open-sourced or needs one for legal clarity even privately.

---

### Suggested order of operations
1. Lock in decisions in §0.
2. Scaffold folder structure (§1) with placeholder READMEs.
3. Get Docker Compose + Dev Container working (§4) — do this *before* writing feature code, since it de-risks your teammate's onboarding immediately.
4. Set up each language's tooling (§2) with a trivial "hello world" in each app.
5. Wire up CI (§3) against those hello-worlds so the pipeline is proven before real code lands.
6. Start building features on this foundation.