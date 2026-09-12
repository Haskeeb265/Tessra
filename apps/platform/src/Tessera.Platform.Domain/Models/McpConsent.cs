namespace Tessera.Platform.Domain.Models;

/// <summary>
/// A user's grant to let an MCP OAuth client act on their behalf within one
/// tenant (the MCP "connector consent"). Platform-level entity (not
/// tenant-filtered by Finbuckle): users are tenant-scoped rows, so the pair
/// (UserId, ClientId) already implies the tenant — TenantId/Identifier are
/// kept as denormalized copies for display and revocation queries.
///
/// v1 uses one coarse scope per tenant ("tools"); the granted scope list is
/// stored as a JSON array so step-up grants can be added later without a
/// schema change.
/// </summary>
public class McpConsent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The tenant-scoped <see cref="User"/> row that granted consent.</summary>
    public Guid UserId { get; set; }

    /// <summary>Tenant id (Users.TenantId semantics), denormalized.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Tenant identifier (slug), denormalized for display/revocation.</summary>
    public string TenantIdentifier { get; set; } = string.Empty;

    /// <summary>The OAuth client_id (OpenIddict application) that was granted access.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>JSON array of granted scope identifiers, e.g. ["tools", "offline_access"].</summary>
    public string ScopesJson { get; set; } = "[]";

    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Set when the user revokes the connector (v1: no UI yet).</summary>
    public DateTime? RevokedAt { get; set; }
}
