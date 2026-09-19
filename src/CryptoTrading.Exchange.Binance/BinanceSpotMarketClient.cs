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
}
