using System.Text.Json.Serialization;

namespace CryptoTrading.Exchange.Binance;

public sealed record BinanceAccountSnapshot(
    [property: JsonPropertyName("makerCommission")] int MakerCommission,
    [property: JsonPropertyName("takerCommission")] int TakerCommission,
    [property: JsonPropertyName("buyerCommission")] int BuyerCommission,
    [property: JsonPropertyName("sellerCommission")] int SellerCommission,
    [property: JsonPropertyName("canTrade")] bool CanTrade,
    [property: JsonPropertyName("canWithdraw")] bool CanWithdraw,
    [property: JsonPropertyName("canDeposit")] bool CanDeposit,
    [property: JsonPropertyName("updateTime")] long UpdateTime,
    [property: JsonPropertyName("accountType")] string AccountType,
    [property: JsonPropertyName("balances")] IReadOnlyList<BinanceBalance> Balances,
    [property: JsonPropertyName("permissions")] IReadOnlyList<string>? Permissions = null);

public sealed record BinanceBalance(
    [property: JsonPropertyName("asset")] string Asset,
    [property: JsonPropertyName("free")] string Free,
    [property: JsonPropertyName("locked")] string Locked);

public sealed record BinanceOpenOrder(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("orderId")] long OrderId,
    [property: JsonPropertyName("clientOrderId")] string ClientOrderId,
    [property: JsonPropertyName("price")] string Price,
    [property: JsonPropertyName("origQty")] string OriginalQuantity,
    [property: JsonPropertyName("executedQty")] string ExecutedQuantity,
    [property: JsonPropertyName("cummulativeQuoteQty")] string CumulativeQuoteQuantity,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("time")] long Time,
    [property: JsonPropertyName("updateTime")] long UpdateTime,
    [property: JsonPropertyName("isWorking")] bool IsWorking,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("side")] string Side,
    [property: JsonPropertyName("timeInForce")] string? TimeInForce = null);

public sealed record BinanceTrade(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("orderId")] long OrderId,
    [property: JsonPropertyName("price")] string Price,
    [property: JsonPropertyName("qty")] string Quantity,
    [property: JsonPropertyName("quoteQty")] string QuoteQuantity,
    [property: JsonPropertyName("commission")] string Commission,
    [property: JsonPropertyName("commissionAsset")] string CommissionAsset,
    [property: JsonPropertyName("time")] long Time,
    [property: JsonPropertyName("isBuyer")] bool IsBuyer,
    [property: JsonPropertyName("isMaker")] bool IsMaker,
    [property: JsonPropertyName("isBestMatch")] bool IsBestMatch);

public sealed record BinanceTradeQuery(
    long? FromId = null,
    DateTimeOffset? StartTimeUtc = null,
    DateTimeOffset? EndTimeUtc = null,
    int? Limit = null)
{
    internal void Validate()
    {
        if (FromId is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(FromId), "Trade fromId cannot be negative.");
        }

        if (StartTimeUtc is { } start && EndTimeUtc is { } end && end < start)
        {
            throw new ArgumentException("Trade end time cannot precede start time.", nameof(EndTimeUtc));
        }

        if (Limit is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(Limit), "Trade limit must be between 1 and 1,000.");
        }
    }
}

public sealed record BinanceClockSnapshot(
    long ServerTimeMilliseconds,
    TimeSpan Offset,
    bool WithinTolerance);

public sealed record BinanceCommissionRateSet(
    [property: JsonPropertyName("maker")] string Maker,
    [property: JsonPropertyName("taker")] string Taker,
    [property: JsonPropertyName("buyer")] string Buyer,
    [property: JsonPropertyName("seller")] string Seller);

public sealed record BinanceCommissionDiscount(
    [property: JsonPropertyName("enabledForAccount")] bool EnabledForAccount,
    [property: JsonPropertyName("enabledForSymbol")] bool EnabledForSymbol,
    [property: JsonPropertyName("discountAsset")] string? DiscountAsset,
    [property: JsonPropertyName("discount")] string? Discount);

public sealed record BinanceCommissionResponse(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("standardCommission")] BinanceCommissionRateSet StandardCommission,
    [property: JsonPropertyName("specialCommission")] BinanceCommissionRateSet SpecialCommission,
    [property: JsonPropertyName("taxCommission")] BinanceCommissionRateSet TaxCommission,
    [property: JsonPropertyName("discount")] BinanceCommissionDiscount Discount);

public sealed record BinanceCommissionSnapshot(
    BinanceCommissionResponse Response,
    DateTimeOffset ObservedAtUtc);
