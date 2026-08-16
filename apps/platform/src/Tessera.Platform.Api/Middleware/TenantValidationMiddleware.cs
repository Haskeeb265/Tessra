using System.Diagnostics;

using Tessera.Platform.Domain.Models;

namespace Tessera.Platform.Api.Middleware;

/// <summary>
/// Validates that the <c>X-Tenant-Id</c> header is present before
/// the request reaches Finbuckle's tenant resolution.
///
/// Requests to public or platform-level endpoints that do not require
/// a tenant context are excluded from validation.
///
/// Returns a <c>400 Bad Request</c> with a consistent
/// <see cref="ApiErrorResponse"/> when the tenant header is missing.
/// </summary>
public class TenantValidationMiddleware
{
    private readonly RequestDelegate _next;

    // ============================================================
    // Tenant-Independent Paths
    // ============================================================

    // These endpoints do not require a tenant context:
    //
    // /health  → Public health checks
    // /openapi → OpenAPI documentation
    // /admin   → Platform-level superadmin endpoints
    // /tenants  → Public tenant/workspace lookup
    private static readonly PathString[] ExcludedPaths =
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

    // ============================================================
    // Request Pipeline
    // ============================================================

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsExcludedPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var tenantIdentifier =
            context.Request.Headers["X-Tenant-Id"]
                .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(tenantIdentifier))
        {
            await WriteBadRequestAsync(context);
            return;
        }

        await _next(context);
    }

    // ============================================================
    // Path Validation
    // ============================================================

    private static bool IsExcludedPath(PathString requestPath)
    {
        return ExcludedPaths.Any(
            excludedPath =>
                requestPath.StartsWithSegments(
                    excludedPath,
                    StringComparison.OrdinalIgnoreCase));
    }

    // ============================================================
    // Error Response
    // ============================================================

    private static async Task WriteBadRequestAsync(
        HttpContext context)
    {
        var response = new ApiErrorResponse
        {
            StatusCode = StatusCodes.Status400BadRequest,
            Message =
                "The X-Tenant-Id header is required. " +
                "Please include it in your request to identify the tenant.",
            TraceId =
                Activity.Current?.Id
                ?? context.TraceIdentifier,
            Timestamp = DateTime.UtcNow
        };

        context.Response.ContentType = "application/json";
        context.Response.StatusCode =
            StatusCodes.Status400BadRequest;

        await context.Response.WriteAsJsonAsync(response);
    }
}