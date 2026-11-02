using System.Globalization;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class RealisticFillModelTests
{
    private static readonly Symbol BtcUsdt = new("BTCUSDT");
    private static readonly DateTimeOffset TestTime = DateTimeOffset.Parse("2026-09-01T00:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void RejectsBuyWhenLowPriceIsAboveReferencePrice()
    {
        var parameters = new RealisticFillModelParameters(enforceIntraBarPriceRange: true);
        var result = RealisticFillModel.EvaluateBuyFill(
            referencePrice: 80_000m,
            highPrice: 81_000m,
            lowPrice: 80_100m, // Entire bar traded above reference price
            barVolume: 100m,
            barQuoteVolume: 8_000_000m,
            baseSlippageRate: 0.0005m,
            requestedQuantity: 0.1m,
            parameters);

        Assert.False(result.IsFillable);
        Assert.Equal("INTRA_BAR_LOW_ABOVE_REFERENCE_PRICE", result.RejectionOrAdjustmentReason);
    }

    [Fact]
    public void RejectsSellWhenHighPriceIsBelowReferencePrice()
    {
        var parameters = new RealisticFillModelParameters(enforceIntraBarPriceRange: true);
        var result = RealisticFillModel.EvaluateSellFill(
            referencePrice: 80_000m,
            highPrice: 79_900m, // Entire bar traded below reference price
            lowPrice: 79_000m,
            barVolume: 100m,
            barQuoteVolume: 8_000_000m,
            baseSlippageRate: 0.0005m,
            requestedQuantity: 0.1m,
            parameters);

        Assert.False(result.IsFillable);
        Assert.Equal("INTRA_BAR_HIGH_BELOW_REFERENCE_PRICE", result.RejectionOrAdjustmentReason);
    }

    [Fact]
    public void CapsExecutionQuantityAtMaxParticipationRate()
    {
        var parameters = new RealisticFillModelParameters(
            enforceIntraBarPriceRange: false,
            modelVolumeParticipationSlippage: false,
            maxParticipationRate: 0.05m); // Max 5% of bar volume

        var barVolume = 10m; // 5% cap is 0.50m
        var result = RealisticFillModel.EvaluateBuyFill(
            referencePrice: 80_000m,
            highPrice: 80_500m,
            lowPrice: 79_500m,
            barVolume: barVolume,
            barQuoteVolume: 800_000m,
            baseSlippageRate: 0.0005m,
            requestedQuantity: 2.0m, // Requesting 20% of bar volume
            parameters);

        Assert.True(result.IsFillable);
        Assert.True(result.IsParticipationCapped);
        Assert.Equal(0.50m, result.MaximumExecutableQuantity);
        Assert.Equal("PARTICIPATION_RATE_CAPPED", result.RejectionOrAdjustmentReason);
    }

    [Fact]
    public void IncreasesSlippageDynamicallyWithVolumeParticipation()
    {
        var parameters = new RealisticFillModelParameters(
            enforceIntraBarPriceRange: false,
            modelVolumeParticipationSlippage: true,
            amihudImpactFactor: 0.20m,
            maxParticipationRate: 0.50m);

        var smallOrder = RealisticFillModel.EvaluateBuyFill(
            referencePrice: 100m,
            highPrice: 105m,
            lowPrice: 95m,
            barVolume: 1_000m,
            barQuoteVolume: 100_000m,
            baseSlippageRate: 0.0005m,
            requestedQuantity: 1m, // 0.1% participation
            parameters);

        var largeOrder = RealisticFillModel.EvaluateBuyFill(
            referencePrice: 100m,
            highPrice: 105m,
            lowPrice: 95m,
            barVolume: 1_000m,
            barQuoteVolume: 100_000m,
            baseSlippageRate: 0.0005m,
            requestedQuantity: 200m, // 20% participation
            parameters);

        Assert.True(largeOrder.EffectiveSlippageRate > smallOrder.EffectiveSlippageRate);
        Assert.True(largeOrder.EffectivePrice > smallOrder.EffectivePrice);
    }

    [Fact]
    public void StrategyBacktestEvaluatorAppliesRealisticFillsToIncreaseExecutionCosts()
    {
        // Construct 10 bars with a valid buy dip and subsequent sell rally
        var bars = new List<CloseBar>();
        for (var i = 0; i < 15; i++)
        {
            var price = 100m + (i == 3 ? -2m : (i > 5 ? 5m : 0m));
            bars.Add(new CloseBar(
                BtcUsdt,
                TestTime.AddMinutes(i + 1),
                price,
                openPrice: price,
                highPrice: price + 1m,
                lowPrice: price - 1m,
                volume: 50m, // thin volume to induce participation impact
                takerBuyBaseVolume: 25m,
                quoteVolume: price * 50m,
                numberOfTrades: 10));
        }

        var signals = new List<StrategyExecutionSignal>();
        for (var i = 0; i < bars.Count; i++)
        {
            var action = i == 2 ? StrategyExecutionAction.EnterLong
                : (i == 7 ? StrategyExecutionAction.ExitLong : StrategyExecutionAction.NoTrade);
            signals.Add(new StrategyExecutionSignal(i, action, reasonCode: action.ToString()));
        }

        var standardOptions = new StrategyBacktestOptions(
            initialCapital: 400m,
            periodsPerYear: 525_600,
            feeRate: 0.001m,
            halfSpreadRate: 0.0005m,
            slippageRate: 0.0005m);

        var realisticOptions = new StrategyBacktestOptions(
            initialCapital: 400m,
            periodsPerYear: 525_600,
            feeRate: 0.001m,
            halfSpreadRate: 0.0005m,
            slippageRate: 0.0005m,
            realisticFills: new RealisticFillModelParameters(
                enforceIntraBarPriceRange: true,
                modelVolumeParticipationSlippage: true,
                amihudImpactFactor: 0.15m));

        var standardResult = StrategyBacktestEvaluator.Evaluate(
            "test-strat", "1.0", "fp", bars, signals, standardOptions);
        var realisticResult = StrategyBacktestEvaluator.Evaluate(
            "test-strat", "1.0", "fp", bars, signals, realisticOptions);

        Assert.NotEmpty(standardResult.Trades);
        Assert.NotEmpty(realisticResult.Trades);
        Assert.True(realisticResult.Metrics.TotalSlippageCost > standardResult.Metrics.TotalSlippageCost);
    }

    [Fact]
    public void StrategyBacktestEvaluatorEnforcesVolumeParticipationCapOnBuy()
    {
        var bars = new List<CloseBar>();
        for (var i = 0; i < 15; i++)
        {
            var price = 100m;
            bars.Add(new CloseBar(
                BtcUsdt,
                TestTime.AddMinutes(i + 1),
                price,
                openPrice: price,
                highPrice: price + 1m,
                lowPrice: price - 1m,
                volume: 50m,
                takerBuyBaseVolume: 25m,
                quoteVolume: price * 50m,
                numberOfTrades: 10));
        }

        var signals = new List<StrategyExecutionSignal>();
        for (var i = 0; i < bars.Count; i++)
        {
            var action = i == 2 ? StrategyExecutionAction.EnterLong
                : (i == 7 ? StrategyExecutionAction.ExitLong : StrategyExecutionAction.NoTrade);
            signals.Add(new StrategyExecutionSignal(i, action, reasonCode: action.ToString()));
        }

        var options = new StrategyBacktestOptions(
            initialCapital: 100_000m,
            periodsPerYear: 525_600,
            feeRate: 0.001m,
            halfSpreadRate: 0.0005m,
            slippageRate: 0.0005m,
            realisticFills: new RealisticFillModelParameters(
                enforceIntraBarPriceRange: true,
                maxParticipationRate: 0.10m)); // 10% of 50 = 5.0 units max

        var result = StrategyBacktestEvaluator.Evaluate("test-strat", "1.0", "fp", bars, signals, options);
        Assert.NotEmpty(result.Trades);
        var entryTrade = result.Trades[0];
        Assert.Equal(5.0m, entryTrade.BaseQuantity);
    }

    [Fact]
    public void EvaluateBuyFillFailsClosedOnZeroVolumeBarWhenParticipationRateConfigured()
    {
        var parameters = new RealisticFillModelParameters(maxParticipationRate: 0.10m);
        var result = RealisticFillModel.EvaluateBuyFill(
            referencePrice: 100m,
            highPrice: 105m,
            lowPrice: 95m,
            barVolume: 0m,
            barQuoteVolume: 0m,
            baseSlippageRate: 0.0005m,
            requestedQuantity: 10m,
            parameters);

        Assert.False(result.IsFillable);
        Assert.Equal("INSUFFICIENT_BAR_VOLUME", result.RejectionOrAdjustmentReason);
    }
}
