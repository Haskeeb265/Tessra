using System.IdentityModel.Tokens.Jwt;
using System.Net.Mail;
using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Tessera.Platform.Api.Data;
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

        // Returns the current user's role plus the actions that role is
        // allowed by the tenant's envelope.
        group.MapGet(
            "/me",
            async (
                AppDbContext db,
                ClaimsPrincipal user,
                HttpContext http) =>
            {
                var current = await LoadCurrentUserAsync(db, user, http);

                if (current.User is null)
                {
                    return Results.Unauthorized();
                }

                var (_, tenant, envelope) = current;
                var role = envelope?.Roles.FirstOrDefault(r =>
                    string.Equals(
                        r.Name,
                        current.User.Role,
                        StringComparison.OrdinalIgnoreCase));

                return Results.Ok(new
                {
                    id = current.User.Id,
                    email = current.User.Email,
                    role = current.User.Role,
                    actions = role?.Actions ?? new List<string>(),
                    tenantId = tenant?.Id,
                    tenantIdentifier = tenant?.Identifier
                });
            })
            .WithName("TenantMe");

        // ============================================================
        // Tenant Envelope
        // ============================================================

        group.MapGet(
            "/envelope",
            async (
                AppDbContext db,
                HttpContext http) =>
            {
                var tenant = await LoadTenantAsync(db, http);

                if (tenant is null || tenant.EnvelopeId is null)
                {
                    return Results.NotFound(
                        new
                        {
                            error =
                                "No envelope is assigned to this workspace yet."
                        });
                }

                var envelope = await db.Envelopes
                    .AsNoTracking()
                    .Include(e => e.Roles)
                    .FirstOrDefaultAsync(e => e.Id == tenant.EnvelopeId);

                if (envelope is null)
                {
                    return Results.NotFound(
                        new
                        {
                            error =
                                "No envelope is assigned to this workspace yet."
                        });
                }

                return Results.Ok(new
                {
                    envelope.Id,
                    envelope.Name,
                    envelope.Description,
                    Roles = envelope.Roles
                        .OrderBy(r => r.Name)
                        .Select(r => new { r.Name, r.Actions })
                });
            })
            .WithName("TenantEnvelope");

        // ============================================================
        // User Management (Tenant Admin)
        // ============================================================

        var users = group.MapGroup("/users")
            .RequireAuthorization("AdminOnly");

        users.MapGet(
            "/",
            async (AppDbContext db) =>
            {
                var result = await db.Users
                    .OrderBy(u => u.Email)
                    .Select(u => new
                    {
                        u.Id,
                        u.Email,
                        u.Role,
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
                HttpContext http) =>
            {
                var email = request.Email
                    .Trim()
                    .ToLowerInvariant();

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

                if (await db.Users.AnyAsync(u => u.Email == email))
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "A user with this email already exists " +
                                "in this workspace."
                        });
                }

                var allowedRoles = await GetAllowedRolesAsync(db, http);
                var role = request.Role.Trim();

                if (!allowedRoles.Contains(
                        role,
                        StringComparer.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                $"Role '{role}' is not available " +
                                "in this workspace."
                        });
                }

                var user = new User
                {
                    Email = email,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(
                        request.Password),
                    Role = role
                };

                db.Users.Add(user);
                await db.SaveChangesAsync();

                return Results.Created(
                    $"/tenant/users/{user.Id}",
                    new
                    {
                        user.Id,
                        user.Email,
                        user.Role,
                        user.CreatedAt
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
                var target = await db.Users
                    .FirstOrDefaultAsync(u => u.Id == id);

                if (target is null)
                {
                    return Results.NotFound();
                }

                var currentUserId = GetUserId(user);

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

                var allowedRoles = await GetAllowedRolesAsync(db, http);
                var role = request.Role.Trim();

                if (!allowedRoles.Contains(
                        role,
                        StringComparer.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                $"Role '{role}' is not available " +
                                "in this workspace."
                        });
                }

                target.Role = role;
                await db.SaveChangesAsync();

                return Results.Ok(
                    new
                    {
                        target.Id,
                        target.Email,
                        target.Role,
                        target.CreatedAt
                    });
            })
            .WithName("TenantUpdateUserRole");

        users.MapDelete(
            "/{id:guid}",
            async (
                Guid id,
                AppDbContext db,
                ClaimsPrincipal user) =>
            {
                var target = await db.Users
                    .FirstOrDefaultAsync(u => u.Id == id);

                if (target is null)
                {
                    return Results.NotFound();
                }

                if (GetUserId(user) == target.Id)
                {
                    return Results.BadRequest(
                        new
                        {
                            error = "You cannot remove your own account."
                        });
                }

                db.Users.Remove(target);
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
        var identifier = http.Request.Headers["X-Tenant-Id"]
            .FirstOrDefault();

        if (string.IsNullOrEmpty(identifier))
        {
            return null;
        }

        return await db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Identifier == identifier);
    }

    private static Guid? GetUserId(ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue(JwtRegisteredClaimNames.Sub)
                  ?? user.FindFirstValue(ClaimTypes.NameIdentifier);

        return Guid.TryParse(sub, out var id) ? id : null;
    }

    private static async Task<(User? User, Tenant? Tenant, Envelope? Envelope)>
        LoadCurrentUserAsync(
            AppDbContext db,
            ClaimsPrincipal user,
            HttpContext http)
    {
        var userId = GetUserId(user);

        if (userId is null)
        {
            return (null, null, null);
        }

        var currentUser = await db.Users
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (currentUser is null)
        {
            return (null, null, null);
        }

        var tenant = await LoadTenantAsync(db, http);

        Envelope? envelope = null;

        if (tenant?.EnvelopeId is not null)
        {
            envelope = await db.Envelopes
                .AsNoTracking()
                .Include(e => e.Roles)
                .FirstOrDefaultAsync(e => e.Id == tenant.EnvelopeId);
        }

        return (currentUser, tenant, envelope);
    }

    /// <summary>
    /// Roles that can be assigned in this workspace: the tenant's envelope
    /// roles, or the built-in Admin/User roles when no envelope is assigned.
    /// </summary>
    private static async Task<List<string>> GetAllowedRolesAsync(
        AppDbContext db,
        HttpContext http)
    {
        var tenant = await LoadTenantAsync(db, http);

        if (tenant?.EnvelopeId is not null)
        {
            var envelope = await db.Envelopes
                .AsNoTracking()
                .Include(e => e.Roles)
                .FirstOrDefaultAsync(e => e.Id == tenant.EnvelopeId);

            if (envelope?.Roles.Count > 0)
            {
                return envelope.Roles
                    .Select(r => r.Name)
                    .ToList();
            }
        }

        return [Roles.Admin, Roles.User];
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
    string Role);

public record UpdateTenantUserRequest(string Role);
