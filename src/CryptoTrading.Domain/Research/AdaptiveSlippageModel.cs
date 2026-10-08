namespace CryptoTrading.Domain.Research;

/// <summary>Estimated slippage and market impact calculation for execution planning.</summary>
public sealed record SlippageEstimate(
    decimal ExpectedSlippageBps,
    decimal MaxExpectedSlippageBps,
    decimal EstimatedMarketImpactNotional,
    bool IsFeasible,
    string Reason);

/// <summary>
/// Adaptive slippage and market impact model (US-172).
/// Computes dynamic execution cost expectations based on book depth, order size, and volatility.
/// </summary>
public static class AdaptiveSlippageModel
{
    public const decimal DefaultBaseSpreadBps = 2.0m; // 2 basis points minimum spread
    public const decimal DefaultImpactFactor = 15.0m;
    public const decimal DefaultMaxAllowedSlippageBps = 20.0m; // 20 basis points hard limit

    public static SlippageEstimate EstimateSlippage(
        decimal orderNotional,
        decimal availableDepthNotional,
        decimal volatilityRate,
        decimal baseSpreadBps = DefaultBaseSpreadBps,
        decimal impactFactor = DefaultImpactFactor,
        decimal maxAllowedSlippageBps = DefaultMaxAllowedSlippageBps)
    {
        if (orderNotional < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(orderNotional), "Order notional cannot be negative.");
        }

        if (availableDepthNotional <= 0m)
        {
            return new SlippageEstimate(
                ExpectedSlippageBps: decimal.MaxValue,
                MaxExpectedSlippageBps: decimal.MaxValue,
                EstimatedMarketImpactNotional: orderNotional,
                IsFeasible: false,
                Reason: "AVAILABLE_DEPTH_EMPTY: Order book depth is zero or negative.");
        }

        if (volatilityRate < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(volatilityRate), "Volatility rate cannot be negative.");
        }

        // Depth ratio: order size relative to available top-of-book depth
        var depthRatio = orderNotional / availableDepthNotional;

        // Volatility multiplier: sqrt-like scaling proxy
        var volMultiplier = 1.0m + (volatilityRate * 10.0m);

        // Expected slippage: base spread + impact factor * depthRatio * volMultiplier
        var expectedSlippageBps = baseSpreadBps + (impactFactor * depthRatio * volMultiplier);

        // Conservative worst-case bound (99th percentile proxy: 2x expected)
        var maxExpectedSlippageBps = expectedSlippageBps * 2.0m;

        var impactNotional = (orderNotional * expectedSlippageBps) / 10_000.0m;

        var isFeasible = expectedSlippageBps <= maxAllowedSlippageBps;
        var reason = isFeasible
            ? "FEASIBLE: Expected slippage within risk tolerance."
            : $"SLIPPAGE_EXCEEDED: Expected {expectedSlippageBps:F2} bps exceeds max allowed {maxAllowedSlippageBps:F2} bps.";

        return new SlippageEstimate(
            ExpectedSlippageBps: Math.Round(expectedSlippageBps, 4),
            MaxExpectedSlippageBps: Math.Round(maxExpectedSlippageBps, 4),
            EstimatedMarketImpactNotional: Math.Round(impactNotional, 4),
            IsFeasible: isFeasible,
            Reason: reason);
    }
}
