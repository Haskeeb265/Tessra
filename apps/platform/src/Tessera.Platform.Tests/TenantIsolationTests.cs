using System.Net;
using System.Net.Http.Json;
using Xunit;

using static Tessera.Platform.Tests.ApiTestHelpers;

namespace Tessera.Platform.Tests;

public class TenantIsolationTests : IClassFixture<TestAppFactory>
{
    private readonly HttpClient _client;
    private readonly TestAppFactory _factory;

    public TenantIsolationTests(TestAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>Creates a fresh tenant and a team in it (superadmin + User member).</summary>
    private async Task<(string Tenant, string AdminToken, string UserToken)>
        NewTeamAsync(string identifier)
    {
        var superadmin = await LoginSuperAdminAsync(_client);

        await CreateTenantAsync(_client, superadmin, identifier, identifier);

        var (adminToken, userToken) = await RegisterTeamAsync(
            _factory, _client, identifier, identifier);

        return (identifier, adminToken, userToken);
    }

    [Fact]
    public async Task Cross_tenant_token_returns_403()
    {
        var (tenant, adminToken, _) = await NewTeamAsync("iso-1");

        var response = await Tenant(Auth(_client, adminToken), BetaIndustries)
            .GetAsync("/tenant/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(BetaIndustries, tenant);
    }

    [Fact]
    public async Task Widgets_are_isolated_between_tenants()
    {
        var (tenantA, adminA, _) = await NewTeamAsync("iso-2");
        var (tenantB, adminB, _) = await NewTeamAsync("iso-3");

        // Each request must carry BOTH the right token and the right tenant
        // header (they are independent on the shared client).
        var created = await Tenant(Auth(_client, adminA), tenantA)
            .PostAsJsonAsync(
                "/widgets",
                new { name = "Secret widget", description = "tenant A only" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var betaWidgets = await Tenant(Auth(_client, adminB), tenantB)
            .GetFromJsonAsync<WidgetInfo[]>("/widgets");
        var alphaWidgets = await Tenant(Auth(_client, adminA), tenantA)
            .GetFromJsonAsync<WidgetInfo[]>("/widgets");

        Assert.Empty(betaWidgets ?? []);
        Assert.Single(alphaWidgets ?? []);
        Assert.Equal("Secret widget", alphaWidgets![0].Name);
        Assert.Equal(tenantA, alphaWidgets[0].TenantId);
    }

    [Fact]
    public async Task Widget_actions_are_enforced_server_side()
    {
        var (_, adminToken, userToken) = await NewTeamAsync("iso-4");

        // Regular user: view allowed, create/edit/delete denied.
        var userCreate = await Auth(_client, userToken).PostAsJsonAsync(
            "/widgets",
            new { name = "Nope" });
        Assert.Equal(HttpStatusCode.Forbidden, userCreate.StatusCode);

        var adminCreate = await Auth(_client, adminToken).PostAsJsonAsync(
            "/widgets",
            new { name = "Admin widget" });
        Assert.Equal(HttpStatusCode.Created, adminCreate.StatusCode);

        var widget = await adminCreate.Content.ReadFromJsonAsync<WidgetInfo>();

        var userDelete = await Auth(_client, userToken)
            .DeleteAsync($"/widgets/{widget!.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, userDelete.StatusCode);

        var userEdit = await Auth(_client, userToken).PutAsJsonAsync(
            $"/widgets/{widget.Id}",
            new { name = "Nope" });
        Assert.Equal(HttpStatusCode.Forbidden, userEdit.StatusCode);

        var userList = await Auth(_client, userToken)
            .GetAsync("/widgets");
        Assert.Equal(HttpStatusCode.OK, userList.StatusCode);

        var adminDelete = await Auth(_client, adminToken)
            .DeleteAsync($"/widgets/{widget.Id}");
        Assert.Equal(HttpStatusCode.NoContent, adminDelete.StatusCode);
    }

    [Fact]
    public async Task Role_rename_preserves_permissions_by_id()
    {
        var (tenant, superToken, _) = await NewTeamAsync("iso-5");

        // Add an Admin-tier member — the Superadmin's role is platform-
        // managed and can't be renamed.
        var adminToken = await InviteAndRegisterAsync(
            _factory, _client, superToken, tenant,
            "iso5-mgr@test.com", "Password123!", "Admin");

        // The admin renames the Admin role — users keep their permissions
        // because they reference the role by ID, not by name.
        var roles = await Tenant(Auth(_client, adminToken), tenant)
            .GetFromJsonAsync<TenantRoleInfo[]>("/tenant/roles");

        var adminRole = roles!.First(r => r.Name == "Admin");

        var rename = await Tenant(Auth(_client, adminToken), tenant)
            .PutAsJsonAsync(
                $"/tenant/roles/{adminRole.Id}",
                new { name = "Owner", actions = adminRole.Actions });
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);

        var me = await Tenant(Auth(_client, adminToken), tenant)
            .GetFromJsonAsync<MeInfo>("/tenant/me");

        Assert.Equal("Owner", me?.Role);
        Assert.Contains("manage_users", me?.Actions ?? []);
        Assert.Contains("delete_widget", me?.Actions ?? []);

        // And the renamed role still authorizes privileged ops.
        var create = await Tenant(Auth(_client, adminToken), tenant)
            .PostAsJsonAsync(
                "/widgets",
                new { name = "Still authorized" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
    }

    [Fact]
    public async Task Role_delete_is_blocked_while_users_are_assigned()
    {
        var (_, adminToken, userToken) = await NewTeamAsync("iso-6");

        var roles = await Auth(_client, adminToken)
            .GetFromJsonAsync<TenantRoleInfo[]>("/tenant/roles");

        var userRole = roles!.First(r => r.Name == "User");

        // The member is assigned to "User" — deletion must fail clearly.
        var blocked = await Auth(_client, adminToken)
            .DeleteAsync($"/tenant/roles/{userRole.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);

        var error = await blocked.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Contains("assigned to users", error?.Error);

        // Reassign the member, then deletion succeeds.
        var users = await Auth(_client, adminToken)
            .GetFromJsonAsync<TenantUserInfo[]>("/tenant/users");
        var member = users!.First(u => u.Email.EndsWith("-user@test.com"));

        var reassign = await Auth(_client, adminToken).PutAsJsonAsync(
            $"/tenant/users/{member.Id}",
            new { role = "Admin" });
        Assert.Equal(HttpStatusCode.OK, reassign.StatusCode);

        var deleted = await Auth(_client, adminToken)
            .DeleteAsync($"/tenant/roles/{userRole.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // The reassigned user's old token is rejected immediately (A5).
        var me = await Auth(_client, userToken).GetAsync("/tenant/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task Role_management_requires_manage_users_action()
    {
        var (_, _, userToken) = await NewTeamAsync("iso-7");

        var create = await Auth(_client, userToken).PostAsJsonAsync(
            "/tenant/roles",
            new { name = "Sneaky", actions = new[] { "delete_widget" } });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

        var invite = await Auth(_client, userToken).PostAsJsonAsync(
            "/tenant/invites",
            new { email = "x@test.com", roleId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Forbidden, invite.StatusCode);

        var users = await Auth(_client, userToken).GetAsync("/tenant/users");
        Assert.Equal(HttpStatusCode.Forbidden, users.StatusCode);
    }

    private record WidgetInfo(Guid Id, string Name, string? Description, string? TenantId);
    private record ErrorBody(string? Error);
}
