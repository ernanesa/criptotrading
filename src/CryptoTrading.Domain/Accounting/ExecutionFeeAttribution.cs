using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Orders;

namespace CryptoTrading.Domain.Accounting;

public sealed record ExecutionFeeEstimate
{
    public ExecutionFeeEstimate(
        decimal rate,
        string snapshotHash,
        string source,
        DateTimeOffset observedAtUtc,
        DateTimeOffset validUntilUtc,
        decimal? referencePrice = null,
        decimal? authorizedQuantity = null,
        DateTimeOffset? decisionAtUtc = null)
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

        if ((referencePrice is null) != (authorizedQuantity is null) ||
            (referencePrice is null) != (decisionAtUtc is null) ||
            referencePrice is <= 0m || authorizedQuantity is <= 0m ||
            (decisionAtUtc is not null && decisionAtUtc.Value.Offset != TimeSpan.Zero))
        {
            throw new ArgumentException("Execution-quality expectation must be complete, positive, and UTC when present.");
        }

        Rate = rate;
        SnapshotHash = snapshotHash.Trim();
        Source = source.Trim();
        ObservedAtUtc = observedAtUtc;
        ValidUntilUtc = validUntilUtc;
        ReferencePrice = referencePrice;
        AuthorizedQuantity = authorizedQuantity;
        DecisionAtUtc = decisionAtUtc;
    }

    public decimal Rate { get; }
    public string SnapshotHash { get; }
    public string Source { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public DateTimeOffset ValidUntilUtc { get; }
    public decimal? ReferencePrice { get; }
    public decimal? AuthorizedQuantity { get; }
    public DateTimeOffset? DecisionAtUtc { get; }
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

public sealed record ExecutionQualityComparison(
    decimal? FillRatio,
    decimal? ImplementationShortfallQuote,
    long? DecisionToFillLatencyMilliseconds,
    string Status,
    string Reason);

/// <summary>Derives execution quality without inventing a benchmark for legacy fills.</summary>
public static class ExecutionQualityEvaluator
{
    public static ExecutionQualityComparison Compare(
        ExecutionFeeEstimate? expectation,
        OrderSide side,
        Quantity fillQuantity,
        Quantity? cumulativeFilledQuantity,
        Price fillPrice,
        DateTimeOffset occurredAtUtc)
    {
        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Fill timestamp must be UTC.", nameof(occurredAtUtc));
        }

        if (expectation?.ReferencePrice is not { } referencePrice ||
            expectation.AuthorizedQuantity is not { } authorizedQuantity ||
            expectation.DecisionAtUtc is not { } decisionAtUtc ||
            cumulativeFilledQuantity is not { } cumulativeQuantity)
        {
            return new(null, null, null, "NOT_COMPARABLE", "EXECUTION_BENCHMARK_UNAVAILABLE");
        }

        if (cumulativeQuantity.Value < fillQuantity.Value || cumulativeQuantity.Value > authorizedQuantity)
        {
            return new(null, null, null, "NOT_COMPARABLE", "CUMULATIVE_FILL_INVALID");
        }

        if (occurredAtUtc < decisionAtUtc)
        {
            return new(null, null, null, "NOT_COMPARABLE", "FILL_PRECEDES_DECISION");
        }

        var ratio = cumulativeQuantity.Value / authorizedQuantity;
        var shortfall = side == OrderSide.Buy
            ? (fillPrice.Value - referencePrice) * fillQuantity.Value
            : (referencePrice - fillPrice.Value) * fillQuantity.Value;
        return new(ratio, shortfall, checked((occurredAtUtc - decisionAtUtc).Ticks / TimeSpan.TicksPerMillisecond),
            "COMPARABLE", "EXECUTION_QUALITY_COMPARABLE");
    }
}
