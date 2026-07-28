using System.Diagnostics;
using System.Security.Claims;
using Tessra.Platform.Domain.Models;

namespace Tessra.Platform.Api.Middleware;

/// <summary>
/// Middleware that runs after authentication and validates that the
/// <c>tenant_identifier</c> claim in the JWT matches the <c>X-Tenant-Id</c>
/// request header.
///
/// IMPORTANT: We compare against the <c>tenant_identifier</c> claim (not
/// <c>tenant_id</c>) because the JWT stores the external identifier
/// (e.g. "alpha-corp") in that claim, which is the same value the client
/// sends in the X-Tenant-Id header. The <c>tenant_id</c> claim holds the
/// internal database ID (e.g. "alpha"), which would never match the header.
///
/// This prevents a cross-tenant token attack: a user who obtains a valid
/// JWT for Tenant A cannot reuse it to access Tenant B's data by simply
/// changing the X-Tenant-Id header.
///
/// If the values don't match, a 403 Forbidden is returned immediately.
/// Unauthenticated requests (no JWT) are passed through — the
/// authorization middleware handles those.
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
        // If the request is not authenticated (no valid JWT), there's nothing
        // to validate. The authorization middleware (which runs next) will
        // handle protected endpoints by returning 401.
        if (context.User.Identity?.IsAuthenticated == true)
        {
            // Compare tenant_identifier (external name like "alpha-corp")
            // against the header, NOT tenant_id (internal ID like "alpha").
            var tokenTenantId = context.User.FindFirstValue("tenant_identifier");
            var headerTenantId = context.Request.Headers["X-Tenant-Id"].FirstOrDefault();

            // Both should be present at this point — the JWT was issued with
            // tenant_id, and the TenantValidationMiddleware already enforced
            // that X-Tenant-Id exists. This is a defence-in-depth check.
            if (!string.IsNullOrEmpty(tokenTenantId) &&
                !string.IsNullOrEmpty(headerTenantId) &&
                !string.Equals(tokenTenantId, headerTenantId, StringComparison.OrdinalIgnoreCase))
            {
                var response = new ApiErrorResponse
                {
                    StatusCode = StatusCodes.Status403Forbidden,
                    Message = "The tenant in your authentication token does not match the tenant specified in the X-Tenant-Id header.",
                    TraceId = Activity.Current?.Id ?? context.TraceIdentifier,
                    Timestamp = DateTime.UtcNow
                };

                context.Response.ContentType = "application/json";
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(response);
                return;
            }
        }

        await _next(context);
    }
}
