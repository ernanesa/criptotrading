using CryptoTrading.Domain.Orders;

namespace CryptoTrading.Domain.MarketMaking;

/// <summary>Estimated position of a limit order inside the price level queue (US-160).</summary>
public sealed record QueuePositionEstimate(
    OrderSide Side,
    decimal Price,
    decimal QuantityAhead,
    decimal OrderQuantity,
    decimal EstimatedFillProbability,
    bool IsAtFrontOfQueue);

/// <summary>
/// Level 3 (L3) order book queue position simulator (US-160).
/// Simulates FIFO price-time priority and queue depletion based on inbound matched trade flow.
/// </summary>
public sealed class OrderBookQueueSimulator
{
    public static QueuePositionEstimate EstimateQueuePosition(
        OrderSide side,
        decimal price,
        decimal totalLevelVolume,
        decimal executedVolumeSincePlacement,
        decimal myOrderQuantity)
    {
        if (price <= 0m || totalLevelVolume < 0m || myOrderQuantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(price), "Price and order quantity must be strictly positive.");
        }

        // Remaining volume ahead of our order at this price level
        var initialVolumeAhead = Math.Max(0m, totalLevelVolume - myOrderQuantity);
        var remainingAhead = Math.Max(0m, initialVolumeAhead - executedVolumeSincePlacement);

        var isAtFront = remainingAhead <= 0m;
        
        // Probability of fill estimates: inversely proportional to queue remaining ahead
        var fillProbability = initialVolumeAhead > 0m
            ? Math.Clamp(executedVolumeSincePlacement / (initialVolumeAhead + myOrderQuantity), 0m, 1.0m)
            : 1.0m;

        return new QueuePositionEstimate(
            side,
            price,
            Math.Round(remainingAhead, 4),
            myOrderQuantity,
            Math.Round(fillProbability, 4),
            isAtFront);
    }
}
