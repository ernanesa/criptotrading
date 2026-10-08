#pragma warning disable CA2007 // Test assertions intentionally follow the task context.

using CryptoTrading.Domain.Primitives;
using CryptoTrading.Observability;
using CryptoTrading.Reconciliation;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class ReconciliationAlertPublisherTests
{
    [Fact]
    public async Task PublishesOneCriticalRedactedAlertForUnhealthyResult()
    {
        var sink = new InMemoryTradingAlertSink();
        using var dispatcher = new TradingAlertDispatcher(sink);
        var result = new ReconciliationRunResult(
            new CorrelationId(Guid.Parse("10000000-0000-0000-0000-000000000001")),
            new DateTimeOffset(2026, 9, 19, 16, 0, 0, TimeSpan.Zero),
            false,
            [],
            [42L]);

        Assert.True(await ReconciliationAlertPublisher.PublishAsync(
            result,
            dispatcher,
            new DateTimeOffset(2026, 9, 19, 16, 0, 1, TimeSpan.Zero), cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true));

        var alert = Assert.Single(sink.Alerts);
        Assert.Equal("account/reconciliation", alert.Scope);
        Assert.Equal(TradingAlertSeverity.Critical, alert.Severity);
        Assert.Equal($"reconciliation:{result.CorrelationId}", alert.DeduplicationKey);
        Assert.DoesNotContain("42", alert.Action, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthyResultDoesNotPublishAnAlert()
    {
        var sink = new InMemoryTradingAlertSink();
        using var dispatcher = new TradingAlertDispatcher(sink);
        var result = new ReconciliationRunResult(CorrelationId.New(), DateTimeOffset.UtcNow, true, [], []);

        Assert.False(await ReconciliationAlertPublisher.PublishAsync(result, dispatcher, DateTimeOffset.UtcNow, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Empty(sink.Alerts);
    }
}
