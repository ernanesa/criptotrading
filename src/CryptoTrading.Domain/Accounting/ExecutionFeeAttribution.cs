using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Domain.Accounting;

public sealed record ExecutionFeeEstimate
{
    public ExecutionFeeEstimate(
        decimal rate,
        string snapshotHash,
        string source,
        DateTimeOffset observedAtUtc,
        DateTimeOffset validUntilUtc)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rate, 0m);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (observedAtUtc.Offset != TimeSpan.Zero || validUntilUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Fee estimate timestamps must be UTC.");
        }

        if (validUntilUtc < observedAtUtc)
        {
            throw new ArgumentException("Fee estimate validity cannot precede observation.");
        }

        Rate = rate;
        SnapshotHash = snapshotHash.Trim();
        Source = source.Trim();
        ObservedAtUtc = observedAtUtc;
        ValidUntilUtc = validUntilUtc;
    }

    public decimal Rate { get; }
    public string SnapshotHash { get; }
    public string Source { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public DateTimeOffset ValidUntilUtc { get; }
}

public enum ExecutionFeeComparisonStatus
{
    NotComparable,
    WithinTolerance,
    Drift,
}

public sealed record ExecutionFeeComparison(
    ExecutionFeeComparisonStatus Status,
    decimal ExpectedQuote,
    decimal? ActualQuote,
    decimal? DeviationBps,
    string Reason,
    string? ValuationSource,
    DateTimeOffset? ValuationAtUtc)
{
    public bool IsDrift => Status == ExecutionFeeComparisonStatus.Drift;
}

/// <summary>Immutable, non-future conversion evidence for a fee asset into the fill quote asset.</summary>
public sealed record ExecutionFeeAssetValuation
{
    public ExecutionFeeAssetValuation(
        Asset asset,
        Asset quoteAsset,
        decimal quotePerAsset,
        DateTimeOffset observedAtUtc,
        string source)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(quotePerAsset, 0m);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Fee valuation timestamp must be UTC.", nameof(observedAtUtc));
        }

        Asset = asset;
        QuoteAsset = quoteAsset;
        QuotePerAsset = quotePerAsset;
        ObservedAtUtc = observedAtUtc;
        Source = source.Trim();
    }

    public Asset Asset { get; }
    public Asset QuoteAsset { get; }
    public decimal QuotePerAsset { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public string Source { get; }
}

public static class ExecutionFeeDriftEvaluator
{
    public const decimal DefaultToleranceBps = 25m;

    public static ExecutionFeeComparison Compare(
        ExecutionFeeEstimate? estimate,
        Money actualCommission,
        Quantity quantity,
        Price price,
        Asset baseAsset,
        Asset quoteAsset,
        DateTimeOffset occurredAtUtc,
        decimal toleranceBps = DefaultToleranceBps,
        ExecutionFeeAssetValuation? assetValuation = null)
    {
        if (estimate is null)
        {
            return new(ExecutionFeeComparisonStatus.NotComparable, 0m, null, null, "FEE_ESTIMATE_UNAVAILABLE", null, null);
        }

        if (toleranceBps < 0m || toleranceBps > 10_000m)
        {
            throw new ArgumentOutOfRangeException(nameof(toleranceBps));
        }

        var expectedQuote = checked(quantity.Value * price.Value * estimate.Rate);
        decimal actualQuote;
        string valuationSource;
        if (actualCommission.Asset == quoteAsset)
        {
            actualQuote = actualCommission.Amount;
            valuationSource = "fill-quote-asset";
        }
        else if (actualCommission.Asset == baseAsset)
        {
            actualQuote = checked(actualCommission.Amount * price.Value);
            valuationSource = "fill-price-base-to-quote";
        }
        else if (assetValuation is not null &&
                 assetValuation.Asset == actualCommission.Asset &&
                 assetValuation.QuoteAsset == quoteAsset &&
                 assetValuation.ObservedAtUtc <= occurredAtUtc)
        {
            actualQuote = checked(actualCommission.Amount * assetValuation.QuotePerAsset);
            valuationSource = assetValuation.Source;
        }
        else
        {
            return new(ExecutionFeeComparisonStatus.NotComparable, expectedQuote, null, null,
                "COMMISSION_VALUATION_UNAVAILABLE", null, null);
        }

        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Fill timestamp must be UTC.", nameof(occurredAtUtc));
        }

        if (expectedQuote == 0m)
        {
            var zeroStatus = actualQuote == 0m ? ExecutionFeeComparisonStatus.WithinTolerance : ExecutionFeeComparisonStatus.Drift;
            return new(zeroStatus, expectedQuote, actualQuote, actualQuote == 0m ? 0m : decimal.MaxValue,
                zeroStatus == ExecutionFeeComparisonStatus.Drift ? "EXECUTION_FEE_DRIFT" : "WITHIN_TOLERANCE",
                valuationSource, occurredAtUtc);
        }

        var deviationBps = Math.Abs(actualQuote - expectedQuote) / expectedQuote * 10_000m;
        var drift = deviationBps > toleranceBps;
        return new(drift ? ExecutionFeeComparisonStatus.Drift : ExecutionFeeComparisonStatus.WithinTolerance,
            expectedQuote, actualQuote, deviationBps, drift ? "EXECUTION_FEE_DRIFT" : "WITHIN_TOLERANCE",
            valuationSource, occurredAtUtc);
    }
}
