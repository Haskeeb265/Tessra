namespace Tessra.Platform.Domain.Models;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
