using Tessera.Platform.Api.Services;
using Xunit;

namespace Tessera.Platform.Tests;

public class TotpServiceTests
{
    // RFC 6238 Appendix B, SHA-1, secret = ASCII "12345678901234567890"
    // (Base32: GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ). The table lists time in
    // seconds since the Unix epoch; the TOTP counter is time / 30, which is
    // what Compute() takes. 6-digit values.
    private const string RfcSecret =
        "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(1, "287082")]          // time 59
    [InlineData(37037036, "081804")]   // time 1111111109
    [InlineData(37037037, "050471")]   // time 1111111111
    [InlineData(41152263, "005924")]   // time 1234567890
    [InlineData(66666666, "279037")]   // time 2000000000
    [InlineData(666666666, "353130")]  // time 20000000000
    public void Matches_rfc6238_sha1_vectors(long counter, string expected)
    {
        Assert.Equal(expected, TotpService.Compute(RfcSecret, counter));
    }

    [Fact]
    public void Generated_secret_is_32_base32_chars()
    {
        var secret = TotpService.GenerateSecret();

        Assert.Equal(32, secret.Length);
        Assert.All(secret, c => Assert.True(char.IsAsciiLetterUpper(c) || char.IsDigit(c)));
    }

    [Fact]
    public void Validate_rejects_wrong_code()
    {
        var secret = TotpService.GenerateSecret();

        Assert.False(TotpService.Validate(secret, "000000"));
        Assert.False(TotpService.Validate(secret, ""));
        Assert.False(TotpService.Validate(secret, "12345"));
    }
}
