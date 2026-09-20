#pragma warning disable CA1515 // Executor is a host composition boundary and is covered by contract tests.

using System.Globalization;
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
    IOptions<WorkerRuntimeOptions> options,
    IOptions<TradingOptions> tradingOptions,
    OrderProjectionService projection,
    ILogger<TestnetOutboxMessageExecutor> logger) : IOutboxMessageExecutor
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
        var transitionTime = DateTimeOffset.UtcNow;
        await projection.TransitionAndProjectAsync(
            order,
            OrderStatus.Validated,
            new IdempotencyKey($"validated:{message.MessageId:N}"),
            transitionTime,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await projection.TransitionAndProjectAsync(
            order,
            OrderStatus.Submitted,
            new IdempotencyKey($"submitted:{message.MessageId:N}"),
            transitionTime,
            projectBeforeSubmission: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        try
        {
            var result = await client.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
            await ProjectExchangeResultAsync(order, result, projection, message.MessageId, cancellationToken).ConfigureAwait(false);
            TestnetExecutorLog.Submitted(logger, message.MessageId, command.Symbol.Value, command.ClientOrderId.Value, recovered: false);
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
            catch (BinanceApiException queryFailure) when ((int?)queryFailure.StatusCode is 400 or 404)
            {
                throw new BinanceOrderSubmissionAmbiguousException(
                    command.ClientOrderId.Value,
                    new InvalidOperationException("Ambiguous Testnet submit was not found by clientOrderId; durable retry remains subject to the outbox policy.", ambiguous));
            }
        }
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
        await projection.TransitionAndProjectAsync(
            order,
            OrderStatus.Acknowledged,
            new IdempotencyKey($"ack:{messageId:N}:{exchangeOrderId}"),
            occurredAtUtc,
            exchangeOrderId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var target = status switch
        {
            "NEW" => (OrderStatus?)null,
            "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
            "FILLED" => OrderStatus.Filled,
            "CANCELED" or "CANCELLED" => OrderStatus.Cancelled,
            "REJECTED" => OrderStatus.Rejected,
            "EXPIRED" => OrderStatus.Expired,
            _ => throw new InvalidOperationException($"Unsupported Binance order status '{result.Status}'.")
        };
        if (target is not { } finalStatus)
        {
            return;
        }

        await projection.TransitionAndProjectAsync(
            order,
            finalStatus,
            new IdempotencyKey($"status:{messageId:N}:{status}"),
            occurredAtUtc,
            exchangeOrderId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

internal static partial class TestnetExecutorLog
{
    [LoggerMessage(LogLevel.Information, "Testnet order command accepted. MessageId={MessageId} Symbol={Symbol} ClientOrderId={ClientOrderId} Recovered={Recovered}")]
    public static partial void Submitted(ILogger logger, Guid messageId, string symbol, string clientOrderId, bool recovered);
}
