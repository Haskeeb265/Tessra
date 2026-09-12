"""Gateway tests: the full manifest -> MCP tools -> HTTP executor -> stub
path, both over HTTP (the real Streamable HTTP surface) and in-process."""

from __future__ import annotations

import json

import httpx2
import pytest
from starlette.applications import Starlette
from starlette.testclient import TestClient

from stub_backend.app import app as stub_app

from tessera_mcp.core.gateway import TenantGateway
from tessera_mcp.executors.http_executor import HttpExecutor
from tessera_mcp.manifest.loader import ManifestLoader

MCP_HEADERS = {
    "MCP-Protocol-Version": "2026-07-28",
    "Accept": "application/json, text/event-stream",
    "Content-Type": "application/json",
}

# The 2026-07-28 stateless protocol carries the envelope in params._meta.
MCP_META = {
    "io.modelcontextprotocol/protocolVersion": "2026-07-28",
    "io.modelcontextprotocol/clientCapabilities": {},
}


def _build_gateway(settings, fake_platform: Starlette) -> TenantGateway:
    loader = ManifestLoader(
        platform_api_url=settings.platform_api_url,
        api_key=settings.gateway_api_key,
        ttl_seconds=60,
        client=httpx2.AsyncClient(
            transport=httpx2.ASGITransport(app=fake_platform),
            base_url="http://platform",
        ),
    )
    executor = HttpExecutor(
        resolve_credential=settings.resolve_credential,
        resolve_backend_url=settings.resolve_backend_url,
        client=httpx2.AsyncClient(
            transport=httpx2.ASGITransport(app=stub_app),
            base_url="http://api.acmedental.test",
        ),
        timeout=10.0,
    )
    return TenantGateway(settings, loader, executor)


def _post_mcp(
    client: TestClient,
    path: str,
    method: str,
    params: dict | None = None,
    request_id: int = 1,
    name: str | None = None,
):
    body: dict = {"jsonrpc": "2.0", "id": request_id, "method": method}
    body["params"] = {"_meta": MCP_META}
    if params is not None:
        body["params"].update(params)
    headers = {
        **MCP_HEADERS,
        "Mcp-Method": method,
    }
    if name is not None:
        headers["Mcp-Name"] = name
    return client.post(path, json=body, headers=headers)


# ================================================================
# tools/list + tools/call over HTTP (authless mode)
# ================================================================


def test_tools_list_returns_the_tenant_manifest(
    acme_settings, fake_platform: Starlette
):
    gateway = _build_gateway(acme_settings, fake_platform)

    with TestClient(gateway.build_app()) as client:
        response = _post_mcp(client, "/t/acme-dental/mcp", "tools/list")

        assert response.status_code == 200, response.text
        result = response.json()["result"]
        names = [t["name"] for t in result["tools"]]
        assert names == [
            "book_appointment",
            "cancel_appointment",
            "list_appointments",
        ]
        book = result["tools"][0]
        assert book["description"].startswith("Books a dental appointment")
        assert book["inputSchema"]["required"] == [
            "patient_name", "date", "time", "service_type",
        ]


def test_call_tool_books_lists_and_cancels_appointments(
    acme_settings, fake_platform: Starlette
):
    gateway = _build_gateway(acme_settings, fake_platform)

    with TestClient(gateway.build_app()) as client:
        with client:
            client.post("/v1/reset")

            # -- book
            book = _post_mcp(
                client,
                "/t/acme-dental/mcp",
                "tools/call",
                {
                    "name": "book_appointment",
                    "arguments": {
                        "patient_name": "Jane Doe",
                        "date": "2026-10-05",
                        "time": "14:30",
                        "service_type": "cleaning",
                    },
                },
                name="book_appointment",
            )
            assert book.status_code == 200, book.text
            appointment = book.json()["result"]["structuredContent"]
            assert appointment["patient"] == "Jane Doe"
            assert appointment["preferred_time"] == "2026-10-05T14:30:00Z"
            assert appointment["service"] == "cleaning"
            appointment_id = appointment["id"]

            # -- list
            listing = _post_mcp(
                client,
                "/t/acme-dental/mcp",
                "tools/call",
                {
                    "name": "list_appointments",
                    "arguments": {"patient_name": "Jane Doe"},
                },
                request_id=2,
                name="list_appointments",
            )
            assert listing.status_code == 200, listing.text
            appointments = listing.json()["result"]["structuredContent"]
            assert len(appointments) == 1
            assert appointments[0]["id"] == appointment_id

            # -- cancel
            cancelled = _post_mcp(
                client,
                "/t/acme-dental/mcp",
                "tools/call",
                {
                    "name": "cancel_appointment",
                    "arguments": {"appointment_id": appointment_id},
                },
                request_id=3,
                name="cancel_appointment",
            )
            assert cancelled.status_code == 200, cancelled.text
            cancellation = cancelled.json()["result"]["structuredContent"]
            assert cancellation["status"] == "cancelled"


def test_tenants_are_isolated(acme_settings, fake_platform: Starlette):
    gateway = _build_gateway(acme_settings, fake_platform)

    with TestClient(gateway.build_app()) as client:
        beta = _post_mcp(client, "/t/beta-dental/mcp", "tools/list")
        assert beta.status_code == 200
        names = [t["name"] for t in beta.json()["result"]["tools"]]
        assert names == ["ping"]

        # acme's tools must not leak into beta's listing
        assert "book_appointment" not in names


def test_unknown_tenant_returns_404(acme_settings, fake_platform: Starlette):
    gateway = _build_gateway(acme_settings, fake_platform)

    with TestClient(gateway.build_app()) as client:
        response = _post_mcp(client, "/t/no-such-co/mcp", "tools/list")
        assert response.status_code == 404


def test_unknown_tool_and_missing_arguments_are_is_error(
    acme_settings, fake_platform: Starlette
):
    gateway = _build_gateway(acme_settings, fake_platform)

    with TestClient(gateway.build_app()) as client:
        unknown = _post_mcp(
            client,
            "/t/acme-dental/mcp",
            "tools/call",
            {"name": "nope", "arguments": {}},
            name="nope",
        )
        assert unknown.status_code == 200
        assert unknown.json()["result"]["isError"] is True

        missing = _post_mcp(
            client,
            "/t/acme-dental/mcp",
            "tools/call",
            {"name": "book_appointment", "arguments": {}},
            name="book_appointment",
        )
        assert missing.status_code == 200
        result = missing.json()["result"]
        assert result["isError"] is True
        assert "Missing required argument" in result["content"][0]["text"]


def test_health_endpoint(acme_settings, fake_platform: Starlette):
    gateway = _build_gateway(acme_settings, fake_platform)

    with TestClient(gateway.build_app()) as client:
        response = client.get("/health")
        assert response.status_code == 200
        assert response.json() == {"status": "ok"}


# ================================================================
# In-process Client(server) — the documented SDK test path
# ================================================================


def test_in_process_client_lists_and_calls_tools(
    acme_settings, fake_platform: Starlette
):
    import asyncio

    from mcp import Client

    gateway = _build_gateway(acme_settings, fake_platform)

    async def run() -> None:
        server = gateway._servers["acme-dental"].server if False else None
        # Force creation of the tenant server, then grab its Server object.
        await gateway._tenant_app("acme-dental")
        server = gateway._servers["acme-dental"].server

        async with Client(server) as client:
            tools = await client.list_tools()
            names = [t.name for t in tools.tools]
            assert names == [
                "book_appointment",
                "cancel_appointment",
                "list_appointments",
            ]

            result = await client.call_tool(
                "book_appointment",
                {
                    "patient_name": "In-Process Patient",
                    "date": "2026-11-01",
                    "time": "09:15",
                    "service_type": "checkup",
                },
            )
            assert result.structured_content["patient"] == "In-Process Patient"

    asyncio.run(run())


# ================================================================
# OAuth-protected mode
# ================================================================


@pytest.fixture
def oauth_keys():
    from cryptography.hazmat.primitives import serialization
    from cryptography.hazmat.primitives.asymmetric import rsa

    private = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    public = private.public_key()
    jwk = {
        "kty": "RSA",
        "use": "sig",
        "alg": "RS256",
        "kid": "mcp-signing-v1",
        "n": _b64url(
            public.public_numbers().n.to_bytes(256, "big")),
        "e": _b64url(
            public.public_numbers().e.to_bytes(3, "big")),
    }
    return private, jwk


def _b64url(data: bytes) -> str:
    import base64

    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def _make_oauth_gateway(settings, fake_platform, jwk, tls_insecure=False):
    """Gateway in oauth mode with a verifier pointed at an in-process JWKS."""

    from tessera_mcp.auth.token_verifier import TokenVerifier

    def jwks_handler(request: httpx2.Request) -> httpx2.Response:
        return httpx2.Response(
            200, json={"keys": [jwk]}, request=request)

    loader = ManifestLoader(
        platform_api_url=settings.platform_api_url,
        api_key=settings.gateway_api_key,
        ttl_seconds=60,
        client=httpx2.AsyncClient(
            transport=httpx2.ASGITransport(app=fake_platform),
            base_url="http://platform",
        ),
    )
    executor = HttpExecutor(
        resolve_credential=settings.resolve_credential,
        resolve_backend_url=settings.resolve_backend_url,
        client=httpx2.AsyncClient(
            transport=httpx2.ASGITransport(app=stub_app),
            base_url="http://api.acmedental.test",
        ),
        timeout=10.0,
    )

    def verifier_factory(resource_url: str) -> TokenVerifier:
        return TokenVerifier(
            issuer_url=settings.issuer_url,
            resource_url=resource_url,
            required_scopes=["tools"],
            jwks_url="http://jwks.test/.well-known/jwks",
            http_client=httpx2.Client(
                transport=httpx2.MockTransport(jwks_handler)),
        )

    from tessera_mcp.config import Settings

    oauth_settings = Settings(
        platform_api_url=settings.platform_api_url,
        gateway_api_key=settings.gateway_api_key,
        mcp_base_url=settings.mcp_base_url,
        issuer_url=settings.issuer_url,
        auth_mode="oauth",
        smb_host_overrides=settings.smb_host_overrides,
        credentials=settings.credentials,
    )

    return TenantGateway(oauth_settings, loader, executor,
                         token_verifier_factory=verifier_factory)


def _sign(claims: dict, private_key) -> str:
    import jwt as pyjwt

    return pyjwt.encode(
        claims, private_key, algorithm="RS256",
        headers={"kid": "mcp-signing-v1"})


def test_oauth_mode_401_without_token_and_200_with_valid_token(
    acme_settings, fake_platform, oauth_keys
):
    private_key, jwk = oauth_keys
    gateway = _make_oauth_gateway(acme_settings, fake_platform, jwk)

    with TestClient(gateway.build_app()) as client:
        # -- no token -> 401 + RFC 9728 resource_metadata challenge
        bare = _post_mcp(client, "/t/acme-dental/mcp", "tools/list")
        assert bare.status_code == 401, bare.text
        challenge = bare.headers.get("www-authenticate", "")
        assert "Bearer" in challenge
        assert "resource_metadata=" in challenge

        # -- PRM document is served per tenant
        prm = client.get(
            "/.well-known/oauth-protected-resource/t/acme-dental/mcp")
        assert prm.status_code == 200, prm.text
        prm_body = prm.json()
        assert prm_body["resource"] == "http://mcp.test/t/acme-dental/mcp"
        assert prm_body["authorization_servers"] == [
            "https://tessera.local"
        ]
        assert "tools" in prm_body["scopes_supported"]

        # -- valid token
        token = _sign(
            {
                "iss": "https://tessera.local",
                "sub": "user-123",
                "aud": "http://mcp.test/t/acme-dental/mcp",
                "exp": 4_000_000_000,
                "iat": 1_700_000_000,
                "scope": "openid offline_access tools",
                "client_id": "https://claude.ai/api/mcp/auth_callback",
            },
            private_key,
        )
        ok = client.post(
            "/t/acme-dental/mcp",
            json={
                "jsonrpc": "2.0",
                "id": 1,
                "method": "tools/list",
                "params": {"_meta": MCP_META},
            },
            headers={**MCP_HEADERS, "Mcp-Method": "tools/list",
                     "Authorization": f"Bearer {token}"},
        )
        assert ok.status_code == 200, ok.text
        names = [t["name"] for t in ok.json()["result"]["tools"]]
        assert "book_appointment" in names


def test_oauth_mode_rejects_wrong_audience_and_missing_scope(
    acme_settings, fake_platform, oauth_keys
):
    private_key, jwk = oauth_keys
    gateway = _make_oauth_gateway(acme_settings, fake_platform, jwk)

    with TestClient(gateway.build_app()) as client:
        base = {
            "iss": "https://tessera.local",
            "sub": "user-123",
            "exp": 4_000_000_000,
            "iat": 1_700_000_000,
            "client_id": "test-client",
        }

        # Wrong audience (another tenant) -> 401
        wrong_aud = _sign(
            {**base, "aud": "http://mcp.test/t/other/mcp",
             "scope": "tools"},
            private_key,
        )
        r1 = client.post(
            "/t/acme-dental/mcp",
            json={
                "jsonrpc": "2.0",
                "id": 1,
                "method": "tools/list",
                "params": {"_meta": MCP_META},
            },
            headers={**MCP_HEADERS, "Mcp-Method": "tools/list",
                     "Authorization": f"Bearer {wrong_aud}"},
        )
        assert r1.status_code == 401, r1.text

        # Missing the tools scope -> 403 insufficient_scope
        no_scope = _sign(
            {**base, "aud": "http://mcp.test/t/acme-dental/mcp",
             "scope": "openid"},
            private_key,
        )
        r2 = client.post(
            "/t/acme-dental/mcp",
            json={
                "jsonrpc": "2.0",
                "id": 1,
                "method": "tools/list",
                "params": {"_meta": MCP_META},
            },
            headers={**MCP_HEADERS, "Mcp-Method": "tools/list",
                     "Authorization": f"Bearer {no_scope}"},
        )
        assert r2.status_code == 403, r2.text

        # Garbage token -> 401
        r3 = client.post(
            "/t/acme-dental/mcp",
            json={
                "jsonrpc": "2.0",
                "id": 1,
                "method": "tools/list",
                "params": {"_meta": MCP_META},
            },
            headers={**MCP_HEADERS, "Mcp-Method": "tools/list",
                     "Authorization": "Bearer garbage"},
        )
        assert r3.status_code == 401, r3.text