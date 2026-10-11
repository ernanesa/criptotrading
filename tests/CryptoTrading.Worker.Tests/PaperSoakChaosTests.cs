using System.Text.Json;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Exchange.Paper;
using CryptoTrading.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CryptoTrading.Worker.Tests;

/// <summary>
/// Deterministic local chaos drill for the PAPER dispatcher boundary. It uses
/// no network, credentials, clock randomness, or production database. The
/// in-memory store deliberately models the durable lease/ack contract so a
/// worker restart can be simulated without losing the outbox.
/// </summary>
public sealed class PaperSoakChaosTests
{
    [Fact]
    [Trait("Category", "PaperChaos")]
    public async Task RepeatedRestartsAndTransientFailuresDoNotDuplicatePaperOrders()
    {
        const int orderCount = 64;
        var store = new ChaosOutboxStore();
        var exchange = new PlannedFaultPaperExchange();
        var executor = new PaperOutboxMessageExecutor(
            exchange,
            Options.Create(new WorkerRuntimeOptions { ExecutionMode = "PAPER", NoTrade = false }),
            NullLogger<PaperOutboxMessageExecutor>.Instance);
        var start = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

        for (var index = 0; index < orderCount; index++)
        {
            var command = NewCommand(index, start);
            exchange.Register(command.ClientOrderId, index);
            var record = new OutboxRecord(
                Guid.Parse($"00000000-0000-0000-0000-{index + 1:D12}"),
                OrderExecutionPipeline.SubmitOrderMessageType,
                command.CorrelationId,
                command.IdempotencyKey,
                command.PolicyVersion,
                command,
                OutboxRecordStatus.Pending,
                command.CreatedAtUtc);

            await store.EnqueueAsync(record, cancellationToken: TestContext.Current.CancellationToken);
            await store.EnqueueAsync(record, cancellationToken: TestContext.Current.CancellationToken); // duplicate producer delivery
        }

        Assert.Equal(orderCount, store.Count);

        var now = start;
        var cycle = 0;
        while (store.ProcessedCount < orderCount)
        {
            cycle++;
            Assert.True(cycle <= orderCount * 8, "The bounded chaos drill did not converge.");
            var owner = $"paper-chaos-worker-{cycle:D3}"; // each cycle is a process restart
            var claimed = await store.ClaimOutboxAsync(owner, now, TimeSpan.FromSeconds(2), 1, cancellationToken: TestContext.Current.CancellationToken);

            if (claimed.Count == 0)
            {
                now = now.AddSeconds(1);
                continue;
            }

            var message = Assert.Single(claimed);
            try
            {
                await executor.ExecuteAsync(message, cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(await store.MarkOutboxProcessedAsync(message.MessageId, owner, now, cancellationToken: TestContext.Current.CancellationToken));
            }
            catch (SimulatedWorkerCrashException)
            {
                // The process vanished after the paper exchange accepted the
                // command. No acknowledgement is written; lease expiry is the
                // only recovery path, exactly as with a real worker crash.
                now = now.AddSeconds(3);
            }
            catch (IOException exception)
            {
                Assert.True(await store.FailOutboxAsync(message.MessageId, owner, exception.GetType().Name, now, cancellationToken: TestContext.Current.CancellationToken));
                now = now.AddSeconds(1);
            }
        }

        Assert.Equal(orderCount, store.ProcessedCount);
        Assert.Equal(orderCount, exchange.OpenOrders.Count);
        Assert.Equal(orderCount, exchange.AcceptedSideEffects);
        Assert.True(exchange.IdempotentReplayCount > 0, "The drill must exercise an idempotent retry.");
        Assert.Equal(0, await store.CountClaimableAsync(now.AddMinutes(1)));
        Assert.All(store.AttemptCounts, attempts => Assert.InRange(attempts, 1, 3));
    }

    [Fact]
    [Trait("Category", "PaperChaos")]
    public async Task PersistentPaperFailuresAreBoundedAndDeadLetteredWithoutExposure()
    {
        const int maxAttempts = 3;
        var store = new ChaosOutboxStore();
        var command = NewCommand(101, DateTimeOffset.UtcNow);
        var exchange = new PersistentlyFailingPaperExchange();
        var executor = new PaperOutboxMessageExecutor(
            exchange,
            Options.Create(new WorkerRuntimeOptions { ExecutionMode = "PAPER", NoTrade = false }),
            NullLogger<PaperOutboxMessageExecutor>.Instance);

        await store.EnqueueAsync(NewRecord(command, 101), TestContext.Current.CancellationToken);
        await DispatchUntilTerminalAsync(store, executor, maxAttempts);

        Assert.Equal(maxAttempts, exchange.SubmitCalls);
        Assert.Equal(0, store.ProcessedCount);
        Assert.Equal(1, store.DeadLetteredCount);
        Assert.Equal("IOException", store.LastTerminalFailure);
        Assert.Empty(exchange.OpenOrders);
        Assert.Equal(0, await store.CountClaimableAsync(DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    [Fact]
    [Trait("Category", "PaperChaos")]
    public async Task TimeoutAfterPaperAcceptanceConvergesWithoutASecondSubmission()
    {
        var store = new ChaosOutboxStore();
        var command = NewCommand(102, DateTimeOffset.UtcNow);
        var exchange = new SubmitThenTimeoutPaperExchange();
        var executor = new PaperOutboxMessageExecutor(
            exchange,
            Options.Create(new WorkerRuntimeOptions { ExecutionMode = "PAPER", NoTrade = false }),
            NullLogger<PaperOutboxMessageExecutor>.Instance);

        await store.EnqueueAsync(NewRecord(command, 102), TestContext.Current.CancellationToken);
        await DispatchUntilTerminalAsync(store, executor, maxAttempts: 3);

        Assert.Equal(1, exchange.SubmitCalls);
        Assert.Equal(1, store.ProcessedCount);
        Assert.Equal(0, store.DeadLetteredCount);
        var order = Assert.Single(exchange.OpenOrders);
        Assert.Equal(command.ClientOrderId, order.ClientOrderId);
        Assert.Equal(0, await store.CountClaimableAsync(DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    [Fact]
    [Trait("Category", "PaperChaos")]
    public async Task UnknownOutcomeIsQuarantinedWithoutBlindRedispatch()
    {
        var store = new ChaosOutboxStore();
        var command = NewCommand(103, DateTimeOffset.UtcNow);
        var executor = new UnknownOutcomePaperExecutor(command.ClientOrderId.Value);

        await store.EnqueueAsync(NewRecord(command, 103), TestContext.Current.CancellationToken);
        await DispatchUntilTerminalAsync(store, executor, maxAttempts: 3);

        Assert.Equal(1, executor.DispatchCalls);
        Assert.Equal(0, store.ProcessedCount);
        Assert.Equal(1, store.DeadLetteredCount);
        Assert.StartsWith("ORDER_OUTCOME_UNKNOWN:", store.LastTerminalFailure, StringComparison.Ordinal);
        Assert.Equal(0, await store.CountClaimableAsync(DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    private static SubmitOrderCommand NewCommand(int index, DateTimeOffset createdAtUtc) => new(
        new OrderId(Guid.Parse($"10000000-0000-0000-0000-{index + 1:D12}")),
        new ClientOrderId($"paper-chaos-{index:D4}"),
        new Symbol("BTCUSDT"),
        OrderSide.Buy,
        OrderType.Market,
        new Quantity(0.001m),
        new Price(50_000m),
        50m,
        new CorrelationId(Guid.Parse($"20000000-0000-0000-0000-{index + 1:D12}")),
        new IdempotencyKey($"paper-chaos-key-{index:D4}"),
        "paper-chaos-v1",
        createdAtUtc);

    private static OutboxRecord NewRecord(SubmitOrderCommand command, int index) => new(
        Guid.Parse($"30000000-0000-0000-0000-{index + 1:D12}"),
        OrderExecutionPipeline.SubmitOrderMessageType,
        command.CorrelationId,
        command.IdempotencyKey,
        command.PolicyVersion,
        command,
        OutboxRecordStatus.Pending,
        command.CreatedAtUtc);

    private static async Task DispatchUntilTerminalAsync(
        ChaosOutboxStore store,
        IOutboxMessageExecutor executor,
        int maxAttempts)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var dispatcher = new OutboxDispatcher(
            [store],
            [executor],
            Options.Create(new WorkerRuntimeOptions
            {
                ExecutionMode = "PAPER",
                NoTrade = false,
                PollIntervalMilliseconds = 100,
                BatchSize = 1,
                LeaseSeconds = 1,
                MaxOutboxAttempts = maxAttempts
            }),
            NullLogger<OutboxDispatcher>.Instance);

        await dispatcher.StartAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            await store.Terminal.Task.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            await dispatcher.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class PlannedFaultPaperExchange : IPaperExchange
    {
        private readonly PaperExchange inner = new();
        private readonly Dictionary<ClientOrderId, int> indexes = [];
        private readonly HashSet<ClientOrderId> preSubmitFailures = [];
        private readonly HashSet<ClientOrderId> postSubmitCrashes = [];

        public int AcceptedSideEffects { get; private set; }

        public int IdempotentReplayCount { get; private set; }

        public IReadOnlyCollection<PaperOrderSnapshot> OpenOrders => inner.OpenOrders;

        public IReadOnlyCollection<PaperFill> Fills => inner.Fills;

        public void Register(ClientOrderId clientOrderId, int index) => indexes.Add(clientOrderId, index);

        public PaperSubmitResult Submit(PaperOrderRequest request, IdempotencyKey idempotencyKey)
        {
            if (!indexes.TryGetValue(request.ClientOrderId, out var index))
            {
                throw new InvalidOperationException("Unregistered chaos client order.");
            }

            if (index % 3 == 0 && preSubmitFailures.Add(request.ClientOrderId))
            {
                throw new IOException("deterministic transient before paper acceptance");
            }

            var result = inner.Submit(request, idempotencyKey);
            if (result.AlreadyProcessed)
            {
                IdempotentReplayCount++;
                return result;
            }

            AcceptedSideEffects++;
            if (index % 5 == 0 && postSubmitCrashes.Add(request.ClientOrderId))
            {
                throw new SimulatedWorkerCrashException();
            }

            return result;
        }

        public PaperOrderSnapshot GetOrder(ClientOrderId clientOrderId) => inner.GetOrder(clientOrderId);

        public PaperCancelResult Cancel(ClientOrderId clientOrderId, IdempotencyKey idempotencyKey) => inner.Cancel(clientOrderId, idempotencyKey);

        public IReadOnlyList<PaperFill> Apply(PaperMarketTick tick) => inner.Apply(tick);

        public IReadOnlyList<PaperFill> Replay(IEnumerable<PaperMarketTick> ticks) => inner.Replay(ticks);
    }

    private sealed class PersistentlyFailingPaperExchange : IPaperExchange
    {
        public int SubmitCalls { get; private set; }

        public IReadOnlyCollection<PaperOrderSnapshot> OpenOrders => [];

        public IReadOnlyCollection<PaperFill> Fills => [];

        public PaperSubmitResult Submit(PaperOrderRequest request, IdempotencyKey idempotencyKey)
        {
            SubmitCalls++;
            throw new IOException("deterministic persistent paper dispatch failure");
        }

        public PaperOrderSnapshot GetOrder(ClientOrderId clientOrderId) => throw new KeyNotFoundException();

        public PaperCancelResult Cancel(ClientOrderId clientOrderId, IdempotencyKey idempotencyKey) => throw new NotSupportedException();

        public IReadOnlyList<PaperFill> Apply(PaperMarketTick tick) => throw new NotSupportedException();

        public IReadOnlyList<PaperFill> Replay(IEnumerable<PaperMarketTick> ticks) => throw new NotSupportedException();
    }

    private sealed class SubmitThenTimeoutPaperExchange : IPaperExchange
    {
        private readonly PaperExchange inner = new();

        public int SubmitCalls { get; private set; }

        public IReadOnlyCollection<PaperOrderSnapshot> OpenOrders => inner.OpenOrders;

        public IReadOnlyCollection<PaperFill> Fills => inner.Fills;

        public PaperSubmitResult Submit(PaperOrderRequest request, IdempotencyKey idempotencyKey)
        {
            SubmitCalls++;
            _ = inner.Submit(request, idempotencyKey);
            throw new TimeoutException("deterministic response loss after paper acceptance");
        }

        public PaperOrderSnapshot GetOrder(ClientOrderId clientOrderId) => inner.GetOrder(clientOrderId);

        public PaperCancelResult Cancel(ClientOrderId clientOrderId, IdempotencyKey idempotencyKey) => inner.Cancel(clientOrderId, idempotencyKey);

        public IReadOnlyList<PaperFill> Apply(PaperMarketTick tick) => inner.Apply(tick);

        public IReadOnlyList<PaperFill> Replay(IEnumerable<PaperMarketTick> ticks) => inner.Replay(ticks);
    }

    private sealed class UnknownOutcomePaperExecutor(string clientOrderId) : IOutboxMessageExecutor
    {
        public string MessageType => OrderExecutionPipeline.SubmitOrderMessageType;

        public int DispatchCalls { get; private set; }

        public Task ExecuteAsync(LeasedOutboxMessage message, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DispatchCalls++;
            throw new OrderOutcomeUnknownException(clientOrderId);
        }
    }

    private sealed class ChaosOutboxStore : IOutboxStore
    {
        private readonly Dictionary<IdempotencyKey, ChaosEntry> entries = [];

        public int Count => entries.Count;

        public int ProcessedCount => entries.Values.Count(entry => entry.Processed);

        public int DeadLetteredCount => entries.Values.Count(entry => entry.DeadLettered);

        public string? LastTerminalFailure { get; private set; }

        public TaskCompletionSource Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IEnumerable<int> AttemptCounts => entries.Values.Select(entry => entry.Attempts);

        public Task<OutboxRecord> EnqueueAsync(OutboxRecord record, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entries.TryGetValue(record.IdempotencyKey, out var existing))
            {
                if (!string.Equals(JsonSerializer.Serialize(existing.Record.Command), JsonSerializer.Serialize(record.Command), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Idempotency key was reused with a different payload.");
                }

                return Task.FromResult(existing.Record);
            }

            entries.Add(record.IdempotencyKey, new ChaosEntry(record));
            return Task.FromResult(record);
        }

        public Task<IReadOnlyList<LeasedOutboxMessage>> ClaimOutboxAsync(
            string leaseOwner,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            int batchSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries.Values
                .Where(candidate => !candidate.Processed
                    && !candidate.DeadLettered
                    && candidate.Record.CreatedAtUtc <= nowUtc
                    && (candidate.LockedUntil is null || candidate.LockedUntil <= nowUtc))
                .OrderBy(candidate => candidate.Record.CreatedAtUtc)
                .FirstOrDefault();
            if (entry is null || batchSize < 1)
            {
                return Task.FromResult<IReadOnlyList<LeasedOutboxMessage>>([]);
            }

            entry.Attempts++;
            entry.LeaseOwner = leaseOwner;
            entry.LockedUntil = nowUtc.Add(leaseDuration);
            return Task.FromResult<IReadOnlyList<LeasedOutboxMessage>>([
                new LeasedOutboxMessage(
                    entry.Record.MessageId,
                    entry.Record.MessageType,
                    entry.Record.CorrelationId,
                    entry.Record.IdempotencyKey,
                    JsonSerializer.Serialize(entry.Record.Command),
                    entry.Record.CreatedAtUtc,
                    entry.Attempts,
                    entry.LockedUntil.Value,
                    leaseOwner),
            ]);
        }

        public Task<bool> MarkOutboxProcessedAsync(
            Guid messageId,
            string leaseOwner,
            DateTimeOffset processedAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = Find(messageId);
            if (entry is null || entry.Processed || !string.Equals(entry.LeaseOwner, leaseOwner, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            entry.Processed = true;
            entry.LockedUntil = null;
            Terminal.TrySetResult();
            return Task.FromResult(true);
        }

        public Task<bool> FailOutboxAsync(
            Guid messageId,
            string leaseOwner,
            string failure,
            DateTimeOffset retryAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = Find(messageId);
            if (entry is null || entry.Processed || !string.Equals(entry.LeaseOwner, leaseOwner, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            entry.LockedUntil = retryAtUtc;
            return Task.FromResult(true);
        }

        public Task<bool> DeadLetterOutboxAsync(
            Guid messageId,
            string leaseOwner,
            string failure,
            DateTimeOffset deadLetteredAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = Find(messageId);
            if (entry is null || entry.Processed || !string.Equals(entry.LeaseOwner, leaseOwner, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            entry.DeadLettered = true;
            entry.LockedUntil = null;
            LastTerminalFailure = failure;
            Terminal.TrySetResult();
            return Task.FromResult(true);
        }

        public Task<int> CountClaimableAsync(DateTimeOffset nowUtc) =>
            Task.FromResult(entries.Values.Count(entry => !entry.Processed && !entry.DeadLettered
                && (entry.LockedUntil is null || entry.LockedUntil <= nowUtc)));

        private ChaosEntry? Find(Guid messageId) => entries.Values.FirstOrDefault(entry => entry.Record.MessageId == messageId);

        private sealed class ChaosEntry(OutboxRecord record)
        {
            public OutboxRecord Record { get; } = record;
            public int Attempts { get; set; }
            public bool Processed { get; set; }
            public bool DeadLettered { get; set; }
            public string? LeaseOwner { get; set; }
            public DateTimeOffset? LockedUntil { get; set; }
        }
    }

    private sealed class SimulatedWorkerCrashException : IOException
    {
        public SimulatedWorkerCrashException()
            : base("deterministic worker crash after paper acceptance")
        {
        }

        public SimulatedWorkerCrashException(string message)
            : base(message)
        {
        }

        public SimulatedWorkerCrashException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
