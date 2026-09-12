namespace Tessera.Platform.Domain.Models;

/// <summary>
/// A tenant-scoped MCP tool manifest — the contract the MCP gateway validates
/// and executes against. This is a placeholder shape aligned with
/// docs/sample_smb.md; the real shared schema lives in docs/mcp.md and the
/// eventual packages/contracts artifact.
/// </summary>
public class ToolManifest
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Internal tenant id this manifest belongs to (Finbuckle).</summary>
    public string? TenantId { get; set; }

    /// <summary>Stable tool name within the tenant (e.g. "book_appointment").</summary>
    public string ToolName { get; set; } = string.Empty;

    /// <summary>Human-readable description for the tool.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>JSON Schema for the tool's input parameters.</summary>
    public string InputSchema { get; set; } = "{}";

    /// <summary>
    /// How the tool is executed (for v1: "http" with method/url/auth/body template).
    /// Stored as JSON for simplicity until a typed execution model is worth it.
    /// </summary>
    public string Execution { get; set; } = "{}";

    /// <summary>Coarse scopes required to call the tool (e.g. ["appointments:write"]).</summary>
    public List<string> RequiredScopes { get; set; } = [];

    /// <summary>Per-tool rate limit override, when configured (nullable for "use tenant default").</summary>
    public string? RateLimitOverride { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag; deleted manifests no longer resolve.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Optimistic concurrency token mapped to PostgreSQL's xmin system column,
    /// matching the other tenant-scoped entities.
    /// </summary>
    public uint RowVersion { get; set; }
}
