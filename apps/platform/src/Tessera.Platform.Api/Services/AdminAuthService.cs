using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Tessera.Platform.Api.Data;
using Tessera.Platform.Domain.Models;

namespace Tessera.Platform.Api.Services;

/// <summary>
/// Handles platform-level (superadmin) authentication. Unlike the tenant
/// auth flow, superadmins are not bound to a tenant — their JWTs carry the
/// SuperAdmin role claim and no tenant claims, and they are validated by the
/// <c>SuperAdminOnly</c> policy on /admin endpoints.
/// </summary>
public class AdminAuthService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;

    public AdminAuthService(AppDbContext db, IConfiguration configuration)
    {
        _db = db;
        _configuration = configuration;
    }

    public async Task<AuthResult> LoginAsync(string email, string password)
    {
        var admin = await _db.AdminUsers.FirstOrDefaultAsync(a => a.Email == email);

        if (admin is null || !BCrypt.Net.BCrypt.Verify(password, admin.PasswordHash))
        {
            return AuthResult.Failure("Invalid email or password.");
        }

        return AuthResult.Success(GenerateAccessToken(admin), "");
    }

    private string GenerateAccessToken(AdminUser admin)
    {
        var jwtSettings = _configuration.GetSection("Jwt");
        var secretKey = jwtSettings["SecretKey"]!;
        var issuer = jwtSettings["Issuer"]!;
        var audience = jwtSettings["Audience"]!;
        var expirationHours = int.Parse(jwtSettings["AdminAccessTokenExpirationHours"] ?? "12");

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, admin.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, admin.Email),
            new Claim(ClaimTypes.Role, Roles.SuperAdmin),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.Iat,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64)
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(expirationHours),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
