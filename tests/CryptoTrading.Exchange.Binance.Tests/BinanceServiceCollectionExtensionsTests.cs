using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CryptoTrading.Exchange.Binance.Tests;

public sealed class BinanceServiceCollectionExtensionsTests
{
    [Fact]
    public void AuthenticatedClientRegistrationLoadsPrivateCredentialFiles()
    {
        using var secrets = new TemporarySecretDirectory();
        var keyPath = secrets.Write("api-key", "file-api-key");
        var secretPath = secrets.Write("api-secret", "file-api-secret");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TRADING_ENVIRONMENT"] = "TESTNET",
                ["BINANCE_REST_BASE_URL"] = "https://testnet.binance.vision/api/",
                ["BINANCE_API_KEY_FILE"] = keyPath,
                ["BINANCE_API_SECRET_FILE"] = secretPath
            })
            .Build();

        using var provider = new ServiceCollection()
            .AddBinanceSpotAccountClientFromConfiguration(configuration)
            .BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<BinanceAuthenticatedClientOptions>>().Value;

        Assert.Equal("file-api-key", options.ApiKey);
        Assert.Equal("file-api-secret", options.ApiSecret);
    }

    private sealed class TemporarySecretDirectory : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"cryptotrading-binance-di-{Guid.NewGuid():N}");

        public TemporarySecretDirectory()
        {
            Directory.CreateDirectory(directory);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        public string Write(string name, string value)
        {
            var path = Path.Combine(directory, name);
            File.WriteAllText(path, value);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return path;
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
