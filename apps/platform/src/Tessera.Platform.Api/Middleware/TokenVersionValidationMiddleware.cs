using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using Tessera.Platform.Api.Data;
using Tessera.Platform.Api.Services;
using Tessera.Platform.Domain.Models;

namespace Tessera.Platform.Api.Middleware;

/// <summary>
/// Validates the JWT's <c>token_version</c> claim against the user's current
/// <c>TokenVersion</c> (A5). Every privilege change (role change, password
/// change, MFA change, deletion) bumps the version, so the change takes
/// effect immediately instead of waiting for token expiry.
///
/// Runs after <c>TenantClaimValidationMiddleware</c> so cross-tenant reuse
/// still yields the distinct 403, and only tenant user JWTs carry the claim
/// (superadmin tokens are exempt).
///
/// Authentication/recovery endpoints are skipped: a client may still hold a
/// stale access token (e.g. after a password change or MFA enrollment) when
/// it calls login, refresh, or reset — those must never be blocked by the
/// very session they are meant to replace.
/// </summary>
public class TokenVersionValidationMiddleware
{
    // Anonymous endpoints that must remain reachable even when a stale
    // bearer token is attached to the request.
    private static readonly HashSet<string> SkippedPaths =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "/auth/login",
            "/auth/register",
            "/auth/refresh",
            "/auth/mfa",
            "/auth/logout",
            "/auth/forgot-password",
            "/auth/reset-password",
            "/auth/verify-email"
        };

    private readonly RequestDelegate _next;

    public TokenVersionValidationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        AppDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated == true &&
            !SkippedPaths.Contains(context.Request.Path.Value ?? string.Empty))
        {
            var tokenVersion = context.User.FindFirstValue("token_version");

            if (tokenVersion is not null)
            {
                var userId = AuthHelpers.GetUserId(context.User);

                var user = userId is not null
                    ? await db.Users
                        .AsNoTracking()
                        .FirstOrDefaultAsync(u => u.Id == userId)
                    : null;

                var valid =
                    user is not null &&
                    !user.IsDeleted &&
                    int.TryParse(tokenVersion, out var version) &&
                    version == user.TokenVersion;

                if (!valid)
                {
                    var response = new ApiErrorResponse
                    {
                        StatusCode = StatusCodes.Status401Unauthorized,
                        Message =
                            "Your session is no longer valid. " +
                            "Please log in again.",
                        TraceId = context.TraceIdentifier,
                        Timestamp = DateTime.UtcNow
                    };

                    context.Response.ContentType = "application/json";
                    context.Response.StatusCode =
                        StatusCodes.Status401Unauthorized;

                    await context.Response.WriteAsJsonAsync(response);

                    return;
                }
            }
        }

        await _next(context);
    }
}
