namespace CryptoTrading.Exchange.Binance;

/// <summary>
/// Account state needed to verify order-count and position filters. One snapshot
/// is intended to serve both checks so a submission does not repeat the calls.
/// </summary>
/// <param name="OpenOrderCount">Open orders for the whole account.</param>
/// <param name="BasePositionQuantity">
/// Base-asset position: free + locked balance plus the unexecuted quantity of
/// open BUY orders on the symbol, matching the MAX_POSITION definition.
/// </param>
/// <param name="SymbolOpenOrderCount">
/// Open orders on the requested symbol, including algorithmic orders, matching
/// the MAX_NUM_ORDERS definition.
/// </param>
/// <param name="CanTrade">
/// Venue-reported permission to trade. False means the account is restricted and
/// no order should be submitted.
/// </param>
/// <param name="IsConsistent">
/// False when the underlying account and open-orders reads were too far apart in
/// time to describe one state.
/// </param>
public sealed record BinanceAccountTradingState(
    long OpenOrderCount,
    long SymbolOpenOrderCount,
    decimal BasePositionQuantity,
    DateTimeOffset ObservedAtUtc,
    bool CanTrade,
    bool IsConsistent = true);

/// <summary>
/// Supplies <see cref="BinanceAccountTradingState"/>. Callers must treat any
/// failure as unknown state, never as zero.
/// </summary>
public interface IBinanceAccountTradingStateProvider
{
    Task<BinanceAccountTradingState> GetTradingStateAsync(
        string symbol,
        string baseAsset,
        CancellationToken cancellationToken = default);
}
