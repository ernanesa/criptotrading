namespace CryptoTrading.Exchange.Binance;

/// <summary>
/// Public reference price used by percentage price filters. Returns null when the
/// venue answers that the symbol has no usable reference price, and throws when
/// the venue reports that none was ever set (error -2043).
/// </summary>
public interface IBinanceReferencePriceProvider
{
    Task<BinanceReferencePriceEvidence?> GetReferencePriceAsync(
        string symbol,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reference price with the identity and time the venue reported for it. A bare
/// decimal cannot prove it belongs to the symbol being ordered.
/// </summary>
public sealed record BinanceReferencePriceEvidence(
    string Symbol,
    decimal Price,
    DateTimeOffset VenueTimestampUtc);
