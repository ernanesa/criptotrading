namespace CryptoTrading.Exchange.Binance;

/// <summary>
/// Canonical asset identity shared by the authenticated Binance boundary and
/// downstream reconciliation. The contract intentionally matches the domain
/// ledger: two to twenty ASCII letters or digits, normalized to upper case.
/// </summary>
public static class BinanceAssetNormalizer
{
    public static string Normalize(string? value, string parameterName = "asset")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length is < 2 or > 20 || normalized.Any(static character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException(
                $"{parameterName} must contain 2-20 ASCII letters or digits.",
                parameterName);
        }

        return normalized;
    }
}
