#!/usr/bin/env bash
# Minimal smoke-test runner for the live platform API.
# Requirements (env):
#   API                       base URL (default http://localhost:5000)
#   JWT__SECRET_KEY          platform JWT secret (must match configured API secret)
#   JWT__ISSUER / AUDIENCE   optional; must match API config if set
#   SA_EMAIL / SA_PASSWORD   platform superadmin (defaults: seed values)
#   TENANT_ADMIN_EMAIL / PW  seeded tenant admin (defaults: seed values;
#                            seeded per-tenant on PostgreSQL only)
set -euo pipefail

API="${API:-http://localhost:5000}"
SA_EMAIL="${SA_EMAIL:-superadmin@tessera.com}"
SA_PASSWORD="${SA_PASSWORD:-Admin123!}"
TENANT_ADMIN_EMAIL="${TENANT_ADMIN_EMAIL:-admin@tessera.com}"
TENANT_ADMIN_PASSWORD="${TENANT_ADMIN_PASSWORD:-Admin123!}"

check() {
  local name="$1" expected="$2"
  shift 2
  echo "==> $name"
  local out
  if ! out="$(curl -sS -w '\n%{http_code}' "$@" 2>&1)"; then
    echo "curl failed: $out"
    return 1
  fi
  local body="${out%$'\n'*}"
  local status="${out##*$'\n'}"
  echo "HTTP $status"
  echo "$body" | sed -n '1,200p'
  if [ "$status" != "$expected" ]; then
    echo "EXPECTED $expected, GOT $status"
    return 1
  fi
  echo "OK"
  echo ""
}

auth() {
  local out
  out="$(curl -sS -X POST "$API/admin/auth/login" -H 'Content-Type: application/json' \
    -d "{\"email\":\"$SA_EMAIL\",\"password\":\"$SA_PASSWORD\"}" \
    -w '\n%{http_code}' 2>&1)"
  local body="${out%$'\n'*}"
  local status="${out##*$'\n'}"
  echo "==> superadmin login -> HTTP $status" >&2
  echo "$body" | sed -n '1,200p' >&2
  if [ "$status" != "200" ]; then echo FAIL >&2; return 1; fi
  local token
  token="$(printf '%s' "$body" | sed -n 's/.*"accessToken":"\([^"]*\)".*/\1/p')"
  if [ -z "$token" ]; then echo "no accessToken in body" >&2; return 1; fi
  echo "token=$token" >&2
  printf '%s' "$token"
}

tenant_auth() {
  local tenant="$1"
  local out
  out="$(curl -sS -X POST "$API/auth/login" -H "X-Tenant-Id: $tenant" -H 'Content-Type: application/json' \
    -d "{\"email\":\"$TENANT_ADMIN_EMAIL\",\"password\":\"$TENANT_ADMIN_PASSWORD\"}" \
    -w '\n%{http_code}' 2>&1)"
  local body="${out%$'\n'*}"
  local status="${out##*$'\n'}"
  echo "==> tenant admin login ($tenant) -> HTTP $status" >&2
  echo "$body" | sed -n '1,200p' >&2
  if [ "$status" != "200" ]; then echo FAIL >&2; return 1; fi
  local token
  token="$(printf '%s' "$body" | sed -n 's/.*"accessToken":"\([^"]*\)".*/\1/p')"
  if [ -z "$token" ]; then echo "no accessToken in body" >&2; return 1; fi
  echo "token=$token" >&2
  printf '%s' "$token"
}

# Creates a throwaway manifest as the tenant admin; prints its id.
create_manifest() {
  local token="$1"
  local out
  out="$(curl -sS -X POST "$API/tenant/manifests" \
    -H "Authorization: Bearer $token" \
    -H 'X-Tenant-Id: acme-dental' \
    -H 'Content-Type: application/json' \
    -d '{
      "toolName": "smoke_test_tool",
      "description": "Throwaway tool created by the smoke test.",
      "inputSchema": {
        "type": "object",
        "properties": { "name": { "type": "string" } },
        "required": ["name"]
      },
      "execution": {
        "type": "http",
        "method": "POST",
        "url": "https://api.acmedental.test/v1/smoke",
        "auth": { "type": "api_key", "credential_ref": "vault://acme-dental/booking-api-key" }
      },
      "requiredScopes": ["appointments:write"]
    }' \
    -w '\n%{http_code}' 2>&1)"
  local body="${out%$'\n'*}"
  local status="${out##*$'\n'}"
  echo "==> create throwaway manifest -> HTTP $status" >&2
  echo "$body" | sed -n '1,200p' >&2
  if [ "$status" != "201" ]; then echo FAIL >&2; return 1; fi
  local id
  id="$(printf '%s' "$body" | sed -n 's/.*"id":"\([^"]*\)".*/\1/p')"
  if [ -z "$id" ]; then echo "no id in body" >&2; return 1; fi
  printf '%s' "$id"
}

echo "Smoke-test start: API=$API"

check "health GET /health" 200 -X GET "$API/health"
check "readiness GET /ready" 200 -X GET "$API/ready"
check "public tenants GET /tenants" 200 -X GET "$API/tenants"

TOKEN="$(auth)"
# shellcheck disable=SC2181
if [ $? -ne 0 ]; then exit 1; fi

check "superadmin tenants list GET /admin/tenants" 200 \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -X GET "$API/admin/tenants"

# ------------------------------------------------------------
# Seeded sample SMB (docs/sample_smb.md): acme-dental must be
# present at startup with its tool manifests stored via the API.
# ------------------------------------------------------------

check "seeded sample SMB GET /admin/tenants/acme-dental" 200 \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -X GET "$API/admin/tenants/acme-dental"

TENANT_TOKEN="$(tenant_auth acme-dental)"
# shellcheck disable=SC2181
if [ $? -ne 0 ]; then
  echo "NOTE: tenant admin login requires the seeded per-tenant user" \
       "(PostgreSQL). Skipping manifest checks."
else
  check "sample SMB manifests GET /tenant/manifests" 200 \
    -H "Authorization: Bearer $TENANT_TOKEN" \
    -H 'X-Tenant-Id: acme-dental' \
    -H 'Content-Type: application/json' \
    -X GET "$API/tenant/manifests"

  MANIFEST_ID="$(create_manifest "$TENANT_TOKEN")"
  # shellcheck disable=SC2181
  if [ $? -ne 0 ]; then exit 1; fi

  check "delete throwaway manifest DELETE /tenant/manifests/$MANIFEST_ID" 204 \
    -H "Authorization: Bearer $TENANT_TOKEN" \
    -H 'X-Tenant-Id: acme-dental' \
    -X DELETE "$API/tenant/manifests/$MANIFEST_ID"

  check "sample SMB back to seeded set GET /tenant/manifests" 200 \
    -H "Authorization: Bearer $TENANT_TOKEN" \
    -H 'X-Tenant-Id: acme-dental' \
    -H 'Content-Type: application/json' \
    -X GET "$API/tenant/manifests"
fi

# ------------------------------------------------------------
# Tenant CRUD cycle on a SCRATCH tenant (acme-dental is seeded,
# so it must not be created/deleted by the smoke test).
# ------------------------------------------------------------

check "create scratch tenant POST /admin/tenants" 201 \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -X POST "$API/admin/tenants" \
  -d '{"identifier":"smoke-test-co","name":"Smoke Test Co","envelopeId":null}'

check "get scratch tenant GET /admin/tenants/smoke-test-co" 200 \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -X GET "$API/admin/tenants/smoke-test-co"

check "suspend scratch tenant PUT /admin/tenants/smoke-test-co/status" 200 \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -X PUT "$API/admin/tenants/smoke-test-co/status" \
  -d '{"status":"Suspended"}'

check "unsuspend scratch tenant PUT /admin/tenants/smoke-test-co/status" 200 \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -X PUT "$API/admin/tenants/smoke-test-co/status" \
  -d '{"status":"Active"}'

check "duplicate identifier guard POST /admin/tenants" 400 \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -X POST "$API/admin/tenants" \
  -d '{"identifier":"smoke-test-co","name":"Smoke Test Dup","envelopeId":null}'

check "bad identifier guard POST /admin/tenants" 400 \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -X POST "$API/admin/tenants" \
  -d '{"identifier":"bad id!","name":"Bad Identifier","envelopeId":null}'

check "delete scratch tenant DELETE /admin/tenants/smoke-test-co" 204 \
  -H "Authorization: Bearer $TOKEN" \
  -X DELETE "$API/admin/tenants/smoke-test-co"

check "deleted tenant gone GET /admin/tenants/smoke-test-co" 404 \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -X GET "$API/admin/tenants/smoke-test-co"

# Tenant rows are soft-deleted, so the identifier stays reserved.
check "re-create after delete is rejected POST /admin/tenants" 400 \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -X POST "$API/admin/tenants" \
  -d '{"identifier":"smoke-test-co","name":"Smoke Test Co","envelopeId":null}'

echo "Smoke-test complete"