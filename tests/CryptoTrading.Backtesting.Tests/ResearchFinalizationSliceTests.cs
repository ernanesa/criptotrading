using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class ResearchFinalizationSliceTests
{
    private static readonly Symbol BtcUsdt = new("BTCUSDT");
    private static readonly Symbol EthUsdt = new("ETHUSDT");
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ==========================================
    // F07: Strategy Fill Feedback & Recovery
    // ==========================================

    [Fact]
    public void F07RejectedFeedbackDoesNotMutateAdaptiveGridInventory()
    {
        var parameters = new AdaptiveGridParameters("grid-feedback-test", "1.0", BtcUsdt, 0.02m, 5, 0.01m, 0.05m, 0.001m, 0.0005m);
        var strategy = new AdaptiveGridStrategy(parameters);
        var bar0 = new CloseBar(BtcUsdt, Start, 100m);
        var bar1 = new CloseBar(BtcUsdt, Start.AddHours(1), 97.5m);

        strategy.GenerateSignal(bar0, 0);
        var signal1 = strategy.GenerateSignal(bar1, 1);

        Assert.Equal(StrategyExecutionAction.BuyBaseQuantity, signal1.Action);
        Assert.Equal(0m, strategy.CurrentInventory); // Signal emitted, but not executed yet!

        // Simulate rejection (e.g. MIN_NOTIONAL filter)
        var feedback = new StrategyExecutionFeedback(
            DecisionBarIndex: 1,
            FillBarIndex: 2,
            TimestampUtc: Start.AddHours(2),
            Action: StrategyExecutionAction.BuyBaseQuantity,
            Outcome: StrategyExecutionOutcome.RejectedMinimumNotional,
            RequestedQuantity: 0.01m,
            ExecutedQuantity: 0m,
            ExecutedPrice: 97.5m,
            FeePaid: 0m,
            ReasonCode: "MIN_NOTIONAL");

        strategy.ApplyExecutionFeedback(feedback);

        // Internal inventory must remain 0, never assuming full fill
        Assert.Equal(0m, strategy.CurrentInventory);
    }

    [Fact]
    public void F07PartialFillFeedbackUpdatesInventoryStrictlyByExecutedQuantity()
    {
        var parameters = new AdaptiveGridParameters("grid-partial-test", "1.0", BtcUsdt, 0.02m, 5, 0.01m, 0.05m, 0.001m, 0.0005m);
        var strategy = new AdaptiveGridStrategy(parameters);
        var bar0 = new CloseBar(BtcUsdt, Start, 100m);
        var bar1 = new CloseBar(BtcUsdt, Start.AddHours(1), 97.5m);

        strategy.GenerateSignal(bar0, 0);
        strategy.GenerateSignal(bar1, 1);

        // Partial fill of 0.004m out of 0.010m requested
        var partialFeedback = new StrategyExecutionFeedback(
            DecisionBarIndex: 1,
            FillBarIndex: 2,
            TimestampUtc: Start.AddHours(2),
            Action: StrategyExecutionAction.BuyBaseQuantity,
            Outcome: StrategyExecutionOutcome.PartiallyFilled,
            RequestedQuantity: 0.01m,
            ExecutedQuantity: 0.004m,
            ExecutedPrice: 97.5m,
            FeePaid: 0.00039m);

        strategy.ApplyExecutionFeedback(partialFeedback);

        Assert.Equal(0.004m, strategy.CurrentInventory);
    }

    [Fact]
    public void F07StateCaptureAndRestoreRoundtripsAccurately()
    {
        var parameters = new AdaptiveGridParameters("grid-snapshot-test", "1.0", BtcUsdt, 0.02m, 5, 0.01m, 0.05m, 0.001m, 0.0005m);
        var strategy = new AdaptiveGridStrategy(parameters);
        strategy.GenerateSignal(new CloseBar(BtcUsdt, Start, 100m), 0);
        strategy.GenerateSignal(new CloseBar(BtcUsdt, Start.AddHours(1), 95m), 1);

        strategy.ApplyExecutionFeedback(new StrategyExecutionFeedback(
            1, 2, Start.AddHours(2), StrategyExecutionAction.BuyBaseQuantity, StrategyExecutionOutcome.Filled, 0.01m, 0.01m, 95m, 0.001m));

        var snapshot = strategy.CaptureState();
        Assert.Equal(0.01m, snapshot.CurrentInventory);
        Assert.Equal(-3, snapshot.ActiveLevel);

        // Create new strategy instance and restore
        var restored = new AdaptiveGridStrategy(parameters);
        restored.RestoreState(snapshot);

        Assert.Equal(0.01m, restored.CurrentInventory);
        Assert.Equal(-3, restored.ActiveGridLevel);
        Assert.Equal(100m, restored.AnchorPrice);
    }

    [Fact]
    public void F07EvaluateWithFeedbackRunsEndToEndDeterministically()
    {
        var parameters = new AdaptiveGridParameters("grid-loop", "1.0", BtcUsdt, 0.02m, 5, 0.01m, 0.05m, 0.001m, 0.0005m);
        var strategy = new AdaptiveGridStrategy(parameters);
        var bars = new[]
        {
            new CloseBar(BtcUsdt, Start, 100m),
            new CloseBar(BtcUsdt, Start.AddHours(1), 97.5m),
            new CloseBar(BtcUsdt, Start.AddHours(2), 95.5m),
            new CloseBar(BtcUsdt, Start.AddHours(3), 100m),
        };
        var options = new StrategyBacktestOptions(1_000m, 365, feeRate: 0.001m, halfSpreadRate: 0.0005m, slippageRate: 0.0005m);

        var result = StrategyBacktestEvaluator.EvaluateWithFeedback(
            "grid-loop", "1.0", "cfg-fingerprint", bars, strategy, options);

        Assert.NotNull(result);
        Assert.Equal(4, result.EquityCurve.Count);
        Assert.True(result.Metrics.FilledOrderCount > 0);
    }

    // ==========================================
    // F08: Conservative Research Execution Model
    // ==========================================

    [Fact]
    public void F08ResearchBarValidatesOhlcRelationshipAndUtc()
    {
        // Low > High should throw
        Assert.Throws<ArgumentException>(() => new ResearchBar(
            BtcUsdt, Start, Start.AddMinutes(1), 100m, 90m, 110m, 100m, 10m));

        // Non-UTC timestamp should throw
        var nonUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(-3));
        Assert.Throws<ArgumentException>(() => new ResearchBar(
            BtcUsdt, nonUtc, Start.AddMinutes(1), 100m, 105m, 95m, 100m, 10m));

        var validBar = new ResearchBar(BtcUsdt, Start, Start.AddMinutes(1), 100m, 105m, 95m, 102m, 50m);
        Assert.Equal(100m, validBar.OpenPrice.Value);
        Assert.Equal(105m, validBar.HighPrice.Value);
        Assert.Equal(95m, validBar.LowPrice.Value);
        Assert.Equal(102m, validBar.ClosePrice.Value);
        Assert.Equal(50m, validBar.Volume);
    }

    [Fact]
    public void F08ResearchExchangeFiltersQuantizesPriceAndQuantity()
    {
        var filters = new ResearchExchangeFilters(
            priceTickSize: 0.01m,
            quantityStepSize: 0.001m,
            minimumQuantity: 0.001m,
            maximumQuantity: 100m,
            minimumNotional: 5m);

        Assert.Equal(100.23m, filters.QuantizePrice(100.239m));
        Assert.Equal(0.015m, filters.QuantizeQuantity(0.0159m));
    }

    [Fact]
    public void F08ConservativeExecutionModelClampsIntrabarPricesAndEnforcesLatency()
    {
        var filters = new ResearchExchangeFilters(0.01m, 0.001m, 0.001m, 100m, 5m);
        var model = new ConservativeResearchExecutionModel(filters, latencyBars: 1, maxVolumeParticipationRate: 0.5m);

        var bars = new[]
        {
            new ResearchBar(BtcUsdt, Start, Start.AddHours(1), 100m, 105m, 95m, 100m, 1_000m),
            new ResearchBar(BtcUsdt, Start.AddHours(1), Start.AddHours(2), 100m, 110m, 98m, 108m, 1_000m),
        };

        var order = new ConservativeExecutionOrder(
            SubmissionBarIndex: 0,
            Action: StrategyExecutionAction.BuyBaseQuantity,
            RequestedQuantity: 0.1m);

        var result = model.SimulateFill(order, bars, availableCash: 1_000m, availablePosition: 0m);

        Assert.True(result.IsFilled);
        Assert.Equal(1, result.ExecutionBarIndex); // Executed at bar 1 due to 1-bar latency
        Assert.InRange(result.ExecutedPrice, 98m, 110m); // Clamped within intrabar [Low, High]
        Assert.Equal(0.1m, result.ExecutedQuantity);
    }

    [Fact]
    public void F08ConservativeExecutionModelCapsFillByVolumeParticipation()
    {
        var filters = new ResearchExchangeFilters(0.01m, 0.001m, 0.001m, 100m, 5m);
        // Max participation is 10%
        var model = new ConservativeResearchExecutionModel(filters, latencyBars: 1, maxVolumeParticipationRate: 0.10m);

        var bars = new[]
        {
            new ResearchBar(BtcUsdt, Start, Start.AddHours(1), 100m, 105m, 95m, 100m, 100m),
            new ResearchBar(BtcUsdt, Start.AddHours(1), Start.AddHours(2), 100m, 105m, 95m, 100m, 10m), // Bar 1 volume = 10 -> max 1.0 quantity
        };

        var order = new ConservativeExecutionOrder(
            SubmissionBarIndex: 0,
            Action: StrategyExecutionAction.BuyBaseQuantity,
            RequestedQuantity: 5.0m); // Wants 5.0

        var result = model.SimulateFill(order, bars, availableCash: 10_000m, availablePosition: 0m);

        Assert.True(result.IsFilled);
        Assert.Equal(StrategyExecutionOutcome.PartiallyFilled, result.Outcome);
        Assert.Equal(1.0m, result.ExecutedQuantity); // Capped at 10% of 10 volume
    }

    [Theory]
    [InlineData(StrategyExecutionAction.BuyBaseQuantity)]
    [InlineData(StrategyExecutionAction.SellBaseQuantity)]
    public void F08LimitOrdersNeverExecuteAtWorseThanTheirLimit(StrategyExecutionAction action)
    {
        var model = new ConservativeResearchExecutionModel(new ResearchExchangeFilters(0.01m, 0.001m, 0.001m, 100m, 5m));
        var bars = new[]
        {
            new ResearchBar(BtcUsdt, Start, Start.AddHours(1), 100m, 105m, 95m, 100m, 1_000m),
            new ResearchBar(BtcUsdt, Start.AddHours(1), Start.AddHours(2), 100m, 105m, 95m, 100m, 1_000m)
        };
        var order = new ConservativeExecutionOrder(0, action, 1m, LimitPrice: 100m);

        var result = model.SimulateFill(order, bars, availableCash: 10_000m, availablePosition: 10m);

        Assert.False(result.IsFilled);
        Assert.Equal(StrategyExecutionOutcome.NoTrade, result.Outcome);
        Assert.Equal(0m, result.ExecutedQuantity);
    }

    [Fact]
    public void F08SellLimitIsCheckedAfterPriceQuantization()
    {
        var model = new ConservativeResearchExecutionModel(
            new ResearchExchangeFilters(0.01m, 0.001m, 0.001m, 100m, 5m),
            halfSpreadRate: 0m, slippageRate: 0m);
        var bars = new[]
        {
            new ResearchBar(BtcUsdt, Start, Start.AddHours(1), 100m, 105m, 95m, 100m, 1_000m),
            new ResearchBar(BtcUsdt, Start.AddHours(1), Start.AddHours(2), 100m, 105m, 95m, 100m, 1_000m)
        };
        var result = model.SimulateFill(
            new ConservativeExecutionOrder(0, StrategyExecutionAction.SellBaseQuantity, 1m, LimitPrice: 100.005m),
            bars, availableCash: 10_000m, availablePosition: 10m);

        Assert.False(result.IsFilled);
        Assert.Equal(StrategyExecutionOutcome.NoTrade, result.Outcome);
    }

    // ==========================================
    // F09: Dynamic Costs & R$ 100 Budget Capacity Gate
    // ==========================================

    [Fact]
    public void F09DynamicExecutionCostModelScalesWithVolatilityAndIncludesFeeBuffer()
    {
        var costModel = new DynamicExecutionCostModel(
            baseHalfSpreadRate: 0.0005m,
            baseSlippageRate: 0.0005m,
            takerFeeRate: 0.0010m,
            feeReserveBufferRate: 0.0025m);

        Assert.Equal(0.0035m, costModel.TotalEffectiveFeeRate); // 10 bps taker + 25 bps reserve buffer = 35 bps

        var lowVolBar = new ResearchBar(BtcUsdt, Start, Start.AddHours(1), 100m, 100.5m, 99.5m, 100m, 1_000m);
        var highVolBar = new ResearchBar(BtcUsdt, Start, Start.AddHours(1), 100m, 110m, 90m, 100m, 1_000m);

        var lowVolCost = costModel.CalculateCosts(lowVolBar, 10m);
        var highVolCost = costModel.CalculateCosts(highVolBar, 10m);

        Assert.True(highVolCost.DynamicSpreadRate > lowVolCost.DynamicSpreadRate);
        Assert.True(highVolCost.DynamicSlippageRate > lowVolCost.DynamicSlippageRate);
    }

    [Fact]
    public void F09StrategyBudgetCapacityGateExcludesMultiLevelGridBelowMinNotional()
    {
        // Campaign budget: 20 USDT (~R$ 100 cap).
        // Multi-level grid with 10% per level slice -> 2.00 USDT per slice.
        // Exchange MIN_NOTIONAL: 5.00 USDT.
        var gridCapacity = StrategyBudgetCapacityGate.Evaluate(
            "adaptive-grid-btcusdt",
            totalBudgetQuote: 20m,
            minimumOrderNotionalQuote: 5m,
            budgetCeilingQuote: 20m,
            gridSliceQuoteFraction: 0.10m);

        Assert.False(gridCapacity.IsEligible);
        Assert.Contains("below minimum exchange notional", gridCapacity.ExclusionReason, StringComparison.OrdinalIgnoreCase);

        // Single allocation strategy (e.g. TrendFollowing) allocates available capital (20 USDT >= 5 USDT)
        var trendCapacity = StrategyBudgetCapacityGate.Evaluate(
            "trend-following-btcusdt",
            totalBudgetQuote: 20m,
            minimumOrderNotionalQuote: 5m,
            budgetCeilingQuote: 20m);

        Assert.True(trendCapacity.IsEligible);
        Assert.Null(trendCapacity.ExclusionReason);
    }

    [Fact]
    public void F09StrategyBudgetCapacityGateRejectsBudgetAboveTwentyUsdt()
    {
        var overBudget = StrategyBudgetCapacityGate.Evaluate(
            "trend-following-btcusdt",
            totalBudgetQuote: 25m,
            minimumOrderNotionalQuote: 5m,
            budgetCeilingQuote: 20m);

        Assert.False(overBudget.IsEligible);
        Assert.Contains("exceeds capacity ceiling", overBudget.ExclusionReason, StringComparison.OrdinalIgnoreCase);
    }

    // ==========================================
    // F10: Point-in-Time Universe & Delisting
    // ==========================================

    [Fact]
    public void F10PointInTimeUniverseFiltersAssetsBeforeListingAndAfterDelisting()
    {
        var assets = new[]
        {
            new PointInTimeAssetMetadata(BtcUsdt, Start), // Never delisted
            new PointInTimeAssetMetadata(EthUsdt, Start.AddDays(5), Start.AddDays(15), delistingLiquidationPrice: 50m),
        };
        var engine = new PointInTimeUniverseEngine(assets);

        // At day 2: ETH is not yet listed
        var snapDay2 = engine.GetUniverseAt(Start.AddDays(2));
        Assert.Contains(BtcUsdt, snapDay2.ActiveSymbols);
        Assert.Contains(EthUsdt, snapDay2.PendingListingSymbols);
        Assert.False(engine.IsActiveAt(EthUsdt, Start.AddDays(2)));

        // At day 10: ETH is active
        var snapDay10 = engine.GetUniverseAt(Start.AddDays(10));
        Assert.Contains(EthUsdt, snapDay10.ActiveSymbols);
        Assert.True(engine.IsActiveAt(EthUsdt, Start.AddDays(10)));

        // At day 20: ETH is delisted
        var snapDay20 = engine.GetUniverseAt(Start.AddDays(20));
        Assert.Contains(EthUsdt, snapDay20.DelistedSymbols);
        Assert.False(engine.IsActiveAt(EthUsdt, Start.AddDays(20)));
    }

    [Fact]
    public void F10PointInTimeUniverseEnforcesDelistingLiquidation()
    {
        var assets = new[]
        {
            new PointInTimeAssetMetadata(EthUsdt, Start, Start.AddDays(10), delistingLiquidationPrice: 42m),
        };
        var engine = new PointInTimeUniverseEngine(assets);

        // Holding 2.5 ETH on delisting day triggers forced liquidation
        var liquidation = engine.EvaluateForcedLiquidation(
            EthUsdt,
            currentBarTimeUtc: Start.AddDays(10),
            currentPosition: 2.5m,
            currentClosePrice: 45m);

        Assert.NotNull(liquidation);
        Assert.Equal(EthUsdt, liquidation.Symbol);
        Assert.Equal(42m, liquidation.LiquidationPrice);
        Assert.Equal(2.5m * 42m, liquidation.RealizedLiquidationNotional);
        Assert.Contains("delisting", liquidation.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
