using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

using Tessera.Platform.Api.Data;
using Tessera.Platform.Domain.Models;

namespace Tessera.Platform.Api.Services;

/// <summary>
/// Handles user registration, authentication, password hashing,
/// JWT generation, and refresh token lifecycle.
/// </summary>
public class AuthService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AuthService(
        AppDbContext db,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
    {
        _db = db;
        _configuration = configuration;
        _httpContextAccessor = httpContextAccessor;
    }

    // ============================================================
    // Registration
    // ============================================================

    public async Task<AuthResult> RegisterAsync(string email, string password)
    {
        // Prevent duplicate users within the current tenant.
        var userExists = await _db.Users.AnyAsync(u => u.Email == email);

        if (userExists)
        {
            return AuthResult.Failure(
                "A user with this email already exists.");
        }

        var user = new User
        {
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password)
        };

        // Tenant bootstrap:
        // The first user in an empty workspace becomes its admin.
        if (!await _db.Users.AnyAsync(u => u.Role == Roles.Admin))
        {
            user.Role = Roles.Admin;
        }

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        return await GenerateAuthResultAsync(user);
    }

    // ============================================================
    // Login
    // ============================================================

    public async Task<AuthResult> LoginAsync(string email, string password)
    {
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Email == email);

        if (user is null ||
            !BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
        {
            return AuthResult.Failure("Invalid email or password.");
        }

        return await GenerateAuthResultAsync(user);
    }

    // ============================================================
    // Refresh Token
    // ============================================================

    public async Task<AuthResult> RefreshAsync(string refreshToken)
    {
        var storedToken = await _db.RefreshTokens
            .FirstOrDefaultAsync(rt => rt.Token == refreshToken);

        if (storedToken is null ||
            storedToken.IsRevoked ||
            storedToken.ExpiresAt < DateTime.UtcNow)
        {
            return AuthResult.Failure(
                "Invalid or expired refresh token.");
        }

        // Revoke the existing refresh token before issuing a new one.
        storedToken.IsRevoked = true;

        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Id == storedToken.UserId);

        if (user is null)
        {
            return AuthResult.Failure("User not found.");
        }

        await _db.SaveChangesAsync();

        return await GenerateAuthResultAsync(user);
    }

    // ============================================================
    // Administration
    // ============================================================

    public async Task<AuthResult> PromoteToAdminAsync(string email)
    {
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Email == email);

        if (user is null)
        {
            return AuthResult.Failure("User not found.");
        }

        if (user.Role == Roles.Admin)
        {
            return AuthResult.Failure(
                "User is already an admin.");
        }

        user.Role = Roles.Admin;

        await _db.SaveChangesAsync();

        return AuthResult.SuccessMessage(
            "User promoted to Admin successfully.");
    }

    // ============================================================
    // Authentication Token Generation
    // ============================================================

    private async Task<AuthResult> GenerateAuthResultAsync(User user)
    {
        var accessToken = GenerateAccessToken(user);
        var refreshToken = await GenerateRefreshTokenAsync(user);

        return AuthResult.Success(
            accessToken,
            refreshToken);
    }

    private string GenerateAccessToken(User user)
    {
        var jwtSettings = _configuration.GetSection("Jwt");

        var secretKey = jwtSettings["SecretKey"]!;
        var issuer = jwtSettings["Issuer"]!;
        var audience = jwtSettings["Audience"]!;

        var expirationMinutes = int.Parse(
            jwtSettings["AccessTokenExpirationMinutes"] ?? "15");

        var securityKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(secretKey));

        var credentials = new SigningCredentials(
            securityKey,
            SecurityAlgorithms.HmacSha256);

        var tenantId = user.TenantId;
        var tenantIdentifier = GetCurrentTenantIdentifier();

        var claims = new[]
        {
            new Claim(
                JwtRegisteredClaimNames.Sub,
                user.Id.ToString()),

            new Claim(
                JwtRegisteredClaimNames.Email,
                user.Email),

            new Claim(
                ClaimTypes.Role,
                user.Role),

            new Claim(
                "tenant_id",
                tenantId ?? string.Empty),

            new Claim(
                "tenant_identifier",
                tenantIdentifier ?? string.Empty),

            new Claim(
                JwtRegisteredClaimNames.Jti,
                Guid.NewGuid().ToString()),

            new Claim(
                JwtRegisteredClaimNames.Iat,
                DateTimeOffset.UtcNow
                    .ToUnixTimeSeconds()
                    .ToString(),
                ClaimValueTypes.Integer64)
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(expirationMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler()
            .WriteToken(token);
    }

    private async Task<string> GenerateRefreshTokenAsync(User user)
    {
        var jwtSettings = _configuration.GetSection("Jwt");

        var expirationDays = int.Parse(
            jwtSettings["RefreshTokenExpirationDays"] ?? "7");

        var randomBytes = RandomNumberGenerator.GetBytes(64);
        var token = Convert.ToBase64String(randomBytes);

        var refreshToken = new RefreshToken
        {
            UserId = user.Id,
            Token = token,
            ExpiresAt = DateTime.UtcNow.AddDays(expirationDays),
            TenantId = user.TenantId
        };

        _db.RefreshTokens.Add(refreshToken);

        await _db.SaveChangesAsync();

        return token;
    }

    // ============================================================
    // Tenant Context
    // ============================================================

    private string? GetCurrentTenantIdentifier()
    {
        return _httpContextAccessor.HttpContext?
            .Request
            .Headers["X-Tenant-Id"]
            .FirstOrDefault();
    }
}

// ================================================================
// Authentication Result
// ================================================================

public class AuthResult
{
    public bool IsSuccess { get; private init; }

    public string? AccessToken { get; private init; }

    public string? RefreshToken { get; private init; }

    public string? Message { get; private init; }

    public string? ErrorMessage { get; private init; }

    public static AuthResult Success(
        string accessToken,
        string refreshToken)
    {
        return new AuthResult
        {
            IsSuccess = true,
            AccessToken = accessToken,
            RefreshToken = refreshToken
        };
    }

    public static AuthResult SuccessMessage(string message)
    {
        return new AuthResult
        {
            IsSuccess = true,
            Message = message
        };
    }

    public static AuthResult Failure(string message)
    {
        return new AuthResult
        {
            IsSuccess = false,
            ErrorMessage = message
        };
    }
}