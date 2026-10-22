using System;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class DynamicExitThresholdsTests
{
    [Fact]
    public void DefaultThresholdsHasExpectedFallbackValues()
    {
        var defaults = DynamicExitThresholds.Default;

        Assert.Equal(180.0m, defaults.TargetProfitBps);
        Assert.Equal(120.0m, defaults.ChandelierActivationBps);
        Assert.Equal(50.0m, defaults.ChandelierPullbackBps);
        Assert.Equal(40.0m, defaults.MinProfitLockBps);
        Assert.Equal(120.0m, defaults.StopLossBps);
    }

    [Fact]
    public void CalculateLowVolatilityAssetScalesToMinimumFloors()
    {
        // 15 bps ATR (low-beta asset in tight consolidation)
        var thresholds = DynamicExitThresholds.Calculate(effectiveVolBps: 15.0m, roundTripFeeBps: 15.0m);

        Assert.Equal(180.0m, thresholds.TargetProfitBps); // Floor at 180 bps
        Assert.Equal(75.0m, thresholds.ChandelierActivationBps); // roundTripFee + 60
        Assert.Equal(35.0m, thresholds.ChandelierPullbackBps); // Floor at 35 bps
        Assert.Equal(40.0m, thresholds.MinProfitLockBps); // Floor at 40 bps
        Assert.Equal(100.0m, thresholds.StopLossBps); // Floor at 100 bps
    }

    [Fact]
    public void CalculateMediumVolatilityAssetExpandsTargetAndTrailingPullback()
    {
        // 60 bps ATR (typical active altcoin like SUI or DOGE)
        var thresholds = DynamicExitThresholds.Calculate(effectiveVolBps: 60.0m, roundTripFeeBps: 15.0m);

        Assert.Equal(210.0m, thresholds.TargetProfitBps); // 60 * 3.5 = 210 bps
        Assert.Equal(120.0m, thresholds.ChandelierActivationBps); // 60 * 2.0 = 120 bps
        Assert.Equal(90.0m, thresholds.ChandelierPullbackBps); // 60 * 1.5 = 90 bps
        Assert.Equal(40.0m, thresholds.MinProfitLockBps); // 40 bps
        Assert.Equal(108.0m, thresholds.StopLossBps); // 60 * 1.8 = 108 bps
    }

    [Fact]
    public void CalculateHighVolatilityAssetClampsAtCeilings()
    {
        // 160 bps ATR (extreme volatility breakout)
        var thresholds = DynamicExitThresholds.Calculate(effectiveVolBps: 160.0m, roundTripFeeBps: 15.0m);

        Assert.Equal(450.0m, thresholds.TargetProfitBps); // Clamped to 450 bps ceiling
        Assert.Equal(320.0m, thresholds.ChandelierActivationBps); // 160 * 2.0 = 320 bps (< 450 - 20)
        Assert.Equal(100.0m, thresholds.ChandelierPullbackBps); // Clamped to 100 bps ceiling
        Assert.Equal(40.0m, thresholds.MinProfitLockBps); // 40 bps
        Assert.Equal(250.0m, thresholds.StopLossBps); // Clamped to 250 bps ceiling
    }

    [Fact]
    public void CalculateHigherCommissionRateExpandsActivationAndProfitLock()
    {
        // 30 bps round-trip fee
        var thresholds = DynamicExitThresholds.Calculate(effectiveVolBps: 20.0m, roundTripFeeBps: 30.0m);

        Assert.Equal(180.0m, thresholds.TargetProfitBps);
        Assert.Equal(90.0m, thresholds.ChandelierActivationBps); // 30 + 60 = 90 bps
        Assert.Equal(35.0m, thresholds.ChandelierPullbackBps);
        Assert.Equal(45.0m, thresholds.MinProfitLockBps); // 30 + 15 = 45 bps
        Assert.Equal(100.0m, thresholds.StopLossBps);
    }

    [Fact]
    public void CalculateExtremeVolatilityClampsChandelierActivationBelowTargetProfit()
    {
        // 250 bps ATR (extreme flash spike): Target profit is 450 bps ceiling
        var thresholds = DynamicExitThresholds.Calculate(effectiveVolBps: 250.0m, roundTripFeeBps: 15.0m);

        Assert.Equal(450.0m, thresholds.TargetProfitBps);
        Assert.Equal(250.0m, thresholds.StopLossBps);
        // Activation must not exceed target profit - 20 bps so chandelier trailing stop triggers before TP
        Assert.True(thresholds.ChandelierActivationBps <= thresholds.TargetProfitBps - 20.0m);
        Assert.Equal(430.0m, thresholds.ChandelierActivationBps);
        Assert.Equal(100.0m, thresholds.ChandelierPullbackBps);
    }
}
