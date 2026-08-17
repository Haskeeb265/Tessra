using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Tessera.Platform.Api.Data;
using Tessera.Platform.Domain.Models;

namespace Tessera.Platform.Api.Services;

/// <summary>
/// Action-based authorization (A4): privileged operations require the
/// acting user's tenant role to include a specific action (e.g.
/// <c>delete_widget</c>, <c>manage_users</c>). Actions are resolved from
/// the tenant role at request time, so role renames never break
/// authorization and the UI can be driven by <c>/tenant/me</c> actions.
/// </summary>
public static class ActionChecks
{
    /// <summary>
    /// Returns null when the current user's role includes
    /// <paramref name="action"/>; otherwise a 403 result to return from the
    /// handler. Returns 401 when the user cannot be resolved.
    /// </summary>
    public static async Task<IResult?> RequiresAsync(
        AppDbContext db,
        HttpContext http,
        ClaimsPrincipal principal,
        string action)
    {
        var userId = AuthHelpers.GetUserId(principal);

        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted);

        if (user is null || user.RoleId is null)
        {
            return Results.Unauthorized();
        }

        var role = await db.TenantRoles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == user.RoleId);

        // The platform-managed Superadmin role can do everything.
        if (role is not null && role.IsSystem)
        {
            return null;
        }

        var allowed = role?.Actions.Any(
            a => string.Equals(a, action, StringComparison.OrdinalIgnoreCase))
            ?? false;

        return allowed
            ? null
            : Results.Json(
                new
                {
                    error =
                        "You are not authorized to perform this action."
                },
                statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Resolves the current user's actions from their tenant role. Used by
    /// <c>/tenant/me</c> so the UI is driven by the same source the server
    /// enforces against.
    /// </summary>
    public static async Task<List<string>> GetActionsAsync(
        AppDbContext db,
        ClaimsPrincipal principal)
    {
        var userId = AuthHelpers.GetUserId(principal);

        if (userId is null)
        {
            return [];
        }

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted);

        if (user?.RoleId is null)
        {
            return [];
        }

        var role = await db.TenantRoles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == user.RoleId);

        // The platform-managed Superadmin role can do everything.
        if (role is not null && role.IsSystem)
        {
            return ActionCatalog.All.ToList();
        }

        return role?.Actions.ToList() ?? [];
    }

    /// <summary>Resolves the current user's role name (for display).</summary>
    public static async Task<string?> GetRoleNameAsync(
        AppDbContext db,
        ClaimsPrincipal principal)
    {
        var userId = AuthHelpers.GetUserId(principal);

        if (userId is null)
        {
            return null;
        }

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted);

        if (user?.RoleId is null)
        {
            return null;
        }

        return await db.TenantRoles
            .AsNoTracking()
            .Where(r => r.Id == user.RoleId)
            .Select(r => r.Name)
            .FirstOrDefaultAsync();
    }
}
