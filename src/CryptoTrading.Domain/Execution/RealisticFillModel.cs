namespace CryptoTrading.Domain.Execution;

/// <summary>
/// Configuration parameters for realistic microstructure fill simulation in backtests.
/// Models price bounds within bar High/Low, volume participation caps, and volume-impact dynamic slippage.
/// </summary>
public sealed record RealisticFillModelParameters
{
    public RealisticFillModelParameters(
        bool enforceIntraBarPriceRange = true,
        bool modelVolumeParticipationSlippage = true,
        decimal amihudImpactFactor = 0.15m,
        decimal maxParticipationRate = 0.10m)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amihudImpactFactor);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxParticipationRate);
        if (maxParticipationRate > 1.0m)
        {
            throw new ArgumentOutOfRangeException(nameof(maxParticipationRate), "Participation rate cannot exceed 100%.");
        }

        EnforceIntraBarPriceRange = enforceIntraBarPriceRange;
        ModelVolumeParticipationSlippage = modelVolumeParticipationSlippage;
        AmihudImpactFactor = amihudImpactFactor;
        MaxParticipationRate = maxParticipationRate;
    }

    public bool EnforceIntraBarPriceRange { get; }
    public bool ModelVolumeParticipationSlippage { get; }
    public decimal AmihudImpactFactor { get; }
    public decimal MaxParticipationRate { get; }

    public static RealisticFillModelParameters ConservativeDefault { get; } = new();
}

public sealed record RealisticFillEvaluationResult(
    bool IsFillable,
    decimal EffectivePrice,
    decimal EffectiveSlippageRate,
    decimal MaximumExecutableQuantity,
    bool IsParticipationCapped,
    string? RejectionOrAdjustmentReason);

public static class RealisticFillModel
{
    /// <summary>
    /// Evaluates realistic fill constraints given bar high/low/volume dynamics.
    /// </summary>
    public static RealisticFillEvaluationResult EvaluateBuyFill(
        decimal referencePrice,
        decimal highPrice,
        decimal lowPrice,
        decimal barVolume,
        decimal barQuoteVolume,
        decimal baseSlippageRate,
        decimal requestedQuantity,
        RealisticFillModelParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(referencePrice);

        if (parameters.EnforceIntraBarPriceRange && lowPrice > referencePrice)
        {
            return new RealisticFillEvaluationResult(
                IsFillable: false,
                EffectivePrice: 0m,
                EffectiveSlippageRate: 0m,
                MaximumExecutableQuantity: 0m,
                IsParticipationCapped: false,
                RejectionOrAdjustmentReason: "INTRA_BAR_LOW_ABOVE_REFERENCE_PRICE");
        }

        if (parameters.MaxParticipationRate > 0m && barVolume <= 0m)
        {
            return new RealisticFillEvaluationResult(
                IsFillable: false,
                EffectivePrice: 0m,
                EffectiveSlippageRate: 0m,
                MaximumExecutableQuantity: 0m,
                IsParticipationCapped: false,
                RejectionOrAdjustmentReason: "INSUFFICIENT_BAR_VOLUME");
        }

        var maxExecutableQty = requestedQuantity;
        var isParticipationCapped = false;

        if (barVolume > 0m && parameters.MaxParticipationRate > 0m)
        {
            var volumeCap = barVolume * parameters.MaxParticipationRate;
            if (maxExecutableQty > volumeCap)
            {
                maxExecutableQty = volumeCap;
                isParticipationCapped = true;
            }
        }

        var dynamicSlippage = baseSlippageRate;
        if (parameters.ModelVolumeParticipationSlippage && barVolume > 0m && maxExecutableQty > 0m)
        {
            var effectiveQuoteVolume = barQuoteVolume > 0m ? barQuoteVolume : barVolume * referencePrice;
            var orderNotional = maxExecutableQty * referencePrice;
            var participationRatio = Math.Clamp(orderNotional / effectiveQuoteVolume, 0m, 1.0m);
            dynamicSlippage = Math.Clamp(baseSlippageRate + (participationRatio * parameters.AmihudImpactFactor), 0m, 0.49m);
        }

        var executedPrice = referencePrice * (1m + dynamicSlippage);
        if (parameters.EnforceIntraBarPriceRange && highPrice > 0m && executedPrice > highPrice)
        {
            executedPrice = highPrice;
            dynamicSlippage = Math.Max(0m, (executedPrice - referencePrice) / referencePrice);
        }

        return new RealisticFillEvaluationResult(
            IsFillable: maxExecutableQty > 0m,
            EffectivePrice: Math.Round(executedPrice, 8),
            EffectiveSlippageRate: Math.Round(dynamicSlippage, 8),
            MaximumExecutableQuantity: maxExecutableQty,
            IsParticipationCapped: isParticipationCapped,
            RejectionOrAdjustmentReason: isParticipationCapped ? "PARTICIPATION_RATE_CAPPED" : null);
    }

    public static RealisticFillEvaluationResult EvaluateSellFill(
        decimal referencePrice,
        decimal highPrice,
        decimal lowPrice,
        decimal barVolume,
        decimal barQuoteVolume,
        decimal baseSlippageRate,
        decimal requestedQuantity,
        RealisticFillModelParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(referencePrice);

        if (parameters.EnforceIntraBarPriceRange && highPrice < referencePrice)
        {
            return new RealisticFillEvaluationResult(
                IsFillable: false,
                EffectivePrice: 0m,
                EffectiveSlippageRate: 0m,
                MaximumExecutableQuantity: 0m,
                IsParticipationCapped: false,
                RejectionOrAdjustmentReason: "INTRA_BAR_HIGH_BELOW_REFERENCE_PRICE");
        }

        if (parameters.MaxParticipationRate > 0m && barVolume <= 0m)
        {
            return new RealisticFillEvaluationResult(
                IsFillable: false,
                EffectivePrice: 0m,
                EffectiveSlippageRate: 0m,
                MaximumExecutableQuantity: 0m,
                IsParticipationCapped: false,
                RejectionOrAdjustmentReason: "INSUFFICIENT_BAR_VOLUME");
        }

        var maxExecutableQty = requestedQuantity;
        var isParticipationCapped = false;

        if (barVolume > 0m && parameters.MaxParticipationRate > 0m)
        {
            var volumeCap = barVolume * parameters.MaxParticipationRate;
            if (maxExecutableQty > volumeCap)
            {
                maxExecutableQty = volumeCap;
                isParticipationCapped = true;
            }
        }

        var dynamicSlippage = baseSlippageRate;
        if (parameters.ModelVolumeParticipationSlippage && barVolume > 0m && maxExecutableQty > 0m)
        {
            var effectiveQuoteVolume = barQuoteVolume > 0m ? barQuoteVolume : barVolume * referencePrice;
            var orderNotional = maxExecutableQty * referencePrice;
            var participationRatio = Math.Clamp(orderNotional / effectiveQuoteVolume, 0m, 1.0m);
            dynamicSlippage = Math.Clamp(baseSlippageRate + (participationRatio * parameters.AmihudImpactFactor), 0m, 0.49m);
        }

        var executedPrice = referencePrice * (1m - dynamicSlippage);
        if (parameters.EnforceIntraBarPriceRange && lowPrice > 0m && executedPrice < lowPrice)
        {
            executedPrice = lowPrice;
            dynamicSlippage = Math.Max(0m, (referencePrice - executedPrice) / referencePrice);
        }

        return new RealisticFillEvaluationResult(
            IsFillable: maxExecutableQty > 0m,
            EffectivePrice: Math.Round(executedPrice, 8),
            EffectiveSlippageRate: Math.Round(dynamicSlippage, 8),
            MaximumExecutableQuantity: maxExecutableQty,
            IsParticipationCapped: isParticipationCapped,
            RejectionOrAdjustmentReason: isParticipationCapped ? "PARTICIPATION_RATE_CAPPED" : null);
    }
}
