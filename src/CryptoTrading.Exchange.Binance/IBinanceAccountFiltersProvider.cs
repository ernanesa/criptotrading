namespace CryptoTrading.Exchange.Binance;

/// <summary>
/// Read-only access to the account-relevant filters reported by the venue.
/// Separate from <see cref="IBinanceSpotAccountClient"/> so existing fakes and
/// callers are not forced to change.
/// </summary>
public interface IBinanceAccountFiltersProvider
{
    /// <summary>
    /// Returns the filters relevant to <paramref name="symbol"/> for the
    /// authenticated account, or <c>null</c>-free failure if the venue cannot
    /// answer. Implementations must not swallow transport errors.
    /// </summary>
    Task<BinanceAccountFilters> GetAccountFiltersAsync(string symbol, CancellationToken cancellationToken = default);
}
