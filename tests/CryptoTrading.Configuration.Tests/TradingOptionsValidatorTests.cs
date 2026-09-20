using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    public void ValidationFailuresNeverEchoCredentialValues()
    {
        const string apiKey = "test-key-that-must-not-leak";
        const string apiSecret = "test-secret-that-must-not-leak";
        var options = ValidOptions();
        options.Environment = "PRODUCTION";
        options.Binance.ApiKey = apiKey;
        options.Binance.ApiSecret = apiSecret;

        var result = new TradingOptionsValidator().Validate(Options.DefaultName, options);
        var failures = string.Join(Environment.NewLine, result.Failures ?? []);

        Assert.DoesNotContain(apiKey, failures, StringComparison.Ordinal);
        Assert.DoesNotContain(apiSecret, failures, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("https://testnet.binance.vision:8443/api")]
    [InlineData("https://ignored@testnet.binance.vision/api")]
    [InlineData("https://testnet.binance.vision/api?redirect=1")]
    [InlineData("https://testnet.binance.vision/api/v3")]
    public void AmbiguousOrNonCanonicalRestEndpointIsRejected(string endpoint)
    {
        var options = ValidOptions();
        options.Binance.RestBaseUrl = new Uri(endpoint);

        var result = new TradingOptionsValidator().Validate(Options.DefaultName, options);

        Assert.Contains(result.Failures!, failure => failure.Contains("REST", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ws://testnet.binance.vision/ws")]
    [InlineData("wss://testnet.binance.vision:8443/ws")]
    [InlineData("wss://ignored@testnet.binance.vision/ws")]
    [InlineData("wss://testnet.binance.vision/ws?streams=btcusdt")]
    [InlineData("wss://testnet.binance.vision/stream")]
    public void AmbiguousOrNonCanonicalWebSocketEndpointIsRejected(string endpoint)
    {
        var options = ValidOptions();
        options.Binance.WebSocketBaseUrl = new Uri(endpoint);

        var result = new TradingOptionsValidator().Validate(Options.DefaultName, options);

        Assert.Contains(result.Failures!, failure => failure.Contains("WS", StringComparison.Ordinal));
    }

    [Fact]
    public void ExternalCredentialFilesAreLoadedWhenDirectValuesAreAbsent()
    {
        using var directory = new TemporarySecretDirectory();
        var keyPath = directory.Write("api-key", "file-api-key");
        var secretPath = directory.Write("api-secret", "file-api-secret");
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["TRADING_ENVIRONMENT"] = "TESTNET",
            ["BINANCE_REST_BASE_URL"] = "https://testnet.binance.vision/api",
            ["BINANCE_WS_BASE_URL"] = "wss://testnet.binance.vision/ws",
            ["BINANCE_API_KEY_FILE"] = keyPath,
            ["BINANCE_API_SECRET_FILE"] = secretPath,
            ["BINANCE_WITHDRAWALS_ENABLED"] = "false",
            ["BINANCE_ALLOWED_SYMBOLS"] = "BTCUSDT"
        });

        using var provider = new ServiceCollection()
            .AddTradingConfiguration(configuration)
            .BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TradingOptions>>().Value;

        Assert.Equal("file-api-key", options.Binance.ApiKey);
        Assert.Equal("file-api-secret", options.Binance.ApiSecret);
    }

    [Fact]
    public void DirectCredentialValuesTakePrecedenceOverFilePaths()
    {
        using var directory = new TemporarySecretDirectory();
        var keyPath = directory.Write("api-key", "file-api-key");
        var secretPath = directory.Write("api-secret", "file-api-secret");
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["TRADING_ENVIRONMENT"] = "TESTNET",
            ["BINANCE_REST_BASE_URL"] = "https://testnet.binance.vision/api",
            ["BINANCE_WS_BASE_URL"] = "wss://testnet.binance.vision/ws",
            ["BINANCE_API_KEY"] = "direct-api-key",
            ["BINANCE_API_SECRET"] = "direct-api-secret",
            ["BINANCE_API_KEY_FILE"] = keyPath,
            ["BINANCE_API_SECRET_FILE"] = secretPath,
            ["BINANCE_WITHDRAWALS_ENABLED"] = "false",
            ["BINANCE_ALLOWED_SYMBOLS"] = "BTCUSDT"
        });

        using var provider = new ServiceCollection()
            .AddTradingConfiguration(configuration)
            .BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TradingOptions>>().Value;

        Assert.Equal("direct-api-key", options.Binance.ApiKey);
        Assert.Equal("direct-api-secret", options.Binance.ApiSecret);
    }

    [Fact]
    public void InsecureExternalCredentialFileIsRejectedOnUnix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var directory = new TemporarySecretDirectory();
        var keyPath = directory.Write("api-key", "file-api-key");
        var secretPath = directory.Write("api-secret", "file-api-secret");
        File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.GroupRead);
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["TRADING_ENVIRONMENT"] = "TESTNET",
            ["BINANCE_REST_BASE_URL"] = "https://testnet.binance.vision/api",
            ["BINANCE_WS_BASE_URL"] = "wss://testnet.binance.vision/ws",
            ["BINANCE_API_KEY_FILE"] = keyPath,
            ["BINANCE_API_SECRET_FILE"] = secretPath,
            ["BINANCE_WITHDRAWALS_ENABLED"] = "false",
            ["BINANCE_ALLOWED_SYMBOLS"] = "BTCUSDT"
        });

        using var provider = new ServiceCollection()
            .AddTradingConfiguration(configuration)
            .BuildServiceProvider();
        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<TradingOptions>>().Value);

        Assert.Contains("credentials", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file-api-key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutableExternalCredentialFileIsRejectedOnUnix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var directory = new TemporarySecretDirectory();
        var keyPath = directory.Write("api-key", "file-api-key");
        var secretPath = directory.Write("api-secret", "file-api-secret");
        File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["TRADING_ENVIRONMENT"] = "TESTNET",
            ["BINANCE_REST_BASE_URL"] = "https://testnet.binance.vision/api",
            ["BINANCE_WS_BASE_URL"] = "wss://testnet.binance.vision/ws",
            ["BINANCE_API_KEY_FILE"] = keyPath,
            ["BINANCE_API_SECRET_FILE"] = secretPath,
            ["BINANCE_WITHDRAWALS_ENABLED"] = "false",
            ["BINANCE_ALLOWED_SYMBOLS"] = "BTCUSDT"
        });

        using var provider = new ServiceCollection()
            .AddTradingConfiguration(configuration)
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<TradingOptions>>().Value);
    }

    [Fact]
    public void SymlinkExternalCredentialFileIsRejectedOnUnix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var directory = new TemporarySecretDirectory();
        var targetPath = directory.Write("api-key-target", "file-api-key");
        var keyPath = Path.Combine(Path.GetDirectoryName(targetPath)!, "api-key-link");
        File.CreateSymbolicLink(keyPath, targetPath);
        var secretPath = directory.Write("api-secret", "file-api-secret");
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["TRADING_ENVIRONMENT"] = "TESTNET",
            ["BINANCE_REST_BASE_URL"] = "https://testnet.binance.vision/api",
            ["BINANCE_WS_BASE_URL"] = "wss://testnet.binance.vision/ws",
            ["BINANCE_API_KEY_FILE"] = keyPath,
            ["BINANCE_API_SECRET_FILE"] = secretPath,
            ["BINANCE_WITHDRAWALS_ENABLED"] = "false",
            ["BINANCE_ALLOWED_SYMBOLS"] = "BTCUSDT"
        });

        using var provider = new ServiceCollection()
            .AddTradingConfiguration(configuration)
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<TradingOptions>>().Value);
    }

    private static IConfiguration CreateConfiguration(IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class TemporarySecretDirectory : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), $"cryptotrading-secrets-{Guid.NewGuid():N}");

        public TemporarySecretDirectory()
        {
            Directory.CreateDirectory(path);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        public string Write(string name, string value)
        {
            var file = Path.Combine(path, name);
            File.WriteAllText(file, value);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return file;
        }

        public void Dispose() => Directory.Delete(path, recursive: true);
    }
}
