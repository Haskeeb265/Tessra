namespace Tessera.Platform.Domain.Models;

/// <summary>
/// An invitation for a user to join a tenant. Created by a tenant admin,
/// delivered by email (via the pluggable <c>IEmailSender</c>), and redeemed
/// at registration by presenting the invite token.
///
/// Replaces the "first user becomes Admin" bootstrap as the secure way to
/// onboard members into a workspace.
/// </summary>
public class Invitation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Internal tenant id the invite is for (Finbuckle).</summary>
    public string? TenantId { get; set; }

    /// <summary>The invited email address. Registration must use this email.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>The tenant role the invited user will be assigned on redemption.</summary>
    public Guid RoleId { get; set; }

    /// <summary>SHA-256 hash of the invite token sent in the email link.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public DateTime? UsedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
