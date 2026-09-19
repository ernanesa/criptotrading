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
    Kline
}

public abstract record BinanceMarketDataEvent(
    string Stream,
    string Symbol,
    long EventTime,
    DateTimeOffset ReceivedAt);

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
    }

    public static BinanceMarketDataEvent Parse(ReadOnlySpan<byte> payload, DateTimeOffset receivedAt)
    {
        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        var stream = root.GetProperty("stream").GetString() ?? throw new JsonException("Missing stream name.");
        var data = root.GetProperty("data");
        var eventType = data.GetProperty("e").GetString();

        return eventType switch
        {
            "trade" => ParseTrade(stream, data, receivedAt),
            "kline" => ParseKline(stream, data, receivedAt),
            _ => throw new JsonException($"Unsupported Binance market event '{eventType}'.")
        };
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

    private static decimal ParseDecimal(JsonElement element, string propertyName) =>
        decimal.Parse(element.GetProperty(propertyName).GetString() ?? throw new JsonException($"Missing numeric property '{propertyName}'."), CultureInfo.InvariantCulture);
}
