using System.Globalization;
using System.Text.Json;

namespace CryptoTrading.Exchange.Binance;

public sealed class BinanceSpotMarketClient : IBinanceSpotMarketClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;

    public BinanceSpotMarketClient(HttpClient httpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task PingAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(new Uri("ping", UriKind.Relative), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "ping", cancellationToken).ConfigureAwait(false);
    }

    public Task<BinanceServerTime> GetServerTimeAsync(CancellationToken cancellationToken = default) =>
        GetAsync<BinanceServerTime>("time", cancellationToken);

    public async Task<IReadOnlyList<BinanceSpotKline>> GetKlinesAsync(
        string symbol,
        string interval,
        DateTimeOffset? startTimeUtc = null,
        DateTimeOffset? endTimeUtc = null,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        var normalizedInterval = NormalizeInterval(interval);
        ValidateUtc(startTimeUtc, nameof(startTimeUtc));
        ValidateUtc(endTimeUtc, nameof(endTimeUtc));
        if (startTimeUtc is { } start && endTimeUtc is { } end && start > end)
        {
            throw new ArgumentException("Kline start time cannot exceed end time.");
        }

        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Kline limit must be between 1 and 1000.");
        }

        var query = new List<string>
        {
            $"symbol={Uri.EscapeDataString(normalizedSymbol)}",
            $"interval={Uri.EscapeDataString(normalizedInterval)}",
            $"limit={limit.ToString(CultureInfo.InvariantCulture)}"
        };
        if (startTimeUtc is { } startTime)
        {
            query.Add($"startTime={startTime.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}");
        }

        if (endTimeUtc is { } endTime)
        {
            query.Add($"endTime={endTime.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}");
        }

        using var response = await httpClient.GetAsync(new Uri($"v3/klines?{string.Join('&', query)}", UriKind.Relative), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "klines", cancellationToken).ConfigureAwait(false);
        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind is not JsonValueKind.Array)
        {
            throw new JsonException("Binance klines response must be an array.");
        }

        var result = new List<BinanceSpotKline>(document.RootElement.GetArrayLength());
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind is not JsonValueKind.Array || row.GetArrayLength() < 7)
            {
                throw new JsonException("Binance kline row must contain at least seven values.");
            }

            var openTime = DateTimeOffset.FromUnixTimeMilliseconds(row[0].GetInt64());
            var closeTime = DateTimeOffset.FromUnixTimeMilliseconds(row[6].GetInt64());
            result.Add(new BinanceSpotKline(
                openTime,
                closeTime,
                ParseDecimal(row[1]),
                ParseDecimal(row[2]),
                ParseDecimal(row[3]),
                ParseDecimal(row[4]),
                ParseDecimal(row[5]),
                normalizedInterval));
        }

        return result;
    }

    public async Task<IReadOnlyList<BinanceAggregateTrade>> GetAggregateTradesAsync(
        string symbol,
        DateTimeOffset? endTimeUtc = null,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        ValidateUtc(endTimeUtc, nameof(endTimeUtc));
        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Aggregate trade limit must be between 1 and 1000.");
        }

        var endpoint = $"v3/aggTrades?symbol={Uri.EscapeDataString(normalizedSymbol)}&limit={limit.ToString(CultureInfo.InvariantCulture)}";
        if (endTimeUtc is { } endTime)
        {
            endpoint += $"&endTime={endTime.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}";
        }

        using var response = await httpClient.GetAsync(new Uri(endpoint, UriKind.Relative), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "aggTrades", cancellationToken).ConfigureAwait(false);
        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind is not JsonValueKind.Array)
        {
            throw new JsonException("Binance aggregate trades response must be an array.");
        }

        var trades = new List<BinanceAggregateTrade>(document.RootElement.GetArrayLength());
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object ||
                !row.TryGetProperty("a", out var aggregateId) ||
                !row.TryGetProperty("p", out var price) ||
                !row.TryGetProperty("q", out var quantity) ||
                !row.TryGetProperty("f", out var firstTradeId) ||
                !row.TryGetProperty("l", out var lastTradeId) ||
                !row.TryGetProperty("T", out var occurredAt) ||
                !row.TryGetProperty("m", out var buyerMaker))
            {
                throw new JsonException("Binance aggregate trade response is incomplete.");
            }

            var parsedPrice = ParseDecimal(price);
            var parsedQuantity = ParseDecimal(quantity);
            if (parsedPrice <= 0m || parsedQuantity <= 0m)
            {
                throw new JsonException("Binance aggregate trade contains an invalid price or quantity.");
            }

            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(occurredAt.GetInt64());
            if (endTimeUtc is { } requestedEnd && timestamp > requestedEnd)
            {
                throw new JsonException("Binance aggregate trade exceeds the requested end time.");
            }

            trades.Add(new BinanceAggregateTrade(
                aggregateId.GetInt64(), parsedPrice, parsedQuantity, firstTradeId.GetInt64(), lastTradeId.GetInt64(),
                timestamp, buyerMaker.GetBoolean()));
        }

        return trades;
    }

    public async Task<BinanceBookTicker> GetBookTickerAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        using var response = await httpClient.GetAsync(
            new Uri($"v3/ticker/bookTicker?symbol={Uri.EscapeDataString(normalizedSymbol)}", UriKind.Relative),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "bookTicker", cancellationToken).ConfigureAwait(false);
        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind is not JsonValueKind.Object ||
            !root.TryGetProperty("symbol", out var symbolElement) ||
            !root.TryGetProperty("bidPrice", out var bidPriceElement) ||
            !root.TryGetProperty("bidQty", out var bidQuantityElement) ||
            !root.TryGetProperty("askPrice", out var askPriceElement) ||
            !root.TryGetProperty("askQty", out var askQuantityElement))
        {
            throw new JsonException("Binance book ticker response is incomplete.");
        }

        var responseSymbol = symbolElement.GetString();
        if (!string.Equals(responseSymbol, normalizedSymbol, StringComparison.Ordinal))
        {
            throw new JsonException("Binance book ticker symbol does not match the requested symbol.");
        }

        var bidPrice = ParseDecimal(bidPriceElement);
        var bidQuantity = ParseDecimal(bidQuantityElement);
        var askPrice = ParseDecimal(askPriceElement);
        var askQuantity = ParseDecimal(askQuantityElement);
        if (bidPrice <= 0m || bidQuantity <= 0m || askPrice <= 0m || askQuantity <= 0m || askPrice < bidPrice)
        {
            throw new JsonException("Binance book ticker contains an invalid top-of-book quote.");
        }

        return new BinanceBookTicker(
            normalizedSymbol,
            bidPrice,
            bidQuantity,
            askPrice,
            askQuantity,
            DateTimeOffset.UtcNow);
    }

    public Task<BinanceExchangeInfo> GetExchangeInfoAsync(
        string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        var endpoint = "exchangeInfo";
        if (symbol is not null)
        {
            var normalizedSymbol = NormalizeSymbol(symbol);
            endpoint += $"?symbol={Uri.EscapeDataString(normalizedSymbol)}";
        }

        return GetAsync<BinanceExchangeInfo>(endpoint, cancellationToken);
    }

    public async Task<BinanceSymbolTradingRules> GetTradingRulesAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        var exchangeInfo = await GetExchangeInfoAsync(normalizedSymbol, cancellationToken).ConfigureAwait(false);
        var matchingSymbols = exchangeInfo.Symbols
            .Where(candidate => string.Equals(candidate.Symbol, normalizedSymbol, StringComparison.Ordinal))
            .ToArray();

        return matchingSymbols.Length switch
        {
            1 => matchingSymbols[0].GetTradingRules(),
            0 => throw new FormatException($"Binance exchangeInfo did not contain symbol '{normalizedSymbol}'."),
            _ => throw new FormatException($"Binance exchangeInfo contained duplicate symbol '{normalizedSymbol}'.")
        };
    }

    private async Task<T> GetAsync<T>(string endpoint, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(new Uri(endpoint, UriKind.Relative), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, endpoint, cancellationToken).ConfigureAwait(false);

        var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return value ?? throw new JsonException($"Binance returned an empty response for '{endpoint}'.");
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string endpoint,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw new BinanceApiException(response.StatusCode, endpoint);
    }

    private static string NormalizeSymbol(string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        var normalized = symbol.Trim().ToUpperInvariant();
        if (normalized.Length > 20 || normalized.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException("Symbol must contain only ASCII letters and digits and be at most 20 characters.", nameof(symbol));
        }

        return normalized;
    }

    private static string NormalizeInterval(string interval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interval);
        var normalized = interval.Trim();
        var allowed = new[] { "1s", "1m", "3m", "5m", "15m", "30m", "1h", "2h", "4h", "6h", "8h", "12h", "1d", "3d", "1w", "1M" };
        if (!allowed.Contains(normalized, StringComparer.Ordinal))
        {
            throw new ArgumentException("Unsupported Binance spot kline interval.", nameof(interval));
        }

        return normalized;
    }

    private static void ValidateUtc(DateTimeOffset? value, string name)
    {
        if (value is { } timestamp && timestamp.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC.", name);
        }
    }

    private static decimal ParseDecimal(JsonElement value) =>
        decimal.Parse(value.GetString() ?? throw new JsonException("Kline numeric value is missing."), CultureInfo.InvariantCulture);
}
