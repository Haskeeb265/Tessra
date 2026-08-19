using Finbuckle.MultiTenant.EntityFrameworkCore;

using Microsoft.EntityFrameworkCore;

using Tessera.Platform.Api.Data;
using Tessera.Platform.Api.Services;
using Tessera.Platform.Domain.Models;

namespace Tessera.Platform.Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        // ============================================================
        // Superadmin Authentication
        // ============================================================

        app.MapGroup("/admin/auth")
            .MapPost(
                "/login",
                async (
                    LoginRequest request,
                    AdminAuthService authService) =>
                {
                    var result = await authService.LoginAsync(
                        request.Email,
                        request.Password);

                    return result.IsSuccess
                        ? Results.Ok(
                            new TokenResponse(
                                result.AccessToken!,
                                result.RefreshToken!))
                        : Results.Unauthorized();
                })
            .WithName("AdminLogin");

        // ============================================================
        // Tenant Management
        // ============================================================

        var tenants = app.MapGroup("/admin/tenants")
            .RequireAuthorization("SuperAdminOnly");

        // ------------------------------------------------------------
        // List Tenants
        // ------------------------------------------------------------

        tenants.MapGet(
            "/",
            async (AppDbContext db) =>
            {
                var result = await db.Tenants
                    .AsNoTracking()
                    .Where(t => !t.IsDeleted)
                    .Select(t => new
                    {
                        t.Id,
                        t.Identifier,
                        t.Name,
                        t.EnvelopeId,

                        EnvelopeName = t.EnvelopeId == null
                            ? null
                            : db.Envelopes
                                .Where(e => e.Id == t.EnvelopeId)
                                .Select(e => e.Name)
                                .FirstOrDefault(),

                        t.CreatedAt
                    })
                    .OrderBy(t => t.Name)
                    .ToListAsync();

                return Results.Ok(result);
            })
            .WithName("AdminListTenants");

        // ------------------------------------------------------------
        // Create Tenant
        // ------------------------------------------------------------

        tenants.MapPost(
            "/",
            async (
                CreateTenantRequest request,
                AppDbContext db,
                HttpContext http) =>
            {
                var identifier = request.Identifier
                    .Trim()
                    .ToLowerInvariant();

                if (!IsValidIdentifier(identifier))
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "Identifier must be lowercase letters, " +
                                "numbers, and hyphens only."
                        });
                }

                if (await db.Tenants.AnyAsync(
                        t => t.Identifier == identifier))
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "A tenant with this identifier already exists."
                        });
                }

                if (request.EnvelopeId is not null &&
                    !await db.Envelopes.AnyAsync(
                        e => e.Id == request.EnvelopeId))
                {
                    return Results.BadRequest(
                        new
                        {
                            error = "The specified envelope does not exist."
                        });
                }

                var tenant = new Tenant
                {
                    Id = identifier,
                    Identifier = identifier,
                    Name = request.Name.Trim(),
                    EnvelopeId = request.EnvelopeId
                };

                db.Tenants.Add(tenant);

                await db.SaveChangesAsync();

                // Seed the tenant's own roles from the assigned envelope
                // template (copy semantics — the tenant owns its roles).
                if (request.EnvelopeId is Guid envelopeId)
                {
                    await CopyEnvelopeRolesToTenantAsync(
                        db,
                        http,
                        tenant.Id,
                        envelopeId);
                }

                return Results.Created(
                    $"/admin/tenants/{tenant.Id}",
                    tenant);
            })
            .WithName("AdminCreateTenant");

        // ------------------------------------------------------------
        // Update Tenant
        // ------------------------------------------------------------

        tenants.MapPut(
            "/{id}",
            async (
                string id,
                UpdateTenantRequest request,
                AppDbContext db,
                HttpContext http) =>
            {
                var tenant = await db.Tenants
                    .FirstOrDefaultAsync(t => t.Id == id);

                if (tenant is null)
                {
                    return Results.NotFound();
                }

                var newIdentifier = request.Identifier
                    .Trim()
                    .ToLowerInvariant();

                if (!IsValidIdentifier(newIdentifier))
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "Identifier must be lowercase letters, " +
                                "numbers, and hyphens only."
                        });
                }

                if (newIdentifier != tenant.Identifier &&
                    await db.Tenants.AnyAsync(
                        t => t.Identifier == newIdentifier))
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "A tenant with this identifier already exists."
                        });
                }

                if (request.EnvelopeId is not null &&
                    !await db.Envelopes.AnyAsync(
                        e => e.Id == request.EnvelopeId))
                {
                    return Results.BadRequest(
                        new
                        {
                            error = "The specified envelope does not exist."
                        });
                }

                // Users store the tenant ID internally, so keep the
                // primary key stable even when the identifier changes.
                var envelopeChanged = tenant.EnvelopeId != request.EnvelopeId;

                tenant.Identifier = newIdentifier;
                tenant.Name = request.Name.Trim();
                tenant.EnvelopeId = request.EnvelopeId;

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
                                "This tenant was modified by someone else. " +
                                "Reload and try again."
                        });
                }

                // When the envelope assignment changes, copy any NEW roles
                // from the new template into the tenant's role set (merge —
                // the tenant's existing, possibly customized roles are kept).
                if (envelopeChanged && request.EnvelopeId is Guid envelopeId)
                {
                    await CopyEnvelopeRolesToTenantAsync(
                        db,
                        http,
                        tenant.Id,
                        envelopeId);
                }

                return Results.Ok(tenant);
            })
            .WithName("AdminUpdateTenant");

        // ------------------------------------------------------------
        // Delete Tenant (soft-delete tenant + users; hard-delete widgets,
        // refresh tokens, invitations so nothing dangles — B1/B4)
        // ------------------------------------------------------------

        tenants.MapDelete(
            "/{id}",
            async (
                string id,
                AppDbContext db,
                HttpContext http) =>
            {
                var tenant = await db.Tenants
                    .FirstOrDefaultAsync(t => t.Id == id && !t.IsDeleted);

                if (tenant is null)
                {
                    return Results.NotFound();
                }

                // PostgreSQL:
                // Use raw SQL to bypass Finbuckle's EnforceMultiTenant,
                // which requires a tenant context that superadmin
                // requests do not have.
                if (db.Database.IsRelational())
                {
                    // B1: widgets are deleted too (no orphaned rows).
                    await db.Database.ExecuteSqlRawAsync(
                        "DELETE FROM \"Widgets\" WHERE \"TenantId\" = {0}",
                        tenant.Id);

                    await db.Database.ExecuteSqlRawAsync(
                        "DELETE FROM \"RefreshTokens\" WHERE \"TenantId\" = {0}",
                        tenant.Id);

                    await db.Database.ExecuteSqlRawAsync(
                        "DELETE FROM \"Invitations\" WHERE \"TenantId\" = {0}",
                        tenant.Id);

                    // B4: soft delete users and the tenant itself.
                    await db.Database.ExecuteSqlRawAsync(
                        """
                        UPDATE "Users"
                        SET "IsDeleted" = true, "RoleId" = NULL
                        WHERE "TenantId" = {0}
                        """,
                        tenant.Id);

                    await db.Database.ExecuteSqlRawAsync(
                        "UPDATE \"Tenants\" SET \"IsDeleted\" = true " +
                        "WHERE \"Id\" = {0}",
                        tenant.Id);

                    return Results.NoContent();
                }

                // InMemory provider:
                // Bind a temporary context to the tenant being deleted
                // so Finbuckle's EnforceMultiTenant accepts the removals.
                await using var bound =
                    MultiTenantDbContext.Create<AppDbContext, Tenant>(
                        new Tenant
                        {
                            Id = tenant.Id
                        },
                        http.RequestServices);

                var widgets = await bound.Widgets
                    .IgnoreQueryFilters()
                    .Where(w => w.TenantId == tenant.Id)
                    .ToListAsync();

                bound.Widgets.RemoveRange(widgets);

                var refreshTokens = await bound.RefreshTokens
                    .IgnoreQueryFilters()
                    .Where(rt => rt.TenantId == tenant.Id)
                    .ToListAsync();

                bound.RefreshTokens.RemoveRange(refreshTokens);

                var invitations = await bound.Invitations
                    .IgnoreQueryFilters()
                    .Where(i => i.TenantId == tenant.Id)
                    .ToListAsync();

                bound.Invitations.RemoveRange(invitations);

                var users = await bound.Users
                    .IgnoreQueryFilters()
                    .Where(u => u.TenantId == tenant.Id)
                    .ToListAsync();

                foreach (var user in users)
                {
                    user.IsDeleted = true;
                    user.RoleId = null;
                }

                await bound.SaveChangesAsync();

                tenant.IsDeleted = true;

                await db.SaveChangesAsync();

                return Results.NoContent();
            })
            .WithName("AdminDeleteTenant");

        // ------------------------------------------------------------
        // Tenant Status (suspend / unsuspend — B3)
        // ------------------------------------------------------------

        tenants.MapPut(
            "/{id}/status",
            async (
                string id,
                UpdateTenantStatusRequest request,
                AppDbContext db) =>
            {
                if (!Enum.TryParse<TenantStatus>(
                        request.Status,
                        ignoreCase: true,
                        out var status))
                {
                    return Results.BadRequest(
                        new
                        {
                            error = "Status must be 'Active' or 'Suspended'."
                        });
                }

                var tenant = await db.Tenants
                    .FirstOrDefaultAsync(t => t.Id == id && !t.IsDeleted);

                if (tenant is null)
                {
                    return Results.NotFound();
                }

                tenant.Status = status;

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
                                "This tenant was modified by someone else. " +
                                "Reload and try again."
                        });
                }

                return Results.Ok(
                    new
                    {
                        tenant.Id,
                        tenant.Identifier,
                        tenant.Status
                    });
            })
            .WithName("AdminUpdateTenantStatus");

        // ------------------------------------------------------------
        // Platform Invite (designate the tenant's first Superadmin)
        // ------------------------------------------------------------

        tenants.MapPost(
            "/{id}/invites",
            async (
                string id,
                PlatformInviteRequest request,
                AppDbContext db,
                HttpContext http,
                IEmailSender emailSender,
                IConfiguration configuration) =>
            {
                // Accept either the internal Id or the external Identifier
                // (seeded tenants like alpha-corp differ between the two).
                var tenant = await db.Tenants
                    .FirstOrDefaultAsync(
                        t => (t.Id == id || t.Identifier == id) &&
                             !t.IsDeleted);

                if (tenant is null)
                {
                    return Results.NotFound();
                }

                var email = request.Email.Trim().ToLowerInvariant();

                if (!IsValidEmail(email))
                {
                    return Results.BadRequest(
                        new { error = "A valid email is required." });
                }

                // Superadmin requests have no tenant context, so all
                // tenant-scoped work runs on a context bound to this tenant.
                await using var bound =
                    MultiTenantDbContext.Create<AppDbContext, Tenant>(
                        new Tenant
                        {
                            Id = tenant.Id
                        },
                        http.RequestServices);

                await using var transaction =
                    await TenantConcurrency.BeginTenantTransactionAsync(
                        bound,
                        tenant.Id);

                // Ensure roles exist, then pick the invite role: the
                // platform-managed Superadmin only while the workspace has no
                // owner and no outstanding owner invite. Tenant admins invite
                // the rest of the team through /tenant/invites.
                await TenantRoleSeeder.EnsureTenantRolesAsync(
                    bound,
                    tenant.EnvelopeId);

                var superadminRole = await bound.TenantRoles
                    .FirstOrDefaultAsync(r => r.IsSystem);

                var hasSuperadmin = superadminRole is not null &&
                    await bound.Users.AnyAsync(
                        u => u.RoleId == superadminRole.Id && !u.IsDeleted);

                if (hasSuperadmin)
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "This workspace already has a Superadmin. " +
                                "Invite additional users from the workspace " +
                                "team page."
                        });
                }

                var now = DateTime.UtcNow;

                var hasPendingOwnerInvite = superadminRole is not null &&
                    await bound.Invitations.AnyAsync(
                        i => i.RoleId == superadminRole.Id &&
                             i.UsedAt == null &&
                             i.ExpiresAt >= now);

                if (hasPendingOwnerInvite)
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "This workspace already has a pending " +
                                "Superadmin invitation."
                        });
                }

                Guid? roleId = superadminRole?.Id;

                if (roleId is null)
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "No assignable role is available in this " +
                                "workspace."
                        });
                }

                var userExists = await bound.Users.AnyAsync(
                    u => u.Email == email && !u.IsDeleted);

                var token = AuthHelpers.NewToken();

                if (!userExists)
                {
                    bound.Invitations.Add(
                        new Invitation
                        {
                            Email = email,
                            RoleId = roleId.Value,
                            TokenHash = AuthHelpers.Sha256Hex(token),
                            ExpiresAt = now.AddHours(72)
                        });

                    await bound.SaveChangesAsync();

                    var webBaseUrl =
                        configuration["Email:WebBaseUrl"]
                        ?? "http://localhost:3000";

                    var link =
                        $"{webBaseUrl}/register?invite={token}" +
                        $"&tenant={tenant.Identifier}";

                    await emailSender.SendAsync(
                        email,
                        "You're invited to join a Tessera workspace",
                        $"<p>Click <a href=\"{link}\">here</a> to accept " +
                        "your invitation. It expires in 3 days.</p>");
                }

                if (transaction is not null)
                {
                    await transaction.CommitAsync();
                }

                return Results.Ok(
                    new
                    {
                        message =
                            "If this email is not already a member, an " +
                            "invitation has been sent."
                    });
            })
            .WithName("AdminInviteTenantUser");

        // ============================================================
        // Envelope Management
        // ============================================================

        var envelopes = app.MapGroup("/admin/envelopes")
            .RequireAuthorization("SuperAdminOnly");

        // ------------------------------------------------------------
        // List Envelopes
        // ------------------------------------------------------------

        envelopes.MapGet(
            "/",
            async (AppDbContext db) =>
            {
                var result = await db.Envelopes
                    .AsNoTracking()
                    .Include(e => e.Roles)
                    .Select(e => new
                    {
                        e.Id,
                        e.Name,
                        e.Description,
                        e.CreatedAt,

                        Roles = e.Roles
                            .OrderBy(r => r.Name)
                            .Select(r => new
                            {
                                r.Name,
                                r.Actions
                            })
                    })
                    .OrderBy(e => e.Name)
                    .ToListAsync();

                return Results.Ok(result);
            })
            .WithName("AdminListEnvelopes");

        // ------------------------------------------------------------
        // Create Envelope
        // ------------------------------------------------------------

        envelopes.MapPost(
            "/",
            async (
                UpsertEnvelopeRequest request,
                AppDbContext db) =>
            {
                var error = ValidateEnvelopeRequest(request);

                if (error is not null)
                {
                    return Results.BadRequest(
                        new { error });
                }

                var envelope = new Envelope
                {
                    Name = request.Name.Trim(),
                    Description = request.Description?.Trim()
                };

                foreach (var role in request.Roles!)
                {
                    envelope.Roles.Add(
                        new AppRole
                        {
                            Name = role.Name.Trim(),
                            Actions = role.Actions
                                .Select(action => action.Trim())
                                .Distinct()
                                .ToList()
                        });
                }

                db.Envelopes.Add(envelope);

                await db.SaveChangesAsync();

                return Results.Created(
                    $"/admin/envelopes/{envelope.Id}",
                    envelope);
            })
            .WithName("AdminCreateEnvelope");

        // ------------------------------------------------------------
        // Update Envelope
        // ------------------------------------------------------------

        envelopes.MapPut(
            "/{id:guid}",
            async (
                Guid id,
                UpsertEnvelopeRequest request,
                AppDbContext db) =>
            {
                var envelope = await db.Envelopes
                    .Include(e => e.Roles)
                    .FirstOrDefaultAsync(e => e.Id == id);

                if (envelope is null)
                {
                    return Results.NotFound();
                }

                var error = ValidateEnvelopeRequest(request);

                if (error is not null)
                {
                    return Results.BadRequest(
                        new { error });
                }

                envelope.Name = request.Name.Trim();
                envelope.Description =
                    request.Description?.Trim();

                // Replace the entire role set.
                db.AppRoles.RemoveRange(envelope.Roles);

                foreach (var role in request.Roles!)
                {
                    envelope.Roles.Add(
                        new AppRole
                        {
                            EnvelopeId = envelope.Id,
                            Name = role.Name.Trim(),
                            Actions = role.Actions
                                .Select(action => action.Trim())
                                .Distinct()
                                .ToList()
                        });
                }

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
                                "This envelope was modified by someone else. " +
                                "Reload and try again."
                        });
                }

                return Results.Ok(envelope);
            })
            .WithName("AdminUpdateEnvelope");

        // ------------------------------------------------------------
        // Delete Envelope
        // ------------------------------------------------------------

        envelopes.MapDelete(
            "/{id:guid}",
            async (
                Guid id,
                AppDbContext db) =>
            {
                var envelope = await db.Envelopes
                    .FirstOrDefaultAsync(e => e.Id == id);

                if (envelope is null)
                {
                    return Results.NotFound();
                }

                // Unassign the envelope from all tenants using it
                // before deleting the envelope itself.
                var assignedTenants = await db.Tenants
                    .Where(t => t.EnvelopeId == envelope.Id)
                    .ToListAsync();

                foreach (var tenant in assignedTenants)
                {
                    tenant.EnvelopeId = null;
                }

                db.Envelopes.Remove(envelope);

                await db.SaveChangesAsync();

                return Results.NoContent();
            })
            .WithName("AdminDeleteEnvelope");
    }

    // ================================================================
    // Helpers
    // ================================================================

    /// <summary>
    /// Copies the roles of <paramref name="envelopeId"/> into the tenant's
    /// own role set. Runs on a tenant-bound context because superadmin
    /// requests have no tenant context and Finbuckle's EnforceMultiTenant
    /// requires one to stamp TenantId on insert. Merge semantics: roles the
    /// tenant already has (by name) are left untouched.
    /// </summary>
    private static async Task CopyEnvelopeRolesToTenantAsync(
        AppDbContext db,
        HttpContext http,
        string tenantId,
        Guid envelopeId)
    {
        var envelope = await db.Envelopes
            .AsNoTracking()
            .Include(e => e.Roles)
            .FirstOrDefaultAsync(e => e.Id == envelopeId);

        if (envelope?.Roles.Count == 0)
        {
            return;
        }

        await using var bound =
            MultiTenantDbContext.Create<AppDbContext, Tenant>(
                new Tenant
                {
                    Id = tenantId
                },
                http.RequestServices);

        var existingNames = await bound.TenantRoles
            .Select(r => r.Name)
            .ToListAsync();

        TenantRoleSeeder.CopyFromEnvelope(bound, envelope!, existingNames);

        // The platform-managed Superadmin role is never part of the
        // template — ensure it exists alongside the copies.
        TenantRoleSeeder.EnsureSuperadmin(bound);

        await bound.SaveChangesAsync();
    }

    private static bool IsValidIdentifier(string identifier)
    {
        return !string.IsNullOrWhiteSpace(identifier) &&
               identifier.Length <= 200 &&
               identifier.All(
                   character =>
                       char.IsAsciiLetterLower(character) ||
                       char.IsDigit(character) ||
                       character == '-');
    }

    private static bool IsValidEmail(string email)
    {
        try
        {
            var address = new System.Net.Mail.MailAddress(email);
            return address.Address == email;
        }
        catch
        {
            return false;
        }
    }

    private static string? ValidateEnvelopeRequest(
        UpsertEnvelopeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return "Envelope name is required.";
        }

        if (request.Roles is null || request.Roles.Count == 0)
        {
            return "An envelope must contain at least one role.";
        }

        var seenRoleNames =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var role in request.Roles)
        {
            if (string.IsNullOrWhiteSpace(role.Name))
            {
                return "Role names cannot be empty.";
            }

            var roleName = role.Name.Trim();

            if (string.Equals(
                    roleName,
                    Roles.TenantSuperadmin,
                    StringComparison.OrdinalIgnoreCase))
            {
                return "The role name 'Superadmin' is reserved for the " +
                    "platform-managed workspace owner role.";
            }

            if (!seenRoleNames.Add(roleName))
            {
                return $"Duplicate role name: {roleName}";
            }
        }

        return null;
    }
}

// ================================================================
// Request DTOs
// ================================================================

public record CreateTenantRequest(
    string Identifier,
    string Name,
    Guid? EnvelopeId = null);

public record UpdateTenantRequest(
    string Identifier,
    string Name,
    Guid? EnvelopeId = null);

public record UpdateTenantStatusRequest(string Status);

public record PlatformInviteRequest(string Email);

public record UpsertEnvelopeRequest(
    string Name,
    string? Description,
    List<RoleRequest> Roles);

public record RoleRequest(
    string Name,
    List<string> Actions);
