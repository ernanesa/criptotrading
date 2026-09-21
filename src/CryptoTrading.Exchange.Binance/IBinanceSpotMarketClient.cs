namespace CryptoTrading.Exchange.Binance;

public interface IBinanceSpotMarketClient
{
    Task PingAsync(CancellationToken cancellationToken = default);

    Task<BinanceServerTime> GetServerTimeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BinanceSpotKline>> GetKlinesAsync(
        string symbol,
        string interval,
        DateTimeOffset? startTimeUtc = null,
        DateTimeOffset? endTimeUtc = null,
        int limit = 500,
        CancellationToken cancellationToken = default);

    /// <summary>Reads public aggregate trades at or before a UTC instant; it never submits an order.</summary>
    Task<IReadOnlyList<BinanceAggregateTrade>> GetAggregateTradesAsync(
        string symbol,
        DateTimeOffset? endTimeUtc = null,
        int limit = 500,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the public best bid/ask book ticker; it never submits an order.</summary>
    Task<BinanceBookTicker> GetBookTickerAsync(
        string symbol,
        CancellationToken cancellationToken = default);

    Task<BinanceExchangeInfo> GetExchangeInfoAsync(
        string? symbol = null,
        CancellationToken cancellationToken = default);

    Task<BinanceSymbolTradingRules> GetTradingRulesAsync(
        string symbol,
        CancellationToken cancellationToken = default);
}
