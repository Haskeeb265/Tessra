namespace Tessera.Platform.Domain.Models;

/// <summary>
/// A platform-level (superadmin) account. Unlike <see cref="User"/>, admin
/// users are NOT bound to a tenant — they manage tenants, envelopes, and
/// platform configuration through the /admin endpoints.
/// </summary>
public class AdminUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
