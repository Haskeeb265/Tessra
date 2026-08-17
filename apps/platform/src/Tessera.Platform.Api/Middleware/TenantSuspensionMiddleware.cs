using Finbuckle.MultiTenant.AspNetCore.Extensions;

using Tessera.Platform.Domain.Models;

namespace Tessera.Platform.Api.Middleware;

/// <summary>
/// Blocks all tenant-scoped requests (including /auth login/register) for
/// suspended tenants (B3). Runs right after <c>UseMultiTenant()</c> so the
/// resolved tenant's <see cref="TenantStatus"/> is available. Platform-level
/// paths (/admin, /health, /ready, /tenants, /openapi) are skipped: even if
/// a client sends a stray X-Tenant-Id header, superadmin and health requests
/// must never be blocked by tenant state.
/// </summary>
public class TenantSuspensionMiddleware
{
    private static readonly PathString[] ExcludedPaths =
    [
        "/health",
        "/ready",
        "/openapi",
        "/admin",
        "/tenants"
    ];

    private readonly RequestDelegate _next;

    public TenantSuspensionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsExcludedPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var tenant = context.GetMultiTenantContext<Tenant>()?.TenantInfo;

        if (tenant is not null && tenant.Status == TenantStatus.Suspended)
        {
            var response = new ApiErrorResponse
            {
                StatusCode = StatusCodes.Status403Forbidden,
                Message =
                    "This workspace is suspended. Contact support for help.",
                TraceId = context.TraceIdentifier,
                Timestamp = DateTime.UtcNow
            };

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = StatusCodes.Status403Forbidden;

            await context.Response.WriteAsJsonAsync(response);

            return;
        }

        await _next(context);
    }

    private static bool IsExcludedPath(PathString requestPath)
    {
        return ExcludedPaths.Any(
            excludedPath =>
                requestPath.StartsWithSegments(
                    excludedPath,
                    StringComparison.OrdinalIgnoreCase));
    }
}
