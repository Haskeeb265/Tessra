"""Tests for the JWKS token verifier (the OAuth resource-server half).

verify_token() returns None on any failure — the SDK's BearerAuthBackend
turns that into a 401 — so every negative case asserts None.
"""

from __future__ import annotations

import base64

import httpx2
import pytest
from cryptography.hazmat.primitives.asymmetric import rsa

from tessera_mcp.auth.token_verifier import TokenVerifier


def _b64url(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def _sign(claims: dict, private_key, kid: str = "mcp-signing-v1") -> str:
    import jwt as pyjwt

    return pyjwt.encode(
        claims, private_key, algorithm="RS256", headers={"kid": kid})


@pytest.fixture
def keys():
    private = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    public = private.public_key()
    jwk = {
        "kty": "RSA",
        "use": "sig",
        "alg": "RS256",
        "kid": "mcp-signing-v1",
        "n": _b64url(public.public_numbers().n.to_bytes(256, "big")),
        "e": _b64url(public.public_numbers().e.to_bytes(3, "big")),
    }
    return private, jwk


def _verifier(jwk: dict) -> TokenVerifier:
    def handler(request: httpx2.Request) -> httpx2.Response:
        return httpx2.Response(200, json={"keys": [jwk]}, request=request)

    return TokenVerifier(
        issuer_url="https://tessera.local",
        resource_url="http://mcp.test/t/acme-dental/mcp",
        required_scopes=["tools"],
        jwks_url="http://jwks.test/.well-known/jwks",
        http_client=httpx2.Client(transport=httpx2.MockTransport(handler)),
    )


def _valid_claims(**overrides) -> dict:
    claims = {
        "iss": "https://tessera.local",
        "sub": "user-123",
        "aud": "http://mcp.test/t/acme-dental/mcp",
        "exp": 4_000_000_000,
        "iat": 1_700_000_000,
        "scope": "openid offline_access tools",
        "client_id": "test-client",
    }
    claims.update(overrides)
    return claims


@pytest.mark.asyncio
async def test_valid_token_passes(keys):
    private, jwk = keys
    token = _sign(_valid_claims(), private)

    result = await _verifier(jwk).verify_token(token)

    assert result is not None
    assert result.subject == "user-123"
    assert result.scopes == ["openid", "offline_access", "tools"]


@pytest.mark.asyncio
async def test_wrong_audience_rejected(keys):
    private, jwk = keys
    token = _sign(_valid_claims(aud="http://mcp.test/t/other/mcp"), private)

    assert await _verifier(jwk).verify_token(token) is None


# NOTE: missing-scope is enforced by the SDK's RequireAuthMiddleware (403
# insufficient_scope), not by the verifier — covered in test_gateway.py.


@pytest.mark.asyncio
async def test_expired_token_rejected(keys):
    private, jwk = keys
    token = _sign(_valid_claims(exp=1_600_000_000), private)

    assert await _verifier(jwk).verify_token(token) is None


@pytest.mark.asyncio
async def test_wrong_issuer_rejected(keys):
    private, jwk = keys
    token = _sign(_valid_claims(iss="https://evil.example"), private)

    assert await _verifier(jwk).verify_token(token) is None


@pytest.mark.asyncio
async def test_garbage_token_rejected(keys):
    _, jwk = keys

    assert await _verifier(jwk).verify_token("garbage.not.a.token") is None


@pytest.mark.asyncio
async def test_token_signed_with_unknown_key_rejected(keys):
    _, jwk = keys
    other_private = rsa.generate_private_key(
        public_exponent=65537, key_size=2048)
    token = _sign(_valid_claims(), other_private, kid="other-key")

    assert await _verifier(jwk).verify_token(token) is None