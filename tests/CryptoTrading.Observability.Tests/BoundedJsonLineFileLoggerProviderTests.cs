using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;

#pragma warning disable CA1848 // Test-only log calls exercise the provider directly.
#pragma warning disable CA1873

namespace CryptoTrading.Observability.Tests;

public sealed class BoundedJsonLineFileLoggerProviderTests
{
    [Fact]
    public void PersistsStructuredRecordsAndFlushesOnDispose()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cryptotrading-log-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "worker.jsonl");
        try
        {
            using (var provider = new BoundedJsonLineFileLoggerProvider(path, maxFileBytes: 1_048_576L, retainedFiles: 2))
            {
                var logger = provider.CreateLogger("CryptoTrading.Tests");
                logger.LogInformation("decision {Outcome} for {Symbol}", "NO_TRADE", "BTCUSDT");
                logger.LogError(new InvalidOperationException("boom"), "cycle failed");
                Assert.Equal(0, provider.DroppedRecords);
            }

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);

            using var first = JsonDocument.Parse(lines[0]);
            Assert.Equal("Information", first.RootElement.GetProperty("Level").GetString());
            Assert.Equal("CryptoTrading.Tests", first.RootElement.GetProperty("Category").GetString());
            Assert.Contains("NO_TRADE", first.RootElement.GetProperty("Message").GetString(), StringComparison.Ordinal);

            using var second = JsonDocument.Parse(lines[1]);
            Assert.Equal("Error", second.RootElement.GetProperty("Level").GetString());
            Assert.Equal("System.InvalidOperationException", second.RootElement.GetProperty("ExceptionType").GetString());
            Assert.Contains("boom", second.RootElement.GetProperty("ExceptionMessage").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void RejectsUnsafeRotationSettings()
    {
        Assert.Throws<ArgumentException>(() => new BoundedJsonLineFileLoggerProvider(" "));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BoundedJsonLineFileLoggerProvider("/tmp/ct-test.log", maxFileBytes: 1024));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BoundedJsonLineFileLoggerProvider("/tmp/ct-test.log", retainedFiles: 0));
    }
}
