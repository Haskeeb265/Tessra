using OpenIddict.Server;

using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Tessera.Platform.Api.McpOAuth;

/// <summary>
/// Registers CIMD (Client ID Metadata Document) clients while OpenIddict
/// validates an authorization request — before its built-in client lookup.
///
/// MCP hosts such as Claude web are not pre-registered: they present an HTTPS
/// URL as their <c>client_id</c>. <see cref="ClientIdMetadataService"/> fetches
/// and validates that document and upserts the OpenIddict application, so the
/// rest of the pipeline (and the <c>/connect/authorize</c> handler) finds a
/// registered client. See docs/mcp/README.md §8.4.
/// </summary>
public sealed class CimdAuthorizationRequestHandler
    : IOpenIddictServerHandler<OpenIddictServerEvents.ValidateAuthorizationRequestContext>
{
    private readonly ClientIdMetadataService _clientIdMetadata;

    public CimdAuthorizationRequestHandler(ClientIdMetadataService clientIdMetadata) =>
        _clientIdMetadata = clientIdMetadata;

    public async ValueTask HandleAsync(
        OpenIddictServerEvents.ValidateAuthorizationRequestContext context)
    {
        var clientId = context.Request.ClientId;

        if (string.IsNullOrWhiteSpace(clientId) ||
            !_clientIdMetadata.IsCimdClientId(clientId))
        {
            return;
        }

        try
        {
            await _clientIdMetadata.EnsureRegisteredAsync(
                clientId, context.CancellationToken);
        }
        catch (CimdRegistrationException exception)
        {
            // A malformed or impostor metadata document must fail the request
            // as invalid_client rather than surfacing as a 500.
            context.Reject(Errors.InvalidClient, exception.Message);
        }
    }
}
