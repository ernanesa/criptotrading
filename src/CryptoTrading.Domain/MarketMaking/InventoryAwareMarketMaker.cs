namespace CryptoTrading.Domain.MarketMaking;

/// <summary>Sized and skewed market making orders adjusted for inventory control (US-161).</summary>
public sealed record SizedMarketMakingQuotes(
    decimal BidPrice,
    decimal BidQuantity,
    decimal AskPrice,
    decimal AskQuantity,
    bool AllowBid,
    bool AllowAsk,
    string Reason);

/// <summary>
/// Inventory-aware market making quote sizer (US-161).
/// Enforces hard inventory limits and dynamically tilts quote sizes and pricing to return inventory to target neutral.
/// </summary>
public static class InventoryAwareMarketMaker
{
    public static SizedMarketMakingQuotes SizeQuotes(
        decimal baseBidPrice,
        decimal baseAskPrice,
        decimal baseOrderQuantity,
        decimal currentInventory,
        decimal maxAllowedInventory)
    {
        if (baseBidPrice <= 0m || baseAskPrice <= 0m || baseOrderQuantity <= 0m || maxAllowedInventory <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(baseBidPrice), "Parameters must be strictly positive.");
        }

        var inventoryRatio = currentInventory / maxAllowedInventory;

        // Hard inventory limit breaches
        if (currentInventory >= maxAllowedInventory)
        {
            // Max long reached -> Cease bidding entirely, only ask to offload inventory
            return new SizedMarketMakingQuotes(
                BidPrice: 0m,
                BidQuantity: 0m,
                AskPrice: baseAskPrice,
                AskQuantity: baseOrderQuantity * 1.5m,
                AllowBid: false,
                AllowAsk: true,
                Reason: "MAX_LONG_INVENTORY_BREACH: Bidding disabled to avoid inventory accumulation.");
        }

        if (currentInventory <= -maxAllowedInventory)
        {
            // Max short reached -> Cease asking entirely, only bid to cover inventory
            return new SizedMarketMakingQuotes(
                BidPrice: baseBidPrice,
                BidQuantity: baseOrderQuantity * 1.5m,
                AskPrice: 0m,
                AskQuantity: 0m,
                AllowBid: true,
                AllowAsk: false,
                Reason: "MAX_SHORT_INVENTORY_BREACH: Asking disabled to avoid short accumulation.");
        }

        // Dynamic size skewing:
        // When long (ratio > 0), reduce bid qty and increase ask qty
        var bidQty = baseOrderQuantity * Math.Max(0.2m, 1.0m - inventoryRatio);
        var askQty = baseOrderQuantity * Math.Max(0.2m, 1.0m + inventoryRatio);

        return new SizedMarketMakingQuotes(
            baseBidPrice,
            Math.Round(bidQty, 4),
            baseAskPrice,
            Math.Round(askQty, 4),
            AllowBid: true,
            AllowAsk: true,
            Reason: $"INVENTORY_BALANCED: Inventory ratio at {inventoryRatio:P1}. Sizes skewed dynamically.");
    }
}
