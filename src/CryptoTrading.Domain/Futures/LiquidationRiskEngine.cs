namespace CryptoTrading.Domain.Futures;

/// <summary>Assessment result evaluating proximity to liquidation price.</summary>
public sealed record LiquidationSafetyAssessment(
    decimal LiquidationPrice,
    decimal DistanceToLiquidationBps,
    bool IsSafe,
    bool RequiresMarginCall,
    bool RequiresEmergencyDeleveraging,
    string Reason);

/// <summary>
/// Futures leverage and liquidation risk engine (US-141).
/// Deterministically monitors positions against liquidation boundaries.
/// </summary>
public static class LiquidationRiskEngine
{
    public const decimal MarginCallThresholdBps = 800.0m; // 8% buffer
    public const decimal EmergencyDeleveragingThresholdBps = 300.0m; // 3% buffer

    public static LiquidationSafetyAssessment AssessPositionRisk(
        FuturesPosition position,
        FuturesContractSpec spec)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(spec);

        if (position.Quantity <= 0m || position.MarkPrice <= 0m)
        {
            return new LiquidationSafetyAssessment(
                LiquidationPrice: 0m,
                DistanceToLiquidationBps: decimal.MaxValue,
                IsSafe: true,
                RequiresMarginCall: false,
                RequiresEmergencyDeleveraging: false,
                Reason: "NO_ACTIVE_EXPOSURE");
        }

        var liqPrice = position.CalculateLiquidationPrice(spec.MaintenanceMarginRatio);

        var distanceRate = position.Side switch
        {
            PositionSide.LongSide => (position.MarkPrice - liqPrice) / position.MarkPrice,
            PositionSide.ShortSide => (liqPrice - position.MarkPrice) / position.MarkPrice,
            _ => 1.0m
        };

        var distanceBps = Math.Round(Math.Max(0m, distanceRate * 10_000.0m), 2);

        if (distanceBps <= EmergencyDeleveragingThresholdBps)
        {
            return new LiquidationSafetyAssessment(
                liqPrice,
                distanceBps,
                IsSafe: false,
                RequiresMarginCall: true,
                RequiresEmergencyDeleveraging: true,
                Reason: $"EMERGENCY_DELEVERAGING: Distance to liquidation ({distanceBps:F1} bps) breached emergency threshold ({EmergencyDeleveragingThresholdBps:F1} bps).");
        }

        if (distanceBps <= MarginCallThresholdBps)
        {
            return new LiquidationSafetyAssessment(
                liqPrice,
                distanceBps,
                IsSafe: false,
                RequiresMarginCall: true,
                RequiresEmergencyDeleveraging: false,
                Reason: $"MARGIN_CALL_WARNING: Distance to liquidation ({distanceBps:F1} bps) breached warning threshold ({MarginCallThresholdBps:F1} bps).");
        }

        return new LiquidationSafetyAssessment(
            liqPrice,
            distanceBps,
            IsSafe: true,
            RequiresMarginCall: false,
            RequiresEmergencyDeleveraging: false,
            Reason: $"SAFE: Liquidation buffer {distanceBps:F1} bps is adequate.");
    }
}
