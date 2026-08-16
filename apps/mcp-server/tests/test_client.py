"""Unit tests for TesseraClient — all HTTP is mocked via httpx.MockTransport."""

from __future__ import annotations

import json
from collections.abc import Callable

import httpx
import pytest

from tessera_mcp.client import TesseraClient, TesseraError

LOGIN_BODY = {"accessToken": "ACC-1", "refreshToken": "REF-1"}


def make_client(handler: Callable[[httpx.Request], httpx.Response]) -> TesseraClient:
    return TesseraClient(
        base_url="http://platform.test",
        tenant_id="alpha-corp",
        email="admin@tessera.com",
        password="Admin123!",
        transport=httpx.MockTransport(handler),
    )


async def test_login_sends_tenant_header_and_credentials() -> None:
    seen: list[httpx.Request] = []

    def handler(request: httpx.Request) -> httpx.Response:
        seen.append(request)
        assert request.url.path == "/auth/login"
        assert request.headers["X-Tenant-Id"] == "alpha-corp"
        assert request.headers.get("Authorization") is None  # login is unauthenticated
        payload = json.loads(request.content)
        assert payload == {"email": "admin@tessera.com", "password": "Admin123!"}
        return httpx.Response(200, json=LOGIN_BODY)

    client = make_client(handler)
    await client.login()
    assert client._access_token == "ACC-1"
    assert client._refresh_token == "REF-1"
    assert len(seen) == 1
    await client.aclose()


async def test_whoami_sends_bearer_token() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path == "/auth/login":
            return httpx.Response(200, json=LOGIN_BODY)
        assert request.url.path == "/tenant/me"
        assert request.headers["Authorization"] == "Bearer ACC-1"
        assert request.headers["X-Tenant-Id"] == "alpha-corp"
        return httpx.Response(
            200,
            json={
                "id": "u-1",
                "email": "admin@tessera.com",
                "role": "Admin",
                "actions": ["create_mcp", "add_tools"],
                "tenantId": "alpha",
                "tenantIdentifier": "alpha-corp",
            },
        )

    client = make_client(handler)
    await client.login()
    me = await client.whoami()
    assert me["role"] == "Admin"
    assert "create_mcp" in me["actions"]
    assert me["tenantIdentifier"] == "alpha-corp"
    await client.aclose()


async def test_401_triggers_single_refresh_and_retry() -> None:
    calls: list[httpx.Request] = []

    def handler(request: httpx.Request) -> httpx.Response:
        calls.append(request)
        if request.url.path == "/auth/login":
            return httpx.Response(200, json=LOGIN_BODY)
        if request.url.path == "/auth/refresh":
            return httpx.Response(
                200, json={"accessToken": "ACC-2", "refreshToken": "REF-2"}
            )
        if request.url.path == "/widgets":
            if request.headers.get("Authorization") == "Bearer ACC-1":
                return httpx.Response(401, json={})
            assert request.headers["Authorization"] == "Bearer ACC-2"
            return httpx.Response(200, json=[{"id": "w-1", "name": "Widget A"}])
        raise AssertionError(f"unexpected path {request.url.path}")

    client = make_client(handler)
    await client.login()
    widgets = await client.list_widgets()
    assert widgets[0]["name"] == "Widget A"
    assert [r.url.path for r in calls] == [
        "/auth/login",
        "/widgets",
        "/auth/refresh",
        "/widgets",
    ]
    await client.aclose()


async def test_refresh_failure_raises() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path == "/auth/login":
            return httpx.Response(200, json=LOGIN_BODY)
        if request.url.path == "/auth/refresh":
            return httpx.Response(401, json={})
        return httpx.Response(401, json={})

    client = make_client(handler)
    await client.login()
    with pytest.raises(TesseraError, match="refresh"):
        await client.whoami()
    await client.aclose()


async def test_platform_error_raises_tessera_error() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(403, json={"error": "forbidden"})

    client = make_client(handler)
    with pytest.raises(TesseraError, match="403"):
        await client.whoami()
    await client.aclose()


async def test_malformed_token_payload_raises() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"unexpected": True})

    client = make_client(handler)
    with pytest.raises(TesseraError, match="token payload"):
        await client.login()
    await client.aclose()


def test_from_env_requires_credentials(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("TESSERA_TENANT_ID", raising=False)
    monkeypatch.delenv("TESSERA_EMAIL", raising=False)
    monkeypatch.delenv("TESSERA_PASSWORD", raising=False)
    monkeypatch.setenv("TESSERA_BASE_URL", "http://example.test")
    with pytest.raises(TesseraError, match="TESSERA_TENANT_ID"):
        TesseraClient.from_env()


def test_from_env_defaults_base_url(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("TESSERA_TENANT_ID", "alpha-corp")
    monkeypatch.setenv("TESSERA_EMAIL", "a@b.test")
    monkeypatch.setenv("TESSERA_PASSWORD", "secret1")
    client = TesseraClient.from_env()
    assert client.base_url == "http://localhost:5085"
    assert client.tenant_id == "alpha-corp"
