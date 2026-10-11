using Xunit;

namespace CryptoTrading.Observability.Tests;

public sealed class SecretRedactorTests
{
    [Theory]
    [InlineData("BINANCE_API_KEY")]
    [InlineData("BINANCE_API_SECRET")]
    [InlineData("Authorization")]
    public void SensitiveNamesAreDetected(string name) => Assert.True(SecretRedactor.IsSensitiveName(name));

    [Fact]
    public void SensitiveValuesAreNeverReturned() =>
        Assert.Equal("[redacted]", SecretRedactor.Value("BINANCE_API_SECRET", "do-not-log"));

    [Fact]
    public void NonSensitiveValuesArePreserved() =>
        Assert.Equal("TESTNET", SecretRedactor.Value("environment", "TESTNET"));

    [Theory]
    [InlineData("https://example.test/diagnostics?access_token=QUERY_TOKEN_SENTINEL&symbol=BTCUSDT", "https://example.test/diagnostics?access_token=[redacted]&symbol=BTCUSDT")]
    [InlineData("https://user:URI_PASSWORD_SENTINEL@example.test/diagnostics", "https://user:[redacted]@example.test/diagnostics")]
    [InlineData("Authorization: Bearer HEADER_TOKEN_SENTINEL", "Authorization: [redacted]")]
    [InlineData("retry failed with bearer SCHEME_TOKEN_SENTINEL", "retry failed with bearer [redacted]")]
    public void EmbeddedSecretValuesAreRedactedWhenPropertyNameIsNotSensitive(string value, string expected)
    {
        var actual = Assert.IsType<string>(SecretRedactor.Value("diagnostic", value));

        Assert.Equal(expected, actual);
        Assert.DoesNotContain("SENTINEL", actual, StringComparison.Ordinal);
    }

    [Fact]
    public void UriValuesAreSanitizedBeforeTheyCanReachDiagnostics()
    {
        var value = new Uri("https://example.test/diagnostics?access_token=not-inspected-as-text");

        Assert.Equal(
            "https://example.test/diagnostics?access_token=[redacted]",
            SecretRedactor.Value("diagnostic", value));
    }
}
