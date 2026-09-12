using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

using static Tessera.Platform.Tests.ApiTestHelpers;

namespace Tessera.Platform.Tests;

public class ManifestTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    private readonly HttpClient _client;

    public ManifestTests(TestAppFactory factory)
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

    private static object SampleManifest(string toolName) => new
    {
        toolName,
        description = $"Description for {toolName}.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                patient_name = new { type = "string" }
            },
            required = new[] { "patient_name" }
        },
        execution = new
        {
            type = "http",
            method = "POST",
            url = "https://api.acmedental.test/v1/appointments",
            auth = new
            {
                type = "api_key",
                credential_ref = "vault://acme-dental/booking-api-key"
            },
            body_template = new { patient = "${patient_name}" },
            response_mapping = "$.data.appointment"
        },
        requiredScopes = new[] { "appointments:write" }
    };

    // ================================================================
    // Sample SMB fixture
    // ================================================================

    [Fact]
    public async Task Sample_smb_manifests_are_seeded_for_acme_dental()
    {
        var tenant = "acme-dental";

        // The sample tenant exists and its first user becomes Superadmin.
        var superToken = await RegisterUserAsync(
            _factory, _client, tenant, "jane@acmedental.test", "Password123!");

        var manifests = await Tenant(Auth(_client, superToken), tenant)
            .GetFromJsonAsync<ManifestInfo[]>("/tenant/manifests");

        Assert.NotNull(manifests);
        Assert.Equal(
            new[]
            {
                "book_appointment",
                "cancel_appointment",
                "list_appointments"
            },
            manifests!.Select(m => m.ToolName).OrderBy(n => n));

        // Spot-check one manifest's stored contract.
        var book = manifests.Single(m => m.ToolName == "book_appointment");

        Assert.Equal("appointments:write", book.RequiredScopes.Single());
        Assert.Equal(JsonValueKind.Object, book.InputSchema.ValueKind);
        Assert.Equal(
            "$.data.appointment",
            book.Execution.GetProperty("response_mapping").GetString());
    }

    [Fact]
    public async Task Admin_role_includes_manage_tools()
    {
        var tenant = await NewTenantAsync("mtest-admin-role");

        var superToken = await RegisterUserAsync(
            _factory, _client, tenant, "mr-admin@test.com", "Password123!");

        var adminToken = await InviteAndRegisterAsync(
            _factory, _client, superToken, tenant,
            "mr-admin2@test.com", "Password123!", "Admin");

        var me = await Tenant(Auth(_client, adminToken), tenant)
            .GetFromJsonAsync<MeInfo>("/tenant/me");

        Assert.Equal("Admin", me?.Role);
        Assert.Contains("manage_tools", me?.Actions ?? []);
    }

    // ================================================================
    // CRUD lifecycle
    // ================================================================

    [Fact]
    public async Task Manifest_crud_lifecycle()
    {
        var tenant = await NewTenantAsync("mtest-crud");

        var adminToken = await RegisterUserAsync(
            _factory, _client, tenant, "mcrud@test.com", "Password123!");

        var api = Tenant(Auth(_client, adminToken), tenant);

        // Create.
        var create = await api.PostAsJsonAsync(
            "/tenant/manifests",
            SampleManifest("book_appointment"));

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var created = await create.Content
            .ReadFromJsonAsync<ManifestInfo>();

        Assert.Equal("book_appointment", created!.ToolName);
        Assert.Equal(JsonValueKind.Object, created.InputSchema.ValueKind);

        // Duplicate name is rejected while the tool is active.
        var duplicate = await api.PostAsJsonAsync(
            "/tenant/manifests",
            SampleManifest("book_appointment"));

        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

        // List + get by id.
        var manifests = await api
            .GetFromJsonAsync<ManifestInfo[]>("/tenant/manifests");

        Assert.Single(manifests!);
        Assert.Equal(created.Id, manifests![0].Id);

        var fetched = await api
            .GetFromJsonAsync<ManifestInfo>($"/tenant/manifests/{created.Id}");

        Assert.Equal("book_appointment", fetched?.ToolName);

        // Update.
        var update = await api.PutAsJsonAsync(
            $"/tenant/manifests/{created.Id}",
            SampleManifest("book_appointment_updated"));

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var updated = await update.Content
            .ReadFromJsonAsync<ManifestInfo>();

        Assert.Equal("book_appointment_updated", updated?.ToolName);

        // Soft delete → no longer listed.
        var del = await api.DeleteAsync(
            $"/tenant/manifests/{created.Id}");

        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var afterDelete = await api
            .GetFromJsonAsync<ManifestInfo[]>("/tenant/manifests");

        Assert.Empty(afterDelete!);

        var getDeleted = await api
            .GetAsync($"/tenant/manifests/{created.Id}");

        Assert.Equal(HttpStatusCode.NotFound, getDeleted.StatusCode);

        // A soft-deleted tool name can be re-used (the filtered unique
        // index on PostgreSQL frees it; the endpoint enforces the same
        // rule on InMemory).
        var recreate = await api.PostAsJsonAsync(
            "/tenant/manifests",
            SampleManifest("book_appointment"));

        Assert.Equal(HttpStatusCode.Created, recreate.StatusCode);
    }

    // ================================================================
    // Validation
    // ================================================================

    [Fact]
    public async Task Manifest_validation_rejects_bad_payloads()
    {
        var tenant = await NewTenantAsync("mtest-validate");

        var adminToken = await RegisterUserAsync(
            _factory, _client, tenant, "mval@test.com", "Password123!");

        var api = Tenant(Auth(_client, adminToken), tenant);

        // Invalid tool name (uppercase / spaces).
        var badName = await api.PostAsJsonAsync(
            "/tenant/manifests",
            SampleManifest("Bad Name!"));

        Assert.Equal(HttpStatusCode.BadRequest, badName.StatusCode);

        // inputSchema must be a JSON object, not a string.
        var badSchema = await api.PostAsJsonAsync(
            "/tenant/manifests",
            new
            {
                toolName = "good_name",
                description = "desc",
                inputSchema = "{}",
                execution = new { type = "http" }
            });

        Assert.Equal(HttpStatusCode.BadRequest, badSchema.StatusCode);

        // execution must be a JSON object.
        var badExecution = await api.PostAsJsonAsync(
            "/tenant/manifests",
            new
            {
                toolName = "good_name",
                description = "desc",
                inputSchema = new { type = "object" },
                execution = new[] { "http" }
            });

        Assert.Equal(HttpStatusCode.BadRequest, badExecution.StatusCode);

        // Missing tool name.
        var missingName = await api.PostAsJsonAsync(
            "/tenant/manifests",
            new
            {
                description = "desc",
                inputSchema = new { type = "object" },
                execution = new { type = "http" }
            });

        Assert.Equal(HttpStatusCode.BadRequest, missingName.StatusCode);

        // Nothing was created by any of the rejected payloads.
        var manifests = await api
            .GetFromJsonAsync<ManifestInfo[]>("/tenant/manifests");

        Assert.Empty(manifests!);
    }

    // ================================================================
    // Authorization
    // ================================================================

    [Fact]
    public async Task Manifest_management_requires_manage_tools()
    {
        var tenant = await NewTenantAsync("mtest-authz");

        var superToken = await RegisterUserAsync(
            _factory, _client, tenant, "mauthz-super@test.com", "Password123!");

        // A plain User member has view_widgets only — no manage_tools.
        var userToken = await InviteAndRegisterAsync(
            _factory, _client, superToken, tenant,
            "mauthz-user@test.com", "Password123!", "User");

        var api = Tenant(Auth(_client, userToken), tenant);

        var list = await api.GetAsync("/tenant/manifests");
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);

        var create = await api.PostAsJsonAsync(
            "/tenant/manifests",
            SampleManifest("sneaky_tool"));

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }
}

public record ManifestInfo(
    Guid Id,
    string ToolName,
    string Description,
    JsonElement InputSchema,
    JsonElement Execution,
    string[] RequiredScopes,
    string? RateLimitOverride,
    DateTime CreatedAt);