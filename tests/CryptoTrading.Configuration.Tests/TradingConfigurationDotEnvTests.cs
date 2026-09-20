using Microsoft.Extensions.Configuration;
using Xunit;

namespace CryptoTrading.Configuration.Tests;

public sealed class TradingConfigurationDotEnvTests
{
    [Fact]
    public void PrivateDotEnvFileIsLoaded()
    {
        using var file = TemporaryDotEnv.Create("DOTENV_TEST_VALUE=loaded");

        var configuration = new ConfigurationBuilder()
            .AddLocalDotEnv(file.Path)
            .Build();

        Assert.Equal("loaded", configuration["DOTENV_TEST_VALUE"]);
    }

    [Fact]
    public void GroupReadableDotEnvFileIsIgnoredOnUnix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var file = TemporaryDotEnv.Create("DOTENV_TEST_VALUE=must-not-load");
        File.SetUnixFileMode(file.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var configuration = new ConfigurationBuilder()
            .AddLocalDotEnv(file.Path)
            .Build();

        Assert.Null(configuration["DOTENV_TEST_VALUE"]);
    }

    private sealed class TemporaryDotEnv : IDisposable
    {
        private readonly string directory;

        private TemporaryDotEnv(string path, string directory)
        {
            Path = path;
            this.directory = directory;
        }

        public string Path { get; }

        public static TemporaryDotEnv Create(string content)
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cryptotrading-dotenv-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, ".env");
            File.WriteAllText(path, content);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return new TemporaryDotEnv(path, directory);
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
