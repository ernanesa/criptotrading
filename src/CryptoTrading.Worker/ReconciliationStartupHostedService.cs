using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Persistence;
using CryptoTrading.Reconciliation;
using Microsoft.Extensions.Options;

namespace CryptoTrading.Worker;

/// <summary>
/// Optional startup-only account reconciliation. It is read-only and never
/// resets SafeMode automatically; the default worker does not register it.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "The host creates this hosted service through dependency injection.")]
internal sealed class ReconciliationStartupHostedService(
    IOptions<WorkerRuntimeOptions> options,
    AccountReconciliationService reconciliation,
    ILogger<ReconciliationStartupHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.ReconciliationEnabled)
        {
            return;
        }

        var result = await reconciliation.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsHealthy)
        {
            ReconciliationStartupLog.Healthy(logger, result.CorrelationId.ToString(), result.Events.Count);
            return;
        }

        ReconciliationStartupLog.Blocked(logger, result.CorrelationId.ToString(), result.Events.Count, result.UnknownTradeIds.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class PostgresLocalBalanceReader(PostgresTradingStore store) : ILocalBalanceReader
{
    public async Task<decimal> GetBalanceAsync(AccountId account, string asset, CancellationToken cancellationToken = default) =>
        (await store.GetBalanceAsync(account, new Asset(asset), cancellationToken).ConfigureAwait(false)).Amount;
}

internal sealed class DenyOperationalControlResetAuthorizer : IOperationalControlResetAuthorizer
{
    public bool IsAuthorized(
        OperationalControlSnapshot current,
        OperationalControlCommand resetCommand,
        OperationalControlResetAuthorization authorization) => false;
}

internal static partial class ReconciliationStartupLog
{
    [LoggerMessage(LogLevel.Information, "Read-only account reconciliation healthy. CorrelationId={CorrelationId} Events={EventCount}")]
    public static partial void Healthy(ILogger logger, string correlationId, int eventCount);

    [LoggerMessage(LogLevel.Error, "Read-only account reconciliation blocked new entries. CorrelationId={CorrelationId} Events={EventCount} UnknownTrades={UnknownTradeCount}")]
    public static partial void Blocked(ILogger logger, string correlationId, int eventCount, int unknownTradeCount);
}
