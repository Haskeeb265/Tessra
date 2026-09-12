"""Pydantic models for tool manifests.

These mirror the shared manifest contract (docs/sample-smb/acme-dental-
manifest.json and the C# ToolManifests entity). The gateway is a dumb
consumer: it never validates business rules, it only maps the contract onto
MCP tools and executes the HTTP config.
"""

from __future__ import annotations

from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field


class ExecutionAuth(BaseModel):
    """How the executor authenticates to the SMB backend."""

    model_config = ConfigDict(extra="allow")

    type: str = "none"
    # vault://<tenant>/<ref> — resolved by the executor's credential source.
    credential_ref: str | None = None


class ExecutionConfig(BaseModel):
    """The manifest's `execution` object — how a tool executes.

    v1 supports the `http` type: method + url + optional body/query/path
    templates + response mapping (docs/mcp/README.md §2.1).
    """

    model_config = ConfigDict(extra="allow")

    # v1 contract: HTTP execution only (docs/mcp/README.md §2.1).
    type: Literal["http"] = "http"
    auth: ExecutionAuth | None = None
    body_template: dict[str, Any] | None = None
    query_template: dict[str, Any] | None = None
    response_mapping: str | None = None


class ToolManifest(BaseModel):
    """One tenant tool: what the AI sees (name/description/schema) plus how
    to execute it (execution config)."""

    model_config = ConfigDict(extra="allow")

    tool_name: str
    description: str = ""
    input_schema: dict[str, Any] = Field(default_factory=dict)
    execution: ExecutionConfig
    required_scopes: list[str] = Field(default_factory=list)


class TenantCatalog(BaseModel):
    """The full response of GET /internal/gateway/manifests."""

    model_config = ConfigDict(extra="allow")

    tenant_id: str
    tenant_name: str | None = None
    status: str = "Active"
    tools: list[ToolManifest] = Field(default_factory=list)

    @property
    def suspended(self) -> bool:
        return self.status.lower() == "suspended"