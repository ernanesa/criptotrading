#pragma warning disable CA1515 // Executor is a host composition boundary and is covered by contract tests.
#pragma warning disable CA1031 // Any transport failure is resolved through an authoritative query before it can be classified.

using System.Text.Json;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Configuration;
using CryptoTrading.Exchange.Binance;
using CryptoTrading.Persistence;
using Microsoft.Extensions.Options;

namespace CryptoTrading.Worker;

/// <summary>
/// Testnet-only cancellation boundary. It persists <see cref="OrderStatus.CancelPending"/>
/// before the venue call and resolves every uncertain result through the authoritative
/// client-order lookup. It never re-submits a cancel blindly.
/// </summary>
public sealed class TestnetCancelOutboxMessageExecutor(
    IBinanceSpotOrderClient client,
    IOptions<WorkerRuntimeOptions> options,
    IOptions<TradingOptions> tradingOptions,
    ILocalOpenOrderStore openOrders,
    OrderProjectionService projection,
    ILogger<TestnetCancelOutboxMessageExecutor> logger) : IOutboxMessageExecutor
{
    public string MessageType => CancelLifecyclePlanner.CancelOrderMessageType;

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
            throw new InvalidOperationException("Testnet cancel executor requires TESTNET mode with WORKER_NO_TRADE=false.");
        }

        var command = DeserializeCommand(message.PayloadJson);
        if (command.CorrelationId != message.CorrelationId || command.IdempotencyKey != message.IdempotencyKey)
        {
            throw new InvalidOperationException("The cancel outbox envelope does not match the command identity.");
        }

        if (!tradingOptions.Value.Binance.ParsedAllowedSymbols.Contains(command.Symbol.Value, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Symbol '{command.Symbol}' is outside BINANCE_ALLOWED_SYMBOLS for Testnet execution.");
        }

        var existing = (await openOrders.ReadOpenOrdersAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(order => string.Equals(order.ClientOrderId, command.ClientOrderId.Value, StringComparison.Ordinal));
        if (existing is null || existing.Status is not ("NEW" or "PARTIALLY_FILLED"))
        {
            throw new InvalidOperationException("A cancellation requires a durable acknowledged or partially-filled open-order projection.");
        }

        var order = Rehydrate(command, existing);
        await projection.TransitionAndProjectAsync(
            order,
            OrderStatus.CancelPending,
            new IdempotencyKey($"cancel-pending:{message.MessageId:N}"),
            DateTimeOffset.UtcNow,
            existing.OrderId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        BinanceSpotOrderResult observed;
        try
        {
            // A retry might run after a venue-side cancellation but before the
            // previous worker persisted its response. Query first so retrying
            // the outbox never emits a second blind DELETE.
            observed = await client.QueryByClientOrderIdAsync(command.Symbol.Value, command.ClientOrderId.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception queryFailure)
        {
            await ProjectUnknownAsync(order, existing, message.MessageId, cancellationToken).ConfigureAwait(false);
            throw new OrderOutcomeUnknownException(command.ClientOrderId.Value, queryFailure);
        }

        var observedStatus = ToVenueStatus(observed.Status);
        if (observedStatus is CancelVenueStatus.Filled or CancelVenueStatus.Canceled or CancelVenueStatus.Expired or CancelVenueStatus.Rejected)
        {
            await ProjectResolutionAsync(order, command, observed, message.MessageId, cancellationToken).ConfigureAwait(false);
            TestnetCancelExecutorLog.Resolved(logger, message.MessageId, command.Symbol.Value, command.ClientOrderId.Value, recovered: true);
            return;
        }

        if (observedStatus is CancelVenueStatus.PendingCancel or CancelVenueStatus.Unknown)
        {
            if (observedStatus == CancelVenueStatus.Unknown)
            {
                await ProjectUnknownAsync(order, existing, message.MessageId, cancellationToken).ConfigureAwait(false);
                throw new OrderOutcomeUnknownException(command.ClientOrderId.Value);
            }

            return;
        }

        BinanceSpotOrderResult result;
        try
        {
            result = await client.CancelAsync(command.Symbol.Value, command.ClientOrderId.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception cancelFailure)
        {
            try
            {
                result = await client.QueryByClientOrderIdAsync(command.Symbol.Value, command.ClientOrderId.Value, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception queryFailure)
            {
                await ProjectUnknownAsync(order, existing, message.MessageId, cancellationToken).ConfigureAwait(false);
                throw new OrderOutcomeUnknownException(command.ClientOrderId.Value, new AggregateException(cancelFailure, queryFailure));
            }
        }

        await ProjectResolutionAsync(order, command, result, message.MessageId, cancellationToken).ConfigureAwait(false);
        TestnetCancelExecutorLog.Resolved(logger, message.MessageId, command.Symbol.Value, command.ClientOrderId.Value, recovered: false);
    }

    internal static CancelOrderCommand DeserializeCommand(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        return new CancelOrderCommand(
            new OrderId(ReadGuid(root, "orderId")), new ClientOrderId(ReadString(root, "clientOrderId")),
            new Symbol(ReadString(root, "symbol")), ReadEnum<OrderSide>(root, "side"), ReadEnum<OrderType>(root, "type"),
            new Quantity(ReadDecimal(root, "originalQuantity")), new Quantity(ReadDecimal(root, "filledQuantity")),
            TryReadDecimal(root, "limitPrice") is { } limitPrice ? new Price(limitPrice) : null,
            new CorrelationId(ReadGuid(root, "correlationId")), new IdempotencyKey(ReadString(root, "idempotencyKey")),
            ReadInt32(root, "attempt"), ReadDateTimeOffset(root, "requestedAtUtc"), ReadDateTimeOffset(root, "expiresAtUtc"),
            TryReadString(root, "exchangeOrderId"));
    }

    private async Task ProjectResolutionAsync(Order order, CancelOrderCommand command, BinanceSpotOrderResult result, Guid messageId, CancellationToken cancellationToken)
    {
        if (!string.Equals(result.Symbol, command.Symbol.Value, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(result.ClientOrderId, command.ClientOrderId.Value, StringComparison.Ordinal) ||
            result.OrderId < 0 || result.ExecutedQuantity < 0m || result.ExecutedQuantity > command.OriginalQuantity.Value)
        {
            throw new InvalidOperationException("Binance cancellation result does not match the durable cancel command.");
        }

        var status = ToVenueStatus(result.Status);
        var resolution = CancelLifecyclePlanner.Resolve(command, status, new Quantity(result.ExecutedQuantity));
        var occurredAtUtc = result.TransactTimeMilliseconds > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(result.TransactTimeMilliseconds)
            : DateTimeOffset.UtcNow;
        await projection.TransitionAndProjectAsync(
            order,
            resolution.ProjectedStatus,
            new IdempotencyKey($"cancel-resolution:{messageId:N}:{status}"),
            occurredAtUtc,
            result.OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private Task<OrderTransition> ProjectUnknownAsync(Order order, LocalOpenOrderRecord existing, Guid messageId, CancellationToken cancellationToken) =>
        projection.TransitionAndProjectAsync(order, OrderStatus.Unknown, new IdempotencyKey($"cancel-unknown:{messageId:N}"),
            DateTimeOffset.UtcNow, existing.OrderId?.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken: cancellationToken);

    private static CancelVenueStatus ToVenueStatus(string status) => status.Trim().ToUpperInvariant() switch
    {
        "NEW" => CancelVenueStatus.New,
        "PARTIALLY_FILLED" => CancelVenueStatus.PartiallyFilled,
        "PENDING_CANCEL" => CancelVenueStatus.PendingCancel,
        "FILLED" => CancelVenueStatus.Filled,
        "CANCELED" or "CANCELLED" => CancelVenueStatus.Canceled,
        "EXPIRED" or "EXPIRED_IN_MATCH" => CancelVenueStatus.Expired,
        "REJECTED" => CancelVenueStatus.Rejected,
        _ => CancelVenueStatus.Unknown
    };

    private static Order Rehydrate(CancelOrderCommand command, LocalOpenOrderRecord existing)
    {
        var order = Order.Create(command.OrderId, command.ClientOrderId, command.Symbol, command.Side, command.Type,
            command.OriginalQuantity, command.LimitPrice, command.IdempotencyKey, command.RequestedAtUtc);
        var occurredAtUtc = existing.UpdatedAtUtc;
        order.TransitionTo(OrderStatus.Validated, new IdempotencyKey($"rehydrate-validated:{command.ClientOrderId.Value}"), occurredAtUtc);
        order.TransitionTo(OrderStatus.Submitted, new IdempotencyKey($"rehydrate-submitted:{command.ClientOrderId.Value}"), occurredAtUtc);
        order.TransitionTo(OrderStatus.Acknowledged, new IdempotencyKey($"rehydrate-ack:{command.ClientOrderId.Value}"), occurredAtUtc,
            existing.OrderId?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (string.Equals(existing.Status, "PARTIALLY_FILLED", StringComparison.Ordinal))
        {
            order.TransitionTo(OrderStatus.PartiallyFilled, new IdempotencyKey($"rehydrate-partial:{command.ClientOrderId.Value}"), occurredAtUtc,
                existing.OrderId?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return order;
    }

    private static JsonElement ReadValue(JsonElement root, string name)
    {
        var property = root.EnumerateObject().FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (property.Value.ValueKind == JsonValueKind.Undefined)
        {
            throw new JsonException($"Property '{name}' is missing.");
        }

        if (property.Value.ValueKind != JsonValueKind.Object)
        {
            return property.Value;
        }

        var nested = property.Value.EnumerateObject()
            .FirstOrDefault(item => string.Equals(item.Name, "value", StringComparison.OrdinalIgnoreCase));
        return nested.Value.ValueKind == JsonValueKind.Undefined ? property.Value : nested.Value;
    }
    private static string ReadString(JsonElement root, string name) => ReadValue(root, name).GetString() ?? throw new JsonException($"Property '{name}' must be a string.");
    private static string? TryReadString(JsonElement root, string name) => TryReadValue(root, name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
    private static Guid ReadGuid(JsonElement root, string name) => Guid.Parse(ReadString(root, name));
    private static decimal ReadDecimal(JsonElement root, string name) => ReadValue(root, name).GetDecimal();
    private static decimal? TryReadDecimal(JsonElement root, string name) => TryReadValue(root, name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetDecimal() : null;
    private static int ReadInt32(JsonElement root, string name) => ReadValue(root, name).GetInt32();
    private static DateTimeOffset ReadDateTimeOffset(JsonElement root, string name) => ReadValue(root, name).GetDateTimeOffset();
    private static T ReadEnum<T>(JsonElement root, string name) where T : struct, Enum
    {
        var value = ReadValue(root, name);
        var parsed = value.ValueKind switch
        {
            JsonValueKind.String when Enum.TryParse<T>(value.GetString(), true, out var textValue) => textValue,
            JsonValueKind.Number when value.TryGetInt32(out var numericValue) => (T)Enum.ToObject(typeof(T), numericValue),
            _ => throw new JsonException($"Property '{name}' must contain a valid enum value.")
        };
        return Enum.IsDefined(parsed) ? parsed : throw new JsonException($"Property '{name}' contains an invalid enum value.");
    }
    private static bool TryReadValue(JsonElement root, string name, out JsonElement value)
    {
        var property = root.EnumerateObject().FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        value = property.Value;
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var nested = value.EnumerateObject().FirstOrDefault(item => string.Equals(item.Name, "value", StringComparison.OrdinalIgnoreCase));
            if (nested.Value.ValueKind != JsonValueKind.Undefined)
            {
                value = nested.Value;
            }
        }

        return true;
    }
}

internal static partial class TestnetCancelExecutorLog
{
    [LoggerMessage(LogLevel.Information, "Testnet cancel command resolved. MessageId={MessageId} Symbol={Symbol} ClientOrderId={ClientOrderId} Recovered={Recovered}")]
    public static partial void Resolved(ILogger logger, Guid messageId, string symbol, string clientOrderId, bool recovered);
}
