using Microsoft.EntityFrameworkCore;

using Tessera.Platform.Api.Data;
using Tessera.Platform.Domain.Models;

namespace Tessera.Platform.Api.Services;

/// <summary>
/// Seeds a tenant's own role set from the assigned envelope (template).
///
/// The <paramref name="db"/> context MUST be bound to the target tenant —
/// either the request's tenant-scoped context (registration bootstrap) or
/// a <c>MultiTenantDbContext.Create</c> bound context (superadmin actions,
/// which have no tenant context of their own). Finbuckle's
/// EnforceMultiTenant stamps the TenantId on save.
/// </summary>
public static class TenantRoleSeeder
{
    /// <summary>
    /// Copies the envelope's roles into the tenant's role set. Roles that
    /// already exist in the tenant by name are skipped (merge semantics) so
    /// re-assigning an envelope never clobbers tenant-owned edits.
    /// </summary>
    public static void CopyFromEnvelope(
        AppDbContext db,
        Envelope envelope,
        IEnumerable<string>? existingNames = null)
    {
        var existing = new HashSet<string>(
            existingNames ?? db.TenantRoles.Select(r => r.Name).ToList(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var role in envelope.Roles)
        {
            if (existing.Contains(role.Name))
            {
                continue;
            }

            db.TenantRoles.Add(
                new TenantRole
                {
                    Name = role.Name,
                    Actions = role.Actions.ToList(),
                    EnvelopeRoleId = role.Id
                });
        }
    }

    /// <summary>Seeds the built-in Admin/User roles when no envelope is assigned.</summary>
    public static void AddBuiltIns(
        AppDbContext db,
        IEnumerable<string>? existingNames = null)
    {
        var existing = new HashSet<string>(
            existingNames ?? db.TenantRoles.Select(r => r.Name).ToList(),
            StringComparer.OrdinalIgnoreCase);

        if (!existing.Contains(Roles.Admin))
        {
            db.TenantRoles.Add(
                new TenantRole
                {
                    Name = Roles.Admin,
                    Actions =
                    [
                        ActionCatalog.ViewWidgets,
                        ActionCatalog.ManageUsers,
                        ActionCatalog.CreateWidget,
                        ActionCatalog.EditWidget,
                        ActionCatalog.DeleteWidget
                    ]
                });
        }

        if (!existing.Contains(Roles.User))
        {
            db.TenantRoles.Add(
                new TenantRole
                {
                    Name = Roles.User,
                    Actions = [ActionCatalog.ViewWidgets]
                });
        }
    }

    /// <summary>
    /// Ensures the tenant has roles: copies from the assigned envelope if it
    /// has roles, otherwise seeds the built-ins — and always ensures the
    /// platform-managed <c>Superadmin</c> role exists. Used wherever a tenant
    /// must have assignable roles (invitation creation, registration, legacy
    /// promote).
    /// </summary>
    public static async Task EnsureTenantRolesAsync(
        AppDbContext db,
        Guid? envelopeId)
    {
        var existingNames = await db.TenantRoles
            .AsNoTracking()
            .Select(r => r.Name)
            .ToListAsync();

        if (existingNames.Count == 0)
        {
            if (envelopeId is Guid id)
            {
                var envelope = await db.Envelopes
                    .AsNoTracking()
                    .Include(e => e.Roles)
                    .FirstOrDefaultAsync(e => e.Id == id);

                if (envelope?.Roles.Count > 0)
                {
                    CopyFromEnvelope(db, envelope, existingNames);
                    await db.SaveChangesAsync();

                    existingNames = await db.TenantRoles
                        .AsNoTracking()
                        .Select(r => r.Name)
                        .ToListAsync();
                }
            }

            AddBuiltIns(db, existingNames);
        }

        EnsureSuperadmin(db);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Adds the platform-managed <c>Superadmin</c> role (all actions,
    /// <see cref="TenantRole.IsSystem"/>) when the tenant does not have one.
    /// The platform bootstrap invitation grants this role.
    /// </summary>
    public static void EnsureSuperadmin(AppDbContext db)
    {
        if (db.TenantRoles.Any(r => r.IsSystem))
        {
            return;
        }

        db.TenantRoles.Add(
            new TenantRole
            {
                Name = Roles.TenantSuperadmin,
                Actions = ActionCatalog.All.ToList(),
                IsSystem = true
            });
    }
}
