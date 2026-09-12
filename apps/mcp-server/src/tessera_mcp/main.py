"""Gateway entrypoint.

Run locally:
    uv run uvicorn tessera_mcp.main:app --host 0.0.0.0 --port 8000
(or `uv run python -m tessera_mcp.main`)

Configuration is environment-driven — see tessera_mcp/config.py and
docs/mcp/README.md §11.
"""

from __future__ import annotations

import os

import uvicorn

from tessera_mcp.config import load_settings
from tessera_mcp.core.gateway import TenantGateway
from tessera_mcp.executors.http_executor import HttpExecutor
from tessera_mcp.manifest.loader import ManifestLoader


def build_gateway() -> tuple[TenantGateway, object]:
    settings = load_settings()

    loader = ManifestLoader(
        platform_api_url=settings.platform_api_url,
        api_key=settings.gateway_api_key,
        ttl_seconds=settings.manifest_ttl_seconds,
    )

    executor = HttpExecutor(
        resolve_credential=settings.resolve_credential,
        resolve_backend_url=settings.resolve_backend_url,
        timeout=settings.executor_timeout_seconds,
    )

    gateway = TenantGateway(settings, loader, executor)
    return gateway, gateway.build_app()


_gateway, app = build_gateway()


def main() -> None:
    uvicorn.run(
        app,
        host=os.environ.get("HOST", "0.0.0.0"),
        port=int(os.environ.get("PORT", "8000")),
    )


if __name__ == "__main__":
    main()