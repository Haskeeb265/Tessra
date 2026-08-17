using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Tessera.Platform.Api.Services;

public static class AuthHelpers
{
    public static Guid? GetUserId(ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue(JwtRegisteredClaimNames.Sub)
                  ?? user.FindFirstValue(ClaimTypes.NameIdentifier);

        return Guid.TryParse(sub, out var id) ? id : null;
    }

    public static string? GetTenantIdentifier(HttpContext http)
    {
        return http.Request.Headers["X-Tenant-Id"].FirstOrDefault();
    }

    /// <summary>SHA-256 hex digest — used to store tokens (invites, email verification, password reset) without keeping the raw token.</summary>
    public static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>Cryptographically random URL-safe token.</summary>
    public static string NewToken(int bytes = 32)
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
