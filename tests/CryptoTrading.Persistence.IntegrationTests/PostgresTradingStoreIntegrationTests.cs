#pragma warning disable CA2007 // Integration assertions intentionally follow the test task context.

using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Domain.Research;
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
    public async Task PersistsExecutionLedgerPositionAndRealizedPnlIdempotently()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip(
                "POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        }

        await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString!));
        await store.ApplyMigrationsAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var symbol = new Symbol($"FOO{suffix}USDT");
        var baseAsset = new Asset($"FOO{suffix}");
        var quoteAsset = new Asset("USDT");
        var wallet = new AccountId($"integration-execution-wallet-{suffix}");
        var fees = new AccountId($"integration-execution-fees-{suffix}");
        var first = ExecutionCommand("buy-" + suffix, 1001, OrderSide.Buy, 1m, 100m, .1m, symbol, baseAsset, quoteAsset, wallet, fees);
        var firstResult = await store.RecordExecutionAsync(first);
        var replay = await store.RecordExecutionAsync(first);
        Assert.False(firstResult.IsDuplicate);
        Assert.True(replay.IsDuplicate);
        Assert.Equal(firstResult.LedgerEntry.Id, replay.LedgerEntry.Id);

        var sell = ExecutionCommand("sell-" + suffix, 1002, OrderSide.Sell, .5m, 120m, .05m, symbol, baseAsset, quoteAsset, wallet, fees);
        var sellResult = await store.RecordExecutionAsync(sell);
        Assert.Equal(9.9m, sellResult.RealizedPnlQuote);
        Assert.Equal(.5m, (await store.GetBalanceAsync(wallet, baseAsset)).Amount);

        static ExecutionAccountingCommand ExecutionCommand(
            string id, long tradeId, OrderSide side, decimal quantity, decimal price, decimal commission,
            Symbol symbol, Asset baseAsset, Asset quoteAsset, AccountId wallet, AccountId fees) => new(
                id, tradeId, tradeId, symbol, baseAsset, quoteAsset, side, new Quantity(quantity), new Price(price),
                new Money(commission, quoteAsset), wallet, fees, LedgerEntryId.New(), CorrelationId.New(),
                new IdempotencyKey("integration-" + id), DateTimeOffset.UtcNow);
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
        await store.ApplyMigrationsAsync();
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

        await store.EnqueueAsync(outbox);
        var persistedEstimate = await store.ReadExpectedExecutionFeeAsync(clientOrderId.Value);
        Assert.NotNull(persistedEstimate);
        Assert.Equal(estimate.Rate, persistedEstimate.Rate);
        Assert.Equal(estimate.SnapshotHash, persistedEstimate.SnapshotHash);
        Assert.Equal(estimate.Source, persistedEstimate.Source);
        Assert.Equal(100m, persistedEstimate.ReferencePrice);
        Assert.Equal(1m, persistedEstimate.AuthorizedQuantity);
        var context = await store.ReadExecutionSubmissionContextAsync(clientOrderId.Value);
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
        var result = await store.RecordExecutionAsync(accounting);

        Assert.True(result.FeeComparison!.IsDrift);
        Assert.Equal("COMPARABLE", result.QualityComparison!.Status);
        Assert.Equal(1m, result.QualityComparison.FillRatio);
        Assert.Equal(0m, result.QualityComparison.ImplementationShortfallQuote);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand(
            "SELECT client_order_id, fee_comparison_status, fee_comparison_reason, commission_valuation_source, cumulative_fill_ratio, implementation_shortfall_quote, execution_quality_status FROM trading.execution_fills WHERE trade_id = @trade_id;",
            connection);
        query.Parameters.AddWithValue("trade_id", tradeId);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(clientOrderId.Value, reader.GetString(0));
        Assert.Equal("DRIFT", reader.GetString(1));
        Assert.Equal("EXECUTION_FEE_DRIFT", reader.GetString(2));
        Assert.Equal("integration:bnb-valuation", reader.GetString(3));
        Assert.Equal(1m, reader.GetDecimal(4));
        Assert.Equal(0m, reader.GetDecimal(5));
        Assert.Equal("COMPARABLE", reader.GetString(6));
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
        await store.CheckConnectivityAsync();
        await store.ApplyMigrationsAsync();

        var now = DateTimeOffset.UtcNow;
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

        Assert.Same(ledger, await store.AppendJournalEntryAsync(ledger));
        var replayed = await store.AppendJournalEntryAsync(ledger);
        Assert.Equal(ledgerId, replayed.Id);
        Assert.Equal(100m, (await store.GetBalanceAsync(new AccountId("integration-cash"), usdt)).Amount);
        Assert.Contains("USDT", await store.GetAssetsAsync(new AccountId("integration-cash")));

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

        await store.EnqueueAsync(outbox);
        await store.EnqueueAsync(outbox);

        var firstOwner = "integration-owner-a";
        var firstLease = await store.ClaimOutboxAsync(firstOwner, now, TimeSpan.FromSeconds(5), 1);
        var leased = Assert.Single(firstLease);
        Assert.Equal(1, leased.Attempts);
        Assert.Equal(firstOwner, leased.LeaseOwner);

        Assert.True(await store.FailOutboxAsync(
            leased.MessageId,
            firstOwner,
            "simulated-transient-failure",
            now.AddSeconds(-1)));

        var secondOwner = "integration-owner-b";
        var retryLease = await store.ClaimOutboxAsync(secondOwner, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), 1);
        var retried = Assert.Single(retryLease);
        Assert.Equal(leased.MessageId, retried.MessageId);
        Assert.Equal(2, retried.Attempts);
        Assert.Equal(secondOwner, retried.LeaseOwner);
        Assert.True(await store.MarkOutboxProcessedAsync(retried.MessageId, secondOwner, DateTimeOffset.UtcNow));
        Assert.Empty(await store.ClaimOutboxAsync("integration-owner-c", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), 1));
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

        var now = DateTimeOffset.UtcNow;
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
            await firstProcess.ApplyMigrationsAsync();
            await firstProcess.EnqueueAsync(outbox);

            var claimedBeforeCrash = await firstProcess.ClaimOutboxAsync(
                "integration-crashed-worker",
                now,
                TimeSpan.FromSeconds(1),
                1);
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
            1);
        var recoveredMessage = Assert.Single(recovered);
        Assert.Equal(outbox.MessageId, recoveredMessage.MessageId);
        Assert.Equal(2, recoveredMessage.Attempts);
        Assert.Equal("integration-restarted-worker", recoveredMessage.LeaseOwner);

        Assert.False(await restartedProcess.MarkOutboxProcessedAsync(
            recoveredMessage.MessageId,
            "integration-crashed-worker",
            now.AddSeconds(2)));
        Assert.True(await restartedProcess.MarkOutboxProcessedAsync(
            recoveredMessage.MessageId,
            "integration-restarted-worker",
            now.AddSeconds(2)));
        Assert.Empty(await restartedProcess.ClaimOutboxAsync(
            "integration-verification-worker",
            now.AddSeconds(2),
            TimeSpan.FromSeconds(5),
            1));
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
        await store.ApplyMigrationsAsync();

        await store.RecordKnownTradeAsync(9_000_001, "BTCUSDT");
        await store.RecordKnownTradeAsync(9_000_001, "BTCUSDT");

        var ids = await store.GetKnownTradeIdsAsync();
        Assert.Contains(9_000_001, ids);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordKnownTradeAsync(9_000_001, "ETHUSDT"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.RecordKnownTradeAsync(-1, "BTCUSDT"));
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
        await store.ApplyMigrationsAsync();
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

        await store.AppendJournalEntryWithKnownTradeAsync(entry, 9_000_002, "BTCUSDT");
        await store.AppendJournalEntryWithKnownTradeAsync(entry, 9_000_002, "BTCUSDT");

        Assert.Contains(9_000_002, await store.GetKnownTradeIdsAsync());
        Assert.Equal(7m, (await store.GetBalanceAsync(new AccountId("atomic-cash"), new Asset("USDT"))).Amount);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AppendJournalEntryWithKnownTradeAsync(entry, 9_000_002, "ETHUSDT"));
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
        await store.ApplyMigrationsAsync();
        var now = DateTimeOffset.UtcNow.AddSeconds(-2);
        var expires = now.AddHours(2);
        var first = RiskPolicyDocument.CreateDraft(
            new RiskPolicy("registry-v1-" + Guid.NewGuid().ToString("N"), 100m, 250m, 25m, 50m, TimeSpan.FromSeconds(5)),
            now, expires, "policy-author");

        await store.SaveRiskPolicyAsync(first);
        await store.SaveRiskPolicyAsync(first);
        var conflicting = RiskPolicyDocument.CreateDraft(
            new RiskPolicy(first.Policy.Version, 101m, 250m, 25m, 50m, TimeSpan.FromSeconds(5)),
            now, expires, "policy-author");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveRiskPolicyAsync(conflicting));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApproveRiskPolicyAsync(
            first.Policy.Version, "approval-self", "policy-author", now.AddMinutes(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ActivateRiskPolicyAsync(first.Policy.Version, now.AddMinutes(1)));

        var approvedFirst = await store.ApproveRiskPolicyAsync(
            first.Policy.Version, "approval-v1", "policy-reviewer", now.AddMinutes(1));
        Assert.Equal(RiskPolicyLifecycle.Approved, approvedFirst.Lifecycle);
        var activeFirst = await store.ActivateRiskPolicyAsync(first.Policy.Version, now.AddMinutes(2));
        Assert.Equal(RiskPolicyLifecycle.Active, activeFirst.Lifecycle);
        Assert.Equal(first.Policy.Version, (await store.GetActiveRiskPolicyAsync(now.AddMinutes(3)))?.Policy.Version);

        var second = RiskPolicyDocument.CreateDraft(
            new RiskPolicy("registry-v2-" + Guid.NewGuid().ToString("N"), 90m, 200m, 20m, 40m, TimeSpan.FromSeconds(5)),
            now, expires, "policy-author");
        await store.SaveRiskPolicyAsync(second);
        await store.ApproveRiskPolicyAsync(second.Policy.Version, "approval-v2", "policy-reviewer", now.AddMinutes(4));
        await store.ActivateRiskPolicyAsync(second.Policy.Version, now.AddMinutes(5));
        Assert.Equal(second.Policy.Version, (await store.GetActiveRiskPolicyAsync(now.AddMinutes(6)))?.Policy.Version);
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
        await store.ApplyMigrationsAsync();
        var receivedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var record = new AccountStreamEventRecord(
            Guid.NewGuid(), "executionReport", "execution-" + Guid.NewGuid().ToString("N"),
            1_720_000_000_123, receivedAt,
            "{\"e\":\"executionReport\",\"s\":\"BTCUSDT\",\"x\":\"TRADE\"}", "BTCUSDT",
            Guid.NewGuid(), 1, 7);

        Assert.True(await store.AppendAccountStreamEventAsync(record));
        Assert.False(await store.AppendAccountStreamEventAsync(record));
        var events = await store.ReadAccountStreamEventsAsync(receivedAt.AddSeconds(-1));
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAccountStreamEventAsync(conflicting));
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
            await firstProcess.ApplyMigrationsAsync();
            await firstProcess.UpsertOpenOrderAsync(initial);
            await firstProcess.UpsertOpenOrderAsync(initial);
            await firstProcess.UpsertOpenOrderAsync(updated);
        }

        // A new store instance represents a worker restart. The projection is
        // replayable: only the latest active identity/status is visible.
        await using (var restartedProcess = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString)))
        {
            var replayed = Assert.Single(await restartedProcess.ReadOpenOrdersAsync());
            Assert.Equal(updated, replayed);

            await restartedProcess.RemoveOpenOrderAsync(clientOrderId);
        }

        await using var verificationProcess = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        Assert.Empty(await verificationProcess.ReadOpenOrdersAsync());

        // A later authoritative open-order snapshot may recreate the identity;
        // this verifies that removal is a projection state transition, not a
        // destructive loss of the ability to replay the order.
        await verificationProcess.UpsertOpenOrderAsync(updated);
        var replayedAfterRemoval = Assert.Single(await verificationProcess.ReadOpenOrdersAsync());
        Assert.Equal(updated, replayedAfterRemoval);
        await verificationProcess.RemoveOpenOrderAsync(clientOrderId);
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
        await store.ApplyMigrationsAsync();
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

        Assert.True(await store.AppendOperationalControlAuditEventAsync(auditEvent));
        Assert.False(await store.AppendOperationalControlAuditEventAsync(auditEvent));

        var persisted = Assert.Single(
            await store.ReadOperationalControlAuditEventsAsync(occurredAtUtc.AddSeconds(-1)));
        Assert.Equal(auditEvent, persisted);
        Assert.Equal(auditEvent.PayloadSha256, persisted.PayloadSha256);

        var conflictingIdentity = auditEvent with { EventId = Guid.NewGuid() };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AppendOperationalControlAuditEventAsync(conflictingIdentity));

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var delete = new NpgsqlCommand(
            "DELETE FROM trading.operational_control_audit_events WHERE event_id = @event_id;",
            connection);
        delete.Parameters.AddWithValue("event_id", auditEvent.EventId);
        await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
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
        await store.ApplyMigrationsAsync();
        var registration = NewExperimentRegistration();
        Assert.True(await store.AppendExperimentPreregistrationAsync(registration));
        Assert.False(await store.AppendExperimentPreregistrationAsync(registration));

        var trial = new ExperimentTrial(
            1, "baseline", "{\"ema_fast\":20}", Hash("trial-result"),
            ExperimentDecision.Rejected, registration.PreregisteredAtUtc, "integration", new IdempotencyKey("trial-1"));
        Assert.True(await store.AppendExperimentTrialAsync(registration.ExperimentId, trial));
        Assert.False(await store.AppendExperimentTrialAsync(registration.ExperimentId, trial));

        var registry = new InMemoryExperimentRegistry();
        registry.Register(registration);
        var access = registry.AccessHoldout(registration.ExperimentId,
            new HoldoutAccessRequest("reviewer", "locked evaluation", registration.PreregisteredAtUtc,
                new IdempotencyKey("holdout-1"), true, "approval-integration"));
        Assert.True(await store.AppendHoldoutAccessAuditAsync(access.Audit));
        Assert.False(await store.AppendHoldoutAccessAuditAsync(access.Audit));

        Assert.True(await store.AppendHoldoutContaminationAsync(
            registration.ExperimentId, "reviewer", "manual contamination drill", "holdout-v2",
            registration.PreregisteredAtUtc.AddMinutes(1), new IdempotencyKey("contamination-1")));
        Assert.False(await store.AppendHoldoutContaminationAsync(
            registration.ExperimentId, "reviewer", "manual contamination drill", "holdout-v2",
            registration.PreregisteredAtUtc.AddMinutes(1), new IdempotencyKey("contamination-1")));

        await using var restartedStore = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        var snapshot = await restartedStore.ReadExperimentGovernanceSnapshotAsync(registration.ExperimentId);
        Assert.NotNull(snapshot);
        Assert.Equal(registration.PreregistrationSha256, snapshot!.Preregistration.PreregistrationSha256);
        Assert.Single(snapshot.Trials);
        Assert.Equal(1, snapshot.HoldoutAccessCount);
        Assert.True(snapshot.HoldoutContaminated);
        Assert.Equal("holdout-v2", snapshot.ReplacementHoldoutVersion);
        Assert.Equal("manual contamination drill", snapshot.ContaminationReason);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var delete = new NpgsqlCommand(
            "DELETE FROM trading.experiment_trials WHERE experiment_id = @experiment_id;", connection);
        delete.Parameters.AddWithValue("experiment_id", registration.ExperimentId.Value);
        await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
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
            WalkForwardPlan.Create(dataset.Bars.Count, 60, 20, 20));
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
        await store.ApplyMigrationsAsync();
        Assert.True(await store.AppendExperimentPreregistrationAsync(preregistration));
        var trial = new ExperimentTrial(
            1,
            "baseline",
            "baseline-v1",
            Hash("integration-trial"),
            ExperimentDecision.Rejected,
            preregistration.PreregisteredAtUtc,
            "integration",
            IdempotencyKey.New());
        Assert.True(await store.AppendExperimentTrialAsync(preregistration.ExperimentId, trial));

        await using var restarted = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        var snapshot = await restarted.ReadExperimentGovernanceSnapshotAsync(preregistration.ExperimentId);
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
}
