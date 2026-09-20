using System.Globalization;
using Xunit;

namespace CryptoTrading.Exchange.Binance.Tests;

public sealed class BinanceClosedKlineGuardTests
{
    [Fact]
    public void AdmitsOnlyClosedBarsAndNormalizesInclusiveCloseTime()
    {
        var guard = new BinanceClosedKlineGuard("BTCUSDT", "1m", TimeSpan.FromMinutes(3));
        var open = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);
        var openEvent = Event(open, isClosed: false);
        var closedEvent = Event(open, isClosed: true);

        Assert.Equal(BinanceKlineRejectionReason.NotClosed, guard.Evaluate(openEvent, open.AddMinutes(1)).RejectionReason);
        var decision = guard.Evaluate(closedEvent, open.AddMinutes(1));

        Assert.True(decision.IsAccepted);
        Assert.Equal(open.AddMinutes(1), decision.Kline!.CloseTimeUtc);
        Assert.Equal(open.AddMinutes(1), decision.Kline.AvailableAtUtc);
    }

    [Fact]
    public void RejectsDuplicatesAndEntersResyncOnGapUntilReset()
    {
        var guard = new BinanceClosedKlineGuard("BTCUSDT", "1m", TimeSpan.FromMinutes(3));
        var firstOpen = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);
        var first = guard.Evaluate(Event(firstOpen, true), firstOpen.AddMinutes(1));
        var duplicate = guard.Evaluate(Event(firstOpen, true), firstOpen.AddMinutes(1));
        var gap = guard.Evaluate(Event(firstOpen.AddMinutes(2), true), firstOpen.AddMinutes(3));
        var blocked = guard.Evaluate(Event(firstOpen.AddMinutes(3), true), firstOpen.AddMinutes(4));

        Assert.True(first.IsAccepted);
        Assert.Equal(BinanceKlineRejectionReason.Duplicate, duplicate.RejectionReason);
        Assert.Equal(BinanceKlineRejectionReason.GapDetected, gap.RejectionReason);
        Assert.Equal(BinanceKlineRejectionReason.ResyncRequired, blocked.RejectionReason);
        Assert.True(guard.ResyncRequired);

        guard.Reset(firstOpen.AddMinutes(2));
        Assert.True(guard.Evaluate(Event(firstOpen.AddMinutes(3), true), firstOpen.AddMinutes(4)).IsAccepted);
    }

    [Fact]
    public void RejectsStaleAndInvalidBars()
    {
        var guard = new BinanceClosedKlineGuard("BTCUSDT", "1m", TimeSpan.FromMinutes(2));
        var open = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);

        Assert.Equal(
            BinanceKlineRejectionReason.Stale,
            guard.Evaluate(Event(open, true), open.AddMinutes(5)).RejectionReason);

        var invalid = Event(open.AddMinutes(1), true) with { High = 0m };
        Assert.Equal(
            BinanceKlineRejectionReason.Invalid,
            guard.Evaluate(invalid, open.AddMinutes(2)).RejectionReason);
    }

    [Fact]
    public void AppliesTheSameContractToRestKlines()
    {
        var guard = new BinanceClosedKlineGuard("BTCUSDT", "1m", TimeSpan.FromMinutes(3));
        var open = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);
        var restKline = new BinanceSpotKline(
            open,
            open.AddMinutes(1).AddMilliseconds(-1),
            100m,
            102m,
            99m,
            101m,
            10m,
            "1m");

        var decision = guard.Evaluate(restKline, open.AddMinutes(1));

        Assert.True(decision.IsAccepted);
        Assert.Equal(open.AddMinutes(1), decision.Kline!.CloseTimeUtc);
    }

    private static BinanceKlineEvent Event(DateTimeOffset open, bool isClosed) => new(
        "btcusdt@kline_1m",
        "BTCUSDT",
        open.ToUnixTimeMilliseconds() + 60_000,
        open,
        open.ToUnixTimeMilliseconds(),
        open.AddMinutes(1).ToUnixTimeMilliseconds() - 1,
        "1m",
        100m,
        101m,
        102m,
        99m,
        10m,
        isClosed);
}
