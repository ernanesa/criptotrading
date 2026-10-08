using CryptoTrading.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;

namespace CryptoTrading.Api.Tests;

public sealed class TradingReadinessCheckTests
{
    [Fact]
    public async Task HealthyWhenTestnetConfigurationIsCompleteAndSafe()
    {
        var options = Options.Create(ValidTradingOptions());
        var check = new TradingReadinessCheck(options);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task HealthyWhenBothCredentialsAreAbsentForReadOnlyRuntime()
    {
        var options = ValidTradingOptions();
        options.Binance.ApiKey = "";
        options.Binance.ApiSecret = "";

        var result = await new TradingReadinessCheck(Options.Create(options))
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task UnhealthyWhenWithdrawalsAreEnabled()
    {
        var options = ValidTradingOptions();
        options.Binance.WithdrawalsEnabled = true;

        var check = new TradingReadinessCheck(Options.Create(options));
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("unsafe", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnhealthyWhenEnvironmentIsNotTestnet()
    {
        var options = ValidTradingOptions();
        options.Environment = "PRODUCTION";

        var check = new TradingReadinessCheck(Options.Create(options));
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Theory]
    [InlineData("", "secret")]
    [InlineData("key", "")]
    public async Task UnhealthyWhenCredentialsAreMissing(string apiKey, string apiSecret)
    {
        var options = ValidTradingOptions();
        options.Binance.ApiKey = apiKey;
        options.Binance.ApiSecret = apiSecret;

        var check = new TradingReadinessCheck(Options.Create(options));
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task UnhealthyWhenAllowedSymbolsAreEmpty()
    {
        var options = ValidTradingOptions();
        options.Binance.AllowedSymbols = "";

        var check = new TradingReadinessCheck(Options.Create(options));
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    private static TradingOptions ValidTradingOptions() => new()
    {
        Environment = "TESTNET",
        Binance = new BinanceOptions
        {
            RestBaseUrl = new Uri("https://testnet.binance.vision/api/"),
            WebSocketBaseUrl = new Uri("wss://testnet.binance.vision/ws-api/v3"),
            ApiKey = "testnet-api-key",
            ApiSecret = "testnet-api-secret",
            WithdrawalsEnabled = false,
            AllowedSymbols = "BTCUSDT"
        }
    };
}
