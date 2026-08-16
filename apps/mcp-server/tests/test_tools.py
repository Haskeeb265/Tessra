"""Unit tests for the tool functions in tools.py (mocked platform HTTP)."""

from __future__ import annotations

from collections.abc import Callable

import httpx
import pytest

from tessera_mcp.client import TesseraClient, TesseraError
from tessera_mcp.tools import list_widgets, whoami

LOGIN_BODY = {"accessToken": "ACC-1", "refreshToken": "REF-1"}


def make_client(handler: Callable[[httpx.Request], httpx.Response]) -> TesseraClient:
    return TesseraClient(
        base_url="http://platform.test",
        tenant_id="alpha-corp",
        email="admin@tessera.com",
        password="Admin123!",
        transport=httpx.MockTransport(handler),
    )


async def test_whoami_returns_profile_json() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path == "/auth/login":
            return httpx.Response(200, json=LOGIN_BODY)
        if request.url.path == "/tenant/me":
            return httpx.Response(
                200,
                json={
                    "id": "u-1",
                    "email": "admin@tessera.com",
                    "role": "Manager",
                    "actions": ["create_widget", "edit_widget"],
                    "tenantId": "alpha",
                    "tenantIdentifier": "alpha-corp",
                },
            )
        raise AssertionError(f"unexpected path {request.url.path}")

    client = make_client(handler)
    result = await whoami(client)
    assert '"role": "Manager"' in result
    assert '"create_widget"' in result
    assert '"alpha-corp"' in result
    await client.aclose()


async def test_list_widgets_returns_widgets_json() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path == "/auth/login":
            return httpx.Response(200, json=LOGIN_BODY)
        if request.url.path == "/widgets":
            return httpx.Response(
                200,
                json=[{"id": "w-1", "name": "Widget A", "description": "First"}],
            )
        raise AssertionError(f"unexpected path {request.url.path}")

    client = make_client(handler)
    result = await list_widgets(client)
    assert '"name": "Widget A"' in result
    await client.aclose()


async def test_tool_propagates_platform_errors() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(403, json={"error": "not allowed"})

    client = make_client(handler)
    with pytest.raises(TesseraError, match="403"):
        await whoami(client)
    await client.aclose()
