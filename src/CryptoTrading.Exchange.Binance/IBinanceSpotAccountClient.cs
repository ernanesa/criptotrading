namespace CryptoTrading.Exchange.Binance;

/// <summary>
/// Authenticated Binance Spot operations that only observe account state.
/// There are intentionally no order, cancel, transfer, or withdrawal methods here.
/// </summary>
public interface IBinanceSpotAccountClient
{
    Task<BinanceAccountSnapshot> GetAccountAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BinanceOpenOrder>> GetOpenOrdersAsync(
        string? symbol = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BinanceTrade>> GetMyTradesAsync(
        string symbol,
        BinanceTradeQuery? query = null,
        CancellationToken cancellationToken = default);

    Task<BinanceClockSnapshot> SynchronizeClockAsync(CancellationToken cancellationToken = default);
}
