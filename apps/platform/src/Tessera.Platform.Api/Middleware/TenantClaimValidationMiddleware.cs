using System.Diagnostics;
using System.Security.Claims;

using Tessera.Platform.Domain.Models;

namespace Tessera.Platform.Api.Middleware;

/// <summary>
/// Validates that the tenant identifier in the authenticated JWT
/// matches the <c>X-Tenant-Id</c> request header.
///
/// The JWT contains two tenant-related claims:
/// - <c>tenant_identifier</c>: External tenant identifier (e.g. "alpha-corp")
/// - <c>tenant_id</c>: Internal database identifier (e.g. "alpha")
///
/// This middleware compares <c>tenant_identifier</c> with the
/// <c>X-Tenant-Id</c> header because they represent the same external
/// tenant identifier.
///
/// This prevents cross-tenant token attacks where a valid JWT issued
/// for Tenant A is reused with a different <c>X-Tenant-Id</c> header
/// to attempt access to Tenant B's data.
///
/// Unauthenticated requests are passed through so that the authorization
/// middleware can handle protected endpoints and return the appropriate
/// 401 response.
/// </summary>
public class TenantClaimValidationMiddleware
{
    private readonly RequestDelegate _next;

    public TenantClaimValidationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Nothing to validate if the request is unauthenticated.
        // Authorization middleware will handle protected endpoints.
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var tokenTenantIdentifier =
                context.User.FindFirstValue("tenant_identifier");

            // Superadmin JWTs carry no tenant claims — skip the check
            // (platform-level requests are authorized separately via the
            // SuperAdminOnly policy). Only tenant user tokens are compared.
            if (!string.IsNullOrEmpty(tokenTenantIdentifier))
            {
                var headerTenantIdentifier =
                    context.Request.Headers["X-Tenant-Id"]
                        .FirstOrDefault();

                // Both values should normally be present:
                // - The JWT is issued with a tenant_identifier claim.
                // - TenantValidationMiddleware ensures X-Tenant-Id exists.
                //
                // This additional check provides defense in depth.
                var tenantsMatch =
                    !string.IsNullOrEmpty(headerTenantIdentifier) &&
                    string.Equals(
                        tokenTenantIdentifier,
                        headerTenantIdentifier,
                        StringComparison.OrdinalIgnoreCase);

                if (!tenantsMatch)
                {
                var response = new ApiErrorResponse
                {
                    StatusCode = StatusCodes.Status403Forbidden,
                    Message =
                        "The tenant in your authentication token does not " +
                        "match the tenant specified in the X-Tenant-Id header.",
                    TraceId =
                        Activity.Current?.Id
                        ?? context.TraceIdentifier,
                    Timestamp = DateTime.UtcNow
                };

                    context.Response.ContentType = "application/json";
                    context.Response.StatusCode =
                        StatusCodes.Status403Forbidden;

                    await context.Response.WriteAsJsonAsync(response);

                    return;
                }
            }
        }

        await _next(context);
    }
}