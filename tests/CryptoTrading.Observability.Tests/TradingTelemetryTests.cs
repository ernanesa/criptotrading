using Xunit;

namespace CryptoTrading.Observability.Tests;

public sealed class TradingTelemetryTests
{
    [Fact]
    public void ReconciliationAndOutboxSignalsAreReflectedInSnapshot()
    {
        var before = TradingTelemetry.Snapshot();

        TradingTelemetry.RecordReconciliation(healthy: false, mismatchCount: 2);
        TradingTelemetry.RecordOutboxQueueLag(TimeSpan.FromSeconds(1));
        TradingTelemetry.RecordOutboxDispatch(succeeded: true);
        TradingTelemetry.RecordOutboxDispatch(succeeded: false);
        TradingTelemetry.RecordOutboxDispatch(succeeded: false, deadLettered: true);

        var after = TradingTelemetry.Snapshot();

        Assert.Equal(before.ReconciliationRuns + 1, after.ReconciliationRuns);
        Assert.Equal(before.ReconciliationMismatches + 2, after.ReconciliationMismatches);
        Assert.Equal(before.OutboxDispatches + 3, after.OutboxDispatches);
        Assert.Equal(before.OutboxDispatchFailures + 2, after.OutboxDispatchFailures);
        Assert.Equal(before.OutboxDeadLetters + 1, after.OutboxDeadLetters);
    }

    [Fact]
    public void NegativeOutboxLagIsIgnored()
    {
        TradingTelemetry.RecordOutboxQueueLag(TimeSpan.FromMilliseconds(-1));

        _ = TradingTelemetry.Snapshot();
    }
}
