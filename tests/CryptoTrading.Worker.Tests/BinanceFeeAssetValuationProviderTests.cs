using CryptoTrading.Exchange.Binance;
using Microsoft.Extensions.Options;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class BinanceFeeAssetValuationProviderTests
{
    [Fact]
    public async Task UsesLatestAggregateTradeAtOrBeforeTheFill()
    {
        var fillAt = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123);
        var market = new StubMarketClient([
            Trade(1, 599m, fillAt.AddSeconds(-2)),
            Trade(2, 601m, fillAt.AddMilliseconds(-1)),
        ]);
        var provider = new BinanceFeeAssetValuationProvider(
            market,
            Options.Create(new WorkerRuntimeOptions { FeeAssetValuationMaxAgeSeconds = 30 }));

        var valuation = await provider.ResolveAsync(Execution(fillAt));

        Assert.NotNull(valuation);
        Assert.Equal(601m, valuation!.QuotePerAsset);
        Assert.Equal(fillAt.AddMilliseconds(-1), valuation.ObservedAtUtc);
        Assert.Equal("BNBUSDT", market.RequestedSymbol);
        Assert.Equal(fillAt, market.RequestedEndTime);
    }

    [Fact]
    public async Task StaleOrUnavailableBnbQuoteDoesNotInventAValuation()
    {
        var fillAt = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123);
        var provider = new BinanceFeeAssetValuationProvider(
            new StubMarketClient([Trade(1, 600m, fillAt.AddSeconds(-31))]),
            Options.Create(new WorkerRuntimeOptions { FeeAssetValuationMaxAgeSeconds = 30 }));

        Assert.Null(await provider.ResolveAsync(Execution(fillAt)));
    }

    private static BinanceExecutionReportEvent Execution(DateTimeOffset occurredAt) => new(
        occurredAt, occurredAt.ToUnixTimeMilliseconds(), "BTCUSDT", 42, 7, "client-42", "BUY", "LIMIT", "TRADE", "FILLED",
        .01m, .01m, 100m, .001m, "BNB");

    private static BinanceAggregateTrade Trade(long id, decimal price, DateTimeOffset occurredAt) =>
        new(id, price, 1m, id, id, occurredAt, false);

    private sealed class StubMarketClient(IReadOnlyList<BinanceAggregateTrade> trades) : IBinanceSpotMarketClient
    {
        public string? RequestedSymbol { get; private set; }
        public DateTimeOffset? RequestedEndTime { get; private set; }

        public Task<IReadOnlyList<BinanceAggregateTrade>> GetAggregateTradesAsync(string symbol, DateTimeOffset? endTimeUtc = null, int limit = 500, CancellationToken cancellationToken = default)
        {
            RequestedSymbol = symbol;
            RequestedEndTime = endTimeUtc;
            return Task.FromResult(trades);
        }

        public Task PingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<BinanceServerTime> GetServerTimeAsync(CancellationToken cancellationToken = default) => Task.FromResult(new BinanceServerTime(0));
        public Task<IReadOnlyList<BinanceSpotKline>> GetKlinesAsync(string symbol, string interval, DateTimeOffset? startTimeUtc = null, DateTimeOffset? endTimeUtc = null, int limit = 500, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<BinanceSpotKline>>([]);
        public Task<BinanceBookTicker> GetBookTickerAsync(string symbol, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BinanceExchangeInfo> GetExchangeInfoAsync(string? symbol = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BinanceSymbolTradingRules> GetTradingRulesAsync(string symbol, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
