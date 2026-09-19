#pragma warning disable CA2007 // Integration assertions intentionally follow the test task context.

using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Domain.Research;
using Npgsql;
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
            createdAtUtc);
        var updated = initial with
        {
            Status = "PARTIALLY_FILLED",
            UpdatedAtUtc = createdAtUtc.AddSeconds(1),
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

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var delete = new NpgsqlCommand(
            "DELETE FROM trading.experiment_trials WHERE experiment_id = @experiment_id;", connection);
        delete.Parameters.AddWithValue("experiment_id", registration.ExperimentId.Value);
        await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
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
