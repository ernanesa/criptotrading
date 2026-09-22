using System.Globalization;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Regimes;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class MarketRegimeClassifierTests
{
    private static readonly Symbol BtcUsdt = new("BTCUSDT");
    private static readonly DateTimeOffset TestTime = DateTimeOffset.Parse("2026-09-21T12:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void InsufficientDataReturnsIlliquidOrStale()
    {
        var snapshot = MarketRegimeClassifier.Classify(BtcUsdt, [100m, 101m, 102m], TestTime);

        Assert.Equal(MarketRegime.IlliquidOrStale, snapshot.Regime);
        Assert.Equal(0.0m, snapshot.Confidence);
        Assert.True(snapshot.ProhibitsNewExposure);
    }

    [Fact]
    public void UpwardLinearSlopeIdentifiesBullTrend()
    {
        // Upward slope with very low variance: 100, 101, 102, 103, 104, 105
        var prices = new decimal[] { 100m, 101m, 102m, 103m, 104m, 105m };
        var snapshot = MarketRegimeClassifier.Classify(BtcUsdt, prices, TestTime);

        Assert.Equal(MarketRegime.BullTrend, snapshot.Regime);
        Assert.True(snapshot.FavorsTrendFollowing);
        Assert.False(snapshot.ProhibitsNewExposure);
        Assert.True(snapshot.Confidence >= 0.70m);
    }

    [Fact]
    public void DownwardLinearSlopeIdentifiesBearTrend()
    {
        // Downward slope with low variance: 105, 104, 103, 102, 101, 100
        var prices = new decimal[] { 105m, 104m, 103m, 102m, 101m, 100m };
        var snapshot = MarketRegimeClassifier.Classify(BtcUsdt, prices, TestTime);

        Assert.Equal(MarketRegime.BearTrend, snapshot.Regime);
        Assert.True(snapshot.FavorsTrendFollowing);
        Assert.False(snapshot.ProhibitsNewExposure);
    }

    [Fact]
    public void OscillatingFlatPricesIdentifyMeanRevertingRange()
    {
        // Flat oscillation around 100 with modest variance: 100, 100.2, 99.8, 100.1, 99.9, 100.0
        var prices = new decimal[] { 100m, 100.2m, 99.8m, 100.1m, 99.9m, 100.0m };
        var snapshot = MarketRegimeClassifier.Classify(BtcUsdt, prices, TestTime);

        Assert.Equal(MarketRegime.MeanRevertingRange, snapshot.Regime);
        Assert.True(snapshot.FavorsMeanReversion);
        Assert.False(snapshot.ProhibitsNewExposure);
    }

    [Fact]
    public void ExcessiveVolatilityIdentifiesHighVolatilityChopAndProhibitsExposure()
    {
        // Wild swings: 100, 150, 70, 160, 60 (StdDev > 40 on mean ~108, volatility > 35%)
        var prices = new decimal[] { 100m, 150m, 70m, 160m, 60m };
        var snapshot = MarketRegimeClassifier.Classify(BtcUsdt, prices, TestTime);

        Assert.Equal(MarketRegime.HighVolatilityChop, snapshot.Regime);
        Assert.True(snapshot.ProhibitsNewExposure);
        Assert.False(snapshot.FavorsTrendFollowing);
        Assert.False(snapshot.FavorsMeanReversion);
    }
}
