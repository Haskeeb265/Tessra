"""Gateway configuration, read from environment variables.

The gateway is a thin protocol adapter (docs/mcp/README.md §2.3): all state
lives in the C# platform; this file only wires up how to reach it.
"""

from __future__ import annotations

import json
import os
from dataclasses import dataclass, field


def _json_dict(name: str, default: dict[str, str]) -> dict[str, str]:
    raw = os.environ.get(name)
    if not raw:
        return default
    try:
        parsed = json.loads(raw)
    except json.JSONDecodeError:
        raise ValueError(f"{name} must be a JSON object")
    if not isinstance(parsed, dict):
        raise ValueError(f"{name} must be a JSON object")
    return {str(k): str(v) for k, v in parsed.items()}


@dataclass(frozen=True)
class Settings:
    """Gateway settings. All fields have local-dev defaults so the gateway
    runs out of the box against a local platform (see docs/mcp/README.md §13)."""

    # --- Platform connectivity (server-to-server) ---
    platform_api_url: str = field(
        default_factory=lambda: os.environ.get(
            "PLATFORM_API_URL", "http://localhost:5000"))
    gateway_api_key: str = field(
        default_factory=lambda: os.environ.get(
            "GATEWAY_API_KEY", "dev-gateway-key"))

    # --- Public addressing ---
    # The externally visible base URL of this gateway. Per-tenant MCP
    # resources are {mcp_base_url}/t/{slug}/mcp — this is the RFC 8707
    # resource the OAuth tokens are bound to, so it must match what clients
    # connect to (through Caddy: https://tessera.local).
    mcp_base_url: str = field(
        default_factory=lambda: os.environ.get(
            "MCP_BASE_URL", "http://localhost:8000"))

    # --- OAuth / authorization server ---
    issuer_url: str = field(
        default_factory=lambda: os.environ.get(
            "ISSUER_URL", "https://tessera.local"))
    # "oauth" = require a valid bearer token on every MCP request;
    # "none"   = authless (loopback dev only).
    auth_mode: str = field(
        default_factory=lambda: os.environ.get("MCP_AUTH_MODE", "oauth").lower())

    # --- Manifest cache ---
    manifest_ttl_seconds: int = field(
        default_factory=lambda: int(
            os.environ.get("MANIFEST_TTL_SECONDS", "60")))

    # --- SMB backend routing (dev) ---
    # Map a manifest's target host to a local base URL so the fake
    # api.acmedental.test backend is reachable from this machine/container.
    smb_host_overrides: dict[str, str] = field(
        default_factory=lambda: _json_dict(
            "SMB_HOST_OVERRIDES",
            {"api.acmedental.test": "http://127.0.0.1:9100"}))

    # --- Credential resolution (dev) ---
    # vault://<tenant>/<ref> -> api key value. Production will resolve these
    # from the encrypted credential store on the platform (deferred, see
    # docs/mcp/README.md §2.7).
    credentials: dict[str, str] = field(
        default_factory=lambda: _json_dict(
            "CREDENTIALS",
            {"acme-dental/booking-api-key": "dev-acme-api-key-123"}))

    executor_timeout_seconds: float = field(
        default_factory=lambda: float(
            os.environ.get("EXECUTOR_TIMEOUT_SECONDS", "30")))

    # Max per-tenant Server instances held in memory (LRU).
    max_tenant_servers: int = field(
        default_factory=lambda: int(os.environ.get("MAX_TENANT_SERVERS", "100")))

    def tenant_resource_url(self, tenant_slug: str) -> str:
        base = self.mcp_base_url.rstrip("/")
        return f"{base}/t/{tenant_slug}/mcp"

    @property
    def jwks_url(self) -> str:
        """Where the gateway fetches the AS signing keys. Overridable so the
        dockerized gateway can fetch the API's JWKS directly (http) instead
        of through the TLS proxy."""
        return os.environ.get("JWKS_URL") or (
            f"{self.issuer_url.rstrip('/')}/.well-known/jwks")

    def resolve_backend_url(self, url: str) -> str:
        """Rewrite a manifest target URL through the host override map."""
        from urllib.parse import urlsplit, urlunsplit

        parts = urlsplit(url)
        override = self.smb_host_overrides.get(parts.netloc)
        if override is None:
            return url
        base = urlsplit(override.rstrip("/"))
        return urlunsplit(
            (base.scheme, base.netloc, parts.path, parts.query, parts.fragment))

    def resolve_credential(self, credential_ref: str) -> str | None:
        """Resolve a vault://tenant/ref reference to an api key value."""
        if credential_ref.startswith("vault://"):
            # Keys in the dev map are tenant/ref, without the scheme.
            credential_ref = credential_ref[len("vault://"):]
        return self.credentials.get(credential_ref)


def load_settings() -> Settings:
    return Settings()