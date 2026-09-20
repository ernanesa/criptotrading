#pragma warning disable CA1515 // Executor is a host composition boundary and is covered by contract tests.

using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
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

        try
        {
            _ = await client.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
            TestnetExecutorLog.Submitted(logger, message.MessageId, command.Symbol.Value, command.ClientOrderId.Value, recovered: false);
        }
        catch (BinanceOrderSubmissionAmbiguousException ambiguous)
        {
            // A submit may have reached Binance before the response was lost.
            // Lookup is mandatory before the outbox may retry the command.
            try
            {
                _ = await client.QueryByClientOrderIdAsync(command.Symbol.Value, command.ClientOrderId.Value, cancellationToken).ConfigureAwait(false);
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
}

internal static partial class TestnetExecutorLog
{
    [LoggerMessage(LogLevel.Information, "Testnet order command accepted. MessageId={MessageId} Symbol={Symbol} ClientOrderId={ClientOrderId} Recovered={Recovered}")]
    public static partial void Submitted(ILogger logger, Guid messageId, string symbol, string clientOrderId, bool recovered);
}
