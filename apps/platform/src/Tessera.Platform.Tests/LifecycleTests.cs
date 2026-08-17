using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using Tessera.Platform.Api.Data;
using Tessera.Platform.Domain.Models;

using static Tessera.Platform.Tests.ApiTestHelpers;

namespace Tessera.Platform.Tests;

public class LifecycleTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    private readonly HttpClient _client;

    public LifecycleTests(TestAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Tenant_delete_cleans_widgets_and_soft_deletes()
    {
        var superadmin = await LoginSuperAdminAsync(_client);

        const string tenant = "lc-1";

        await CreateTenantAsync(_client, superadmin, tenant, tenant);

        var adminToken = await RegisterUserAsync(
            _factory, _client, tenant, "lc1-admin@test.com", "Password123!");

        await Auth(_client, adminToken).PostAsJsonAsync(
            "/widgets",
            new { name = "To be cleaned" });

        var deleted = await Auth(_client, superadmin)
            .DeleteAsync($"/admin/tenants/{tenant}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // The public tenant list no longer shows it (B4).
        var tenants = await _client.GetFromJsonAsync<PublicTenant[]>("/tenants");
        Assert.DoesNotContain(tenants ?? [], t => t.Id == tenant);

        // Widgets were deleted (B1) and users/tenant soft-deleted.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var orphanWidgets = await db.Widgets
            .IgnoreQueryFilters()
            .Where(w => w.TenantId == tenant)
            .CountAsync();
        Assert.Equal(0, orphanWidgets);

        var deletedUsers = await db.Users
            .IgnoreQueryFilters()
            .Where(u => u.TenantId == tenant && u.IsDeleted)
            .CountAsync();
        Assert.Equal(1, deletedUsers);

        var tenantRow = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenant);
        Assert.True(tenantRow?.IsDeleted);
    }

    [Fact]
    public async Task Suspended_tenant_is_blocked_until_reactivated()
    {
        var superadmin = await LoginSuperAdminAsync(_client);

        const string tenant = "lc-2";

        await CreateTenantAsync(_client, superadmin, tenant, tenant);

        var adminToken = await RegisterUserAsync(
            _factory, _client, tenant, "lc2@test.com", "Password123!");

        // Suspend.
        var suspend = await Auth(_client, superadmin).PutAsJsonAsync(
            $"/admin/tenants/{tenant}/status",
            new { status = "Suspended" });
        Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);

        // Logins and API calls are blocked (B3).
        var login = await Tenant(_client, tenant).PostAsJsonAsync(
            "/auth/login",
            new { email = "lc2@test.com", password = "Password123!" });
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);

        var me = await Tenant(Auth(_client, adminToken), tenant)
            .GetAsync("/tenant/me");
        Assert.Equal(HttpStatusCode.Forbidden, me.StatusCode);

        // Reactivate.
        var activate = await Auth(_client, superadmin).PutAsJsonAsync(
            $"/admin/tenants/{tenant}/status",
            new { status = "Active" });
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);

        var loginAgain = await Tenant(_client, tenant).PostAsJsonAsync(
            "/auth/login",
            new { email = "lc2@test.com", password = "Password123!" });
        Assert.Equal(HttpStatusCode.OK, loginAgain.StatusCode);
    }

    [Fact]
    public async Task Invitation_flow_redeems_role_and_rejects_wrong_email()
    {
        const string tenant = "lc-3";

        // Seed a tenant + admin via superadmin (invites need manage_users).
        var superadmin = await LoginSuperAdminAsync(_client);
        await CreateTenantAsync(_client, superadmin, tenant, tenant);

        var adminToken = await RegisterUserAsync(
            _factory, _client, tenant, "lc3-admin@test.com", "Password123!");

        var roles = await Auth(_client, adminToken)
            .GetFromJsonAsync<TenantRoleInfo[]>("/tenant/roles");
        var adminRole = roles!.First(r => r.Name == "Admin");

        // Invite a new member with the Admin role.
        var invite = await Auth(_client, adminToken).PostAsJsonAsync(
            "/tenant/invites",
            new { email = "lc3-invited@test.com", roleId = adminRole.Id });
        Assert.Equal(HttpStatusCode.OK, invite.StatusCode);

        var email = _factory.Emails.Sent.Last();
        var inviteToken = Regex.Match(
                email.Body, "invite=([^&\"]+)")
            .Groups[1].Value;

        // Wrong email with a valid token is rejected.
        var wrong = await Tenant(_client, tenant).PostAsJsonAsync(
            "/auth/register",
            new
            {
                email = "someone-else@test.com",
                password = "Password123!",
                inviteToken
            });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        // Correct email redeems the invite and gets the Admin role.
        var register = await Tenant(_client, tenant).PostAsJsonAsync(
            "/auth/register",
            new
            {
                email = "lc3-invited@test.com",
                password = "Password123!",
                inviteToken
            });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var tokens = await register.Content.ReadFromJsonAsync<TokenPair>();
        var me = await Auth(_client, tokens!.AccessToken)
            .GetFromJsonAsync<MeInfo>("/tenant/me");

        Assert.Equal("Admin", me?.Role);
        Assert.Contains("manage_users", me?.Actions ?? []);

        // The invite is single-use.
        var reuse = await Tenant(_client, tenant).PostAsJsonAsync(
            "/auth/register",
            new
            {
                email = "lc3-invited@test.com",
                password = "Password123!",
                inviteToken
            });
        Assert.Equal(HttpStatusCode.BadRequest, reuse.StatusCode);
    }

    [Fact]
    public async Task Auth_endpoints_are_rate_limited()
    {
        using var factory = TestAppFactory.WithConfiguration(config => config
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:Enabled"] = "true",
                ["RateLimiting:AuthPermitLimit"] = "3",
                ["RateLimiting:AuthWindowSeconds"] = "60"
            }));
        var client = factory.CreateClient();

        var statusCodes = new List<HttpStatusCode>();

        for (var i = 0; i < 6; i++)
        {
            var response = await Tenant(client, AlphaCorp).PostAsJsonAsync(
                "/auth/login",
                new { email = "nobody@test.com", password = "wrong" });

            statusCodes.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statusCodes);
        // The first requests succeeded (401), the excess were throttled.
        Assert.Equal(HttpStatusCode.TooManyRequests, statusCodes[^1]);
    }

    private record PublicTenant(string Id, string Name);
    private record ErrorBody(string? Error);
}
