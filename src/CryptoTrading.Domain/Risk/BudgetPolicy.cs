using System.Globalization;

namespace CryptoTrading.Domain.Risk;

/// <summary>
/// Fresh point-in-time USDTBRL conversion quote used to convert BRL budget and
/// loss ceilings into quote-asset (USDT) limits.
/// </summary>
public sealed record UsdtBrlFxQuote
{
    public UsdtBrlFxQuote(
        decimal priceBrlPerUsdt,
        DateTimeOffset observedAtUtc,
        bool isComplete = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(priceBrlPerUsdt, 0m);
        if (observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("USDTBRL FX quote timestamp must be UTC.", nameof(observedAtUtc));
        }

        PriceBrlPerUsdt = priceBrlPerUsdt;
        ObservedAtUtc = observedAtUtc;
        IsComplete = isComplete;
    }

    public decimal PriceBrlPerUsdt { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public bool IsComplete { get; }

    public bool IsFresh(DateTimeOffset nowUtc, TimeSpan? maxAge = null)
    {
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Current timestamp must be UTC.", nameof(nowUtc));
        }

        var ageLimit = maxAge ?? TimeSpan.FromSeconds(BudgetPolicy.DefaultMaxFxQuoteAgeSeconds);
        return IsComplete &&
               PriceBrlPerUsdt > 0m &&
               ObservedAtUtc <= nowUtc &&
               nowUtc - ObservedAtUtc <= ageLimit;
    }

    public decimal ConvertBrlToUsdtConservative(decimal amountBrl, decimal haircutRatio = BudgetPolicy.DefaultFxSafetyHaircutRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amountBrl);
        if (haircutRatio is < 0m or >= 0.50m)
        {
            throw new ArgumentOutOfRangeException(nameof(haircutRatio), "Haircut ratio must be in [0, 0.50).");
        }

        if (PriceBrlPerUsdt <= 0m)
        {
            throw new InvalidOperationException("FX price must be positive.");
        }

        return Math.Round((amountBrl / PriceBrlPerUsdt) * (1m - haircutRatio), 8, MidpointRounding.ToZero);
    }

    public decimal ComputeEffectiveCeilingUsdt(
        decimal nominalBudgetBrl = BudgetPolicy.DefaultNominalBudgetBrl,
        decimal absoluteHardCapUsdt = BudgetPolicy.DefaultHardCapUsdt,
        decimal haircutRatio = BudgetPolicy.DefaultFxSafetyHaircutRate)
    {
        var conservativeUsdt = ConvertBrlToUsdtConservative(nominalBudgetBrl, haircutRatio);
        return Math.Min(absoluteHardCapUsdt, conservativeUsdt);
    }
}

/// <summary>Outcome of converting the operator BRL budget policy into USDT limits.</summary>
public sealed record BudgetCeilingValuationResult(
    bool IsValid,
    decimal EffectiveBudgetCeilingUsdt,
    decimal MaxDailyLossUsdt,
    decimal MaxCumulativeDrawdownUsdt,
    decimal MaxRiskPerTradeUsdt,
    decimal FxRateBrlPerUsdt,
    string ReasonCode);

/// <summary>Input snapshot for evaluating aggregate capital commitment against the budget ceiling.</summary>
public sealed record BudgetCommitmentRequest
{
    public BudgetCommitmentRequest(
        decimal requestedOrderNotionalUsdt,
        decimal openPositionsNotionalUsdt,
        int openPositionsCount,
        decimal pendingOrdersReservedUsdt,
        decimal unknownOrdersLockedUsdt,
        decimal dailyRealizedLossUsdt,
        decimal cumulativeNetRealizedLossUsdt,
        decimal estimatedStopRiskUsdt,
        DateTimeOffset evaluatedAtUtc,
        UsdtBrlFxQuote? fxQuote,
        bool isPositionReduction = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requestedOrderNotionalUsdt);
        ArgumentOutOfRangeException.ThrowIfNegative(openPositionsNotionalUsdt);
        ArgumentOutOfRangeException.ThrowIfNegative(openPositionsCount);
        ArgumentOutOfRangeException.ThrowIfNegative(pendingOrdersReservedUsdt);
        ArgumentOutOfRangeException.ThrowIfNegative(unknownOrdersLockedUsdt);
        ArgumentOutOfRangeException.ThrowIfNegative(dailyRealizedLossUsdt);
        ArgumentOutOfRangeException.ThrowIfNegative(cumulativeNetRealizedLossUsdt);
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedStopRiskUsdt);
        if (evaluatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Commitment evaluation timestamp must be UTC.", nameof(evaluatedAtUtc));
        }

        RequestedOrderNotionalUsdt = requestedOrderNotionalUsdt;
        OpenPositionsNotionalUsdt = openPositionsNotionalUsdt;
        OpenPositionsCount = openPositionsCount;
        PendingOrdersReservedUsdt = pendingOrdersReservedUsdt;
        UnknownOrdersLockedUsdt = unknownOrdersLockedUsdt;
        DailyRealizedLossUsdt = dailyRealizedLossUsdt;
        CumulativeNetRealizedLossUsdt = cumulativeNetRealizedLossUsdt;
        EstimatedStopRiskUsdt = estimatedStopRiskUsdt;
        EvaluatedAtUtc = evaluatedAtUtc;
        FxQuote = fxQuote;
        IsPositionReduction = isPositionReduction;
    }

    public decimal RequestedOrderNotionalUsdt { get; }
    public decimal OpenPositionsNotionalUsdt { get; }
    public int OpenPositionsCount { get; }
    public decimal PendingOrdersReservedUsdt { get; }
    public decimal UnknownOrdersLockedUsdt { get; }
    public decimal DailyRealizedLossUsdt { get; }
    public decimal CumulativeNetRealizedLossUsdt { get; }
    public decimal EstimatedStopRiskUsdt { get; }
    public DateTimeOffset EvaluatedAtUtc { get; }
    public UsdtBrlFxQuote? FxQuote { get; }
    public bool IsPositionReduction { get; }
}

/// <summary>Result of evaluating aggregate capital commitment against the R$ 100 / <= 20 USDT budget policy.</summary>
public sealed record BudgetCommitmentDecision(
    bool IsApproved,
    decimal EffectiveBudgetCeilingUsdt,
    decimal TotalCommittedBeforeUsdt,
    decimal RequiredOrderReservationUsdt,
    decimal AvailableHeadroomUsdt,
    string ReasonCode);

/// <summary>
/// Immutable operator budget policy enforcing the R$ 100 nominal ceiling,
/// fresh USDTBRL conversion (<= 60s) with 2% FX safety haircut, 20.00 USDT hard cap,
/// single open Spot position (BTCUSDT), R$ 2 daily loss, R$ 5 cumulative drawdown,
/// R$ 1 max risk per trade, and 10 bps + 25 bps fee reserve.
/// </summary>
public sealed record BudgetPolicy
{
    public const string DefaultOwner = "Ernane";
    public const decimal DefaultNominalBudgetBrl = 100.00m;
    public const decimal DefaultHardCapUsdt = 20.00m;
    public const decimal DefaultFxSafetyHaircutRate = 0.02m;
    public const int DefaultMaxFxQuoteAgeSeconds = 60;
    public const int DefaultMaxOpenPositions = 1;
    public const decimal DefaultMaxDailyLossBrl = 2.00m;
    public const decimal DefaultMaxCumulativeDrawdownBrl = 5.00m;
    public const decimal DefaultMaxRiskPerTradeBrl = 1.00m;
    public const decimal DefaultTakerFeeBps = 10.0m;
    public const decimal DefaultFeeReserveBufferBps = 25.0m;
    public const decimal DefaultMinOrderNotionalUsdt = 5.00m;
    public const string DefaultAllowedSymbol = "BTCUSDT";

    public BudgetPolicy(
        string owner = DefaultOwner,
        decimal nominalBudgetBrl = DefaultNominalBudgetBrl,
        decimal hardCapUsdt = DefaultHardCapUsdt,
        decimal fxSafetyHaircutRate = DefaultFxSafetyHaircutRate,
        TimeSpan? maxFxQuoteAge = null,
        int maxOpenPositions = DefaultMaxOpenPositions,
        decimal maxDailyLossBrl = DefaultMaxDailyLossBrl,
        decimal maxCumulativeDrawdownBrl = DefaultMaxCumulativeDrawdownBrl,
        decimal maxRiskPerTradeBrl = DefaultMaxRiskPerTradeBrl,
        decimal takerFeeBps = DefaultTakerFeeBps,
        decimal feeReserveBufferBps = DefaultFeeReserveBufferBps,
        decimal minOrderNotionalUsdt = DefaultMinOrderNotionalUsdt,
        string allowedSymbol = DefaultAllowedSymbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(allowedSymbol);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(nominalBudgetBrl, 0m);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(hardCapUsdt, 0m);
        if (fxSafetyHaircutRate is < 0m or >= 0.50m)
        {
            throw new ArgumentOutOfRangeException(nameof(fxSafetyHaircutRate), "FX safety haircut rate must be in [0, 0.50).");
        }

        var resolvedFxAge = maxFxQuoteAge ?? TimeSpan.FromSeconds(DefaultMaxFxQuoteAgeSeconds);
        if (resolvedFxAge <= TimeSpan.Zero || resolvedFxAge > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(maxFxQuoteAge), "Max FX quote age must be positive and at most 10 minutes.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxOpenPositions, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxDailyLossBrl, 0m);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCumulativeDrawdownBrl, maxDailyLossBrl);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxRiskPerTradeBrl, 0m);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxRiskPerTradeBrl, maxDailyLossBrl);
        ArgumentOutOfRangeException.ThrowIfNegative(takerFeeBps);
        ArgumentOutOfRangeException.ThrowIfNegative(feeReserveBufferBps);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(minOrderNotionalUsdt, 0m);

        Owner = owner.Trim();
        NominalBudgetBrl = nominalBudgetBrl;
        HardCapUsdt = hardCapUsdt;
        FxSafetyHaircutRate = fxSafetyHaircutRate;
        MaxFxQuoteAge = resolvedFxAge;
        MaxOpenPositions = maxOpenPositions;
        MaxDailyLossBrl = maxDailyLossBrl;
        MaxCumulativeDrawdownBrl = maxCumulativeDrawdownBrl;
        MaxRiskPerTradeBrl = maxRiskPerTradeBrl;
        TakerFeeBps = takerFeeBps;
        FeeReserveBufferBps = feeReserveBufferBps;
        MinOrderNotionalUsdt = minOrderNotionalUsdt;
        AllowedSymbol = allowedSymbol.Trim().ToUpperInvariant();
    }

    public static BudgetPolicy ApprovedMicroLiveDefault { get; } = new();

    public string Owner { get; }
    public decimal NominalBudgetBrl { get; }
    public decimal HardCapUsdt { get; }
    public decimal AbsoluteHardCapUsdt => HardCapUsdt;
    public decimal FxSafetyHaircutRate { get; }
    public decimal FxHaircutRatio => FxSafetyHaircutRate;
    public TimeSpan MaxFxQuoteAge { get; }
    public int MaxOpenPositions { get; }
    public decimal MaxDailyLossBrl { get; }
    public decimal MaxCumulativeDrawdownBrl { get; }
    public decimal MaxRiskPerTradeBrl { get; }
    public decimal TakerFeeBps { get; }
    public decimal TakerFeeRatePerSide => TakerFeeBps / 10_000m;
    public decimal FeeReserveBufferBps { get; }
    public decimal FeeBufferRate => FeeReserveBufferBps / 10_000m;
    public decimal MinOrderNotionalUsdt { get; }
    public string AllowedSymbol { get; }

    /// <summary>Round-trip taker fees (2 * 10 bps) plus reserve buffer (25 bps) = 45 bps (0.0045).</summary>
    public decimal RoundTripFeeAndReserveRate => ((TakerFeeBps * 2m) + FeeReserveBufferBps) / 10_000m;

    /// <summary>Single-leg taker fee (10 bps) plus reserve buffer (25 bps) = 35 bps (0.0035).</summary>
    public decimal SingleLegFeeAndReserveRate => (TakerFeeBps + FeeReserveBufferBps) / 10_000m;

    public BudgetCeilingValuationResult CalculateEffectiveCeiling(
        UsdtBrlFxQuote? quote,
        DateTimeOffset evaluatedAtUtc)
    {
        if (evaluatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Evaluation timestamp must be UTC.", nameof(evaluatedAtUtc));
        }

        if (quote is null || !quote.IsComplete || quote.PriceBrlPerUsdt <= 0m)
        {
            return new BudgetCeilingValuationResult(
                false, 0m, 0m, 0m, 0m, 0m, RiskReasonCodes.FxQuoteUnavailable);
        }

        if (quote.ObservedAtUtc.Offset != TimeSpan.Zero ||
            quote.ObservedAtUtc > evaluatedAtUtc ||
            evaluatedAtUtc - quote.ObservedAtUtc > MaxFxQuoteAge)
        {
            return new BudgetCeilingValuationResult(
                false, 0m, 0m, 0m, 0m, quote.PriceBrlPerUsdt, RiskReasonCodes.FxQuoteStale);
        }

        var fx = quote.PriceBrlPerUsdt;
        var haircutMultiplier = 1m - FxSafetyHaircutRate;
        var convertedBudgetUsdt = Math.Round((NominalBudgetBrl / fx) * haircutMultiplier, 8, MidpointRounding.ToZero);
        var effectiveCeilingUsdt = Math.Min(HardCapUsdt, convertedBudgetUsdt);
        var scaleRatio = convertedBudgetUsdt > 0m ? effectiveCeilingUsdt / convertedBudgetUsdt : 1m;

        var dailyLossUsdt = Math.Round((MaxDailyLossBrl / fx) * haircutMultiplier * scaleRatio, 8, MidpointRounding.ToZero);
        var drawdownUsdt = Math.Round((MaxCumulativeDrawdownBrl / fx) * haircutMultiplier * scaleRatio, 8, MidpointRounding.ToZero);
        var perTradeRiskUsdt = Math.Round((MaxRiskPerTradeBrl / fx) * haircutMultiplier * scaleRatio, 8, MidpointRounding.ToZero);

        return new BudgetCeilingValuationResult(
            true,
            effectiveCeilingUsdt,
            dailyLossUsdt,
            drawdownUsdt,
            perTradeRiskUsdt,
            fx,
            RiskReasonCodes.TargetWithinLimits);
    }

    public BudgetCommitmentDecision EvaluateCommitment(BudgetCommitmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var valuation = CalculateEffectiveCeiling(request.FxQuote, request.EvaluatedAtUtc);
        var totalCommittedBefore = request.OpenPositionsNotionalUsdt +
                                   request.PendingOrdersReservedUsdt +
                                   request.UnknownOrdersLockedUsdt +
                                   request.CumulativeNetRealizedLossUsdt;

        if (!valuation.IsValid)
        {
            if (request.IsPositionReduction)
            {
                return new BudgetCommitmentDecision(
                    true, 0m, totalCommittedBefore, 0m, 0m, valuation.ReasonCode);
            }

            return new BudgetCommitmentDecision(
                false, 0m, totalCommittedBefore, 0m, 0m, valuation.ReasonCode);
        }

        var availableHeadroom = Math.Max(0m, valuation.EffectiveBudgetCeilingUsdt - totalCommittedBefore);
        var requiredReservation = Math.Round(
            request.RequestedOrderNotionalUsdt * (1m + RoundTripFeeAndReserveRate),
            8,
            MidpointRounding.ToPositiveInfinity);

        if (request.IsPositionReduction)
        {
            return new BudgetCommitmentDecision(
                true,
                valuation.EffectiveBudgetCeilingUsdt,
                totalCommittedBefore,
                0m,
                availableHeadroom,
                RiskReasonCodes.TargetWithinLimits);
        }

        if (request.UnknownOrdersLockedUsdt > 0m)
        {
            return new BudgetCommitmentDecision(
                false,
                valuation.EffectiveBudgetCeilingUsdt,
                totalCommittedBefore,
                requiredReservation,
                availableHeadroom,
                RiskReasonCodes.LockedUnknownCapitalActive);
        }

        if (request.CumulativeNetRealizedLossUsdt >= valuation.MaxCumulativeDrawdownUsdt)
        {
            return new BudgetCommitmentDecision(
                false,
                valuation.EffectiveBudgetCeilingUsdt,
                totalCommittedBefore,
                requiredReservation,
                availableHeadroom,
                RiskReasonCodes.DrawdownLimit);
        }

        if (request.DailyRealizedLossUsdt >= valuation.MaxDailyLossUsdt)
        {
            return new BudgetCommitmentDecision(
                false,
                valuation.EffectiveBudgetCeilingUsdt,
                totalCommittedBefore,
                requiredReservation,
                availableHeadroom,
                RiskReasonCodes.DailyLossLimit);
        }

        if (request.OpenPositionsCount >= MaxOpenPositions)
        {
            return new BudgetCommitmentDecision(
                false,
                valuation.EffectiveBudgetCeilingUsdt,
                totalCommittedBefore,
                requiredReservation,
                availableHeadroom,
                RiskReasonCodes.MaxOpenPositionsExceeded);
        }

        if (request.RequestedOrderNotionalUsdt < MinOrderNotionalUsdt)
        {
            return new BudgetCommitmentDecision(
                false,
                valuation.EffectiveBudgetCeilingUsdt,
                totalCommittedBefore,
                requiredReservation,
                availableHeadroom,
                RiskReasonCodes.BelowMinimumOrderNotional);
        }

        if (requiredReservation > availableHeadroom)
        {
            return new BudgetCommitmentDecision(
                false,
                valuation.EffectiveBudgetCeilingUsdt,
                totalCommittedBefore,
                requiredReservation,
                availableHeadroom,
                RiskReasonCodes.BudgetCeilingExceeded);
        }

        if (request.EstimatedStopRiskUsdt > valuation.MaxRiskPerTradeUsdt)
        {
            return new BudgetCommitmentDecision(
                false,
                valuation.EffectiveBudgetCeilingUsdt,
                totalCommittedBefore,
                requiredReservation,
                availableHeadroom,
                RiskReasonCodes.PerTradeRiskExceeded);
        }

        return new BudgetCommitmentDecision(
            true,
            valuation.EffectiveBudgetCeilingUsdt,
            totalCommittedBefore,
            requiredReservation,
            availableHeadroom - requiredReservation,
            RiskReasonCodes.TargetWithinLimits);
    }

    internal string ToCanonicalComponent() => string.Join(':',
        Owner,
        NominalBudgetBrl.ToString("0.##############################", CultureInfo.InvariantCulture),
        HardCapUsdt.ToString("0.##############################", CultureInfo.InvariantCulture),
        FxSafetyHaircutRate.ToString("0.##############################", CultureInfo.InvariantCulture),
        MaxFxQuoteAge.Ticks.ToString(CultureInfo.InvariantCulture),
        MaxOpenPositions.ToString(CultureInfo.InvariantCulture),
        MaxDailyLossBrl.ToString("0.##############################", CultureInfo.InvariantCulture),
        MaxCumulativeDrawdownBrl.ToString("0.##############################", CultureInfo.InvariantCulture),
        MaxRiskPerTradeBrl.ToString("0.##############################", CultureInfo.InvariantCulture),
        TakerFeeBps.ToString("0.##############################", CultureInfo.InvariantCulture),
        FeeReserveBufferBps.ToString("0.##############################", CultureInfo.InvariantCulture),
        MinOrderNotionalUsdt.ToString("0.##############################", CultureInfo.InvariantCulture),
        AllowedSymbol);
}
