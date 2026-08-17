using System.Net;
using System.Net.Http.Json;
using Xunit;

using static Tessera.Platform.Tests.ApiTestHelpers;

namespace Tessera.Platform.Tests;

public class HierarchyTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    private readonly HttpClient _client;

    public HierarchyTests(TestAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<string> NewTenantAsync(string identifier)
    {
        var superadmin = await LoginSuperAdminAsync(_client);
        await CreateTenantAsync(_client, superadmin, identifier, identifier);
        return identifier;
    }

    [Fact]
    public async Task Superadmin_role_is_protected_from_tenant_edits()
    {
        var tenant = await NewTenantAsync("hier-1");

        var superToken = await RegisterUserAsync(
            _factory, _client, tenant, "h1-super@test.com", "Password123!");

        var roles = await Tenant(Auth(_client, superToken), tenant)
            .GetFromJsonAsync<TenantRoleInfo[]>("/tenant/roles");

        var superRole = roles!.First(r => r.Name == "Superadmin");

        // Cannot rename, edit actions, or delete the system role.
        var rename = await Tenant(Auth(_client, superToken), tenant)
            .PutAsJsonAsync(
                $"/tenant/roles/{superRole.Id}",
                new { name = "Owner", actions = superRole.Actions });
        Assert.Equal(HttpStatusCode.BadRequest, rename.StatusCode);

        var edit = await Tenant(Auth(_client, superToken), tenant)
            .PutAsJsonAsync(
                $"/tenant/roles/{superRole.Id}",
                new { name = "Superadmin", actions = new[] { "view_widgets" } });
        Assert.Equal(HttpStatusCode.BadRequest, edit.StatusCode);

        var del = await Tenant(Auth(_client, superToken), tenant)
            .DeleteAsync($"/tenant/roles/{superRole.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, del.StatusCode);
    }

    [Fact]
    public async Task Superadmin_role_cannot_be_assigned_or_demoted_tenant_side()
    {
        var tenant = await NewTenantAsync("hier-2");

        var superToken = await RegisterUserAsync(
            _factory, _client, tenant, "h2-super@test.com", "Password123!");

        var roles = await Tenant(Auth(_client, superToken), tenant)
            .GetFromJsonAsync<TenantRoleInfo[]>("/tenant/roles");

        var superRole = roles!.First(r => r.Name == "Superadmin");

        // Tenant-side invite with the Superadmin role is rejected.
        var invite = await Tenant(Auth(_client, superToken), tenant)
            .PostAsJsonAsync(
                "/tenant/invites",
                new { email = "h2-wannabe@test.com", roleId = superRole.Id });
        Assert.Equal(HttpStatusCode.BadRequest, invite.StatusCode);

        // Direct user creation with the Superadmin role is rejected.
        var create = await Tenant(Auth(_client, superToken), tenant)
            .PostAsJsonAsync(
                "/tenant/users",
                new
                {
                    email = "h2-direct@test.com",
                    password = "Password123!",
                    role = "Superadmin"
                });
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);

        // And the existing Superadmin cannot be demoted.
        var superUser = (await Tenant(Auth(_client, superToken), tenant)
                .GetFromJsonAsync<TenantUserInfo[]>("/tenant/users"))!
            .First(u => u.Email == "h2-super@test.com");

        var demote = await Tenant(Auth(_client, superToken), tenant)
            .PutAsJsonAsync(
                $"/tenant/users/{superUser.Id}",
                new { role = "User" });
        Assert.Equal(HttpStatusCode.BadRequest, demote.StatusCode);

        var remove = await Tenant(Auth(_client, superToken), tenant)
            .DeleteAsync($"/tenant/users/{superUser.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, remove.StatusCode);
    }

    [Fact]
    public async Task Envelope_cannot_declare_a_reserved_superadmin_role()
    {
        var superToken = await LoginSuperAdminAsync(_client);

        var create = await Auth(_client, superToken).PostAsJsonAsync(
            "/admin/envelopes",
            new
            {
                name = "Sneaky",
                roles = new[]
                {
                    new
                    {
                        name = "Superadmin",
                        actions = new[] { "view_widgets" }
                    }
                }
            });

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    }

    [Fact]
    public async Task Superadmin_can_do_everything_and_admin_invites_work()
    {
        var tenant = await NewTenantAsync("hier-3");

        // First invite → Superadmin.
        var superToken = await RegisterUserAsync(
            _factory, _client, tenant, "h3-super@test.com", "Password123!");

        var superMe = await Tenant(Auth(_client, superToken), tenant)
            .GetFromJsonAsync<MeInfo>("/tenant/me");

        // The Superadmin holds every action in the catalog.
        Assert.Equal("Superadmin", superMe?.Role);
        foreach (var action in new[]
                 {
                     "view_widgets", "create_widget", "edit_widget",
                     "delete_widget", "manage_users"
                 })
        {
            Assert.Contains(action, superMe?.Actions ?? []);
        }

        // The Superadmin creates an Admin assistant via invite.
        var adminToken = await InviteAndRegisterAsync(
            _factory, _client, superToken, tenant,
            "h3-admin@test.com", "Password123!", "Admin");

        var adminMe = await Tenant(Auth(_client, adminToken), tenant)
            .GetFromJsonAsync<MeInfo>("/tenant/me");

        Assert.Equal("Admin", adminMe?.Role);
        Assert.Contains("manage_users", adminMe?.Actions ?? []);
        Assert.DoesNotContain("Superadmin", adminMe?.Role);
    }
}
