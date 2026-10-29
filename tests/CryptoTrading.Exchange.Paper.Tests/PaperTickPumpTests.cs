using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Exchange.Paper.Tests;

public sealed class PaperTickPumpTests
{
    private static readonly Symbol BtcUsdt = new("BTCUSDT");

    [Fact]
    public void TickOlderThanTheOrderProducesNoFill()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(now);
        var exchange = new PaperExchange(timeProvider: clock);
        var pump = new PaperTickPump(exchange);
        Submit(exchange, now);

        var produced = pump.Apply(Tick(now.AddSeconds(-1)));

        Assert.Equal(0, produced);
        Assert.Empty(pump.Fills);
        Assert.Null(pump.Summarize());
    }

    [Fact]
    public void SubmissionLatencyDelaysTheFill()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(now);
        var exchange = new PaperExchange(new PaperExchangeOptions { SubmissionLatency = TimeSpan.FromSeconds(1) }, clock);
        var pump = new PaperTickPump(exchange);
        Submit(exchange, now);

        Assert.Equal(0, pump.Apply(Tick(now)));
        Assert.Equal(1, pump.Apply(Tick(now.AddSeconds(2))));
    }

    [Fact]
    public void TickAfterSubmissionLatencyFillsAndProducesSummary()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(now);
        var exchange = new PaperExchange(timeProvider: clock);
        var pump = new PaperTickPump(exchange);
        Submit(exchange, now);

        var produced = pump.Apply(Tick(now.AddSeconds(2)));

        Assert.Equal(1, produced);
        var summary = pump.Summarize();
        Assert.NotNull(summary);
        Assert.True(summary!.FeesPaidQuote > 0m);
        Assert.Equal(0.01m, summary.OpenQuantity);
    }

    [Fact]
    public void SummaryIsAbsentWhileNoFillHappened()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var pump = new PaperTickPump(new PaperExchange(timeProvider: new MutableClock(now)));

        Assert.Null(pump.Summarize());
        Assert.Empty(pump.Fills);
    }

    [Fact]
    public void FillsIsAStableReadOnlySnapshot()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var exchange = new PaperExchange(timeProvider: new MutableClock(now));
        var pump = new PaperTickPump(exchange);
        Submit(exchange, now);
        var before = pump.Fills;

        pump.Apply(Tick(now));

        Assert.Empty(before);
        Assert.Single(pump.Fills);
        Assert.Throws<NotSupportedException>(() => ((IList<PaperFill>)pump.Fills).Clear());
    }

    private static void Submit(PaperExchange exchange, DateTimeOffset now) =>
        exchange.Submit(
            new PaperOrderRequest(new ClientOrderId("pump-1"), BtcUsdt, OrderSide.Buy, OrderType.Market, new Quantity(0.01m)),
            new IdempotencyKey("pump-1"));

    private static PaperMarketTick Tick(DateTimeOffset at) =>
        new(BtcUsdt, new Price(100m), new Price(100m), new Price(100m), at);

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
