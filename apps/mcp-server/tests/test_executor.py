"""Tests for the HTTP executor: templating, credentials, response mapping."""

from __future__ import annotations

import httpx2
import pytest

from stub_backend.app import app as stub_app

from tessera_mcp.config import Settings
from tessera_mcp.executors.http_executor import HttpExecutor, ExecutionError
from tessera_mcp.manifest.loader import ManifestLoader

from tests.conftest import SAMPLE_MANIFESTS


def build_fake_platform(catalogs: dict):
    from starlette.applications import Starlette
    from starlette.requests import Request
    from starlette.responses import JSONResponse
    from starlette.routing import Route

    async def manifests(request: Request) -> JSONResponse:
        api_key = request.headers.get("X-Gateway-Api-Key")
        if api_key != "test-gateway-key":
            return JSONResponse({"error": "unauthorized"}, status_code=401)
        tenant = request.query_params.get("tenant")
        catalog = catalogs.get(tenant)
        if catalog is None:
            return JSONResponse({"error": "not found"}, status_code=404)
        return JSONResponse(catalog)

    return Starlette(
        routes=[
            Route("/internal/gateway/manifests", manifests, methods=["GET"]),
        ]
    )


@pytest.fixture
def executor() -> HttpExecutor:
    settings = Settings(
        platform_api_url="http://platform",
        gateway_api_key="test-gateway-key",
        mcp_base_url="http://mcp.test",
        issuer_url="https://tessera.local",
        auth_mode="none",
        smb_host_overrides={"api.acmedental.test": "http://api.acmedental.test"},
        credentials={"acme-dental/booking-api-key": "dev-acme-api-key-123"},
    )
    return HttpExecutor(
        resolve_credential=settings.resolve_credential,
        resolve_backend_url=settings.resolve_backend_url,
        client=httpx2.AsyncClient(
            transport=httpx2.ASGITransport(app=stub_app),
            base_url="http://api.acmedental.test",
        ),
        timeout=10.0,
    )


@pytest.fixture
def loader() -> ManifestLoader:
    platform = build_fake_platform(SAMPLE_MANIFESTS)
    return ManifestLoader(
        platform_api_url="http://platform",
        api_key="test-gateway-key",
        ttl_seconds=60,
        client=httpx2.AsyncClient(
            transport=httpx2.ASGITransport(app=platform),
            base_url="http://platform",
        ),
    )


@pytest.mark.asyncio
async def test_book_appointment_renders_template_and_maps_response(
    executor: HttpExecutor, loader: ManifestLoader
):
    manifest = await loader.load("acme-dental")
    tool = manifest.tools[0]

    result = await executor.execute(
        tool.execution,
        {
            "patient_name": "Sam Taylor",
            "date": "2026-12-01",
            "time": "10:00",
            "service_type": "checkup",
        },
    )

    assert result["patient"] == "Sam Taylor"
    assert result["preferred_time"] == "2026-12-01T10:00:00Z"
    assert result["service"] == "checkup"
    # credential was sent as the X-API-Key header by the stub
    assert result["authenticated_via"] == "dev-acme-api-key-123"


@pytest.mark.asyncio
async def test_list_appointments_maps_array(executor: HttpExecutor, loader: ManifestLoader):
    manifest = await loader.load("acme-dental")
    list_tool = manifest.tools[1]

    result = await executor.execute(
        list_tool.execution, {"patient_name": "Sam Taylor"})

    assert isinstance(result, list)


@pytest.mark.asyncio
async def test_missing_required_argument_raises(executor: HttpExecutor, loader: ManifestLoader):
    manifest = await loader.load("acme-dental")
    tool = manifest.tools[0]

    with pytest.raises(ExecutionError) as exc_info:
        await executor.execute(tool.execution, {})
    assert "Missing required argument" in str(exc_info.value)


@pytest.mark.asyncio
async def test_backend_error_surfaces(executor: HttpExecutor, loader: ManifestLoader):
    manifest = await loader.load("acme-dental")
    tool = manifest.tools[0]

    # Book with an empty patient name -> stub rejects with 400
    with pytest.raises(ExecutionError) as exc_info:
        await executor.execute(
            tool.execution,
            {"patient_name": "", "date": "2026-12-01", "time": "10:00",
             "service_type": "checkup"},
        )
    assert "400" in str(exc_info.value)