# Session Progress — MCP Gateway Implementation

> Working log for the current session (branch `tessera/mcp`).
> Scope: (1) read all docs + current changes, (2) deep R&D on a plug-and-play
> MCP server that connects the local sample SMB (Acme Dental) to an online AI
> assistant, (3) test and verify everything. User confirmed: **full OAuth,
> callable through Claude web (claude.ai)**.

---

## 1. R&D conclusions (drives every implementation decision)

1. **MCP Python SDK v2** (`mcp 2.2.0`, installed) implements the
   2026-07-28 *stateless* spec: no `initialize` handshake, one POST = one
   response, `params._meta` envelope, `Mcp-Method` / `Mcp-Name` routing
   headers, `tools/list` cacheable. Low-level
   `Server(name, on_list_tools=…, on_call_tool=…)` +
   `streamable_http_app(json_response=True, stateless_http=True, host=…)` is
   exactly right for a manifest-driven per-tenant gateway. It still serves
   2025-era clients (dual-era).
2. **Authorization contract** (spec + `docs/mcp/README.md` §8): MCP server =
   OAuth 2.1 resource server → 401 + RFC 9728 `resource_metadata` challenge →
   PRM doc → AS discovery → PKCE flow. SDK has `AuthSettings` + `TokenVerifier`
   seams; our verifier validates RS256 against the C# AS JWKS with per-tenant
   `aud` binding (RFC 8707).
3. **OpenIddict 7.7 has no CIMD/DCR support** (openiddict-core#2404 open) —
   implemented as a custom `ValidateAuthorizationRequest` event handler
   (`ClientIdMetadataService`) that runs before built-in client validation.
4. **Claude web requires a public HTTPS URL** — a TLS tunnel (or public host)
   is needed for the final hop; the gateway itself is transport-agnostic.

## 2. What was built this session

### C# platform (`apps/platform`)

| File | Change |
|---|---|
| `Endpoints/GatewayEndpoints.cs` | **New.** `GET /internal/gateway/manifests?tenant={slug}` — server-to-server manifest read for the Python gateway, guarded by `X-Gateway-Api-Key`. Returns 404 unknown / 403 suspended. |
| `McpOAuth/ClientIdMetadataService.cs` | **New.** CIMD client registration: fetches + caches `client_id` metadata documents, validates client_id (https, path), loopback redirect URIs (any port), and contract compliance. Wired as an OpenIddict `ValidateAuthorizationRequest` inline handler in `Program.cs` (`SetOrder(int.MinValue + 10_000)` so it runs before the built-in client lookup). |
| `Middleware/TenantValidationMiddleware.cs` | Excluded `/internal/gateway/*` and `/.well-known/*` from tenant header validation. |
| `Middleware/TenantSuspensionMiddleware.cs` | Same exclusions. |
| `Program.cs` | Registered `GatewayEndpoints` + `ClientIdMetadataService` + OpenIddict event handler. |
| `appsettings.Development.json` / `appsettings.Docker.json` | Added `Gateway:ApiKey` + `Cimd` sections. |
| Tests | **New** `GatewayManifestTests.cs` (6 tests) + `CimdTests.cs` (2 tests) — suite is **46/46 green**. |

### Python gateway (`apps/mcp-server`, new app)

| File | Purpose |
|---|---|
| `pyproject.toml` | `uv` project; deps `mcp`, `httpx2`, `pydantic`, `pyjwt[crypto]`, `uvicorn`; dev extra `pytest`, `pytest-asyncio`. |
| `src/tessera_mcp/config.py` | Env-driven `Settings` (platform URL, gateway key, MCP base URL, issuer, auth mode, `SMB_HOST_OVERRIDES`, `CREDENTIALS`, TTLs). `resolve_credential` strips `vault://` prefix; `resolve_backend_url` rewrites manifest target hosts. |
| `src/tessera_mcp/manifest/models.py` | Pydantic `TenantCatalog` / `ToolManifest` / `ExecutionConfig` / `ExecutionAuth` mirroring the C# manifest contract. |
| `src/tessera_mcp/manifest/loader.py` | `ManifestLoader` — TTL cache over `GET /internal/gateway/manifests`, sentinel-caches unknown/suspended tenants, raises `ManifestSourceError` on platform failure with no cache. |
| `src/tessera_mcp/executors/http_executor.py` | `HttpExecutor` — `${arg}` path/body/query templating, `X-Api-Key` credential injection, response_mapping JSONPath, host overrides, `ExecutionError` surfaced to the AI client. |
| `src/tessera_mcp/utils/jsonpath.py` | Minimal JSONPath subset (`$.a.b[0][*]['k']`) with element-wise mapping after `[*]`. |
| `src/tessera_mcp/auth/token_verifier.py` | `TokenVerifier` — JWKS fetch/cache/refresh-on-unknown-`kid`, RS256, `iss`/`aud`(=tenant resource)/`exp`/`scope` validation; returns SDK `AccessToken` or `None`. |
| `src/tessera_mcp/core/gateway.py` | `TenantGateway` — per-tenant low-level `Server` + `streamable_http_app` (stateless, JSON responses), LRU-cached with `asyncio` task holding each session manager open; raw-ASGI dispatch (`/health`, `/t/{tenant}/mcp`, `/.well-known/oauth-protected-resource/...`); OAuth mode wires `AuthSettings` + verifier. |
| `src/tessera_mcp/main.py` | `uvicorn` entrypoint. |
| `stub_backend/app.py` | **New** local Acme Dental backend (`/v1/appointments` book/list/cancel + `/health` + `/v1/reset`), API-key protected. |
| `tests/` | `conftest.py` (fake platform + settings + sample manifests), `test_gateway.py`, `test_manifest_loader.py`, `test_executor.py`, `test_token_verifier.py`, `test_jsonpath.py`, `test_stub_backend.py` — **40 tests, 33 passing** (7 failures are test-side fixes, listed below). |

### Session bugs found & fixed

- jsonpath tokenizer was anchored `^` → multi-segment paths failed. Fixed.
- Wildcard `[*]` didn't map later segments element-wise. Fixed.
- `TenantGateway` handlers were bound methods (Starlette wraps them as
  `Request` handlers) → replaced with raw-ASGI `_AsgiApp` dispatch.
- 2026 stateless protocol requires `params._meta` envelope
  (`io.modelcontextprotocol/protocolVersion` + `clientCapabilities`) and
  `Mcp-Method` / `Mcp-Name` headers — test helper updated; SDK rejects
  mismatches with 400, which the tests now satisfy.
- Credential lookup must strip `vault://` scheme. Fixed in `Settings`.

## 3. First boot — VERIFIED LIVE (session milestone)

All three services ran together and the full chain worked end-to-end over
real HTTP (no test doubles):

- **C# platform** on `http://127.0.0.1:5010` (`dotnet run --no-launch-profile`,
  `ASPNETCORE_URLS` is ignored without that flag), seeded 3 Acme Dental
  manifests; `GET /internal/gateway/manifests?tenant=acme-dental` returned the
  real catalog with `X-Gateway-Api-Key: dev-gateway-key`.
- **Python gateway** on `:8000` (`MCP_AUTH_MODE=none PLATFORM_API_URL=…:5010`)
  and **stub backend** on `:9100`.
- **tools/list** returned the 3 seeded tools with correct schemas.
- **tools/call book_appointment** → appointment booked (`id 7fd4a53d3848`,
  credential injected, response mapped); **list** → shows the booking;
  **cancel** → `status: cancelled`; unknown tenant → clean **404**.

### Boot findings fixed this round

- Loader crashed on platform error payloads (pydantic on a 400 body) →
  now raises `ManifestSourceError` with the platform's message; ≥400 handled
  uniformly; non-JSON content-type rejected. Tests re-run: 39/39 green.
- `verify_token` made `async` (SDK `BearerAuthBackend` awaits it); scope
  enforcement delegated to the SDK middleware (403 insufficient_scope) instead
  of the verifier (401); verifier tests updated.
- Gateway got a `token_verifier_factory` seam for tests/custom verifiers.
- PRM tenant parsing bug fixed (`/.well-known/oauth-protected-resource/
  t/{tenant}/mcp` → tenant is `parts[1]`).
- OAuth-mode unit path (401 challenge + PRM + aud/scope enforcement) is now
  **39/39 green** in-process; the remaining live OAuth dance (Caddy + real AS)
  is part of E2E below.

## 3.5 Claude web tunnel test — CIMD blocker RESOLVED this session

- Installed `cloudflared` v2026.9.0 at `$HOME/cloudflared.exe` (no signup
  needed for temp tunnels).
- Started a cloudflared temp tunnel to `:8000` →
  `https://needed-picnic-dealers-wheels.trycloudflare.com`.
- Booted all 3 services live: C# platform :5010, stub :9100, gateway :8000.
- **Tunnel + gateway live-verified in authless mode**: health endpoint, `tools/list`,
  and a live `book_appointment` (booked `f26fcc34f5ce`) all worked through the
  public HTTPS tunnel.
- **OAuth mode live-verified through the tunnel**: restarted gateway with
  `MCP_AUTH_MODE=oauth`, `MCP_BASE_URL=<tunnel>`, `ISSUER_URL=https://tessera.local`.
  Confirmed `401 invalid_token` on an unauthenticated `tools/list` and correct
  RFC 9728 PRM metadata at
  `/.well-known/oauth-protected-resource/t/acme-dental/mcp`.
- Added the connector in claude.ai → Settings → Connectors using the tunnel URL
  + `/t/acme-dental/mcp` path, auth = "Sign in now (Detected)", OAuth client =
  "Use Claude's published identity (Recommended)".
- **Claude web OAuth flow failed — root cause found** (this is where the session
  is now):
  - The AS discovery doc did NOT advertise `client_id_metadata_document_supported:
    true` and did NOT list `none` in `token_endpoint_auth_methods_supported`.
  - Result: Claude did NOT do CIMD. It fell back and sent
    `client_id=admin@tessera.com` (the user email) to the authorize endpoint.
  - OpenIddict rejected that with `invalid_client` (ID2052) because there's no
    client registered with that ID, and our CIMD handler never ran because
    `client_id` wasn't an HTTPS URL.
  - Concretely: Claude's "Use Claude's published identity" only activates when
    the AS discovery document says it supports CIMD + public (none) token auth.
- **Fix — RESOLVED this session; verification gate passed live.**
  - **The prepared fix was wrong.** `ClientAuthenticationMethods` is not a
    property of `OpenIddictServerBuilder` in 7.7.0 (that was the CS1061) nor of
    `OpenIddictServerOptions`. Confirmed against the local NuGet package
    (`~/.nuget/packages/openiddict.server/7.7.0`) instead of guessing. The only
    client-auth API is `OpenIddictServerBuilder.AcceptAnonymousClients()`, and
    it only flips a flag — it changes **nothing** in the discovery document.
  - **A second stale API** in the same block: `AddEntityFrameworkCoreStores<T>()`
    is also gone in 7.7. Stores are registered via
    `AddCore(o => o.UseEntityFrameworkCore().UseDbContext<T>())`, and that
    overload requires the context to be registered with `AddDbContext`.
  - **Real fix for the discovery blocker:** OpenIddict 7.7 emits neither
    `client_id_metadata_document_supported` nor `"none"`, so amend the payload
    from an `OpenIddictServerEvents.ApplyConfigurationResponseContext` handler
    (`SetOrder(10_000)` → runs after the built-ins): set
    `client_id_metadata_document_supported = true`, and set
    `token_endpoint_auth_methods_supported` to include
    `ClientAuthenticationMethods.None`.
  - **Other pieces lost in the broken edit pass, now restored** (all documented
    in `docs/platform/README.md` §6.4, all required for build/tests):
    - `AddHttpClient()` — `ClientIdMetadataService` injects
      `IHttpClientFactory`; without it *every* test failed at startup.
    - OpenIddict server config: issuer, endpoint URIs, PKCE, `RegisterScopes`,
      `DisableAccessTokenEncryption()`, `SetRefreshTokenReuseLeeway(0)`,
      `DisableResourceValidation()` + ignore audience/resource permissions,
      RS256/AES keys, `UseAspNetCore()` authorize+token passthrough.
    - `McpOAuth/McpOAuthKeys.cs` (**new**) — RS256 signing
      (`kid mcp-signing-v1`) + AES encryption keys, persisted under
      `McpOAuth:KeyDirectory`, ephemeral otherwise.
    - `McpOAuth/CimdAuthorizationRequestHandler.cs` (**new**) — the actual CIMD
      wiring. `Program.cs` *claimed* CIMD was hooked into the
      validate-authorization pipeline, but no handler was ever registered, so
      CIMD clients could never register. Now runs before the built-in client
      lookup and rejects bad documents as `invalid_client` (not a 500).
    - Cookie scheme `TesseraMcpOAuth` registered via `AddCookie(...)` — the
      authorize handler was throwing 400 "No authentication handler is
      registered for the scheme 'TesseraMcpOAuth'".
    - Dev client `tessera-local-dev` seeding (public, loopback redirect,
      code+refresh grant permissions).
    - `app.MapManifestEndpoints()` and `app.MapGatewayEndpoints()` — neither was
      called, so every manifest/gateway route 404'd.
    - Acme Dental fixture: `acme-dental` was missing from the seeded tenant set
      and `SampleSmbSeeder.SeedAcmeDentalAsync` was never invoked.
  - **Verification (live this session):** booted the API on `:5099`; discovery
    now returns `token_endpoint_auth_methods_supported: [none,
    client_secret_post, client_secret_basic, private_key_jwt]` and
    `client_id_metadata_document_supported: true`. Locked in with assertions in
    `McpOAuthTests.Discovery_metadata_advertises_required_capabilities`.
  - **Suites:** C# **46/46** green, Python **39/39** green.
  - **Still not re-tested:** the claude.ai connector itself — it needs a fresh
    tunnel and the connector re-added so it doesn't reuse a stale issuer (see
    §4 item 5).

## 4. What's left (in order)

1. ~~**Fix the 7 remaining Python test failures**~~ **DONE — 39/39 green.**
   - `TenantGateway` now accepts a `token_verifier_factory` (defaults to the
     standard JWKS verifier); oauth tests inject an in-process verifier.
   - `ExecutionConfig.type` tightened to `Literal["http"]`.
   - `verify_token` is `async` (SDK `BearerAuthBackend` awaits it); missing-
     scope enforcement delegated to the SDK middleware (403) instead of the
     verifier (401).
   - PRM tenant parsing fixed: in `/.well-known/oauth-protected-resource/
     t/{tenant}/mcp` the tenant is `parts[1]`.
2. ~~**Finish the Claude web CIMD fix**~~ **DONE — discovery gate verified live.**
   Full detail in §3.5. The AS now advertises CIMD +
   `token_endpoint_auth_methods_supported` incl. `none` by amending the
   discovery response. Note the original plan (`AcceptAnonymousClients()` /
   `ClientAuthenticationMethods.Add(...)`) targeted an API that does not exist
   in OpenIddict 7.7 and would never have changed the discovery document.
   - **Remaining (user-assisted):** re-add the claude.ai connector over a fresh
     tunnel. Success = Claude opens the consent popup at the AS, log in as
     `admin@tessera.com`/`Admin123!`, approve, connector goes "Connected", and
     tools are callable from chat.
3. **Docker + Caddy orchestration** (after Claude web works):
   Dockerfile for `apps/mcp-server`, `mcp-gateway` (+ stub backend) services in
   `docker-compose.yml`, Caddy routes `tessera.local/t/*` +
   `/.well-known/oauth-protected-resource/*` → gateway.
4. **E2E OAuth dance through the real C# AS** (over Caddy TLS): PRM → CIMD
   authorize → token → authenticated tools/list + tools/call; negatives: no token
   → 401 challenge, wrong aud → 401, missing scope → 403. (The gateway-side
   OAuth unit path is verified; the live AS round-trip is not.)
5. **Claude web hop (user-assisted)**: started but NOT yet successful — connector
   was added, but the CIMD fix above is what makes it work. After the fix +
   reconnect, the success path is: consent popup → log in as
   `admin@tessera.com`/`Admin123!` → approve → connector "Connected" → call
   tools from chat.
6. **Docs**: update `docs/mcp/README.md` phases/gap list (Phase A built),
   `docs/platform` (gateway endpoint + CIMD), root `readme.md` run
   instructions, Claude web tunnel guide.
7. **Housekeeping**: delete/gitignore `apps/platform/final_cookie.txt`,
   `token_resp.json`, `pkce.txt` before commit.

## 5. Claude web OAuth — RESOLVED (2026-09-12)

Pre-flight was failing with **530 Not Found** (tunnel dead) and, once the
tunnel was back, **400 "The OpenID Connect request cannot be retrieved"** on
`/connect/authorize`. Both had concrete, now-fixed causes.

### Bug A — the startup script killed its own tunnel
`scripts/start-claude-web.sh` captured the tunnel URL and then ran
`kill $TUNNEL_PID`. With the tunnel dead, Cloudflare returned **530** for every
request. The script now keeps the tunnel alive (`nohup`, pidfile, `--stop`
flag), and also starts the Next.js portal on `:3000` (login + consent pages
live there, and Caddy proxies the catch-all to it).

### Bug B — absolute endpoint URIs never matched the inbound request
This was the real OAuth blocker. `SetAuthorizationEndpointUris`,
`SetTokenEndpointUris` and `SetJsonWebKeySetEndpointUris` were given **absolute**
`https://{issuer}/...` URIs. OpenIddict resolves an inbound request against the
configured endpoint URIs, and behind Caddy every request arrives as
**`http://<host>/...`** — so nothing matched, OpenIddict never parsed the
request, and the passthrough handler threw
`InvalidOperationException: The OpenID Connect request cannot be retrieved`.
(Absolute URIs also 404'd the JWKS endpoint, which is why it needed a bespoke
middleware in the first place.)

- **Fix:** endpoint URIs are **relative** again (`connect/authorize`,
  `connect/token`, `.well-known/jwks`).
- The public `https://` URLs are produced correctly by **trusting forwarded
  headers**: `ForwardedHeadersOptions` (`XForwardedFor|Host|Proto`) +
  `app.UseForwardedHeaders()` as the first middleware, with Caddy forcing
  `header_up X-Forwarded-Proto https` via the `(tls_hop)` snippet. TLS always
  ends at the tunnel/edge, so that scheme is always truthful.

### Bug C — JWKS served the wrong key / crashed
Publishing a key that never matched the signer would have produced silent
signature-mismatch failures later.

- **Bug C1 (critical):** `GetJwksDocument()` called `Load(null!)`; a null
  `IConfiguration` makes `McpOAuthConfig.KeyDirectory` return `""`, so it took
the **ephemeral** branch on every request — a brand-new random key each time,
  never the persisted signer.
- **Bug C2:** the ephemeral `Load()` was not memoized, so each call generated a
  fresh `RSA.Create(4096)`; `JsonWebKey.Create()` on those threw
  `ArgumentNullException: IDX10000: The parameter 'inArray' cannot be a 'null'
  or an empty object`.
- **Fix:** `GetJwksDocument(RsaSecurityKey signingKey)` takes the key as a
  parameter and builds the JWK by hand (base64url `n`/`e` from
  `ExportParameters`, no `JsonWebKey.Create`); the key is loaded **once** into
a local shared by both the OpenIddict config and the JWKS middleware (it was
  previously declared inside the `AddServer` lambda, so nothing outside could
  reach it); the ephemeral branch is `Lazy<>`-memoized.

### Bug D — the login page rendered but never hydrated
After Bug B was fixed the flow reached the portal, but submitting credentials
just reloaded the login page with **"No workspace was specified."**

- The portal's own log showed the smoking gun:
  `GET /oauth/login?` (empty query) repeated, plus
  `⚠ Blocked cross-origin request to Next.js dev resource /_next/hmr from
  "<tunnel>"`.
- Next.js dev rejects `/_next/*` requests that carry a non-localhost `Origin`
  (403) unless the host is listed in `allowedDevOrigins`. So the client bundle
  never loaded, the page never hydrated, and the sign-in button fell back to a
  **native form submit** → `GET /oauth/login?` → `tenant` came back empty.
  Nothing ever reached `/connect/login` (confirmed absent from the API log).
- **Fix:** `apps/web/next.config.ts` now sets
  `allowedDevOrigins: ["*.trycloudflare.com", "localhost:3000",
  "127.0.0.1:3000"]`. The wildcard survives a new tunnel hostname on each run.
- Verified: `GET /_next/hmr` with `Origin: <tunnel>` went **403 → 404** (no
  longer blocked), and a real dev chunk returns **200
  application/javascript**.
- The start script now asserts this (`portal assets : OK`) so it can't
  silently regress.

### Bug E — the login form posted empty credentials
After Bug D the fetch finally fired, but the API logged
`POST /connect/login ... application/json 26` — 26 bytes is exactly
`{"email":"","password":""}` (a working login posts 52) and the call
returned **401**.

- React state was empty even though the fields *looked* filled. Browser
  password managers autofill — and anything typed before hydration completes
  sticks in the DOM — **without firing `onChange`** on a controlled input, so
  `email`/`password` stayed `""` while the DOM held the real values.
- **Fix (`apps/web/app/oauth/login/page.tsx`):** the inputs gained `name`
  attributes and `handleSubmit` / `handleMfaSubmit` now read the submitted
  values from `new FormData(e.currentTarget)`, falling back to state only when
  a field is genuinely empty.
- Verified: `name="email"` / `name="password"` present in the SSR HTML and the
  page hot-recompiles cleanly.
- This is a real-user bug, not a test artifact — anyone whose password manager
  fills the connector login would have hit it. The missing `name` attributes
  also meant a native (unhydrated) submit sent no values at all.

### Verified live (2026-09-12, through the public tunnel)

| Check | Result |
|---|---|
| `/.well-known/openid-configuration` `issuer` | `https://<tunnel>/` |
| `authorization_endpoint` / `token_endpoint` | `https://<tunnel>/...` |
| `jwks_uri` | `https://<tunnel>/.well-known/jwks` |
| `/.well-known/jwks` | **200**, 4096-bit key, `kid=mcp-signing-v1`, `alg=RS256`, `use=sig` |
| JWKS modulus vs `signing.pem` `openssl -modulus` | **match** (identical 4096-bit modulus) |
| `/.well-known/oauth-protected-resource/t/acme-dental/mcp` | 200, `resource` = public MCP URL |
| `/t/acme-dental/mcp` unauthenticated | **401** + RFC 9728 challenge |
| `/connect/authorize` (PKCE, `tessera-local-dev`) | **302 → `/oauth/login?tenant=acme-dental&returnUrl=<public URL>`** |
| Portal `/oauth/login` through the tunnel | 200, HTML (email + password form) |
| C# suite | **46/46** green |

### End-to-end tool call — VERIFIED (2026-09-12)
The connector connected in claude.ai, and the resource-server half was then
exercised directly with a real RS256 token minted by the AS (PKCE,
`tessera-local-dev`, `resource=<tunnel>/t/acme-dental/mcp`):

| Call | Result |
|---|---|
| `tools/list` | 200 — all three tools |
| `tools/call list_appointments` | 200 — `[]` |
| `tools/call book_appointment` | 200 — id `06e80074ab03`, `authenticated_via: dev-acme-api-key-123` |
| `tools/call list_appointments` (again) | 200 — shows the booking (round trip) |

- Token claims confirmed: `iss` = tunnel origin, `aud` =
  `<tunnel>/t/acme-dental/mcp` (RFC 8707 tenant binding is correct),
  `scope = tools offline_access`.
- **Open question:** the coarse `tools` scope is accepted for every tool, so the
  manifests' `required_scopes` (`appointments:read` / `appointments:write`) are
  **not enforced** by the gateway. Fine for v1, but worth deciding deliberately.
- Reset the Acme Dental fixture between runs:
  `docker exec platform-stub-backend-1 python -c "import urllib.request as u;
  print(u.urlopen(u.Request('http://localhost:9100/v1/reset', method='POST',
  data=b'', headers={'X-Api-Key':'dev-acme-api-key-123'})).read())"`

#### Tool contract vs. authorization — what actually happened

Asking Claude to "list appointments" (meaning Jane Doe's) produced a **refusal,
not a call**. The tools were offered fine (`Acme:list_appointments` etc.), but
Claude declined, reasoning that a tool described as *"Lists the current
patient's upcoming appointments"* which nonetheless takes an arbitrary
`patient_name` is being used to look up a third party's health records without
authorization.

- **The tool contract really is contradictory.** A self-scoped tool should take
  no patient argument and derive identity from the token; a staff-facing tool
  should say so explicitly. Neither is written down, so the model resolved the
  ambiguity conservatively. That is a genuine usability bug in the manifest
  descriptions — a legitimate clinic workflow would be refused the same way.
- **That refusal proves nothing about server-side isolation.** No HTTP call was
  made; only Claude's own judgement was exercised. What *was* demonstrated, by
  calling the gateway directly with a real token for `admin@tessera.com`:
  `book_appointment` booked for `patient_name: "Jane Doe"` and
  `list_appointments` returned it. Authorization is **per tenant, not per
  user** — the stub filters on the caller-supplied `patient` query param
  (`apps/mcp-server/stub_backend/app.py`) and nothing ties an appointment to the
  token's `sub`.
- **Decision needed before this is more than a demo:** either scope the tools to
  the authenticated user (drop `patient_name`, resolve identity from the token,
  enforce it server-side) or declare them staff/practice-management tools and
  gate them behind an explicit role + scope. Note the manifests'
  `required_scopes` (`appointments:read` / `appointments:write`) are currently
  **not enforced** by the gateway, so they cannot carry that weight today.

### Remaining (user-assisted)
Add/re-add the connector in claude.ai against the printed tunnel URL. Success =
Claude opens the login/consent screen, sign in as `admin@tessera.com` /
`Admin123!`, approve, connector shows "Connected", tools callable from chat.

### Gotchas found along the way
- Docker Desktop must be running before the script; the script now checks.
- `trycloudflare.com` hostnames are only resolvable/reachable through
  Cloudflare's edge in some setups — verify with a real `curl` to the public URL,
  not `localhost`.
- A tunnel started before Caddy is listening can end up serving 502/530 until
  restarted; the script starts the tunnel only once the stack is up.
- Piping this repo's start script to `tail` can hang the pipeline because the
  background tunnel/portal inherit the pipe; run it directly in a terminal.

## 6. Commands

```bash
# C# suite (46 passing)
cd apps/platform && dotnet test src/Tessera.Platform.Tests

# Python suite (39 passing)
cd apps/mcp-server && uv run pytest -q

# Start everything for Claude web testing:
./scripts/start-claude-web.sh
# → prints the tunnel URL; paste into Claude.ai → Settings → Connectors

# Check JWKS endpoint (must be 200 and match the signing key):
curl -sS http://localhost:80/.well-known/jwks

# Verify the published key IS the signing key (moduli must be equal):
U=$(grep -oE 'https://[a-z0-9-]+\.trycloudflare\.com' apps/platform/.env)
docker cp platform-api-1:/app/keys/signing.pem /tmp/signing.pem
export PEM_MOD=$(openssl rsa -in /tmp/signing.pem -noout -modulus | sed 's/^Modulus=//')
curl -sS "$U/.well-known/jwks" | python -c "
import sys,os,base64,json
k=json.load(sys.stdin)['keys'][0]; n=k['n']
n=int.from_bytes(base64.urlsafe_b64decode(n+'='*(-len(n)%4)),'big')
print('kid/alg/use:',k.get('kid'),k.get('alg'),k.get('use'))
print('MATCH:', n==int(os.environ['PEM_MOD'],16))"

# Simulate Claude's authorize request (expect 302 -> /oauth/login):
curl -sS -o /dev/null -D - "$U/connect/authorize?response_type=code&client_id=tessera-local-dev&redirect_uri=http%3A%2F%2F127.0.0.1%3A9876%2Fcallback&scope=tools&resource=$U%2Ft%2Facme-dental%2Fmcp&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256" | grep -i location

# Check discovery doc (working):
curl -sS http://localhost:80/.well-known/openid-configuration | python3 -m json.tool

# Check PRM (working):
curl -sS http://localhost:80/.well-known/oauth-protected-resource/t/acme-dental/mcp

# Restart just the API after a code change:
docker compose -f apps/platform/docker-compose.yml down api && 
  docker compose -f apps/platform/docker-compose.yml up -d --build api
```

Boot gotchas (bit us, documented here so they don't again):
- `dotnet run` ignores `ASPNETCORE_URLS` unless `--no-launch-profile` is passed
  (launchSettings wins); port 5000 is taken by WSL/Docker relays on this machine.
- The gateway defaults to `MCP_AUTH_MODE=oauth` — unauthenticated calls get a
  correct 401 RFC 9728 challenge; pass `MCP_AUTH_MODE=none` for authless dev.
- A platform error payload (e.g. middleware 400) previously crashed the loader
  with a pydantic error; it now raises `ManifestSourceError` with the
  platform's message.
