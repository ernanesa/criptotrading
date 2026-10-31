#pragma warning disable CA2007 // Integration assertions intentionally follow the test task context.

using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Domain.Research;
using CryptoTrading.Domain.Runtime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using CryptoTrading.Backtesting;
using CryptoTrading.Backtesting.Strategies;
using Xunit;
using Xunit.Sdk;

namespace CryptoTrading.Persistence.IntegrationTests;

/// <summary>
/// Runs against the PostgreSQL instance explicitly supplied by the integration
/// script. It never reads Binance credentials and uses unique identities per run.
/// </summary>
public sealed class PostgresTradingStoreIntegrationTests
{
    [Fact]
    public async Task AutonomyDecisionCommitAtomicallyPersistsDispatchableEvidenceOutboxAndCheckpoint()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) throw SkipException.ForSkip("POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        await store.ApplyMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var commit = NewAutonomyCommit("dispatch-" + Guid.NewGuid().ToString("N")[..8], now, dispatchable: true);

        await store.CommitAsync(commit, cancellationToken: TestContext.Current.CancellationToken);
        await store.CommitAsync(commit, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(now, await store.GetLastProcessedCloseAsync(commit.CheckpointScope, cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(await store.ReadExecutionSubmissionContextAsync(commit.OutboxRecord!.Command.ClientOrderId.Value, cancellationToken: TestContext.Current.CancellationToken));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM trading.autonomy_decisions WHERE decision_id = @decision_id;", connection);
        command.Parameters.AddWithValue("decision_id", commit.DecisionId);
        Assert.Equal(1L, await command.ExecuteScalarAsync(cancellationToken: TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CommitAsync(commit with { FeatureFingerprint = Hash("conflicting-feature") }, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AutonomyDecisionCommitPersistsNoTradeAndCheckpointWithoutOutbox()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) throw SkipException.ForSkip("POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        await store.ApplyMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 21, 12, 1, 0, TimeSpan.Zero);
        var commit = NewAutonomyCommit("no-trade-" + Guid.NewGuid().ToString("N")[..8], now, dispatchable: false);

        await store.CommitAsync(commit, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(now, await store.GetLastProcessedCloseAsync(commit.CheckpointScope, cancellationToken: TestContext.Current.CancellationToken));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT outcome, outbox_message_id IS NULL FROM trading.autonomy_decisions WHERE decision_id = @decision_id;", connection);
        command.Parameters.AddWithValue("decision_id", commit.DecisionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("NO_TRADE", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
    }

    [Fact]
    public async Task RuntimeStateCheckpointPersistsMonotonicTransitionAndAcceptsOnlyEquivalentReplay()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) throw SkipException.ForSkip("POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        await store.ApplyMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var transition = new RuntimeStateTransition(RuntimeState.Stopped, RuntimeState.Bootstrapping, 1, "integration-boot", DateTimeOffset.UtcNow);

        await store.AppendRuntimeTransitionAsync(transition, cancellationToken: TestContext.Current.CancellationToken);
        await store.AppendRuntimeTransitionAsync(transition, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new RuntimeStateCheckpoint(RuntimeState.Bootstrapping, 1), await store.ReadRuntimeStateAsync(cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendRuntimeTransitionAsync(transition with { Reason = "conflict" }, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TerminalCancellationAtomicallyDeactivatesOpenOrderAndPersistsIdempotentResidualCandidate()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) throw SkipException.ForSkip("POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        await store.ApplyMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow;
        var command = new CancelOrderCommand(OrderId.New(), new ClientOrderId("cancel-" + Guid.NewGuid().ToString("N")[..8]), new Symbol("BTCUSDT"),
            OrderSide.Buy, OrderType.Limit, new Quantity(.01m), new Quantity(.005m), new Price(10_000m), CorrelationId.New(),
            new IdempotencyKey("cancel-key-" + Guid.NewGuid().ToString("N")[..8]), 0, now.AddMinutes(-2), now.AddMinutes(-1), "42");
        await store.UpsertOpenOrderAsync(new LocalOpenOrderRecord(command.Symbol.Value, 42, command.ClientOrderId.Value, "PENDING_CANCEL", now, .01m, .005m), cancellationToken: TestContext.Current.CancellationToken);
        var outcome = CancelLifecyclePlanner.Resolve(command, CancelVenueStatus.Canceled, new Quantity(.005m));
        var transition = new OrderTransition(OrderStatus.CancelPending, outcome.ProjectedStatus, new IdempotencyKey("terminal-" + command.ClientOrderId.Value), now, "42");
        var resolution = new TerminalCancellationResolution(command, outcome, Guid.NewGuid(), OrderLifecycleEvent.Create(command.ClientOrderId.Value, transition));

        var first = await store.ResolveTerminalCancellationAsync(resolution, cancellationToken: TestContext.Current.CancellationToken);
        var replay = await store.ResolveTerminalCancellationAsync(resolution, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.Equal(first, replay);
        Assert.Contains(await store.ReadReplanCandidatesAsync(cancellationToken: TestContext.Current.CancellationToken), candidate => candidate.CandidateId == first.CandidateId);
        Assert.DoesNotContain(await store.ReadOpenOrdersAsync(cancellationToken: TestContext.Current.CancellationToken), item => item.ClientOrderId == command.ClientOrderId.Value);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM trading.execution_replan_candidates WHERE cancel_message_id = @message_id;", connection);
        count.Parameters.AddWithValue("message_id", resolution.CancelMessageId);
        Assert.Equal(1L, await count.ExecuteScalarAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExpiredCancellationScanSkipsAlreadyEnqueuedOrderAndReturnsNextEligibleOrder()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) throw SkipException.ForSkip("POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        await store.ApplyMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow;
        var first = NewExpiredSubmit("ttl-a-" + Guid.NewGuid().ToString("N")[..8], now);
        var second = NewExpiredSubmit("ttl-b-" + Guid.NewGuid().ToString("N")[..8], now);
        foreach (var command in new[] { first, second })
        {
            await store.EnqueueAsync(new OutboxRecord(Guid.NewGuid(), OrderExecutionPipeline.SubmitOrderMessageType, command.CorrelationId, command.IdempotencyKey, "ttl", command, OutboxRecordStatus.Pending, command.CreatedAtUtc), cancellationToken: TestContext.Current.CancellationToken);
            await store.UpsertOpenOrderAsync(new LocalOpenOrderRecord(command.Symbol.Value, 42, command.ClientOrderId.Value, "NEW", now, command.Quantity.Value, 0m), cancellationToken: TestContext.Current.CancellationToken);
            await store.AppendOrderLifecycleEventAsync(OrderLifecycleEvent.Create(command.ClientOrderId.Value, new OrderTransition(OrderStatus.Submitted, OrderStatus.Acknowledged, new IdempotencyKey("ack-" + command.ClientOrderId.Value), now, "42")), cancellationToken: TestContext.Current.CancellationToken);
        }
        var firstPlan = CancelLifecyclePlanner.Plan((await store.ReadExpiredCancellableExecutionsAsync(now, cancellationToken: TestContext.Current.CancellationToken)).Single(item => item.ClientOrderId == first.ClientOrderId));
        await store.EnqueueCancellationAsync(firstPlan.OutboxRecord!, cancellationToken: TestContext.Current.CancellationToken);
        await store.EnqueueCancellationAsync(firstPlan.OutboxRecord!, cancellationToken: TestContext.Current.CancellationToken);

        var eligible = await store.ReadExpiredCancellableExecutionsAsync(now, cancellationToken: TestContext.Current.CancellationToken);

        var snapshot = Assert.Single(eligible);
        Assert.Equal(second.ClientOrderId, snapshot.ClientOrderId);
    }
    [Fact]
    public async Task AppendsOrderLifecycleEventsIdempotentlyAndRejectsConflictingReplay()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        }

        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        await store.ApplyMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var clientOrderId = "ct-lifecycle-" + suffix;
        var occurredAtUtc = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var transition = new OrderTransition(
            OrderStatus.Validated, OrderStatus.Submitted, new IdempotencyKey("lifecycle-" + suffix), occurredAtUtc, "12345");
        var lifecycleEvent = OrderLifecycleEvent.Create(clientOrderId, transition);

        Assert.True(await store.AppendOrderLifecycleEventAsync(lifecycleEvent, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await store.AppendOrderLifecycleEventAsync(lifecycleEvent, cancellationToken: TestContext.Current.CancellationToken));

        var conflicting = OrderLifecycleEvent.Create(
            clientOrderId,
            transition with { To = OrderStatus.Rejected });
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendOrderLifecycleEventAsync(conflicting, cancellationToken: TestContext.Current.CancellationToken));

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM trading.order_lifecycle_events WHERE client_order_id = @client_order_id;", connection);
        command.Parameters.AddWithValue("client_order_id", clientOrderId);
        Assert.Equal(1L, await command.ExecuteScalarAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PersistsExecutionLedgerPositionAndRealizedPnlIdempotently()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        }

        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString!));
        await store.ApplyMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var symbol = new Symbol($"FOO{suffix}USDT");
        var baseAsset = new Asset($"FOO{suffix}");
        var quoteAsset = new Asset("USDT");
        var wallet = new AccountId($"integration-execution-wallet-{suffix}");
        var fees = new AccountId($"integration-execution-fees-{suffix}");
        var first = ExecutionCommand("buy-" + suffix, 1001, OrderSide.Buy, 1m, 100m, .1m, symbol, baseAsset, quoteAsset, wallet, fees);
        var firstResult = await store.RecordExecutionAsync(first, cancellationToken: TestContext.Current.CancellationToken);
        var replay = await store.RecordExecutionAsync(first, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(firstResult.IsDuplicate);
        Assert.True(replay.IsDuplicate);
        Assert.Equal(firstResult.LedgerEntry.Id, replay.LedgerEntry.Id);
        var conflictingReplay = new ExecutionAccountingCommand(
            first.ExecutionId, first.TradeId, first.OrderId, first.Symbol, first.BaseAsset, first.QuoteAsset,
            first.Side, new Quantity(2m), first.Price, first.Commission, first.WalletAccount, first.FeesAccount,
            first.LedgerEntryId, first.CorrelationId, first.IdempotencyKey, first.OccurredAtUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RecordExecutionAsync(conflictingReplay, TestContext.Current.CancellationToken));
        var conflictingOrder = new ExecutionAccountingCommand(
            first.ExecutionId, first.TradeId, first.OrderId + 1, first.Symbol, first.BaseAsset, first.QuoteAsset,
            first.Side, first.Quantity, first.Price, first.Commission, first.WalletAccount, first.FeesAccount,
            first.LedgerEntryId, first.CorrelationId, first.IdempotencyKey, first.OccurredAtUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RecordExecutionAsync(conflictingOrder, TestContext.Current.CancellationToken));
        var conflictingClientId = new ExecutionAccountingCommand(
            first.ExecutionId, first.TradeId, first.OrderId, first.Symbol, first.BaseAsset, first.QuoteAsset,
            first.Side, first.Quantity, first.Price, first.Commission, first.WalletAccount, first.FeesAccount,
            first.LedgerEntryId, first.CorrelationId, first.IdempotencyKey, first.OccurredAtUtc, clientOrderId: "different-client");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RecordExecutionAsync(conflictingClientId, TestContext.Current.CancellationToken));
        var laterEstimate = new ExecutionFeeEstimate(0.001m, "offline-snapshot", "unit-test",
            first.OccurredAtUtc.AddMinutes(-1), first.OccurredAtUtc.AddMinutes(1));
        var replayWithChangedEstimate = new ExecutionAccountingCommand(
            first.ExecutionId, first.TradeId, first.OrderId, first.Symbol, first.BaseAsset, first.QuoteAsset,
            first.Side, first.Quantity, first.Price, first.Commission, first.WalletAccount, first.FeesAccount,
            first.LedgerEntryId, first.CorrelationId, first.IdempotencyKey, first.OccurredAtUtc, feeEstimate: laterEstimate);
        var historical = await store.RecordExecutionAsync(replayWithChangedEstimate, TestContext.Current.CancellationToken);
        Assert.True(historical.IsDuplicate);
        Assert.Equal(firstResult.FeeComparison, historical.FeeComparison);
        Assert.Equal(firstResult.QualityComparison, historical.QualityComparison);

        var sell = ExecutionCommand("sell-" + suffix, 1002, OrderSide.Sell, .5m, 120m, .05m, symbol, baseAsset, quoteAsset, wallet, fees);
        var sellResult = await store.RecordExecutionAsync(sell, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(9.9m, sellResult.RealizedPnlQuote);
        Assert.Equal(.5m, (await store.GetBalanceAsync(wallet, baseAsset, cancellationToken: TestContext.Current.CancellationToken)).Amount);

        static ExecutionAccountingCommand ExecutionCommand(
            string id, long tradeId, OrderSide side, decimal quantity, decimal price, decimal commission,
            Symbol symbol, Asset baseAsset, Asset quoteAsset, AccountId wallet, AccountId fees) => new(
                id, tradeId, tradeId, symbol, baseAsset, quoteAsset, side, new Quantity(quantity), new Price(price),
                new Money(commission, quoteAsset), wallet, fees, LedgerEntryId.New(), CorrelationId.New(),
                new IdempotencyKey("integration-" + id), DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task IdenticalReplayAcceptsPostgresNumericScaleForFillAndJournal()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("POSTGRES_CONNECTION_STRING must point to an isolated test database.");
        }

        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        await store.ApplyMigrationsAsync(TestContext.Current.CancellationToken);
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var baseAsset = new Asset("BAS" + suffix);
        var quote = new Asset("USDT");
        var command = new ExecutionAccountingCommand(
            "numeric-replay-" + suffix, Convert.ToInt64(suffix, 16), 1, new Symbol(baseAsset.Value + "USDT"),
            baseAsset, quote, OrderSide.Buy, new Quantity(0.1234567890123456789m), new Price(1.234567890123456789m),
            new Money(0m, quote), new AccountId("isolated-wallet-" + suffix), new AccountId("isolated-fees-" + suffix),
            LedgerEntryId.New(), CorrelationId.New(), IdempotencyKey.New(), DateTimeOffset.UtcNow);

        var initial = await store.RecordExecutionAsync(command, TestContext.Current.CancellationToken);
        var replay = await store.RecordExecutionAsync(command, TestContext.Current.CancellationToken);

        Assert.False(initial.IsDuplicate);
        Assert.True(replay.IsDuplicate);
        Assert.Equal(initial.LedgerEntry.Id, replay.LedgerEntry.Id);
    }

    [Fact]
    public async Task SellWithoutBasisDoesNotCommitExecutionOrJournal()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("POSTGRES_CONNECTION_STRING must point to an isolated test database.");
        }

        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        await store.ApplyMigrationsAsync(TestContext.Current.CancellationToken);
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var baseAsset = new Asset("BAS" + suffix);
        var quote = new Asset("USDT");
        var entryId = LedgerEntryId.New();
        var command = new ExecutionAccountingCommand(
            "missing-basis-" + suffix, Convert.ToInt64(suffix, 16), 1, new Symbol(baseAsset.Value + "USDT"),
            baseAsset, quote, OrderSide.Sell, new Quantity(1m), new Price(120m), new Money(0m, quote),
            new AccountId("isolated-wallet-" + suffix), new AccountId("isolated-fees-" + suffix),
            entryId, CorrelationId.New(), IdempotencyKey.New(), DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordExecutionAsync(command, TestContext.Current.CancellationToken));

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var query = new NpgsqlCommand(
            "SELECT (SELECT count(*) FROM trading.execution_fills WHERE execution_id = @execution_id) + (SELECT count(*) FROM trading.journal_entries WHERE entry_id = @entry_id);", connection);
        query.Parameters.AddWithValue("execution_id", command.ExecutionId);
        query.Parameters.AddWithValue("entry_id", entryId.Value);
        Assert.Equal(0L, await query.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PersistsNegativeQuoteCommissionAsAppendOnlyRebate()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        }

        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        await store.ApplyMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var baseAsset = new Asset("REB" + suffix);
        var quoteAsset = new Asset("USDT");
        var wallet = new AccountId("integration-rebate-wallet-" + suffix);
        var command = new ExecutionAccountingCommand(
            "rebate-" + suffix, Convert.ToInt64(suffix, 16), 71, new Symbol(baseAsset.Value + "USDT"), baseAsset, quoteAsset,
            OrderSide.Buy, new Quantity(1m), new Price(100m), new Money(-.1m, quoteAsset), wallet,
            new AccountId("integration-rebate-fees-" + suffix), LedgerEntryId.New(), CorrelationId.New(), IdempotencyKey.New(), DateTimeOffset.UtcNow);

        _ = await store.RecordExecutionAsync(command, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(-99.9m, (await store.GetBalanceAsync(wallet, quoteAsset, cancellationToken: TestContext.Current.CancellationToken)).Amount);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var query = new NpgsqlCommand("SELECT commission FROM trading.execution_fills WHERE execution_id = @execution_id;", connection);
        query.Parameters.AddWithValue("execution_id", command.ExecutionId);
        Assert.Equal(-.1m, await query.ExecuteScalarAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PersistsFeeExpectationAndDriftComparisonForAnExecution()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        }

        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        await store.ApplyMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var now = DateTimeOffset.UtcNow;
        var clientOrderId = new ClientOrderId("fee-" + suffix);
        var estimate = new ExecutionFeeEstimate(.001m, "fee-snapshot-" + suffix, "integration", now, now.AddMinutes(5));
        var submit = new SubmitOrderCommand(
            OrderId.New(), clientOrderId, new Symbol("FEE" + suffix + "USDT"), OrderSide.Buy, OrderType.Market,
            new Quantity(1m), new Price(100m), 100m, CorrelationId.New(), IdempotencyKey.New(), "integration-fee", now,
            estimate.Rate, estimate.SnapshotHash, estimate.Source, estimate.ObservedAtUtc, estimate.ValidUntilUtc,
            ExecutionAttempt: 3, ExecutionExpiresAtUtc: now.AddMinutes(1));
        var outbox = new OutboxRecord(
            Guid.NewGuid(), OrderExecutionPipeline.SubmitOrderMessageType, submit.CorrelationId, submit.IdempotencyKey,
            "integration-fee", submit, OutboxRecordStatus.Pending, now.AddDays(1));

        await store.EnqueueAsync(outbox, cancellationToken: TestContext.Current.CancellationToken);
        var persistedEstimate = await store.ReadExpectedExecutionFeeAsync(clientOrderId.Value, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(persistedEstimate);
        Assert.Equal(estimate.Rate, persistedEstimate.Rate);
        Assert.Equal(estimate.SnapshotHash, persistedEstimate.SnapshotHash);
        Assert.Equal(estimate.Source, persistedEstimate.Source);
        Assert.Equal(100m, persistedEstimate.ReferencePrice);
        Assert.Equal(1m, persistedEstimate.AuthorizedQuantity);
        var context = await store.ReadExecutionSubmissionContextAsync(clientOrderId.Value, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(context);
        Assert.Equal(submit.OrderId, context.ApplicationOrderId);
        Assert.Equal(submit.CorrelationId, context.CorrelationId);
        Assert.Equal(outbox.MessageId, context.OutboxMessageId);
        Assert.Equal(3, context.ExecutionAttempt);
        Assert.True(Math.Abs((submit.CreatedAtUtc - context.PlannedAtUtc).Ticks) < TimeSpan.TicksPerMicrosecond);
        Assert.NotNull(context.ExpiresAtUtc);
        Assert.True(Math.Abs((submit.ExecutionExpiresAtUtc!.Value - context.ExpiresAtUtc.Value).Ticks) < TimeSpan.TicksPerMicrosecond);

        var tradeId = Convert.ToInt64(suffix, 16);
        var accounting = new ExecutionAccountingCommand(
            "fee-trade-" + suffix, tradeId, tradeId,
            submit.Symbol, new Asset("FEE" + suffix), new Asset("USDT"), OrderSide.Buy, new Quantity(1m), new Price(100m),
            new Money(.002m, new Asset("BNB")), new AccountId("integration-fee-wallet-" + suffix),
            new AccountId("integration-fee-expense-" + suffix), LedgerEntryId.New(), CorrelationId.New(), IdempotencyKey.New(), now,
            clientOrderId.Value, persistedEstimate, 25m,
            new ExecutionFeeAssetValuation(new Asset("BNB"), new Asset("USDT"), 100m, now, "integration:bnb-valuation"),
            new Quantity(1m));
        var result = await store.RecordExecutionAsync(accounting, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.FeeComparison!.IsDrift);
        Assert.Equal("COMPARABLE", result.QualityComparison!.Status);
        Assert.Equal(1m, result.QualityComparison.FillRatio);
        Assert.Equal(0m, result.QualityComparison.ImplementationShortfallQuote);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var query = new NpgsqlCommand(
            "SELECT client_order_id, fee_comparison_status, fee_comparison_reason, commission_valuation_source, cumulative_fill_ratio, implementation_shortfall_quote, execution_quality_status FROM trading.execution_fills WHERE trade_id = @trade_id;",
            connection);
        query.Parameters.AddWithValue("trade_id", tradeId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(clientOrderId.Value, reader.GetString(0));
        Assert.Equal("DRIFT", reader.GetString(1));
        Assert.Equal("EXECUTION_FEE_DRIFT", reader.GetString(2));
        Assert.Equal("integration:bnb-valuation", reader.GetString(3));
        Assert.Equal(1m, reader.GetDecimal(4));
        Assert.Equal(0m, reader.GetDecimal(5));
        Assert.Equal("COMPARABLE", reader.GetString(6));

        var lifecycle = OrderLifecycleEvent.Create(
            clientOrderId.Value,
            new OrderTransition(
                OrderStatus.Submitted, OrderStatus.Acknowledged, new IdempotencyKey("ack-" + suffix), now.AddSeconds(1), "123"));
        Assert.True(await store.AppendOrderLifecycleEventAsync(lifecycle, cancellationToken: TestContext.Current.CancellationToken));

        var report = await store.ReadExecutionAttributionReportAsync(clientOrderId.Value, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(report);
        Assert.Equal(context, report.Context);
        Assert.Equal(0, report.OutboxAttempts);
        var lifecycleFact = Assert.Single(report.Lifecycle);
        Assert.Equal(OrderStatus.Submitted, lifecycleFact.From);
        Assert.Equal(OrderStatus.Acknowledged, lifecycleFact.To);
        Assert.Equal(lifecycle.EventId, lifecycleFact.EventId);
        var fill = Assert.Single(report.Fills);
        Assert.Equal(accounting.ExecutionId, fill.ExecutionId);
        Assert.Equal(1m, fill.CumulativeFillRatio);
        Assert.Equal("COMPARABLE", fill.QualityStatus);
        Assert.Equal("DRIFT", fill.FeeStatus);
    }

    [Fact]
    public async Task PersistsLedgerAndOutboxWithLeaseRetryAndIdempotency()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        }

        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString!));
        await store.ApplyMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken);
        await store.CheckConnectivityAsync(cancellationToken: TestContext.Current.CancellationToken);

        // The integration script deliberately preserves its database volume. Keep
        // this message earlier than other run-scoped outbox records so the claim
        // assertions cannot consume unrelated pending work from a previous test.
        var now = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var correlationId = CorrelationId.New();
        var ledgerId = LedgerEntryId.New();
        var ledgerKey = IdempotencyKey.New();
        var usdt = new Asset("USDT");
        var ledger = new LedgerEntry(
            ledgerId,
            correlationId,
            now,
            "integration-test-ledger",
            [
                new LedgerPosting(new AccountId("integration-cash"), usdt, 100m),
                new LedgerPosting(new AccountId("integration-clearing"), usdt, -100m),
            ],
            ledgerKey);

        Assert.Same(ledger, await store.AppendJournalEntryAsync(ledger, cancellationToken: TestContext.Current.CancellationToken));
        var replayed = await store.AppendJournalEntryAsync(ledger, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ledgerId, replayed.Id);
        Assert.Equal(100m, (await store.GetBalanceAsync(new AccountId("integration-cash"), usdt, cancellationToken: TestContext.Current.CancellationToken)).Amount);
        Assert.Contains("USDT", await store.GetAssetsAsync(new AccountId("integration-cash"), cancellationToken: TestContext.Current.CancellationToken));

        var orderId = OrderId.New();
        var commandKey = IdempotencyKey.New();
        var command = new SubmitOrderCommand(
            orderId,
            ClientOrderId.New(),
            new Symbol("BTCUSDT"),
            OrderSide.Buy,
            OrderType.Market,
            new Quantity(0.001m),
            new Price(50_000m),
            50m,
            correlationId,
            commandKey,
            "integration-test",
            now);
        var outbox = new OutboxRecord(
            Guid.NewGuid(),
            OrderExecutionPipeline.SubmitOrderMessageType,
            correlationId,
            commandKey,
            "integration-test",
            command,
            OutboxRecordStatus.Pending,
            now);

        await store.EnqueueAsync(outbox, cancellationToken: TestContext.Current.CancellationToken);
        await store.EnqueueAsync(outbox, cancellationToken: TestContext.Current.CancellationToken);

        var firstOwner = "integration-owner-a";
        var firstLease = await store.ClaimOutboxAsync(firstOwner, now, TimeSpan.FromSeconds(5), 1, cancellationToken: TestContext.Current.CancellationToken);
        var leased = Assert.Single(firstLease);
        Assert.Equal(1, leased.Attempts);
        Assert.Equal(firstOwner, leased.LeaseOwner);

        Assert.True(await store.FailOutboxAsync(
            leased.MessageId,
            firstOwner,
            "simulated-transient-failure",
            now.AddSeconds(-1), cancellationToken: TestContext.Current.CancellationToken));

        var secondOwner = "integration-owner-b";
        var retryLease = await store.ClaimOutboxAsync(secondOwner, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), 1, cancellationToken: TestContext.Current.CancellationToken);
        var retried = Assert.Single(retryLease);
        Assert.Equal(leased.MessageId, retried.MessageId);
        Assert.Equal(2, retried.Attempts);
        Assert.Equal(secondOwner, retried.LeaseOwner);
        Assert.True(await store.MarkOutboxProcessedAsync(retried.MessageId, secondOwner, DateTimeOffset.UtcNow, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await store.ClaimOutboxAsync("integration-owner-c", now, TimeSpan.FromSeconds(5), 1, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExpiredLeaseIsRecoverableAfterStoreRestartWithoutDuplicateDelivery()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        }

        // The integration script deliberately preserves its volume between
        // runs. Use a deterministic historical lease window so unrelated
        // prior outbox messages cannot be claimed by this recovery proof.
        var now = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var correlationId = CorrelationId.New();
        var commandKey = IdempotencyKey.New();
        var command = new SubmitOrderCommand(
            OrderId.New(),
            ClientOrderId.New(),
            new Symbol("BTCUSDT"),
            OrderSide.Buy,
            OrderType.Market,
            new Quantity(0.001m),
            new Price(50_000m),
            50m,
            correlationId,
            commandKey,
            "integration-crash-recovery",
            now);
        var outbox = new OutboxRecord(
            Guid.NewGuid(),
            OrderExecutionPipeline.SubmitOrderMessageType,
            correlationId,
            commandKey,
            "integration-crash-recovery",
            command,
            OutboxRecordStatus.Pending,
            now);

        await using (var firstProcess = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString)))
        {
            await firstProcess.ApplyMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken);
            await firstProcess.EnqueueAsync(outbox, cancellationToken: TestContext.Current.CancellationToken);

            var claimedBeforeCrash = await firstProcess.ClaimOutboxAsync(
                "integration-crashed-worker",
                now,
                TimeSpan.FromSeconds(1),
                1, cancellationToken: TestContext.Current.CancellationToken);
            var leasedBeforeCrash = Assert.Single(claimedBeforeCrash);
            Assert.Equal(1, leasedBeforeCrash.Attempts);
        }

        // A new store instance represents a worker restart. The old lease is not
        // acknowledged, so recovery must wait for its bounded expiry.
        await using var restartedProcess = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        var recovered = await restartedProcess.ClaimOutboxAsync(
            "integration-restarted-worker",
            now.AddSeconds(2),
            TimeSpan.FromSeconds(5),
            1, cancellationToken: TestContext.Current.CancellationToken);
        var recoveredMessage = Assert.Single(recovered);
        Assert.Equal(outbox.MessageId, recoveredMessage.MessageId);
        Assert.Equal(2, recoveredMessage.Attempts);
        Assert.Equal("integration-restarted-worker", recoveredMessage.LeaseOwner);

        Assert.False(await restartedProcess.MarkOutboxProcessedAsync(
            recoveredMessage.MessageId,
            "integration-crashed-worker",
            now.AddSeconds(2), cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await restartedProcess.MarkOutboxProcessedAsync(
            recoveredMessage.MessageId,
            "integration-restarted-worker",
            now.AddSeconds(2), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await restartedProcess.ClaimOutboxAsync(
            "integration-verification-worker",
            now.AddSeconds(2),
            TimeSpan.FromSeconds(5),
            1, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PersistsImmutableKnownTradeIdsForReconciliation()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        }

        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString!));
        await store.ApplyMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken);

        await store.RecordKnownTradeAsync(9_000_001, "BTCUSDT", cancellationToken: TestContext.Current.CancellationToken);
        await store.RecordKnownTradeAsync(9_000_001, "BTCUSDT", cancellationToken: TestContext.Current.CancellationToken);

    await store.RecordKnownTradeAsync(9_000_001, "ETHUSDT", cancellationToken: TestContext.Current.CancellationToken);
    var identities = await store.GetKnownTradeIdsAsync(cancellationToken: TestContext.Current.CancellationToken);
    Assert.Contains(new KnownReconciliationTrade("BTCUSDT", 9_000_001), identities);
    Assert.Contains(new KnownReconciliationTrade("ETHUSDT", 9_000_001), identities);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.RecordKnownTradeAsync(-1, "BTCUSDT", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AtomicallyPersistsJournalLedgerAndKnownTradeEvidence()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        }

        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString!));
        await store.ApplyMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow;
        var entry = new LedgerEntry(
            LedgerEntryId.New(),
            CorrelationId.New(),
            now,
            "atomic-trade-reconciliation",
            [
                new LedgerPosting(new AccountId("atomic-cash"), new Asset("USDT"), 7m),
                new LedgerPosting(new AccountId("atomic-clearing"), new Asset("USDT"), -7m),
            ],
            IdempotencyKey.New());

        await store.AppendJournalEntryWithKnownTradeAsync(entry, 9_000_002, "BTCUSDT", cancellationToken: TestContext.Current.CancellationToken);
        await store.AppendJournalEntryWithKnownTradeAsync(entry, 9_000_002, "BTCUSDT", cancellationToken: TestContext.Current.CancellationToken);

    Assert.Contains(new KnownReconciliationTrade("BTCUSDT", 9_000_002), await store.GetKnownTradeIdsAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(7m, (await store.GetBalanceAsync(new AccountId("atomic-cash"), new Asset("USDT"), cancellationToken: TestContext.Current.CancellationToken)).Amount);
    await store.AppendJournalEntryWithKnownTradeAsync(entry, 9_000_002, "ETHUSDT", cancellationToken: TestContext.Current.CancellationToken);
    Assert.Contains(new KnownReconciliationTrade("ETHUSDT", 9_000_002), await store.GetKnownTradeIdsAsync(cancellationToken: TestContext.Current.CancellationToken));
}

    [Fact]
    public async Task PersistsApprovalGatedRiskPolicyRegistryAndSupersedesPreviousActiveVersion()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        }

        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString!));
        await store.ApplyMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow.AddSeconds(-2);
        var expires = now.AddHours(2);
        var first = RiskPolicyDocument.CreateDraft(
            new RiskPolicy("registry-v1-" + Guid.NewGuid().ToString("N"), 100m, 250m, 25m, 50m, TimeSpan.FromSeconds(5)),
            now, expires, "policy-author");

        await store.SaveRiskPolicyAsync(first, cancellationToken: TestContext.Current.CancellationToken);
        await store.SaveRiskPolicyAsync(first, cancellationToken: TestContext.Current.CancellationToken);
        var conflicting = RiskPolicyDocument.CreateDraft(
            new RiskPolicy(first.Policy.Version, 101m, 250m, 25m, 50m, TimeSpan.FromSeconds(5)),
            now, expires, "policy-author");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveRiskPolicyAsync(conflicting, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApproveRiskPolicyAsync(
            first.Policy.Version, "approval-self", "policy-author", now.AddMinutes(1), cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ActivateRiskPolicyAsync(first.Policy.Version, now.AddMinutes(1), cancellationToken: TestContext.Current.CancellationToken));

        var approvedFirst = await store.ApproveRiskPolicyAsync(
            first.Policy.Version, "approval-v1", "policy-reviewer", now.AddMinutes(1), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(RiskPolicyLifecycle.Approved, approvedFirst.Lifecycle);
        var activeFirst = await store.ActivateRiskPolicyAsync(first.Policy.Version, now.AddMinutes(2), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(RiskPolicyLifecycle.Active, activeFirst.Lifecycle);
        Assert.Equal(first.Policy.Version, (await store.GetActiveRiskPolicyAsync(now.AddMinutes(3), cancellationToken: TestContext.Current.CancellationToken))?.Policy.Version);

        var second = RiskPolicyDocument.CreateDraft(
            new RiskPolicy("registry-v2-" + Guid.NewGuid().ToString("N"), 90m, 200m, 20m, 40m, TimeSpan.FromSeconds(5)),
            now, expires, "policy-author");
        await store.SaveRiskPolicyAsync(second, cancellationToken: TestContext.Current.CancellationToken);
        await store.ApproveRiskPolicyAsync(second.Policy.Version, "approval-v2", "policy-reviewer", now.AddMinutes(4), cancellationToken: TestContext.Current.CancellationToken);
        await store.ActivateRiskPolicyAsync(second.Policy.Version, now.AddMinutes(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(second.Policy.Version, (await store.GetActiveRiskPolicyAsync(now.AddMinutes(6), cancellationToken: TestContext.Current.CancellationToken))?.Policy.Version);
    }

    [Fact]
    public async Task PersistsAccountStreamEventsIdempotentlyAndReadsThemByTimeWindow()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        }

        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString!));
        await store.ApplyMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var receivedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var record = new AccountStreamEventRecord(
            Guid.NewGuid(), "executionReport", "execution-" + Guid.NewGuid().ToString("N"),
            1_720_000_000_123, receivedAt,
            "{\"e\":\"executionReport\",\"s\":\"BTCUSDT\",\"x\":\"TRADE\"}", "BTCUSDT",
            Guid.NewGuid(), 1, 7);

        Assert.True(await store.AppendAccountStreamEventAsync(record, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await store.AppendAccountStreamEventAsync(record, cancellationToken: TestContext.Current.CancellationToken));
        var events = await store.ReadAccountStreamEventsAsync(receivedAt.AddSeconds(-1), cancellationToken: TestContext.Current.CancellationToken);
        var persisted = Assert.Single(events, item => item.EventId == record.EventId);
        Assert.Equal(record.PayloadSha256, persisted.PayloadSha256);
        Assert.Equal(record.Symbol, persisted.Symbol);
        Assert.Equal(record.SessionId, persisted.SessionId);
        Assert.Equal(record.SessionGeneration, persisted.SessionGeneration);
        Assert.Equal(record.SubscriptionId, persisted.SubscriptionId);

        var conflicting = new AccountStreamEventRecord(
            record.EventId, record.EventType, record.DedupeKey, record.EventTimeMilliseconds,
            record.ReceivedAtUtc, "{\"e\":\"executionReport\",\"s\":\"ETHUSDT\"}", "ETHUSDT",
            record.SessionId, record.SessionGeneration, record.SubscriptionId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAccountStreamEventAsync(conflicting, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReplaysOpenOrderProjectionAcrossStoreRestartAndRetainsRemovalState()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        }

        var clientOrderId = "r-" + Guid.NewGuid().ToString("N");
        var createdAtUtc = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        var initial = new LocalOpenOrderRecord(
            "BTCUSDT",
            7_000_000_001,
            clientOrderId,
            "NEW",
            createdAtUtc,
            OriginalQuantity: 0.01m,
            CumulativeExecutedQuantity: 0m);
        var updated = initial with
        {
            Status = "PARTIALLY_FILLED",
            UpdatedAtUtc = createdAtUtc.AddSeconds(1),
            CumulativeExecutedQuantity = 0.005m,
        };

        await using (var firstProcess = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString)))
        {
            await firstProcess.ApplyMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken);
            await firstProcess.UpsertOpenOrderAsync(initial, cancellationToken: TestContext.Current.CancellationToken);
            await firstProcess.UpsertOpenOrderAsync(initial, cancellationToken: TestContext.Current.CancellationToken);
            await firstProcess.UpsertOpenOrderAsync(updated, cancellationToken: TestContext.Current.CancellationToken);
        }

        // A new store instance represents a worker restart. The projection is
        // replayable: only the latest active identity/status is visible.
        await using (var restartedProcess = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString)))
        {
            var replayed = Assert.Single(await restartedProcess.ReadOpenOrdersAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(updated, replayed);

            await restartedProcess.RemoveOpenOrderAsync(clientOrderId, cancellationToken: TestContext.Current.CancellationToken);
        }

        await using var verificationProcess = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        Assert.Empty(await verificationProcess.ReadOpenOrdersAsync(cancellationToken: TestContext.Current.CancellationToken));

        // A projection upsert cannot revive an identity after removal. REST
        // resolution uses its separate timestamp-conditional active-row path.
        await verificationProcess.UpsertOpenOrderAsync(updated, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(await verificationProcess.ReadOpenOrdersAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AccountStreamTerminalTombstoneRejectsDelayedActiveReportAcrossRestart()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        }

        var clientOrderId = "t-" + Guid.NewGuid().ToString("N");
        var observedAtUtc = DateTimeOffset.UtcNow;
        var initial = new LocalOpenOrderRecord(
            "BTCUSDT", 7_000_000_002, clientOrderId, "NEW", observedAtUtc, 0.01m, 0m);
        var terminal = initial with
        {
            Status = "CANCELED",
            UpdatedAtUtc = observedAtUtc.AddMilliseconds(2),
            CumulativeExecutedQuantity = 0.005m,
            ExchangeUpdatedAtUtc = observedAtUtc.AddMilliseconds(2),
        };

        await using (var firstProcess = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString)))
        {
            await firstProcess.ApplyMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken);
            await firstProcess.UpsertOpenOrderAsync(initial, cancellationToken: TestContext.Current.CancellationToken);
            await firstProcess.AppendOrderLifecycleEventAsync(OrderLifecycleEvent.Create(clientOrderId,
                new OrderTransition(OrderStatus.Submitted, OrderStatus.Acknowledged,
                    new IdempotencyKey("seed-" + clientOrderId), observedAtUtc, "7000000002")), TestContext.Current.CancellationToken);
            var partialFill = initial with
            {
                Status = "PARTIALLY_FILLED",
                UpdatedAtUtc = observedAtUtc.AddMilliseconds(1),
                CumulativeExecutedQuantity = 0.005m,
                ExchangeUpdatedAtUtc = observedAtUtc.AddMilliseconds(1),
            };
            Assert.True(await firstProcess.ApplyAccountStreamExecutionReportAsync(
                partialFill, isTerminal: false, cancellationToken: TestContext.Current.CancellationToken));
            var staleNew = partialFill with { Status = "NEW" };
            Assert.False(await firstProcess.ApplyAccountStreamExecutionReportAsync(
                staleNew, isTerminal: false, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(2, (await firstProcess.ReadOrderLifecycleEventsAsync(clientOrderId, TestContext.Current.CancellationToken)).Count);
            var laterRegressiveNew = staleNew with { ExchangeUpdatedAtUtc = observedAtUtc.AddMilliseconds(2) };
            Assert.False(await firstProcess.ApplyAccountStreamExecutionReportAsync(
                laterRegressiveNew, isTerminal: false, cancellationToken: TestContext.Current.CancellationToken));
            var newStatusWithHigherFill = staleNew with { CumulativeExecutedQuantity = 0.006m };
            Assert.False(await firstProcess.ApplyAccountStreamExecutionReportAsync(
                newStatusWithHigherFill, isTerminal: false, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal("PARTIALLY_FILLED", Assert.Single(await firstProcess.ReadOpenOrdersAsync(
                cancellationToken: TestContext.Current.CancellationToken)).Status);
            Assert.True(await firstProcess.ApplyAccountStreamExecutionReportAsync(
                terminal, isTerminal: true, cancellationToken: TestContext.Current.CancellationToken));
        }

        await using var restartedProcess = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        var delayedActive = initial with
        {
            Status = "PARTIALLY_FILLED",
            UpdatedAtUtc = observedAtUtc.AddMilliseconds(1),
            CumulativeExecutedQuantity = 0.005m,
            ExchangeUpdatedAtUtc = observedAtUtc.AddMilliseconds(1),
        };
        Assert.False(await restartedProcess.ApplyAccountStreamExecutionReportAsync(
            delayedActive, isTerminal: false, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await restartedProcess.ReadOpenOrdersAsync(cancellationToken: TestContext.Current.CancellationToken));

        // A REST snapshot must never reactivate an identity with a durable
        // terminal account-stream tombstone, even if its timestamp claims to be newer.
        var staleRestSnapshot = initial with
        {
            Status = "NEW",
            UpdatedAtUtc = observedAtUtc.AddMilliseconds(3),
            ExchangeUpdatedAtUtc = observedAtUtc.AddMilliseconds(1),
        };
        Assert.False(await restartedProcess.ApplyAuthoritativeOpenOrderSnapshotAsync(
            staleRestSnapshot, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await restartedProcess.ReadOpenOrdersAsync(cancellationToken: TestContext.Current.CancellationToken));

        // Reconciliation cannot undo a terminal event; conflicting external state
        // remains a SafeMode mismatch for an operator to investigate.
        var freshRestSnapshot = staleRestSnapshot with
        {
            UpdatedAtUtc = observedAtUtc.AddMilliseconds(4),
            ExchangeUpdatedAtUtc = observedAtUtc.AddMilliseconds(3),
        };
        Assert.False(await restartedProcess.ApplyAuthoritativeOpenOrderSnapshotAsync(
            freshRestSnapshot, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await restartedProcess.ReadOpenOrdersAsync(cancellationToken: TestContext.Current.CancellationToken));
        await restartedProcess.RemoveOpenOrderAsync(clientOrderId, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AccountStreamEqualTimestampLifecycleUsesDurableOrderNotRandomEventId()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) throw SkipException.ForSkip("Run scripts/test-postgres-integration.sh.");
        var token = TestContext.Current.CancellationToken;
        var id = "s-" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        await store.ApplyMigrationsAsync(token);
        var initial = new LocalOpenOrderRecord("BTCUSDT", 44, id, "NEW", now, 1m, 0m, now);
        await store.UpsertOpenOrderAsync(initial, token);
        var seed = OrderLifecycleEvent.Create(id, new OrderTransition(OrderStatus.Submitted, OrderStatus.Acknowledged,
            new IdempotencyKey("seed-" + id), now, "44")) with { EventId = Guid.Parse("ffffffff-ffff-ffff-" + Guid.NewGuid().ToString("D")[19..]) };
        await store.AppendOrderLifecycleEventAsync(seed, token);
        try
        {
            var partial = initial with { Status = "PARTIALLY_FILLED", CumulativeExecutedQuantity = 0.5m };
            Assert.True(await store.ApplyAccountStreamExecutionReportAsync(partial, false, token));
            Assert.True(await store.ApplyAccountStreamExecutionReportAsync(partial with { Status = "CANCELED" }, true, token));
            var events = await store.ReadOrderLifecycleEventsAsync(id, token);
            Assert.Equal(3, events.Count);
            Assert.Equal(OrderStatus.PartiallyFilled, Assert.Single(events, row => row.Transition.To == OrderStatus.Cancelled).Transition.From);
            Assert.Equal(OrderStatus.Acknowledged, events[0].Transition.To);
            Assert.Equal(OrderStatus.Cancelled, events[^1].Transition.To);
            Assert.False(await store.ApplyAccountStreamExecutionReportAsync(partial with { Status = "CANCELED" }, true, token));
            Assert.Equal(3, (await store.ReadOrderLifecycleEventsAsync(id, token)).Count);
        }
        finally { await store.RemoveOpenOrderAsync(id, CancellationToken.None); }
    }

    [Theory]
    [InlineData("append")]
    [InlineData("stream")]
    [InlineData("cancel")]
    public async Task LifecycleWritersAcquireSameOrderLockBeforeMutatingEvidence(string writer)
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) throw SkipException.ForSkip("Run scripts/test-postgres-integration.sh.");
        var token = TestContext.Current.CancellationToken;
        var id = "l-" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        await store.ApplyMigrationsAsync(token);
        var initial = new LocalOpenOrderRecord("BTCUSDT", 42, id, "PENDING_CANCEL", now, 1m, 0.5m, now);
        await store.UpsertOpenOrderAsync(initial, token);
        await store.AppendOrderLifecycleEventAsync(OrderLifecycleEvent.Create(id, new OrderTransition(
            OrderStatus.Submitted, OrderStatus.Acknowledged, new IdempotencyKey("seed-" + id), now, "42")), token);
        await using var gateConnection = new NpgsqlConnection(connectionString);
        await gateConnection.OpenAsync(token);
        await using var gate = await gateConnection.BeginTransactionAsync(token);
        await using var acquire = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@identity, 0));", gateConnection, gate);
        acquire.Parameters.AddWithValue("identity", "order-lifecycle:" + id);
        await acquire.ExecuteNonQueryAsync(token);

        var cancel = new CancelOrderCommand(OrderId.New(), new ClientOrderId(id), new Symbol("BTCUSDT"), OrderSide.Buy, OrderType.Limit,
            new Quantity(1m), new Quantity(0.5m), new Price(100m), CorrelationId.New(), new IdempotencyKey("cancel-" + id),
            0, now.AddMinutes(-2), now.AddMinutes(-1), "42");
        var outcome = CancelLifecyclePlanner.Resolve(cancel, CancelVenueStatus.Canceled, new Quantity(0.5m));
        Task operation = writer switch
        {
            "append" => store.AppendOrderLifecycleEventAsync(OrderLifecycleEvent.Create(id, new OrderTransition(
                OrderStatus.Acknowledged, OrderStatus.CancelPending, new IdempotencyKey("pending-" + id), now.AddSeconds(1), "42")), token),
            "stream" => store.ApplyAccountStreamExecutionReportAsync(initial with { Status = "CANCELED", ExchangeUpdatedAtUtc = now.AddSeconds(1) }, true, token),
            _ => store.ResolveTerminalCancellationAsync(new TerminalCancellationResolution(cancel, outcome, Guid.NewGuid(),
                OrderLifecycleEvent.Create(id, new OrderTransition(OrderStatus.CancelPending, OrderStatus.Cancelled,
                    new IdempotencyKey("terminal-" + id), now.AddSeconds(1), "42"))), token)
        };
        try
        {
            await using var waiting = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM pg_locks
                WHERE locktype = 'advisory' AND NOT granted AND objsubid = 1
                  AND classid::bigint = ((hashtextextended(@identity, 0) >> 32) & 4294967295)
                  AND objid::bigint = (hashtextextended(@identity, 0) & 4294967295));
                """, gateConnection, gate);
            waiting.Parameters.AddWithValue("identity", "order-lifecycle:" + id);
            var observedWaiting = false;
            for (var attempt = 0; attempt < 200 && !observedWaiting; attempt++)
            {
                observedWaiting = await waiting.ExecuteScalarAsync(token) is true;
                if (!observedWaiting) await Task.Delay(TimeSpan.FromMilliseconds(25), TimeProvider.System, token);
            }
            Assert.True(observedWaiting, "Writer did not wait on the shared order lock.");
            Assert.Single(await store.ReadOrderLifecycleEventsAsync(id, token));
            Assert.Equal("PENDING_CANCEL", Assert.Single(await store.ReadOpenOrdersAsync(token), row => row.ClientOrderId == id).Status);
        }
        finally
        {
            await gate.RollbackAsync(CancellationToken.None);
            await operation.WaitAsync(TimeSpan.FromSeconds(10), token);
            await store.RemoveOpenOrderAsync(id, CancellationToken.None);
        }
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "SQL uses only generated hex fixture names and integrity-checked embedded migration text.")]
    public async Task LifecycleSequenceUpgradeLocksBeforeCheckingConcurrentHistoricalInsert()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) throw SkipException.ForSkip("Run scripts/test-postgres-integration.sh.");
        var token = TestContext.Current.CancellationToken;
        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        await store.ApplyMigrationsAsync(token);
        var suffix = Guid.NewGuid().ToString("N");
        var table = "trading.lifecycle_fixture_" + suffix;
        var migration = Assert.Single(PersistenceMigrations.ReadAll(), script => script.Version == "036" && script.Direction == "up");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var create = new NpgsqlCommand($"CREATE TABLE {table} (client_order_id text, occurred_at timestamptz); INSERT INTO {table} VALUES ('fixture', '2026-10-30T00:00:00Z'); CREATE TEMP TABLE migration_versions_fixture (version text PRIMARY KEY);", connection);
        await create.ExecuteNonQueryAsync(token);
        await using var writer = new NpgsqlConnection(connectionString);
        await writer.OpenAsync(token);
        await using var writerTransaction = await writer.BeginTransactionAsync(token);
        await using var migrationTransaction = await connection.BeginTransactionAsync(token);
        var writerCommitted = false;
        try
        {
            await using var insert = new NpgsqlCommand($"INSERT INTO {table} VALUES ('fixture', '2026-10-30T00:00:00Z');", writer, writerTransaction);
            await insert.ExecuteNonQueryAsync(token);
            var sql = migration.Sql.Replace("trading.order_lifecycle_events", table, StringComparison.Ordinal)
                .Replace("trading.schema_migrations", "pg_temp.migration_versions_fixture", StringComparison.Ordinal)
                .Replace("order_lifecycle_events_sequence_idx", "fixture_sequence_" + suffix, StringComparison.Ordinal)
                .Replace("order_lifecycle_events_client_sequence_idx", "fixture_client_" + suffix, StringComparison.Ordinal);
            await using var upgrade = new NpgsqlCommand(sql, connection, migrationTransaction);
            var upgrading = upgrade.ExecuteNonQueryAsync(token);
            await using var waiting = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_locks WHERE relation = to_regclass(@table) AND mode = 'AccessExclusiveLock' AND NOT granted);", writer, writerTransaction);
            waiting.Parameters.AddWithValue("table", table);
            var observedWaiting = false;
            for (var attempt = 0; attempt < 200 && !observedWaiting; attempt++)
            {
                observedWaiting = await waiting.ExecuteScalarAsync(token) is true;
                if (!observedWaiting) await Task.Delay(TimeSpan.FromMilliseconds(25), TimeProvider.System, token);
            }
            Assert.True(observedWaiting, "Upgrade did not wait for the concurrent writer.");
            await writerTransaction.CommitAsync(token);
            writerCommitted = true;
            var error = await Assert.ThrowsAsync<PostgresException>(() => upgrading);
            Assert.Contains("HISTORICAL_TIMESTAMP_TIES_REQUIRE_RECONCILIATION", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (!writerCommitted) await writerTransaction.RollbackAsync(CancellationToken.None);
            await migrationTransaction.RollbackAsync(CancellationToken.None);
            await using var cleanup = new NpgsqlCommand($"DROP TABLE {table};", connection);
            await cleanup.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only integrity-checked embedded migration SQL is executed in an isolated test transaction.")]
    public async Task LifecycleSequenceUpgradeRefusesHistoricalTimestampAmbiguity(bool tiedTimestamps)
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) throw SkipException.ForSkip("Run scripts/test-postgres-integration.sh.");
        var token = TestContext.Current.CancellationToken;
        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        await store.ApplyMigrationsAsync(token);
        var migration = Assert.Single(PersistenceMigrations.ReadAll(), script => script.Version == "036" && script.Direction == "up");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        try
        {
            // Target temporary fixtures with the same migration SQL, without
            // touching shared financial evidence or historical migrations.
            await using var fixture = new NpgsqlCommand("CREATE TEMP TABLE lifecycle_fixture (client_order_id text, occurred_at timestamptz); CREATE TEMP TABLE migration_versions_fixture (version text PRIMARY KEY);", connection, transaction);
            await fixture.ExecuteNonQueryAsync(token);
            await using var seed = new NpgsqlCommand("""
                INSERT INTO lifecycle_fixture (client_order_id, occurred_at)
                VALUES (@identity, @time), (@identity, @second_time);
                """, connection, transaction);
            var identity = "m-" + Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow;
            seed.Parameters.AddWithValue("identity", identity);
            seed.Parameters.AddWithValue("time", now);
            seed.Parameters.AddWithValue("second_time", tiedTimestamps ? now : now.AddSeconds(1));
            await seed.ExecuteNonQueryAsync(token);
            var fixtureSql = migration.Sql.Replace("trading.order_lifecycle_events", "pg_temp.lifecycle_fixture", StringComparison.Ordinal)
                .Replace("trading.schema_migrations", "pg_temp.migration_versions_fixture", StringComparison.Ordinal);
            await using var up = new NpgsqlCommand(fixtureSql, connection, transaction);
            if (tiedTimestamps)
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => up.ExecuteNonQueryAsync(token));
                Assert.Contains("HISTORICAL_TIMESTAMP_TIES_REQUIRE_RECONCILIATION", error.Message, StringComparison.Ordinal);
            }
            else
            {
                await up.ExecuteNonQueryAsync(token);
            }
        }
        finally { await transaction.RollbackAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task AccountStreamClockConflictCannotReleaseReservationOrOmitTerminalEvidence(bool lockedUnknown, bool clockConflict)
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) throw SkipException.ForSkip("Run scripts/test-postgres-integration.sh.");
        var token = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N");
        var id = "c-" + suffix;
        var now = DateTimeOffset.UtcNow;
        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        await store.ApplyMigrationsAsync(token);
        var initial = new LocalOpenOrderRecord("BTCUSDT", 43, id, "NEW", now, 1m, 0m, now);
        await store.UpsertOpenOrderAsync(initial, token);
        await store.AppendOrderLifecycleEventAsync(OrderLifecycleEvent.Create(id,
            new OrderTransition(OrderStatus.Submitted, OrderStatus.Acknowledged,
                new IdempotencyKey("seed-" + suffix), clockConflict ? now.AddSeconds(2) : now, "43")), token);
        Assert.True((await store.TryReserveCapitalAsync(new CapitalReservationRequest(
            "res-" + suffix, "account-" + suffix, "BTCUSDT", id, "USDT", 100m, 100m, 0m, 0m,
            "test-worker", 1L, now), token)).Succeeded);
        if (lockedUnknown)
            await store.TransitionReservationAsync(id, CapitalReservationStatus.LockedUnknown, 0m, "test-worker", 1L,
                now.AddSeconds(1), "AMBIGUOUS_SUBMISSION", token);
        try
        {
            var terminal = initial with { Status = "CANCELED", UpdatedAtUtc = now.AddSeconds(1), ExchangeUpdatedAtUtc = now.AddSeconds(1) };
            if (clockConflict)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApplyAccountStreamExecutionReportAsync(terminal, true, token));
                Assert.Contains("LIFECYCLE_CLOCK_CONFLICT", error.Message, StringComparison.Ordinal);
                Assert.Equal("NEW", Assert.Single(await store.ReadOpenOrdersAsync(token), row => row.ClientOrderId == id).Status);
                Assert.Single(await store.ReadOrderLifecycleEventsAsync(id, token));
            }
            else
            {
                Assert.True(await store.ApplyAccountStreamExecutionReportAsync(terminal, true, token));
                Assert.DoesNotContain(await store.ReadOpenOrdersAsync(token), row => row.ClientOrderId == id);
                Assert.Equal(2, (await store.ReadOrderLifecycleEventsAsync(id, token)).Count);
            }

            var reservations = await store.ReadActiveReservationsAsync("account-" + suffix, token);
            if (lockedUnknown || clockConflict)
                Assert.Equal(lockedUnknown ? CapitalReservationStatus.LockedUnknown : CapitalReservationStatus.Reserved, Assert.Single(reservations).Status);
            else
                Assert.Empty(reservations);
        }
        finally
        {
            await store.RemoveOpenOrderAsync(id, CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "DDL identifiers and target literal are generated hex IDs; table and operation are fixed test constants.")]
    public async Task AccountStreamProjectionLifecycleAndReservationRollBackTogether(bool failReservation)
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) throw SkipException.ForSkip("Run scripts/test-postgres-integration.sh.");
        var token = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N");
        var clientOrderId = "a-" + suffix;
        var now = DateTimeOffset.UtcNow;
        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        await store.ApplyMigrationsAsync(token);
        var initial = new LocalOpenOrderRecord("BTCUSDT", 42, clientOrderId, "NEW", now, 1m, 0m, now);
        await store.UpsertOpenOrderAsync(initial, token);
        await store.AppendOrderLifecycleEventAsync(OrderLifecycleEvent.Create(clientOrderId,
            new OrderTransition(OrderStatus.Submitted, OrderStatus.Acknowledged,
                new IdempotencyKey("seed-" + suffix), now, "42")), token);
        var reservation = await store.TryReserveCapitalAsync(new CapitalReservationRequest(
            "res-" + suffix, "account-" + suffix, "BTCUSDT", clientOrderId, "USDT",
            100m, 100m, 0m, 0m, "test-worker", 1L, now), token);
        Assert.True(reservation.Succeeded);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        var table = failReservation ? "capital_reservations" : "order_lifecycle_events";
        var operation = failReservation ? "UPDATE" : "INSERT";
        // All interpolated identifiers and literals are generated test-only hex IDs.
        await using var installFailure = new NpgsqlCommand($"""
            CREATE FUNCTION trading.fail_{suffix}() RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN
                IF NEW.client_order_id = '{clientOrderId}' THEN RAISE EXCEPTION 'synthetic stream write failure'; END IF;
                RETURN NEW;
            END $body$;
            CREATE TRIGGER fail_{suffix} BEFORE {operation} ON trading.{table}
            FOR EACH ROW EXECUTE FUNCTION trading.fail_{suffix}();
            """, connection);
        await installFailure.ExecuteNonQueryAsync(token);
        try
        {
            var report = initial with
            {
                Status = failReservation ? "CANCELED" : "PARTIALLY_FILLED",
                UpdatedAtUtc = now.AddMilliseconds(1), ExchangeUpdatedAtUtc = now.AddMilliseconds(1),
                CumulativeExecutedQuantity = 0.5m
            };
            await Assert.ThrowsAsync<PostgresException>(() => store.ApplyAccountStreamExecutionReportAsync(report, failReservation, token));
            var active = Assert.Single(await store.ReadOpenOrdersAsync(token), order => order.ClientOrderId == clientOrderId);
            Assert.Equal("NEW", active.Status);
            Assert.Equal(0m, active.CumulativeExecutedQuantity);
            Assert.Single(await store.ReadOrderLifecycleEventsAsync(clientOrderId, token));
            Assert.Equal(CapitalReservationStatus.Reserved,
                Assert.Single(await store.ReadActiveReservationsAsync("account-" + suffix, token)).Status);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($"DROP TRIGGER fail_{suffix} ON trading.{table}; DROP FUNCTION trading.fail_{suffix}();", connection);
            await cleanup.ExecuteNonQueryAsync(CancellationToken.None);
            await store.RemoveOpenOrderAsync(clientOrderId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task ReconciliationEventsAreDurableIdempotentAndRejectIdentityCollision()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        }

        var eventId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        // timestamptz stores microseconds; the immutable envelope retains all
        // original .NET ticks. Replay must compare the indexed timestamp using
        // database precision without weakening the original payload digest.
        var occurredAtUtc = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero).AddTicks(7);
        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = (short)1,
            eventType = "BalanceReconciled",
            eventId,
            correlationId,
            occurredAtUtc,
            data = new { asset = "USDT", delta = 0m },
        });
        var eventRecord = new ReconciliationEventRecord(
            eventId, correlationId, occurredAtUtc, "BalanceReconciled", 1,
            payload, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))));
        await using (var firstProcess = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString)))
        {
            await firstProcess.ApplyMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(await firstProcess.AppendReconciliationEventAsync(eventRecord, cancellationToken: TestContext.Current.CancellationToken));
        }

        await using var restartedProcess = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        Assert.False(await restartedProcess.AppendReconciliationEventAsync(eventRecord, cancellationToken: TestContext.Current.CancellationToken));

        var conflictingPayload = JsonSerializer.Serialize(new
        {
            schemaVersion = (short)1,
            eventType = "BalanceReconciled",
            eventId,
            correlationId,
            occurredAtUtc,
            data = new { asset = "USDT", delta = 1m },
        });
        var conflictingRecord = eventRecord with
        {
            PayloadJson = conflictingPayload,
            PayloadSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(conflictingPayload))),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restartedProcess.AppendReconciliationEventAsync(conflictingRecord, cancellationToken: TestContext.Current.CancellationToken));

        var changedTimestamp = occurredAtUtc.AddTicks(1);
        var changedTimestampPayload = JsonSerializer.Serialize(new
        {
            schemaVersion = (short)1,
            eventType = "BalanceReconciled",
            eventId,
            correlationId,
            occurredAtUtc = changedTimestamp,
            data = new { asset = "USDT", delta = 0m },
        });
        var timestampCollision = eventRecord with
        {
            OccurredAtUtc = changedTimestamp,
            PayloadJson = changedTimestampPayload,
            PayloadSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(changedTimestampPayload))),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restartedProcess.AppendReconciliationEventAsync(timestampCollision, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PersistsOperationalControlAuditIdempotentlyAndRejectsIdentityCollision()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        }

        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString!));
        await store.ApplyMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var occurredAtUtc = DateTimeOffset.UtcNow;
        var auditEvent = new OperationalControlAuditEvent(
            Guid.NewGuid(),
            OperationalMode.Normal,
            OperationalMode.SafeMode,
            "integration-test",
            "integration audit persistence",
            IdempotencyKey.New(),
            occurredAtUtc,
            null);

        Assert.True(await store.AppendOperationalControlAuditEventAsync(auditEvent, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await store.AppendOperationalControlAuditEventAsync(auditEvent, cancellationToken: TestContext.Current.CancellationToken));

        var persisted = Assert.Single(
            await store.ReadOperationalControlAuditEventsAsync(occurredAtUtc.AddSeconds(-1), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(auditEvent, persisted);
        Assert.Equal(auditEvent.PayloadSha256, persisted.PayloadSha256);

        var conflictingIdentity = auditEvent with { EventId = Guid.NewGuid() };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AppendOperationalControlAuditEventAsync(conflictingIdentity, cancellationToken: TestContext.Current.CancellationToken));

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var delete = new NpgsqlCommand(
            "DELETE FROM trading.operational_control_audit_events WHERE event_id = @event_id;",
            connection);
        delete.Parameters.AddWithValue("event_id", auditEvent.EventId);
        await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PersistsExperimentGovernanceIdempotentlyAndKeepsHoldoutEvidenceAppendOnly()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        }

        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString!));
        await store.ApplyMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var registration = NewExperimentRegistration();
        Assert.True(await store.AppendExperimentPreregistrationAsync(registration, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await store.AppendExperimentPreregistrationAsync(registration, cancellationToken: TestContext.Current.CancellationToken));

        var trial = new ExperimentTrial(
            1, "baseline", "{\"ema_fast\":20}", Hash("trial-result"),
            ExperimentDecision.Rejected, registration.PreregisteredAtUtc, "integration", new IdempotencyKey("trial-1"));
        Assert.True(await store.AppendExperimentTrialAsync(registration.ExperimentId, trial, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await store.AppendExperimentTrialAsync(registration.ExperimentId, trial, cancellationToken: TestContext.Current.CancellationToken));

        var registry = new InMemoryExperimentRegistry();
        registry.Register(registration);
        var access = registry.AccessHoldout(registration.ExperimentId,
            new HoldoutAccessRequest("reviewer", "locked evaluation", registration.PreregisteredAtUtc,
                new IdempotencyKey("holdout-1"), true, "approval-integration"));
        Assert.True(await store.AppendHoldoutAccessAuditAsync(access.Audit, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await store.AppendHoldoutAccessAuditAsync(access.Audit, cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(await store.AppendHoldoutContaminationAsync(
            registration.ExperimentId, "reviewer", "manual contamination drill", "holdout-v2",
            registration.PreregisteredAtUtc.AddMinutes(1), new IdempotencyKey("contamination-1"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await store.AppendHoldoutContaminationAsync(
            registration.ExperimentId, "reviewer", "manual contamination drill", "holdout-v2",
            registration.PreregisteredAtUtc.AddMinutes(1), new IdempotencyKey("contamination-1"), cancellationToken: TestContext.Current.CancellationToken));

        await using var restartedStore = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        var snapshot = await restartedStore.ReadExperimentGovernanceSnapshotAsync(registration.ExperimentId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(snapshot);
        Assert.Equal(registration.PreregistrationSha256, snapshot!.Preregistration.PreregistrationSha256);
        Assert.Single(snapshot.Trials);
        Assert.Equal(1, snapshot.HoldoutAccessCount);
        Assert.True(snapshot.HoldoutContaminated);
        Assert.Equal("holdout-v2", snapshot.ReplacementHoldoutVersion);
        Assert.Equal("manual contamination drill", snapshot.ContaminationReason);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var delete = new NpgsqlCommand(
            "DELETE FROM trading.experiment_trials WHERE experiment_id = @experiment_id;", connection);
        delete.Parameters.AddWithValue("experiment_id", registration.ExperimentId.Value);
        await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReconstructedSnapshotProjectsIntoGovernedEvidenceBundle()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        }

        var dataset = SyntheticMarketDataset.CreateBtcUsdtV1();
        var parameters = TrendFollowingParameters.BtcUsdtBaseline;
        var evaluation = WalkForwardEvaluator.Evaluate(
            dataset.Metadata,
            dataset.Bars,
            parameters,
            BacktestEvaluationOptions.DailyCrypto(),
            WalkForwardPlan.Create(dataset.Bars.Count, 60, 20, 20).Take(1).ToArray());
        var preregistration = new ExperimentPreregistration(
            ExperimentId.New(),
            "integration-governed-evidence",
            "baseline produces positive net return after costs",
            "baseline-only",
            ["baseline"],
            1,
            "net_return",
            "reject if net return is not positive",
            "NO_TRADE/cash and buy-and-hold",
            new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero),
            "integration",
            IdempotencyKey.New(),
            parameters.Version,
            "integration-commit",
            dataset.Metadata.Version,
            dataset.Metadata.ContentHash,
            "holdout-v1",
            "metrics-v1",
            "integration-environment",
            Hash("integration-dependencies"),
            17,
            dataset.Bars[0].CloseTimeUtc,
            dataset.Bars[^1].CloseTimeUtc.AddDays(1));

        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        await store.ApplyMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(await store.AppendExperimentPreregistrationAsync(preregistration, cancellationToken: TestContext.Current.CancellationToken));
        var trial = new ExperimentTrial(
            1,
            "baseline",
            "baseline-v1",
            Hash("integration-trial"),
            ExperimentDecision.Rejected,
            preregistration.PreregisteredAtUtc,
            "integration",
            IdempotencyKey.New());
        Assert.True(await store.AppendExperimentTrialAsync(preregistration.ExperimentId, trial, cancellationToken: TestContext.Current.CancellationToken));

        await using var restarted = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        var snapshot = await restarted.ReadExperimentGovernanceSnapshotAsync(preregistration.ExperimentId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(snapshot);
        using var output = new TemporaryDirectory();
        var input = ExperimentEvidenceWriter.FromGovernanceState(
            snapshot!.Preregistration,
            snapshot.Trials.Count,
            snapshot.HoldoutAccessCount,
            snapshot.HoldoutContaminated,
            evaluation,
            "Rejected",
            new Dictionary<string, string> { ["dataset"] = dataset.Metadata.ContentHash },
            ["AC-POSTGRES-GOVERNED-EVIDENCE-01"]);
        var bundle = ExperimentEvidenceWriter.Write(output.Path, input, DateTimeOffset.UtcNow);

        Assert.True(File.Exists(bundle.ArtifactPath));
        Assert.True(File.Exists(bundle.ManifestPath));
        Assert.Contains("\"promotion_authorized\":false", bundle.ManifestJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapitalReservationEnforcesAtomicCeilingFencingAndStickyLockedUnknownAcrossPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        }

        var accountId = "spot-res-" + Guid.NewGuid().ToString("N")[..12];
        var now = DateTimeOffset.UtcNow;

        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        await store.ApplyMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken);

        var first = await store.TryReserveCapitalAsync(
            new CapitalReservationRequest(
                reservationId: $"res-1-{accountId}",
                accountId: accountId,
                symbol: "BTCUSDT",
                clientOrderId: $"ord-1-{accountId}",
                asset: "USDT",
                requestedAmount: 12.00m,
                effectiveBudgetCeilingAmount: 20.00m,
                openPositionsCommittedAmount: 0m,
                realizedLossesAmount: 0m,
                workerInstanceId: "worker-primary",
                fencingToken: 10L,
                occurredAtUtc: now),
            TestContext.Current.CancellationToken);
        Assert.True(first.Succeeded);
        Assert.Equal(12.00m, first.TotalActiveReservedAmount);

        var overCeiling = await store.TryReserveCapitalAsync(
            new CapitalReservationRequest(
                reservationId: $"res-2-{accountId}",
                accountId: accountId,
                symbol: "BTCUSDT",
                clientOrderId: $"ord-2-{accountId}",
                asset: "USDT",
                requestedAmount: 9.00m,
                effectiveBudgetCeilingAmount: 20.00m,
                openPositionsCommittedAmount: 0m,
                realizedLossesAmount: 0m,
                workerInstanceId: "worker-primary",
                fencingToken: 10L,
                occurredAtUtc: now),
            TestContext.Current.CancellationToken);
        Assert.False(overCeiling.Succeeded);
        Assert.Equal("BUDGET_CEILING_EXCEEDED", overCeiling.ReasonCode);

        var staleFence = await store.TryReserveCapitalAsync(
            new CapitalReservationRequest(
                reservationId: $"res-stale-{accountId}",
                accountId: accountId,
                symbol: "BTCUSDT",
                clientOrderId: $"ord-stale-{accountId}",
                asset: "USDT",
                requestedAmount: 1.00m,
                effectiveBudgetCeilingAmount: 20.00m,
                openPositionsCommittedAmount: 0m,
                realizedLossesAmount: 0m,
                workerInstanceId: "worker-stale",
                fencingToken: 9L,
                occurredAtUtc: now),
            TestContext.Current.CancellationToken);
        Assert.False(staleFence.Succeeded);
        Assert.Equal("STALE_WORKER_FENCING_TOKEN", staleFence.ReasonCode);

        var lockedUnknown = await store.TransitionReservationAsync(
            $"ord-1-{accountId}",
            CapitalReservationStatus.LockedUnknown,
            0m,
            "worker-primary",
            10L,
            now.AddSeconds(1),
            "EXCHANGE_SUBMIT_TIMEOUT",
            TestContext.Current.CancellationToken);
        Assert.Equal(CapitalReservationStatus.LockedUnknown, lockedUnknown.Status);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.TransitionReservationAsync(
                $"ord-1-{accountId}",
                CapitalReservationStatus.Released,
                0m,
                "worker-primary",
                10L,
                now.AddSeconds(2),
                "TTL_EXPIRED",
                TestContext.Current.CancellationToken));

        var reconciled = await store.ReconcileLockedUnknownAsync(
            $"ord-1-{accountId}",
            CapitalReservationStatus.Released,
            0m,
            "reconcile-corr-1",
            now.AddSeconds(3),
            TestContext.Current.CancellationToken);
        Assert.Equal(CapitalReservationStatus.Released, reconciled.Status);
        Assert.Empty(await store.ReadActiveReservationsAsync(accountId, TestContext.Current.CancellationToken));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cryptotrading-governed-evidence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private static ExperimentPreregistration NewExperimentRegistration() =>
        new(
            ExperimentId.New(), "integration-family", "trend persists after confirmation", "{\"ema_fast\":[20,30]}",
            ["baseline", "faster"], 2, "net_return", "reject if lower bound <= 0", "NO_TRADE/cash",
            new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero), "integration",
            IdempotencyKey.New(), "trend-v1", "commit-integration", "dataset-v1", Hash("dataset"),
            "holdout-v1", "metrics-v1", "environment-v1", Hash("dependencies"), 42,
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 19, 0, 0, 0, TimeSpan.Zero));

    private static string Hash(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private static AutonomyDecisionCommit NewAutonomyCommit(string suffix, DateTimeOffset closeTimeUtc, bool dispatchable)
    {
        var symbol = new Symbol("BTCUSDT");
        var command = new SubmitOrderCommand(
            OrderId.New(), new ClientOrderId("decision-" + suffix), symbol, OrderSide.Buy, OrderType.Limit,
            new Quantity(.01m), new Price(10_000m), 100m, CorrelationId.New(), new IdempotencyKey("decision-" + suffix),
            "policy-v1", closeTimeUtc, ExecutionAttempt: 0, ExecutionExpiresAtUtc: closeTimeUtc.AddMinutes(1));
        var outbox = dispatchable
            ? new OutboxRecord(Guid.NewGuid(), OrderExecutionPipeline.SubmitOrderMessageType, command.CorrelationId,
                command.IdempotencyKey, command.PolicyVersion, command, OutboxRecordStatus.Pending, closeTimeUtc)
            : null;
        var risk = dispatchable
            ? new RiskDecision(RiskDecisionStatus.Approved, 100m, 100m, "policy-v1", [])
            : null;
        return new AutonomyDecisionCommit(
            "ad-" + suffix, "PAPER", "integration-account", "integration:" + suffix, symbol, "baseline", "1.0.0",
            closeTimeUtc, closeTimeUtc, "baseline-feature-snapshot.v1", Hash("config"), Hash("feature"),
            dispatchable ? "EnterLong" : "NoTrade", dispatchable ? "CrossUp" : "InsufficientHistory", risk,
            dispatchable ? "DISPATCHABLE" : "NO_TRADE", dispatchable ? null : "INSUFFICIENT_HISTORY", "{}", outbox);
    }

    private static SubmitOrderCommand NewExpiredSubmit(string clientOrderId, DateTimeOffset now) => new(
        OrderId.New(), new ClientOrderId(clientOrderId), new Symbol("BTCUSDT"), OrderSide.Buy, OrderType.Limit,
        new Quantity(.01m), new Price(10_000m), 100m, CorrelationId.New(), new IdempotencyKey("submit-" + clientOrderId), "ttl", now.AddMinutes(-2),
        ExecutionAttempt: 0, ExecutionExpiresAtUtc: now.AddMinutes(-1));
}
