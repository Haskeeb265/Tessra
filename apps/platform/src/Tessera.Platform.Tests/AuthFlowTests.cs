using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Configuration;
using Xunit;

using Tessera.Platform.Api.Services;

using static Tessera.Platform.Tests.ApiTestHelpers;

namespace Tessera.Platform.Tests;

public class AuthFlowTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    private readonly HttpClient _client;

    public AuthFlowTests(TestAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static string ExtractLink(string body, string param)
    {
        var match = Regex.Match(
            body,
            $"href=\"[^\"]*\\?[^\"]*{param}=([^&\"]+)\"");

        Assert.True(match.Success, $"No '{param}' link found in: {body}");
        return match.Groups[1].Value;
    }

    // ================================================================
    // Registration & hierarchy
    // ================================================================

    [Fact]
    public async Task Platform_invite_bootstraps_only_one_superadmin()
    {
        // Use a brand-new workspace: alpha-corp is shared by other tests in
        // this class, so its first redemption is not guaranteed to be this one.
        const string tenant = "bootstrap-1";

        var superadmin = await LoginSuperAdminAsync(_client);
        await CreateTenantAsync(_client, superadmin, tenant, tenant);

        var firstInvite = await Auth(_client, superadmin).PostAsJsonAsync(
            $"/admin/tenants/{tenant}/invites",
            new { email = "a1-admin@test.com" });
        Assert.Equal(HttpStatusCode.OK, firstInvite.StatusCode);

        // No second owner invite while the first is still pending.
        var pendingBlocked = await Auth(_client, superadmin).PostAsJsonAsync(
            $"/admin/tenants/{tenant}/invites",
            new { email = "a1-admin2@test.com" });
        Assert.Equal(HttpStatusCode.BadRequest, pendingBlocked.StatusCode);

        var inviteToken = InviteTokenFromEmail(_factory, "a1-admin@test.com");
        var register = await Tenant(_client, tenant).PostAsJsonAsync(
            "/auth/register",
            new
            {
                email = "a1-admin@test.com",
                password = "Password123!",
                inviteToken
            });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var adminPair = await register.Content.ReadFromJsonAsync<TokenPair>();
        var adminToken = adminPair!.AccessToken;

        // Once the owner exists, platform invites stop. The tenant owner must
        // invite the rest of the team from the workspace team page.
        var ownerExistsBlocked = await Auth(_client, superadmin).PostAsJsonAsync(
            $"/admin/tenants/{tenant}/invites",
            new { email = "a1-admin2@test.com" });
        Assert.Equal(HttpStatusCode.BadRequest, ownerExistsBlocked.StatusCode);

        var adminToken2 = await InviteAndRegisterAsync(
            _factory, _client, adminToken, tenant,
            "a1-admin2@test.com", "Password123!", "Admin");

        // The Superadmin invites a plain member with the User role.
        var userToken = await InviteAndRegisterAsync(
            _factory, _client, adminToken, tenant,
            "a1-user@test.com", "Password123!", "User");

        var adminMe = await Tenant(Auth(_client, adminToken), tenant)
            .GetFromJsonAsync<MeInfo>("/tenant/me");
        var admin2Me = await Tenant(Auth(_client, adminToken2), tenant)
            .GetFromJsonAsync<MeInfo>("/tenant/me");
        var userMe = await Tenant(Auth(_client, userToken), tenant)
            .GetFromJsonAsync<MeInfo>("/tenant/me");

        // Superadmin: every action in the catalog.
        Assert.Equal("Superadmin", adminMe?.Role);
        Assert.Contains("manage_users", adminMe?.Actions ?? []);
        Assert.Contains("delete_widget", adminMe?.Actions ?? []);

        // Tenant-side invite → Admin tier, never a second Superadmin.
        Assert.Equal("Admin", admin2Me?.Role);
        Assert.Contains("manage_users", admin2Me?.Actions ?? []);
        Assert.DoesNotContain("Superadmin", admin2Me?.Role);

        // Invited member → User tier.
        Assert.Equal("User", userMe?.Role);
        Assert.DoesNotContain("manage_users", userMe?.Actions ?? []);
    }

    [Fact]
    public async Task Registration_without_invite_is_rejected_and_generic()
    {
        var tenant = await CreateIsolatedTenantAsync("auth-no-invite");

        var response = await Tenant(_client, tenant).PostAsJsonAsync(
            "/auth/register",
            new { email = "a2@test.com", password = "Password123!" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();

        // Invite-only (B2) + no account-enumeration leak (C3).
        Assert.Equal("Registration failed.", body?.Error);

        // Registering with an invite, then re-registering the same email
        // (without a fresh invite) also fails generically.
        await RegisterUserAsync(
            _factory, _client, tenant, "a2@test.com", "Password123!");

        var dup = await Tenant(_client, tenant).PostAsJsonAsync(
            "/auth/register",
            new { email = "a2@test.com", password = "Password123!" });

        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);

        var dupBody = await dup.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal("Registration failed.", dupBody?.Error);
    }

    // ================================================================
    // Refresh tokens: rotation, reuse detection, logout
    // ================================================================

    [Fact]
    public async Task Refresh_rotates_and_reuse_revokes_the_family()
    {
        var tenant = await CreateIsolatedTenantAsync("auth-refresh");

        var login = await Tenant(_client, tenant).PostAsJsonAsync(
            "/auth/login",
            new { email = "a3@test.com", password = "Password123!" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);

        await RegisterUserAsync(
            _factory, _client, tenant, "a3@test.com", "Password123!");

        var pair = await Tenant(_client, tenant).PostAsJsonAsync(
                "/auth/login",
                new { email = "a3@test.com", password = "Password123!" })
            .ContinueWith(t => t.Result.Content.ReadFromJsonAsync<TokenPair>())
            .Unwrap();

        // First refresh succeeds and rotates.
        var refreshed = await Tenant(_client, tenant).PostAsJsonAsync(
            "/auth/refresh",
            new { refreshToken = pair!.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        var newPair = await refreshed.Content.ReadFromJsonAsync<TokenPair>();

        // Replaying the rotated (now revoked) token revokes the family.
        var replay = await Tenant(_client, tenant).PostAsJsonAsync(
            "/auth/refresh",
            new { refreshToken = pair!.RefreshToken });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);

        // The successor token is dead too (family revoked).
        var successor = await Tenant(_client, tenant).PostAsJsonAsync(
            "/auth/refresh",
            new { refreshToken = newPair!.RefreshToken });
        Assert.Equal(HttpStatusCode.BadRequest, successor.StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_refresh_tokens()
    {
        var tenant = await CreateIsolatedTenantAsync("auth-logout");

        await RegisterUserAsync(
            _factory, _client, tenant, "a4@test.com", "Password123!");

        var pair = await Tenant(_client, tenant).PostAsJsonAsync(
                "/auth/login",
                new { email = "a4@test.com", password = "Password123!" })
            .ContinueWith(t => t.Result.Content.ReadFromJsonAsync<TokenPair>())
            .Unwrap();

        var logout = await Tenant(_client, tenant).PostAsJsonAsync(
            "/auth/logout",
            new { refreshToken = pair!.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var refresh = await Tenant(_client, tenant).PostAsJsonAsync(
            "/auth/refresh",
            new { refreshToken = pair!.RefreshToken });
        Assert.Equal(HttpStatusCode.BadRequest, refresh.StatusCode);
    }

    // ================================================================
    // Password management
    // ================================================================

    [Fact]
    public async Task Change_password_revokes_all_sessions()
    {
        var tenant = await CreateIsolatedTenantAsync("auth-password");

        var token = await RegisterUserAsync(
            _factory, _client, tenant, "a5@test.com", "Password123!");

        var change = await Auth(_client, token).PostAsJsonAsync(
            "/auth/change-password",
            new { currentPassword = "Password123!", newPassword = "NewPassword123!" });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);

        // Old access token is dead immediately (token_version bumped).
        var me = await Auth(_client, token).GetAsync("/tenant/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);

        // Old password no longer works; the new one does.
        var oldLogin = await Tenant(_client, tenant).PostAsJsonAsync(
            "/auth/login",
            new { email = "a5@test.com", password = "Password123!" });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        var newLogin = await Tenant(_client, tenant).PostAsJsonAsync(
            "/auth/login",
            new { email = "a5@test.com", password = "NewPassword123!" });
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    [Fact]
    public async Task Password_reset_flow()
    {
        var tenant = await CreateIsolatedTenantAsync("auth-reset");

        await RegisterUserAsync(
            _factory, _client, tenant, "a6@test.com", "Password123!");

        var forgot = await Tenant(_client, tenant).PostAsJsonAsync(
            "/auth/forgot-password",
            new { email = "a6@test.com" });
        Assert.Equal(HttpStatusCode.OK, forgot.StatusCode);

        var email = _factory.Emails.Sent.Last();
        var resetToken = ExtractLink(email.Body, "token");

        var reset = await Tenant(_client, tenant).PostAsJsonAsync(
            "/auth/reset-password",
            new { token = resetToken, newPassword = "ResetPassword123!" });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        var login = await Tenant(_client, tenant).PostAsJsonAsync(
            "/auth/login",
            new { email = "a6@test.com", password = "ResetPassword123!" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    // ================================================================
    // Email verification (C2)
    // ================================================================

    [Fact]
    public async Task Email_verification_is_required_when_enabled()
    {
        using var factory = TestAppFactory.WithConfiguration(config => config
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:RequireEmailVerification"] = "true"
            }));
        var client = factory.CreateClient();

        var tenant = "auth-verify";
        var superToken = await LoginSuperAdminAsync(client);
        await CreateTenantAsync(client, superToken, tenant, tenant);

        // Platform-invite the user, then redeem — verification is required,
        // so redeeming returns a message, not tokens.
        var invite = await Auth(client, superToken).PostAsJsonAsync(
            $"/admin/tenants/{tenant}/invites",
            new { email = "a7@test.com" });
        invite.EnsureSuccessStatusCode();

        var inviteToken = InviteTokenFromEmail(factory, "a7@test.com");

        var register = await Tenant(client, tenant).PostAsJsonAsync(
            "/auth/register",
            new { email = "a7@test.com", password = "Password123!", inviteToken });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var body = await register.Content
            .ReadFromJsonAsync<MessageBody>();
        Assert.Contains("verification link", body?.Message);

        // Login is blocked until verified.
        var blocked = await Tenant(client, tenant).PostAsJsonAsync(
            "/auth/login",
            new { email = "a7@test.com", password = "Password123!" });
        Assert.Equal(HttpStatusCode.Unauthorized, blocked.StatusCode);

        // Verify via the emailed link, then log in.
        var verifyEmail = factory.Emails.Sent.Last(
            e => string.Equals(e.To, "a7@test.com", StringComparison.OrdinalIgnoreCase));
        var verifyToken = ExtractLink(verifyEmail.Body, "token");

        var verify = await Tenant(client, tenant).PostAsJsonAsync(
            "/auth/verify-email",
            new { token = verifyToken });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);

        var login = await Tenant(client, tenant).PostAsJsonAsync(
            "/auth/login",
            new { email = "a7@test.com", password = "Password123!" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    // ================================================================
    // MFA (TOTP)
    // ================================================================

    [Fact]
    public async Task Mfa_enroll_and_login_flow()
    {
        var tenant = await CreateIsolatedTenantAsync("auth-mfa");

        var token = await RegisterUserAsync(
            _factory, _client, tenant, "a8@test.com", "Password123!");

        // Enroll.
        var enroll = await Auth(_client, token).PostAsync(
            "/auth/mfa/enroll", null);
        Assert.Equal(HttpStatusCode.OK, enroll.StatusCode);

        var enrolled = await enroll.Content
            .ReadFromJsonAsync<MfaEnrollment>();
        Assert.False(string.IsNullOrEmpty(enrolled?.Secret));

        var code = TotpService.Compute(
            enrolled!.Secret,
            (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                .TotalSeconds / 30);

        var confirm = await Auth(_client, token).PostAsJsonAsync(
            "/auth/mfa/verify",
            new { code });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        // Login now requires the second factor.
        var login = await Tenant(_client, tenant).PostAsJsonAsync(
            "/auth/login",
            new { email = "a8@test.com", password = "Password123!" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var mfa = await login.Content.ReadFromJsonAsync<MfaChallenge>();
        Assert.True(mfa?.MfaRequired);

        var complete = await Tenant(_client, tenant).PostAsJsonAsync(
            "/auth/mfa",
            new { mfaToken = mfa!.MfaToken, code });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
    }

    private async Task<string> CreateIsolatedTenantAsync(string tenant)
    {
        var superToken = await LoginSuperAdminAsync(_client);
        await CreateTenantAsync(_client, superToken, tenant, tenant);
        return tenant;
    }

    private record ErrorBody(string? Error);
    private record MessageBody(string? Message);
    private record MfaEnrollment(string? Secret, string? OtpauthUri);
    private record MfaChallenge(bool MfaRequired, string? MfaToken);
}
