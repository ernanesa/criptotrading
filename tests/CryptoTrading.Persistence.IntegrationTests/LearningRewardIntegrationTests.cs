#pragma warning disable CA2007 // Database assertions follow the integration test task context.

using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Regimes;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Domain.Runtime;
using Npgsql;
using Xunit;
using Xunit.Sdk;

namespace CryptoTrading.Persistence.IntegrationTests;

/// <summary>Exercises durable reward attribution against the PostgreSQL test database only.</summary>
public sealed class LearningRewardIntegrationTests
{
    [Fact]
    public async Task ExecutionRewardIsNormalizedIdempotentAndAggregatedOnceAcrossConcurrentReplays()
    {
        var connectionString = GetConnectionString();
        await using var store = await CreateStoreAsync(connectionString);
        var context = await CreateCommittedOrderAsync(store, connectionString, "learning-success");
        await SeedInventoryAsync(store, context.Symbol, context.BaseAsset, context.QuoteAsset, "seed-" + context.Suffix);
        var sell = CreateExecution(
            "sell-" + context.Suffix,
            context.Symbol,
            context.BaseAsset,
            context.QuoteAsset,
            OrderSide.Sell,
            quantity: 1m,
            price: 110m,
            commission: .11m,
            clientOrderId: context.ClientOrderId,
            feeRate: .001m);
        var execution = await store.RecordExecutionAsync(sell, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(execution.RealizedPnlQuote);
        Assert.Equal(ExecutionFeeComparisonStatus.WithinTolerance, execution.FeeComparison!.Status);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => store.RecordExecutionRewardAsync(sell.ExecutionId, cancellationToken: TestContext.Current.CancellationToken)));

        Assert.Single(attempts, attempt => attempt.Recorded);
        Assert.Equal(7, attempts.Count(attempt => attempt.Replayed));
        Assert.All(attempts, attempt => Assert.Null(attempt.SkippedReason));
        var expectedReward = decimal.Round(execution.RealizedPnlQuote.Value / 110m, 18, MidpointRounding.AwayFromZero);
        Assert.All(attempts, attempt => Assert.Equal(expectedReward, attempt.Reward));

        var aggregate = Assert.Single(
            await store.GetPerformancesAsync(cancellationToken: TestContext.Current.CancellationToken),
            performance => performance.StrategyId == context.StrategyId && performance.Regime == MarketRegime.BullTrend);
        Assert.Equal(1, aggregate.ObservationCount);
        Assert.Equal(expectedReward, aggregate.CumulativeReward);
    }

    [Fact]
    public async Task SkipsMissingRealizedPnlFeeDriftAndUnattributedExecution()
    {
        var connectionString = GetConnectionString();
        await using var store = await CreateStoreAsync(connectionString);

        var buyContext = await CreateCommittedOrderAsync(store, connectionString, "learning-no-pnl", OrderSide.Buy);
        var buy = CreateExecution(
            "buy-no-pnl-" + buyContext.Suffix,
            buyContext.Symbol,
            buyContext.BaseAsset,
            buyContext.QuoteAsset,
            OrderSide.Buy,
            quantity: 1m,
            price: 100m,
            commission: .1m,
            clientOrderId: buyContext.ClientOrderId,
            feeRate: .001m);
        var buyResult = await store.RecordExecutionAsync(buy, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(buyResult.RealizedPnlQuote);
        Assert.Equal(ExecutionFeeComparisonStatus.WithinTolerance, buyResult.FeeComparison!.Status);
        var noPnl = await store.RecordExecutionRewardAsync(buy.ExecutionId, cancellationToken: TestContext.Current.CancellationToken);

        var driftContext = await CreateCommittedOrderAsync(store, connectionString, "learning-drift");
        await SeedInventoryAsync(store, driftContext.Symbol, driftContext.BaseAsset, driftContext.QuoteAsset, "seed-drift-" + driftContext.Suffix);
        var drift = CreateExecution(
            "sell-drift-" + driftContext.Suffix,
            driftContext.Symbol,
            driftContext.BaseAsset,
            driftContext.QuoteAsset,
            OrderSide.Sell,
            quantity: 1m,
            price: 110m,
            commission: .5m,
            clientOrderId: driftContext.ClientOrderId,
            feeRate: .001m);
        var driftResult = await store.RecordExecutionAsync(drift, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(driftResult.RealizedPnlQuote);
        Assert.Equal(ExecutionFeeComparisonStatus.Drift, driftResult.FeeComparison!.Status);
        var driftReward = await store.RecordExecutionRewardAsync(drift.ExecutionId, cancellationToken: TestContext.Current.CancellationToken);

        var noAttributionContext = await CreateCommittedOrderAsync(store, connectionString, "learning-no-attribution");
        await SeedInventoryAsync(store, noAttributionContext.Symbol, noAttributionContext.BaseAsset, noAttributionContext.QuoteAsset, "seed-unlinked-" + noAttributionContext.Suffix);
        var unlinked = CreateExecution(
            "sell-unlinked-" + noAttributionContext.Suffix,
            noAttributionContext.Symbol,
            noAttributionContext.BaseAsset,
            noAttributionContext.QuoteAsset,
            OrderSide.Sell,
            quantity: 1m,
            price: 110m,
            commission: .11m,
            clientOrderId: null,
            feeRate: .001m);
        var unlinkedResult = await store.RecordExecutionAsync(unlinked, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(unlinkedResult.RealizedPnlQuote);
        Assert.Equal(ExecutionFeeComparisonStatus.WithinTolerance, unlinkedResult.FeeComparison!.Status);
        var missingAttribution = await store.RecordExecutionRewardAsync(unlinked.ExecutionId, cancellationToken: TestContext.Current.CancellationToken);

        var incompleteFeeContext = await CreateCommittedOrderAsync(store, connectionString, "learning-incomplete-fee");
        await SeedInventoryAsync(store, incompleteFeeContext.Symbol, incompleteFeeContext.BaseAsset,
            incompleteFeeContext.QuoteAsset, "seed-incomplete-fee-" + incompleteFeeContext.Suffix);
        var incompleteFeeSell = CreateExecution(
            "sell-incomplete-fee-" + incompleteFeeContext.Suffix,
            incompleteFeeContext.Symbol,
            incompleteFeeContext.BaseAsset,
            incompleteFeeContext.QuoteAsset,
            OrderSide.Sell,
            quantity: 1m,
            price: 110m,
            commission: .11m,
            clientOrderId: incompleteFeeContext.ClientOrderId,
            feeRate: null);
        var incompleteFeeResult = await store.RecordExecutionAsync(incompleteFeeSell, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(incompleteFeeResult.RealizedPnlQuote);
        Assert.Equal(ExecutionFeeComparisonStatus.NotComparable, incompleteFeeResult.FeeComparison!.Status);
        var incompleteFeeReward = await store.RecordExecutionRewardAsync(incompleteFeeSell.ExecutionId, cancellationToken: TestContext.Current.CancellationToken);

        var baseFeeContext = await CreateCommittedOrderAsync(store, connectionString, "learning-base-fee");
        await SeedInventoryAsync(store, baseFeeContext.Symbol, baseFeeContext.BaseAsset, baseFeeContext.QuoteAsset,
            "seed-base-fee-" + baseFeeContext.Suffix, quantity: 2m);
        var baseFeeSell = CreateExecution(
            "sell-base-fee-" + baseFeeContext.Suffix,
            baseFeeContext.Symbol,
            baseFeeContext.BaseAsset,
            baseFeeContext.QuoteAsset,
            OrderSide.Sell,
            quantity: 1m,
            price: 110m,
            commission: .001m,
            clientOrderId: baseFeeContext.ClientOrderId,
            feeRate: .001m,
            commissionAsset: baseFeeContext.BaseAsset);
        var baseFeeResult = await store.RecordExecutionAsync(baseFeeSell, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(baseFeeResult.RealizedPnlQuote);
        Assert.Equal(ExecutionFeeComparisonStatus.WithinTolerance, baseFeeResult.FeeComparison!.Status);
        var baseFeeReward = await store.RecordExecutionRewardAsync(baseFeeSell.ExecutionId, cancellationToken: TestContext.Current.CancellationToken);

        AssertSkipped(noPnl);
        AssertSkipped(driftReward);
        AssertSkipped(missingAttribution);
        AssertSkipped(incompleteFeeReward);
        AssertSkipped(baseFeeReward);
        var strategyIds = new[]
        {
            buyContext.StrategyId, driftContext.StrategyId, noAttributionContext.StrategyId,
            incompleteFeeContext.StrategyId, baseFeeContext.StrategyId,
        };
        Assert.DoesNotContain((await store.GetPerformancesAsync(cancellationToken: TestContext.Current.CancellationToken)), item => strategyIds.Contains(item.StrategyId, StringComparer.Ordinal));
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        }

        return connectionString;
    }

    private static async Task<PostgresTradingStore> CreateStoreAsync(string connectionString)
    {
        var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        await store.ApplyMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken);
        return store;
    }

    private static async Task<TestOrderContext> CreateCommittedOrderAsync(
        PostgresTradingStore store,
        string connectionString,
        string purpose,
        OrderSide side = OrderSide.Sell)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var baseAsset = new Asset("L" + suffix);
        var quoteAsset = new Asset("USDT");
        var symbol = new Symbol(baseAsset.Value + quoteAsset.Value);
        var clientOrderId = "lr-" + Guid.NewGuid().ToString("N")[..24];
        var strategyId = "strategy-" + purpose + "-" + suffix;
        var now = DateTimeOffset.UtcNow.AddSeconds(-1);
        var command = new SubmitOrderCommand(
            OrderId.New(), new ClientOrderId(clientOrderId), symbol, side, OrderType.Limit,
            new Quantity(1m), new Price(110m), 110m, CorrelationId.New(),
            new IdempotencyKey("learning-" + suffix), "integration-learning", now,
            ExecutionAttempt: 0, ExecutionExpiresAtUtc: now.AddMinutes(1));
        var outbox = new OutboxRecord(
            Guid.NewGuid(), OrderExecutionPipeline.SubmitOrderMessageType, command.CorrelationId,
            command.IdempotencyKey, command.PolicyVersion, command, OutboxRecordStatus.Pending, now);
        var commit = new AutonomyDecisionCommit(
            "learning-decision-" + suffix, "PAPER", "integration-learning-account", "learning:" + suffix,
            symbol, strategyId, "1.0.0", now, now, "integration-feature-v1", Hash("config-" + suffix),
            Hash("feature-" + suffix), side == OrderSide.Buy ? "EnterLong" : "EnterShort",
            MarketRegime.BullTrend.ToString(), new RiskDecision(RiskDecisionStatus.Approved, 110m, 110m, "integration-learning", []),
            "DISPATCHABLE", null, "{}", outbox);
        await store.CommitAsync(commit, cancellationToken: TestContext.Current.CancellationToken);
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
            await using var deferOutbox = new NpgsqlCommand(
                "UPDATE trading.outbox_messages SET available_at = TIMESTAMPTZ '2099-01-01 00:00:00+00' WHERE message_id = @message_id;",
                connection);
            deferOutbox.Parameters.AddWithValue("message_id", outbox.MessageId);
            await deferOutbox.ExecuteNonQueryAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        return new(suffix, symbol, baseAsset, quoteAsset, clientOrderId, strategyId);
    }

    private static async Task SeedInventoryAsync(
        PostgresTradingStore store,
        Symbol symbol,
        Asset baseAsset,
        Asset quoteAsset,
        string executionId,
        decimal quantity = 1m)
    {
        _ = await store.RecordExecutionAsync(CreateExecution(
            executionId, symbol, baseAsset, quoteAsset, OrderSide.Buy,
            quantity, price: 100m, commission: 0m, clientOrderId: null, feeRate: null), cancellationToken: TestContext.Current.CancellationToken);
    }

    private static ExecutionAccountingCommand CreateExecution(
        string executionId,
        Symbol symbol,
        Asset baseAsset,
        Asset quoteAsset,
        OrderSide side,
        decimal quantity,
        decimal price,
        decimal commission,
        string? clientOrderId,
        decimal? feeRate,
        Asset? commissionAsset = null)
    {
        var occurredAt = DateTimeOffset.UtcNow;
        var estimate = feeRate is { } rate
            ? new ExecutionFeeEstimate(rate, "fee-" + executionId, "integration-learning", occurredAt.AddSeconds(-1), occurredAt.AddMinutes(1))
            : null;
        return new ExecutionAccountingCommand(
            executionId, Convert.ToInt64(Guid.NewGuid().ToString("N")[..12], 16),
            Convert.ToInt64(Guid.NewGuid().ToString("N")[..12], 16), symbol, baseAsset, quoteAsset, side,
            new Quantity(quantity), new Price(price), new Money(commission, commissionAsset ?? quoteAsset),
            new AccountId("learning-wallet-" + executionId), new AccountId("learning-fees-" + executionId),
            LedgerEntryId.New(), CorrelationId.New(), new IdempotencyKey("learning-ledger-" + executionId),
            occurredAt, clientOrderId, estimate);
    }

    private static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private static void AssertSkipped(ExecutionLearningRewardResult result)
    {
        Assert.False(result.Recorded);
        Assert.False(result.Replayed);
        Assert.Equal("EXECUTION_ATTRIBUTION_REALIZED_PNL_OR_FEE_INCOMPLETE", result.SkippedReason);
    }

    private sealed record TestOrderContext(
        string Suffix,
        Symbol Symbol,
        Asset BaseAsset,
        Asset QuoteAsset,
        string ClientOrderId,
        string StrategyId);
}
