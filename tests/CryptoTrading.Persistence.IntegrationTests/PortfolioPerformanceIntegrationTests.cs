#pragma warning disable CA2007 // Tests retain the xUnit synchronization context.

using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Research;
using Npgsql;
using Xunit;
using Xunit.Sdk;

namespace CryptoTrading.Persistence.IntegrationTests;

public sealed class PortfolioPerformanceIntegrationTests
{
    [Fact]
    public async Task PerformanceUsesValuedFeesOnlyAndKeepsUnknownAssetsSeparated()
    {
        await using var store = await CreateStoreAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var occurredAtUtc = new DateTimeOffset(2026, 10, 7, 11, 0, 0, TimeSpan.Zero);
        var before = await store.GetPerformanceMetricsAsync(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero), cancellationToken: TestContext.Current.CancellationToken);
        var beforeBnb = before.UnvaluedFeeAmounts.SingleOrDefault(static amount => amount.Asset == "BNB")?.Amount ?? 0m;
        var rejected = NewExecution("rejected-unvalued-" + suffix, 500_000 + Convert.ToInt64(suffix, 16), new Money(.01m, new Asset("BNB")), occurredAtUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordExecutionAsync(rejected, cancellationToken: TestContext.Current.CancellationToken));
        await using (var verification = new NpgsqlConnection(Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")))
        {
            await verification.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
            await using var query = new NpgsqlCommand("SELECT (SELECT count(*) FROM trading.execution_fills WHERE execution_id = @execution) + (SELECT count(*) FROM trading.journal_entries WHERE entry_id = @entry);", verification);
            query.Parameters.AddWithValue("execution", rejected.ExecutionId);
            query.Parameters.AddWithValue("entry", rejected.LedgerEntryId.Value);
            Assert.Equal(0L, (long)(await query.ExecuteScalarAsync(cancellationToken: TestContext.Current.CancellationToken))!);
        }
        // A trusted conversion allows accounting; a missing expectation still makes the fee-comparison report partial.
        await store.RecordExecutionAsync(NewExecution("valued-" + suffix, 100_000 + Convert.ToInt64(suffix, 16), new Money(.10m, new Asset("USDT")), occurredAtUtc), cancellationToken: TestContext.Current.CancellationToken);
        await store.RecordExecutionAsync(NewExecution("valued-btc-" + suffix, 200_000 + Convert.ToInt64(suffix, 16), new Money(.001m, new Asset("BTC")), occurredAtUtc), cancellationToken: TestContext.Current.CancellationToken);
        await store.RecordExecutionAsync(NewExecution("valued-bnb-" + suffix, 300_000 + Convert.ToInt64(suffix, 16), new Money(.01m, new Asset("BNB")), occurredAtUtc,
            new ExecutionFeeAssetValuation(new Asset("BNB"), new Asset("USDT"), 20m, occurredAtUtc.AddMinutes(-1), "integration-bnb-mark")), cancellationToken: TestContext.Current.CancellationToken);
        await store.RecordExecutionAsync(NewExecution("unvalued-" + suffix, 400_000 + Convert.ToInt64(suffix, 16), new Money(.01m, new Asset("BNB")), occurredAtUtc,
            new ExecutionFeeAssetValuation(new Asset("BNB"), new Asset("USDT"), 20m, occurredAtUtc.AddMinutes(-1), "integration-bnb-mark"), hasFeeEstimate: false), cancellationToken: TestContext.Current.CancellationToken);

        var metrics = await store.GetPerformanceMetricsAsync(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("BTCUSDT", metrics.ScopeSymbol);
        Assert.Equal("USDT", metrics.QuoteAsset);
        Assert.Equal(before.TotalFeesPaidQuote + 10.30m, metrics.TotalFeesPaidQuote);
        var unknownFee = Assert.Single(metrics.UnvaluedFeeAmounts, static amount => amount.Asset == "BNB");
        Assert.Equal("BNB", unknownFee.Asset);
        Assert.Equal(beforeBnb + .01m, unknownFee.Amount);
        Assert.False(metrics.FeesAreComplete);
    }

    [Fact]
    public async Task PeriodDeltasRequireKnownSnapshotsAndUseUtcBoundaries()
    {
        await using var store = await CreateStoreAsync();
        var asOfUtc = new DateTimeOffset(2090, 1, 1, 12, 0, 0, TimeSpan.Zero).AddSeconds(Guid.NewGuid().GetHashCode() & 0x7FFF);
        var startUtc = new DateTimeOffset(asOfUtc.UtcDateTime.Date, TimeSpan.Zero);
        await store.RecordNavSnapshotAsync(new("BTCUSDT", "USDT", startUtc, 100m, 10m, 0m, "integration"), cancellationToken: TestContext.Current.CancellationToken);
        await store.RecordNavSnapshotAsync(new("BTCUSDT", "USDT", asOfUtc, 115m, 14m, 5m, "integration"), cancellationToken: TestContext.Current.CancellationToken);

        var metrics = await store.GetPerformanceMetricsAsync(asOfUtc, cancellationToken: TestContext.Current.CancellationToken);
        var day = metrics.Periods[0];

        Assert.Equal(115m, metrics.CurrentNetAssetValueQuote);
        Assert.Equal(14m, metrics.CurrentUnrealizedPnlQuote);
        Assert.Equal(10m, day.NetAssetValueDeltaQuote);
        Assert.Equal(4m, day.UnrealizedPnlDeltaQuote);
        Assert.Equal(PerformanceSnapshotAvailability.Available, day.SnapshotAvailability);

        var unknownFlowAsOfUtc = asOfUtc.AddMinutes(1);
        await store.RecordNavSnapshotAsync(new("BTCUSDT", "USDT", unknownFlowAsOfUtc, 120m, 15m, null, "integration"), cancellationToken: TestContext.Current.CancellationToken);
        var unknownFlow = (await store.GetPerformanceMetricsAsync(unknownFlowAsOfUtc, cancellationToken: TestContext.Current.CancellationToken)).Periods[0];
        Assert.Null(unknownFlow.NetAssetValueDeltaQuote);
        Assert.Equal(PerformanceSnapshotAvailability.UnknownExternalCashFlow, unknownFlow.SnapshotAvailability);

        var withoutHistory = await store.GetPerformanceMetricsAsync(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), cancellationToken: TestContext.Current.CancellationToken);
        Assert.All(withoutHistory.Periods, period =>
        {
            Assert.Null(period.NetAssetValueDeltaQuote);
            Assert.NotEqual(PerformanceSnapshotAvailability.Available, period.SnapshotAvailability);
        });

        await Assert.ThrowsAsync<ArgumentException>(() => store.GetPerformanceMetricsAsync(asOfUtc.ToOffset(TimeSpan.FromHours(-3)), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ModelRegistrationAcceptsEquivalentReplayAndRejectsDivergentCollision()
    {
        await using var store = await CreateStoreAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var model = NewModel("model-" + suffix, "strategy-" + suffix, "{\"lookback\":20}", now);

        await store.RegisterModelAsync(model, cancellationToken: TestContext.Current.CancellationToken);
        await store.RegisterModelAsync(model, cancellationToken: TestContext.Current.CancellationToken);

        var collision = NewModel(model.ModelId, model.StrategyId, "{\"lookback\":21}", now);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RegisterModelAsync(collision, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static async Task<PostgresTradingStore> CreateStoreAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) throw SkipException.ForSkip("POSTGRES_CONNECTION_STRING must be supplied by scripts/test-postgres-integration.sh.");
        var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
        await store.ApplyMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken);
        return store;
    }

    private static ExecutionAccountingCommand NewExecution(
        string executionId,
        long tradeId,
        Money commission,
        DateTimeOffset occurredAtUtc,
        ExecutionFeeAssetValuation? feeAssetValuation = null,
        bool hasFeeEstimate = true) => new(
        executionId, Math.Abs(tradeId), Math.Abs(tradeId), new Symbol("BTCUSDT"), new Asset("BTC"), new Asset("USDT"),
        OrderSide.Buy, new Quantity(.01m), new Price(10_000m), commission,
        new AccountId("performance-wallet-" + executionId), new AccountId("performance-fees-" + executionId),
        LedgerEntryId.New(), CorrelationId.New(), new IdempotencyKey("performance-" + executionId), occurredAtUtc,
        feeEstimate: hasFeeEstimate ? new ExecutionFeeEstimate(.001m, "fee-" + executionId, "integration", occurredAtUtc.AddMinutes(-1), occurredAtUtc.AddMinutes(1)) : null,
        feeAssetValuation: feeAssetValuation);

    private static StrategyModelRecord NewModel(string modelId, string strategyId, string fingerprint, DateTimeOffset now) => new(
        modelId, strategyId, "1.0.0", "abc123", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
        fingerprint, ModelLifecycleStatus.Candidate, now, "integration");
}
