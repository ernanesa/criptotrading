namespace CryptoTrading.Exchange.Binance;

/// <summary>
/// Public reference price used by percentage price filters. Returns null when
/// the venue answers that the symbol has no reference price set, and throws when
/// the venue reports that none was ever set (error -2043).
/// </summary>
public interface IBinanceReferencePriceProvider
{
    Task<decimal?> GetReferencePriceAsync(string symbol, CancellationToken cancellationToken = default);
}
