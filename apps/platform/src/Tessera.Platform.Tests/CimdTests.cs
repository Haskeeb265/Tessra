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
/// Exercises CIMD (Client ID Metadata Document) client registration
/// (docs/mcp/README.md §8.4): a client whose client_id is an https URL
/// (loopback http in tests) is registered on first use by fetching its
/// metadata document. Verifies the full authorization-code + PKCE dance
/// works for a CIMD client without any pre-registration.
/// </summary>
public class CimdTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    private readonly HttpClient _client;

    public CimdTests(TestAppFactory factory)
    {
        _factory = factory;

        _client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                HandleCookies = true,
                AllowAutoRedirect = false
            });
    }

    // ================================================================
    // Happy path: CIMD client registers itself mid-flow
    // ================================================================

    [Fact]
    public async Task Cimd_client_registers_and_completes_the_pkce_dance()
    {
        using var docServer = new FakeCimdDocServer();
        await docServer.StartAsync();

        var clientId = docServer.ClientId;
        var redirectUri = docServer.RedirectUri;

        const string tenant = "cimd-tenant";
        const string email = "cimd@test.com";
        const string password = "Password123!";

        var superToken = await LoginSuperAdminAsync(_client);
        await CreateTenantAsync(_client, superToken, tenant, tenant);
        await RegisterUserAsync(_factory, _client, tenant, email, password);

        var (verifier, challenge) = NewPkcePair();
        var authorizeUrl = BuildAuthorizeUrl(
            tenant, clientId, redirectUri, challenge);

        // ---- 1. Anonymous authorize -> portal login
        var first = await Tenant(_client, tenant).GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Contains(
            "/oauth/login", first.Headers.Location!.ToString());

        // ---- 2. Login sets the OAuth cookie
        var login = await Tenant(_client, tenant).PostAsJsonAsync(
            "/connect/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // ---- 3. Authorize again -> consent required (client is new)
        var consentNeeded = await Tenant(_client, tenant)
            .GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.Redirect, consentNeeded.StatusCode);
        Assert.Contains(
            "/oauth/consent", consentNeeded.Headers.Location!.ToString());

        // The consent-info endpoint must resolve the CIMD client.
        var consentInfo = await Tenant(_client, tenant).GetAsync(
            $"/connect/consent-info?client_id=" +
            $"{Uri.EscapeDataString(clientId)}&scope=" +
            $"{Uri.EscapeDataString("tools offline_access")}");
        Assert.Equal(HttpStatusCode.OK, consentInfo.StatusCode);

        // ---- 4. Consent
        var consent = await Tenant(_client, tenant).PostAsJsonAsync(
            "/connect/consent",
            new
            {
                clientId,
                scopes = new[] { "tools", "offline_access" }
            });
        Assert.Equal(HttpStatusCode.OK, consent.StatusCode);

        // ---- 5. Authorize -> code
        var done = await Tenant(_client, tenant).GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        var location = done.Headers.Location!.ToString();
        Assert.StartsWith(redirectUri, location);
        Assert.Contains("code=", location);

        var code = new Uri(location).Query.TrimStart('?')
            .Split('&')
            .FirstOrDefault(
                part => part.StartsWith("code=", StringComparison.Ordinal))
            ?.Split('=')[1];
        Assert.False(string.IsNullOrEmpty(code));

        // ---- 6. Exchange the code
        var token = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier
        });

        Assert.Equal(HttpStatusCode.OK, token.StatusCode);

        var tokenBody =
            await token.Content.ReadFromJsonAsync<TokenEnvelope>();
        Assert.NotNull(tokenBody?.AccessToken);
        Assert.NotNull(tokenBody.RefreshToken);
    }

    // ================================================================
    // Negative paths
    // ================================================================

    [Fact]
    public async Task Cimd_document_with_mismatched_client_id_is_rejected()
    {
        using var docServer = new FakeCimdDocServer();
        await docServer.StartAsync();

        // Serve a doc whose client_id does NOT match the requested URL.
        docServer.Document = new Dictionary<string, object>
        {
            ["client_id"] = "https://someone-else.example.test/cimd",
            ["client_name"] = "Impostor",
            ["redirect_uris"] = new[]
            {
                "https://claude.example.test/api/mcp/auth_callback"
            }
        };

        const string tenant = "cimd-bad";
        var superToken = await LoginSuperAdminAsync(_client);
        await CreateTenantAsync(_client, superToken, tenant, tenant);

        var (_, challenge) = NewPkcePair();

        var response = await Tenant(_client, tenant).GetAsync(
            BuildAuthorizeUrl(
                tenant,
                docServer.ClientId,
                docServer.RedirectUri,
                challenge));

        // The AS must reject with invalid_client (not 500, not a redirect).
        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError,
            response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("client_id", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cimd_document_with_disallowed_redirect_uri_is_rejected()
    {
        using var docServer = new FakeCimdDocServer();
        await docServer.StartAsync();

        docServer.Document = new Dictionary<string, object>
        {
            ["client_id"] = docServer.ClientId,
            ["client_name"] = "Claude Web (test)",
            ["redirect_uris"] = new[]
            {
                "https://claude.example.test/api/mcp/auth_callback"
            }
        };

        const string tenant = "cimd-bad-redirect";
        var superToken = await LoginSuperAdminAsync(_client);
        await CreateTenantAsync(_client, superToken, tenant, tenant);

        var (_, challenge) = NewPkcePair();

        // Present a redirect_uri that is NOT in the doc.
        var query = new Dictionary<string, string>
        {
            ["client_id"] = docServer.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = "https://evil.example.test/callback",
            ["scope"] = "tools offline_access",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = "test-state",
            ["resource"] = McpResource(tenant)
        };

        var response = await Tenant(_client, tenant).GetAsync(
            "/connect/authorize?" + string.Join("&", query.Select(
                pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}")));

        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError,
            response.StatusCode);
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static string McpResource(string tenant) =>
        $"https://tessera.local/t/{tenant}/mcp";

    private static string BuildAuthorizeUrl(
        string tenant,
        string clientId,
        string redirectUri,
        string codeChallenge)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
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
            "refresh_token")] string? RefreshToken);
}

/// <summary>
/// A tiny loopback HTTP server serving a configurable CIMD document at
/// /cimd, so tests exercise the AS's real document fetch path
/// (HttpClient -> HttpListener).
/// </summary>
internal sealed class FakeCimdDocServer : IDisposable
{
    private readonly HttpListener _listener = new();

    public string ClientId { get; private set; } = "";

    public string RedirectUri { get; private set; } = "";

    public Dictionary<string, object> Document { get; set; } =
        new()
        {
            ["client_id"] = "placeholder",
            ["client_name"] = "Claude Web (test)",
            ["redirect_uris"] = new[]
            {
                "https://claude.example.test/api/mcp/auth_callback"
            },
            ["token_endpoint_auth_method"] = "none",
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["scope"] = "openid offline_access tools"
        };

    public async Task StartAsync()
    {
        var port = FreePort();

        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();

        ClientId = $"http://127.0.0.1:{port}/cimd";
        RedirectUri =
            "https://claude.example.test/api/mcp/auth_callback";

        Document["client_id"] = ClientId;

        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch
                {
                    break;
                }

                if (context.Request.Url!.AbsolutePath == "/cimd")
                {
                    var json = JsonSerializer.Serialize(Document);
                    var bytes = Encoding.UTF8.GetBytes(json);

                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(
                        bytes, 0, bytes.Length);
                    context.Response.Close();
                }
                else
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                }
            }
        });
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(
            IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // already closed
        }
    }
}