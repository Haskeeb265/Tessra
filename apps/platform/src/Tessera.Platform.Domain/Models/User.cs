namespace Tessera.Platform.Domain.Models;

/// <summary>
/// Constants for the built-in roles used throughout the application.
/// </summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string User = "User";
    public const string SuperAdmin = "SuperAdmin";
}

/// <summary>
/// The catalog of Tessera capabilities (actions) that a role can be granted.
/// These are stored on roles so the UI can display them, but they are NOT
/// enforced yet — enforcement is on the backlog until the underlying MCP
/// functionality exists.
/// </summary>
public static class ActionCatalog
{
    public const string ViewWidgets = "view_widgets";
    public const string CreateWidget = "create_widget";
    public const string EditWidget = "edit_widget";
    public const string DeleteWidget = "delete_widget";
    public const string ManageUsers = "manage_users";
    public const string CreateMcp = "create_mcp";
    public const string AddTools = "add_tools";
    public const string DeleteMcp = "delete_mcp";

    /// <summary>All known actions, for validation/display purposes.</summary>
    public static readonly string[] All =
    [
        ViewWidgets, CreateWidget, EditWidget, DeleteWidget,
        ManageUsers, CreateMcp, AddTools, DeleteMcp
    ];
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
