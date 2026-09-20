using System.Text.Json.Serialization;

namespace CryptoTrading.Exchange.Binance;

public sealed record BinanceServerTime(
    [property: JsonPropertyName("serverTime")] long ServerTime);

public sealed record BinanceSpotKline(
    DateTimeOffset OpenTimeUtc,
    DateTimeOffset CloseTimeUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    string Interval);

/// <summary>
/// Public top-of-book evidence. Binance does not include an observation timestamp
/// in this REST payload; the adapter stamps the UTC receive time after validation.
/// </summary>
public sealed record BinanceBookTicker(
    string Symbol,
    decimal BidPrice,
    decimal BidQuantity,
    decimal AskPrice,
    decimal AskQuantity,
    DateTimeOffset ObservedAtUtc)
{
    public decimal BidLiquidityNotional => BidPrice * BidQuantity;

    public decimal AskLiquidityNotional => AskPrice * AskQuantity;
}

public sealed record BinanceExchangeInfo(
    [property: JsonPropertyName("timezone")] string Timezone,
    [property: JsonPropertyName("serverTime")] long ServerTime,
    [property: JsonPropertyName("symbols")] IReadOnlyList<BinanceSymbolInfo> Symbols);

public sealed record BinanceSymbolInfo(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("baseAsset")] string BaseAsset,
    [property: JsonPropertyName("quoteAsset")] string QuoteAsset,
    [property: JsonPropertyName("baseAssetPrecision")] int BaseAssetPrecision,
    [property: JsonPropertyName("quoteAssetPrecision")] int QuoteAssetPrecision,
    [property: JsonPropertyName("filters")] IReadOnlyList<BinanceSymbolFilter>? Filters = null);

/// <summary>
/// The filter payload is intentionally represented as strings. Binance sends decimal
/// quantities as JSON strings and converting them through floating point would make
/// exact tick/step validation unsafe.
/// </summary>
public sealed record BinanceSymbolFilter(
    [property: JsonPropertyName("filterType")] string FilterType,
    [property: JsonPropertyName("minPrice")] string? MinPrice = null,
    [property: JsonPropertyName("maxPrice")] string? MaxPrice = null,
    [property: JsonPropertyName("tickSize")] string? TickSize = null,
    [property: JsonPropertyName("minQty")] string? MinQty = null,
    [property: JsonPropertyName("maxQty")] string? MaxQty = null,
    [property: JsonPropertyName("stepSize")] string? StepSize = null,
    [property: JsonPropertyName("minNotional")] string? MinNotional = null,
    [property: JsonPropertyName("maxNotional")] string? MaxNotional = null,
    [property: JsonPropertyName("applyToMarket")] bool? ApplyToMarket = null,
    [property: JsonPropertyName("avgPriceMins")] int? AveragePriceMinutes = null);
