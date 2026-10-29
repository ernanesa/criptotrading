using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Exchange.Binance;
using CryptoTrading.Exchange.Paper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class PaperExecutionHostedServiceTests
{
    private static readonly Symbol BtcUsdt = new("BTCUSDT");

    [Fact]
    public async Task PollingMarketDataFillsASubmittedPaperOrder()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableTime(now);
        var paper = new PaperExchange(timeProvider: clock);
        var market = new StubMarket(new BinanceBookTicker("BTCUSDT", 100m, 1m, 101m, 1m, now));
        using var service = Create(paper, market, clock, executionMode: "PAPER");
        paper.Submit(new PaperOrderRequest(new ClientOrderId("paper-1"), BtcUsdt, OrderSide.Buy, OrderType.Market, new Quantity(0.01m)),
            new IdempotencyKey("paper-1"));

        var produced = await service.PumpOnceAsync("BTCUSDT", TestContext.Current.CancellationToken);

        Assert.Equal(1, produced);
        Assert.Single(service.Fills);
    }

    [Fact]
    public async Task OldQuoteCannotBeRetimestampedToFillANewerOrder()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableTime(now);
        var paper = new PaperExchange(timeProvider: clock);
        var market = new StubMarket(new BinanceBookTicker("BTCUSDT", 100m, 1m, 101m, 1m, now.AddSeconds(-1)));
        using var service = Create(paper, market, clock, executionMode: "PAPER");
        paper.Submit(new PaperOrderRequest(new ClientOrderId("paper-stale"), BtcUsdt, OrderSide.Buy, OrderType.Market, new Quantity(0.01m)),
            new IdempotencyKey("paper-stale"));

        Assert.Equal(0, await service.PumpOnceAsync("BTCUSDT", TestContext.Current.CancellationToken));
        Assert.Empty(service.Fills);
    }

    [Fact]
    public async Task WrongSymbolQuoteCannotFillAnotherSymbolsOrder()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableTime(now);
        var paper = new PaperExchange(timeProvider: clock);
        var market = new StubMarket(new BinanceBookTicker("ETHUSDT", 100m, 1m, 101m, 1m, now));
        using var service = Create(paper, market, clock, executionMode: "PAPER");
        paper.Submit(new PaperOrderRequest(new ClientOrderId("paper-other"), new Symbol("ETHUSDT"), OrderSide.Buy, OrderType.Market, new Quantity(0.01m)),
            new IdempotencyKey("paper-other"));

        Assert.Equal(0, await service.PumpOnceAsync("BTCUSDT", TestContext.Current.CancellationToken));
        Assert.Empty(service.Fills);
    }

    [Fact]
    public async Task NoTradeDoesNotPollOrApplyMarketTicks()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableTime(now);
        var paper = new PaperExchange(timeProvider: clock);
        var market = new StubMarket(new BinanceBookTicker("BTCUSDT", 100m, 1m, 101m, 1m, now));
        using var service = new PaperExecutionHostedService(paper, market, Options.Create(new WorkerRuntimeOptions
        {
            ExecutionMode = "PAPER",
            NoTrade = true,
            AutonomySymbols = "BTCUSDT",
            PollIntervalMilliseconds = 100
        }), clock, NullLogger<PaperExecutionHostedService>.Instance);

        Assert.Equal(0, await service.PumpOnceAsync("BTCUSDT", TestContext.Current.CancellationToken));

        Assert.Equal(0, market.Calls);
        Assert.Empty(service.Fills);
    }

    [Fact]
    public async Task CrossedBookNeverProducesAnExecution()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableTime(now);
        var paper = new PaperExchange(timeProvider: clock);
        var market = new StubMarket(new BinanceBookTicker("BTCUSDT", 101m, 1m, 100m, 1m, now));
        using var service = Create(paper, market, clock, executionMode: "PAPER");
        paper.Submit(new PaperOrderRequest(new ClientOrderId("paper-2"), BtcUsdt, OrderSide.Buy, OrderType.Market, new Quantity(0.01m)),
            new IdempotencyKey("paper-2"));

        Assert.Equal(0, await service.PumpOnceAsync("BTCUSDT", TestContext.Current.CancellationToken));
        Assert.Empty(service.Fills);
    }

    [Fact]
    public async Task MarketDataFailureDoesNotCreateAFill()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableTime(now);
        var paper = new PaperExchange(timeProvider: clock);
        using var service = Create(paper, new StubMarket(failure: new HttpRequestException("offline")), clock, "PAPER");

        Assert.Equal(0, await service.PumpOnceAsync("BTCUSDT", TestContext.Current.CancellationToken));
        Assert.Empty(service.Fills);
    }

    [Fact]
    public async Task NonPaperModeDoesNotPollMarketData()
    {
        var clock = new MutableTime(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));
        var market = new StubMarket(new BinanceBookTicker("BTCUSDT", 100m, 1m, 101m, 1m, clock.GetUtcNow()));
        using var service = Create(new PaperExchange(timeProvider: clock), market, clock, executionMode: "TESTNET");

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, market.Calls);
    }

    private static PaperExecutionHostedService Create(IPaperExchange paper, IBinanceSpotMarketClient market, TimeProvider clock, string executionMode) =>
        new(paper, market, Options.Create(new WorkerRuntimeOptions
        {
            ExecutionMode = executionMode,
            NoTrade = false,
            AutonomySymbols = "BTCUSDT",
            PollIntervalMilliseconds = 100
        }), clock, NullLogger<PaperExecutionHostedService>.Instance);

    private sealed class StubMarket(BinanceBookTicker? ticker = null, Exception? failure = null) : IBinanceSpotMarketClient
    {
        public int Calls { get; private set; }

        public Task<BinanceBookTicker> GetBookTickerAsync(string symbol, CancellationToken cancellationToken = default)
        {
            Calls++;
            return failure is null ? Task.FromResult(ticker!) : Task.FromException<BinanceBookTicker>(failure);
        }

        public Task PingAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BinanceServerTime> GetServerTimeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BinanceSpotKline>> GetKlinesAsync(string symbol, string interval, DateTimeOffset? startTimeUtc = null, DateTimeOffset? endTimeUtc = null, int limit = 500, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BinanceAggregateTrade>> GetAggregateTradesAsync(string symbol, DateTimeOffset? endTimeUtc = null, int limit = 500, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BinanceExchangeInfo> GetExchangeInfoAsync(string? symbol = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BinanceSymbolTradingRules> GetTradingRulesAsync(string symbol, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
