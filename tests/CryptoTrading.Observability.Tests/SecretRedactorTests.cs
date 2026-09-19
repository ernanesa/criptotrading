using CryptoTrading.Observability;
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
}
