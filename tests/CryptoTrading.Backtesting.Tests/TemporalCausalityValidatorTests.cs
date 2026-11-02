using System.Globalization;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Backtesting.Validation;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class TemporalCausalityValidatorTests
{
    private static readonly Symbol BtcUsdt = new("BTCUSDT");
    private static readonly DateTimeOffset StartTime = DateTimeOffset.Parse("2026-09-01T00:00:00Z", CultureInfo.InvariantCulture);

    private static List<CloseBar> GenerateSyntheticBars(int count)
    {
        var bars = new List<CloseBar>(count);
        var basePrice = 80_000m;
        for (var i = 0; i < count; i++)
        {
            var sin = (decimal)Math.Sin(i * 0.15);
            var cos = (decimal)Math.Cos(i * 0.05);
            var price = Math.Round(basePrice + (sin * 500m) + (cos * 300m) + (i * 10m), 2);
            var high = price + 50m;
            var low = price - 50m;
            var open = price - 10m;
            var volume = 100m + (Math.Abs(sin) * 50m);
            var takerBuy = volume * 0.55m;

            bars.Add(new CloseBar(
                BtcUsdt,
                StartTime.AddMinutes(i + 1),
                price,
                openPrice: open,
                highPrice: high,
                lowPrice: low,
                volume: volume,
                takerBuyBaseVolume: takerBuy,
                quoteVolume: price * volume,
                numberOfTrades: 100));
        }

        return bars;
    }

    [Fact]
    public void MeanReversionStrategyIsLookaheadFree()
    {
        var bars = GenerateSyntheticBars(80);
        var strategy = new MeanReversionStrategy(new MeanReversionParameters(
            "mean-reversion-test", "1.0.0", BtcUsdt, period: 20, entryZScoreThreshold: -1.5m, exitZScoreThreshold: 0.0m, 0.05m, 0.001m, 0.0005m));

        var report = TemporalCausalityValidator.ValidateStrategyLookahead(
            bars,
            b => strategy.Evaluate(b),
            d => $"{d.Signal}:{d.Reason}:{d.ZScore:F4}",
            minWarmupBars: 25,
            componentId: "MeanReversion");

        Assert.True(report.IsCausallyInvariant);
        Assert.Equal(0, report.LookaheadViolationsCount);
        Assert.True(report.TotalTestedPoints > 50);
    }

    [Fact]
    public void BreakoutStrategyIsLookaheadFree()
    {
        var bars = GenerateSyntheticBars(80);
        var strategy = new BreakoutStrategy(new BreakoutParameters(
            "breakout-test", "1.0.0", BtcUsdt, entryPeriod: 20, exitPeriod: 10, 0.001m, 0.0005m));

        var report = TemporalCausalityValidator.ValidateStrategyLookahead(
            bars,
            b => strategy.Evaluate(b),
            d => $"{d.Signal}:{d.Reason}",
            minWarmupBars: 25,
            componentId: "Breakout");

        Assert.True(report.IsCausallyInvariant);
        Assert.Equal(0, report.LookaheadViolationsCount);
    }

    [Fact]
    public void TrendFollowingStrategyIsLookaheadFree()
    {
        var bars = GenerateSyntheticBars(80);
        var strategy = new TrendFollowingStrategy(new TrendFollowingParameters(
            "trend-test", "1.0.0", BtcUsdt, fastPeriod: 9, slowPeriod: 21, 0.001m, 0.0005m));

        var report = TemporalCausalityValidator.ValidateStrategyLookahead(
            bars,
            b => strategy.Evaluate(b),
            d => $"{d.Signal}:{d.Reason}",
            minWarmupBars: 25,
            componentId: "TrendFollowing");

        Assert.True(report.IsCausallyInvariant);
        Assert.Equal(0, report.LookaheadViolationsCount);
    }

    [Fact]
    public void VolatilitySqueezeStrategyIsLookaheadFree()
    {
        var bars = GenerateSyntheticBars(80);
        var strategy = new VolatilitySqueezeStrategy(new VolatilitySqueezeParameters(
            "squeeze-test", "1.0.0", BtcUsdt, period: 20, bollingerMultiplier: 2.0m, keltnerMultiplier: 1.5m, 0.001m, 0.0005m));

        var report = TemporalCausalityValidator.ValidateStrategyLookahead(
            bars,
            b => strategy.Evaluate(b),
            d => $"{d.Signal}:{d.Reason}",
            minWarmupBars: 25,
            componentId: "VolatilitySqueeze");

        Assert.True(report.IsCausallyInvariant);
        Assert.Equal(0, report.LookaheadViolationsCount);
    }

    [Fact]
    public void DetectsIntentionalLookaheadBiasInFlawedEvaluator()
    {
        var bars = GenerateSyntheticBars(50);

        // Flawed evaluator that peeks at future bar (index + 1) when evaluating decisions for the series
        IReadOnlyList<string> FlawedEvaluator(IReadOnlyList<CloseBar> slice)
        {
            var result = new string[slice.Count];
            for (var i = 0; i < slice.Count; i++)
            {
                // Leaks future: if next bar exists in slice, check if price rises next bar
                var nextBarIsHigher = (i + 1 < slice.Count) && slice[i + 1].ClosePrice.Value > slice[i].ClosePrice.Value;
                result[i] = nextBarIsHigher ? "FUTURE_UP" : "FLAT";
            }
            return result;
        }

        var report = TemporalCausalityValidator.ValidateStrategyLookahead(
            bars,
            FlawedEvaluator,
            d => d,
            minWarmupBars: 10,
            componentId: "CheatingFuturePeekEvaluator");

        Assert.False(report.IsCausallyInvariant);
        Assert.True(report.LookaheadViolationsCount > 0);
        Assert.NotEmpty(report.Violations);
    }

    [Fact]
    public void TechnicalIndicatorWarmupIsStableAcrossVaryingHistoryLengths()
    {
        var bars = GenerateSyntheticBars(140);

        var report = TemporalCausalityValidator.ValidateIndicatorWarmupStability(
            bars,
            shortWarmupBars: 70,
            longWarmupBars: 130,
            tolerancePercent: 2.0m);

        Assert.True(report.IsStable, string.Join("; ", report.Metrics.Select(m => $"{m.IndicatorName}: short={m.ShortWarmupValue}, long={m.LongWarmupValue}, diff={m.RelativeDifferencePercent}%")));
        Assert.NotEmpty(report.Metrics);
        Assert.All(report.Metrics, m => Assert.True(m.IsAcceptable, $"{m.IndicatorName} relative difference {m.RelativeDifferencePercent}% exceeded tolerance"));
    }

    [Fact]
    public void ValidateStrategyLookaheadRejectsNonPositiveStep()
    {
        var bars = GenerateSyntheticBars(30);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TemporalCausalityValidator.ValidateStrategyLookahead(
                bars,
                slice => slice.Select(_ => "HOLD").ToList(),
                d => d,
                step: 0));
    }

    [Fact]
    public void ValidateIndicatorWarmupStabilityRejectsInvertedWarmupLengths()
    {
        var bars = GenerateSyntheticBars(50);
        Assert.Throws<ArgumentException>(() =>
            TemporalCausalityValidator.ValidateIndicatorWarmupStability(
                bars,
                shortWarmupBars: 40,
                longWarmupBars: 35));
    }
}
