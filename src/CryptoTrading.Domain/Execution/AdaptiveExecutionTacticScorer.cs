namespace CryptoTrading.Domain.Execution;

public enum ExecutionTactic
{
    PassiveMakerPegged,
    PassiveMakerDeep,
    AggressiveTakerImmediate,
    IcebergChunked,
}

/// <summary>Decision produced by adaptive execution tactic scoring (US-083).</summary>
public sealed record ExecutionTacticDecision(
    ExecutionTactic SelectedTactic,
    decimal UrgencyScore,
    decimal ExpectedSlippageBps,
    decimal NetExpectedBenefitBps,
    bool RequiresImmediateFill,
    string Reason,
    int PegOffsetTicks = 0);

/// <summary>
/// Adaptive execution tactic scorer (US-083).
/// Optimizes Maker versus Taker order placement based on alpha decay urgency, book depth, order book imbalance, and fee differentials.
/// </summary>
public static class AdaptiveExecutionTacticScorer
{
    public const decimal DefaultUrgencyTakerThreshold = 0.75m; // Above 75% urgency favors immediate taker

    public static ExecutionTacticDecision SelectTactic(
        decimal signalUrgencyScore, // 0.0 (very patient) to 1.0 (immediate alpha decay)
        decimal topOfBookSpreadBps,
        decimal expectedSlippageBps,
        decimal makerFeeRate,
        decimal takerFeeRate,
        decimal orderBookImbalance = 0m,
        decimal volatilityBps = 0m)
    {
        if (signalUrgencyScore < 0m || signalUrgencyScore > 1.0m)
        {
            throw new ArgumentOutOfRangeException(nameof(signalUrgencyScore), "Urgency score must be between 0.0 and 1.0.");
        }

        var feeDiffBps = (takerFeeRate - makerFeeRate) * 10_000.0m;

        // Modulate urgency by order book imbalance: strong bid dominance nudges urgency up slightly to avoid queue starvation
        var effectiveUrgency = signalUrgencyScore;
        if (orderBookImbalance > 0.40m)
        {
            effectiveUrgency = Math.Min(1.0m, signalUrgencyScore + 0.05m);
        }
        else if (orderBookImbalance < -0.40m)
        {
            effectiveUrgency = Math.Max(0.0m, signalUrgencyScore - 0.05m);
        }

        // Case 1: High urgency / rapid signal decay -> Taker immediate
        if (effectiveUrgency >= DefaultUrgencyTakerThreshold)
        {
            return new ExecutionTacticDecision(
                ExecutionTactic.AggressiveTakerImmediate,
                effectiveUrgency,
                expectedSlippageBps,
                NetExpectedBenefitBps: -feeDiffBps,
                RequiresImmediateFill: true,
                Reason: $"TAKER_EXECUTION: High signal urgency {effectiveUrgency:P0} justifies paying taker fee to prevent alpha edge decay.");
        }

        // Case 2: Tight spread & low slippage -> Passive maker pegged to near touch
        if (topOfBookSpreadBps <= 8.0m)
        {
            // If spread permits (> 3 bps) and market has moderate volatility, step 1 tick inside spread to gain execution priority
            var pegOffsetTicks = (topOfBookSpreadBps >= 3.0m && volatilityBps >= 20.0m) || orderBookImbalance > 0.25m ? 1 : 0;
            return new ExecutionTacticDecision(
                ExecutionTactic.PassiveMakerPegged,
                effectiveUrgency,
                ExpectedSlippageBps: 0.5m,
                NetExpectedBenefitBps: feeDiffBps,
                RequiresImmediateFill: false,
                Reason: $"MAKER_PEGGED: Tight spread ({topOfBookSpreadBps:F1} bps) allows passive maker capture with high fill probability.",
                PegOffsetTicks: pegOffsetTicks);
        }

        // Case 3: Wide spread / patient signal -> Deep maker limit or iceberg
        if (effectiveUrgency <= 0.30m)
        {
            return new ExecutionTacticDecision(
                ExecutionTactic.PassiveMakerDeep,
                effectiveUrgency,
                ExpectedSlippageBps: 0.0m,
                NetExpectedBenefitBps: feeDiffBps + topOfBookSpreadBps,
                RequiresImmediateFill: false,
                Reason: $"MAKER_DEEP: Patient signal ({effectiveUrgency:P0}) and wide spread ({topOfBookSpreadBps:F1} bps) warrant deep passive quoting.");
        }

        // Default: Iceberg / chunked limit
        return new ExecutionTacticDecision(
            ExecutionTactic.IcebergChunked,
            effectiveUrgency,
            expectedSlippageBps,
            NetExpectedBenefitBps: feeDiffBps / 2.0m,
            RequiresImmediateFill: false,
            Reason: "ICEBERG_BALANCED: Moderate urgency and spread balance maker capture against adverse selection risk.");
    }
}
