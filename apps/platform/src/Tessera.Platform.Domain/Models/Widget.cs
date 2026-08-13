namespace Tessera.Platform.Domain.Models;

public class Widget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}