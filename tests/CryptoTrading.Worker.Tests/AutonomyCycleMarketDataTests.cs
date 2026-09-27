using System.Globalization;
using CryptoTrading.Backtesting.Autonomy;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Exchange.Binance;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class AutonomyCycleMarketDataTests
{
    [Fact]
    public void RestKlinesPreserveClosedBarAvailabilityForFeatureSnapshot()
    {
        var open = DateTimeOffset.Parse("2026-09-20T12:00:00Z", CultureInfo.InvariantCulture);
        var now = open.AddMinutes(3);
        var mapped = AutonomyCycleHostedService.ValidateAndMapKlines(
            [Kline(open, 100m), Kline(open.AddMinutes(1), 101m), Kline(open.AddMinutes(2), 102m), Kline(open.AddMinutes(3), 103m)],
            "BTCUSDT",
            "1m",
            TimeSpan.FromMinutes(5),
            now,
            previousCloseUtc: null);

        Assert.Equal(3, mapped.Count);
        Assert.Equal(0, mapped[0].Sequence);
        Assert.Equal(1, mapped[1].Sequence);
        Assert.All(mapped, envelope => Assert.Equal(now, envelope.AvailableAtUtc));
        Assert.Equal(open.AddMinutes(1), mapped[0].Bar.CloseTimeUtc);
        Assert.Equal(open.AddMinutes(2), mapped[1].Bar.CloseTimeUtc);
        Assert.Equal(open.AddMinutes(3), mapped[2].Bar.CloseTimeUtc);

        var snapshot = BaselineFeatureSnapshot.Create(
            mapped,
            new TrendFollowingParameters("worker-parity", "1.0.0", new Symbol("BTCUSDT"), 2, 3, 0m, 0m));
        Assert.Equal(now, snapshot.AvailableAtUtc);
        Assert.Equal(mapped[^1].Bar.CloseTimeUtc, snapshot.Decision.CloseTimeUtc);
    }

    [Fact]
    public void RestKlinesSnapshotWith100BarsDoesNotRejectHistoricalBarsAsStale()
    {
        var start = DateTimeOffset.Parse("2026-09-20T10:00:00Z", CultureInfo.InvariantCulture);
        var klines = new List<BinanceSpotKline>(100);
        for (var i = 0; i < 100; i++)
        {
            klines.Add(Kline(start.AddMinutes(i), 100m + i));
        }

        // Now is 30 seconds after the 100th bar closed (which is start + 100m)
        var now = start.AddMinutes(100).AddSeconds(30);
        var mapped = AutonomyCycleHostedService.ValidateAndMapKlines(
            klines,
            "BTCUSDT",
            "1m",
            TimeSpan.FromMinutes(2),
            now,
            previousCloseUtc: null);

        Assert.Equal(100, mapped.Count);
        Assert.Equal(0, mapped[0].Sequence);
        Assert.Equal(99, mapped[99].Sequence);
        Assert.Equal(start.AddMinutes(1), mapped[0].Bar.CloseTimeUtc);
        Assert.Equal(start.AddMinutes(100), mapped[99].Bar.CloseTimeUtc);
    }

    [Fact]
    public void RestKlinesSnapshotRejectsWhenLatestBarIsStale()
    {
        var start = DateTimeOffset.Parse("2026-09-20T10:00:00Z", CultureInfo.InvariantCulture);
        var klines = new List<BinanceSpotKline>
        {
            Kline(start, 100m),
            Kline(start.AddMinutes(1), 101m)
        };

        // Latest bar closed at start + 2m. Now is 10 minutes later, staleness threshold is 2m.
        var now = start.AddMinutes(12);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AutonomyCycleHostedService.ValidateAndMapKlines(
                klines,
                "BTCUSDT",
                "1m",
                TimeSpan.FromMinutes(2),
                now,
                previousCloseUtc: null));

        Assert.Contains("Stale", ex.Message, StringComparison.Ordinal);
    }

    private static BinanceSpotKline Kline(DateTimeOffset open, decimal close) => new(
        open,
        open.AddMinutes(1).AddMilliseconds(-1),
        close,
        close,
        close,
        close,
        1m,
        "1m");
}
