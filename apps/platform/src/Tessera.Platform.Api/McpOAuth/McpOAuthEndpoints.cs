using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;

using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;

using Tessera.Platform.Api.Data;
using Tessera.Platform.Api.Services;
using Tessera.Platform.Domain.Models;

using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Tessera.Platform.Api.McpOAuth;

/// <summary>
/// The MCP OAuth authorization server surface (docs/mcp-auth-platform.md).
///
/// Interactive flows (authorization code + PKCE) are served with the login and
/// consent pages living in the Next.js portal: the AS redirects the browser to
/// /oauth/login and /oauth/consent on the portal, which drives the JSON
/// endpoints below (login, login/mfa, consent) and returns the browser to the
/// /connect/authorize URL.
///
/// The token endpoint re-issues tokens for the code + refresh grants and is
/// handled here per the canonical OpenIddict server pattern.
/// </summary>
public static class McpOAuthEndpoints
{
    public static void MapMcpOAuthEndpoints(this WebApplication app)
    {
        // ============================================================
        // Authorization endpoint (interactive, pass-through)
        // ============================================================

        app.MapMethods(
            "/connect/authorize",
            [HttpMethods.Get, HttpMethods.Post],
            async (
                HttpContext context,
                AppDbContext db,
                McpOAuthService svc,
                IOpenIddictApplicationManager applications,
                IOpenIddictAuthorizationManager authorizations) =>
            {
                var request = context.GetOpenIddictServerRequest()
                    ?? throw new InvalidOperationException(
                        "The OpenID Connect request cannot be retrieved.");

                // The portal consent page redirects here with deny=1 when the
                // user declines — OpenIddict then returns access_denied to the
                // requesting client.
                if (context.Request.Query["deny"] == "1" ||
                    (context.Request.HasFormContentType &&
                     context.Request.Form["deny"] == "1"))
                {
                    return Forbid(
                        context,
                        Errors.AccessDenied,
                        "The resource owner denied the request.");
                }

                // RFC 8707: the tenant MCP URL identifies the target resource
                // (and therefore the tenant) for this authorization.
                var resource = RawResource(context, request);
                var tenantSlug = McpOAuthService.TenantSlugFromResource(resource)
                    ?? context.Request.Headers["X-Tenant-Id"].FirstOrDefault();

                if (string.IsNullOrWhiteSpace(tenantSlug))
                {
                    return Forbid(
                        context,
                        Errors.InvalidRequest,
                        "A tenant resource must be specified " +
                        "(resource=https://…/t/{tenant}/mcp).");
                }

                var tenant = await svc.FindTenantByIdentifierAsync(tenantSlug);

                if (tenant is null || tenant.Status != TenantStatus.Active)
                {
                    return Forbid(
                        context,
                        Errors.InvalidRequest,
                        "This workspace does not exist or is not active.");
                }

                // ---- Interactive authentication (cookie) ----
                var cookie = await svc.ReadCookieUserAsync(context);
                var user = cookie?.User;
                var cookieTenant = cookie?.Tenant;

                var authenticatedForTenant =
                    user is not null &&
                    cookieTenant is not null &&
                    string.Equals(
                        cookieTenant.Identifier,
                        tenant.Identifier,
                        StringComparison.Ordinal);

                if (!authenticatedForTenant ||
                    request.HasPromptValue(PromptValues.Login) ||
                    request.MaxAge is 0)
                {
                    if (request.HasPromptValue(PromptValues.None))
                    {
                        return Forbid(context, Errors.LoginRequired,
                            "The user is not logged in.");
                    }

                    // Send the user to the portal login page; on success the
                    // portal redirects back to this exact URL.
                    var portalLogin =
                        $"{McpOAuthConfig.PortalBaseUrl(_appConfig(context))}" +
                        "/oauth/login?tenant=" +
                        Uri.EscapeDataString(tenant.Identifier) +
                        "&returnUrl=" +
                        Uri.EscapeDataString(context.Request.GetEncodedUrl());

                    return Results.Redirect(portalLogin);
                }

                if (svc.RequiresEmailVerification && !user!.EmailVerified)
                {
                    return Forbid(context, Errors.InvalidGrant,
                        "The account email has not been verified.");
                }

                // ---- Application + consent ----
                var application =
                    await applications.FindByClientIdAsync(request.ClientId!)
                    ?? throw new InvalidOperationException(
                        "Details concerning the calling client application " +
                        "cannot be found.");

                var applicationId =
                    await applications.GetIdAsync(application);

                if (applicationId is null)
                {
                    return Forbid(context, Errors.InvalidClient,
                        "The calling client application has no identifier.");
                }
                var requestedScopes = request.GetScopes();
                var consent = await FindConsentAsync(
                    db, user!.Id, request.ClientId!);

                var granted = consent is not null &&
                    requestedScopes.All(
                        scope => svc.ReadScopes(consent).Contains(scope));

                if (!granted)
                {
                    if (request.HasPromptValue(PromptValues.None))
                    {
                        return Forbid(context, Errors.ConsentRequired,
                            "Interactive user consent is required.");
                    }

                    var portalConsent =
                        $"{McpOAuthConfig.PortalBaseUrl(_appConfig(context))}" +
                        "/oauth/consent?tenant=" +
                        Uri.EscapeDataString(tenant.Identifier) +
                        "&client_id=" +
                        Uri.EscapeDataString(request.ClientId!) +
                        "&scope=" +
                        Uri.EscapeDataString(string.Join(' ', requestedScopes)) +
                        "&returnUrl=" +
                        Uri.EscapeDataString(context.Request.GetEncodedUrl());

                    return Results.Redirect(portalConsent);
                }

                var authorizationId = await GetOrCreateAuthorizationAsync(
                    authorizations,
                    applications,
                    user!.Id.ToString(),
                    applicationId,
                    requestedScopes);

                var identity = svc.BuildTokenIdentity(
                    user, tenant, requestedScopes, resource, authorizationId);

                return Results.SignIn(
                    new ClaimsPrincipal(identity),
                    null,
                    OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            });

        // ============================================================
        // Token endpoint (authorization code + refresh token grants)
        // ============================================================

        app.MapPost(
            "/connect/token",
            async (
                HttpContext context,
                McpOAuthService svc) =>
            {
                var request = context.GetOpenIddictServerRequest()
                    ?? throw new InvalidOperationException(
                        "The OpenID Connect request cannot be retrieved.");

                if (!request.IsAuthorizationCodeGrantType() &&
                    !request.IsRefreshTokenGrantType())
                {
                    return Forbid(context, Errors.UnsupportedGrantType,
                        "The specified grant type is not supported.");
                }

                var result = await context.AuthenticateAsync(
                    OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

                if (result is not { Succeeded: true } ||
                    result.Principal is null)
                {
                    return Forbid(context, Errors.InvalidGrant,
                        "The authorization code or refresh token is invalid.");
                }

                var principal = result.Principal;
                var subject = principal.FindFirstValue(Claims.Subject);

                if (!Guid.TryParse(subject, out var userId))
                {
                    return Forbid(context, Errors.InvalidGrant,
                        "The token is no longer valid.");
                }

                var user = await svc.FindUserByIdAsync(userId);
                var tenantId = principal.FindFirstValue(
                    McpOAuthConstants.ClaimTenantId);

                var tenant = string.IsNullOrEmpty(tenantId)
                    ? null
                    : await svc.FindTenantByIdAsync(tenantId);

                var tokenVersionOk = int.TryParse(
                    principal.FindFirstValue(
                        McpOAuthConstants.ClaimTokenVersion),
                    out var expectedVersion) &&
                    user is not null &&
                    user.TokenVersion == expectedVersion;

                if (user is null || tenant is null ||
                    tenant.Status != TenantStatus.Active ||
                    user.TenantId != tenant.Id ||
                    !tokenVersionOk)
                {
                    return Forbid(context, Errors.InvalidGrant,
                        "The token is no longer valid.");
                }

                if (svc.RequiresEmailVerification && !user.EmailVerified)
                {
                    return Forbid(context, Errors.InvalidGrant,
                        "The account email has not been verified.");
                }

                // Rebuild a fresh identity from the stored token claims so the
                // refreshed access token reflects the current user state.
                var identity = new ClaimsIdentity(
                    principal.Claims,
                    authenticationType:
                        TokenValidationParameters.DefaultAuthenticationType,
                    nameType: Claims.Name,
                    roleType: Claims.Role);

                identity.SetClaim(Claims.Subject, user.Id.ToString());
                identity.SetClaim(Claims.Email, user.Email);
                identity.SetClaim(Claims.Name, user.Email);
                identity.SetClaim(
                    Claims.PreferredUsername, user.Email);
                identity.SetClaim(
                    McpOAuthConstants.ClaimTenantId, tenant.Id);
                identity.SetClaim(
                    McpOAuthConstants.ClaimTenantIdentifier,
                    tenant.Identifier);
                identity.SetClaim(
                    McpOAuthConstants.ClaimTokenVersion,
                    user.TokenVersion.ToString(),
                    ClaimValueTypes.Integer32);
                identity.SetDestinations(McpOAuthService.GetDestinations);

                return Results.SignIn(
                    new ClaimsPrincipal(identity),
                    null,
                    OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            });

        // ============================================================
        // Interactive login (JSON, driven by the portal)
        // ============================================================

        app.MapPost(
            "/connect/login",
            async (
                McpOAuthLoginRequest body,
                HttpContext context,
                McpOAuthService svc) =>
            {
                var tenantIdentifier =
                    context.Request.Headers["X-Tenant-Id"].FirstOrDefault();

                if (string.IsNullOrWhiteSpace(tenantIdentifier))
                {
                    return Results.BadRequest(new
                    {
                        error = "The X-Tenant-Id header is required."
                    });
                }

                var tenant = await svc.FindTenantByIdentifierAsync(
                    tenantIdentifier);

                if (tenant is null ||
                    tenant.Status != TenantStatus.Active)
                {
                    return Results.Unauthorized();
                }

                var user = await svc.FindUserForTenantAsync(
                    tenant.Id, body.Email);

                if (user is null ||
                    !svc.VerifyPassword(user, body.Password))
                {
                    return Results.Unauthorized();
                }

                if (svc.RequiresEmailVerification && !user.EmailVerified)
                {
                    return Results.BadRequest(new
                    {
                        error = "Please verify your email address " +
                                "before logging in."
                    });
                }

                if (user.MfaEnabled)
                {
                    return Results.Ok(new
                    {
                        mfaRequired = true,
                        mfaToken = svc.CreateMfaToken(user, tenant.Identifier)
                    });
                }

                await svc.SignInCookieAsync(context, user, tenant);

                return Results.Ok(new { ok = true });
            });

        app.MapPost(
            "/connect/login/mfa",
            async (
                McpOAuthMfaRequest body,
                HttpContext context,
                McpOAuthService svc) =>
            {
                var read = svc.ReadMfaToken(body.MfaToken);

                if (read is null || read.Value.User is null ||
                    string.IsNullOrEmpty(read.Value.TenantIdentifier) ||
                    !read.Value.User.MfaEnabled ||
                    string.IsNullOrEmpty(read.Value.User.MfaSecret))
                {
                    return Results.BadRequest(new
                    {
                        error = "This MFA session is invalid or expired."
                    });
                }

                var (user, tenantIdentifier) = read.Value;

                if (!TotpService.Validate(user.MfaSecret!, body.Code))
                {
                    return Results.BadRequest(new
                    {
                        error = "Invalid authentication code."
                    });
                }

                var tenant = await svc.FindTenantByIdentifierAsync(
                    tenantIdentifier);

                if (tenant is null ||
                    tenant.Status != TenantStatus.Active ||
                    user.TenantId != tenant.Id)
                {
                    return Results.Unauthorized();
                }

                await svc.SignInCookieAsync(context, user, tenant);

                return Results.Ok(new { ok = true });
            });

        app.MapPost(
            "/connect/logout",
            async (HttpContext context, McpOAuthService svc) =>
            {
                await svc.SignOutCookieAsync(context);
                return Results.Ok(new { ok = true });
            });

        // ============================================================
        // Consent (JSON, driven by the portal)
        // ============================================================

        app.MapGet(
            "/connect/consent-info",
            async (
                HttpContext context,
                McpOAuthService svc,
                IOpenIddictApplicationManager applications,
                IOpenIddictScopeManager scopes) =>
            {
                var tenantIdentifier =
                    context.Request.Headers["X-Tenant-Id"].FirstOrDefault();

                var tenant = string.IsNullOrEmpty(tenantIdentifier)
                    ? null
                    : await svc.FindTenantByIdentifierAsync(tenantIdentifier);

                var clientId = context.Request.Query["client_id"]
                    .FirstOrDefault();
                var scopeParam = context.Request.Query["scope"]
                    .FirstOrDefault();

                var application =
                    string.IsNullOrEmpty(clientId)
                        ? null
                        : await applications.FindByClientIdAsync(clientId);

                if (tenant is null || application is null)
                {
                    return Results.NotFound(new { error = "Not found." });
                }

                var clientName =
                    await applications.GetLocalizedDisplayNameAsync(
                        application, System.Globalization.CultureInfo
                            .CurrentCulture)
                    ?? clientId;

                var requestedScopes =
                    string.IsNullOrWhiteSpace(scopeParam)
                        ? []
                        : scopeParam.Split(' ', StringSplitOptions
                            .RemoveEmptyEntries);

                var scopeInfos = new List<object>();

                foreach (var scope in requestedScopes)
                {
                    var registered = await scopes.FindByNameAsync(scope);
                    var description =
                        registered is null
                            ? null
                            : await scopes.GetLocalizedDescriptionAsync(
                                registered,
                                System.Globalization.CultureInfo
                                    .CurrentCulture);

                    scopeInfos.Add(new
                    {
                        id = scope,
                        description =
                            description ?? DescribeScope(scope)
                    });
                }

                return Results.Ok(new
                {
                    clientId,
                    clientName,
                    tenantId = tenant.Identifier,
                    tenantName = tenant.Name,
                    scopes = scopeInfos
                });
            });

        app.MapPost(
            "/connect/consent",
            async (
                McpOAuthConsentRequest body,
                HttpContext context,
                AppDbContext db,
                McpOAuthService svc) =>
            {
                var cookie = await svc.ReadCookieUserAsync(context);

                if (cookie is null)
                {
                    return Results.Unauthorized();
                }

                var (user, tenant) = cookie.Value;

                if (body.Scopes is null || body.Scopes.Count == 0 ||
                    string.IsNullOrWhiteSpace(body.ClientId))
                {
                    return Results.BadRequest(new
                    {
                        error = "Client and scopes are required."
                    });
                }

                var consent = await FindConsentAsync(
                    db, user!.Id, body.ClientId);

                if (consent is null)
                {
                    consent = new McpConsent
                    {
                        UserId = user.Id,
                        TenantId = tenant!.Id,
                        TenantIdentifier = tenant.Identifier,
                        ClientId = body.ClientId,
                        ScopesJson = McpOAuthService.SerializeScopes(
                            body.Scopes)
                    };

                    db.McpConsents.Add(consent);
                }
                else
                {
                    var granted = svc.ReadScopes(consent);
                    consent.ScopesJson = McpOAuthService.SerializeScopes(
                        granted.Concat(body.Scopes));
                }

                await db.SaveChangesAsync();

                return Results.Ok(new { ok = true });
            });
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static IConfiguration _appConfig(HttpContext context) =>
        context.RequestServices.GetRequiredService<IConfiguration>();

    /// <summary>Raw RFC 8707 resource parameter (query string or form).</summary>
    private static string? RawResource(
        HttpContext context,
        OpenIddictRequest request)
    {
        var value = context.Request.HasFormContentType
            ? (string?)context.Request.Form["resource"]
            : (string?)context.Request.Query["resource"];

        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        // OpenIddict may surface non-standard params on the request object.
        var raw = request.GetParameter("resource")?.GetRawValue();

        return raw switch
        {
            null => null,
            string single => single,
            JsonElement { ValueKind: JsonValueKind.String } element =>
                element.GetString(),
            JsonElement { ValueKind: JsonValueKind.Array } array
                when array.GetArrayLength() > 0 =>
                array[0].GetString(),
            _ => raw.ToString()
        };
    }

    private static IResult Forbid(
        HttpContext context,
        string error,
        string description)
    {
        var properties = new AuthenticationProperties(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] =
                    error,
                [OpenIddictServerAspNetCoreConstants.Properties
                    .ErrorDescription] = description
            });

        return Results.Forbid(
            properties,
            authenticationSchemes:
                [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }

    private static Task<McpConsent?> FindConsentAsync(
        AppDbContext db,
        Guid userId,
        string clientId)
    {
        return db.McpConsents
            .AsNoTracking()
            .FirstOrDefaultAsync(c =>
                c.UserId == userId &&
                c.ClientId == clientId &&
                c.RevokedAt == null);
    }

    private static async Task<Guid> GetOrCreateAuthorizationAsync(
        IOpenIddictAuthorizationManager authorizations,
        IOpenIddictApplicationManager applications,
        string subject,
        string applicationId,
        IReadOnlyList<string> scopes)
    {
        var existing = await authorizations.FindAsync(
                subject,
                applicationId,
                Statuses.Valid,
                AuthorizationTypes.Permanent,
                scopes.ToImmutableArray())
            .ToListAsync();

        if (existing.Count > 0)
        {
            var id = await authorizations.GetIdAsync(existing[^1]);
            return Guid.Parse(id!);
        }

        var descriptor = new OpenIddictAuthorizationDescriptor
        {
            ApplicationId = applicationId,
            Subject = subject,
            Type = AuthorizationTypes.Permanent
        };

        foreach (var scope in scopes)
        {
            descriptor.Scopes.Add(scope);
        }

        var created = await authorizations.CreateAsync(descriptor);

        var createdId = await authorizations.GetIdAsync(created);
        return Guid.Parse(createdId!);
    }

    private static string DescribeScope(string scope) =>
        scope switch
        {
            McpOAuthConstants.ScopeTools =>
                "Access this workspace's MCP tools through AI assistants.",
            OpenIddictConstants.Scopes.OfflineAccess =>
                "Stay signed in and refresh access when it expires.",
            _ => $"Access scope '{scope}'."
        };
}

// ================================================================
// Request DTOs
// ================================================================

public record McpOAuthLoginRequest(
    string Email,
    string Password,
    string? ReturnUrl = null);

public record McpOAuthMfaRequest(string MfaToken, string Code);

public record McpOAuthConsentRequest(
    string ClientId,
    List<string>? Scopes = null);
