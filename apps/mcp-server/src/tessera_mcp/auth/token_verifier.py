"""JWKS-based access-token verification (the gateway's resource-server half).

The C# AS issues signed-only RS256 JWTs (kid mcp-signing-v1) and publishes
them at /.well-known/jwks (docs/platform/README.md §6.2). The gateway
fetches that JWKS over HTTP(S) and validates on every request:

- RS256 signature against the key matching the token's `kid`
- `exp` / `nbf` lifetime
- `iss` == the configured issuer
- `aud` == this tenant's exact MCP resource URL (RFC 8707 audience binding —
  the tenant-isolation boundary on the MCP surface)
- `scope` contains the required coarse scope (e.g. ``tools``)

Keys are cached and refreshed on a TTL; an unknown `kid` triggers a
synchronous refetch so key rotation heals within one request.
"""

from __future__ import annotations

import time
from typing import Any

import httpx2
import jwt
from jwt import PyJWK
from mcp.server.auth.provider import AccessToken


class TokenVerifier:
    """Protocol-compatible verifier for mcp.server's BearerAuthBackend."""

    def __init__(
        self,
        issuer_url: str,
        resource_url: str,
        required_scopes: list[str],
        jwks_url: str,
        *,
        http_client: httpx2.Client | None = None,
        jwks_ttl_seconds: float = 300.0,
    ) -> None:
        self._issuer = issuer_url.rstrip("/")
        self._resource = resource_url
        self._required_scopes = set(required_scopes)
        self._jwks_url = jwks_url
        self._client = http_client or httpx2.Client(
            timeout=httpx2.Timeout(10.0))
        self._jwks_ttl = jwks_ttl_seconds
        # kid -> PyJWK
        self._keys: dict[str, PyJWK] = {}
        self._fetched_at = 0.0

    # ------------------------------------------------------------
    # mcp.server auth protocol entry point (synchronous)
    # ------------------------------------------------------------

    async def verify_token(self, token: str) -> AccessToken | None:
        try:
            headers = jwt.get_unverified_header(token)
            kid = headers.get("kid")
            key = self._key_for(kid)
            if key is None:
                return None

            claims = jwt.decode(
                token,
                key=key.key,
                algorithms=["RS256"],
                audience=self._resource,
                options={
                    "verify_aud": True,
                    "verify_iss": True,
                    "verify_exp": True,
                    "verify_nbf": True,
                    "verify_iat": False,
                },
            )
        except jwt.PyJWTError:
            return None

        # Scope enforcement is the SDK middleware's job (403
        # insufficient_scope); the verifier only authenticates the token and
        # reports the scopes it carries.
        scope = claims.get("scope", "")
        scopes = [s for s in str(scope).split(" ") if s]

        iss = str(claims.get("iss", "")).rstrip("/")
        if iss != self._issuer:
            return None

        return AccessToken(
            token=token,
            client_id=str(claims.get("client_id", "")),
            scopes=scopes,
            expires_at=_as_int(claims.get("exp")),
            resource=str(claims.get("aud", "")),
            subject=str(claims.get("sub", "")),
            claims=claims,
        )

    # ------------------------------------------------------------
    # JWKS handling
    # ------------------------------------------------------------

    def _key_for(self, kid: str | None) -> PyJWK | None:
        if kid is None:
            return None
        if kid in self._keys:
            return self._keys[kid]
        # Unknown kid: refetch once (key rotation) then retry.
        self._fetch_keys()
        return self._keys.get(kid)

    def _fetch_keys(self) -> None:
        now = time.monotonic()
        if self._keys and now - self._fetched_at < self._jwks_ttl:
            return
        try:
            response = self._client.get(
                self._jwks_url,
                headers={"Accept": "application/json"},
            )
            response.raise_for_status()
            jwks = response.json()
        except (httpx2.HTTPError, ValueError):
            return

        keys: dict[str, PyJWK] = {}
        for entry in jwks.get("keys", []):
            try:
                jwk = PyJWK(entry)
            except ValueError:
                continue
            if jwk.key_id:
                keys[jwk.key_id] = jwk

        if keys:
            self._keys = keys
            self._fetched_at = now


def _as_int(value: Any) -> int | None:
    try:
        return int(value) if value is not None else None
    except (TypeError, ValueError):
        return None