using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;

using Tessera.Platform.Api.Data;
using Tessera.Platform.Api.Services;
using Tessera.Platform.Domain.Models;

namespace Tessera.Platform.Api.Endpoints;

public static class ManifestEndpoints
{
    public static void MapManifestEndpoints(this WebApplication app)
    {
        // MCP tool manifests are tenant-scoped records managed by the
        // workspace admin (requires the manage_tools action). The MCP
        // gateway reads these through the platform API on the tenant's
        // behalf — it does NOT use these dashboard endpoints (docs/mcp.md).
        var group = app.MapGroup("/tenant/manifests").RequireAuthorization();

        // ============================================================
        // Manifest CRUD (action-enforced — see ActionCatalog.ManageTools)
        // ============================================================

        group.MapGet(
            "/",
            async (
                AppDbContext db,
                HttpContext http,
                ClaimsPrincipal user) =>
            {
                var denied = await ActionChecks.RequiresAsync(
                    db, http, user, ActionCatalog.ManageTools);

                if (denied is not null)
                {
                    return denied;
                }

                var manifests = await db.ToolManifests
                    .AsNoTracking()
                    .Where(m => !m.IsDeleted)
                    .OrderBy(m => m.ToolName)
                    .ToListAsync();

                return Results.Ok(manifests.Select(ToResponse));
            })
            .WithName("TenantListManifests");

        group.MapGet(
            "/{id:guid}",
            async (
                Guid id,
                AppDbContext db,
                HttpContext http,
                ClaimsPrincipal user) =>
            {
                var denied = await ActionChecks.RequiresAsync(
                    db, http, user, ActionCatalog.ManageTools);

                if (denied is not null)
                {
                    return denied;
                }

                var manifest = await db.ToolManifests
                    .AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Id == id && !m.IsDeleted);

                return manifest is null
                    ? Results.NotFound()
                    : Results.Ok(ToResponse(manifest));
            })
            .WithName("TenantGetManifest");

        group.MapPost(
            "/",
            async (
                UpsertToolManifestRequest request,
                AppDbContext db,
                HttpContext http,
                ClaimsPrincipal user) =>
            {
                var denied = await ActionChecks.RequiresAsync(
                    db, http, user, ActionCatalog.ManageTools);

                if (denied is not null)
                {
                    return denied;
                }

                var error = ValidateManifestRequest(request);

                if (error is not null)
                {
                    return Results.BadRequest(new { error });
                }

                var toolName = request.ToolName.Trim();

                // Mirrors the filtered unique index
                // (IX_ToolManifests_TenantId_ToolName_OnlyActive): names must
                // be unique among ACTIVE manifests, so a soft-deleted tool
                // frees its name for re-use.
                if (await db.ToolManifests.AnyAsync(
                        m => m.ToolName == toolName && !m.IsDeleted))
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "A tool with this name already exists in " +
                                "this workspace."
                        });
                }

                var manifest = new ToolManifest
                {
                    ToolName = toolName,
                    Description = request.Description.Trim(),
                    InputSchema = request.InputSchema.GetRawText(),
                    Execution = request.Execution.GetRawText(),
                    RequiredScopes = request.RequiredScopes ?? [],
                    RateLimitOverride = string.IsNullOrWhiteSpace(
                        request.RateLimitOverride)
                        ? null
                        : request.RateLimitOverride.Trim()
                };

                db.ToolManifests.Add(manifest);

                try
                {
                    await db.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    // Race with a concurrent create of the same tool name.
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "A tool with this name already exists in " +
                                "this workspace."
                        });
                }

                return Results.Created(
                    $"/tenant/manifests/{manifest.Id}",
                    ToResponse(manifest));
            })
            .WithName("TenantCreateManifest");

        group.MapPut(
            "/{id:guid}",
            async (
                Guid id,
                UpsertToolManifestRequest request,
                AppDbContext db,
                HttpContext http,
                ClaimsPrincipal user) =>
            {
                var denied = await ActionChecks.RequiresAsync(
                    db, http, user, ActionCatalog.ManageTools);

                if (denied is not null)
                {
                    return denied;
                }

                var error = ValidateManifestRequest(request);

                if (error is not null)
                {
                    return Results.BadRequest(new { error });
                }

                var manifest = await db.ToolManifests
                    .FirstOrDefaultAsync(m => m.Id == id && !m.IsDeleted);

                if (manifest is null)
                {
                    return Results.NotFound();
                }

                var toolName = request.ToolName.Trim();

                if (toolName != manifest.ToolName &&
                    await db.ToolManifests.AnyAsync(
                        m => m.ToolName == toolName && !m.IsDeleted))
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "A tool with this name already exists in " +
                                "this workspace."
                        });
                }

                manifest.ToolName = toolName;
                manifest.Description = request.Description.Trim();
                manifest.InputSchema = request.InputSchema.GetRawText();
                manifest.Execution = request.Execution.GetRawText();
                manifest.RequiredScopes = request.RequiredScopes ?? [];
                manifest.RateLimitOverride =
                    string.IsNullOrWhiteSpace(request.RateLimitOverride)
                    ? null
                    : request.RateLimitOverride.Trim();

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
                                "This manifest was modified by someone " +
                                "else. Reload and try again."
                        });
                }
                catch (DbUpdateException)
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "A tool with this name already exists in " +
                                "this workspace."
                        });
                }

                return Results.Ok(ToResponse(manifest));
            })
            .WithName("TenantUpdateManifest");

        group.MapDelete(
            "/{id:guid}",
            async (
                Guid id,
                AppDbContext db,
                HttpContext http,
                ClaimsPrincipal user) =>
            {
                var denied = await ActionChecks.RequiresAsync(
                    db, http, user, ActionCatalog.ManageTools);

                if (denied is not null)
                {
                    return denied;
                }

                var manifest = await db.ToolManifests
                    .FirstOrDefaultAsync(m => m.Id == id && !m.IsDeleted);

                if (manifest is null)
                {
                    return Results.NotFound();
                }

                // Soft delete: the filtered unique index then frees the
                // tool name for re-use, and the gateway stops resolving it.
                manifest.IsDeleted = true;

                await db.SaveChangesAsync();

                return Results.NoContent();
            })
            .WithName("TenantDeleteManifest");
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static object ToResponse(ToolManifest manifest) => new
    {
        manifest.Id,
        manifest.ToolName,
        manifest.Description,
        InputSchema = JsonNode.Parse(manifest.InputSchema),
        Execution = JsonNode.Parse(manifest.Execution),
        manifest.RequiredScopes,
        manifest.RateLimitOverride,
        manifest.CreatedAt
    };

    private static string? ValidateManifestRequest(
        UpsertToolManifestRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ToolName))
        {
            return "Tool name is required.";
        }

        var toolName = request.ToolName.Trim();

        if (toolName.Length > 200)
        {
            return "Tool name must be 200 characters or fewer.";
        }

        // MCP tool names are snake_case identifiers (matches the sample
        // manifests in docs/sample-smb).
        if (!toolName.All(
                c => char.IsAsciiLetterLower(c) ||
                     char.IsAsciiDigit(c) ||
                     c == '_'))
        {
            return "Tool name must be lowercase letters, digits, and " +
                   "underscores only.";
        }

        if (string.IsNullOrWhiteSpace(request.Description))
        {
            return "A tool description is required.";
        }

        if (request.Description.Trim().Length > 1000)
        {
            return "Description must be 1000 characters or fewer.";
        }

        if (request.InputSchema.ValueKind != JsonValueKind.Object)
        {
            return "inputSchema must be a JSON object (JSON Schema).";
        }

        if (request.Execution.ValueKind != JsonValueKind.Object)
        {
            return "execution must be a JSON object " +
                   "({ type, method, url, auth, ... }).";
        }

        if (request.RequiredScopes is not null &&
            request.RequiredScopes.Any(
                s => string.IsNullOrWhiteSpace(s)))
        {
            return "Required scopes cannot be empty strings.";
        }

        if (!string.IsNullOrWhiteSpace(request.RateLimitOverride) &&
            request.RateLimitOverride.Trim().Length > 500)
        {
            return "Rate limit override must be 500 characters or fewer.";
        }

        return null;
    }
}

// ================================================================
// Request DTOs
// ================================================================

/// <summary>
/// Upsert payload for a tenant's MCP tool manifest. <c>InputSchema</c> and
/// <c>Execution</c> are accepted as JSON objects and stored as JSON text on
/// the entity (see <see cref="ToolManifest"/>).
/// </summary>
public record UpsertToolManifestRequest(
    string ToolName,
    string Description,
    JsonElement InputSchema,
    JsonElement Execution,
    List<string>? RequiredScopes = null,
    string? RateLimitOverride = null);