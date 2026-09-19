namespace CryptoTrading.Exchange.Binance;

public interface IBinanceSpotMarketClient
{
    Task PingAsync(CancellationToken cancellationToken = default);

    Task<BinanceServerTime> GetServerTimeAsync(CancellationToken cancellationToken = default);

    Task<BinanceExchangeInfo> GetExchangeInfoAsync(
        string? symbol = null,
        CancellationToken cancellationToken = default);

    Task<BinanceSymbolTradingRules> GetTradingRulesAsync(
        string symbol,
        CancellationToken cancellationToken = default);
}
