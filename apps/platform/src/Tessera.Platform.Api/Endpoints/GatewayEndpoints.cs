using System.Security.Cryptography;
using System.Text.Json.Nodes;

using Finbuckle.MultiTenant.EntityFrameworkCore;

using Microsoft.EntityFrameworkCore;

using Tessera.Platform.Api.Data;
using Tessera.Platform.Domain.Models;

namespace Tessera.Platform.Api.Endpoints;

/// <summary>
/// Server-to-server endpoints consumed by the Python MCP gateway
/// (apps/mcp-server). These are deliberately NOT the dashboard
/// <c>manage_tools</c> endpoints: they authenticate with a shared gateway
/// API key (<c>Gateway:ApiKey</c>, env <c>Gateway__ApiKey</c>) instead of a
/// user JWT, so the gateway can read any tenant's manifest without holding
/// end-user credentials. See docs/mcp/README.md §11.
/// </summary>
public static class GatewayEndpoints
{
    /// <summary>Header carrying the shared gateway API key.</summary>
    public const string GatewayApiKeyHeader = "X-Gateway-Api-Key";

    public static void MapGatewayEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/internal/gateway");

        // ------------------------------------------------------------
        // Tenant directory — lets the gateway warm/refresh its per-tenant
        // server registry without knowing tenants in advance.
        // ------------------------------------------------------------
        group.MapGet(
            "/tenants",
            async (AppDbContext db, HttpContext http, IConfiguration config) =>
            {
                if (!AuthorizeGateway(http, config))
                {
                    return Results.Unauthorized();
                }

                var tenants = await db.Tenants
                    .AsNoTracking()
                    .Where(t => !t.IsDeleted)
                    .OrderBy(t => t.Identifier)
                    .Select(t => new
                    {
                        id = t.Identifier,
                        name = t.Name,
                        status = t.Status.ToString()
                    })
                    .ToListAsync();

                return Results.Ok(new { tenants });
            })
            .WithName("GatewayListTenants");

        // ------------------------------------------------------------
        // Manifest read — the gateway resolves a tenant's tool catalog
        // from this endpoint and caches it with a TTL.
        //   GET /internal/gateway/manifests?tenant={slug}
        // 200: { tenant_id, tenant_name, status, tools: [...] }
        // 401: bad/missing gateway key · 404: unknown tenant · 403: suspended
        // ------------------------------------------------------------
        group.MapGet(
            "/manifests",
            async (
                string? tenant,
                AppDbContext db,
                HttpContext http,
                IConfiguration config) =>
            {
                if (!AuthorizeGateway(http, config))
                {
                    return Results.Unauthorized();
                }

                if (string.IsNullOrWhiteSpace(tenant))
                {
                    return Results.BadRequest(
                        new { error = "The tenant query parameter is required." });
                }

                var tenantRow = await db.Tenants
                    .AsNoTracking()
                    .FirstOrDefaultAsync(t =>
                        t.Identifier == tenant && !t.IsDeleted);

                if (tenantRow is null)
                {
                    return Results.NotFound(
                        new { error = "This workspace does not exist." });
                }

                if (tenantRow.Status == TenantStatus.Suspended)
                {
                    return Results.Json(
                        new { error = "This workspace is suspended." },
                        statusCode: StatusCodes.Status403Forbidden);
                }

                // ToolManifests is tenant-scoped (Finbuckle global query
                // filter); the gateway has no X-Tenant-Id, so query through
                // a context bound to the resolved tenant (same pattern as
                // the admin tenant-delete path).
                await using var bound =
                    MultiTenantDbContext.Create<AppDbContext, Tenant>(
                        new Tenant { Id = tenantRow.Id },
                        http.RequestServices);

                var manifests = await bound.ToolManifests
                    .AsNoTracking()
                    .Where(m => !m.IsDeleted)
                    .OrderBy(m => m.ToolName)
                    .ToListAsync();

                return Results.Ok(new
                {
                    tenant_id = tenantRow.Identifier,
                    tenant_name = tenantRow.Name,
                    status = tenantRow.Status.ToString(),
                    tools = manifests.Select(ToGatewayTool)
                });
            })
            .WithName("GatewayListManifests");
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static bool AuthorizeGateway(
        HttpContext http,
        IConfiguration configuration)
    {
        var expected = configuration["Gateway:ApiKey"];

        // Fail closed: without a configured key no gateway can authenticate.
        if (string.IsNullOrEmpty(expected))
        {
            return false;
        }

        var provided =
            http.Request.Headers[GatewayApiKeyHeader].FirstOrDefault();

        if (string.IsNullOrEmpty(provided))
        {
            return false;
        }

        // Constant-time comparison so a timing side-channel can't leak the key.
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
        var providedBytes = System.Text.Encoding.UTF8.GetBytes(provided);

        return expectedBytes.Length == providedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(
                   expectedBytes, providedBytes);
    }

    private static object ToGatewayTool(ToolManifest manifest) => new
    {
        tool_name = manifest.ToolName,
        description = manifest.Description,
        input_schema = JsonNode.Parse(manifest.InputSchema),
        execution = JsonNode.Parse(manifest.Execution),
        required_scopes = manifest.RequiredScopes
    };
}