using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Xunit;

using static Tessera.Platform.Tests.ApiTestHelpers;

namespace Tessera.Platform.Tests;

/// <summary>
/// Exercises the MCP OAuth authorization server surface (/connect/*,
/// docs/mcp-auth-platform.md) end-to-end over HTTP: discovery metadata, the
/// authorization-code + PKCE dance (login + consent via the JSON endpoints
/// the Next.js portal drives), token issuance, and refresh rotation.
/// </summary>
public class McpOAuthTests : IClassFixture<TestAppFactory>
{
    private const string ClientId = "tessera-local-dev";
    private const string RedirectUri =
        "http://127.0.0.1:9876/callback";

    private readonly TestAppFactory _factory;
    private readonly HttpClient _client;

    public McpOAuthTests(TestAppFactory factory)
    {
        _factory = factory;

        // Cookies must persist across the interactive flow.
        _client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                HandleCookies = true,
                AllowAutoRedirect = false
            });
    }

    private static string McpResource(string tenant) =>
        $"https://tessera.local/t/{tenant}/mcp";

    // ================================================================
    // Discovery metadata
    // ================================================================

    [Fact]
    public async Task Discovery_metadata_advertises_required_capabilities()
    {
        var response = await _client.GetAsync(
            "/.well-known/openid-configuration");
        response.EnsureSuccessStatusCode();

        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(
            "https://tessera.local/",
            doc.GetProperty("issuer").GetString());

        var endpoints = new[]
        {
            "authorization_endpoint",
            "token_endpoint",
            "jwks_uri"
        };

        foreach (var endpoint in endpoints)
        {
            Assert.True(
                doc.TryGetProperty(endpoint, out var value) &&
                !string.IsNullOrWhiteSpace(value.GetString()),
                $"Missing {endpoint} in discovery metadata.");
        }

        var codeMethods = doc.GetProperty("code_challenge_methods_supported");
        Assert.Contains("S256", codeMethods.EnumerateArray().Select(
            e => e.GetString()));

        var scopes = doc.GetProperty("scopes_supported")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("tools", scopes);
        Assert.Contains("offline_access", scopes);

        // CIMD support must be advertised, and public clients (PKCE, no
        // secret) must be listed. MCP hosts such as Claude web read both
        // before sending an HTTPS URL as their client_id; without them the
        // host falls back to a non-URL client id that the AS rejects with
        // invalid_client.
        Assert.True(
            doc.GetProperty("client_id_metadata_document_supported")
                .GetBoolean(),
            "client_id_metadata_document_supported must be advertised.");

        var authMethods = doc
            .GetProperty("token_endpoint_auth_methods_supported")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("none", authMethods);
    }

    // ================================================================
    // Authorization code + PKCE dance
    // ================================================================

    [Fact]
    public async Task Authorization_code_flow_with_pkce_issues_and_refreshes_tokens()
    {
        const string tenant = "mcp-oauth-1";
        const string email = "oauth@test.com";
        const string password = "Password123!";

        var superToken = await LoginSuperAdminAsync(_client);
        await CreateTenantAsync(_client, superToken, tenant, tenant);

        await RegisterUserAsync(
            _factory, _client, tenant, email, password);

        var (verifier, challenge) = NewPkcePair();
        var authorizeUrl = BuildAuthorizeUrl(tenant, challenge);

        // ---- 1. Anonymous authorize -> redirected to the portal login page
        var first = await Tenant(_client, tenant).GetAsync(authorizeUrl);

        if (first.StatusCode != HttpStatusCode.Redirect ||
            !first.Headers.Location!.ToString().Contains("/oauth/login"))
        {
            var firstBody = await first.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException(
                $"Step 1 authorize got {first.StatusCode} " +
                $"(location={first.Headers.Location}): {firstBody}");
        }

        // ---- 2. Portal login (JSON) sets the OAuth cookie
        var login = await Tenant(_client, tenant).PostAsJsonAsync(
            "/connect/login",
            new { email, password });

        if (login.StatusCode != HttpStatusCode.OK)
        {
            var loginBody = await login.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException(
                $"/connect/login returned {login.StatusCode}: {loginBody}");
        }

        // ---- 3. Authorize again -> consent required -> portal consent page
        var consentNeeded = await Tenant(_client, tenant)
            .GetAsync(authorizeUrl);

        if (consentNeeded.StatusCode != HttpStatusCode.Redirect)
        {
            var body = await consentNeeded.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException(
                $"/connect/authorize after login returned " +
                $"{consentNeeded.StatusCode}: {body}");
        }

        Assert.Contains(
            "/oauth/consent",
            consentNeeded.Headers.Location!.ToString());

        // ---- 4. Portal consent (JSON) records the grant
        var consent = await Tenant(_client, tenant).PostAsJsonAsync(
            "/connect/consent",
            new
            {
                clientId = ClientId,
                scopes = new[] { "tools", "offline_access" }
            });
        Assert.Equal(HttpStatusCode.OK, consent.StatusCode);

        // ---- 5. Authorize again -> redirect to client with a code
        var done = await Tenant(_client, tenant).GetAsync(authorizeUrl);

        if (done.StatusCode != HttpStatusCode.Redirect)
        {
            var body = await done.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException(
                $"/connect/authorize after consent returned " +
                $"{done.StatusCode}: {body}");
        }

        var location = done.Headers.Location!.ToString();
        Assert.StartsWith(RedirectUri, location);
        Assert.Contains("code=", location);

        var code = new Uri(location).Query.TrimStart('?')
            .Split('&')
            .FirstOrDefault(
                part => part.StartsWith("code=", StringComparison.Ordinal))
            ?.Split('=')[1];

        if (string.IsNullOrEmpty(code))
        {
            throw new Xunit.Sdk.XunitException(
                $"Authorize redirect lacked a code: {location}");
        }

        // ---- 6. Exchange the code at the token endpoint
        var token = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = verifier
        });

        Assert.Equal(HttpStatusCode.OK, token.StatusCode);

        var tokenBody = await token.Content.ReadFromJsonAsync<TokenEnvelope>();
        Assert.NotNull(tokenBody?.AccessToken);
        Assert.NotNull(tokenBody.RefreshToken);

        // ---- 7. Refresh rotates the refresh token
        var refresh = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokenBody!.RefreshToken!,
            ["client_id"] = ClientId
        });

        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);

        var refreshBody =
            await refresh.Content.ReadFromJsonAsync<TokenEnvelope>();
        Assert.NotNull(refreshBody?.AccessToken);
        Assert.NotNull(refreshBody.RefreshToken);

        // ---- 8. Replaying the rotated refresh token is rejected
        var replay = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokenBody!.RefreshToken!,
            ["client_id"] = ClientId
        });

        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Fact]
    public async Task Wrong_tenant_login_is_rejected()
    {
        const string tenantA = "mcp-oauth-a";
        const string tenantB = "mcp-oauth-b";
        const string email = "oauthtwo@test.com";
        const string password = "Password123!";

        // The user only exists in tenant A.
        var superToken = await LoginSuperAdminAsync(_client);
        await CreateTenantAsync(_client, superToken, tenantA, tenantA);
        await CreateTenantAsync(_client, superToken, tenantB, tenantB);

        await RegisterUserAsync(
            _factory, _client, tenantA, email, password);

        // Logging into tenant B with tenant A's credentials fails.
        var login = await Tenant(_client, tenantB).PostAsJsonAsync(
            "/connect/login",
            new { email, password });

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static string BuildAuthorizeUrl(
        string tenant,
        string codeChallenge)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["scope"] = "tools offline_access",
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["state"] = "test-state",
            ["resource"] = McpResource(tenant)
        };

        return "/connect/authorize?" + string.Join("&", query.Select(
            pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
    }

    private static (string Verifier, string Challenge) NewPkcePair()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var verifier = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        var digest = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));

        var challenge = Convert.ToBase64String(digest)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return (verifier, challenge);
    }

    private Task<HttpResponseMessage> PostTokenAsync(
        IReadOnlyDictionary<string, string> form)
    {
        var content = new FormUrlEncodedContent(form);
        return _client.PostAsync("/connect/token", content);
    }

    private record TokenEnvelope(
        [property: System.Text.Json.Serialization.JsonPropertyName(
            "access_token")] string? AccessToken,
        [property: System.Text.Json.Serialization.JsonPropertyName(
            "token_type")] string? TokenType,
        [property: System.Text.Json.Serialization.JsonPropertyName(
            "expires_in")] int? ExpiresIn,
        [property: System.Text.Json.Serialization.JsonPropertyName(
            "refresh_token")] string? RefreshToken,
        string? Scope,
        string? Error,
        [property: System.Text.Json.Serialization.JsonPropertyName(
            "error_description")] string? ErrorDescription);
}
