using System.Security.Claims;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

using OpenIddict.Abstractions;

using Tessera.Platform.Api.Data;
using Tessera.Platform.Domain.Models;

using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Tessera.Platform.Api.McpOAuth;

/// <summary>
/// Helpers for the MCP OAuth authorization server:
///  - resolving the tenant from an RFC 8707 resource parameter or header
///  - credential + MFA verification against the tenant-scoped Users table
///  - the interactive cookie (its claims and lifecycle)
///  - building the claims identity OpenIddict turns into tokens
///
/// OAuth endpoints are tenant-independent at the transport level (they are
/// excluded from X-Tenant-Id middleware), so all tenant-scoped reads go
/// through IgnoreQueryFilters() with an explicit TenantId — the same pattern
/// the startup seeder uses outside an HTTP tenant context.
/// </summary>
public class McpOAuthService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly IDataProtector _mfaProtector;

    public McpOAuthService(
        AppDbContext db,
        IConfiguration configuration,
        Microsoft.AspNetCore.DataProtection.IDataProtectionProvider protection)
    {
        _db = db;
        _configuration = configuration;
        _mfaProtector = protection.CreateProtector("Tessera.McpOAuth.MfaStep");
    }

    // ================================================================
    // Tenant resolution
    // ================================================================

    /// <summary>
    /// Parses the tenant slug out of an RFC 8707 resource parameter pointing
    /// at a tenant MCP endpoint (e.g. https://mcp.example.com/t/acme-dental/mcp
    /// → "acme-dental").
    /// </summary>
    public static string? TenantSlugFromResource(string? resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            return null;
        }

        try
        {
            var uri = new Uri(resource, UriKind.Absolute);
            var segments = uri.AbsolutePath.Split(
                '/', StringSplitOptions.RemoveEmptyEntries);

            // /t/{tenant}/mcp
            return segments.Length >= 3 &&
                   segments[0].Equals("t", StringComparison.OrdinalIgnoreCase)
                ? Uri.UnescapeDataString(segments[1])
                : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    public async Task<Tenant?> FindTenantByIdentifierAsync(string identifier)
    {
        return await _db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t =>
                t.Identifier == identifier && !t.IsDeleted);
    }

    public async Task<Tenant?> FindTenantByIdAsync(string tenantId)
    {
        return await _db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId && !t.IsDeleted);
    }

    // ================================================================
    // Credentials + MFA (tenant-scoped Users rows)
    // ================================================================

    public async Task<User?> FindUserForTenantAsync(
        string tenantId,
        string email)
    {
        var normalized = email.Trim().ToLowerInvariant();

        return await _db.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(u =>
                u.TenantId == tenantId &&
                u.Email == normalized &&
                !u.IsDeleted);
    }

    public async Task<User?> FindUserByIdAsync(Guid userId)
    {
        return await _db.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted);
    }

    public bool VerifyPassword(User user, string password) =>
        BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);

    public bool RequiresEmailVerification =>
        _configuration.GetValue<bool>("Auth:RequireEmailVerification");

    /// <summary>Issues the short-lived, protected MFA-step token.</summary>
    public string CreateMfaToken(User user, string tenantIdentifier)
    {
        var payload = JsonSerializer.Serialize(new
        {
            uid = user.Id,
            tid = user.TenantId,
            ten = tenantIdentifier,
            exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()
        });

        return _mfaProtector.Protect(payload);
    }

    /// <summary>
    /// Validates an MFA-step token and returns the user + tenant it was
    /// minted for, or null when invalid/expired.
    /// </summary>
    public (User? User, string? TenantIdentifier)? ReadMfaToken(string token)
    {
        try
        {
            var payload = _mfaProtector.Unprotect(token);
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (root.GetProperty("exp").GetInt64() <
                DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                return null;
            }

            var userId = Guid.Parse(root.GetProperty("uid").GetString()!);
            var tenantIdentifier = root.GetProperty("ten").GetString();

            return (FindUserByIdAsync(userId).GetAwaiter().GetResult(),
                    tenantIdentifier);
        }
        catch
        {
            return null;
        }
    }

    // ================================================================
    // Interactive cookie
    // ================================================================

    public async Task SignInCookieAsync(
        HttpContext context,
        User user,
        Tenant tenant)
    {
        var identity = new ClaimsIdentity(
            McpOAuthConstants.CookieScheme,
            Claims.Name,
            Claims.Role);

        identity.AddClaim(new Claim(Claims.Subject, user.Id.ToString()));
        identity.AddClaim(new Claim(Claims.Email, user.Email));
        identity.AddClaim(new Claim(Claims.Name, user.Email));
        identity.AddClaim(new Claim(
            McpOAuthConstants.ClaimTenantId, tenant.Id));
        identity.AddClaim(new Claim(
            McpOAuthConstants.ClaimTenantIdentifier, tenant.Identifier));
        identity.AddClaim(new Claim(
            McpOAuthConstants.ClaimTokenVersion,
            user.TokenVersion.ToString(),
            ClaimValueTypes.Integer32));

        var properties = new AuthenticationProperties
        {
            IsPersistent = true,
            IssuedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = DateTimeOffset.UtcNow.Add(
                McpOAuthConfig.CookieLifetime(_configuration))
        };

        await context.SignInAsync(
            McpOAuthConstants.CookieScheme,
            new ClaimsPrincipal(identity),
            properties);
    }

    public async Task SignOutCookieAsync(HttpContext context) =>
        await context.SignOutAsync(McpOAuthConstants.CookieScheme);

    /// <summary>
    /// Resolves the signed-in user from the interactive cookie, reloading the
    /// row and enforcing tenant + token_version freshness. Returns null (and
    /// clears the cookie) when stale.
    /// </summary>
    public async Task<(User? User, Tenant? Tenant)?> ReadCookieUserAsync(
        HttpContext context)
    {
        var result = await context.AuthenticateAsync(
            McpOAuthConstants.CookieScheme);

        if (result is not { Succeeded: true })
        {
            return null;
        }

        var principal = result.Principal;

        if (!Guid.TryParse(
                principal.FindFirstValue(Claims.Subject),
                out var userId))
        {
            return null;
        }

        var tenantId = principal.FindFirstValue(
            McpOAuthConstants.ClaimTenantId);
        var tenantIdentifier = principal.FindFirstValue(
            McpOAuthConstants.ClaimTenantIdentifier);

        if (string.IsNullOrEmpty(tenantId) ||
            string.IsNullOrEmpty(tenantIdentifier))
        {
            return null;
        }

        var tenant = await FindTenantByIdAsync(tenantId);

        if (tenant is null ||
            tenant.Status != TenantStatus.Active ||
            !string.Equals(
                tenant.Identifier, tenantIdentifier, StringComparison.Ordinal))
        {
            await SignOutCookieAsync(context);
            return null;
        }

        var user = await FindUserByIdAsync(userId);

        if (user is null ||
            user.TenantId != tenantId ||
            user.TokenVersion != ReadTokenVersion(principal))
        {
            await SignOutCookieAsync(context);
            return null;
        }

        return (user, tenant);
    }

    private static int ReadTokenVersion(ClaimsPrincipal principal) =>
        int.TryParse(
            principal.FindFirstValue(McpOAuthConstants.ClaimTokenVersion),
            out var version)
            ? version
            : -1;

    // ================================================================
    // Consent
    // ================================================================

    public List<string> ReadScopes(McpConsent consent)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(
                       consent.ScopesJson)
                   ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string SerializeScopes(IEnumerable<string> scopes) =>
        JsonSerializer.Serialize(scopes.Distinct().OrderBy(s => s));

    // ================================================================
    // Claims identity used by OpenIddict to mint tokens
    // ================================================================

    /// <summary>
    /// Builds the identity OpenIddict turns into a code/access/refresh token.
    /// The RFC 8707 resource (tenant MCP URL) becomes the token audience.
    /// </summary>
    public ClaimsIdentity BuildTokenIdentity(
        User user,
        Tenant tenant,
        IEnumerable<string> scopes,
        string? resource,
        Guid authorizationId)
    {
        var identity = new ClaimsIdentity(
            TokenValidationParameters.DefaultAuthenticationType,
            Claims.Name,
            Claims.Role);

        identity.SetClaim(Claims.Subject, user.Id.ToString());
        identity.SetClaim(Claims.Email, user.Email);
        identity.SetClaim(Claims.Name, user.Email);
        identity.SetClaim(Claims.PreferredUsername, user.Email);
        identity.SetClaim(McpOAuthConstants.ClaimTenantId, tenant.Id);
        identity.SetClaim(
            McpOAuthConstants.ClaimTenantIdentifier, tenant.Identifier);
        identity.SetClaim(
            McpOAuthConstants.ClaimTokenVersion,
            user.TokenVersion.ToString(),
            ClaimValueTypes.Integer32);

        identity.SetScopes(scopes);

        // RFC 8707: the token's audience is the tenant MCP endpoint.
        if (!string.IsNullOrWhiteSpace(resource))
        {
            identity.SetResources([resource]);
        }

        identity.SetAuthorizationId(authorizationId.ToString());
        identity.SetDestinations(GetDestinations);

        return identity;
    }

    /// <summary>
    /// Routes a claim to the token(s) it belongs in. Adapted from the
    /// canonical OpenIddict server sample.
    /// </summary>
    public static IEnumerable<string> GetDestinations(Claim claim)
    {
        switch (claim.Type)
        {
            case Claims.Name or Claims.PreferredUsername or Claims.Email:
                yield return Destinations.AccessToken;
                yield break;

            case McpOAuthConstants.ClaimTenantId
                or McpOAuthConstants.ClaimTenantIdentifier
                or McpOAuthConstants.ClaimTokenVersion:
                yield return Destinations.AccessToken;
                yield break;

            default:
                yield return Destinations.AccessToken;
                yield break;
        }
    }

    /// <summary>Base64/hex helpers are not needed elsewhere; kept for clarity.</summary>
    internal static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }
}
