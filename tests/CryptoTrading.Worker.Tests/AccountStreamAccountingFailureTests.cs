using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Domain.Runtime;
using CryptoTrading.Exchange.Binance;
using CryptoTrading.Observability;
using CryptoTrading.Persistence;
using CryptoTrading.Reconciliation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class AccountStreamAccountingFailureTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task AccountingFailureStopsStreamEvenWhenSafeModeControlFails(int controlFailure)
    {
        var stores = new OfflineStores();
        var control = new TestControl(controlFailure);
        var stream = new TwoFillStream();
        using var supervisor = new RuntimeSupervisor();
        using var alerts = new TradingAlertDispatcher(new InMemoryTradingAlertSink());
        using var writer = new ReconciliationReportWriter(Path.Combine("/tmp/kilo", "offline-unused-account-reconciliation"));
        var reconciliation = new AccountReconciliationService(stores, stores, stores,
            new InMemoryReconciliationEventSink(), control,
            new ReconciliationOptions(new AccountId("exchange-wallet"), ["BTCUSDT"]));
        var options = Options.Create(new WorkerRuntimeOptions { AccountStreamEnabled = true, NoTrade = true, ReconciliationAccount = "exchange-wallet" });
        using var service = new AccountStreamHostedService(options, stream, stores, stores, stores, stores, stores,
            control, reconciliation, writer, alerts, supervisor, NullLogger<AccountStreamHostedService>.Instance);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        try
        {
            var start = service.StartAsync(cancellation.Token);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await start.ConfigureAwait(false);
                await service.ExecuteTask!.WaitAsync(TimeSpan.FromMilliseconds(750), TestContext.Current.CancellationToken).ConfigureAwait(false);
            });

            Assert.DoesNotContain("offline-synthetic", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("offline-control-failure", error.Message, StringComparison.Ordinal);
            Assert.Single(stores.Events);
            Assert.Equal(AccountStreamHostedService.ToRecord(stream.First).PayloadSha256, stores.Events[0].PayloadSha256);
            Assert.Equal(["event", "projection", "accounting"], stores.Steps);
            Assert.Equal(1, stores.ProjectedCount);
            Assert.Equal(1, stores.AccountingAttempts);
            Assert.True(control.SafeModeAttempts > 0);
            if (controlFailure is 0 or 4) Assert.Equal(OperationalMode.SafeMode, control.Snapshot.Mode);
        }
        finally
        {
            await cancellation.CancelAsync();
        }
    }

    [Fact]
    public async Task NormalCancellationDoesNotRecordAccountingFailure()
    {
        var stores = new OfflineStores();
        var control = new TestControl(0);
        var stream = new WaitingStream();
        using var supervisor = new RuntimeSupervisor();
        using var alerts = new TradingAlertDispatcher(new InMemoryTradingAlertSink());
        using var writer = new ReconciliationReportWriter(Path.Combine("/tmp/kilo", "offline-unused-account-reconciliation"));
        var reconciliation = new AccountReconciliationService(stores, stores, stores,
            new InMemoryReconciliationEventSink(), control,
            new ReconciliationOptions(new AccountId("exchange-wallet"), ["BTCUSDT"]));
        var options = Options.Create(new WorkerRuntimeOptions { AccountStreamEnabled = true, NoTrade = true });
        using var service = new AccountStreamHostedService(options, stream, stores, stores, stores, stores, stores,
            control, reconciliation, writer, alerts, supervisor, NullLogger<AccountStreamHostedService>.Instance);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await service.StartAsync(cancellation.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Empty(stores.Events);
        Assert.Equal(0, stores.AccountingAttempts);
        Assert.Equal(0, control.SafeModeAttempts);
    }

    [Fact]
    public async Task CancellationConcurrentWithAccountingFailureDoesNotHideTheFailure()
    {
        var stores = new OfflineStores();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var control = new TestControl(0) { OnEnter = cancellation.Cancel };
        using var supervisor = new RuntimeSupervisor();
        using var alerts = new TradingAlertDispatcher(new InMemoryTradingAlertSink());
        using var writer = new ReconciliationReportWriter(Path.Combine("/tmp/kilo", "offline-unused-account-reconciliation"));
        var reconciliation = new AccountReconciliationService(stores, stores, stores,
            new InMemoryReconciliationEventSink(), control,
            new ReconciliationOptions(new AccountId("exchange-wallet"), ["BTCUSDT"]));
        var options = Options.Create(new WorkerRuntimeOptions { AccountStreamEnabled = true, NoTrade = true });
        using var service = new AccountStreamHostedService(options, new TwoFillStream(), stores, stores, stores, stores, stores,
            control, reconciliation, writer, alerts, supervisor, NullLogger<AccountStreamHostedService>.Instance);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await service.StartAsync(cancellation.Token).ConfigureAwait(false);
            await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(false);
        });

        Assert.DoesNotContain("offline-synthetic", error.Message, StringComparison.Ordinal);
        Assert.Equal(OperationalMode.SafeMode, control.Snapshot.Mode);
        Assert.Single(stores.Events);
    }

    [Theory]
    [InlineData("event")]
    [InlineData("projection")]
    [InlineData("accounting")]
    public async Task CancellationDuringFillPersistenceRequiresContainment(string stage)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var stores = new OfflineStores
        {
            CancelAtEventStore = stage == "event",
            CancelAtProjection = stage == "projection",
            CancelAtAccounting = stage == "accounting",
            OnCancel = cancellation.Cancel
        };
        var control = new TestControl(0);
        using var supervisor = new RuntimeSupervisor();
        using var alerts = new TradingAlertDispatcher(new InMemoryTradingAlertSink());
        using var writer = new ReconciliationReportWriter(Path.Combine("/tmp/kilo", "offline-unused-account-reconciliation"));
        var reconciliation = new AccountReconciliationService(stores, stores, stores,
            new InMemoryReconciliationEventSink(), control,
            new ReconciliationOptions(new AccountId("exchange-wallet"), ["BTCUSDT"]));
        var options = Options.Create(new WorkerRuntimeOptions { AccountStreamEnabled = true, NoTrade = true });
        using var service = new AccountStreamHostedService(options, new TwoFillStream(), stores, stores, stores, stores, stores,
            control, reconciliation, writer, alerts, supervisor, NullLogger<AccountStreamHostedService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await service.StartAsync(cancellation.Token).ConfigureAwait(false);
            await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(false);
        });

        Assert.Equal(OperationalMode.SafeMode, control.Snapshot.Mode);
        Assert.Equal("ACCOUNTING_EXECUTION_FAILURE", control.Snapshot.Reason);
        Assert.True(control.SafeModeAttempts > 0);
        Assert.InRange(stores.Events.Count, 0, 1);
    }

    [Fact]
    public async Task NonCooperativeSafeModeControlCannotPreventTerminalFailure()
    {
        var stores = new OfflineStores();
        var control = new TestControl(3);
        var logger = new RecordingLogger();
        using var clock = new ManualFailureTimeProvider();
        using var supervisor = new RuntimeSupervisor();
        using var alerts = new TradingAlertDispatcher(new InMemoryTradingAlertSink());
        using var writer = new ReconciliationReportWriter(Path.Combine("/tmp/kilo", "offline-unused-account-reconciliation"));
        var reconciliation = new AccountReconciliationService(stores, stores, stores,
            new InMemoryReconciliationEventSink(), control,
            new ReconciliationOptions(new AccountId("exchange-wallet"), ["BTCUSDT"]));
        var options = Options.Create(new WorkerRuntimeOptions { AccountStreamEnabled = true, NoTrade = true });
        using var service = new AccountStreamHostedService(options, new TwoFillStream(), stores, stores, stores, stores, stores,
            control, reconciliation, writer, alerts, supervisor, logger, timeProvider: clock);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        try
        {
            var start = service.StartAsync(cancellation.Token);
            await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            clock.Fire();
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await start.ConfigureAwait(false);
                await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(false);
            });
            Assert.Single(stores.Events);
            Assert.Equal(1, stores.AccountingAttempts);
            Assert.Contains(logger.Messages, message => message.Contains("SafeMode was not confirmed", StringComparison.Ordinal));
        }
        finally
        {
            control.Pending.TrySetException(new IOException("offline-control-failure"));
            await cancellation.CancelAsync();
        }
    }

    [Fact]
    public async Task NonCooperativeSupervisorCannotPreventTerminalFailureOrSafeModeAttempt()
    {
        var stores = new OfflineStores();
        var safeModeAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var control = new TestControl(0) { OnEnter = () => safeModeAttempt.TrySetResult() };
        var supervisor = new HangingSupervisor();
        var logger = new RecordingLogger();
        using var clock = new ManualFailureTimeProvider();
        using var alerts = new TradingAlertDispatcher(new InMemoryTradingAlertSink());
        using var writer = new ReconciliationReportWriter(Path.Combine("/tmp/kilo", "offline-unused-account-reconciliation"));
        var reconciliation = new AccountReconciliationService(stores, stores, stores,
            new InMemoryReconciliationEventSink(), control,
            new ReconciliationOptions(new AccountId("exchange-wallet"), ["BTCUSDT"]));
        var options = Options.Create(new WorkerRuntimeOptions { AccountStreamEnabled = true, NoTrade = true });
        using var service = new AccountStreamHostedService(options, new TwoFillStream(), stores, stores, stores, stores, stores,
            control, reconciliation, writer, alerts, supervisor, logger, timeProvider: clock);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        try
        {
            var start = service.StartAsync(cancellation.Token);
            await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            await safeModeAttempt.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            clock.Fire();
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await start.ConfigureAwait(false);
                await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(false);
            });
            Assert.Equal(OperationalMode.SafeMode, control.Snapshot.Mode);
            Assert.Single(stores.Events);
            Assert.DoesNotContain(logger.Messages, message => message.Contains("SafeMode was not confirmed", StringComparison.Ordinal));
        }
        finally
        {
            supervisor.Pending.TrySetException(new IOException("offline-supervisor-failure"));
            await cancellation.CancelAsync();
        }
    }

    [Fact]
    public async Task SupervisorFailureDoesNotHideTerminalErrorOrPreventSafeModeAttempt()
    {
        var stores = new OfflineStores();
        var control = new TestControl(0);
        using var alerts = new TradingAlertDispatcher(new InMemoryTradingAlertSink());
        using var writer = new ReconciliationReportWriter(Path.Combine("/tmp/kilo", "offline-unused-account-reconciliation"));
        var reconciliation = new AccountReconciliationService(stores, stores, stores,
            new InMemoryReconciliationEventSink(), control,
            new ReconciliationOptions(new AccountId("exchange-wallet"), ["BTCUSDT"]));
        var options = Options.Create(new WorkerRuntimeOptions { AccountStreamEnabled = true, NoTrade = true });
        using var service = new AccountStreamHostedService(options, new TwoFillStream(), stores, stores, stores, stores, stores,
            control, reconciliation, writer, alerts, new ThrowingSupervisor(), NullLogger<AccountStreamHostedService>.Instance);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await service.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(false);
        });

        Assert.DoesNotContain("offline-supervisor-failure", error.Message, StringComparison.Ordinal);
        Assert.Equal(OperationalMode.SafeMode, control.Snapshot.Mode);
        Assert.Single(stores.Events);
    }

    private sealed class ThrowingSupervisor : IRuntimeSupervisor
    {
        public RuntimeSupervisorSnapshot Snapshot { get; } = new(RuntimeState.Stopped, 0, false, false, false, OperationalMode.Normal, 0);
        public Task BeginBootstrappingAsync(string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkReconciliationAsync(bool healthy, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkAccountStreamAsync(bool connected, string reason, CancellationToken cancellationToken = default) => Task.FromException(new IOException("offline-supervisor-failure"));
        public Task MarkWarmupAsync(bool complete, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EnterSafeModeAsync(string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ObserveOperationalControlAsync(OperationalControlSnapshot operationalControl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAsync(string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class HangingSupervisor : IRuntimeSupervisor
    {
        public TaskCompletionSource Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RuntimeSupervisorSnapshot Snapshot { get; } = new(RuntimeState.Stopped, 0, false, false, false, OperationalMode.Normal, 0);
        public Task BeginBootstrappingAsync(string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkReconciliationAsync(bool healthy, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkAccountStreamAsync(bool connected, string reason, CancellationToken cancellationToken = default) => Pending.Task;
        public Task MarkWarmupAsync(bool complete, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EnterSafeModeAsync(string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ObserveOperationalControlAsync(OperationalControlSnapshot operationalControl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAsync(string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ManualFailureTimeProvider : TimeProvider, IDisposable
    {
        private ManualTimer? timer;
        public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            timer = new ManualTimer(callback, state);
            TimerCreated.TrySetResult();
            return timer;
        }
        public void Fire() => timer!.Fire();
        public void Dispose() => timer?.Dispose();
    }

    private sealed class RecordingLogger : ILogger<AccountStreamHostedService>
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception));
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Fire() => callback(state);
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class WaitingStream : IBinanceUserDataStreamClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<BinanceUserDataEvent> ReadAsync(string listenKey,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            yield break;
        }
    }

    private sealed class TwoFillStream : IBinanceUserDataStreamClient
    {
        public BinanceExecutionReportEvent First { get; } = CreateFill(42, 7, "client-42", 0.01m);

        public async IAsyncEnumerable<BinanceUserDataEvent> ReadAsync(string listenKey,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return First;
            yield return CreateFill(43, 8, "client-43", 0.02m);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        private static BinanceExecutionReportEvent CreateFill(long orderId, long tradeId, string clientOrderId, decimal quantity)
        {
            var now = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123);
            return new BinanceExecutionReportEvent(now, now.ToUnixTimeMilliseconds(), "BTCUSDT", orderId, tradeId,
                clientOrderId, "BUY", "LIMIT", "TRADE", "FILLED", quantity, quantity, 100.50m, 0.001m, "USDT", OriginalQuantity: quantity);
        }
    }

    private sealed class TestControl(int failureMode) : IAsyncOperationalControl
    {
        private readonly InMemoryOperationalControl inner = new(new DenyOperationalControlResetAuthorizer(),
            asyncAuditSink: failureMode == 4 ? new FailingAuditSink() : null);
        public Action? OnEnter { get; init; }
        public TaskCompletionSource<OperationalControlResult> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SafeModeAttempts { get; private set; }
        public OperationalControlSnapshot Snapshot => inner.Snapshot;
        public Task<OperationalControlResult> ActivateKillSwitchAsync(OperationalControlCommand command, CancellationToken cancellationToken = default) => inner.ActivateKillSwitchAsync(command, cancellationToken);
        public Task<OperationalControlResult> ResetAsync(OperationalControlCommand command, OperationalControlResetAuthorization authorization, CancellationToken cancellationToken = default) => inner.ResetAsync(command, authorization, cancellationToken);
        public Task<OperationalControlResult> EnterSafeModeAsync(OperationalControlCommand command, CancellationToken cancellationToken = default)
        {
            SafeModeAttempts++;
            OnEnter?.Invoke();
            return failureMode switch
            {
                1 => Task.FromException<OperationalControlResult>(new InvalidOperationException("offline-control-failure")),
                2 => Task.FromException<OperationalControlResult>(new IOException("offline-control-failure")),
                3 => Pending.Task,
                _ => inner.EnterSafeModeAsync(command, cancellationToken)
            };
        }
    }

    private sealed class FailingAuditSink : IAsyncOperationalControlAuditSink
    {
        public Task AppendAsync(OperationalControlAuditEvent auditEvent, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("offline-audit-failure"));
    }

    private sealed class OfflineStores : IAccountStreamEventStore, IAccountStreamOrderProjectionStore, IExecutionAccountingStore,
        IExpectedExecutionFeeStore, IExecutionFeeAssetValuationProvider, IReadOnlyAccountSnapshotProvider, ILocalBalanceReader, IKnownTradeReader
    {
        public List<AccountStreamEventRecord> Events { get; } = [];
        public List<string> Steps { get; } = [];
        public int ProjectedCount { get; private set; }
        public int AccountingAttempts { get; private set; }
        public bool CancelAtEventStore { get; init; }
        public bool CancelAtProjection { get; init; }
        public bool CancelAtAccounting { get; init; }
        public Action? OnCancel { get; init; }
        public Task<bool> AppendAccountStreamEventAsync(AccountStreamEventRecord record, CancellationToken cancellationToken = default)
        {
            Steps.Add("event");
            if (CancelAtEventStore) { OnCancel?.Invoke(); return Task.FromCanceled<bool>(cancellationToken); }
            Events.Add(record);
            return Task.FromResult(true);
        }
        public Task<IReadOnlyList<AccountStreamEventRecord>> ReadAccountStreamEventsAsync(DateTimeOffset sinceUtc, int limit = 1_000, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LocalOpenOrderRecord>> ReadOpenOrdersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LocalOpenOrderRecord>>([]);
        public Task UpsertOpenOrderAsync(LocalOpenOrderRecord order, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveOpenOrderAsync(string clientOrderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> ApplyAccountStreamExecutionReportAsync(LocalOpenOrderRecord order, bool isTerminal, CancellationToken cancellationToken = default)
        {
            Steps.Add("projection");
            ProjectedCount++;
            if (CancelAtProjection) { OnCancel?.Invoke(); return Task.FromCanceled<bool>(cancellationToken); }
            return Task.FromResult(true);
        }
        public Task<bool> ApplyAuthoritativeOpenOrderSnapshotAsync(LocalOpenOrderRecord order, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExecutionAccountingResult> RecordExecutionAsync(ExecutionAccountingCommand command, CancellationToken cancellationToken = default)
        {
            Steps.Add("accounting");
            AccountingAttempts++;
            if (CancelAtAccounting) { OnCancel?.Invoke(); return Task.FromCanceled<ExecutionAccountingResult>(cancellationToken); }
            return Task.FromException<ExecutionAccountingResult>(new InvalidOperationException("offline-synthetic"));
        }
        public Task RecordExpectedExecutionFeeAsync(SubmitOrderCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExecutionFeeEstimate?> ReadExpectedExecutionFeeAsync(string clientOrderId, CancellationToken cancellationToken = default) => Task.FromResult<ExecutionFeeEstimate?>(null);
        public Task<ExecutionFeeAssetValuation?> ResolveAsync(BinanceExecutionReportEvent execution, CancellationToken cancellationToken = default) => Task.FromResult<ExecutionFeeAssetValuation?>(null);
        public Task<ReconciliationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<decimal> GetBalanceAsync(AccountId account, string asset, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlySet<string>> GetAssetsAsync(AccountId account, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlySet<KnownTradeIdentity>> GetKnownTradeIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
