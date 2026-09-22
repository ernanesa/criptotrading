namespace CryptoTrading.Domain.MarketMaking;

/// <summary>Result of optimal quoting calculated via Avellaneda-Stoikov framework (US-162).</summary>
public sealed record OptimalQuotes(
    decimal MidPrice,
    decimal ReservationPrice,
    decimal OptimalBidPrice,
    decimal OptimalAskPrice,
    decimal OptimalSpread,
    decimal InventorySkew);

/// <summary>
/// Classic Avellaneda-Stoikov high-frequency market making model (US-162).
/// Computes optimal reservation prices and symmetric/asymmetric bid-ask spreads
/// parameterized by inventory risk aversion and market volatility.
/// </summary>
public static class AvellanedaStoikovModel
{
    public const decimal DefaultRiskAversionGamma = 0.1m; // gamma
    public const decimal DefaultOrderBookIntensityKappa = 1.5m; // kappa

    public static OptimalQuotes ComputeQuotes(
        decimal midPrice,
        decimal currentInventoryQuantity, // q
        decimal volatilitySigma, // sigma
        decimal timeHorizonFraction = 1.0m, // (T - t) normalized to 1.0
        decimal riskAversionGamma = DefaultRiskAversionGamma,
        decimal orderBookIntensityKappa = DefaultOrderBookIntensityKappa)
    {
        if (midPrice <= 0m || volatilitySigma <= 0m || riskAversionGamma <= 0m || orderBookIntensityKappa <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(midPrice), "Parameters must be strictly positive.");
        }

        // Reservation price: r(s, q, t) = s - q * gamma * sigma^2 * (T - t)
        var sigmaSquared = volatilitySigma * volatilitySigma;
        var inventorySkew = currentInventoryQuantity * riskAversionGamma * sigmaSquared * timeHorizonFraction;
        var reservationPrice = midPrice - inventorySkew;

        // Optimal total spread: s_opt = gamma * sigma^2 * (T - t) + (2 / gamma) * ln(1 + gamma / kappa)
        var lnTerm = (decimal)Math.Log((double)(1.0m + (riskAversionGamma / orderBookIntensityKappa)));
        var totalSpread = (riskAversionGamma * sigmaSquared * timeHorizonFraction) + ((2.0m / riskAversionGamma) * lnTerm);
        var halfSpread = totalSpread / 2.0m;

        // Optimal bid and ask placed around reservation price
        var optimalBid = Math.Max(0m, reservationPrice - halfSpread);
        var optimalAsk = reservationPrice + halfSpread;

        return new OptimalQuotes(
            Math.Round(midPrice, 2),
            Math.Round(reservationPrice, 2),
            Math.Round(optimalBid, 2),
            Math.Round(optimalAsk, 2),
            Math.Round(optimalAsk - optimalBid, 2),
            Math.Round(inventorySkew, 4));
    }
}
