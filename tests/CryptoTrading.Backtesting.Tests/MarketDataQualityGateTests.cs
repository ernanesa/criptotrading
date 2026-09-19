using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class MarketDataQualityGateTests
{
    [Fact]
    public void AcceptsClosedBarsOnlyAfterTheyBecomeAvailable()
    {
        var dataset = SyntheticMarketDataset.CreateBtcUsdtV1();
        var records = new[]
        {
            Envelope(0, 100m, DateTimeOffset.UnixEpoch.AddMinutes(1)),
            Envelope(1, 101m, DateTimeOffset.UnixEpoch.AddMinutes(2)),
        };

        var result = MarketDataQualityGate.ValidateClosedBars(dataset.Metadata, records, "btcusdt");

        Assert.Equal("synthetic-btcusdt-oos", result.DatasetId);
        Assert.Equal(2, result.BarCount);
        Assert.Equal(records[0].Bar.CloseTimeUtc, result.FirstBarUtc);
        Assert.Equal(records[1].Bar.CloseTimeUtc, result.LastBarUtc);
        Assert.Equal(64, result.ReplayFingerprint.Length);
    }

    [Fact]
    public void RejectsLookAheadWhenBarClosesAfterAvailability()
    {
        var dataset = SyntheticMarketDataset.CreateBtcUsdtV1();
        var records = new[]
        {
            Envelope(0, 100m, DateTimeOffset.UnixEpoch.AddSeconds(-1)),
        };

        var exception = Assert.Throws<InvalidDataException>(() =>
            MarketDataQualityGate.ValidateClosedBars(dataset.Metadata, records, "BTCUSDT"));

        Assert.Contains("used before availability", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsDuplicateSequenceAndNonMonotonicBarTime()
    {
        var dataset = SyntheticMarketDataset.CreateBtcUsdtV1();
        var duplicateSequence = new[]
        {
            Envelope(0, 100m, DateTimeOffset.UnixEpoch.AddMinutes(1)),
            Envelope(0, 101m, DateTimeOffset.UnixEpoch.AddMinutes(2)),
        };
        var nonMonotonicTime = new[]
        {
            Envelope(0, 100m, DateTimeOffset.UnixEpoch.AddMinutes(2)),
            Envelope(1, 101m, DateTimeOffset.UnixEpoch.AddMinutes(1)),
        };

        Assert.Throws<InvalidDataException>(() =>
            MarketDataQualityGate.ValidateClosedBars(dataset.Metadata, duplicateSequence, "BTCUSDT"));
        Assert.Throws<InvalidDataException>(() =>
            MarketDataQualityGate.ValidateClosedBars(dataset.Metadata, nonMonotonicTime, "BTCUSDT"));
    }

    private static ClosedBarEnvelope Envelope(long sequence, decimal price, DateTimeOffset availableAtUtc) =>
        new(
            sequence,
            new CloseBar(
                new Symbol("BTCUSDT"),
                DateTimeOffset.UnixEpoch.AddMinutes(sequence + 1),
                price),
            availableAtUtc);
}
