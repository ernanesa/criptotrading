#pragma warning disable CA1812 // Hosted service is created by the generic host through dependency injection.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Exchange.Binance;
using CryptoTrading.Persistence;
using CryptoTrading.Reconciliation;
using Microsoft.Extensions.Options;

namespace CryptoTrading.Worker;

/// <summary>
/// Consumes the modern Binance Spot user-data stream and persists every accepted
/// event before it can be used by a downstream projection. The stream is an
/// evidence source, not an authorization source; any transport or persistence
/// failure enters SafeMode and stops the host.
/// </summary>
internal sealed class AccountStreamHostedService(
    IOptions<WorkerRuntimeOptions> options,
    IBinanceUserDataStreamClient streamClient,
    IAccountStreamEventStore eventStore,
    IAccountStreamOrderProjectionStore openOrderStore,
    IExecutionAccountingStore executionAccountingStore,
    IExpectedExecutionFeeStore expectedExecutionFeeStore,
    IExecutionFeeAssetValuationProvider feeAssetValuationProvider,
    IAsyncOperationalControl operationalControl,
    AccountReconciliationService reconciliation,
    ReconciliationReportWriter reconciliationReportWriter,
    CryptoTrading.Observability.TradingAlertDispatcher alertDispatcher,
    CryptoTrading.Domain.Runtime.IRuntimeSupervisor runtimeSupervisor,
    ILogger<AccountStreamHostedService> logger,
    IExecutionLearningRewardStore? learningRewards = null) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private bool initialConnectionPending = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.AccountStreamEnabled)
        {
            return;
        }

        try
        {
            // The modern signed API creates the subscription on the WebSocket;
            // the legacy listen-key argument is intentionally empty and ignored
            // by BinanceUserDataStreamApiClient.
            await foreach (var userEvent in streamClient.ReadAsync(string.Empty, stoppingToken).ConfigureAwait(false))
            {
                var record = ToRecord(userEvent);
                using var streamActivity = CryptoTrading.Observability.TradingTelemetry.ActivitySource.StartActivity("account-stream.persist");
                using var streamScope = logger.BeginScope(new Dictionary<string, object?>
                {
                    ["event_name"] = "account-stream.persist",
                    ["event_type"] = userEvent.GetType().Name,
                    ["trace_id"] = System.Diagnostics.Activity.Current?.TraceId.ToString()
                });
                _ = await eventStore.AppendAccountStreamEventAsync(record, stoppingToken).ConfigureAwait(false);
                if (userEvent is BinanceExecutionReportEvent execution)
                {
                    await AccountStreamOrderProjection.ApplyAsync(openOrderStore, execution, stoppingToken).ConfigureAwait(false);
                    var feeEstimate = await expectedExecutionFeeStore
                        .ReadExpectedExecutionFeeAsync(execution.ClientOrderId, stoppingToken)
                        .ConfigureAwait(false);
                    var feeAssetValuation = await feeAssetValuationProvider.ResolveAsync(execution, stoppingToken).ConfigureAwait(false);
                    var accounting = AccountStreamExecutionAccounting.ToCommand(
                        execution,
                        new AccountId(options.Value.ReconciliationAccount),
                        feeEstimate,
                        options.Value.AutonomyFeeDriftToleranceBps,
                        feeAssetValuation);
                    if (accounting is not null)
                    {
                        try
                        {
                            var accountingResult = await executionAccountingStore.RecordExecutionAsync(accounting, stoppingToken).ConfigureAwait(false);
                            AccountStreamLog.Accounted(logger, accountingResult.ExecutionId, execution.ClientOrderId,
                                accountingResult.IsDuplicate, accounting.Symbol.Value, accounting.Side.ToString(),
                                accounting.Quantity.Value, accounting.Price.Value, accounting.QuoteAsset.Value,
                                accounting.Commission.Asset.Value, accounting.Commission.Amount,
                                accountingResult.RealizedPnlQuote, accountingResult.AverageCostQuote,
                                accountingResult.FeeComparison?.Status.ToString() ?? "UNAVAILABLE", accounting.OccurredAtUtc);
                            if (learningRewards is not null)
                            {
                                var reward = await learningRewards.RecordExecutionRewardAsync(accountingResult.ExecutionId, stoppingToken).ConfigureAwait(false);
                                AccountStreamLog.Learning(logger, accountingResult.ExecutionId, reward.Recorded, reward.Replayed, reward.SkippedReason);
                            }
                            if (feeEstimate is not null && accountingResult.FeeComparison is { } comparison &&
                                (comparison.IsDrift || comparison.Status == ExecutionFeeComparisonStatus.NotComparable))
                            {
                                await HandleFeeComparisonFailureAsync(execution, comparison, stoppingToken).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            using var accountingErrorScope = logger.BeginScope(new Dictionary<string, object?>
                            {
                                ["error_stack"] = ex.StackTrace,
                                ["execution_id"] = accounting.ExecutionId,
                                ["client_order_id"] = execution.ClientOrderId
                            });
                            AccountStreamLog.Failed(logger, ex.GetType().Name);
                            await EnterSafeModeAsync("ACCOUNTING_EXECUTION_FAILURE", stoppingToken).ConfigureAwait(false);
                        }
                    }
                }

                if (userEvent is BinanceUserDataStreamControlEvent control &&
                    control.EventType is "eventStreamTerminated" or "serverShutdown" or "connectionLost")
                {
                    await runtimeSupervisor.MarkAccountStreamAsync(false, $"account-stream-{control.EventType}", stoppingToken).ConfigureAwait(false);
                    await EnterSafeModeAsync($"ACCOUNT_STREAM_{control.EventType.ToUpperInvariant()}", stoppingToken).ConfigureAwait(false);
                }
                else if (userEvent is BinanceUserDataStreamControlEvent { EventType: "connectionEstablished" })
                {
                    if (initialConnectionPending)
                    {
                        initialConnectionPending = false;
                    }
                    else
                    {
                        await ReconcileAfterReconnectAsync(stoppingToken).ConfigureAwait(false);
                    }

                    await runtimeSupervisor.MarkAccountStreamAsync(true, "account-stream-connected", stoppingToken).ConfigureAwait(false);
                }
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                await runtimeSupervisor.MarkAccountStreamAsync(false, "account-stream-ended", stoppingToken).ConfigureAwait(false);
                await EnterSafeModeAsync("ACCOUNT_STREAM_ENDED", stoppingToken).ConfigureAwait(false);
                throw new InvalidOperationException("The account stream ended before host cancellation.");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown must not be recorded as an operational fault.
        }
        catch (Exception exception)
        {
            using var errorScope = logger.BeginScope(new Dictionary<string, object?> { ["error_stack"] = exception.StackTrace });
            await runtimeSupervisor.MarkAccountStreamAsync(false, "account-stream-failure", stoppingToken).ConfigureAwait(false);
            await EnterSafeModeAsync("ACCOUNT_STREAM_FAILURE", stoppingToken).ConfigureAwait(false);
            AccountStreamLog.Failed(logger, exception.GetType().Name);
            // The host logs terminal BackgroundService exceptions; do not forward an upstream payload to it.
            throw new InvalidOperationException("Account stream failed closed; inspect the correlated structured failure event.");
        }
    }

    private async Task ReconcileAfterReconnectAsync(CancellationToken cancellationToken)
    {
        var result = await reconciliation.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        var reportPath = await reconciliationReportWriter.AppendAsync(new ReconciliationRunReport(
            Guid.NewGuid().ToString("N"),
            result.ObservedAtUtc,
            result.IsHealthy,
            result.Events.Count,
            result.MismatchCount,
                result.UnknownTrades?.Count ?? result.UnknownTradeIds.Count,
            result.OpenOrderCount), cancellationToken).ConfigureAwait(false);
        _ = await ReconciliationAlertPublisher.PublishAsync(result, alertDispatcher, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        await runtimeSupervisor.MarkReconciliationAsync(
            result.IsHealthy,
            result.IsHealthy ? "reconnect-reconciliation-healthy" : "reconnect-reconciliation-mismatch",
            cancellationToken).ConfigureAwait(false);

        if (result.IsHealthy)
        {
            ReconciliationStartupLog.Healthy(logger, result.CorrelationId.ToString(), result.Events.Count, reportPath);

            if (ReconciliationOperationalControlResetAuthorizer.IsAutoRecoverable(operationalControl.Snapshot))
            {
                var resetCommand = new OperationalControlCommand(
                    "account-reconciliation",
                    $"Reconciliation auto-recovery after reconnect: balance and orders healthy (correlation {result.CorrelationId}).",
                    new IdempotencyKey($"reconciliation-auto-recovery:{result.CorrelationId}"),
                    DateTimeOffset.UtcNow);
                var authorization = new OperationalControlResetAuthorization(
                    "account-reconciliation",
                    $"reconciliation-auto-recovery:{result.CorrelationId}");
                _ = await operationalControl.ResetAsync(resetCommand, authorization, cancellationToken).ConfigureAwait(false);
                AccountStreamLog.AutoRecovered(logger, result.CorrelationId.ToString());
                await runtimeSupervisor.ObserveOperationalControlAsync(operationalControl.Snapshot, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            ReconciliationStartupLog.Blocked(logger, result.CorrelationId.ToString(), result.Events.Count, result.UnknownTrades?.Count ?? result.UnknownTradeIds.Count, reportPath);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "SafeMode is a fail-closed boundary; control adapters are external and must not mask the original stream failure.")]
    private async Task EnterSafeModeAsync(string reason, CancellationToken cancellationToken)
    {
        try
        {
            _ = await operationalControl.EnterSafeModeAsync(new OperationalControlCommand(
                "account-stream",
                reason,
                IdempotencyKey.New(),
                DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            AccountStreamLog.SafeMode(logger, reason);
        }
        catch (InvalidOperationException exception)
        {
            AccountStreamLog.ControlFailure(logger, exception.GetType().Name);
        }
        catch (ArgumentException exception)
        {
            AccountStreamLog.ControlFailure(logger, exception.GetType().Name);
        }
        catch (UnauthorizedAccessException exception)
        {
            // Preserve the fail-closed behavior even if the control adapter fails;
            // the caller still throws the original stream/storage failure.
            AccountStreamLog.ControlFailure(logger, exception.GetType().Name);
        }
    }

    private async Task HandleFeeComparisonFailureAsync(
        BinanceExecutionReportEvent execution,
        ExecutionFeeComparison comparison,
        CancellationToken cancellationToken)
    {
        _ = await ExecutionFeeDriftAlertPublisher.PublishAsync(
            execution,
            comparison,
            alertDispatcher,
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
        await EnterSafeModeAsync(comparison.Reason, cancellationToken).ConfigureAwait(false);
        await ReconcileAfterReconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static AccountStreamEventRecord ToRecord(BinanceUserDataEvent userEvent)
    {
        ArgumentNullException.ThrowIfNull(userEvent);
        var eventType = userEvent switch
        {
            BinanceExecutionReportEvent => "executionReport",
            BinanceOutboundAccountPositionEvent => "outboundAccountPosition",
            BinanceUserDataStreamControlEvent control => control.EventType,
            _ => throw new ArgumentOutOfRangeException(nameof(userEvent), userEvent.GetType(), "Unsupported account stream event.")
        };
        var payload = JsonSerializer.Serialize(userEvent, userEvent.GetType(), JsonOptions);
        var dedupeKey = BuildDedupeKey(userEvent, payload);
        return new AccountStreamEventRecord(
            DeterministicGuid(dedupeKey),
            eventType,
            dedupeKey,
            userEvent.EventTimeMilliseconds,
            userEvent.ReceivedAtUtc,
            payload,
            userEvent is BinanceExecutionReportEvent execution ? execution.Symbol : null,
            userEvent.SessionId,
            userEvent.SessionGeneration,
            userEvent.StreamSubscriptionId ?? (userEvent as BinanceUserDataStreamControlEvent)?.SubscriptionId);
    }

    private static string BuildDedupeKey(BinanceUserDataEvent userEvent, string payload)
    {
        return userEvent switch
        {
            BinanceExecutionReportEvent execution =>
                $"executionReport:{execution.Symbol}:{execution.OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{execution.TradeId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}:{execution.ExecutionType}:{execution.EventTimeMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{execution.CumulativeExecutedQuantity.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            BinanceOutboundAccountPositionEvent position =>
                $"outboundAccountPosition:{position.EventTimeMilliseconds}:{position.LastUpdateTimeMilliseconds}:{AccountStreamEventRecord.ComputeHash(payload)}",
            BinanceUserDataStreamControlEvent control =>
                $"control:{control.EventType}:{control.EventTimeMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{control.SubscriptionId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}",
            _ => throw new ArgumentOutOfRangeException(nameof(userEvent))
        };
    }

    private static Guid DeterministicGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }
}

internal static partial class AccountStreamLog
{
    [LoggerMessage(LogLevel.Information, "Execution accounted. ExecutionId={ExecutionId} ClientOrderId={ClientOrderId} Replayed={Replayed} Symbol={Symbol} Side={Side} Quantity={Quantity} Price={Price} QuoteAsset={QuoteAsset} CommissionAsset={CommissionAsset} CommissionAmount={CommissionAmount} RealizedPnlQuote={RealizedPnlQuote} AverageCostQuote={AverageCostQuote} FeeStatus={FeeStatus} OccurredAtUtc={OccurredAtUtc}")]
    public static partial void Accounted(ILogger logger, string executionId, string clientOrderId, bool replayed,
        string symbol, string side, decimal quantity, decimal price, string quoteAsset, string commissionAsset,
        decimal commissionAmount, decimal? realizedPnlQuote, decimal? averageCostQuote, string feeStatus, DateTimeOffset occurredAtUtc);

    [LoggerMessage(LogLevel.Information, "Execution learning attribution. ExecutionId={ExecutionId} Recorded={Recorded} Replayed={Replayed} SkippedReason={SkippedReason}")]
    public static partial void Learning(ILogger logger, string executionId, bool recorded, bool replayed, string? skippedReason);

    [LoggerMessage(LogLevel.Information, "Account stream reconciliation auto-recovered SafeMode to Normal. CorrelationId={CorrelationId}")]
    public static partial void AutoRecovered(ILogger logger, string correlationId);

    [LoggerMessage(LogLevel.Warning, "Account stream entered SafeMode. Reason={Reason}")]
    public static partial void SafeMode(ILogger logger, string reason);

    [LoggerMessage(LogLevel.Error, "Account stream failed closed. ExceptionType={ExceptionType}")]
    public static partial void Failed(ILogger logger, string exceptionType);

    [LoggerMessage(LogLevel.Error, "Account stream SafeMode control failed. ExceptionType={ExceptionType}")]
    public static partial void ControlFailure(ILogger logger, string exceptionType);
}
