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
        // ─── Superadmin auth (public, no tenant required) ──────────────
        app.MapGroup("/admin/auth")
            .MapPost("/login", async (
                LoginRequest request,
                AdminAuthService authService) =>
            {
                var result = await authService.LoginAsync(request.Email, request.Password);

                return result.IsSuccess
                    ? Results.Ok(new TokenResponse(result.AccessToken!, result.RefreshToken!))
                    : Results.Unauthorized();
            })
            .WithName("AdminLogin");

        // ─── Tenant management ─────────────────────────────────────────
        var tenants = app.MapGroup("/admin/tenants")
            .RequireAuthorization("SuperAdminOnly");

        tenants.MapGet("/", async (AppDbContext db) =>
        {
            var result = await db.Tenants
                .AsNoTracking()
                .Select(t => new
                {
                    t.Id,
                    t.Identifier,
                    t.Name,
                    t.EnvelopeId,
                    EnvelopeName = t.EnvelopeId == null
                        ? null
                        : db.Envelopes.Where(e => e.Id == t.EnvelopeId).Select(e => e.Name).FirstOrDefault(),
                    t.CreatedAt
                })
                .OrderBy(t => t.Name)
                .ToListAsync();

            return Results.Ok(result);
        })
        .WithName("AdminListTenants");

        tenants.MapPost("/", async (CreateTenantRequest request, AppDbContext db) =>
        {
            var identifier = request.Identifier.Trim().ToLowerInvariant();

            if (!IsValidIdentifier(identifier))
            {
                return Results.BadRequest(new { error = "Identifier must be lowercase letters, numbers, and hyphens only." });
            }

            if (await db.Tenants.AnyAsync(t => t.Identifier == identifier))
            {
                return Results.BadRequest(new { error = "A tenant with this identifier already exists." });
            }

            if (request.EnvelopeId is not null &&
                !await db.Envelopes.AnyAsync(e => e.Id == request.EnvelopeId))
            {
                return Results.BadRequest(new { error = "The specified envelope does not exist." });
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

            return Results.Created($"/admin/tenants/{tenant.Id}", tenant);
        })
        .WithName("AdminCreateTenant");

        tenants.MapPut("/{id}", async (string id, UpdateTenantRequest request, AppDbContext db) =>
        {
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id);
            if (tenant is null)
            {
                return Results.NotFound();
            }

            var newIdentifier = request.Identifier.Trim().ToLowerInvariant();
            if (!IsValidIdentifier(newIdentifier))
            {
                return Results.BadRequest(new { error = "Identifier must be lowercase letters, numbers, and hyphens only." });
            }

            if (newIdentifier != tenant.Identifier &&
                await db.Tenants.AnyAsync(t => t.Identifier == newIdentifier))
            {
                return Results.BadRequest(new { error = "A tenant with this identifier already exists." });
            }

            if (request.EnvelopeId is not null &&
                !await db.Envelopes.AnyAsync(e => e.Id == request.EnvelopeId))
            {
                return Results.BadRequest(new { error = "The specified envelope does not exist." });
            }

            // Users store the tenant Id internally; keep it stable even if
            // the display identifier changes.
            tenant.Identifier = newIdentifier;
            tenant.Name = request.Name.Trim();
            tenant.EnvelopeId = request.EnvelopeId;

            await db.SaveChangesAsync();

            return Results.Ok(tenant);
        })
        .WithName("AdminUpdateTenant");

        tenants.MapDelete("/{id}", async (string id, AppDbContext db, HttpContext http) =>
        {
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id);
            if (tenant is null)
            {
                return Results.NotFound();
            }

            // Cascade: remove the tenant's users and refresh tokens, then the
            // tenant itself. On PostgreSQL we use raw SQL (like the seed) to
            // bypass Finbuckle's EnforceMultiTenant, which requires a tenant
            // context that superadmin requests don't have.
            if (db.Database.IsRelational())
            {
                await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM \"Users\" WHERE \"TenantId\" = {0}", tenant.Id);
                await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM \"RefreshTokens\" WHERE \"TenantId\" = {0}", tenant.Id);
                await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM \"Tenants\" WHERE \"Id\" = {0}", tenant.Id);
                return Results.NoContent();
            }

            // InMemory provider: bind a throwaway context to the tenant being
            // deleted so EnforceMultiTenant accepts the row removals.
            await using var bound = MultiTenantDbContext.Create<AppDbContext, Tenant>(
                new Tenant { Id = tenant.Id }, http.RequestServices);
            bound.Users.RemoveRange(
                await bound.Users.IgnoreQueryFilters().Where(u => u.TenantId == tenant.Id).ToListAsync());
            bound.RefreshTokens.RemoveRange(
                await bound.RefreshTokens.IgnoreQueryFilters().Where(rt => rt.TenantId == tenant.Id).ToListAsync());
            await bound.SaveChangesAsync();

            db.Tenants.Remove(tenant);
            await db.SaveChangesAsync();

            return Results.NoContent();
        })
        .WithName("AdminDeleteTenant");

        // ─── Envelope management ───────────────────────────────────────
        var envelopes = app.MapGroup("/admin/envelopes")
            .RequireAuthorization("SuperAdminOnly");

        envelopes.MapGet("/", async (AppDbContext db) =>
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
                    Roles = e.Roles.OrderBy(r => r.Name).Select(r => new
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

        envelopes.MapPost("/", async (UpsertEnvelopeRequest request, AppDbContext db) =>
        {
            var error = ValidateEnvelopeRequest(request);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            var envelope = new Envelope
            {
                Name = request.Name.Trim(),
                Description = request.Description?.Trim()
            };

            foreach (var role in request.Roles!)
            {
                envelope.Roles.Add(new AppRole
                {
                    Name = role.Name.Trim(),
                    Actions = role.Actions.Select(a => a.Trim()).Distinct().ToList()
                });
            }

            db.Envelopes.Add(envelope);
            await db.SaveChangesAsync();

            return Results.Created($"/admin/envelopes/{envelope.Id}", envelope);
        })
        .WithName("AdminCreateEnvelope");

        envelopes.MapPut("/{id:guid}", async (Guid id, UpsertEnvelopeRequest request, AppDbContext db) =>
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
                return Results.BadRequest(new { error });
            }

            envelope.Name = request.Name.Trim();
            envelope.Description = request.Description?.Trim();

            // Replace the role set wholesale (cascade deletes old roles).
            db.AppRoles.RemoveRange(envelope.Roles);
            foreach (var role in request.Roles!)
            {
                envelope.Roles.Add(new AppRole
                {
                    EnvelopeId = envelope.Id,
                    Name = role.Name.Trim(),
                    Actions = role.Actions.Select(a => a.Trim()).Distinct().ToList()
                });
            }

            await db.SaveChangesAsync();

            return Results.Ok(envelope);
        })
        .WithName("AdminUpdateEnvelope");

        envelopes.MapDelete("/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var envelope = await db.Envelopes.FirstOrDefaultAsync(e => e.Id == id);
            if (envelope is null)
            {
                return Results.NotFound();
            }

            // Unassign the envelope from any tenants that use it, then delete.
            var assigned = await db.Tenants.Where(t => t.EnvelopeId == envelope.Id).ToListAsync();
            foreach (var assignedTenant in assigned)
            {
                assignedTenant.EnvelopeId = null;
            }

            db.Envelopes.Remove(envelope);
            await db.SaveChangesAsync();

            return Results.NoContent();
        })
        .WithName("AdminDeleteEnvelope");
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private static bool IsValidIdentifier(string identifier) =>
        !string.IsNullOrWhiteSpace(identifier) &&
        identifier.Length <= 200 &&
        identifier.All(c => char.IsAsciiLetterLower(c) || char.IsDigit(c) || c == '-');

    private static string? ValidateEnvelopeRequest(UpsertEnvelopeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return "Envelope name is required.";
        }

        if (request.Roles is null || request.Roles.Count == 0)
        {
            return "An envelope must contain at least one role.";
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in request.Roles)
        {
            if (string.IsNullOrWhiteSpace(role.Name))
            {
                return "Role names cannot be empty.";
            }
            if (!seen.Add(role.Name.Trim()))
            {
                return $"Duplicate role name: {role.Name.Trim()}";
            }
        }

        return null;
    }
}

// ─── Request DTOs ──────────────────────────────────────────────────

public record CreateTenantRequest(string Identifier, string Name, Guid? EnvelopeId = null);
public record UpdateTenantRequest(string Identifier, string Name, Guid? EnvelopeId = null);
public record UpsertEnvelopeRequest(string Name, string? Description, List<RoleRequest> Roles);
public record RoleRequest(string Name, List<string> Actions);
