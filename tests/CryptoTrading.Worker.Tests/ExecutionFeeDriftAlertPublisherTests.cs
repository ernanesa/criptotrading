using CryptoTrading.Domain.Accounting;
using CryptoTrading.Exchange.Binance;
using CryptoTrading.Observability;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class ExecutionFeeDriftAlertPublisherTests
{
    [Fact]
    public async Task PublishesRedactedCriticalAlertForCommissionDrift()
    {
        var sink = new InMemoryTradingAlertSink();
        using var dispatcher = new TradingAlertDispatcher(sink);
        var execution = new BinanceExecutionReportEvent(
            DateTimeOffset.UtcNow, 1_700_000_000_000, "BTCUSDT", 42, 7, "client-order-42",
            "BUY", "LIMIT", "TRADE", "FILLED", .01m, .01m, 100m, .02m, "USDT");
        var comparison = new ExecutionFeeComparison(
            ExecutionFeeComparisonStatus.Drift, .01m, .02m, 10_000m, "EXECUTION_FEE_DRIFT",
            "fill-quote-asset", DateTimeOffset.UtcNow);

        Assert.True(await ExecutionFeeDriftAlertPublisher.PublishAsync(
            execution, comparison, dispatcher, DateTimeOffset.UtcNow, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true));

        var alert = Assert.Single(sink.Alerts);
        Assert.Equal(TradingAlertSeverity.Critical, alert.Severity);
        Assert.Equal("execution/fee/BTCUSDT", alert.Scope);
        Assert.Equal("client-order-42", alert.CorrelationId);
        Assert.DoesNotContain("0.02", alert.Action, StringComparison.Ordinal);
    }
}
