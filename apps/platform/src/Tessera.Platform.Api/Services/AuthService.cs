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
/// Handles user registration, authentication, password hashing, JWT
/// generation, refresh token lifecycle, email verification, invitations,
/// password reset, and TOTP MFA.
/// </summary>
public class AuthService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IEmailSender _emailSender;

    public AuthService(
        AppDbContext db,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        IEmailSender emailSender)
    {
        _db = db;
        _configuration = configuration;
        _httpContextAccessor = httpContextAccessor;
        _emailSender = emailSender;
    }

    // ============================================================
    // Registration
    // ============================================================

    /// <summary>
    /// Registers a user in the current tenant by redeeming an invitation
    /// (onboarding is invite-only). The invite is validated (email match,
    /// unused, unexpired) and the user is created with the invite's role —
    /// unless no tenant Superadmin exists yet, in which case the redeemer
    /// gets the platform-managed Superadmin role (first invite in a
    /// workspace gets it). If email verification is required, the user is
    /// created unverified and no tokens are returned — a verification email
    /// is sent instead.
    /// </summary>
    public async Task<AuthResult> RegisterAsync(
        string email,
        string password,
        string? inviteToken = null)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(GetCurrentTenantIdentifier()))
        {
            return AuthResult.Failure("A tenant is required.");
        }

        // Invite-only onboarding (B2): no invite token, no account.
        if (string.IsNullOrEmpty(inviteToken))
        {
            return AuthResult.Failure("Registration failed.");
        }

        var invite = await _db.Invitations
            .FirstOrDefaultAsync(i =>
                i.TokenHash == AuthHelpers.Sha256Hex(inviteToken));

        if (invite is null || invite.UsedAt is not null ||
            invite.ExpiresAt < DateTime.UtcNow ||
            !string.Equals(
                invite.Email,
                normalizedEmail,
                StringComparison.OrdinalIgnoreCase))
        {
            return AuthResult.Failure(
                "This invitation is invalid or has expired.");
        }

        // Duplicate check (app-level; the DB has a filtered unique index too).
        var userExists = await _db.Users.AnyAsync(
            u => u.Email == normalizedEmail && !u.IsDeleted);

        if (userExists)
        {
            return AuthResult.Failure("Registration failed.");
        }

        // ----- Role resolution: first invite in a workspace gets the
        // Superadmin role; a second invite pointing at it falls back to
        // Admin (only one tenant Superadmin, designated by the platform).
        Guid? roleId = invite.RoleId;
        var superadminRole = await _db.TenantRoles
            .FirstOrDefaultAsync(r => r.IsSystem);

        if (superadminRole is not null)
        {
            var hasSuperadmin = await _db.Users.AnyAsync(
                u => u.RoleId == superadminRole.Id && !u.IsDeleted);

            if (!hasSuperadmin)
            {
                roleId = superadminRole.Id;
            }
            else if (roleId == superadminRole.Id)
            {
                roleId = (await _db.TenantRoles
                    .FirstOrDefaultAsync(
                        r => r.Name.ToLower() == Roles.Admin.ToLower()))
                    ?.Id;
            }
        }

        if (roleId is null ||
            !await _db.TenantRoles.AnyAsync(r => r.Id == roleId))
        {
            return AuthResult.Failure(
                "This invitation references a role that no longer exists.");
        }

        var invitedUser = new User
        {
            Email = normalizedEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            RoleId = roleId
        };

        _db.Users.Add(invitedUser);
        invite.UsedAt = DateTime.UtcNow;

        var requireVerification =
            _configuration.GetValue<bool>("Auth:RequireEmailVerification");

        if (requireVerification)
        {
            // Store only the SHA-256 hash of the token; the raw token goes
            // in the emailed link so the database never holds a usable secret.
            var verificationToken = AuthHelpers.NewToken();

            invitedUser.EmailVerified = false;
            invitedUser.VerificationToken =
                AuthHelpers.Sha256Hex(verificationToken);
            invitedUser.VerificationTokenExpiresAt =
                DateTime.UtcNow.AddHours(24);

            await _db.SaveChangesAsync();

            await SendVerificationEmailAsync(invitedUser, verificationToken);

            // Generic response: never reveal whether the email was new.
            return AuthResult.SuccessMessage(
                "If this email is new, a verification link has been sent.");
        }

        invitedUser.EmailVerified = true;
        await _db.SaveChangesAsync();

        return await GenerateAuthResultAsync(invitedUser);
    }

    // ============================================================
    // Login + MFA
    // ============================================================

    public async Task<AuthResult> LoginAsync(string email, string password)
    {
        var user = await _db.Users
            .FirstOrDefaultAsync(
                u => u.Email == email.Trim().ToLowerInvariant() &&
                     !u.IsDeleted);

        if (user is null ||
            !BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
        {
            return AuthResult.Failure("Invalid email or password.");
        }

        var requireVerification =
            _configuration.GetValue<bool>("Auth:RequireEmailVerification");

        if (requireVerification && !user.EmailVerified)
        {
            return AuthResult.Failure(
                "Please verify your email address before logging in.");
        }

        if (user.MfaEnabled)
        {
            return AuthResult.RequireMfa(GenerateMfaToken(user));
        }

        return await GenerateAuthResultAsync(user);
    }

    /// <summary>Second step of an MFA-protected login.</summary>
    public async Task<AuthResult> CompleteMfaLoginAsync(
        string mfaToken,
        string code)
    {
        var user = TryReadMfaToken(mfaToken);

        if (user is null)
        {
            return AuthResult.Failure("This MFA session is invalid or expired.");
        }

        if (!user.MfaEnabled ||
            string.IsNullOrEmpty(user.MfaSecret) ||
            !TotpService.Validate(user.MfaSecret, code))
        {
            return AuthResult.Failure("Invalid authentication code.");
        }

        return await GenerateAuthResultAsync(user);
    }

    // ============================================================
    // Refresh tokens (single-use families with reuse detection)
    // ============================================================

    public async Task<AuthResult> RefreshAsync(string refreshToken)
    {
        var storedToken = await _db.RefreshTokens
            .FirstOrDefaultAsync(rt => rt.Token == refreshToken);

        if (storedToken is null)
        {
            return AuthResult.Failure("Invalid or expired refresh token.");
        }

        var now = DateTime.UtcNow;

        if (storedToken.IsRevoked || storedToken.ExpiresAt < now)
        {
            // Reuse detection: a revoked/expired token being replayed is a
            // sign of theft — revoke the entire token family.
            var family = await _db.RefreshTokens
                .Where(rt => rt.FamilyId == storedToken.FamilyId)
                .ToListAsync();

            foreach (var token in family)
            {
                token.IsRevoked = true;
            }

            await _db.SaveChangesAsync();

            return AuthResult.Failure("Invalid or expired refresh token.");
        }

        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Id == storedToken.UserId &&
                                     !u.IsDeleted);

        if (user is null)
        {
            return AuthResult.Failure("User not found.");
        }

        // Rotate: revoke the presented token, issue a successor in the
        // same family.
        storedToken.IsRevoked = true;

        await _db.SaveChangesAsync();

        return await GenerateAuthResultAsync(user, storedToken.FamilyId);
    }

    /// <summary>Server-side logout: revokes the presented token's whole family.</summary>
    public async Task LogoutAsync(string refreshToken)
    {
        var storedToken = await _db.RefreshTokens
            .FirstOrDefaultAsync(rt => rt.Token == refreshToken);

        if (storedToken is null)
        {
            return;
        }

        var family = await _db.RefreshTokens
            .Where(rt => rt.FamilyId == storedToken.FamilyId)
            .ToListAsync();

        foreach (var token in family)
        {
            token.IsRevoked = true;
        }

        await _db.SaveChangesAsync();
    }

    // ============================================================
    // Password management
    // ============================================================

    /// <summary>Changes the password and revokes every session for the user.</summary>
    public async Task<AuthResult> ChangePasswordAsync(
        Guid userId,
        string currentPassword,
        string newPassword)
    {
        if (newPassword.Length < 8)
        {
            return AuthResult.Failure(
                "Password must be at least 8 characters.");
        }

        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted);

        if (user is null ||
            !BCrypt.Net.BCrypt.Verify(currentPassword, user.PasswordHash))
        {
            return AuthResult.Failure("Invalid password.");
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.TokenVersion++;
        user.PasswordResetToken = null;
        user.PasswordResetTokenExpiresAt = null;

        await RevokeAllRefreshTokensAsync(user.Id);
        await _db.SaveChangesAsync();

        return AuthResult.SuccessMessage("Password changed.");
    }

    /// <summary>Always returns success so email existence is not revealed.</summary>
    public async Task ForgotPasswordAsync(string email)
    {
        var user = await _db.Users
            .FirstOrDefaultAsync(
                u => u.Email == email.Trim().ToLowerInvariant() &&
                     !u.IsDeleted);

        if (user is null)
        {
            return;
        }

        // Store only the SHA-256 hash; the raw token goes in the emailed link.
        var resetToken = AuthHelpers.NewToken();

        user.PasswordResetToken = AuthHelpers.Sha256Hex(resetToken);
        user.PasswordResetTokenExpiresAt = DateTime.UtcNow.AddHours(1);

        await _db.SaveChangesAsync();

        var link =
            $"{GetWebBaseUrl()}/reset-password?token={resetToken}";

        await _emailSender.SendAsync(
            user.Email,
            "Reset your Tessera password",
            $"<p>Click <a href=\"{link}\">here</a> to reset your password. " +
            "This link expires in 1 hour.</p>");
    }

    public async Task<AuthResult> ResetPasswordAsync(
        string token,
        string newPassword)
    {
        if (newPassword.Length < 8)
        {
            return AuthResult.Failure(
                "Password must be at least 8 characters.");
        }

        var user = await _db.Users
            .FirstOrDefaultAsync(u =>
                u.PasswordResetToken == AuthHelpers.Sha256Hex(token));

        if (user is null ||
            user.PasswordResetTokenExpiresAt is null ||
            user.PasswordResetTokenExpiresAt < DateTime.UtcNow)
        {
            return AuthResult.Failure(
                "This reset link is invalid or has expired.");
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.TokenVersion++;
        user.PasswordResetToken = null;
        user.PasswordResetTokenExpiresAt = null;

        await RevokeAllRefreshTokensAsync(user.Id);
        await _db.SaveChangesAsync();

        return AuthResult.SuccessMessage("Password reset. You can now log in.");
    }

    public async Task<AuthResult> VerifyEmailAsync(string token)
    {
        var user = await _db.Users
            .FirstOrDefaultAsync(u =>
                u.VerificationToken == AuthHelpers.Sha256Hex(token));

        if (user is null ||
            user.VerificationTokenExpiresAt is null ||
            user.VerificationTokenExpiresAt < DateTime.UtcNow)
        {
            return AuthResult.Failure(
                "This verification link is invalid or has expired.");
        }

        user.EmailVerified = true;
        user.VerificationToken = null;
        user.VerificationTokenExpiresAt = null;

        await _db.SaveChangesAsync();

        return AuthResult.SuccessMessage("Email verified. You can now log in.");
    }

    // ============================================================
    // MFA enrollment
    // ============================================================

    public async Task<AuthResult> EnrollMfaAsync(Guid userId)
    {
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted);

        if (user is null)
        {
            return AuthResult.Failure("User not found.");
        }

        user.MfaSecret = TotpService.GenerateSecret();
        user.MfaEnabled = false;

        await _db.SaveChangesAsync();

        var uri = TotpService.GenerateOtpAuthUri(user.MfaSecret, user.Email);

        return AuthResult.SuccessMessage(
            $"secret:{user.MfaSecret}|uri:{uri}");
    }

    public async Task<AuthResult> ConfirmMfaAsync(Guid userId, string code)
    {
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted);

        if (user is null || string.IsNullOrEmpty(user.MfaSecret))
        {
            return AuthResult.Failure("MFA is not being enrolled.");
        }

        if (!TotpService.Validate(user.MfaSecret, code))
        {
            return AuthResult.Failure("Invalid authentication code.");
        }

        user.MfaEnabled = true;
        user.TokenVersion++;

        await RevokeAllRefreshTokensAsync(user.Id);
        await _db.SaveChangesAsync();

        return AuthResult.SuccessMessage("MFA enabled.");
    }

    public async Task<AuthResult> DisableMfaAsync(Guid userId, string code)
    {
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted);

        if (user is null || !user.MfaEnabled)
        {
            return AuthResult.Failure("MFA is not enabled.");
        }

        if (string.IsNullOrEmpty(user.MfaSecret) ||
            !TotpService.Validate(user.MfaSecret, code))
        {
            return AuthResult.Failure("Invalid authentication code.");
        }

        user.MfaEnabled = false;
        user.MfaSecret = null;
        user.TokenVersion++;

        await RevokeAllRefreshTokensAsync(user.Id);
        await _db.SaveChangesAsync();

        return AuthResult.SuccessMessage("MFA disabled.");
    }

    // ============================================================
    // Invitations
    // ============================================================

    /// <summary>
    /// Creates an invitation for <paramref name="email"/> with the given
    /// tenant role and emails the invite link. The platform-managed
    /// Superadmin role cannot be assigned here (only the platform
    /// superadmin designates it). Always returns the same generic result
    /// (email existence is not revealed).
    /// </summary>
    public async Task<AuthResult> InviteUserAsync(
        string email,
        Guid roleId,
        int expiresInHours = 72)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();

        var role = await _db.TenantRoles
            .FirstOrDefaultAsync(r => r.Id == roleId);

        if (role is null)
        {
            return AuthResult.Failure("The specified role does not exist.");
        }

        if (role.IsSystem)
        {
            return AuthResult.Failure(
                "The Superadmin role is managed by the platform and " +
                "cannot be assigned here.");
        }

        var userExists = await _db.Users.AnyAsync(
            u => u.Email == normalizedEmail && !u.IsDeleted);

        var token = AuthHelpers.NewToken();

        if (!userExists)
        {
            var invitation = new Invitation
            {
                Email = normalizedEmail,
                RoleId = roleId,
                TokenHash = AuthHelpers.Sha256Hex(token),
                ExpiresAt = DateTime.UtcNow.AddHours(expiresInHours)
            };

            _db.Invitations.Add(invitation);
            await _db.SaveChangesAsync();

            var link =
                $"{GetWebBaseUrl()}/register?invite={token}" +
                $"&tenant={GetCurrentTenantIdentifier()}";

            await _emailSender.SendAsync(
                normalizedEmail,
                "You're invited to join a Tessera workspace",
                $"<p>Click <a href=\"{link}\">here</a> to accept your " +
                "invitation. It expires in 3 days.</p>");
        }

        return AuthResult.SuccessMessage(
            "If this email is not already a member, an invitation has been sent.");
    }

    // ============================================================
    // Administration (legacy promote endpoint)
    // ============================================================

    public async Task<AuthResult> PromoteToAdminAsync(string email)
    {
        var user = await _db.Users
            .FirstOrDefaultAsync(
                u => u.Email == email.Trim().ToLowerInvariant() &&
                     !u.IsDeleted);

        if (user is null)
        {
            return AuthResult.Failure("User not found.");
        }

        await TenantRoleSeeder.EnsureTenantRolesAsync(
            _db,
            await GetTenantEnvelopeIdAsync());

        var adminRole = await _db.TenantRoles
            .FirstOrDefaultAsync(r => r.Name.ToLower() == Roles.Admin.ToLower());

        if (adminRole is null)
        {
            return AuthResult.Failure("No Admin role is available.");
        }

        if (user.RoleId == adminRole.Id)
        {
            return AuthResult.Failure("User is already an admin.");
        }

        user.RoleId = adminRole.Id;
        user.TokenVersion++;

        await RevokeAllRefreshTokensAsync(user.Id);
        await _db.SaveChangesAsync();

        return AuthResult.SuccessMessage(
            "User promoted to Admin successfully.");
    }

    // ============================================================
    // Authentication Token Generation
    // ============================================================

    private async Task<AuthResult> GenerateAuthResultAsync(
        User user,
        Guid? familyId = null)
    {
        var accessToken = await GenerateAccessTokenAsync(user);
        var refreshToken = await GenerateRefreshTokenAsync(user, familyId);

        return AuthResult.Success(accessToken, refreshToken);
    }

    private async Task<string> GenerateAccessTokenAsync(User user)
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

        var roleName = user.RoleId is Guid roleId
            ? await _db.TenantRoles
                .AsNoTracking()
                .Where(r => r.Id == roleId)
                .Select(r => r.Name)
                .FirstOrDefaultAsync()
            : null;

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

            // Role name is informational for the UI; authorization is
            // action-based (see ActionChecks).
            new Claim(
                ClaimTypes.Role,
                roleName ?? string.Empty),

            new Claim(
                "role_id",
                user.RoleId?.ToString() ?? string.Empty),

            new Claim(
                "token_version",
                user.TokenVersion.ToString(),
                ClaimValueTypes.Integer32),

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

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task<string> GenerateRefreshTokenAsync(
        User user,
        Guid? familyId)
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
            FamilyId = familyId ?? Guid.NewGuid(),
            ExpiresAt = DateTime.UtcNow.AddDays(expirationDays),
            TenantId = user.TenantId
        };

        _db.RefreshTokens.Add(refreshToken);

        await _db.SaveChangesAsync();

        return token;
    }

    /// <summary>Short-lived JWT that proves the password step of an MFA login.</summary>
    private string GenerateMfaToken(User user)
    {
        var jwtSettings = _configuration.GetSection("Jwt");

        var secretKey = jwtSettings["SecretKey"]!;
        var issuer = jwtSettings["Issuer"]!;
        var audience = jwtSettings["Audience"]!;

        var mfaMinutes = int.Parse(
            jwtSettings["MfaTokenExpirationMinutes"] ?? "5");

        var securityKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(secretKey));

        var credentials = new SigningCredentials(
            securityKey,
            SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim("mfa", "true"),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(
                JwtRegisteredClaimNames.Iat,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64)
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(mfaMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Validates an MFA token and returns its user, or null.</summary>
    private User? TryReadMfaToken(string mfaToken)
    {
        var jwtSettings = _configuration.GetSection("Jwt");

        var secretKey = jwtSettings["SecretKey"]!;
        var issuer = jwtSettings["Issuer"]!;
        var audience = jwtSettings["Audience"]!;

        var handler = new JwtSecurityTokenHandler();

        try
        {
            var principal = handler.ValidateToken(
                mfaToken,
                new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = issuer,
                    ValidAudience = audience,
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(secretKey)),
                    ClockSkew = TimeSpan.Zero
                },
                out _);

            if (principal.FindFirstValue("mfa") != "true")
            {
                return null;
            }

            var userId = AuthHelpers.GetUserId(principal);

            if (userId is null)
            {
                return null;
            }

            return _db.Users
                .FirstOrDefault(u => u.Id == userId && !u.IsDeleted);
        }
        catch
        {
            return null;
        }
    }

    // ============================================================
    // Helpers
    // ============================================================

    private async Task<Guid?> GetTenantEnvelopeIdAsync()
    {
        var identifier = GetCurrentTenantIdentifier();

        if (string.IsNullOrEmpty(identifier))
        {
            return null;
        }

        return await _db.Tenants
            .AsNoTracking()
            .Where(t => t.Identifier == identifier && !t.IsDeleted)
            .Select(t => t.EnvelopeId)
            .FirstOrDefaultAsync();
    }

    private async Task RevokeAllRefreshTokensAsync(Guid userId)
    {
        var tokens = await _db.RefreshTokens
            .Where(rt => rt.UserId == userId && !rt.IsRevoked)
            .ToListAsync();

        foreach (var token in tokens)
        {
            token.IsRevoked = true;
        }
    }

    private async Task SendVerificationEmailAsync(
        User user,
        string rawToken)
    {
        var link =
            $"{GetWebBaseUrl()}/verify-email?token={rawToken}";

        await _emailSender.SendAsync(
            user.Email,
            "Verify your Tessera email",
            $"<p>Click <a href=\"{link}\">here</a> to verify your email " +
            "address. The link expires in 24 hours.</p>");
    }

    private string GetWebBaseUrl()
    {
        return _configuration["Email:WebBaseUrl"] ?? "http://localhost:3000";
    }

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

    public bool MfaRequired { get; private init; }

    public string? MfaToken { get; private init; }

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

    public static AuthResult RequireMfa(string mfaToken)
    {
        return new AuthResult
        {
            IsSuccess = true,
            MfaRequired = true,
            MfaToken = mfaToken
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
