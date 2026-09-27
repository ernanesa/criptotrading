using System.Globalization;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Regimes;
using CryptoTrading.Domain.Research;
using Xunit;

namespace CryptoTrading.Persistence.Tests;

public sealed class AutonomousLearningPersistenceTests
{
    private static readonly Symbol BtcUsdt = new("BTCUSDT");

    private static PostgresTradingStore CreateStore()
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString) && File.Exists(".env"))
        {
            foreach (var line in File.ReadAllLines(".env"))
            {
                if (line.StartsWith("POSTGRES_CONNECTION_STRING=", StringComparison.OrdinalIgnoreCase))
                {
                    connectionString = line["POSTGRES_CONNECTION_STRING=".Length..].Trim();
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = "Host=127.0.0.1;Port=5432;Database=cryptotrading;Username=cryptotrading;Password=cryptotrading_local_pwd";
        }

        return new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
    }

    [Fact]
    public async Task MarketRegimeSnapshotPersistenceRoundTrip()
    {
        await using var store = CreateStore();
        var now = DateTimeOffset.UtcNow;
        var snapshot = new MarketRegimeSnapshot(
            BtcUsdt,
            now,
            MarketRegime.BullTrend,
            Confidence: 0.85m,
            TrendSlopeRate: 0.0042m,
            VolatilityRate: 0.015m,
            Reason: "STRONG_BULL_TREND_DETECTED");

        await store.RecordMarketRegimeAsync(snapshot);

        var latest = await store.GetLatestMarketRegimeAsync(BtcUsdt);
        Assert.NotNull(latest);
        Assert.Equal(snapshot.Symbol, latest.Symbol);
        Assert.Equal(snapshot.Regime, latest.Regime);
        Assert.Equal(snapshot.Confidence, latest.Confidence);
        Assert.Equal(snapshot.TrendSlopeRate, latest.TrendSlopeRate);
        Assert.Equal(snapshot.VolatilityRate, latest.VolatilityRate);
        Assert.Equal(snapshot.Reason, latest.Reason);

        var history = await store.GetMarketRegimeHistoryAsync(BtcUsdt, limit: 10);
        Assert.NotEmpty(history);
        Assert.Contains(history, h => h.Reason == "STRONG_BULL_TREND_DETECTED");
    }

    [Fact]
    public async Task ModelStrategyRegistryDurableLifecycleManagement()
    {
        await using var store = CreateStore();
        var now = DateTimeOffset.UtcNow;
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var strategyId = $"strat-{uniqueSuffix}";
        var modelV1Id = $"model-v1-{uniqueSuffix}";
        var modelV2Id = $"model-v2-{uniqueSuffix}";

        var modelV1 = new StrategyModelRecord(
            modelV1Id,
            strategyId,
            version: "1.0.0",
            codeCommitSha: "a1b2c3d4e5f67890",
            datasetHash: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            configurationFingerprint: "{\"lookback\": 20}",
            status: ModelLifecycleStatus.Active,
            registeredAtUtc: now,
            registeredBy: "quant-lead-1",
            promotionJustification: "Initial production baseline champion");

        await store.RegisterModelAsync(modelV1);

        var retrievedV1 = await store.GetModelAsync(modelV1Id);
        Assert.NotNull(retrievedV1);
        Assert.Equal(ModelLifecycleStatus.Active, retrievedV1.Status);
        Assert.Equal("Initial production baseline champion", retrievedV1.PromotionJustification);

        var active = await store.GetActiveModelAsync(strategyId);
        Assert.NotNull(active);
        Assert.Equal(modelV1Id, active.ModelId);

        // Register candidate V2
        var modelV2 = new StrategyModelRecord(
            modelV2Id,
            strategyId,
            version: "2.0.0",
            codeCommitSha: "b2c3d4e5f67890a1",
            datasetHash: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            configurationFingerprint: "{\"lookback\": 25}",
            status: ModelLifecycleStatus.Candidate,
            registeredAtUtc: now.AddSeconds(1),
            registeredBy: "quant-lead-2");

        await store.RegisterModelAsync(modelV2);

        // Promote V2 to Active -> V1 automatically retired
        var promoted = await store.TransitionStatusAsync(
            modelV2Id,
            ModelLifecycleStatus.Active,
            actor: "ChampionChallengerPipeline",
            justification: "Challenger beat champion by +0.22 Sharpe with lower drawdown",
            transitionAtUtc: now.AddSeconds(2));

        Assert.Equal(ModelLifecycleStatus.Active, promoted.Status);

        var newActive = await store.GetActiveModelAsync(strategyId);
        Assert.NotNull(newActive);
        Assert.Equal(modelV2Id, newActive.ModelId);

        var oldModel = await store.GetModelAsync(modelV1Id);
        Assert.NotNull(oldModel);
        Assert.Equal(ModelLifecycleStatus.Retired, oldModel.Status);

        var allModels = await store.ListModelsAsync(strategyId);
        Assert.Equal(2, allModels.Count);
    }

    [Fact]
    public async Task DriftObservationPersistenceAndEvaluation()
    {
        await using var store = CreateStore();
        var now = DateTimeOffset.UtcNow;
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var strategyId = $"strat-drift-{uniqueSuffix}";

        var observation = new DriftObservation(
            strategyId,
            DriftType.ExecutionSlippageDrift,
            ExpectedValue: 5.0m,
            ObservedValue: 18.5m,
            AllowedDeviationThreshold: 10.0m,
            ObservedAtUtc: now);

        var evaluation = DriftMonitoringEngine.Evaluate(observation);

        await store.RecordDriftObservationAsync(observation, evaluation);

        var drifts = await store.GetRecentDriftsAsync(strategyId, limit: 10);
        Assert.NotEmpty(drifts);
        var record = drifts[0];
        Assert.Equal(strategyId, record.StrategyId);
        Assert.Equal(DriftType.ExecutionSlippageDrift, record.Type);
        Assert.Equal(DriftSeverity.BreachedThreshold, record.Severity);
        Assert.True(record.RequiresQuarantine);
        Assert.Contains("DRIFT_BREACH", record.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StrategyRegimePerformanceRewardAccumulation()
    {
        await using var store = CreateStore();
        var now = DateTimeOffset.UtcNow;
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var strategyId = $"strat-alloc-{uniqueSuffix}";

        // Record first reward
        await store.RecordStrategyRewardAsync(strategyId, MarketRegime.BullTrend, 0.40m, now);

        // Record second reward
        await store.RecordStrategyRewardAsync(strategyId, MarketRegime.BullTrend, 0.60m, now.AddMinutes(5));

        var performances = await store.GetPerformancesAsync();
        var entry = performances.FirstOrDefault(p => p.StrategyId == strategyId && p.Regime == MarketRegime.BullTrend);

        Assert.NotNull(entry);
        Assert.Equal(2, entry.ObservationCount);
        Assert.Equal(1.00m, entry.CumulativeReward);
        Assert.Equal(0.50m, entry.AverageReward);
    }
}
