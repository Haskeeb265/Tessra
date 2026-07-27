using Finbuckle.MultiTenant.Abstractions;

namespace Tessra.Platform.Domain.Models;

public class Tenant : ITenantInfo
{
    public string Id { get; set; } = string.Empty;
    public string Identifier { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ConnectionString { get; set; }
}