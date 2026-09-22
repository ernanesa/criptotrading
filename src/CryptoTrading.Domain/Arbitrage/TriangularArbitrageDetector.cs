namespace CryptoTrading.Domain.Arbitrage;

/// <summary>Opportunity evaluated for a 3-pair triangular currency cycle (US-151).</summary>
public sealed record TriangularArbitrageOpportunity(
    string CyclePath,
    decimal GrossRateMultiplier,
    decimal NetRateMultiplier,
    decimal NetProfitBps,
    bool IsProfitable,
    string Reason);

/// <summary>
/// Triangular cross-pair arbitrage detector (US-151).
/// Identifies riskless triangular pricing dislocations across three correlated order book legs,
/// rigorously deducting exchange taker fees on all three executions.
/// </summary>
public static class TriangularArbitrageDetector
{
    public const decimal DefaultMinProfitHurdleBps = 10.0m; // 10 bps minimum net profit to execute
    public const decimal DefaultTakerFeeRate = 0.001m; // 10 bps per leg (0.10%)

    /// <summary>
    /// Evaluates triangular cycle: Base (USDT) -> Intermediate A (BTC) -> Intermediate B (ETH) -> Base (USDT).
    /// </summary>
    public static TriangularArbitrageOpportunity EvaluateCycle(
        decimal usdtToBtcAskPrice, // Price to BUY BTC with USDT
        decimal ethToBtcBidPrice,  // Price to SELL ETH for BTC (or btcToEth ask)
        decimal ethToUsdtBidPrice, // Price to SELL ETH for USDT
        decimal takerFeeRate = DefaultTakerFeeRate,
        decimal minProfitHurdleBps = DefaultMinProfitHurdleBps)
    {
        if (usdtToBtcAskPrice <= 0m || ethToBtcBidPrice <= 0m || ethToUsdtBidPrice <= 0m)
        {
            return new TriangularArbitrageOpportunity(
                "USDT->BTC->ETH->USDT",
                0m, 0m, 0m,
                IsProfitable: false,
                Reason: "INVALID_PRICES: Legs must have positive prices.");
        }

        // Leg 1: Buy BTC with USDT -> 1 USDT gets (1 / usdtToBtcAskPrice) BTC
        // Leg 2: Buy ETH with BTC  -> gets (BTC / ethToBtcBidPrice) ETH
        // Leg 3: Sell ETH for USDT -> gets (ETH * ethToUsdtBidPrice) USDT
        var grossMultiplier = (1.0m / usdtToBtcAskPrice) * (1.0m / ethToBtcBidPrice) * ethToUsdtBidPrice;

        // Account for taker fee on all 3 legs: (1 - f)^3
        var feeHaircut = (1.0m - takerFeeRate) * (1.0m - takerFeeRate) * (1.0m - takerFeeRate);
        var netMultiplier = grossMultiplier * feeHaircut;

        var netProfitBps = (netMultiplier - 1.0m) * 10_000.0m;
        var isProfitable = netProfitBps >= minProfitHurdleBps;

        var reason = isProfitable
            ? $"ARBITRAGE_FOUND: Cycle net yield +{netProfitBps:F1} bps exceeds hurdle {minProfitHurdleBps:F1} bps."
            : $"UNPROFITABLE: Cycle net yield {netProfitBps:F1} bps below required hurdle {minProfitHurdleBps:F1} bps after fees.";

        return new TriangularArbitrageOpportunity(
            "USDT->BTC->ETH->USDT",
            Math.Round(grossMultiplier, 6),
            Math.Round(netMultiplier, 6),
            Math.Round(netProfitBps, 2),
            isProfitable,
            reason);
    }
}
