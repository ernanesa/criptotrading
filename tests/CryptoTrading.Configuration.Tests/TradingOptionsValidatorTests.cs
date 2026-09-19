using CryptoTrading.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace CryptoTrading.Configuration.Tests;

public sealed class TradingOptionsValidatorTests
{
    private static TradingOptions ValidOptions() => new()
    {
        Environment = "TESTNET",
        Binance = new BinanceOptions
        {
            RestBaseUrl = new Uri("https://testnet.binance.vision/api"),
            WebSocketBaseUrl = new Uri("wss://testnet.binance.vision/ws"),
            ApiKey = "test-key",
            ApiSecret = "test-secret",
            AllowedSymbols = "BTCUSDT",
            WithdrawalsEnabled = false
        }
    };

    [Fact]
    public void ValidTestnetConfigurationIsAccepted()
    {
        var result = new TradingOptionsValidator().Validate(Options.DefaultName, ValidOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ProductionEnvironmentIsRejected()
    {
        var options = ValidOptions();
        options.Environment = "PRODUCTION";

        var result = new TradingOptionsValidator().Validate(Options.DefaultName, options);

        Assert.Contains(result.Failures!, failure => failure.Contains("TESTNET", StringComparison.Ordinal));
    }

    [Fact]
    public void WithdrawalsAreRejectedEvenWithTestnetEndpoint()
    {
        var options = ValidOptions();
        options.Binance.WithdrawalsEnabled = true;

        var result = new TradingOptionsValidator().Validate(Options.DefaultName, options);

        Assert.Contains(result.Failures!, failure => failure.Contains("Withdrawals", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingCredentialsAreRejected()
    {
        var options = ValidOptions();
        options.Binance.ApiKey = string.Empty;
        options.Binance.ApiSecret = string.Empty;

        var result = new TradingOptionsValidator().Validate(Options.DefaultName, options);

        Assert.Contains(result.Failures!, failure => failure.Contains("credentials", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProductionBinanceEndpointIsRejected()
    {
        var options = ValidOptions();
        options.Binance.RestBaseUrl = new Uri("https://api.binance.com/api");
        options.Binance.WebSocketBaseUrl = new Uri("wss://stream.binance.com/ws");

        var result = new TradingOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.True(result.Failures!.Count() >= 2);
    }
}
