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
    ReconciliationReportWriter reportWriter,
    CryptoTrading.Observability.TradingAlertDispatcher alertDispatcher,
    CryptoTrading.Domain.Runtime.IRuntimeSupervisor runtimeSupervisor,
    ILogger<ReconciliationStartupHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.ReconciliationEnabled)
        {
            return;
        }

        var result = await reconciliation.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        var reportPath = await reportWriter.AppendAsync(new ReconciliationRunReport(
            Guid.NewGuid().ToString("N"),
            result.ObservedAtUtc,
            result.IsHealthy,
            result.Events.Count,
            result.MismatchCount,
            result.UnknownTradeIds.Count,
            result.OpenOrderCount), cancellationToken).ConfigureAwait(false);
        _ = await ReconciliationAlertPublisher.PublishAsync(result, alertDispatcher, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        await runtimeSupervisor.MarkReconciliationAsync(
            result.IsHealthy,
            result.IsHealthy ? "startup-reconciliation-healthy" : "startup-reconciliation-mismatch",
            cancellationToken).ConfigureAwait(false);
        await runtimeSupervisor.MarkWarmupAsync(
            result.IsHealthy,
            result.IsHealthy ? "startup-reconciliation-warmup-complete" : "startup-reconciliation-warmup-blocked",
            cancellationToken).ConfigureAwait(false);
        if (result.IsHealthy)
        {
            ReconciliationStartupLog.Healthy(logger, result.CorrelationId.ToString(), result.Events.Count, reportPath);
            return;
        }

        ReconciliationStartupLog.Blocked(logger, result.CorrelationId.ToString(), result.Events.Count, result.UnknownTradeIds.Count, reportPath);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class PostgresLocalBalanceReader(PostgresTradingStore store) : ILocalBalanceReader
{
    public async Task<decimal> GetBalanceAsync(AccountId account, string asset, CancellationToken cancellationToken = default) =>
        (await store.GetBalanceAsync(account, new Asset(asset), cancellationToken).ConfigureAwait(false)).Amount;

    public Task<IReadOnlySet<string>> GetAssetsAsync(AccountId account, CancellationToken cancellationToken = default) =>
        store.GetAssetsAsync(account, cancellationToken);
}

internal sealed class PostgresKnownTradeReader(PostgresTradingStore store) : IKnownTradeReader
{
    public Task<IReadOnlySet<long>> GetKnownTradeIdsAsync(CancellationToken cancellationToken = default) =>
        store.GetKnownTradeIdsAsync(cancellationToken);
}

internal sealed class PostgresLocalOpenOrderReader(PostgresTradingStore store) : ILocalOpenOrderReader
{
    public async Task<IReadOnlyList<ReconciliationOpenOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken = default)
    {
        var records = await store.ReadOpenOrdersAsync(cancellationToken).ConfigureAwait(false);
        return records
            .Select(static order => new ReconciliationOpenOrder(order.Symbol, order.OrderId ?? 0, order.ClientOrderId, order.Status))
            .ToArray();
    }
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
    [LoggerMessage(LogLevel.Information, "Read-only account reconciliation healthy. CorrelationId={CorrelationId} Events={EventCount} ReportPath={ReportPath}")]
    public static partial void Healthy(ILogger logger, string correlationId, int eventCount, string reportPath);

    [LoggerMessage(LogLevel.Error, "Read-only account reconciliation blocked new entries. CorrelationId={CorrelationId} Events={EventCount} UnknownTrades={UnknownTradeCount} ReportPath={ReportPath}")]
    public static partial void Blocked(ILogger logger, string correlationId, int eventCount, int unknownTradeCount, string reportPath);
}
