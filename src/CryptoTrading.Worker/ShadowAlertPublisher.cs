using CryptoTrading.Observability;
using CryptoTrading.Backtesting;

namespace CryptoTrading.Worker;

internal static class ShadowAlertPublisher
{
    public static async Task<int> PublishAsync(
        ShadowRunResult result,
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

        var published = 0;
        if (result.DivergenceCount > 0)
        {
            published += await PublishAsync(
                dispatcher,
                new TradingAlert(
                    "LOCAL",
                    "PAPER",
                    "shadow/drift",
                    occurredAtUtc,
                    Guid.NewGuid().ToString("N"),
                    TradingAlertSeverity.Critical,
                    "ops",
                    "halt Shadow promotion and inspect the report",
                    "docs/shadow-soak.md",
                    $"shadow-divergence:{result.DivergenceCount}"),
                cancellationToken).ConfigureAwait(false);
        }

        if (result.StaleCount > 0)
        {
            published += await PublishAsync(
                dispatcher,
                new TradingAlert(
                    "LOCAL",
                    "PAPER",
                    "shadow/stale",
                    occurredAtUtc,
                    Guid.NewGuid().ToString("N"),
                    TradingAlertSeverity.Warning,
                    "ops",
                    "inspect stale-data windows before promotion",
                    "docs/shadow-soak.md",
                    $"shadow-stale:{result.StaleCount}"),
                cancellationToken).ConfigureAwait(false);
        }

        if (result.RecoveryCount > 0)
        {
            published += await PublishAsync(
                dispatcher,
                new TradingAlert(
                    "LOCAL",
                    "PAPER",
                    "shadow/recovery",
                    occurredAtUtc,
                    Guid.NewGuid().ToString("N"),
                    TradingAlertSeverity.Warning,
                    "ops",
                    "review recovery evidence before promotion",
                    "docs/recovery-drill.md",
                    $"shadow-recovery:{result.RecoveryCount}"),
                cancellationToken).ConfigureAwait(false);
        }

        return published;
    }

    private static async Task<int> PublishAsync(
        TradingAlertDispatcher dispatcher,
        TradingAlert alert,
        CancellationToken cancellationToken)
    {
        return await dispatcher.PublishAsync(alert, cancellationToken).ConfigureAwait(false) ? 1 : 0;
    }
}
