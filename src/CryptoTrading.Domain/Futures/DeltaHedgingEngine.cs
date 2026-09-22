using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Domain.Futures;

/// <summary>Proposed hedging action to neutralize directional exposure.</summary>
public sealed record HedgeOrderProposal(
    Symbol Symbol,
    PositionSide? ActionSide,
    decimal HedgeQuantity,
    decimal CurrentNetDelta,
    decimal ProjectedNetDelta,
    bool HedgeRequired,
    string Reason);

/// <summary>
/// Delta hedging engine (US-143).
/// Computes net delta between spot inventory and futures short/long positions to maintain delta-neutral posture.
/// </summary>
public static class DeltaHedgingEngine
{
    public const decimal DefaultMaxAllowedDeltaDrift = 0.01m; // 0.01 BTC delta tolerance

    public static HedgeOrderProposal ComputeHedge(
        Symbol symbol,
        decimal spotInventoryQuantity,
        IReadOnlyList<FuturesPosition> futuresPositions,
        decimal maxAllowedDeltaDrift = DefaultMaxAllowedDeltaDrift)
    {
        ArgumentNullException.ThrowIfNull(futuresPositions);

        decimal futuresDelta = 0m;
        foreach (var pos in futuresPositions)
        {
            if (pos.Symbol == symbol)
            {
                futuresDelta += pos.Side == PositionSide.LongSide ? pos.Quantity : -pos.Quantity;
            }
        }

        var netDelta = spotInventoryQuantity + futuresDelta;

        if (Math.Abs(netDelta) <= maxAllowedDeltaDrift)
        {
            return new HedgeOrderProposal(
                symbol,
                ActionSide: null,
                HedgeQuantity: 0m,
                CurrentNetDelta: Math.Round(netDelta, 4),
                ProjectedNetDelta: Math.Round(netDelta, 4),
                HedgeRequired: false,
                Reason: $"DELTA_NEUTRAL: Current delta {netDelta:F4} is within tolerance {maxAllowedDeltaDrift:F4}.");
        }

        var actionSide = netDelta > 0m ? PositionSide.ShortSide : PositionSide.LongSide;
        var hedgeQuantity = Math.Abs(netDelta);

        return new HedgeOrderProposal(
            symbol,
            actionSide,
            Math.Round(hedgeQuantity, 4),
            CurrentNetDelta: Math.Round(netDelta, 4),
            ProjectedNetDelta: 0m,
            HedgeRequired: true,
            Reason: $"HEDGE_REQUIRED: Net delta {netDelta:F4} exceeds tolerance. Opening {actionSide} {hedgeQuantity:F4} to restore neutrality.");
    }
}
