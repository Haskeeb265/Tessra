namespace Tessera.Platform.Domain.Models;

/// <summary>
/// A role owned by a single tenant. Tenants derive their roles by copying
/// them from the envelope (template) assigned by a superadmin, then own
/// them: they can rename roles, edit actions, add new roles, and delete
/// them (unless users are still assigned).
///
/// Users reference a role by <see cref="Id"/>, so renames never strand
/// users — resolving a user's actions is a lookup by <see cref="Id"/>,
/// not by name.
/// </summary>
public class TenantRole
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Internal tenant id this role belongs to (Finbuckle).</summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// The envelope role this was copied from (provenance), when the tenant
    /// roles were seeded from an assigned envelope. Null for roles the
    /// tenant created themselves or built-in roles.
    /// </summary>
    public Guid? EnvelopeRoleId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Tessera capability names this role is allowed (e.g. "delete_widget").</summary>
    public List<string> Actions { get; set; } = [];

    /// <summary>
    /// A platform-managed role: the tenant's <c>Superadmin</c> tier. It
    /// always holds every action in <see cref="ActionCatalog"/>, cannot be
    /// renamed, edited, or deleted by the tenant, and can only be assigned
    /// by the platform superadmin through the workspace bootstrap invite.
    /// </summary>
    public bool IsSystem { get; set; }

    public uint RowVersion { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
