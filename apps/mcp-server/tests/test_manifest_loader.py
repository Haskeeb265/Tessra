"""Tests for the manifest loader (platform -> gateway manifest fetching)."""

from __future__ import annotations

import asyncio

import httpx2
import pytest

from tessera_mcp.manifest.models import ExecutionConfig, ToolManifest
from tessera_mcp.manifest.loader import ManifestLoader, ManifestSourceError

from tests.conftest import SAMPLE_MANIFESTS, build_fake_platform


@pytest.mark.asyncio
async def test_loads_and_parses_manifest(manifest_loader: ManifestLoader):
    manifest = await manifest_loader.load("acme-dental")

    assert manifest is not None
    assert manifest.tenant_id == "acme-dental"
    assert manifest.tenant_name == "Acme Dental"
    assert len(manifest.tools) == 3

    book = manifest.tools[0]
    assert book.tool_name == "book_appointment"
    assert isinstance(book.execution, ExecutionConfig)
    assert book.execution.type == "http"
    assert book.execution.method == "POST"
    assert "v1/appointments" in book.execution.url
    assert book.execution.auth.credential_ref == "vault://acme-dental/booking-api-key"
    assert book.execution.response_mapping == "$.data.appointment"
    assert book.required_scopes == ["appointments:write"]


@pytest.mark.asyncio
async def test_manifest_is_cached_until_ttl(manifest_loader: ManifestLoader):
    first = await manifest_loader.load("acme-dental")
    second = await manifest_loader.load("acme-dental")

    # Same object identity => served from cache without a second fetch
    assert first is second


@pytest.mark.asyncio
async def test_cache_expires_after_ttl(manifest_loader: ManifestLoader):
    manifest_loader._ttl = 0.05

    first = await manifest_loader.load("acme-dental")
    await asyncio.sleep(0.06)
    second = await manifest_loader.load("acme-dental")

    assert first is not second
    assert second is not None
    assert second.tenant_id == "acme-dental"


@pytest.mark.asyncio
async def test_unknown_tenant_returns_none(manifest_loader: ManifestLoader):
    assert await manifest_loader.load("no-such-co") is None


@pytest.mark.asyncio
async def test_suspended_tenant_returns_suspended_catalog():
    platform = build_fake_platform(
        {
            "acme-dental": {
                **SAMPLE_MANIFESTS["acme-dental"],
                "status": "Suspended",
            }
        }
    )
    loader = ManifestLoader(
        platform_api_url="http://platform",
        api_key="test-gateway-key",
        ttl_seconds=60,
        client=httpx2.AsyncClient(
            transport=httpx2.ASGITransport(app=platform),
            base_url="http://platform",
        ),
    )

    catalog = await loader.load("acme-dental")
    assert catalog is not None
    assert catalog.suspended is True


@pytest.mark.asyncio
async def test_wrong_api_key_raises_source_error(fake_platform):
    loader = ManifestLoader(
        platform_api_url="http://platform",
        api_key="wrong-key",
        ttl_seconds=60,
        client=httpx2.AsyncClient(
            transport=httpx2.ASGITransport(app=fake_platform),
            base_url="http://platform",
        ),
    )

    with pytest.raises(ManifestSourceError):
        await loader.load("acme-dental")


def test_tool_manifest_requires_http_execution_type():
    with pytest.raises(ValueError):
        ToolManifest(
            tool_name="x",
            description="x",
            input_schema={"type": "object"},
            execution=ExecutionConfig(type="ssh", method="GET", url="http://x"),
        )