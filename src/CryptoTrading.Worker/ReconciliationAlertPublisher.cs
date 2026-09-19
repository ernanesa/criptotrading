using CryptoTrading.Observability;
using CryptoTrading.Reconciliation;

namespace CryptoTrading.Worker;

internal static class ReconciliationAlertPublisher
{
    public static async Task<bool> PublishAsync(
        ReconciliationRunResult result,
        TradingAlertDispatcher dispatcher,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Alert timestamp must be UTC.", nameof(occurredAtUtc));
        }

        if (result.IsHealthy)
        {
            return false;
        }

        return await dispatcher.PublishAsync(new TradingAlert(
            "TESTNET",
            "BINANCE",
            "account/reconciliation",
            occurredAtUtc,
            result.CorrelationId.ToString(),
            TradingAlertSeverity.Critical,
            "ops",
            "keep SafeMode active and investigate the redacted reconciliation report",
            "docs/readiness/RECON-20260919-local.md",
            $"reconciliation:{result.CorrelationId}"), cancellationToken).ConfigureAwait(false);
    }
}
