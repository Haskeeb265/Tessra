using Finbuckle.MultiTenant.Abstractions;

namespace Tessra.Platform.Domain.Models;

[MultiTenant]
public class Widget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}