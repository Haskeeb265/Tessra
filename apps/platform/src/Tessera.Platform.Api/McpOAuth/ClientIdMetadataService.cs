using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Caching.Memory;

using OpenIddict.Abstractions;

using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Tessera.Platform.Api.McpOAuth;

/// <summary>
/// Client ID Metadata Document (CIMD, OAuth 2.1 — see docs/mcp/README.md
/// §8.4). MCP clients like Claude web register by using an HTTPS URL as
/// their <c>client_id</c>; the authorization server fetches this document
/// from that URL to learn the client's redirect URIs and metadata. Only the
/// fields this AS consumes are modeled.
/// </summary>
public sealed class ClientIdMetadataDocument
{
    [JsonPropertyName("client_id")]
    public string? ClientId { get; set; }

    [JsonPropertyName("client_name")]
    public string? ClientName { get; set; }

    [JsonPropertyName("redirect_uris")]
    public List<string>? RedirectUris { get; set; }

    [JsonPropertyName("token_endpoint_auth_method")]
    public string? TokenEndpointAuthMethod { get; set; }

    [JsonPropertyName("grant_types")]
    public List<string>? GrantTypes { get; set; }

    [JsonPropertyName("response_types")]
    public List<string>? ResponseTypes { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
}

/// <summary>Thrown when a CIMD client cannot be validated or registered.</summary>
public sealed class CimdRegistrationException : Exception
{
    public CimdRegistrationException(string message) : base(message)
    {
    }
}

/// <summary>
/// Registers MCP clients from their Client ID Metadata Documents.
///
/// Flow (per the MCP authorization spec): a client whose <c>client_id</c> is
/// an HTTPS URL is not pre-registered in the store — the AS fetches the
/// document at that URL, validates it (client_id must equal the URL, redirect
/// URIs must be https, or loopback in dev), and upserts an OpenIddict
/// application on first use. Documents are cached in memory (TTL honors
/// Cache-Control, default 10 min) so the fetch happens once per doc.
///
/// OpenIddict has no built-in CIMD/DCR support (tracked upstream for 8.0), so
/// this is custom work — see docs/mcp/README.md §12 item 1.
/// </summary>
public class ClientIdMetadataService
{
    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxCacheTtl = TimeSpan.FromHours(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly IOpenIddictApplicationManager _applications;
    private readonly IConfiguration _configuration;

    public ClientIdMetadataService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IOpenIddictApplicationManager applications,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _applications = applications;
        _configuration = configuration;
    }

    /// <summary>
    /// Whether <paramref name="clientId"/> looks like a CIMD URL client id:
    /// an absolute URI. https is always accepted; http is only accepted for
    /// loopback addresses (used by local dev clients and integration tests).
    /// </summary>
    public bool IsCimdClientId(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return false;
        }

        if (!Uri.TryCreate(clientId, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http"))
        {
            return false;
        }

        if (uri.Scheme == "https")
        {
            return true;
        }

        // http is only ever acceptable on loopback.
        return uri.IsLoopback;
    }

    /// <summary>
    /// Ensures an OpenIddict application exists for <paramref name="clientId"/>,
    /// fetching + validating its CIMD document when it doesn't.
    /// </summary>
    public async Task EnsureRegisteredAsync(
        string clientId,
        CancellationToken cancellationToken)
    {
        if (!IsCimdClientId(clientId))
        {
            throw new CimdRegistrationException(
                "The client_id must be an HTTPS URL " +
                "(Client ID Metadata Document) or a loopback URI.");
        }

        if (await _applications.FindByClientIdAsync(clientId) is not null)
        {
            return;
        }

        var document = await FetchDocumentAsync(clientId, cancellationToken);

        ValidateDocument(clientId, document);

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            DisplayName = string.IsNullOrWhiteSpace(document.ClientName)
                ? clientId
                : document.ClientName,
            // MCP clients are public: PKCE is mandatory, no client secret.
            ClientType = ClientTypes.Public,
            ApplicationType = ApplicationTypes.Native,
            ConsentType = ConsentTypes.Explicit
        };

        foreach (var redirectUri in document.RedirectUris!)
        {
            descriptor.RedirectUris.Add(
                new Uri(redirectUri, UriKind.Absolute));
        }

        descriptor.Permissions.Add(Permissions.Endpoints.Authorization);
        descriptor.Permissions.Add(Permissions.Endpoints.Token);
        descriptor.Permissions.Add(Permissions.GrantTypes.AuthorizationCode);
        descriptor.Permissions.Add(Permissions.GrantTypes.RefreshToken);
        descriptor.Permissions.Add(Permissions.ResponseTypes.Code);
        descriptor.Permissions.Add(
            Permissions.Prefixes.Scope + McpOAuthConstants.ScopeTools);
        descriptor.Permissions.Add(
            Permissions.Prefixes.Scope +
            OpenIddictConstants.Scopes.OfflineAccess);

        // Honor any registered scopes the doc advertises.
        if (!string.IsNullOrWhiteSpace(document.Scope))
        {
            foreach (var scope in document.Scope.Split(
                         ' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (scope is McpOAuthConstants.ScopeTools or
                    OpenIddictConstants.Scopes.OfflineAccess)
                {
                    descriptor.Permissions.Add(
                        Permissions.Prefixes.Scope + scope);
                }
            }
        }

        try
        {
            await _applications.CreateAsync(descriptor);
        }
        catch
        {
            // A concurrent authorize request may have registered the same
            // client first — if it now exists, that's fine; otherwise the
            // registration genuinely failed and must surface.
            if (await _applications.FindByClientIdAsync(clientId) is null)
            {
                throw;
            }
        }
    }

    /// <summary>
    /// Validates a fetched CIMD document: <c>client_id</c> must equal the
    /// requested URL exactly, and every redirect URI must be an absolute
    /// https URI (http loopback allowed in dev). Throws
    /// <see cref="CimdRegistrationException"/> on any violation.
    /// </summary>
    private void ValidateDocument(
        string clientId,
        ClientIdMetadataDocument document)
    {
        if (!string.Equals(
                document.ClientId,
                clientId,
                StringComparison.Ordinal))
        {
            throw new CimdRegistrationException(
                "The client_id in the metadata document does not match " +
                "the requested client_id URL.");
        }

        if (document.RedirectUris is not { Count: > 0 })
        {
            throw new CimdRegistrationException(
                "The client metadata document must declare at least one " +
                "redirect_uri.");
        }

        var allowLoopbackHttp = !IsProduction;

        foreach (var raw in document.RedirectUris)
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
                uri.Fragment.Length > 0)
            {
                throw new CimdRegistrationException(
                    $"Invalid redirect_uri '{raw}' in client metadata.");
            }

            var valid = uri.Scheme switch
            {
                "https" => true,
                "http" => allowLoopbackHttp && uri.IsLoopback,
                _ => false
            };

            if (!valid)
            {
                throw new CimdRegistrationException(
                    $"redirect_uri '{raw}' must be https " +
                    "(or http://127.0.0.1 in development).");
            }
        }
    }

    /// <summary>
    /// Fetches + caches the CIMD document for <paramref name="clientId"/>.
    /// The cache TTL honors the response's Cache-Control max-age when
    /// present, otherwise <see cref="DefaultCacheTtl"/>.
    /// </summary>
    private async Task<ClientIdMetadataDocument> FetchDocumentAsync(
        string clientId,
        CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(clientId, out ClientIdMetadataDocument? cached) &&
            cached is not null)
        {
            return cached;
        }

        using var http = _httpClientFactory.CreateClient("cimd");

        using var request = new HttpRequestMessage(
            HttpMethod.Get, clientId);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(
                request, cancellationToken);
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException)
        {
            throw new CimdRegistrationException(
                $"Could not fetch the client metadata document at " +
                $"{clientId}: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new CimdRegistrationException(
                    $"Fetching the client metadata document at {clientId} " +
                    $"returned HTTP {(int)response.StatusCode}.");
            }

            ClientIdMetadataDocument document;

            try
            {
                document = await response.Content
                    .ReadFromJsonAsync<ClientIdMetadataDocument>(
                        cancellationToken)
                    ?? throw new JsonException("Empty document.");
            }
            catch (JsonException)
            {
                throw new CimdRegistrationException(
                    $"The client metadata document at {clientId} is not " +
                    "valid JSON.");
            }

            var ttl = TtlFromResponse(response);

            _cache.Set(clientId, document, ttl);

            return document;
        }
    }

    private static TimeSpan TtlFromResponse(HttpResponseMessage response)
    {
        var cacheControl = response.Headers.CacheControl;

        if (cacheControl?.MaxAge is { } maxAge)
        {
            return maxAge > MaxCacheTtl ? MaxCacheTtl : maxAge;
        }

        return DefaultCacheTtl;
    }

    private bool IsProduction =>
        string.Equals(
            _configuration["ASPNETCORE_ENVIRONMENT"] ??
            _configuration["DOTNET_ENVIRONMENT"],
            "Production",
            StringComparison.OrdinalIgnoreCase);
}