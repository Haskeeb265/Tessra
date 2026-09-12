"""The manifest-driven, tenant-aware MCP gateway.

One gateway serves every tenant (docs/mcp/README.md §2.1). Each tenant gets
its own low-level MCP :class:`Server` instance (so tools/list and tools/call
are scoped to that tenant's manifest) exposed at
``/t/{tenant-slug}/mcp``. With OAuth enabled, each tenant server is also an
OAuth 2.1 resource server: it serves the RFC 9728 protected-resource
metadata for its own resource URL and validates bearer tokens against the
C# authorization server's JWKS.

The gateway holds no business logic and never touches the database — it is
a protocol adapter over the platform's manifest endpoint (§2.3).
"""

from __future__ import annotations

import asyncio
import json
from collections import OrderedDict
from collections.abc import Callable, Mapping
from typing import Any

from mcp.server.auth.settings import AuthSettings
from mcp.server.context import ServerRequestContext
from mcp.server.lowlevel import Server
from mcp.types import (
    INVALID_REQUEST,
    CallToolRequestParams,
    CallToolResult,
    ListToolsResult,
    PaginatedRequestParams,
    TextContent,
    Tool,
)
from mcp.server.transport_security import TransportSecuritySettings
from mcp.shared.exceptions import MCPError
from starlette.applications import Starlette
from starlette.responses import Response
from starlette.routing import Route

from tessera_mcp.auth.token_verifier import TokenVerifier
from tessera_mcp.config import Settings
from tessera_mcp.executors.http_executor import ExecutionError, HttpExecutor
from tessera_mcp.manifest.loader import ManifestLoader

SERVER_NAME = "tessera-mcp"
SERVER_VERSION = "0.1.0"


class TenantGateway:
    """Holds per-tenant MCP servers and serves the gateway ASGI app."""

    def __init__(
        self,
        settings: Settings,
        loader: ManifestLoader,
        executor: HttpExecutor,
        token_verifier_factory: "Callable[[str], Any] | None" = None,
    ) -> None:
        """`token_verifier_factory` maps a tenant resource URL to a
        `TokenVerifier` instance; defaults to the standard JWKS verifier.
        Overridable for tests (in-process JWKS) or alternative verifiers."""
        self._settings = settings
        self._loader = loader
        self._executor = executor
        self._token_verifier_factory = (
            token_verifier_factory or self._default_token_verifier)
        self._servers: "OrderedDict[str, _TenantEntry]" = OrderedDict()
        self._lock = asyncio.Lock()

    # ================================================================
    # ASGI app
    # ================================================================

    def build_app(self) -> Starlette:
        """The gateway ASGI app.

        Dispatch is implemented as a raw-ASGI wrapper (class instance)
        because the tenant apps are themselves ASGI apps that must receive
        the untouched (scope, receive, send) triple — Starlette's Route
        wraps plain functions into request handlers, which would break the
        forwarding.
        """
        return Starlette(
            routes=[
                Route("/health", _AsgiApp(self._dispatch), methods=["GET"]),
                Route(
                    "/t/{tenant}/mcp",
                    _AsgiApp(self._dispatch),
                    methods=["GET", "POST", "DELETE"],
                ),
                # RFC 9728 protected-resource metadata, served per tenant
                # (the path embeds the tenant, e.g.
                #  /.well-known/oauth-protected-resource/t/acme-dental/mcp).
                Route(
                    "/.well-known/oauth-protected-resource/{path:path}",
                    _AsgiApp(self._dispatch),
                    methods=["GET", "OPTIONS"],
                ),
            ],
            # Closes the loader/executor HTTP clients on shutdown.
            lifespan=self._lifespan,
        )

    async def _lifespan(self, app: Starlette):
        yield
        await self._loader.aclose()
        await self._executor.aclose()
        for entry in self._servers.values():
            if entry.task is not None and not entry.task.done():
                entry.task.cancel()

    # ================================================================
    # Raw ASGI dispatch
    # ================================================================

    async def _dispatch(self, scope, receive, send) -> None:
        path = scope.get("path", "")

        if path == "/health":
            await _respond_json({"status": "ok"}, 200, scope, receive, send)
            return

        if path.startswith("/t/") and path.endswith("/mcp"):
            tenant = path.split("/")[2]
            if not tenant:
                await _respond_json(
                    {"error": "This workspace does not exist."},
                    404, scope, receive, send,
                )
                return

            # Resolve the tenant up front: unknown/deleted workspaces get
            # a clean 404 instead of a JSON-RPC error mid-handshake.
            catalog = await self._loader.load(tenant)
            if catalog is None:
                await _respond_json(
                    {"error": "This workspace does not exist."},
                    404, scope, receive, send,
                )
                return

            app = await self._tenant_app(tenant)
            await app(scope, receive, send)
            return

        # RFC 9728 protected-resource metadata, served per tenant:
        # /.well-known/oauth-protected-resource/t/{tenant}/mcp
        prefix = "/.well-known/oauth-protected-resource/"
        if path.startswith(prefix):
            parts = path[len(prefix):].split("/")
            # Remaining path mirrors the MCP route: t/{tenant}/mcp
            tenant = parts[1] if len(parts) >= 2 and parts[0] == "t" else ""
            if not tenant:
                await _respond_json(
                    {"error": "Not found."}, 404, scope, receive, send)
                return
            app = await self._tenant_app(tenant)
            await app(scope, receive, send)
            return

        await _respond_json(
            {"error": "Not found."}, 404, scope, receive, send)

    # ================================================================
    # Per-tenant servers
    # ================================================================

    async def _tenant_app(self, tenant_slug: str) -> Starlette:
        entry = self._servers.get(tenant_slug)
        if entry is not None:
            self._servers.move_to_end(tenant_slug)
            await entry.ready.wait()
            return entry.app

        async with self._lock:
            entry = self._servers.get(tenant_slug)
            if entry is not None:
                self._servers.move_to_end(tenant_slug)
                await entry.ready.wait()
                return entry.app

            server, app = self._build_tenant_server(tenant_slug)
            entry = _TenantEntry(server, app)
            self._servers[tenant_slug] = entry
            self._evict_if_needed()

            # The session manager must have run() entered (once) before it
            # can serve requests; hold it open for the gateway's lifetime.
            entry.task = asyncio.create_task(
                _hold_manager(server.session_manager, entry.ready))

            await entry.ready.wait()
            return entry.app

    def _build_tenant_server(self, tenant_slug: str) -> tuple[Server, Starlette]:
        server = Server(
            SERVER_NAME,
            version=SERVER_VERSION,
            title=f"Tessera MCP — {tenant_slug}",
            on_list_tools=self._make_list_tools_handler(tenant_slug),
            on_call_tool=self._make_call_tool_handler(tenant_slug),
        )

        settings = self._settings
        resource_url = settings.tenant_resource_url(tenant_slug)

        kwargs: dict[str, Any] = {
            "streamable_http_path": f"/t/{tenant_slug}/mcp",
            "json_response": True,
            "stateless_http": True,
            # The public hostname: prevents the SDK from auto-enabling DNS
            # rebinding protection keyed to 127.0.0.1 (which would reject
            # requests forwarded by the Caddy proxy with Host: tessera.local).
            "host": _hostname_of(resource_url),
            # Tunneled/cloud deployment: the Host header will not be one of
            # the localhost values the SDK auto-protects against, so we
            # disable DNS rebinding protection to accept any Host header
            # (the AS/tenant/resource binding is enforced by the
            # TokenVerifier, not by Host header matching).
            "transport_security": TransportSecuritySettings(
                enable_dns_rebinding_protection=False,
            ),
        }

        if settings.auth_mode == "oauth":
            kwargs["auth"] = AuthSettings(
                issuer_url=settings.issuer_url,
                resource_server_url=resource_url,
                required_scopes=["tools"],
                # The TokenVerifier validates aud itself (per-tenant
                # resource binding); see auth/token_verifier.py.
                validate_token_resource=False,
            )
            kwargs["token_verifier"] = self._token_verifier_factory(
                resource_url)

        # Creates the StreamableHTTPSessionManager + Starlette app.
        app = server.streamable_http_app(**kwargs)
        return server, app

    def _default_token_verifier(self, resource_url: str) -> TokenVerifier:
        """Standard verifier: JWKS from the platform AS, aud-bound to the
        tenant's resource URL."""
        return TokenVerifier(
            issuer_url=self._settings.issuer_url,
            resource_url=resource_url,
            required_scopes=["tools"],
            jwks_url=self._settings.jwks_url,
        )

    def _evict_if_needed(self) -> None:
        while len(self._servers) > self._settings.max_tenant_servers:
            _slug, entry = self._servers.popitem(last=False)
            if entry.task is not None and not entry.task.done():
                entry.task.cancel()

    # ================================================================
    # MCP handlers (closures bound to a tenant)
    # ================================================================

    def _make_list_tools_handler(
        self, tenant_slug: str
    ) -> Any:
        async def handle_list_tools(
            ctx: ServerRequestContext,
            params: PaginatedRequestParams | None,
        ) -> ListToolsResult:
            catalog = await self._loader.load(tenant_slug)
            if catalog is None:
                raise MCPError(
                    INVALID_REQUEST,
                    "This workspace does not exist or is no longer active.")
            if catalog.suspended:
                raise MCPError(
                    INVALID_REQUEST,
                    "This workspace is suspended.")

            tools = [
                Tool(
                    name=tool.tool_name,
                    description=tool.description,
                    input_schema=tool.input_schema,
                )
                for tool in catalog.tools
            ]
            # Deterministic order (docs/mcp/README.md §7: clients cache the
            # tool catalog; deterministic order keeps caches stable).
            tools.sort(key=lambda t: t.name)
            return ListToolsResult(tools=tools)

        return handle_list_tools

    def _make_call_tool_handler(
        self, tenant_slug: str
    ) -> Any:
        async def handle_call_tool(
            ctx: ServerRequestContext,
            params: CallToolRequestParams,
        ) -> CallToolResult:
            catalog = await self._loader.load(tenant_slug)
            if catalog is None or catalog.suspended:
                return _error_result(
                    "This workspace does not exist or is no longer active.")

            manifest = next(
                (t for t in catalog.tools if t.tool_name == params.name),
                None,
            )
            if manifest is None:
                return _error_result(
                    f"Unknown tool: {params.name!r}. "
                    f"Available tools: "
                    f"{', '.join(t.tool_name for t in catalog.tools) or 'none'}.")

            try:
                result = await self._executor.execute(
                    manifest.execution, params.arguments or {})
            except ExecutionError as exc:
                return _error_result(str(exc))

            if isinstance(result, str):
                return CallToolResult(content=[TextContent(text=result)])
            return CallToolResult(
                content=[
                    TextContent(text=json.dumps(result, indent=2, default=str))
                ],
                structured_content=result,
            )

        return handle_call_tool


# ================================================================
# Helpers
# ================================================================


class _AsgiApp:
    """Wraps a raw-ASGI handler so Starlette's Route treats it as ASGI
    (functions/methods get wrapped into request handlers instead)."""

    __slots__ = ("_handler",)

    def __init__(self, handler) -> None:
        self._handler = handler

    async def __call__(self, scope, receive, send) -> None:
        await self._handler(scope, receive, send)


async def _respond_json(
    body: dict,
    status_code: int,
    scope,
    receive,
    send,
) -> None:
    payload = json.dumps(body).encode()
    response = Response(
        payload, status_code=status_code, media_type="application/json")
    await response(scope, receive, send)


class _TenantEntry:
    __slots__ = ("server", "app", "task", "ready")

    def __init__(self, server: Server, app: Starlette) -> None:
        self.server = server
        self.app = app
        self.task: asyncio.Task | None = None
        self.ready: asyncio.Event = asyncio.Event()


async def _hold_manager(manager: Any, ready: asyncio.Event) -> None:
    try:
        async with manager.run():
            ready.set()
            await asyncio.Event().wait()
    except asyncio.CancelledError:
        raise


def _tenant_from_path(path: str) -> str:
    parts = [p for p in path.split("/") if p]
    # ["t", "{tenant}", "mcp"]
    if len(parts) >= 3 and parts[0] == "t" and parts[2] == "mcp":
        return parts[1]
    return ""


def _hostname_of(url: str) -> str:
    from urllib.parse import urlsplit

    return urlsplit(url).hostname or "localhost"


def _error_result(message: str) -> CallToolResult:
    return CallToolResult(
        content=[TextContent(text=message)],
        is_error=True,
    )