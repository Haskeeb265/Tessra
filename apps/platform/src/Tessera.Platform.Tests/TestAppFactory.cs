using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using Tessera.Platform.Api;
using Tessera.Platform.Api.Services;

namespace Tessera.Platform.Tests;

/// <summary>Captures emails so tests can extract verification/reset/invite links.</summary>
public class RecordingEmailSender : IEmailSender
{
    private readonly object _lock = new();

    public List<(string To, string Subject, string Body)> Sent { get; } = [];

    public Task SendAsync(
        string to,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            Sent.Add((to, subject, htmlBody));
        }

        return Task.CompletedTask;
    }
}

public class TestAppFactory : WebApplicationFactory<Program>
{
    private readonly Action<IConfigurationBuilder>? _configure;

    public RecordingEmailSender Emails { get; } = new();

    public TestAppFactory()
    {
    }

    private TestAppFactory(Action<IConfigurationBuilder> configure)
    {
        _configure = configure;
    }

    /// <summary>Creates a factory with extra configuration (used by tests that don't use it as a fixture).</summary>
    public static TestAppFactory WithConfiguration(
        Action<IConfigurationBuilder> configure) =>
        new(configure);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // InMemory database (no connection string configured).
                // Each factory gets its own uniquely-named store so tests
                // running in parallel never share (or race on) state.
                ["ConnectionStrings:DefaultConnection"] = "",
                ["Database:InMemoryName"] =
                    $"TesseraTest-{Guid.NewGuid():N}",
                // Fixed test signing key.
                ["Jwt:SecretKey"] = "Tessera-Test-Secret-Key-Min-32-Chars!!",
                // Rate limiting is off by default; the rate-limit test uses
                // its own factory with it enabled.
                ["RateLimiting:Enabled"] = "false",
                ["Auth:RequireEmailVerification"] = "false"
            });

            _configure?.Invoke(config);
        });

        builder.ConfigureServices(services =>
        {
            services.AddScoped<IEmailSender>(_ => Emails);
        });
    }
}

/// <summary>Shared helpers for exercising the API in tests.</summary>
public static class ApiTestHelpers
{
    public const string SuperAdminEmail = "superadmin@tessera.com";
    public const string SuperAdminPassword = "Admin123!";

    public static readonly string AlphaCorp = "alpha-corp";
    public static readonly string BetaIndustries = "beta-industries";

    public static async Task<string> LoginSuperAdminAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/admin/auth/login",
            new { email = SuperAdminEmail, password = SuperAdminPassword });

        response.EnsureSuccessStatusCode();

        var tokens = await response.Content
            .ReadFromJsonAsync<TokenPair>();

        return tokens!.AccessToken;
    }

    /// <summary>
    /// Extracts the invite token from the most recent invitation email sent
    /// to <paramref name="email"/>.
    /// </summary>
    public static string InviteTokenFromEmail(
        TestAppFactory factory,
        string email)
    {
        var sent = factory.Emails.Sent.Last(
            e => string.Equals(
                e.To,
                email,
                StringComparison.OrdinalIgnoreCase));

        var match = Regex.Match(sent.Body, "invite=([^&\"]+)");

        Assert.True(
            match.Success,
            $"No invite link found in email to {email}: {sent.Body}");

        return match.Groups[1].Value;
    }

    /// <summary>
    /// Registers a user via a PLATFORM invitation: the platform superadmin
    /// invites <paramref name="email"/> into <paramref name="tenant"/> and
    /// the invite is redeemed. The first redemption in a workspace becomes
    /// its Superadmin; later platform invites default to Admin.
    /// </summary>
    public static async Task<string> RegisterUserAsync(
        TestAppFactory factory,
        HttpClient client,
        string tenant,
        string email,
        string password)
    {
        var superToken = await LoginSuperAdminAsync(client);

        var invite = await Auth(client, superToken).PostAsJsonAsync(
            $"/admin/tenants/{tenant}/invites",
            new { email });

        invite.EnsureSuccessStatusCode();

        var inviteToken = InviteTokenFromEmail(factory, email);

        return await RedeemInviteAsync(
            client, tenant, email, password, inviteToken);
    }

    /// <summary>
    /// Invites <paramref name="email"/> with <paramref name="roleName"/>
    /// as <paramref name="inviterToken"/> (tenant-side, requires
    /// manage_users) and redeems the invitation.
    /// </summary>
    public static async Task<string> InviteAndRegisterAsync(
        TestAppFactory factory,
        HttpClient client,
        string inviterToken,
        string tenant,
        string email,
        string password,
        string roleName)
    {
        var roles = await Tenant(Auth(client, inviterToken), tenant)
            .GetFromJsonAsync<TenantRoleInfo[]>("/tenant/roles");

        var role = roles!.First(r => r.Name == roleName);

        var invite = await Tenant(Auth(client, inviterToken), tenant)
            .PostAsJsonAsync(
                "/tenant/invites",
                new { email, roleId = role.Id });

        invite.EnsureSuccessStatusCode();

        var inviteToken = InviteTokenFromEmail(factory, email);

        return await RedeemInviteAsync(
            client, tenant, email, password, inviteToken);
    }

    private static async Task<string> RedeemInviteAsync(
        HttpClient client,
        string tenant,
        string email,
        string password,
        string inviteToken)
    {
        var response = await Tenant(client, tenant).PostAsJsonAsync(
            "/auth/register",
            new { email, password, inviteToken });

        response.EnsureSuccessStatusCode();

        var tokens = await response.Content.ReadFromJsonAsync<TokenPair>();
        return tokens!.AccessToken;
    }

    /// <summary>
    /// Sets up a two-member team in a fresh tenant: the first member via a
    /// platform invite (becomes the workspace Superadmin), the second via a
    /// tenant invite with the built-in User role.
    /// </summary>
    public static async Task<(string AdminToken, string UserToken)> RegisterTeamAsync(
        TestAppFactory factory,
        HttpClient client,
        string tenant,
        string prefix)
    {
        var adminToken = await RegisterUserAsync(
            factory, client, tenant, $"{prefix}-admin@test.com", "Password123!");

        var userToken = await InviteAndRegisterAsync(
            factory, client, adminToken, tenant,
            $"{prefix}-user@test.com", "Password123!", "User");

        return (adminToken, userToken);
    }

    public static HttpClient Auth(HttpClient client, string accessToken)
    {
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    public static HttpClient Tenant(HttpClient client, string tenant)
    {
        client.DefaultRequestHeaders.Remove("X-Tenant-Id");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        return client;
    }

    public static async Task<string> CreateTenantAsync(
        HttpClient client,
        string adminToken,
        string identifier,
        string name)
    {
        var response = await Auth(client, adminToken).PostAsJsonAsync(
            "/admin/tenants",
            new { identifier, name });

        response.EnsureSuccessStatusCode();

        return identifier;
    }

}

public record TokenPair(string AccessToken, string RefreshToken);

public record MeInfo(
    string? Id,
    string? Email,
    string? Role,
    Guid? RoleId,
    string[] Actions,
    string? TenantId,
    string? TenantIdentifier);

public record TenantRoleInfo(Guid Id, string Name, string[] Actions);

public record TenantUserInfo(Guid Id, string Email, string? Role, DateTime CreatedAt);
