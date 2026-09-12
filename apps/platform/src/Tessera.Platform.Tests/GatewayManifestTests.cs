using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using Tessera.Platform.Api;

using Xunit;

using static Tessera.Platform.Tests.ApiTestHelpers;

namespace Tessera.Platform.Tests;

/// <summary>
/// Exercises the server-to-server gateway endpoints (/internal/gateway/*)
/// that the Python MCP gateway uses to resolve tenant manifests
/// (docs/mcp/README.md §11): API-key auth, tenant resolution, and the
/// seeded Acme Dental fixture.
/// </summary>
public class GatewayManifestTests : IClassFixture<TestAppFactory>
{
    private const string GatewayKey = "test-gateway-key";

    private readonly HttpClient _client;

    public GatewayManifestTests(TestAppFactory factory)
    {
        _client = TestAppFactory.WithConfiguration(config => config
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Gateway:ApiKey"] = GatewayKey
                }))
            .CreateClient(
                new WebApplicationFactoryClientOptions
                {
                    AllowAutoRedirect = false
                });
    }

    private static HttpClient Key(HttpClient client, string key)
    {
        client.DefaultRequestHeaders.Remove(
            Tessera.Platform.Api.Endpoints.GatewayEndpoints
                .GatewayApiKeyHeader);
        client.DefaultRequestHeaders.Add(
            Tessera.Platform.Api.Endpoints.GatewayEndpoints
                .GatewayApiKeyHeader, key);
        return client;
    }

    [Fact]
    public async Task Gateway_reads_seeded_acme_dental_manifests()
    {
        var response = await Key(_client, GatewayKey).GetAsync(
            "/internal/gateway/manifests?tenant=acme-dental");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("acme-dental", body.GetProperty("tenant_id").GetString());
        Assert.Equal("Active", body.GetProperty("status").GetString());

        var tools = body.GetProperty("tools")
            .EnumerateArray()
            .Select(t => t.GetProperty("tool_name").GetString()!)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(
            ["book_appointment", "cancel_appointment", "list_appointments"],
            tools);

        // The execution config must round-trip as JSON so the gateway can
        // execute it (HTTP method/url/auth).
        var execution = body.GetProperty("tools").EnumerateArray()
            .First(t => t.GetProperty("tool_name").GetString() ==
                        "book_appointment")
            .GetProperty("execution");

        Assert.Equal("POST", execution.GetProperty("method").GetString());
        Assert.Equal(
            "https://api.acmedental.test/v1/appointments",
            execution.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Gateway_rejects_bad_or_missing_api_key()
    {
        var missing = await _client.GetAsync(
            "/internal/gateway/manifests?tenant=acme-dental");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        var wrong = await Key(_client, "wrong-key").GetAsync(
            "/internal/gateway/manifests?tenant=acme-dental");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public async Task Gateway_404_for_unknown_tenant_and_400_for_missing_param()
    {
        var unknown = await Key(_client, GatewayKey).GetAsync(
            "/internal/gateway/manifests?tenant=nope-inc");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var missing = await Key(_client, GatewayKey).GetAsync(
            "/internal/gateway/manifests");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task Gateway_lists_active_tenants()
    {
        var response = await Key(_client, GatewayKey)
            .GetAsync("/internal/gateway/tenants");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var tenants = body.GetProperty("tenants")
            .EnumerateArray()
            .Select(t => t.GetProperty("id").GetString())
            .ToArray();

        Assert.Contains("acme-dental", tenants);
        Assert.Contains("alpha-corp", tenants);
    }

    [Fact]
    public async Task Gateway_endpoint_needs_no_tenant_header()
    {
        // The /internal surface must be exempt from TenantValidationMiddleware:
        // the gateway never sends X-Tenant-Id.
        var response = await Key(_client, GatewayKey).GetAsync(
            "/internal/gateway/manifests?tenant=acme-dental");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}