using Xunit;
using System.Text.Json;

namespace CryptoTrading.Observability.Tests;

public sealed class TradingAlertDispatcherTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task DeliversRequiredOperationalContextAndDeduplicates()
    {
        var sink = new InMemoryTradingAlertSink();
        using var dispatcher = new TradingAlertDispatcher(sink);
        var alert = CreateAlert();

        Assert.True(await dispatcher.PublishAsync(alert).ConfigureAwait(true));
        Assert.False(await dispatcher.PublishAsync(alert with { CorrelationId = "second-correlation" }).ConfigureAwait(true));

        var delivered = Assert.Single(sink.Alerts);
        Assert.Equal("TESTNET", delivered.Environment);
        Assert.Equal("BINANCE", delivered.Exchange);
        Assert.Equal("BTCUSDT/account", delivered.Scope);
        Assert.Equal("ops", delivered.Owner);
        Assert.Equal("reconcile before new entry", delivered.Action);
        Assert.Equal("docs/runbook-local.md#reconciliation", delivered.Runbook);
    }

    [Fact]
    public async Task FailedDeliveryIsRetryableAndNotMarkedAsDelivered()
    {
        var sink = new FailingSink();
        using var dispatcher = new TradingAlertDispatcher(sink);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await dispatcher.PublishAsync(CreateAlert()).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.True(await dispatcher.PublishAsync(CreateAlert()).ConfigureAwait(true));
        Assert.Equal(2, sink.Attempts);
    }

    [Fact]
    public async Task RejectsNonUtcTimestampBeforeDelivery()
    {
        var sink = new InMemoryTradingAlertSink();
        using var dispatcher = new TradingAlertDispatcher(sink);
        var alert = CreateAlert() with { OccurredAtUtc = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.FromHours(-3)) };

        await Assert.ThrowsAsync<ArgumentException>(async () => await dispatcher.PublishAsync(alert).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Empty(sink.Alerts);
    }

    [Fact]
    public async Task JsonlSinkPersistsRedactedAlertEnvelope()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cryptotrading-alerts-{Guid.NewGuid():N}");
        try
        {
            using var sink = new JsonlTradingAlertSink(directory);
            await sink.DeliverAsync(CreateAlert());

            var file = Assert.Single(Directory.EnumerateFiles(directory, "alerts-*.jsonl"));
            var line = await File.ReadAllTextAsync(file).ConfigureAwait(true);
            var persisted = JsonSerializer.Deserialize<TradingAlert>(line, JsonOptions);
            Assert.Equal("reconciliation:BTCUSDT", persisted?.DeduplicationKey);
            Assert.DoesNotContain("secret", line, StringComparison.OrdinalIgnoreCase);
            AssertUserOnlyFileMode(file);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void AssertUserOnlyFileMode(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    private static TradingAlert CreateAlert() => new(
        "TESTNET",
        "BINANCE",
        "BTCUSDT/account",
        new DateTimeOffset(2026, 9, 19, 15, 0, 0, TimeSpan.Zero),
        "corr-123",
        TradingAlertSeverity.Critical,
        "ops",
        "reconcile before new entry",
        "docs/runbook-local.md#reconciliation",
        "reconciliation:BTCUSDT");

    private sealed class FailingSink : ITradingAlertSink
    {
        public int Attempts { get; private set; }

        public ValueTask DeliverAsync(TradingAlert alert, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Attempts == 1)
            {
                throw new InvalidOperationException("synthetic sink failure");
            }

            return ValueTask.CompletedTask;
        }
    }
}
