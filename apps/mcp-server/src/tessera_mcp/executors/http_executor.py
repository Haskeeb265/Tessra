"""Generic HTTP executor: turns a manifest's `execution` config + tool
arguments into a real HTTP call against the SMB backend.

v1 supports:
- path/body/query templates with ``${arg}`` substitution
- api_key auth resolved from the credential source (dev: local map;
  production: platform vault)
- a `response_mapping` JSONPath applied to the JSON response
- host overrides so the fake api.acmedental.test resolves locally
"""

from __future__ import annotations

import json
import re
from typing import Any, Callable

import httpx2

from tessera_mcp.manifest.models import ExecutionConfig
from tessera_mcp.utils.jsonpath import JsonPathError, evaluate

_TEMPLATE = re.compile(r"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")

CredentialResolver = Callable[[str], str | None]


class ExecutionError(Exception):
    """A tool call failed; the message is surfaced to the AI client."""


class HttpExecutor:
    def __init__(
        self,
        resolve_credential: CredentialResolver,
        resolve_backend_url: Callable[[str], str] | None = None,
        client: httpx2.AsyncClient | None = None,
        timeout: float = 30.0,
    ) -> None:
        self._resolve_credential = resolve_credential
        self._resolve_backend_url = resolve_backend_url or (lambda url: url)
        self._client = client or httpx2.AsyncClient(
            timeout=httpx2.Timeout(timeout, connect=10.0))

    async def aclose(self) -> None:
        await self._client.aclose()

    async def execute(
        self,
        execution: ExecutionConfig,
        arguments: dict[str, Any],
    ) -> Any:
        """Execute the tool; returns the mapped response value.

        Raises ExecutionError on any failure (HTTP errors, bad templates,
        missing credentials, mapping misses).
        """
        if execution.type != "http":
            raise ExecutionError(
                f"Unsupported execution type: {execution.type!r} "
                "(v1 supports 'http' only).")

        url = self._render(execution.url, arguments)
        url = self._resolve_backend_url(url)

        headers: dict[str, str] = {"Accept": "application/json"}
        self._apply_auth(execution, headers, arguments)

        method = execution.method.upper()
        kwargs: dict[str, Any] = {"headers": headers}

        if execution.query_template:
            kwargs["params"] = {
                str(k): self._render_value(v, arguments)
                for k, v in execution.query_template.items()
            }

        if execution.body_template is not None and method in (
            "POST", "PUT", "PATCH",
        ):
            kwargs["json"] = {
                str(k): self._render_value(v, arguments)
                for k, v in execution.body_template.items()
            }

        try:
            response = await self._client.request(method, url, **kwargs)
        except httpx2.HTTPError as exc:
            raise ExecutionError(
                f"Request to the SMB backend failed: {exc}") from exc

        if response.status_code >= 400:
            raise ExecutionError(
                f"SMB backend returned HTTP {response.status_code}: "
                f"{response.text[:500]}")

        try:
            document = response.json()
        except ValueError:
            document = {"raw": response.text}

        if execution.response_mapping:
            try:
                return evaluate(execution.response_mapping, document)
            except JsonPathError as exc:
                raise ExecutionError(
                    f"Response mapping failed: {exc}") from exc

        return document

    # ================================================================
    # Template rendering
    # ================================================================

    def _render(self, template: str, arguments: dict[str, Any]) -> str:
        missing: list[str] = []

        def replace(match: re.Match[str]) -> str:
            name = match.group(1)
            if name not in arguments or arguments[name] is None:
                missing.append(name)
                return ""
            value = arguments[name]
            return str(value) if not isinstance(value, (dict, list)) \
                else json.dumps(value)

        rendered = _TEMPLATE.sub(replace, template)
        if missing:
            raise ExecutionError(
                f"Missing required argument(s): {', '.join(missing)}")
        return rendered

    def _render_value(
        self,
        value: Any,
        arguments: dict[str, Any],
    ) -> Any:
        if isinstance(value, str):
            return self._render(value, arguments)
        if isinstance(value, dict):
            return {
                str(k): self._render_value(v, arguments)
                for k, v in value.items()
            }
        if isinstance(value, list):
            return [self._render_value(v, arguments) for v in value]
        return value

    # ================================================================
    # Auth
    # ================================================================

    def _apply_auth(
        self,
        execution: ExecutionConfig,
        headers: dict[str, str],
        arguments: dict[str, Any],
    ) -> None:
        auth = execution.auth
        if auth is None or auth.type in ("none", None, ""):
            return

        if auth.type == "api_key":
            credential_ref = auth.credential_ref
            if not credential_ref:
                raise ExecutionError(
                    "api_key auth requires a credential_ref.")
            api_key = self._resolve_credential(credential_ref)
            if not api_key:
                raise ExecutionError(
                    f"No credential resolved for {credential_ref}.")
            # SMB backends conventionally accept the key via X-Api-Key.
            headers["X-Api-Key"] = api_key
            return

        # bearer / oauth2 can be added when the platform vault ships.
        raise ExecutionError(f"Unsupported auth type: {auth.type!r}")