#pragma warning disable CA1515 // Worker contracts are intentionally public for host-level contract tests and composition.
#pragma warning disable CA1031 // Dispatch boundary converts all non-cancellation failures into durable retries.

using System.Text.Json;
using System.Diagnostics;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Exchange.Paper;
using CryptoTrading.Observability;
using CryptoTrading.Persistence;
using Microsoft.Extensions.Options;

namespace CryptoTrading.Worker;

public interface IOutboxMessageExecutor
{
    string MessageType { get; }

    Task ExecuteAsync(LeasedOutboxMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Safe PAPER executor. NO_TRADE acknowledges only after intentional suppression.</summary>
public sealed class PaperOutboxMessageExecutor(
    IPaperExchange exchange,
    IOptions<WorkerRuntimeOptions> options,
    ILogger<PaperOutboxMessageExecutor> logger) : IOutboxMessageExecutor
{
    public string MessageType => OrderExecutionPipeline.SubmitOrderMessageType;

    public Task ExecuteAsync(LeasedOutboxMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(message.MessageType, MessageType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported outbox message type '{message.MessageType}'.");
        }

        var runtime = options.Value;
        if (runtime.NoTrade)
        {
            // Deliberately do not deserialize or execute a command in the default safe mode.
            return Task.CompletedTask;
        }

        var command = DeserializeCommand(message.PayloadJson);
        if (command.CorrelationId != message.CorrelationId
            || command.IdempotencyKey != message.IdempotencyKey)
        {
            throw new InvalidOperationException("The outbox envelope does not match the command identity.");
        }

        var request = new PaperOrderRequest(
            command.ClientOrderId,
            command.Symbol,
            command.Side,
            command.Type,
            command.Quantity);
        PaperSubmitResult result;
        try
        {
            result = exchange.Submit(request, command.IdempotencyKey);
        }
        catch (TimeoutException submitTimeout)
        {
            // A timeout is ambiguous: the exchange may have accepted the order
            // before the response was lost. Never submit again blindly. Resolve
            // the outcome by the durable client order identity first; if it is
            // still absent, preserve the timeout so the outbox retry can safely
            // repeat the same idempotent command.
            try
            {
                var recovered = exchange.GetOrder(command.ClientOrderId);
                PaperExecutorLog.RecoveredAfterAmbiguousTimeout(logger, message.MessageId, command.ClientOrderId.Value);
                result = new PaperSubmitResult(recovered, AlreadyProcessed: true);
            }
            catch (KeyNotFoundException)
            {
                throw new TimeoutException(
                    $"The paper exchange did not confirm client order '{command.ClientOrderId}' after an ambiguous submit timeout.",
                    submitTimeout);
            }
        }

        PaperExecutorLog.Submitted(logger, message.MessageId, command.Symbol.Value, result.AlreadyProcessed);
        return Task.CompletedTask;
    }

    internal static SubmitOrderCommand DeserializeCommand(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var command = new SubmitOrderCommand(
            new OrderId(ReadGuid(root, "orderId")),
            new ClientOrderId(ReadString(root, "clientOrderId")),
            new Symbol(ReadString(root, "symbol")),
            ReadEnum<OrderSide>(root, "side"),
            ReadEnum<OrderType>(root, "type"),
            new Quantity(ReadDecimal(root, "quantity")),
            new Price(ReadDecimal(root, "referencePrice")),
            ReadDecimal(root, "authorizedNotional"),
            new CorrelationId(ReadGuid(root, "correlationId")),
            new IdempotencyKey(ReadString(root, "idempotencyKey")),
            ReadString(root, "policyVersion"),
            ReadValue(root, "createdAtUtc").GetDateTimeOffset());

        if (command.Quantity.Value <= 0m)
        {
            throw new JsonException("Property 'quantity' must be greater than zero.");
        }

        if (command.AuthorizedNotional <= 0m)
        {
            throw new JsonException("Property 'authorizedNotional' must be greater than zero.");
        }

        if (command.Quantity.Value * command.ReferencePrice.Value != command.AuthorizedNotional)
        {
            throw new JsonException("quantity multiplied by referencePrice must equal authorizedNotional.");
        }

        if (command.CreatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new JsonException("Property 'createdAtUtc' must be a UTC timestamp.");
        }

        return command;
    }

    private static string ReadString(JsonElement root, string property) => ReadValue(root, property).GetString()
        ?? throw new JsonException($"Property '{property}' must be a string.");

    private static Guid ReadGuid(JsonElement root, string property) => Guid.Parse(ReadString(root, property));

    private static decimal ReadDecimal(JsonElement root, string property) => ReadValue(root, property).GetDecimal();

    private static T ReadEnum<T>(JsonElement root, string property) where T : struct, Enum
    {
        var value = ReadValue(root, property);
        T parsed;
        if (value.ValueKind == JsonValueKind.String)
        {
            if (!Enum.TryParse(value.GetString(), ignoreCase: true, out parsed))
            {
                throw new JsonException($"Property '{property}' contains an unknown enum value.");
            }
        }
        else if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numericValue))
        {
            parsed = (T)Enum.ToObject(typeof(T), numericValue);
        }
        else
        {
            throw new JsonException($"Property '{property}' must be a string or integer enum value.");
        }

        if (!Enum.IsDefined(parsed))
        {
            throw new JsonException($"Property '{property}' contains an undefined enum value.");
        }

        return parsed;
    }

    private static JsonElement ReadValue(JsonElement root, string property)
    {
        var value = root.EnumerateObject()
            .FirstOrDefault(candidate => string.Equals(candidate.Name, property, StringComparison.OrdinalIgnoreCase)).Value;
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw new JsonException($"Property '{property}' is missing.");
        }

        var nested = value.ValueKind == JsonValueKind.Object
            ? value.EnumerateObject()
                .FirstOrDefault(candidate => string.Equals(candidate.Name, "value", StringComparison.OrdinalIgnoreCase)).Value
            : default;
        return value.ValueKind == JsonValueKind.Object && nested.ValueKind != JsonValueKind.Undefined
            ? nested
            : value;
    }
}

/// <summary>Leases and dispatches durable commands with bounded retries.</summary>
public sealed class OutboxDispatcher(
    IEnumerable<IOutboxStore> stores,
    IEnumerable<IOutboxMessageExecutor> executors,
    IOptions<WorkerRuntimeOptions> options,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private readonly string leaseOwner = $"worker-{Environment.MachineName}-{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runtime = options.Value;
        runtime.Validate();
        var storeList = stores.ToArray();
        var executorMap = executors.ToDictionary(static executor => executor.MessageType, StringComparer.Ordinal);

        if (storeList.Length == 0)
        {
            DispatcherLog.Disabled(logger, "persistence-disabled");
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            return;
        }

        DispatcherLog.Started(logger, leaseOwner, runtime.NoTrade, runtime.ExecutionMode);
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var store in storeList)
            {
                var messages = await store.ClaimOutboxAsync(
                    leaseOwner,
                    DateTimeOffset.UtcNow,
                    TimeSpan.FromSeconds(runtime.LeaseSeconds),
                    runtime.BatchSize,
                    stoppingToken).ConfigureAwait(false);

                foreach (var message in messages)
                {
                    TradingTelemetry.RecordOutboxQueueLag(DateTimeOffset.UtcNow - message.AvailableAt);
                    await DispatchOneAsync(store, message, executorMap, stoppingToken).ConfigureAwait(false);
                }
            }

            await Task.Delay(runtime.PollIntervalMilliseconds, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task DispatchOneAsync(
        IOutboxStore store,
        LeasedOutboxMessage message,
        Dictionary<string, IOutboxMessageExecutor> executors,
        CancellationToken cancellationToken)
    {
        using var activity = TradingTelemetry.ActivitySource.StartActivity(
            "cryptotrading.outbox.dispatch",
            ActivityKind.Internal);
        activity?.SetTag("crypto.operation", "outbox_dispatch");
        try
        {
            if (!executors.TryGetValue(message.MessageType, out var executor))
            {
                throw new InvalidOperationException($"No executor registered for '{message.MessageType}'.");
            }

            await executor.ExecuteAsync(message, cancellationToken).ConfigureAwait(false);
            if (!await store.MarkOutboxProcessedAsync(message.MessageId, leaseOwner, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false))
            {
                DispatcherLog.LeaseLost(logger, message.MessageId);
            }
            else
            {
                activity?.SetStatus(ActivityStatusCode.Ok);
                TradingTelemetry.RecordOutboxDispatch(succeeded: true);
                DispatcherLog.Processed(logger, message.MessageId, message.MessageType);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag("error.type", exception.GetType().Name);
            var delaySeconds = Math.Min(300d, Math.Pow(2d, Math.Min(message.Attempts - 1, 8)));
            if (message.Attempts >= options.Value.MaxOutboxAttempts)
            {
                _ = await store.DeadLetterOutboxAsync(
                    message.MessageId,
                    leaseOwner,
                    exception.GetType().Name,
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);
                TradingTelemetry.RecordOutboxDispatch(succeeded: false, deadLettered: true);
                DispatcherLog.DeadLettered(logger, message.MessageId, message.MessageType, exception.GetType().Name);
            }
            else
            {
                var retryAt = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);
                _ = await store.FailOutboxAsync(message.MessageId, leaseOwner, exception.GetType().Name, retryAt, cancellationToken).ConfigureAwait(false);
                TradingTelemetry.RecordOutboxDispatch(succeeded: false);
                DispatcherLog.Failed(logger, message.MessageId, message.MessageType, exception.GetType().Name, delaySeconds);
            }
        }
    }
}

internal static partial class PaperExecutorLog
{
    [LoggerMessage(LogLevel.Information, "Paper outbox command submitted. MessageId={MessageId} Symbol={Symbol} AlreadyProcessed={AlreadyProcessed}")]
    public static partial void Submitted(ILogger logger, Guid messageId, string symbol, bool alreadyProcessed);

    [LoggerMessage(LogLevel.Warning, "Paper outbox command recovered after ambiguous submit timeout. MessageId={MessageId} ClientOrderId={ClientOrderId}")]
    public static partial void RecoveredAfterAmbiguousTimeout(ILogger logger, Guid messageId, string clientOrderId);
}

internal static partial class DispatcherLog
{
    [LoggerMessage(LogLevel.Information, "Outbox dispatcher started. LeaseOwner={LeaseOwner} ExecutionMode={ExecutionMode} NoTrade={NoTrade}")]
    public static partial void Started(ILogger logger, string leaseOwner, bool noTrade, string executionMode);

    [LoggerMessage(LogLevel.Information, "Outbox dispatcher disabled: {Reason}")]
    public static partial void Disabled(ILogger logger, string reason);

    [LoggerMessage(LogLevel.Information, "Outbox message processed. MessageId={MessageId} MessageType={MessageType}")]
    public static partial void Processed(ILogger logger, Guid messageId, string messageType);

    [LoggerMessage(LogLevel.Warning, "Outbox lease was lost before acknowledgement. MessageId={MessageId}")]
    public static partial void LeaseLost(ILogger logger, Guid messageId);

    [LoggerMessage(LogLevel.Error, "Outbox dispatch failed. MessageId={MessageId} MessageType={MessageType} ErrorType={ErrorType} RetryAfterSeconds={RetryAfterSeconds}")]
    public static partial void Failed(ILogger logger, Guid messageId, string messageType, string errorType, double retryAfterSeconds);

    [LoggerMessage(LogLevel.Error, "Outbox message moved to dead-letter after retry limit. MessageId={MessageId} MessageType={MessageType} ErrorType={ErrorType}")]
    public static partial void DeadLettered(ILogger logger, Guid messageId, string messageType, string errorType);
}
