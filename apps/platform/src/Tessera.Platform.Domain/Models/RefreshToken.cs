namespace Tessera.Platform.Domain.Models;

/// <summary>
/// A refresh token issued to a user. Exchanged for a new access token
/// when the previous one expires; revoked on rotation.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsRevoked { get; set; }
    public string? TenantId { get; set; }
}
