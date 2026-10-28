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
    string Interval,
    decimal QuoteVolume = 0m,
    long NumberOfTrades = 0,
    decimal TakerBuyBaseVolume = 0m,
    decimal TakerBuyQuoteVolume = 0m);

/// <summary>Public aggregate trade evidence emitted by the Binance matching engine.</summary>
public sealed record BinanceAggregateTrade(
    long AggregateTradeId,
    decimal Price,
    decimal Quantity,
    long FirstTradeId,
    long LastTradeId,
    DateTimeOffset OccurredAtUtc,
    bool IsBuyerMaker);

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
    [property: JsonPropertyName("symbols")] IReadOnlyList<BinanceSymbolInfo> Symbols,
    [property: JsonPropertyName("exchangeFilters")] IReadOnlyList<BinanceExchangeFilter>? ExchangeFilters = null);

public sealed record BinanceSymbolInfo(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("baseAsset")] string BaseAsset,
    [property: JsonPropertyName("quoteAsset")] string QuoteAsset,
    [property: JsonPropertyName("baseAssetPrecision")] int BaseAssetPrecision,
    [property: JsonPropertyName("quoteAssetPrecision")] int QuoteAssetPrecision,
    [property: JsonPropertyName("filters")] IReadOnlyList<BinanceSymbolFilter>? Filters = null,
    [property: JsonPropertyName("orderTypes")] IReadOnlyList<string>? OrderTypes = null);

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
    [property: JsonPropertyName("avgPriceMins")] int? AveragePriceMinutes = null,
    [property: JsonPropertyName("multiplierUp")] string? MultiplierUp = null,
    [property: JsonPropertyName("multiplierDown")] string? MultiplierDown = null,
    [property: JsonPropertyName("bidMultiplierUp")] string? BidMultiplierUp = null,
    [property: JsonPropertyName("bidMultiplierDown")] string? BidMultiplierDown = null,
    [property: JsonPropertyName("askMultiplierUp")] string? AskMultiplierUp = null,
    [property: JsonPropertyName("askMultiplierDown")] string? AskMultiplierDown = null,
    [property: JsonPropertyName("maxPosition")] string? MaxPosition = null,
    [property: JsonPropertyName("maxNumOrders")] long? MaxNumOrders = null);

/// <summary>
/// Reference price for a symbol (<c>GET /api/v3/referencePrice</c>, weight 2).
/// <see cref="Price"/> is null when the venue has no reference price set; the
/// call is rejected when no reference price was ever set.
/// </summary>
public sealed record BinanceReferencePrice(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("referencePrice")] string? ReferencePrice,
    [property: JsonPropertyName("timestamp")] long Timestamp);

/// <summary>
/// Exchange-wide filter advertised at the root of <c>exchangeInfo</c>.
/// Numeric limits are kept as raw JSON so validation can decide exactly what
/// it supports instead of silently receiving a default for an unknown shape.
/// See https://github.com/binance/binance-spot-api-docs/blob/master/filters.md#exchange-filters
/// </summary>
public sealed record BinanceExchangeFilter(
    [property: JsonPropertyName("filterType")] string FilterType,
    [property: JsonPropertyName("maxNumOrders")] long? MaxNumOrders = null,
    [property: JsonPropertyName("maxNumAlgoOrders")] long? MaxNumAlgoOrders = null);

/// <summary>
/// Account-specific filters returned by <c>GET /api/v3/myFilters</c> (USER_DATA,
/// weight 40). This is the only endpoint that reports whether an account has
/// <c>MAX_ASSET</c> filters applied.
/// See https://github.com/binance/binance-spot-api-docs/blob/master/rest-api.md#query-relevant-filters-user_data
/// </summary>
public sealed record BinanceAccountFilters(
    [property: JsonPropertyName("exchangeFilters")] IReadOnlyList<BinanceExchangeFilter>? ExchangeFilters,
    [property: JsonPropertyName("symbolFilters")] IReadOnlyList<BinanceSymbolFilter>? SymbolFilters,
    [property: JsonPropertyName("assetFilters")] IReadOnlyList<BinanceAssetFilter>? AssetFilters,
    DateTimeOffset ObservedAtUtc)
{
    /// <summary>True when every advertised filter is one this adapter can reason about.</summary>
    public bool HasOnlyKnownFilterTypes =>
        (ExchangeFilters ?? []).All(static filter =>
            filter.FilterType is "EXCHANGE_MAX_NUM_ORDERS" or "EXCHANGE_MAX_NUM_ALGO_ORDERS") &&
        (AssetFilters ?? []).All(static filter => filter.FilterType is "MAX_ASSET");
}

/// <summary>
/// Per-asset order limit. <c>asset</c> is the asset whose amount is compared with
/// <c>limit</c>; equality with the account's base or quote asset determines the
/// unit of the comparison. Binance sends <c>limit</c> as a JSON string, so it is
/// preserved verbatim and parsed with invariant culture to avoid precision loss.
/// </summary>
public sealed record BinanceAssetFilter(
    [property: JsonPropertyName("filterType")] string FilterType,
    [property: JsonPropertyName("asset")] string? Asset = null,
    [property: JsonPropertyName("limit")] string? Limit = null);
