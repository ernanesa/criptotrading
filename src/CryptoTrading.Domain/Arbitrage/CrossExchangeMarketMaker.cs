namespace CryptoTrading.Domain.Arbitrage;

/// <summary>Calculated cross-exchange market maker two-sided quote (US-153).</summary>
public sealed record XemQuote(
    decimal MakerBidPrice,
    decimal MakerAskPrice,
    decimal ExpectedSpreadBps,
    bool IsViable,
    string Reason);

/// <summary>
/// Cross-Exchange Market Making (XEMM) pricing engine (US-152, US-153).
/// Generates maker quotes on primary venue backed by instantaneous taker hedging on secondary venue.
/// </summary>
public static class CrossExchangeMarketMaker
{
    public const decimal DefaultMinSpreadHurdleBps = 15.0m; // 15 bps minimum net capture

    public static XemQuote CalculateQuotes(
        decimal hedgeVenueBidPrice, // Best bid on taker venue (where we sell if maker buy fills)
        decimal hedgeVenueAskPrice, // Best ask on taker venue (where we buy if maker sell fills)
        decimal takerFeeRate = 0.001m, // 10 bps
        decimal makerFeeRate = 0.0005m, // 5 bps
        decimal minSpreadHurdleBps = DefaultMinSpreadHurdleBps)
    {
        if (hedgeVenueBidPrice <= 0m || hedgeVenueAskPrice <= 0m)
        {
            return new XemQuote(0m, 0m, 0m, IsViable: false, "INVALID_HEDGE_PRICES");
        }

        // Total fee burden per round trip (maker on Venue A + taker on Venue B)
        var feeBps = (takerFeeRate + makerFeeRate) * 10_000.0m;
        var totalRequiredMarginBps = feeBps + minSpreadHurdleBps;
        var marginMultiplier = totalRequiredMarginBps / 10_000.0m;

        // Maker Bid: Must be low enough that selling on hedge venue covers cost
        // MakerBid = HedgeBid * (1 - marginMultiplier)
        var makerBid = hedgeVenueBidPrice * (1.0m - marginMultiplier);

        // Maker Ask: Must be high enough that buying on hedge venue covers cost
        // MakerAsk = HedgeAsk * (1 + marginMultiplier)
        var makerAsk = hedgeVenueAskPrice * (1.0m + marginMultiplier);

        var spreadBps = ((makerAsk - makerBid) / makerBid) * 10_000.0m;

        var isViable = makerBid > 0m && makerAsk > makerBid;

        return new XemQuote(
            Math.Round(makerBid, 2),
            Math.Round(makerAsk, 2),
            Math.Round(spreadBps, 2),
            IsViable: isViable,
            isViable ? "XEMM_QUOTES_CALCULATED: Valid spread covering fees and profit hurdle." : "NON_VIABLE");
    }
}
