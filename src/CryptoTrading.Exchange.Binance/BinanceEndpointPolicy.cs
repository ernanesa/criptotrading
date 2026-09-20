using System.Net.WebSockets;

namespace CryptoTrading.Exchange.Binance;

/// <summary>Strict local endpoint policy for Binance adapters.</summary>
internal static class BinanceEndpointPolicy
{
    public static Uri ValidateRest(Uri address, string expectedHost, string expectedPath, string description)
    {
        if (!IsAllowed(address, Uri.UriSchemeHttps, expectedHost, expectedPath))
        {
            throw new InvalidOperationException($"Binance {description} must be the HTTPS allowlisted endpoint.");
        }

        return address.AbsoluteUri.EndsWith('/', StringComparison.Ordinal)
            ? address
            : new Uri(address.AbsoluteUri + "/");
    }

    public static Uri ValidateWebSocket(Uri address, string expectedHost, string expectedPath, string description)
    {
        if (!IsAllowed(address, Uri.UriSchemeWss, expectedHost, expectedPath))
        {
            throw new InvalidOperationException($"Binance {description} must be the allowlisted secure WebSocket endpoint.");
        }

        return address;
    }

    private static bool IsAllowed(Uri address, string expectedScheme, string expectedHost, string expectedPath)
    {
        if (!address.IsAbsoluteUri ||
            !string.Equals(address.Scheme, expectedScheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(address.Host, expectedHost, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(address.UserInfo) ||
            !string.IsNullOrEmpty(address.Query) ||
            !string.IsNullOrEmpty(address.Fragment) ||
            (address.Port != -1 && address.Port != 443))
        {
            return false;
        }

        var actualPath = address.AbsolutePath.TrimEnd('/');
        var requiredPath = expectedPath.TrimEnd('/');
        return string.Equals(actualPath, requiredPath, StringComparison.Ordinal);
    }
}

internal static class BinanceWebSocketLimits
{
    /// <summary>Bound one assembled WebSocket message before JSON parsing.</summary>
    public const int MaxMessageBytes = 1_048_576;

    public static void EnsureWithinLimit(long accumulatedBytes, int nextFragmentBytes)
    {
        if (nextFragmentBytes < 0 || accumulatedBytes > MaxMessageBytes - nextFragmentBytes)
        {
            throw new WebSocketException(WebSocketError.InvalidMessageType, "Binance WebSocket message exceeds the configured byte limit.");
        }
    }
}
