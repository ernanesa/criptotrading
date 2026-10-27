using CryptoTrading.Exchange.Binance;

namespace CryptoTrading.Worker;

internal static class PortfolioTickerValidation
{
    internal static bool IsValid(BinanceBookTicker? ticker, string symbol, DateTimeOffset now, TimeSpan maximumAge) =>
        ticker is not null &&
        string.Equals(ticker.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
        ticker.BidPrice > 0m && ticker.AskPrice >= ticker.BidPrice &&
        ticker.ObservedAtUtc <= now && now - ticker.ObservedAtUtc <= maximumAge;
}
