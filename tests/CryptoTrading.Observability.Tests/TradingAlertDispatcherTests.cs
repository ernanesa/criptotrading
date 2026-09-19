using Xunit;

namespace CryptoTrading.Observability.Tests;

public sealed class TradingAlertDispatcherTests
{
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
