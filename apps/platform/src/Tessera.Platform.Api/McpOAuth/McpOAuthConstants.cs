namespace Tessera.Platform.Api.McpOAuth;

/// <summary>
/// Constants shared by the MCP OAuth authorization server surface
/// (/connect/*) and the interactive login/consent flow hosted in the Next.js
/// portal. See docs/mcp-auth-platform.md for the conformance contract.
/// </summary>
public static class McpOAuthConstants
{
    /// <summary>Cookie authentication scheme for the interactive OAuth flow.</summary>
    public const string CookieScheme = "TesseraMcpOAuth";

    public const string CookieName = "tessera.mcp_oauth";

    // Claims carried on both the interactive cookie and the OAuth tokens.
    public const string ClaimTenantId = "tenant_id";
    public const string ClaimTenantIdentifier = "tenant_identifier";
    public const string ClaimTokenVersion = "token_version";

    /// <summary>
    /// Coarse v1 scope: "access this workspace's MCP tools". Per-tenant tools
    /// scopes (manifest required_scopes) stay aspirational (docs/mcp.md §2.8).
    /// </summary>
    public const string ScopeTools = "tools";

    /// <summary>Pre-registered dev client used by the scripted PKCE test harness.</summary>
    public const string LocalDevClientId = "tessera-local-dev";

    /// <summary>Loopback redirect accepted for the local dev client (port-agnostic on 127.0.0.1).</summary>
    public const string LocalDevRedirectUri = "http://127.0.0.1:9876/callback";

    /// <summary>
    /// Callback URL that MCP hosts (Claude web, Claude Desktop, mobile, Cowork)
    /// use to receive the authorization code after the user consents. Register
    /// this exact URI on any pre-registered OAuth client or CIMD document.
    /// </summary>
    public const string ClaudeAuthCallbackUrl =
        "https://claude.ai/api/mcp/auth_callback";

    /// <summary>
    /// The CIMD document URL that Claude web presents as its <c>client_id</c>
    /// when the connector is configured with "Use Claude's published identity".
    /// Our <see cref="ClientIdMetadataService"/> fetches this document to learn
    /// Claude's redirect URIs and metadata.
    /// </summary>
    public const string ClaudeCimdDocumentUrl =
        "https://claude.ai/api/mcp/client_metadata";

    /// <summary>
    /// The OAuth 2.0 "none" client authentication method value, used by public
    /// clients (PKCE, no client secret) including CIMD/DCR-registered MCP
    /// clients such as Claude web. Must be registered on the token endpoint so
    /// the discovery document includes token_endpoint_auth_methods_supported:
    /// [..., "none"].
    /// </summary>
    public const string ClientAuthenticationMethodNone =
        "none";
}

/// <summary>
/// Centralized reading of the McpOAuth configuration section with dev defaults.
/// Expected shape (appsettings or env):
///   McpOAuth:Issuer          e.g. https://tessera.local
///   McpOAuth:PortalBaseUrl   e.g. https://tessera.local
///   McpOAuth:AccessTokenLifetimeMinutes   (default 15)
///   McpOAuth:RefreshTokenLifetimeDays     (default 14)
///   McpOAuth:KeyDirectory     directory to persist dev signing/encryption keys
///   McpOAuth:SigningKeyPem / McpOAuth:EncryptionKeyBase64 (optional overrides)
/// </summary>
public static class McpOAuthConfig
{
    public static string Issuer(IConfiguration configuration) =>
        configuration["McpOAuth:Issuer"] ?? "https://tessera.local";

    public static string PortalBaseUrl(IConfiguration configuration) =>
        configuration["McpOAuth:PortalBaseUrl"] ?? "https://tessera.local";

    public static string McpBaseUrl(IConfiguration configuration) =>
        configuration["McpOAuth:McpBaseUrl"] ?? "https://tessera.local";

    public static TimeSpan AccessTokenLifetime(IConfiguration configuration) =>
        TimeSpan.FromMinutes(
            configuration.GetValue<int?>("McpOAuth:AccessTokenLifetimeMinutes") ?? 15);

    public static TimeSpan RefreshTokenLifetime(IConfiguration configuration) =>
        TimeSpan.FromDays(
            configuration.GetValue<int?>("McpOAuth:RefreshTokenLifetimeDays") ?? 14);

    public static TimeSpan CookieLifetime(IConfiguration configuration) =>
        TimeSpan.FromHours(
            configuration.GetValue<int?>("McpOAuth:CookieLifetimeHours") ?? 12);

    public static string? KeyDirectory(IConfiguration configuration) =>
        configuration["McpOAuth:KeyDirectory"];
}
