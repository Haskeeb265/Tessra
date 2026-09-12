namespace Tessera.Platform.Domain.Models;

/// <summary>
/// Constants for the built-in roles used throughout the application.
/// </summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string User = "User";

    /// <summary>Platform-level superadmin role claim (see <c>AdminUsers</c>).</summary>
    public const string SuperAdmin = "SuperAdmin";

    /// <summary>
    /// The tenant-scoped top role: platform-managed (<see cref="TenantRole.IsSystem"/>),
    /// always holds every action, and is granted to the first invitation
    /// redeemed in a workspace. Distinct from <see cref="SuperAdmin"/> (the
    /// platform account) — the two never share a token.
    /// </summary>
    public const string TenantSuperadmin = "Superadmin";
}

/// <summary>
/// The catalog of Tessera capabilities (actions) that a role can be granted.
/// These are stored on roles so the UI can display them and the platform can
/// enforce them before privileged operations.
/// </summary>
public static class ActionCatalog
{
    public const string ViewWidgets = "view_widgets";
    public const string CreateWidget = "create_widget";
    public const string EditWidget = "edit_widget";
    public const string DeleteWidget = "delete_widget";
    public const string ManageUsers = "manage_users";

    /// <summary>
    /// Managing the tenant's MCP tool manifests (create/edit/delete/read).
    /// The MCP gateway itself reads manifests without this action — it
    /// resolves tools through the tenant's OAuth-authorized identity, not
    /// through the dashboard role set (see docs/mcp.md §2.5).
    /// </summary>
    public const string ManageTools = "manage_tools";

    /// <summary>All known actions, for validation/display purposes.</summary>
    public static readonly string[] All =
    [
        ViewWidgets, CreateWidget, EditWidget, DeleteWidget, ManageUsers,
        ManageTools
    ];
}

/// <summary>
/// A tenant-scoped user account. The user's permissions come from their
/// assigned tenant role (see <see cref="RoleId"/>); the role is resolved
/// at request time so renames never break authorization.
/// </summary>
public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// The tenant role assigned to this user. Null means the user has no
    /// role (and therefore no actions) until one is assigned.
    /// </summary>
    public Guid? RoleId { get; set; }

    public string? TenantId { get; set; }

    /// <summary>
    /// Bumped whenever the user's permissions change (role change, password
    /// change, MFA change). Included as a JWT claim and validated per
    /// request so demoted users lose privileges before token expiry.
    /// </summary>
    public int TokenVersion { get; set; }

    /// <summary>Soft-delete flag; deleted users cannot log in or be listed.</summary>
    public bool IsDeleted { get; set; }

    // Email verification (C2)
    public bool EmailVerified { get; set; }
    public string? VerificationToken { get; set; }
    public DateTime? VerificationTokenExpiresAt { get; set; }

    // Password reset (C2)
    public string? PasswordResetToken { get; set; }
    public DateTime? PasswordResetTokenExpiresAt { get; set; }

    // MFA (TOTP, C2)
    public string? MfaSecret { get; set; }
    public bool MfaEnabled { get; set; }

    public uint RowVersion { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
