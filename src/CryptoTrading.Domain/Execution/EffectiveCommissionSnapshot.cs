using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Domain.Execution;

public sealed record CommissionRateSet(decimal Maker, decimal Taker, decimal Buyer, decimal Seller)
{
    public CommissionRateSet Validate(string name)
    {
        if (Maker < 0m || Taker < 0m || Buyer < 0m || Seller < 0m)
        {
            throw new ArgumentOutOfRangeException(name, "Commission rates cannot be negative.");
        }

        return this;
    }

    public decimal For(OrderSide side, bool maker) =>
        (maker ? Maker : Taker) + (side == OrderSide.Buy ? Buyer : Seller);
}

/// <summary>Point-in-time commission evidence used by autonomous sizing.</summary>
public sealed record EffectiveCommissionSnapshot
{
    public EffectiveCommissionSnapshot(
        Symbol symbol,
        string environment,
        string accountScope,
        CommissionRateSet standard,
        CommissionRateSet special,
        CommissionRateSet tax,
        bool discountEnabledForAccount,
        bool discountEnabledForSymbol,
        string? discountAsset,
        decimal? discountRate,
        bool discountAssetValuationAvailable,
        DateTimeOffset observedAtUtc,
        string source = "binance:GET:/api/v3/account/commission",
        decimal? discountAssetQuotePrice = null,
        DateTimeOffset? discountAssetValuationAtUtc = null,
        string? discountAssetValuationSource = null)
    {
        if (string.IsNullOrWhiteSpace(environment) || string.IsNullOrWhiteSpace(accountScope))
        {
            throw new ArgumentException("Commission environment and account scope are required.");
        }

        if (observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Commission observation time must be UTC.", nameof(observedAtUtc));
        }

        ArgumentNullException.ThrowIfNull(standard);
        ArgumentNullException.ThrowIfNull(special);
        ArgumentNullException.ThrowIfNull(tax);
        Standard = standard.Validate(nameof(standard));
        Special = special.Validate(nameof(special));
        Tax = tax.Validate(nameof(tax));
        if (discountRate is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(discountRate));
        }

        if (discountEnabledForAccount && discountEnabledForSymbol &&
            (string.IsNullOrWhiteSpace(discountAsset) || discountRate is null))
        {
            throw new ArgumentException("An enabled discount must include an asset and rate.");
        }

        if (discountAssetValuationAvailable &&
            (discountAssetQuotePrice is not > 0m || discountAssetValuationAtUtc is null ||
             discountAssetValuationAtUtc.Value.Offset != TimeSpan.Zero || string.IsNullOrWhiteSpace(discountAssetValuationSource)))
        {
            throw new ArgumentException("A usable discount valuation requires price, UTC timestamp, and source.");
        }

        Symbol = symbol;
        Environment = environment.Trim();
        AccountScope = accountScope.Trim();
        DiscountEnabledForAccount = discountEnabledForAccount;
        DiscountEnabledForSymbol = discountEnabledForSymbol;
        DiscountAsset = discountAsset?.Trim().ToUpperInvariant();
        DiscountRate = discountRate;
        DiscountAssetValuationAvailable = discountAssetValuationAvailable;
        DiscountAssetQuotePrice = discountAssetQuotePrice;
        DiscountAssetValuationAtUtc = discountAssetValuationAtUtc;
        DiscountAssetValuationSource = discountAssetValuationSource?.Trim();
        ObservedAtUtc = observedAtUtc;
        Source = string.IsNullOrWhiteSpace(source) ? throw new ArgumentException("Source is required.", nameof(source)) : source.Trim();
        CanonicalPayloadHash = ComputeHash();
    }

    public Symbol Symbol { get; }
    public string Environment { get; }
    public string AccountScope { get; }
    public CommissionRateSet Standard { get; }
    public CommissionRateSet Special { get; }
    public CommissionRateSet Tax { get; }
    public bool DiscountEnabledForAccount { get; }
    public bool DiscountEnabledForSymbol { get; }
    public string? DiscountAsset { get; }
    public decimal? DiscountRate { get; }
    public bool DiscountAssetValuationAvailable { get; }
    public decimal? DiscountAssetQuotePrice { get; }
    public DateTimeOffset? DiscountAssetValuationAtUtc { get; }
    public string? DiscountAssetValuationSource { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public string Source { get; }
    public string CanonicalPayloadHash { get; }

    public bool IsFresh(DateTimeOffset nowUtc, TimeSpan maximumAge) =>
        nowUtc.Offset == TimeSpan.Zero && maximumAge > TimeSpan.Zero &&
        ObservedAtUtc <= nowUtc && nowUtc - ObservedAtUtc <= maximumAge;

    public bool TryGetEffectiveRate(OrderSide side, bool makerEligible, DateTimeOffset nowUtc, TimeSpan maximumAge, out decimal rate, out string reason)
    {
        rate = 0m;
        reason = string.Empty;
        if (!IsFresh(nowUtc, maximumAge))
        {
            reason = "COMMISSION_SNAPSHOT_STALE";
            return false;
        }

        var standardRate = Standard.For(side, makerEligible);
        if (DiscountEnabledForAccount && DiscountEnabledForSymbol)
        {
            if (!DiscountAssetValuationAvailable || DiscountRate is null || DiscountAssetQuotePrice is not > 0m ||
                DiscountAssetValuationAtUtc is null || string.IsNullOrWhiteSpace(DiscountAssetValuationSource))
            {
                reason = "COMMISSION_BNB_VALUATION_UNAVAILABLE";
                return false;
            }

            standardRate *= 1m - DiscountRate.Value;
        }

        rate = standardRate + Special.For(side, makerEligible) + Tax.For(side, makerEligible);
        if (rate < 0m || rate > 1_000_000m)
        {
            rate = 0m;
            reason = "COMMISSION_RATE_INVALID";
            return false;
        }

        return true;
    }

    private string ComputeHash()
    {
        var canonical = string.Join('|', Symbol.Value, Environment, AccountScope,
            Format(Standard), Format(Special), Format(Tax), DiscountEnabledForAccount,
            DiscountEnabledForSymbol, DiscountAsset ?? string.Empty,
            DiscountRate?.ToString("G29", CultureInfo.InvariantCulture) ?? string.Empty,
            DiscountAssetValuationAvailable, DiscountAssetQuotePrice, DiscountAssetValuationAtUtc?.ToString("O", CultureInfo.InvariantCulture),
            DiscountAssetValuationSource, ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture), Source);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string Format(CommissionRateSet rates) => string.Join(',',
        rates.Maker.ToString("G29", CultureInfo.InvariantCulture), rates.Taker.ToString("G29", CultureInfo.InvariantCulture),
        rates.Buyer.ToString("G29", CultureInfo.InvariantCulture), rates.Seller.ToString("G29", CultureInfo.InvariantCulture));
}
