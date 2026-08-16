"""TesseraClient — the MCP server's bridge to the C# platform API.

The C# platform is the single source of truth for auth, tenant context and
roles. This client only *consumes* it: it logs in as a tenant user, sends
the ``X-Tenant-Id`` header on every request (just like the Next.js portals),
and auto-refreshes the access token when the platform returns 401.

Contract (camelCase JSON, ASP.NET Core defaults):

* ``POST /auth/login``  {email, password}        -> {accessToken, refreshToken}
* ``POST /auth/refresh`` {refreshToken}           -> {accessToken, refreshToken}
* ``GET  /tenant/me``                             -> {id, email, role, actions, ...}
* ``GET  /widgets``                               -> [{id, name, description, ...}]

All requests require the ``X-Tenant-Id`` header. Login/refresh do not require
a bearer token; everything else does.
"""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from typing import Any, Final, cast

import httpx

DEFAULT_BASE_URL: Final = "http://localhost:5085"
TENANT_HEADER: Final = "X-Tenant-Id"


class TesseraError(RuntimeError):
    """Raised when the platform rejects a request (4xx/5xx or bad payload)."""


@dataclass
class TesseraClient:
    """Authenticated HTTP client for the Tessera platform API."""

    base_url: str
    tenant_id: str
    email: str
    password: str
    transport: httpx.AsyncBaseTransport | None = None

    _client: httpx.AsyncClient = field(init=False, repr=False)
    _access_token: str | None = field(default=None, init=False, repr=False)
    _refresh_token: str | None = field(default=None, init=False, repr=False)

    def __post_init__(self) -> None:
        self._client = httpx.AsyncClient(
            base_url=self.base_url.rstrip("/"),
            timeout=30.0,
            transport=self.transport,
        )

    # ─── Public API ────────────────────────────────────────────────────

    async def login(self) -> None:
        """Exchange credentials for an access/refresh token pair."""
        data = await self._post(
            "/auth/login", json={"email": self.email, "password": self.password}
        )
        self._store_tokens(data)

    async def whoami(self) -> dict[str, Any]:
        """Return the calling user's profile: role, allowed actions, tenant."""
        return cast(dict[str, Any], await self._get("/tenant/me"))

    async def list_widgets(self) -> list[dict[str, Any]]:
        """Return the tenant's widgets (placeholder resource from the platform)."""
        return cast(list[dict[str, Any]], await self._get("/widgets"))

    async def aclose(self) -> None:
        await self._client.aclose()

    # ─── Requests ──────────────────────────────────────────────────────

    async def _get(self, path: str) -> Any:
        response = await self._request("GET", path)
        return response.json()

    async def _post(self, path: str, *, json: dict[str, Any]) -> Any:
        response = await self._request("POST", path, json=json)
        return response.json()

    async def _request(self, method: str, path: str, **kwargs: Any) -> httpx.Response:
        """Send an authenticated request; refresh + retry once on 401."""
        response = await self._send(method, path, **kwargs)
        if response.status_code == 401 and self._refresh_token is not None:
            await self._refresh()
            response = await self._send(method, path, **kwargs)
        if response.is_error:
            raise TesseraError(f"{method} {path} -> {response.status_code}: {response.text}")
        return response

    async def _send(self, method: str, path: str, **kwargs: Any) -> httpx.Response:
        headers = kwargs.pop("headers", {})
        headers[TENANT_HEADER] = self.tenant_id
        if self._access_token is not None:
            headers["Authorization"] = f"Bearer {self._access_token}"
        return await self._client.request(method, path, headers=headers, **kwargs)

    async def _refresh(self) -> None:
        if self._refresh_token is None:
            raise TesseraError("Cannot refresh: no refresh token available.")
        response = await self._client.post(
            "/auth/refresh",
            headers={TENANT_HEADER: self.tenant_id},
            json={"refreshToken": self._refresh_token},
        )
        if response.is_error:
            raise TesseraError(f"POST /auth/refresh -> {response.status_code}: {response.text}")
        self._store_tokens(response.json())

    def _store_tokens(self, data: Any) -> None:
        access = data.get("accessToken") if isinstance(data, dict) else None
        refresh = data.get("refreshToken") if isinstance(data, dict) else None
        if not access or not refresh:
            raise TesseraError(f"Unexpected token payload: {data!r}")
        self._access_token = access
        self._refresh_token = refresh

    # ─── Construction ──────────────────────────────────────────────────

    @classmethod
    def from_env(cls) -> TesseraClient:
        """Build a client from environment variables (used by the MCP entry point).

        ``TESSERA_BASE_URL`` defaults to the local dev API. The tenant
        identifier + credentials identify *which* workspace user this MCP
        server acts on behalf of.
        """
        required = ("TESSERA_TENANT_ID", "TESSERA_EMAIL", "TESSERA_PASSWORD")
        missing = [name for name in required if not os.getenv(name)]
        if missing:
            raise TesseraError(
                f"Missing required environment variables: {', '.join(missing)}"
            )
        return cls(
            base_url=os.getenv("TESSERA_BASE_URL", DEFAULT_BASE_URL),
            tenant_id=os.environ["TESSERA_TENANT_ID"],
            email=os.environ["TESSERA_EMAIL"],
            password=os.environ["TESSERA_PASSWORD"],
        )
