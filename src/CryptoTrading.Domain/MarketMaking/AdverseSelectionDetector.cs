using CryptoTrading.Domain.Orders;

namespace CryptoTrading.Domain.MarketMaking;

/// <summary>Recorded markout for an executed market making fill (US-163).</summary>
public sealed record FillMarkoutObservation(
    string FillId,
    OrderSide Side,
    decimal FillPrice,
    decimal MarkoutPrice5s,
    DateTimeOffset ExecutedAtUtc)
{
    /// <summary>Positive markout indicates favorable post-fill price move; negative indicates adverse selection.</summary>
    public decimal MarkoutPnl => Side switch
    {
        OrderSide.Buy => MarkoutPrice5s - FillPrice,
        OrderSide.Sell => FillPrice - MarkoutPrice5s,
        _ => 0m
    };

    public bool IsAdverse => MarkoutPnl < 0m;
}

/// <summary>Evaluation result assessing presence of toxic informed flow.</summary>
public sealed record AdverseSelectionAssessment(
    int TotalFillsEvaluated,
    int AdverseFillsCount,
    decimal AdverseRatio,
    decimal AverageMarkoutPnl,
    bool IsToxicFlowDetected,
    bool ShouldWidenSpreads,
    string Reason);

/// <summary>
/// Toxic flow and adverse selection detector (US-163).
/// Analyzes post-trade price markouts to detect predatory or informed flow and defensively adjust quoting parameters.
/// </summary>
public static class AdverseSelectionDetector
{
    public const decimal DefaultToxicRatioThreshold = 0.65m; // 65% adverse fills signals toxic flow

    public static AdverseSelectionAssessment EvaluateMarkouts(
        IReadOnlyList<FillMarkoutObservation> observations,
        decimal toxicThreshold = DefaultToxicRatioThreshold)
    {
        ArgumentNullException.ThrowIfNull(observations);

        if (observations.Count < 5)
        {
            return new AdverseSelectionAssessment(
                observations.Count,
                AdverseFillsCount: 0,
                AdverseRatio: 0m,
                AverageMarkoutPnl: 0m,
                IsToxicFlowDetected: false,
                ShouldWidenSpreads: false,
                Reason: "INSUFFICIENT_OBSERVATIONS: Need at least 5 fills to evaluate markouts.");
        }

        var adverseCount = observations.Count(o => o.IsAdverse);
        var adverseRatio = (decimal)adverseCount / observations.Count;
        var avgMarkout = observations.Average(o => o.MarkoutPnl);

        var isToxic = adverseRatio >= toxicThreshold;

        var reason = isToxic
            ? $"TOXIC_FLOW_DETECTED: {adverseRatio:P1} of fills experienced adverse selection. Widening spreads recommended."
            : $"BENIGN_FLOW: Adverse ratio {adverseRatio:P1} within safe operating bounds.";

        return new AdverseSelectionAssessment(
            observations.Count,
            adverseCount,
            Math.Round(adverseRatio, 4),
            Math.Round(avgMarkout, 4),
            IsToxicFlowDetected: isToxic,
            ShouldWidenSpreads: isToxic,
            reason);
    }
}
