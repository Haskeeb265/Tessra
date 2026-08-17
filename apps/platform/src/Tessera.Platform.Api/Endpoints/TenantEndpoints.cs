using System.IdentityModel.Tokens.Jwt;
using System.Net.Mail;
using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Tessera.Platform.Api.Data;
using Tessera.Platform.Api.Services;
using Tessera.Platform.Domain.Models;

namespace Tessera.Platform.Api.Endpoints;

public static class TenantEndpoints
{
    public static void MapTenantEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/tenant").RequireAuthorization();

        // ============================================================
        // Current User
        // ============================================================

        // Returns the current user's role plus the actions that role allows
        // (resolved live from the tenant's role set, so renames never break
        // permissions and the UI can be driven by the same source the
        // server enforces against).
        group.MapGet(
            "/me",
            async (
                AppDbContext db,
                ClaimsPrincipal user,
                HttpContext http) =>
            {
                var userId = AuthHelpers.GetUserId(user);

                if (userId is null)
                {
                    return Results.Unauthorized();
                }

                var current = await db.Users
                    .AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted);

                if (current is null)
                {
                    return Results.Unauthorized();
                }

                var role = current.RoleId is Guid roleId
                    ? await db.TenantRoles
                        .AsNoTracking()
                        .FirstOrDefaultAsync(r => r.Id == roleId)
                    : null;

                var tenant = await LoadTenantAsync(db, http);

                return Results.Ok(new
                {
                    id = current.Id,
                    email = current.Email,
                    role = role?.Name,
                    roleId = role?.Id,
                    actions = role?.Actions ?? new List<string>(),
                    tenantId = tenant?.Id,
                    tenantIdentifier = tenant?.Identifier
                });
            })
            .WithName("TenantMe");

        // ============================================================
        // Tenant roles (the workspace's own role set)
        // ============================================================

        group.MapGet(
            "/envelope",
            async (
                AppDbContext db,
                HttpContext http) =>
            {
                var tenant = await LoadTenantAsync(db, http);

                if (tenant is null)
                {
                    return Results.NotFound(
                        new { error = "Workspace not found." });
                }

                var roles = await db.TenantRoles
                    .AsNoTracking()
                    .OrderBy(r => r.Name)
                    .Select(r => new
                    {
                        r.Id,
                        r.Name,
                        r.Actions,
                        r.IsSystem
                    })
                    .ToListAsync();

                // Kept as "envelope" for backwards compatibility with the
                // business portal, but the roles are the tenant's own copies.
                return Results.Ok(new
                {
                    id = (Guid?)null,
                    name = $"{tenant.Name} roles",
                    description = (string?)null,
                    roles
                });
            })
            .WithName("TenantEnvelope");

        // Role management (tenant admins manage their own role set).
        var roles = group.MapGroup("/roles");

        roles.MapGet(
            "/",
            async (AppDbContext db) =>
            {
                var result = await db.TenantRoles
                    .AsNoTracking()
                    .OrderBy(r => r.Name)
                    .Select(r => new
                    {
                        r.Id,
                        r.Name,
                        r.Actions,
                        r.IsSystem
                    })
                    .ToListAsync();

                return Results.Ok(result);
            })
            .WithName("TenantListRoles");

        roles.MapPost(
            "/",
            async (
                UpsertTenantRoleRequest request,
                AppDbContext db,
                HttpContext http,
                ClaimsPrincipal user) =>
            {
                var denied = await ActionChecks.RequiresAsync(
                    db, http, user, ActionCatalog.ManageUsers);

                if (denied is not null)
                {
                    return denied;
                }

                var error = ValidateRoleRequest(request);

                if (error is not null)
                {
                    return Results.BadRequest(new { error });
                }

                var name = request.Name.Trim();

                if (await db.TenantRoles.AnyAsync(
                        r => r.Name.ToLower() == name.ToLower()))
                {
                    return Results.BadRequest(
                        new { error = "A role with this name already exists." });
                }

                var role = new TenantRole
                {
                    Name = name,
                    Actions = request.Actions
                        .Select(a => a.Trim().ToLowerInvariant())
                        .Distinct()
                        .ToList()
                };

                db.TenantRoles.Add(role);

                try
                {
                    await db.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    return Results.BadRequest(
                        new { error = "A role with this name already exists." });
                }

                return Results.Created(
                    $"/tenant/roles/{role.Id}",
                    new { role.Id, role.Name, role.Actions });
            })
            .WithName("TenantCreateRole");

        roles.MapPut(
            "/{id:guid}",
            async (
                Guid id,
                UpsertTenantRoleRequest request,
                AppDbContext db,
                HttpContext http,
                ClaimsPrincipal user) =>
            {
                var denied = await ActionChecks.RequiresAsync(
                    db, http, user, ActionCatalog.ManageUsers);

                if (denied is not null)
                {
                    return denied;
                }

                var error = ValidateRoleRequest(request);

                if (error is not null)
                {
                    return Results.BadRequest(new { error });
                }

                var role = await db.TenantRoles
                    .FirstOrDefaultAsync(r => r.Id == id);

                if (role is null)
                {
                    return Results.NotFound();
                }

                if (role.IsSystem)
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "The " + role.Name + " role is managed by " +
                                "the platform and cannot be changed."
                        });
                }

                var name = request.Name.Trim();

                if (await db.TenantRoles.AnyAsync(
                        r => r.Id != id && r.Name.ToLower() == name.ToLower()))
                {
                    return Results.BadRequest(
                        new { error = "A role with this name already exists." });
                }

                role.Name = name;
                role.Actions = request.Actions
                    .Select(a => a.Trim().ToLowerInvariant())
                    .Distinct()
                    .ToList();

                try
                {
                    await db.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    return Results.Conflict(
                        new
                        {
                            error =
                                "This role was modified by someone else. " +
                                "Reload and try again."
                        });
                }

                return Results.Ok(new { role.Id, role.Name, role.Actions });
            })
            .WithName("TenantUpdateRole");

        roles.MapDelete(
            "/{id:guid}",
            async (
                Guid id,
                AppDbContext db,
                HttpContext http,
                ClaimsPrincipal user) =>
            {
                var denied = await ActionChecks.RequiresAsync(
                    db, http, user, ActionCatalog.ManageUsers);

                if (denied is not null)
                {
                    return denied;
                }

                var role = await db.TenantRoles
                    .FirstOrDefaultAsync(r => r.Id == id);

                if (role is null)
                {
                    return Results.NotFound();
                }

                if (role.IsSystem)
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "The " + role.Name + " role is managed by " +
                                "the platform and cannot be deleted."
                        });
                }

                // Block deletion while users are assigned — no stranding,
                // no silent permission loss (the A3 fallback never triggers).
                var assignedUsers = await db.Users.AnyAsync(
                    u => u.RoleId == id && !u.IsDeleted);

                if (assignedUsers)
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "This role is assigned to users. Reassign " +
                                "them before deleting it."
                        });
                }

                db.TenantRoles.Remove(role);

                try
                {
                    await db.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    return Results.Conflict(
                        new
                        {
                            error =
                                "This role was modified by someone else. " +
                                "Reload and try again."
                        });
                }

                return Results.NoContent();
            })
            .WithName("TenantDeleteRole");

        // ============================================================
        // Invitations
        // ============================================================

        var invites = group.MapGroup("/invites");

        invites.MapGet(
            "/",
            async (
                AppDbContext db,
                HttpContext http,
                ClaimsPrincipal user) =>
            {
                var denied = await ActionChecks.RequiresAsync(
                    db, http, user, ActionCatalog.ManageUsers);

                if (denied is not null)
                {
                    return denied;
                }

                var now = DateTime.UtcNow;

                var result = await db.Invitations
                    .AsNoTracking()
                    .OrderByDescending(i => i.CreatedAt)
                    .Select(i => new
                    {
                        i.Id,
                        i.Email,
                        i.RoleId,
                        i.ExpiresAt,
                        i.UsedAt,
                        i.CreatedAt,
                        Expired = i.ExpiresAt < now
                    })
                    .ToListAsync();

                return Results.Ok(result);
            })
            .WithName("TenantListInvites");

        invites.MapPost(
            "/",
            async (
                CreateInviteRequest request,
                AuthService authService,
                AppDbContext db,
                HttpContext http,
                ClaimsPrincipal user) =>
            {
                var denied = await ActionChecks.RequiresAsync(
                    db, http, user, ActionCatalog.ManageUsers);

                if (denied is not null)
                {
                    return denied;
                }

                var email = request.Email.Trim().ToLowerInvariant();

                if (!IsValidEmail(email))
                {
                    return Results.BadRequest(
                        new { error = "A valid email is required." });
                }

                var result = await authService.InviteUserAsync(
                    email,
                    request.RoleId);

                return result.IsSuccess
                    ? Results.Ok(new { message = result.Message })
                    : Results.BadRequest(new { error = result.ErrorMessage });
            })
            .WithName("TenantCreateInvite");

        // ============================================================
        // User Management (requires manage_users action)
        // ============================================================

        var users = group.MapGroup("/users");

        users.MapGet(
            "/",
            async (
                AppDbContext db,
                HttpContext http,
                ClaimsPrincipal user) =>
            {
                var denied = await ActionChecks.RequiresAsync(
                    db, http, user, ActionCatalog.ManageUsers);

                if (denied is not null)
                {
                    return denied;
                }

                var result = await db.Users
                    .AsNoTracking()
                    .Where(u => !u.IsDeleted)
                    .OrderBy(u => u.Email)
                    .Select(u => new
                    {
                        u.Id,
                        u.Email,
                        Role = db.TenantRoles
                            .Where(r => r.Id == u.RoleId)
                            .Select(r => r.Name)
                            .FirstOrDefault(),
                        RoleIsSystem = db.TenantRoles
                            .Where(r => r.Id == u.RoleId)
                            .Select(r => r.IsSystem)
                            .FirstOrDefault(),
                        u.CreatedAt
                    })
                    .ToListAsync();

                return Results.Ok(result);
            })
            .WithName("TenantListUsers");

        users.MapPost(
            "/",
            async (
                CreateTenantUserRequest request,
                AppDbContext db,
                HttpContext http,
                ClaimsPrincipal user) =>
            {
                var denied = await ActionChecks.RequiresAsync(
                    db, http, user, ActionCatalog.ManageUsers);

                if (denied is not null)
                {
                    return denied;
                }

                var email = request.Email.Trim().ToLowerInvariant();

                if (!IsValidEmail(email))
                {
                    return Results.BadRequest(
                        new { error = "A valid email is required." });
                }

                if (string.IsNullOrWhiteSpace(request.Password) ||
                    request.Password.Length < 8)
                {
                    return Results.BadRequest(
                        new
                        {
                            error = "Password must be at least 8 characters."
                        });
                }

                if (await db.Users.AnyAsync(
                        u => u.Email == email && !u.IsDeleted))
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "A user with this email already exists " +
                                "in this workspace."
                        });
                }

                var role = await ResolveRoleAsync(
                    db, request.RoleId, request.Role);

                if (role is null)
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "The specified role is not available " +
                                "in this workspace."
                        });
                }

                if (role.IsSystem)
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "The Superadmin role is managed by the " +
                                "platform and cannot be assigned here."
                        });
                }

                var newUser = new User
                {
                    Email = email,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(
                        request.Password),
                    RoleId = role.Id,
                    EmailVerified = true
                };

                db.Users.Add(newUser);
                await db.SaveChangesAsync();

                return Results.Created(
                    $"/tenant/users/{newUser.Id}",
                    new
                    {
                        newUser.Id,
                        newUser.Email,
                        Role = role.Name,
                        newUser.CreatedAt
                    });
            })
            .WithName("TenantCreateUser");

        users.MapPut(
            "/{id:guid}",
            async (
                Guid id,
                UpdateTenantUserRequest request,
                AppDbContext db,
                HttpContext http,
                ClaimsPrincipal user) =>
            {
                var denied = await ActionChecks.RequiresAsync(
                    db, http, user, ActionCatalog.ManageUsers);

                if (denied is not null)
                {
                    return denied;
                }

                var target = await db.Users
                    .FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted);

                if (target is null)
                {
                    return Results.NotFound();
                }

                var currentUserId = AuthHelpers.GetUserId(user);

                if (currentUserId == target.Id)
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "You cannot change your own role here — " +
                                "ask another admin."
                        });
                }

                // The platform-managed Superadmin tier cannot be demoted
                // from the tenant side (only the platform designates it).
                var systemRole = await db.TenantRoles
                    .FirstOrDefaultAsync(r => r.IsSystem);

                if (systemRole is not null && target.RoleId == systemRole.Id)
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "The Superadmin role is managed by the " +
                                "platform and cannot be changed here."
                        });
                }

                var role = await ResolveRoleAsync(
                    db, request.RoleId, request.Role);

                if (role is null)
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "The specified role is not available " +
                                "in this workspace."
                        });
                }

                if (role.IsSystem)
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "The Superadmin role is managed by the " +
                                "platform and cannot be assigned here."
                        });
                }

                if (target.RoleId != role.Id)
                {
                    target.RoleId = role.Id;
                    target.TokenVersion++;

                    // Force re-auth so the new permissions take effect
                    // immediately (A5).
                    await RevokeUserTokensAsync(db, target.Id);
                }

                await db.SaveChangesAsync();

                return Results.Ok(
                    new
                    {
                        target.Id,
                        target.Email,
                        Role = role.Name,
                        target.CreatedAt
                    });
            })
            .WithName("TenantUpdateUserRole");

        users.MapDelete(
            "/{id:guid}",
            async (
                Guid id,
                AppDbContext db,
                HttpContext http,
                ClaimsPrincipal user) =>
            {
                var denied = await ActionChecks.RequiresAsync(
                    db, http, user, ActionCatalog.ManageUsers);

                if (denied is not null)
                {
                    return denied;
                }

                var target = await db.Users
                    .FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted);

                if (target is null)
                {
                    return Results.NotFound();
                }

                if (AuthHelpers.GetUserId(user) == target.Id)
                {
                    return Results.BadRequest(
                        new { error = "You cannot remove your own account." });
                }

                // The platform-managed Superadmin tier cannot be removed
                // from the tenant side (only the platform designates it).
                var systemRole = await db.TenantRoles
                    .FirstOrDefaultAsync(r => r.IsSystem);

                if (systemRole is not null && target.RoleId == systemRole.Id)
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "The Superadmin role is managed by the " +
                                "platform and cannot be changed here."
                        });
                }

                // Soft delete + revoke every session.
                target.IsDeleted = true;
                target.TokenVersion++;
                target.RoleId = null;

                await RevokeUserTokensAsync(db, target.Id);
                await db.SaveChangesAsync();

                return Results.NoContent();
            })
            .WithName("TenantDeleteUser");
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static async Task<Tenant?> LoadTenantAsync(
        AppDbContext db,
        HttpContext http)
    {
        var identifier = AuthHelpers.GetTenantIdentifier(http);

        if (string.IsNullOrEmpty(identifier))
        {
            return null;
        }

        return await db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(
                t => t.Identifier == identifier && !t.IsDeleted);
    }

    private static string? ValidateRoleRequest(
        UpsertTenantRoleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return "Role name is required.";
        }

        var knownActions = new HashSet<string>(
            ActionCatalog.All,
            StringComparer.OrdinalIgnoreCase);

        foreach (var action in request.Actions)
        {
            if (!knownActions.Contains(action.Trim()))
            {
                return $"Unknown action: '{action}'. " +
                       "See ActionCatalog for valid actions.";
            }
        }

        return null;
    }

    private static async Task<TenantRole?> ResolveRoleAsync(
        AppDbContext db,
        Guid? roleId,
        string? roleName)
    {
        if (roleId is Guid id)
        {
            return await db.TenantRoles
                .FirstOrDefaultAsync(r => r.Id == id);
        }

        if (!string.IsNullOrWhiteSpace(roleName))
        {
            var name = roleName.Trim();

            return await db.TenantRoles
                .FirstOrDefaultAsync(r => r.Name.ToLower() == name.ToLower());
        }

        return null;
    }

    private static async Task RevokeUserTokensAsync(
        AppDbContext db,
        Guid userId)
    {
        var tokens = await db.RefreshTokens
            .Where(rt => rt.UserId == userId && !rt.IsRevoked)
            .ToListAsync();

        foreach (var token in tokens)
        {
            token.IsRevoked = true;
        }
    }

    private static bool IsValidEmail(string email)
    {
        try
        {
            var address = new MailAddress(email);
            return address.Address == email;
        }
        catch
        {
            return false;
        }
    }
}

// ================================================================
// Request DTOs
// ================================================================

public record CreateTenantUserRequest(
    string Email,
    string Password,
    string Role,
    Guid? RoleId = null);

public record UpdateTenantUserRequest(string Role, Guid? RoleId = null);

public record UpsertTenantRoleRequest(string Name, List<string> Actions);

public record CreateInviteRequest(string Email, Guid RoleId);
