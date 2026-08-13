using Finbuckle.MultiTenant.Abstractions;

namespace Tessera.Platform.Domain.Models;

public class Tenant : ITenantInfo
{
    public string Id { get; set; } = string.Empty;
    public string Identifier { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ConnectionString { get; set; }

    /// <summary>
    /// The envelope (role + action bundle) assigned to this tenant by a
    /// superadmin. Null until an envelope is assigned.
    /// </summary>
    public Guid? EnvelopeId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}