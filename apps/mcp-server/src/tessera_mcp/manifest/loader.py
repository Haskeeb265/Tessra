"""Manifest loader: fetch tenant catalogs from the C# platform and cache them.

The gateway must never touch the database (docs/mcp/README.md §2.3); it
reads manifests over the platform's server-to-server endpoint and caches
with a TTL so a platform hiccup degrades to cached reads, not failure.
Unknown and suspended tenants are cached as sentinels too, so a deleted
tenant stops resolving immediately.
"""

from __future__ import annotations

import time
from typing import Any

import httpx2

from .models import TenantCatalog

_UNKNOWN = "__unknown__"
_SUSPENDED = "__suspended__"


class ManifestLoader:
    """TTL cache in front of the platform's gateway manifest endpoint."""

    def __init__(
        self,
        platform_api_url: str,
        api_key: str,
        ttl_seconds: int = 60,
        timeout: float = 10.0,
        client: httpx2.AsyncClient | None = None,
    ) -> None:
        self._base = platform_api_url.rstrip("/")
        self._api_key = api_key
        self._ttl = ttl_seconds
        self._client = client or httpx2.AsyncClient(
            timeout=httpx2.Timeout(timeout))
        # tenant_slug -> (catalog_or_sentinel, fetched_at)
        self._cache: dict[str, tuple[Any, float]] = {}

    async def aclose(self) -> None:
        await self._client.aclose()

    def _cache_get(self, slug: str) -> Any | None:
        entry = self._cache.get(slug)
        if entry is None:
            return None
        value, fetched_at = entry
        if time.monotonic() - fetched_at > self._ttl:
            del self._cache[slug]
            return None
        return value

    async def load(self, tenant_slug: str) -> TenantCatalog | None:
        """Return the tenant's catalog, or None when unknown/deleted.

        Raises ManifestSourceError when the platform is unreachable and no
        cached copy exists (callers may decide to 503).
        """
        cached = self._cache_get(tenant_slug)
        if cached is _SUSPENDED:
            return TenantCatalog(
                tenant_id=tenant_slug, status="Suspended", tools=[])
        if cached is not None and cached is not _UNKNOWN:
            return cached

        catalog = await self._fetch(tenant_slug)

        # Sentinels: unknown tenants are cached briefly so a deleted tenant
        # stops resolving without hammering the platform.
        if catalog is None:
            self._cache[tenant_slug] = (_UNKNOWN, time.monotonic())
            return None
        if catalog.suspended:
            self._cache[tenant_slug] = (_SUSPENDED, time.monotonic())
            return catalog

        self._cache[tenant_slug] = (catalog, time.monotonic())
        return catalog

    async def _fetch(self, tenant_slug: str) -> TenantCatalog | None:
        url = f"{self._base}/internal/gateway/manifests"
        try:
            response = await self._client.get(
                url,
                params={"tenant": tenant_slug},
                headers={
                    "X-Gateway-Api-Key": self._api_key,
                    "Accept": "application/json",
                },
            )
        except httpx2.HTTPError as exc:
            raise ManifestSourceError(
                f"Platform unreachable while loading manifest for "
                f"{tenant_slug}: {exc}") from exc

        if response.status_code == 404:
            return None
        if response.status_code == 403:
            return TenantCatalog(
                tenant_id=tenant_slug, status="Suspended", tools=[])
        if response.status_code == 401:
            raise ManifestSourceError(
                "Platform rejected the gateway API key (401).")
        if response.status_code >= 400:
            # Covers 400/405/etc — e.g. a middleware rejection payload. Fail
            # loudly with the platform's own message instead of a cryptic
            # pydantic error, and never cache a failure payload.
            raise ManifestSourceError(
                f"Platform returned {response.status_code} for tenant "
                f"{tenant_slug}: {response.text[:300]}")

        content_type = response.headers.get("content-type", "")
        if "application/json" not in content_type:
            raise ManifestSourceError(
                f"Platform returned non-JSON ({content_type or 'unknown'}) "
                f"for tenant {tenant_slug}.")

        try:
            payload = response.json()
        except ValueError as exc:
            raise ManifestSourceError(
                f"Platform returned non-JSON for tenant {tenant_slug}."
            ) from exc

        return TenantCatalog.model_validate(payload)


class ManifestSourceError(Exception):
    """The platform manifest source could not be read."""