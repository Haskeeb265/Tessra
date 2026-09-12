using System.Security.Cryptography;

using Microsoft.IdentityModel.Tokens;

namespace Tessera.Platform.Api.McpOAuth;

/// <summary>
/// Signing + encryption material for the MCP OAuth authorization server.
///
/// Access tokens are signed RS256 JWTs (published via <c>/.well-known/jwks</c>)
/// so the Python gateway can validate them with the public key alone; refresh
/// tokens are encrypted JWTs, so the server also needs an AES (Aes256KW) key.
///
/// When <c>McpOAuth:KeyDirectory</c> is configured (a mounted volume under
/// Docker) the RSA private key and AES key are generated once and persisted
/// there, so issued tokens survive restarts. Without it — tests and quick
/// local runs — ephemeral in-memory keys are used.
/// </summary>
public static class McpOAuthKeys
{
    /// <summary>
    /// Stable key identifier advertised in the access-token JWT header and the
    /// JWKS document. The gateway refreshes its key cache on an unknown kid,
    /// so this value is a readable alias rather than a thumbprint.
    /// </summary>
    public const string SigningKeyId = "mcp-signing-v1";

    private const string EncryptionKeyId = "mcp-encryption-v1";

    private static readonly Lazy<(RsaSecurityKey Signing, SymmetricSecurityKey Encryption)> _ephemeralKeys =
        new(() => (CreateEphemeralRsaKey(), CreateEphemeralAesKey()));

    /// <summary>Loads (or, on first run, creates) the server's key material.</summary>
    public static (RsaSecurityKey Signing, SymmetricSecurityKey Encryption) Load(
        IConfiguration configuration)
    {
        var directory = McpOAuthConfig.KeyDirectory(configuration);

        if (string.IsNullOrWhiteSpace(directory))
        {
            return _ephemeralKeys.Value; // same instance every call
        }

        Directory.CreateDirectory(directory);

        return (
            LoadOrCreateRsaKey(Path.Combine(directory, "signing.pem")),
            LoadOrCreateAesKey(Path.Combine(directory, "encryption.key")));
    }

    private static RsaSecurityKey CreateEphemeralRsaKey() =>
        new(RSA.Create(4096)) { KeyId = SigningKeyId };

    private static SymmetricSecurityKey CreateEphemeralAesKey() =>
        new(RandomNumberGenerator.GetBytes(32)) { KeyId = EncryptionKeyId };

    private static RsaSecurityKey LoadOrCreateRsaKey(string path)
    {
        RSA rsa;

        if (File.Exists(path))
        {
            rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(path));
        }
        else
        {
            rsa = RSA.Create(4096);
            File.WriteAllText(path, rsa.ExportPkcs8PrivateKeyPem());
        }

        return new RsaSecurityKey(rsa) { KeyId = SigningKeyId };
    }



    /// <summary>
    /// Produces a JWKS document (RFC 7517) for the given signing key.
    /// Builds the JWK manually to avoid JsonWebKey.Create() issues with
    /// keys whose RSA parameters are not immediately exportable.
    /// </summary>
    public static object GetJwksDocument(RsaSecurityKey signingKey)
    {
        // Force the underlying RSA to generate/export its parameters.
        var rsa = signingKey.Rsa 
            ?? throw new InvalidOperationException(
                "Signing key has no underlying RSA object.");
        var parameters = rsa.ExportParameters(true);

        if (parameters.Modulus is null || parameters.Exponent is null)
        {
            throw new InvalidOperationException(
                "RSA key has no exportable modulus/exponent.");
        }

        return new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    kid = SigningKeyId,
                    alg = "RS256",
                    use = "sig",
                    n = Base64UrlEncoder.Encode(parameters.Modulus),
                    e = Base64UrlEncoder.Encode(parameters.Exponent)
                }
            }
        };
    }

    private static SymmetricSecurityKey LoadOrCreateAesKey(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var existing = Convert.FromBase64String(
                    File.ReadAllText(path).Trim());

                if (existing.Length == 32)
                {
                    return new SymmetricSecurityKey(existing)
                    {
                        KeyId = EncryptionKeyId
                    };
                }
            }
            catch (FormatException)
            {
                // Corrupt key file — fall through and regenerate.
            }
        }

        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(path, Convert.ToBase64String(key));

        return new SymmetricSecurityKey(key) { KeyId = EncryptionKeyId };
    }
}
