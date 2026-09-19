using System.Text.Json.Serialization;

namespace CryptoTrading.Exchange.Binance;

public sealed record BinanceServerTime(
    [property: JsonPropertyName("serverTime")] long ServerTime);

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
    [property: JsonPropertyName("quoteAssetPrecision")] int QuoteAssetPrecision);
