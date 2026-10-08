#pragma warning disable CA1515 // Executor is a host composition boundary and is covered by contract tests.

using System.Globalization;
using System.Diagnostics;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Configuration;
using CryptoTrading.Exchange.Binance;
using CryptoTrading.Persistence;
using Microsoft.Extensions.Options;

namespace CryptoTrading.Worker;

/// <summary>
/// Explicit Binance Spot Testnet outbox boundary. It is registered only when
/// all Worker Testnet gates are enabled; it has no Demo/Live implementation.
/// </summary>
public sealed class TestnetOutboxMessageExecutor(
    IBinanceSpotOrderClient client,
    IBinanceSpotAccountClient accountClient,
    IOptions<WorkerRuntimeOptions> options,
    IOptions<TradingOptions> tradingOptions,
    OrderProjectionService projection,
    ILogger<TestnetOutboxMessageExecutor> logger,
    IOptions<BinanceAuthenticatedClientOptions>? authenticatedOptions = null,
    IExecutionSubmissionGuard? submissionGuard = null,
    IDurableSubmissionStore? submissionStore = null) : IOutboxMessageExecutor
{
    public string MessageType => OrderExecutionPipeline.SubmitOrderMessageType;

    public async Task ExecuteAsync(LeasedOutboxMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(message.MessageType, MessageType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported outbox message type '{message.MessageType}'.");
        }

        var runtime = options.Value;
        runtime.Validate();
        if (!string.Equals(runtime.ExecutionMode, "TESTNET", StringComparison.OrdinalIgnoreCase) || runtime.NoTrade)
        {
            throw new InvalidOperationException("Testnet executor requires TESTNET mode with WORKER_NO_TRADE=false.");
        }

        var command = PaperOutboxMessageExecutor.DeserializeCommand(message.PayloadJson);
        if (command.CorrelationId != message.CorrelationId || command.IdempotencyKey != message.IdempotencyKey)
        {
            throw new InvalidOperationException("The outbox envelope does not match the command identity.");
        }

        if (!tradingOptions.Value.Binance.ParsedAllowedSymbols.Contains(command.Symbol.Value, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Symbol '{command.Symbol}' is outside BINANCE_ALLOWED_SYMBOLS for Testnet execution.");
        }

        if (submissionGuard is null || submissionStore is null)
        {
            throw new ExecutionDispatchBlockedException("DURABLE_SUBMISSION_GUARD_REQUIRED");
        }
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["event_name"] = "execution.submit",
            ["message_id"] = message.MessageId,
            ["correlation_id"] = message.CorrelationId.Value,
            ["client_order_id"] = command.ClientOrderId.Value,
            ["symbol"] = command.Symbol.Value,
            ["policy_version"] = command.PolicyVersion,
            ["attempt"] = message.Attempts,
            ["lease_owner"] = message.LeaseOwner,
            ["trace_id"] = Activity.Current?.TraceId.ToString()
        });

        var request = new BinanceSpotOrderRequest(
            command.Symbol.Value,
            command.Side == OrderSide.Buy ? BinanceSpotOrderSide.Buy : BinanceSpotOrderSide.Sell,
            command.Type == OrderType.Limit ? BinanceSpotOrderType.Limit : BinanceSpotOrderType.Market,
            command.Quantity.Value,
            command.ClientOrderId.Value,
            command.Type == OrderType.Limit ? command.ReferencePrice.Value : null);

        var order = Order.Create(
            command.OrderId,
            command.ClientOrderId,
            command.Symbol,
            command.Side,
            command.Type,
            command.Quantity,
            command.Type == OrderType.Limit ? command.ReferencePrice : null,
            command.IdempotencyKey,
            command.CreatedAtUtc);
        // Attempts count deliveries, not venue side effects. A retry without
        // the durable fence still follows normal pre-submit validation.
        if (await submissionStore.HasSubmissionStartedAsync(message, cancellationToken).ConfigureAwait(false))
        {
            await ProjectSubmittedAsync(order, message, command, cancellationToken).ConfigureAwait(false);
            await RecoverAsync(order, message, cancellationToken).ConfigureAwait(false);
            return;
        }

        ValidateCurrentCommission(command, DateTimeOffset.UtcNow);
        await ValidateAvailableBalanceAsync(command, runtime, cancellationToken).ConfigureAwait(false);
        await submissionGuard.ValidateAsync(command, message, cancellationToken).ConfigureAwait(false);
        // WorkerExecutionRegistration configures the authenticated order HTTP client for ten seconds.
        var submitTimeout = TimeSpan.FromSeconds(10);
        var submitLeaseMargin = TimeSpan.FromSeconds(2);
        if (!await submissionStore.TryBeginSubmissionAsync(message,
                submitTimeout + submitLeaseMargin, cancellationToken).ConfigureAwait(false))
        {
            if (await submissionStore.HasSubmissionStartedAsync(message, cancellationToken).ConfigureAwait(false))
            {
                await ProjectSubmittedAsync(order, message, command, cancellationToken).ConfigureAwait(false);
                await RecoverAsync(order, message, cancellationToken).ConfigureAwait(false);
                return;
            }
            throw new ExecutionDispatchBlockedException("OUTBOX_LEASE_TOO_SHORT_OR_LOST");
        }
        try
        {
            await ProjectSubmittedAsync(order, message, command, cancellationToken).ConfigureAwait(false);
            try
            {
                await submissionGuard.ValidateFinalAsync(command, message, cancellationToken).ConfigureAwait(false);
            }
            catch (ExecutionDispatchBlockedException)
            {
                await projection.TransitionAndProjectAsync(
                    order,
                    OrderStatus.Unknown,
                    new IdempotencyKey($"unknown:{message.MessageId:N}"),
                    DateTimeOffset.UtcNow,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                throw new OrderOutcomeUnknownException(command.ClientOrderId.Value);
            }
            var result = await client.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
            try
            {
                await ProjectExchangeResultAsync(order, result, projection, message.MessageId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // Any local projection failure after a venue response requires a read-only identity lookup.
            catch (Exception)
            {
                await RecoverAsync(order, message, cancellationToken).ConfigureAwait(false);
                return;
            }
#pragma warning restore CA1031
            TestnetExecutorLog.Submitted(logger, message.MessageId, command.Symbol.Value, command.ClientOrderId.Value, recovered: false);
        }
        catch (BinanceOrderRejectedException rejected)
        {
            if (!string.Equals(rejected.ClientOrderId, command.ClientOrderId.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Binance rejection client identity does not match the submitted command.", rejected);
            }

            await projection.TransitionAndProjectAsync(
                order,
                OrderStatus.Rejected,
                new IdempotencyKey($"rejected:{message.MessageId:N}"),
                command.CreatedAtUtc,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            TestnetExecutorLog.Rejected(logger, message.MessageId, command.Symbol.Value, command.ClientOrderId.Value, rejected.StatusCode);
        }
        catch (BinancePreTradeValidationException)
        {
            await projection.TransitionAndProjectAsync(
                order,
                OrderStatus.Rejected,
                new IdempotencyKey($"rejected:{message.MessageId:N}"),
                command.CreatedAtUtc,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            TestnetExecutorLog.Rejected(logger, message.MessageId, command.Symbol.Value, command.ClientOrderId.Value, System.Net.HttpStatusCode.BadRequest);
        }
        catch (BinanceOrderSubmissionAmbiguousException ambiguous)
        {
            // A submit may have reached Binance before the response was lost.
            // Lookup is mandatory before the outbox may retry the command.
            try
            {
                var result = await client.QueryByClientOrderIdAsync(command.Symbol.Value, command.ClientOrderId.Value, cancellationToken).ConfigureAwait(false);
                await ProjectExchangeResultAsync(order, result, projection, message.MessageId, cancellationToken).ConfigureAwait(false);
                TestnetExecutorLog.Submitted(logger, message.MessageId, command.Symbol.Value, command.ClientOrderId.Value, recovered: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception queryFailure)
            {
                // The query itself may fail or return no order. Either way the
                // submit outcome is not provable. Persist UNKNOWN before
                // quarantining the outbox message; retrying SubmitAsync would
                // be an unsafe duplicate even when Binance eventually accepts
                // the original request.
                await projection.TransitionAndProjectAsync(
                    order,
                    OrderStatus.Unknown,
                    new IdempotencyKey($"unknown:{message.MessageId:N}"),
                    DateTimeOffset.UtcNow,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                throw new OrderOutcomeUnknownException(command.ClientOrderId.Value, new AggregateException(ambiguous, queryFailure));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The start marker survives shutdown; the next owner must query.
            throw;
        }
        catch (Exception exception)
        {
            // Includes a failed database projection after a confirmed venue
            // response. Never reinterpret that as permission to resubmit.
            TestnetExecutorLog.Uncertain(logger, message.MessageId, command.ClientOrderId.Value, exception.GetType().Name);
            throw new OrderOutcomeUnknownException(command.ClientOrderId.Value);
        }
    }

    private async Task RecoverAsync(Order order, LeasedOutboxMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var result = await client.QueryByClientOrderIdAsync(order.Symbol.Value, order.ClientOrderId.Value, cancellationToken).ConfigureAwait(false);
            await ProjectExchangeResultAsync(order, result, projection, message.MessageId, cancellationToken).ConfigureAwait(false);
            TestnetExecutorLog.Submitted(logger, message.MessageId, order.Symbol.Value, order.ClientOrderId.Value, recovered: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            TestnetExecutorLog.Uncertain(logger, message.MessageId, order.ClientOrderId.Value, exception.GetType().Name);
            await projection.TransitionAndProjectAsync(order, OrderStatus.Unknown,
                new IdempotencyKey($"unknown:{message.MessageId:N}"), order.CreatedAtUtc,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            throw new OrderOutcomeUnknownException(order.ClientOrderId.Value);
        }
    }

    private async Task ProjectSubmittedAsync(Order order, LeasedOutboxMessage message, SubmitOrderCommand command, CancellationToken cancellationToken)
    {
        await projection.TransitionAndProjectAsync(
            order,
            OrderStatus.Validated,
            new IdempotencyKey($"validated:{message.MessageId:N}"),
            command.CreatedAtUtc,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await projection.TransitionAndProjectAsync(
            order,
            OrderStatus.Submitted,
            new IdempotencyKey($"submitted:{message.MessageId:N}"),
            command.CreatedAtUtc,
            projectBeforeSubmission: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateAvailableBalanceAsync(
        SubmitOrderCommand command,
        WorkerRuntimeOptions runtime,
        CancellationToken cancellationToken)
    {
        var account = await accountClient.GetAccountAsync(cancellationToken).ConfigureAwait(false);
        var allowTestnetWithdraw = authenticatedOptions?.Value.AllowTestnetWithdrawCapability ?? false;
        if (!account.CanTrade || (account.CanWithdraw && !allowTestnetWithdraw))
        {
            throw new InvalidOperationException("Testnet account capabilities are unsafe for order submission.");
        }

        var (baseAsset, quoteAsset) = SplitSymbol(command.Symbol.Value);
        var requiredAsset = command.Side == OrderSide.Buy ? quoteAsset : baseAsset;
        var free = ParseFreeBalance(account, requiredAsset);
        if (command.Side == OrderSide.Sell)
        {
            if (free < command.Quantity.Value)
            {
                throw new InvalidOperationException("Insufficient free base balance for Testnet SELL.");
            }
            return;
        }

        var rate = command.EstimatedCommissionRate!.Value;
        if (!new FeeReservePolicy(runtime.AutonomyFeeReserveBps).TryCalculate(command.AuthorizedNotional, rate, out var reserve) ||
            command.AuthorizedNotional + reserve >= free)
        {
            throw new InvalidOperationException("Insufficient free quote balance including fee reserve for Testnet BUY.");
        }
    }

    private static void ValidateCurrentCommission(SubmitOrderCommand command, DateTimeOffset nowUtc)
    {
        if (nowUtc.Offset != TimeSpan.Zero || command.EstimatedCommissionRate is not { } rate || rate < 0m ||
            string.IsNullOrWhiteSpace(command.CommissionSnapshotHash) || string.IsNullOrWhiteSpace(command.CommissionSource) ||
            command.CommissionObservedAtUtc is not { } observedAtUtc || command.CommissionValidUntilUtc is not { } validUntilUtc ||
            observedAtUtc.Offset != TimeSpan.Zero || validUntilUtc.Offset != TimeSpan.Zero ||
            observedAtUtc > validUntilUtc || nowUtc > validUntilUtc)
        {
            throw new InvalidOperationException("Testnet submission requires a complete, currently valid commission snapshot.");
        }
    }

    private static decimal ParseFreeBalance(BinanceAccountSnapshot account, string asset)
    {
        var value = account.Balances.FirstOrDefault(balance => string.Equals(balance.Asset, asset, StringComparison.OrdinalIgnoreCase))?.Free;
        return !string.IsNullOrWhiteSpace(value) && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0m
            ? parsed
            : 0m;
    }

    private static (string Base, string Quote) SplitSymbol(string symbol)
    {
        const string quote = "USDT";
        return symbol.EndsWith(quote, StringComparison.Ordinal) && symbol.Length > quote.Length
            ? (symbol[..^quote.Length], quote)
            : throw new InvalidOperationException($"Unsupported Testnet quote asset for pre-submit balance validation: '{symbol}'.");
    }

    private static async Task ProjectExchangeResultAsync(
        Order order,
        BinanceSpotOrderResult result,
        OrderProjectionService projection,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!string.Equals(result.Symbol, order.Symbol.Value, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(result.ClientOrderId, order.ClientOrderId.Value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Binance order result identity does not match the submitted command.");
        }

        if (result.OrderId < 0 || result.TransactTimeMilliseconds < 0)
        {
            throw new InvalidOperationException("Binance order result contains an invalid exchange identity or timestamp.");
        }

        var occurredAtUtc = result.TransactTimeMilliseconds == 0
            ? DateTimeOffset.UtcNow
            : DateTimeOffset.FromUnixTimeMilliseconds(result.TransactTimeMilliseconds);
        var exchangeOrderId = result.OrderId.ToString(CultureInfo.InvariantCulture);
        var status = result.Status.Trim().ToUpperInvariant();
        var existingTransitions = await projection.ReadLifecycleAsync(order.ClientOrderId.Value, cancellationToken).ConfigureAwait(false);
        var ackKey = new IdempotencyKey($"ack:{messageId:N}:{exchangeOrderId}");
        var ackTime = existingTransitions.FirstOrDefault(item => item.Transition.IdempotencyKey == ackKey)?.Transition.OccurredAtUtc ?? occurredAtUtc;
        await projection.TransitionAndProjectAsync(
            order,
            OrderStatus.Acknowledged,
            ackKey,
            ackTime,
            exchangeOrderId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var target = status switch
        {
            "NEW" => (OrderStatus?)null,
            "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
            "FILLED" => OrderStatus.Filled,
            "PENDING_CANCEL" => OrderStatus.CancelPending,
            "CANCELED" or "CANCELLED" => OrderStatus.Cancelled,
            "REJECTED" => OrderStatus.Rejected,
            "EXPIRED" or "EXPIRED_IN_MATCH" => OrderStatus.Expired,
            // Preserve a venue status we do not yet understand as an open
            // Unknown order. Completing the outbox is safe only because this
            // projection is durable and reconciliation remains authoritative.
            _ => OrderStatus.Unknown
        };
        if (target is not { } finalStatus)
        {
            return;
        }

        var statusKey = new IdempotencyKey($"status:{messageId:N}:{status}");
        var statusTime = existingTransitions.FirstOrDefault(item => item.Transition.IdempotencyKey == statusKey)?.Transition.OccurredAtUtc ?? occurredAtUtc;
        await projection.TransitionAndProjectAsync(
            order,
            finalStatus,
            statusKey,
            statusTime,
            exchangeOrderId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

internal static partial class TestnetExecutorLog
{
    [LoggerMessage(LogLevel.Error, "Submission requires reconciliation. MessageId={MessageId} ClientOrderId={ClientOrderId} ErrorType={ErrorType}")]
    public static partial void Uncertain(ILogger logger, Guid messageId, string clientOrderId, string errorType);
    [LoggerMessage(LogLevel.Information, "Testnet order command accepted. MessageId={MessageId} Symbol={Symbol} ClientOrderId={ClientOrderId} Recovered={Recovered}")]
    public static partial void Submitted(ILogger logger, Guid messageId, string symbol, string clientOrderId, bool recovered);

    [LoggerMessage(LogLevel.Warning, "Testnet order command rejected by Binance. MessageId={MessageId} Symbol={Symbol} ClientOrderId={ClientOrderId} StatusCode={StatusCode}")]
    public static partial void Rejected(ILogger logger, Guid messageId, string symbol, string clientOrderId, System.Net.HttpStatusCode? statusCode);
}
