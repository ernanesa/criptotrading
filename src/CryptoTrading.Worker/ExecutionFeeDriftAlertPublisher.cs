using CryptoTrading.Domain.Accounting;
using CryptoTrading.Exchange.Binance;
using CryptoTrading.Observability;

namespace CryptoTrading.Worker;

internal static class ExecutionFeeDriftAlertPublisher
{
    public static Task<bool> PublishAsync(
        BinanceExecutionReportEvent execution,
        ExecutionFeeComparison comparison,
        TradingAlertDispatcher dispatcher,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Alert timestamp must be UTC.", nameof(occurredAtUtc));
        }

        return dispatcher.PublishAsync(new TradingAlert(
            "TESTNET",
            "BINANCE",
            $"execution/fee/{execution.Symbol}",
            occurredAtUtc,
            execution.ClientOrderId,
            TradingAlertSeverity.Critical,
            "ops",
            "keep SafeMode active; reconcile the fill commission before any reset",
            "docs/runbook-local.md#commission-drift",
            $"execution-fee:{execution.OrderId}:{execution.TradeId}:{comparison.Reason}"), cancellationToken).AsTask();
    }
}
