using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CryptoTrading.Configuration.Tests;

public sealed class PostgresConnectionConfigurationTests
{
    [Fact]
    public void PrivateFileBuildsEscapedConnectionStringWithoutInjectingSettings()
    {
        using var fixture = new PasswordFixture("fixture;Pooling=false;quote=\"value\"");
        if (!OperatingSystem.IsLinux())
        {
            Assert.Throws<InvalidOperationException>(() => PostgresConnectionConfiguration.Resolve(fixture.Configuration()));
            return;
        }

        var result = PostgresConnectionConfiguration.Resolve(fixture.Configuration());
        var parsed = new DbConnectionStringBuilder { ConnectionString = result };

        Assert.Equal("fixture;Pooling=false;quote=\"value\"", parsed["Password"]);
        Assert.Equal("localhost", parsed["Host"]);
        Assert.Equal("5432", parsed["Port"]);
        Assert.Equal("trading", parsed["Database"]);
        Assert.False(parsed.ContainsKey("Pooling"));
    }

    [Fact]
    public void LegacyHostConnectionStringRemainsSupported()
    {
        var configuration = Config(new Dictionary<string, string?>
        {
            ["POSTGRES_CONNECTION_STRING"] = "Host=localhost;Database=fixture;Username=fixture",
        });
        Assert.Equal("Host=localhost;Database=fixture;Username=fixture", PostgresConnectionConfiguration.Resolve(configuration));
    }

    [Fact]
    public void BlankFileSettingDoesNotShadowLegacyHostDsn()
    {
        var configuration = Config(new Dictionary<string, string?>
        {
            ["POSTGRES_CONNECTION_STRING"] = "Host=localhost;Database=fixture;Username=fixture",
            ["POSTGRES_PASSWORD_FILE"] = "  ",
        });
        Assert.Equal("Host=localhost;Database=fixture;Username=fixture", PostgresConnectionConfiguration.Resolve(configuration));
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void FileModeAcceptsOnlyExplicitLocalDestinations(string host)
    {
        using var fixture = new PasswordFixture("fixture-local-only");
        if (!OperatingSystem.IsLinux())
        {
            Assert.Throws<InvalidOperationException>(() =>
                PostgresConnectionConfiguration.Resolve(fixture.Configuration("POSTGRES_HOST", host)));
            return;
        }

        var parsed = new DbConnectionStringBuilder
        {
            ConnectionString = PostgresConnectionConfiguration.Resolve(fixture.Configuration("POSTGRES_HOST", host)),
        };
        Assert.Equal(host, parsed["Host"]);
    }

    [Theory]
    [InlineData("db.attacker.invalid")]
    [InlineData("8.8.8.8")]
    public void FileModeRejectsRemoteCredentialDestinations(string host)
    {
        using var fixture = new PasswordFixture("fixture-local-only");
        Assert.Throws<InvalidOperationException>(() =>
            PostgresConnectionConfiguration.Resolve(fixture.Configuration("POSTGRES_HOST", host)));
    }

    [Fact]
    public void OversizedPasswordFileFailsClosed()
    {
        using var fixture = new PasswordFixture(new string('a', 4097));
        Assert.Throws<InvalidOperationException>(() => PostgresConnectionConfiguration.Resolve(fixture.Configuration()));
    }

    [Fact]
    public void ParentTraversalPathIsRejectedInsteadOfNormalized()
    {
        using var fixture = new PasswordFixture("fixture-traversal");
        var child = Path.Combine(fixture.DirectoryPath, "child");
        Directory.CreateDirectory(child);
        var traversal = Path.Combine(child, "..", "postgres_password");
        Assert.Throws<InvalidOperationException>(() =>
            PostgresConnectionConfiguration.Resolve(fixture.Configuration("POSTGRES_PASSWORD_FILE", traversal)));
    }

    [Fact]
    public void MissingConfigurationLeavesPersistenceUnconfigured()
    {
        Assert.Equal(string.Empty, PostgresConnectionConfiguration.Resolve(Config([])));
    }

    [Fact]
    public void FileAndDirectConnectionStringAreRejectedAsAmbiguous()
    {
        using var fixture = new PasswordFixture("fixture-not-a-real-secret");
        var configuration = fixture.Configuration("POSTGRES_CONNECTION_STRING", "Host=unrelated");
        Assert.Throws<InvalidOperationException>(() => PostgresConnectionConfiguration.Resolve(configuration));
    }

    [Theory]
    [InlineData("POSTGRES_PORT", "0")]
    [InlineData("POSTGRES_PORT", "65536")]
    [InlineData("POSTGRES_PORT", "5432;Pooling=false")]
    [InlineData("POSTGRES_HOST", "localhost;Username=other")]
    [InlineData("POSTGRES_DB", "")]
    [InlineData("POSTGRES_USER", "")]
    [InlineData("POSTGRES_PASSWORD", "forbidden-direct-fixture")]
    public void InvalidMetadataOrDirectPasswordFailsClosed(string key, string value)
    {
        using var fixture = new PasswordFixture("fixture-never-reproduced");
        var exception = Assert.Throws<InvalidOperationException>(() =>
            PostgresConnectionConfiguration.Resolve(fixture.Configuration(key, value)));
        Assert.DoesNotContain("fixture-never-reproduced", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.FilePath, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)]
    public void UnsafeFilePermissionsFailClosed(UnixFileMode mode)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var fixture = new PasswordFixture("fixture-permissions");
        File.SetUnixFileMode(fixture.FilePath, mode);
        Assert.Throws<InvalidOperationException>(() => PostgresConnectionConfiguration.Resolve(fixture.Configuration()));
    }

    [Fact]
    public void SymlinkFileAndParentAreRejected()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var fixture = new PasswordFixture("fixture-symlink");
        var link = Path.Combine(fixture.DirectoryPath, "link");
        File.CreateSymbolicLink(link, fixture.FilePath);
        Assert.Throws<InvalidOperationException>(() =>
            PostgresConnectionConfiguration.Resolve(fixture.Configuration("POSTGRES_PASSWORD_FILE", link)));
        var directoryLink = fixture.DirectoryPath + "-link";
        try
        {
            Directory.CreateSymbolicLink(directoryLink, fixture.DirectoryPath);
            Assert.Throws<InvalidOperationException>(() => PostgresConnectionConfiguration.Resolve(
                fixture.Configuration("POSTGRES_PASSWORD_FILE", Path.Combine(directoryLink, "postgres_password"))));
        }
        finally
        {
            Directory.Delete(directoryLink);
        }
    }

    [Fact]
    public void MissingAndEmptyFilesFailWithoutLeakingPath()
    {
        using var fixture = new PasswordFixture(string.Empty);
        Assert.Throws<InvalidOperationException>(() => PostgresConnectionConfiguration.Resolve(fixture.Configuration()));
        File.Delete(fixture.FilePath);
        var exception = Assert.Throws<InvalidOperationException>(() => PostgresConnectionConfiguration.Resolve(fixture.Configuration()));
        Assert.DoesNotContain(fixture.FilePath, exception.ToString(), StringComparison.Ordinal);
    }

    private static IConfigurationRoot Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class PasswordFixture : IDisposable
    {
        public PasswordFixture(string content)
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"cryptotrading-pg-password-fixture-{Guid.NewGuid():N}");
            Directory.CreateDirectory(DirectoryPath);
            FilePath = Path.Combine(DirectoryPath, "postgres_password");
            File.WriteAllText(FilePath, content);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        public string DirectoryPath { get; }
        public string FilePath { get; }

        public IConfigurationRoot Configuration(string? key = null, string? value = null)
        {
            var values = new Dictionary<string, string?>
            {
                ["POSTGRES_HOST"] = "localhost",
                ["POSTGRES_PORT"] = "5432",
                ["POSTGRES_DB"] = "trading",
                ["POSTGRES_USER"] = "trading_user",
                ["POSTGRES_PASSWORD_FILE"] = FilePath,
            };
            if (key is not null)
            {
                values[key] = value;
            }

            return Config(values);
        }

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
