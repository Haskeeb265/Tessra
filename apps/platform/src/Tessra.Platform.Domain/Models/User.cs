namespace Tessra.Platform.Domain.Models;

/// <summary>
/// Constants for the built-in roles used throughout the application.
/// </summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string User = "User";
}

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = Roles.User;
    public string? TenantId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
