using System.Diagnostics;
using Tessera.Platform.Domain.Models;

namespace Tessera.Platform.Api.Middleware;

/// <summary>
/// Middleware that validates the presence of the X-Tenant-Id header
/// before the request reaches Finbuckle's tenant resolution.
/// Returns a 400 Bad Request with a consistent error response if the header is missing.
/// 
/// Public endpoints (like /health and /openapi) are excluded from validation.
/// </summary>
public class TenantValidationMiddleware
{
    private readonly RequestDelegate _next;

    // Paths that don't require a tenant context:
    // - /health and /openapi are public
    // - /admin is the superadmin area (platform-level, no tenant)
    // - /tenants is a public lookup used by the frontends' workspace picker
    private static readonly PathString[] _excludedPaths =
    [
        "/health",
        "/openapi",
        "/admin",
        "/tenants"
    ];

    public TenantValidationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip validation for public endpoints
        foreach (var excludedPath in _excludedPaths)
        {
            if (context.Request.Path.StartsWithSegments(excludedPath, StringComparison.OrdinalIgnoreCase))
            {
                await _next(context);
                return;
            }
        }

        var tenantHeader = context.Request.Headers["X-Tenant-Id"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(tenantHeader))
        {
            var response = new ApiErrorResponse
            {
                StatusCode = StatusCodes.Status400BadRequest,
                Message = "The X-Tenant-Id header is required. Please include it in your request to identify the tenant.",
                TraceId = Activity.Current?.Id ?? context.TraceIdentifier,
                Timestamp = DateTime.UtcNow
            };

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(response);
            return;
        }

        await _next(context);
    }
}
