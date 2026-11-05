using System.Net;

namespace CryptoTrading.Exchange.Binance;

public sealed class BinanceApiException : HttpRequestException
{
    public BinanceApiException()
    {
        Endpoint = string.Empty;
    }

    public BinanceApiException(string message)
        : base(message)
    {
        Endpoint = string.Empty;
    }

    public BinanceApiException(string message, Exception innerException)
        : base(message, innerException)
    {
        Endpoint = string.Empty;
    }

    public BinanceApiException(HttpStatusCode statusCode, string endpoint, string? responseBody = null)
        : base(string.IsNullOrWhiteSpace(responseBody)
            ? $"Binance request to '{endpoint}' failed with HTTP {(int)statusCode} ({statusCode})."
            : $"Binance request to '{endpoint}' failed with HTTP {(int)statusCode} ({statusCode}): {responseBody}", null, statusCode)
    {
        Endpoint = endpoint;
    }

    public string Endpoint { get; }
}
