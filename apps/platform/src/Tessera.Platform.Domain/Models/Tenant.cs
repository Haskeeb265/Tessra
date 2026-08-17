using Finbuckle.MultiTenant.Abstractions;

namespace Tessera.Platform.Domain.Models;

/// <summary>
/// Lifecycle state of a tenant. Suspended tenants cannot log in or call
/// tenant-scoped APIs (blocked in middleware); used for trials, non-payment,
/// and abuse handling.
/// </summary>
public enum TenantStatus
{
    Active = 0,
    Suspended = 1
}

public class Tenant : ITenantInfo
{
    public string Id { get; set; } = string.Empty;
    public string Identifier { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ConnectionString { get; set; }

    /// <summary>
    /// The envelope (role template bundle) assigned to this tenant by a
    /// superadmin. The envelope's roles are copied into the tenant's own
    /// role set when assigned; later envelope edits do NOT cascade.
    /// </summary>
    public Guid? EnvelopeId { get; set; }

    public TenantStatus Status { get; set; } = TenantStatus.Active;

    /// <summary>Soft-delete flag; deleted tenants no longer resolve.</summary>
    public bool IsDeleted { get; set; }

    public uint RowVersion { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}