namespace Tessera.Platform.Domain.Models;

/// <summary>
/// An "envelope" is a bundle of roles (and their allowed actions) that a
/// superadmin assigns to a tenant. The tenant's users are assigned roles
/// from this envelope in the business portal.
/// </summary>
public class Envelope
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<AppRole> Roles { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
