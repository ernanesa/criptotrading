using System.Globalization;
using CryptoTrading.Backtesting;
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

    [Fact]
    public void RestKlinesSnapshotAllowsResumeWhenCheckpointIsOlderThanSnapshotWindow()
    {
        var start = DateTimeOffset.Parse("2026-09-20T12:00:00Z", CultureInfo.InvariantCulture);
        var klines = new List<BinanceSpotKline>(25);
        for (var i = 0; i < 25; i++)
        {
            klines.Add(Kline(start.AddMinutes(i), 100m + i));
        }

        var now = start.AddMinutes(25).AddSeconds(10);
        // Checkpoint from 2 hours prior to the snapshot window
        var oldCheckpoint = start.AddHours(-2);

        var mapped = AutonomyCycleHostedService.ValidateAndMapKlines(
            klines,
            "BTCUSDT",
            "1m",
            TimeSpan.FromMinutes(2),
            now,
            previousCloseUtc: oldCheckpoint);

        Assert.Equal(25, mapped.Count);
        Assert.Equal(start.AddMinutes(1), mapped[0].Bar.CloseTimeUtc);
        Assert.Equal(start.AddMinutes(25), mapped[^1].Bar.CloseTimeUtc);
    }

    [Fact]
    public void IntrabarSyntheticBarMaintainsMonotonicAvailabilityAndIncreasingCloseTime()
    {
        var open = DateTimeOffset.Parse("2026-09-20T12:00:00Z", CultureInfo.InvariantCulture);
        var now = open.AddMinutes(3).AddSeconds(20);
        var mapped = AutonomyCycleHostedService.ValidateAndMapKlines(
            [Kline(open, 100m), Kline(open.AddMinutes(1), 101m), Kline(open.AddMinutes(2), 102m), Kline(open.AddMinutes(3), 103m)],
            "BTCUSDT",
            "1m",
            TimeSpan.FromMinutes(5),
            now,
            previousCloseUtc: null);

        // Simulate intrabar ticker observed 10 seconds before 'now', inside the 4th candle
        var tickerObservedAtUtc = now.AddSeconds(-10);
        var intrabarCloseUtc = DateTimeOffset.FromUnixTimeMilliseconds(tickerObservedAtUtc.ToUnixTimeMilliseconds());

        var intrabarAvailableAtUtc = now >= mapped[^1].AvailableAtUtc ? now : mapped[^1].AvailableAtUtc;
        if (intrabarCloseUtc <= mapped[^1].Bar.CloseTimeUtc)
        {
            intrabarCloseUtc = mapped[^1].Bar.CloseTimeUtc.AddMilliseconds(1);
        }

        var syntheticEnvelope = new ClosedBarEnvelope(
            mapped.Count,
            new CloseBar(new Symbol("BTCUSDT"), intrabarCloseUtc, 104.5m),
            intrabarAvailableAtUtc);

        var listWithIntrabar = new List<ClosedBarEnvelope>(mapped) { syntheticEnvelope };

        // Must succeed without throwing "Closed-bar availability times must be monotonic."
        var snapshot = BaselineFeatureSnapshot.Create(
            listWithIntrabar,
            new TrendFollowingParameters("worker-parity", "1.0.0", new Symbol("BTCUSDT"), 2, 3, 0m, 0m));

        Assert.Equal(intrabarAvailableAtUtc, snapshot.AvailableAtUtc);
        Assert.Equal(intrabarCloseUtc, snapshot.Decision.CloseTimeUtc);
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
