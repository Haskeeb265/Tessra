using System.Security.Cryptography;
using System.Text;

namespace Tessera.Platform.Api.Services;

/// <summary>
/// Minimal RFC 6238 TOTP implementation (HMAC-SHA1, 30-second step,
/// 6 digits, base32 secrets) so MFA needs no third-party package.
/// </summary>
public static class TotpService
{
    private const int StepSeconds = 30;
    private const int Digits = 6;
    private static readonly DateTime UnixEpoch =
        new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Generates a new random base32 secret (32 chars).</summary>
    public static string GenerateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        return Base32Encode(bytes);
    }

    /// <summary>otpauth:// URI for authenticator-app enrollment.</summary>
    public static string GenerateOtpAuthUri(string secret, string account)
    {
        return
            $"otpauth://totp/Tessera:{Uri.EscapeDataString(account)}" +
            $"?secret={secret}&issuer=Tessera&algorithm=SHA1" +
            $"&digits={Digits}&period={StepSeconds}";
    }

    /// <summary>
    /// Validates a 6-digit code against the secret, allowing a window of
    /// ±<paramref name="window"/> time steps around the current one.
    /// </summary>
    public static bool Validate(string secret, string code, int window = 1)
    {
        if (string.IsNullOrWhiteSpace(secret) ||
            string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var normalized = Normalize(code);

        if (normalized.Length != Digits ||
            !normalized.All(char.IsDigit))
        {
            return false;
        }

        var counter =
            (long)(DateTime.UtcNow - UnixEpoch).TotalSeconds / StepSeconds;

        for (var i = -window; i <= window; i++)
        {
            if (Compute(secret, counter + i) == normalized)
            {
                return true;
            }
        }

        return false;
    }

    // Internal so the test project can verify against RFC 6238 vectors.
    internal static string Compute(string secret, long counter)
    {
        var key = Base32Decode(secret);

        var counterBytes = BitConverter.GetBytes(counter);

        // TOTP uses big-endian (network byte order).
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counterBytes);
        }

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(counterBytes);

        var offset = hash[^1] & 0x0F;

        var binary =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        var otp = binary % (int)Math.Pow(10, Digits);

        return otp.ToString("D6");
    }

    private static string Normalize(string code) =>
        code.Trim().Replace(" ", string.Empty);

    // ================================================================
    // RFC 4648 base32 (no padding)
    // ================================================================

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder();

        for (var i = 0; i < data.Length;)
        {
            var buffer = new byte[5];
            var count = Math.Min(5, data.Length - i);
            Array.Copy(data, i, buffer, 0, count);
            i += count;

            var value =
                ((uint)buffer[0] << 32) |
                ((uint)buffer[1] << 24) |
                ((uint)buffer[2] << 16) |
                ((uint)buffer[3] << 8) |
                buffer[4];

            var bits = count * 8;

            for (var j = 0; j < (bits + 4) / 5; j++)
            {
                var shift = 32 - 5 * (j + 1);
                sb.Append(Alphabet[(int)((value >> shift) & 0x1F)]);
            }
        }

        return sb.ToString();
    }

    private static byte[] Base32Decode(string input)
    {
        var cleaned = input.Trim()
            .ToUpperInvariant()
            .Replace("=", string.Empty)
            .Replace(" ", string.Empty);

        var outputLength = cleaned.Length * 5 / 8;
        var output = new byte[outputLength];

        var bitBuffer = 0;
        var bitCount = 0;
        var outIndex = 0;

        foreach (var character in cleaned)
        {
            var index = Alphabet.IndexOf(character);

            if (index < 0)
            {
                continue;
            }

            bitBuffer = (bitBuffer << 5) | index;
            bitCount += 5;

            if (bitCount >= 8)
            {
                output[outIndex++] =
                    (byte)((bitBuffer >> (bitCount - 8)) & 0xFF);
                bitCount -= 8;
            }
        }

        return output;
    }
}
