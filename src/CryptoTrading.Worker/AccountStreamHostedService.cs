#pragma warning disable CA1812 // Hosted service is created by the generic host through dependency injection.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    IOperationalControl operationalControl,
    AccountReconciliationService reconciliation,
    ReconciliationReportWriter reconciliationReportWriter,
    CryptoTrading.Observability.TradingAlertDispatcher alertDispatcher,
    ILogger<AccountStreamHostedService> logger) : BackgroundService
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
                _ = await eventStore.AppendAccountStreamEventAsync(record, stoppingToken).ConfigureAwait(false);

                if (userEvent is BinanceUserDataStreamControlEvent control &&
                    control.EventType is "eventStreamTerminated" or "serverShutdown")
                {
                    EnterSafeMode($"ACCOUNT_STREAM_{control.EventType.ToUpperInvariant()}");
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
                }
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                EnterSafeMode("ACCOUNT_STREAM_ENDED");
                throw new InvalidOperationException("The account stream ended before host cancellation.");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown must not be recorded as an operational fault.
        }
        catch (Exception exception)
        {
            EnterSafeMode("ACCOUNT_STREAM_FAILURE");
            AccountStreamLog.Failed(logger, exception.GetType().Name);
            throw;
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
            result.UnknownTradeIds.Count,
            result.OpenOrderCount), cancellationToken).ConfigureAwait(false);
        _ = await ReconciliationAlertPublisher.PublishAsync(result, alertDispatcher, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        if (result.IsHealthy)
        {
            ReconciliationStartupLog.Healthy(logger, result.CorrelationId.ToString(), result.Events.Count, reportPath);
        }
        else
        {
            ReconciliationStartupLog.Blocked(logger, result.CorrelationId.ToString(), result.Events.Count, result.UnknownTradeIds.Count, reportPath);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "SafeMode is a fail-closed boundary; control adapters are external and must not mask the original stream failure.")]
    private void EnterSafeMode(string reason)
    {
        try
        {
            _ = operationalControl.EnterSafeMode(new OperationalControlCommand(
                "account-stream",
                reason,
                IdempotencyKey.New(),
                DateTimeOffset.UtcNow));
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
    [LoggerMessage(LogLevel.Warning, "Account stream entered SafeMode. Reason={Reason}")]
    public static partial void SafeMode(ILogger logger, string reason);

    [LoggerMessage(LogLevel.Error, "Account stream failed closed. ExceptionType={ExceptionType}")]
    public static partial void Failed(ILogger logger, string exceptionType);

    [LoggerMessage(LogLevel.Error, "Account stream SafeMode control failed. ExceptionType={ExceptionType}")]
    public static partial void ControlFailure(ILogger logger, string exceptionType);
}
