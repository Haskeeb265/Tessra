"""Shared test fixtures.

- FakePlatform: a minimal stand-in for the C# platform's
  /internal/gateway/* endpoints (server-to-server manifest reads).
- The real stub_backend app for executor tests.
- Sample manifests mirroring docs/sample-smb/acme-dental-manifest.json.
"""

from __future__ import annotations

import pytest
import httpx2
from starlette.applications import Starlette
from starlette.requests import Request
from starlette.responses import JSONResponse
from starlette.routing import Route

from stub_backend.app import app as stub_app


SAMPLE_MANIFESTS = {
    "acme-dental": {
        "tenant_id": "acme-dental",
        "tenant_name": "Acme Dental",
        "status": "Active",
        "tools": [
            {
                "tool_name": "book_appointment",
                "description": (
                    "Books a dental appointment for a given date/time and "
                    "service type."),
                "input_schema": {
                    "type": "object",
                    "properties": {
                        "patient_name": {"type": "string"},
                        "date": {"type": "string", "format": "date"},
                        "time": {
                            "type": "string",
                            "pattern": "^([01]\\d|2[0-3]):[0-5]\\d$",
                        },
                        "service_type": {
                            "type": "string",
                            "enum": [
                                "cleaning", "checkup", "whitening",
                                "fillings",
                            ],
                        },
                    },
                    "required": [
                        "patient_name", "date", "time", "service_type",
                    ],
                },
                "execution": {
                    "type": "http",
                    "method": "POST",
                    "url": "https://api.acmedental.test/v1/appointments",
                    "auth": {
                        "type": "api_key",
                        "credential_ref": "vault://acme-dental/booking-api-key",
                    },
                    "body_template": {
                        "patient": "${patient_name}",
                        "preferred_time": "${date}T${time}:00Z",
                        "service": "${service_type}",
                    },
                    "response_mapping": "$.data.appointment",
                },
                "required_scopes": ["appointments:write"],
            },
            {
                "tool_name": "list_appointments",
                "description": "Lists the current patient's upcoming appointments.",
                "input_schema": {
                    "type": "object",
                    "properties": {"patient_name": {"type": "string"}},
                    "required": ["patient_name"],
                },
                "execution": {
                    "type": "http",
                    "method": "GET",
                    "url": "https://api.acmedental.test/v1/appointments",
                    "auth": {
                        "type": "api_key",
                        "credential_ref": "vault://acme-dental/booking-api-key",
                    },
                    "query_template": {"patient": "${patient_name}"},
                    "response_mapping": "$.data.appointments",
                },
                "required_scopes": ["appointments:read"],
            },
            {
                "tool_name": "cancel_appointment",
                "description": "Cancels an existing appointment by its ID.",
                "input_schema": {
                    "type": "object",
                    "properties": {"appointment_id": {"type": "string"}},
                    "required": ["appointment_id"],
                },
                "execution": {
                    "type": "http",
                    "method": "DELETE",
                    "url": (
                        "https://api.acmedental.test/v1/appointments/"
                        "${appointment_id}"),
                    "auth": {
                        "type": "api_key",
                        "credential_ref": "vault://acme-dental/booking-api-key",
                    },
                    "response_mapping": "$.data.cancellation",
                },
                "required_scopes": ["appointments:write"],
            },
        ],
    },
    "beta-dental": {
        "tenant_id": "beta-dental",
        "tenant_name": "Beta Dental",
        "status": "Active",
        "tools": [
            {
                "tool_name": "ping",
                "description": "Returns a greeting.",
                "input_schema": {
                    "type": "object",
                    "properties": {"name": {"type": "string"}},
                },
                "execution": {
                    "type": "http",
                    "method": "GET",
                    "url": "https://api.acmedental.test/v1/ping",
                    "response_mapping": "$.message",
                },
                "required_scopes": ["tools"],
            },
        ],
    },
}


def build_fake_platform(catalogs: dict) -> Starlette:
    async def manifests(request: Request) -> JSONResponse:
        api_key = request.headers.get("X-Gateway-Api-Key")
        if api_key != "test-gateway-key":
            return JSONResponse({"error": "unauthorized"}, status_code=401)

        tenant = request.query_params.get("tenant")
        catalog = catalogs.get(tenant)

        if catalog is None:
            return JSONResponse({"error": "not found"}, status_code=404)

        if catalog.get("status") == "Suspended":
            return JSONResponse({"error": "suspended"}, status_code=403)

        return JSONResponse(catalog)

    return Starlette(
        routes=[
            Route("/internal/gateway/manifests", manifests, methods=["GET"]),
        ]
    )


@pytest.fixture
def fake_platform() -> Starlette:
    return build_fake_platform(SAMPLE_MANIFESTS)


@pytest.fixture
def stub_client() -> httpx2.AsyncClient:
    """httpx2 client wired to the real stub backend app (no sockets)."""
    return httpx2.AsyncClient(
        transport=httpx2.ASGITransport(app=stub_app),
        base_url="http://api.acmedental.test",
    )


@pytest.fixture
def manifest_loader(fake_platform: Starlette):
    from tessera_mcp.manifest.loader import ManifestLoader

    client = httpx2.AsyncClient(
        transport=httpx2.ASGITransport(app=fake_platform),
        base_url="http://platform",
    )
    return ManifestLoader(
        platform_api_url="http://platform",
        api_key="test-gateway-key",
        ttl_seconds=60,
        client=client,
    )


@pytest.fixture
def acme_settings():
    from tessera_mcp.config import Settings

    return Settings(
        platform_api_url="http://platform",
        gateway_api_key="test-gateway-key",
        mcp_base_url="http://mcp.test",
        issuer_url="https://tessera.local",
        auth_mode="none",
        smb_host_overrides={"api.acmedental.test": "http://api.acmedental.test"},
        credentials={"acme-dental/booking-api-key": "dev-acme-api-key-123"},
    )