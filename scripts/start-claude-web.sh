#!/usr/bin/env bash
# start-claude-web.sh
#
# Brings up everything Claude.ai needs to connect as an MCP host:
#   1. cloudflared tunnel -> localhost:80  (stays running at the end)
#   2. writes ORIGIN=<tunnel url> into apps/platform/.env
#   3. docker compose up -d --build   (API, MCP gateway, Caddy, stub, DB)
#   4. starts the Next.js portal on :3000 (login + consent pages live there)
#   5. verifies the OAuth surface end-to-end
#   6. prints the MCP URL to paste into Claude
#
# Usage:  ./scripts/start-claude-web.sh
# Stop:   ./scripts/start-claude-web.sh --stop
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PLATFORM_DIR="$ROOT/apps/platform"
WEB_DIR="$ROOT/apps/web"
ENV_FILE="$PLATFORM_DIR/.env"
TUNNEL_LOG="$PLATFORM_DIR/.tunnel_url.txt"
TUNNEL_PID_FILE="$PLATFORM_DIR/.tunnel.pid"
PORTAL_LOG="$WEB_DIR/.portal.log"

BASE_URL="http://localhost:80"

# ------------------------------------------------------------------
# --stop
# ------------------------------------------------------------------
if [ "${1:-}" = "--stop" ]; then
    echo "==> Stopping portal, tunnel and stack..."
    pkill -f "next dev" 2>/dev/null || true
    if [ -f "$TUNNEL_PID_FILE" ]; then
        kill "$(cat "$TUNNEL_PID_FILE")" 2>/dev/null || true
        rm -f "$TUNNEL_PID_FILE"
    fi
    pkill -f "cloudflared tunnel --url" 2>/dev/null || true
    (cd "$PLATFORM_DIR" && docker compose down)
    echo "==> Stopped."
    exit 0
fi

echo "============================================"
echo "  Tessera - Claude Web Auth Test Startup"
echo "============================================"
echo ""

# ---- 0. Docker must be running -----------------------------------
if ! docker info >/dev/null 2>&1; then
    echo "ERROR: Docker Desktop is not running."
    echo "  Start Docker Desktop, wait for the whale to settle, then re-run."
    exit 1
fi
echo "  Docker: OK"

# ---- 1. Locate cloudflared ---------------------------------------
CLOUDFLARED="${CLOUDFLARED:-}"
if [ -z "$CLOUDFLARED" ]; then
    for candidate in \
        "$HOME/cloudflared.exe" \
        "/c/Users/${USERNAME:-}/cloudflared.exe" \
        "/usr/local/bin/cloudflared" \
        "/usr/bin/cloudflared"; do
        if [ -f "$candidate" ]; then
            CLOUDFLARED="$candidate"
            break
        fi
    done
fi
if [ -z "$CLOUDFLARED" ] || [ ! -f "$CLOUDFLARED" ]; then
    echo "ERROR: cloudflared not found. Set \$CLOUDFLARED to the binary path, e.g."
    echo "  export CLOUDFLARED=/c/Users/YourName/cloudflared.exe"
    exit 1
fi
echo "  cloudflared: $CLOUDFLARED"
echo ""

# ---- 2. Start the tunnel and KEEP IT RUNNING ---------------------
# A tunnel is the only thing that makes the stack reachable from Claude.
# Killing it here (as an earlier revision did) turns every request into a
# Cloudflare 530 "Not found".
echo "==> Starting cloudflared tunnel -> $BASE_URL ..."
pkill -f "cloudflared tunnel --url" 2>/dev/null || true
sleep 1
: > "$TUNNEL_LOG"

nohup "$CLOUDFLARED" tunnel --url "$BASE_URL" > "$TUNNEL_LOG" 2>&1 < /dev/null &
TUNNEL_PID=$!
echo "$TUNNEL_PID" > "$TUNNEL_PID_FILE"
echo "  Tunnel PID: $TUNNEL_PID"

URL=""
for i in $(seq 1 60); do
    sleep 1
    URL="$(grep -oE 'https://[a-z0-9-]+\.trycloudflare\.com' "$TUNNEL_LOG" 2>/dev/null | head -1 || true)"
    [ -n "$URL" ] && break
    printf "  Waiting for tunnel... (%d/60)\r" "$i"
done
echo ""

if [ -z "$URL" ]; then
    echo "ERROR: no tunnel URL after 60s. Tunnel output:"
    tail -20 "$TUNNEL_LOG" || true
    exit 1
fi

# The tunnel needs a moment to register its connection at the edge before the
# hostname resolves; without this Claude can see a transient 530.
sleep 5
echo "==> Tunnel: $URL"
echo ""

# ---- 3. Point the stack at the new origin ------------------------
echo "ORIGIN=$URL" > "$ENV_FILE"
echo "==> Wrote ORIGIN=$URL -> apps/platform/.env"
echo ""

# ---- 4. Build + start the stack ----------------------------------
echo "==> Building and starting the stack (first build takes a few minutes)..."
cd "$PLATFORM_DIR"
docker compose up -d --build

echo ""
echo "==> Waiting for the API to become healthy (up to 90s)..."
API_READY=0
for i in $(seq 1 45); do
    sleep 2
    if [ "$(curl -sS -o /dev/null -w '%{http_code}' http://localhost:5000/health 2>/dev/null)" = "200" ]; then
        API_READY=1
        break
    fi
    printf "  Waiting for API... (%d/45)\r" "$i"
done
echo ""
if [ "$API_READY" -ne 1 ]; then
    echo "ERROR: API never became healthy. Logs:"
    docker compose logs --tail 40 api
    exit 1
fi
echo "  API: healthy"

# ---- 5. Next.js portal (login + consent pages) -------------------
# Caddy forwards everything that is not /t/* or an API path here, so the
# interactive login and consent screens need this running.
echo "==> Checking portal on :3000 ..."
if [ "$(curl -sS -o /dev/null -w '%{http_code}' http://localhost:3000/ 2>/dev/null)" != "000" ]; then
    echo "  Portal: already running"
else
    echo "  Starting portal (npm run dev)..."
    (cd "$WEB_DIR" && nohup npm run dev > "$PORTAL_LOG" 2>&1 < /dev/null &)
    PORTAL_READY=0
    for i in $(seq 1 45); do
        sleep 2
        if [ "$(curl -sS -o /dev/null -w '%{http_code}' http://localhost:3000/ 2>/dev/null)" != "000" ]; then
            PORTAL_READY=1
            break
        fi
        printf "  Waiting for portal... (%d/45)\r" "$i"
    done
    echo ""
    if [ "$PORTAL_READY" -eq 1 ]; then
        echo "  Portal: up (logs: $PORTAL_LOG)"
    else
        echo "  WARNING: portal did not come up. Check $PORTAL_LOG"
    fi
fi
echo ""

# ---- 6. Verify the OAuth surface ---------------------------------
# Checking the discovery doc against the *new* hostname is what catches a
# stale ORIGIN (old tunnel URL baked into issued tokens + advertised URLs).
echo "==> Verifying OAuth surface..."
FAIL=0

DISCO="$(curl -sS "$BASE_URL/.well-known/openid-configuration" 2>/dev/null || true)"
if echo "$DISCO" | grep -q "\"issuer\":\"$URL/\""; then
    echo "  issuer          : OK ($URL/)"
else
    echo "  issuer          : MISMATCH -- discovery still advertises an old origin"
    echo "$DISCO" | head -c 300; echo
    FAIL=1
fi

if echo "$DISCO" | grep -q "\"jwks_uri\":\"$URL/.well-known/jwks\""; then
    echo "  jwks_uri        : OK"
else
    echo "  jwks_uri        : WRONG (must be absolute on the public origin)"
    FAIL=1
fi

JWKS_CODE="$(curl -sS -o /dev/null -w '%{http_code}' "$BASE_URL/.well-known/jwks")"
if [ "$JWKS_CODE" = "200" ]; then
    echo "  /.well-known/jwks: OK (200)"
else
    echo "  /.well-known/jwks: FAILED ($JWKS_CODE)"
    FAIL=1
fi

PRM="$(curl -sS "$BASE_URL/.well-known/oauth-protected-resource/t/acme-dental/mcp" 2>/dev/null || true)"
if echo "$PRM" | grep -q "$URL/t/acme-dental/mcp"; then
    echo "  protected-resource: OK"
else
    echo "  protected-resource: WRONG resource advertised"
    FAIL=1
fi

MCP_CODE="$(curl -sS -o /dev/null -w '%{http_code}' "$BASE_URL/t/acme-dental/mcp")"
if [ "$MCP_CODE" = "401" ]; then
    echo "  MCP endpoint    : OK (401 until Claude signs in)"
else
    echo "  MCP endpoint    : unexpected $MCP_CODE (expected 401)"
    FAIL=1
fi

# Next.js dev rejects /_next/* requests that carry a non-localhost Origin
# unless allowedDevOrigins covers the host. When that regresses the login page
# still renders (SSR) but never hydrates, so the form does a native submit and
# drops ?tenant=/?returnUrl= -- surfacing as "No workspace was specified."
ASSET_CODE="$(curl -sS -o /dev/null -w '%{http_code}' -H "Origin: $URL" "$BASE_URL/_next/hmr")"
if [ "$ASSET_CODE" = "403" ]; then
    echo "  portal assets   : BLOCKED (/_next/* rejects Origin $URL)"
    echo "                    fix allowedDevOrigins in apps/web/next.config.ts"
    FAIL=1
else
    echo "  portal assets   : OK (portal can load its client bundle)"
fi

echo ""
if [ "$FAIL" -ne 0 ]; then
    echo "============================================"
    echo "  PROBLEM: the OAuth surface is not ready."
    echo "============================================"
    echo "Inspect:  cd apps/platform && docker compose logs --tail 50 api"
    exit 1
fi

# ---- 7. Done -----------------------------------------------------
echo "============================================"
echo "  READY"
echo "============================================"
echo ""
echo "Paste this URL into Claude:"
echo ""
echo "  $URL/t/acme-dental/mcp"
echo ""
echo "In Claude.ai -> Settings -> Connectors -> Add custom connector:"
echo "  URL        : $URL/t/acme-dental/mcp"
echo "  Auth       : Sign in now (Detected)"
echo "  OAuth client: Use Claude's published identity (Recommended)"
echo ""
echo "Keep this terminal's tunnel alive - it is the public entry point."
echo "Notes:"
echo "  Token endpoint is at $URL/connect/token"
echo "  Stop everything: ./scripts/start-claude-web.sh --stop"
echo ""
