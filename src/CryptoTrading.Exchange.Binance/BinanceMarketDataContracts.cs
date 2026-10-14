using System.Globalization;
using System.Text.Json;

namespace CryptoTrading.Exchange.Binance;

public sealed record BinanceMarketSubscription(string Symbol, BinanceMarketStreamKind Kind, string? Interval = null)
{
    public string ToStreamName()
    {
        var symbol = ToLowerAscii(NormalizeSymbol(Symbol));
        return Kind switch
        {
            BinanceMarketStreamKind.Trade => $"{symbol}@trade",
            BinanceMarketStreamKind.Kline when !string.IsNullOrWhiteSpace(Interval) => $"{symbol}@kline_{ToLowerAscii(Interval.Trim())}",
            BinanceMarketStreamKind.Kline => throw new ArgumentException("Kline subscriptions require an interval.", nameof(Interval)),
            BinanceMarketStreamKind.BookTicker => $"{symbol}@bookTicker",
            _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unsupported market stream kind.")
        };
    }

    internal static string NormalizeSymbol(string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        var normalized = symbol.Trim().ToUpperInvariant();
        if (normalized.Length > 20 || normalized.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException("Symbol must contain only ASCII letters and digits and be at most 20 characters.", nameof(symbol));
        }

        return normalized;
    }

    private static string ToLowerAscii(string value) =>
        string.Create(value.Length, value, static (destination, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                var character = source[index];
                destination[index] = character is >= 'A' and <= 'Z' ? (char)(character + ('a' - 'A')) : character;
            }
        });
}

public enum BinanceMarketStreamKind
{
    Trade,
    Kline,
    BookTicker
}

public abstract record BinanceMarketDataEvent(
    string Stream,
    string Symbol,
    long EventTime,
    DateTimeOffset ReceivedAt);

public sealed record BinanceBookTickerStreamEvent(
    string Stream,
    string Symbol,
    long UpdateId,
    DateTimeOffset ReceivedAt,
    decimal BestBidPrice,
    decimal BestBidQuantity,
    decimal BestAskPrice,
    decimal BestAskQuantity) : BinanceMarketDataEvent(Stream, Symbol, UpdateId, ReceivedAt);

public sealed record BinanceTradeEvent(
    string Stream,
    string Symbol,
    long EventTime,
    DateTimeOffset ReceivedAt,
    long TradeId,
    decimal Price,
    decimal Quantity,
    bool IsBuyerMaker) : BinanceMarketDataEvent(Stream, Symbol, EventTime, ReceivedAt);

public sealed record BinanceKlineEvent(
    string Stream,
    string Symbol,
    long EventTime,
    DateTimeOffset ReceivedAt,
    long OpenTime,
    long CloseTime,
    string Interval,
    decimal Open,
    decimal Close,
    decimal High,
    decimal Low,
    decimal Volume,
    bool IsClosed) : BinanceMarketDataEvent(Stream, Symbol, EventTime, ReceivedAt);

public sealed record BinanceOrderBookLevel(decimal Price, decimal Quantity);

public sealed record BinanceDepthUpdateEvent(
    string Stream,
    string Symbol,
    long EventTime,
    DateTimeOffset ReceivedAt,
    long FirstUpdateId,
    long FinalUpdateId,
    long? PreviousFinalUpdateId,
    IReadOnlyList<BinanceOrderBookLevel> Bids,
    IReadOnlyList<BinanceOrderBookLevel> Asks) : BinanceMarketDataEvent(Stream, Symbol, EventTime, ReceivedAt);

public sealed record BinanceOrderBookSnapshot(
    string Symbol,
    long LastUpdateId,
    IReadOnlyList<BinanceOrderBookLevel> Bids,
    IReadOnlyList<BinanceOrderBookLevel> Asks);

internal static class BinanceMarketDataParser
{
    public static BinanceMarketDataEvent? TryParse(ReadOnlySpan<byte> payload, DateTimeOffset receivedAt)
    {
        try
        {
            return Parse(payload, receivedAt);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public static BinanceMarketDataEvent Parse(ReadOnlySpan<byte> payload, DateTimeOffset receivedAt)
    {
        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        var stream = root.GetProperty("stream").GetString() ?? throw new JsonException("Missing stream name.");
        var data = root.GetProperty("data");

        if (!data.TryGetProperty("e", out var eventTypeProp))
        {
            if (data.TryGetProperty("u", out var updateIdProp) && data.TryGetProperty("b", out _) && data.TryGetProperty("a", out _))
            {
                return ParseBookTicker(stream, data, receivedAt, updateIdProp.GetInt64());
            }

            throw new JsonException("Unsupported market data payload without event type.");
        }

        var eventType = eventTypeProp.GetString();

        return eventType switch
        {
            "trade" => ParseTrade(stream, data, receivedAt),
            "kline" => ParseKline(stream, data, receivedAt),
            "depthUpdate" => ParseDepthUpdate(stream, data, receivedAt),
            _ => throw new JsonException($"Unsupported Binance market event '{eventType}'.")
        };
    }

    private static BinanceBookTickerStreamEvent ParseBookTicker(string stream, JsonElement data, DateTimeOffset receivedAt, long updateId)
    {
        var symbol = data.GetProperty("s").GetString() ?? throw new JsonException("Missing bookTicker symbol.");
        var bidPrice = ParseDecimal(data, "b");
        var bidQuantity = ParseDecimal(data, "B");
        var askPrice = ParseDecimal(data, "a");
        var askQuantity = ParseDecimal(data, "A");

        if (bidPrice <= 0m || askPrice < bidPrice || bidQuantity <= 0m || askQuantity <= 0m)
        {
            throw new JsonException($"Invalid bookTicker quote for symbol '{symbol}': bidPrice={bidPrice}, askPrice={askPrice}, bidQty={bidQuantity}, askQty={askQuantity}");
        }

        return new(stream, symbol, updateId, receivedAt, bidPrice, bidQuantity, askPrice, askQuantity);
    }

    private static BinanceTradeEvent ParseTrade(string stream, JsonElement data, DateTimeOffset receivedAt) =>
        new(
            stream,
            data.GetProperty("s").GetString() ?? throw new JsonException("Missing trade symbol."),
            data.GetProperty("E").GetInt64(),
            receivedAt,
            data.GetProperty("t").GetInt64(),
            ParseDecimal(data, "p"),
            ParseDecimal(data, "q"),
            data.GetProperty("m").GetBoolean());

    private static BinanceKlineEvent ParseKline(string stream, JsonElement data, DateTimeOffset receivedAt)
    {
        var kline = data.GetProperty("k");
        return new BinanceKlineEvent(
            stream,
            kline.GetProperty("s").GetString() ?? throw new JsonException("Missing kline symbol."),
            data.GetProperty("E").GetInt64(),
            receivedAt,
            kline.GetProperty("t").GetInt64(),
            kline.GetProperty("T").GetInt64(),
            kline.GetProperty("i").GetString() ?? throw new JsonException("Missing kline interval."),
            ParseDecimal(kline, "o"),
            ParseDecimal(kline, "c"),
            ParseDecimal(kline, "h"),
            ParseDecimal(kline, "l"),
            ParseDecimal(kline, "v"),
            kline.GetProperty("x").GetBoolean());
    }

    private static BinanceDepthUpdateEvent ParseDepthUpdate(string stream, JsonElement data, DateTimeOffset receivedAt) =>
        new(
            stream,
            data.GetProperty("s").GetString() ?? throw new JsonException("Missing depth symbol."),
            data.GetProperty("E").GetInt64(),
            receivedAt,
            data.GetProperty("U").GetInt64(),
            data.GetProperty("u").GetInt64(),
            data.TryGetProperty("pu", out var previousFinalUpdateId) ? previousFinalUpdateId.GetInt64() : null,
            ParseLevels(data, "b"),
            ParseLevels(data, "a"));

    private static List<BinanceOrderBookLevel> ParseLevels(JsonElement data, string propertyName)
    {
        var values = data.GetProperty(propertyName);
        if (values.ValueKind is not JsonValueKind.Array)
        {
            throw new JsonException($"Depth property '{propertyName}' must be an array.");
        }

        var levels = new List<BinanceOrderBookLevel>(values.GetArrayLength());
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind is not JsonValueKind.Array || value.GetArrayLength() != 2)
            {
                throw new JsonException($"Depth level in '{propertyName}' must contain price and quantity.");
            }

            levels.Add(new BinanceOrderBookLevel(
                ParseDecimal(value[0]),
                ParseDecimal(value[1])));
        }

        return levels;
    }

    private static decimal ParseDecimal(JsonElement element, string propertyName) =>
        decimal.Parse(element.GetProperty(propertyName).GetString() ?? throw new JsonException($"Missing numeric property '{propertyName}'."), CultureInfo.InvariantCulture);

    private static decimal ParseDecimal(JsonElement element) =>
        decimal.Parse(element.GetString() ?? throw new JsonException("Missing numeric level value."), CultureInfo.InvariantCulture);
}
