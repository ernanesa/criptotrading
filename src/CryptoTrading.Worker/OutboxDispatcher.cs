#pragma warning disable CA1515 // Worker contracts are intentionally public for host-level contract tests and composition.
#pragma warning disable CA1031 // Dispatch boundary converts all non-cancellation failures into durable retries.

using System.Text.Json;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Exchange.Paper;
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

        var result = exchange.Submit(
            new PaperOrderRequest(command.ClientOrderId, command.Symbol, command.Side, command.Type, command.Quantity),
            command.IdempotencyKey);
        PaperExecutorLog.Submitted(logger, message.MessageId, command.Symbol.Value, result.AlreadyProcessed);
        return Task.CompletedTask;
    }

    private static SubmitOrderCommand DeserializeCommand(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        return new SubmitOrderCommand(
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
    }

    private static string ReadString(JsonElement root, string property) => ReadValue(root, property).GetString()
        ?? throw new JsonException($"Property '{property}' must be a string.");

    private static Guid ReadGuid(JsonElement root, string property) => Guid.Parse(ReadString(root, property));

    private static decimal ReadDecimal(JsonElement root, string property) => ReadValue(root, property).GetDecimal();

    private static T ReadEnum<T>(JsonElement root, string property) where T : struct, Enum
    {
        var value = ReadValue(root, property);
        return value.ValueKind == JsonValueKind.String
            ? Enum.Parse<T>(value.GetString()!, ignoreCase: true)
            : (T)Enum.ToObject(typeof(T), value.GetInt32());
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
                    await DispatchOneAsync(store, message, executorMap, runtime, stoppingToken).ConfigureAwait(false);
                }
            }

            await Task.Delay(runtime.PollIntervalMilliseconds, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task DispatchOneAsync(
        IOutboxStore store,
        LeasedOutboxMessage message,
        Dictionary<string, IOutboxMessageExecutor> executors,
        WorkerRuntimeOptions runtime,
        CancellationToken cancellationToken)
    {
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
                DispatcherLog.Processed(logger, message.MessageId, message.MessageType);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var delaySeconds = Math.Min(300d, Math.Pow(2d, Math.Min(message.Attempts - 1, 8)));
            var retryAt = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);
            await store.FailOutboxAsync(message.MessageId, leaseOwner, exception.GetType().Name, retryAt, cancellationToken).ConfigureAwait(false);
            DispatcherLog.Failed(logger, message.MessageId, message.MessageType, exception.GetType().Name, delaySeconds);
        }
    }
}

internal static partial class PaperExecutorLog
{
    [LoggerMessage(LogLevel.Information, "Paper outbox command submitted. MessageId={MessageId} Symbol={Symbol} AlreadyProcessed={AlreadyProcessed}")]
    public static partial void Submitted(ILogger logger, Guid messageId, string symbol, bool alreadyProcessed);
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
}
