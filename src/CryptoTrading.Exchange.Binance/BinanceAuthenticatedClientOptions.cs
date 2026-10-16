namespace CryptoTrading.Exchange.Binance;

/// <summary>
/// Credentials and safety limits used by the authenticated read-only Spot API client.
/// This type deliberately has no options for withdrawals, transfers, or order mutation.
/// </summary>
public sealed class BinanceAuthenticatedClientOptions
{
    public BinanceEnvironment Environment { get; set; } = BinanceEnvironment.Testnet;

    public Uri? RestBaseAddress { get; set; }

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public string ApiKey { get; set; } = string.Empty;

    public string ApiSecret { get; set; } = string.Empty;

    public long RecvWindowMilliseconds { get; set; } = 5_000;

    public TimeSpan ClockDriftTolerance { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Optional TTL for reusing a recently observed exchange clock offset before
    /// issuing another GET /api/v3/time round-trip. Defaults to zero (always sync).
    /// </summary>
    public TimeSpan ClockSyncCacheDuration { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Allows a Spot Testnet account payload to report <c>canWithdraw=true</c>.
    /// This is an observation-only compatibility switch for Testnet diagnostics;
    /// it neither enables nor adds a withdrawal endpoint.
    /// </summary>
    public bool AllowTestnetWithdrawCapability { get; set; }

    internal Uri GetRestBaseAddress()
    {
        var defaultAddress = Environment switch
        {
            BinanceEnvironment.Testnet => new Uri("https://testnet.binance.vision/api/"),
            BinanceEnvironment.Demo => new Uri("https://demo-api.binance.com/api/"),
            _ => throw new ArgumentOutOfRangeException(nameof(Environment), Environment, "Unsupported Binance environment.")
        };

        var address = RestBaseAddress ?? defaultAddress;
        var expectedHost = Environment switch
        {
            BinanceEnvironment.Testnet => "testnet.binance.vision",
            BinanceEnvironment.Demo => "demo-api.binance.com",
            _ => throw new ArgumentOutOfRangeException(nameof(Environment), Environment, "Unsupported Binance environment.")
        };
        return BinanceEndpointPolicy.ValidateRest(address, expectedHost, "/api", $"{Environment} REST base address");
    }

    internal void Validate()
    {
        _ = GetRestBaseAddress();

        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout), "Request timeout must be greater than zero and at most two minutes.");
        }

        if (string.IsNullOrWhiteSpace(ApiKey) || ApiKey.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Binance API key is required and cannot contain whitespace.", nameof(ApiKey));
        }

        if (string.IsNullOrWhiteSpace(ApiSecret) || ApiSecret.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Binance API secret is required and cannot contain whitespace.", nameof(ApiSecret));
        }

        if (RecvWindowMilliseconds is < 1 or > 60_000)
        {
            throw new ArgumentOutOfRangeException(nameof(RecvWindowMilliseconds), "Binance recvWindow must be between 1 and 60,000 milliseconds.");
        }

        if (ClockDriftTolerance < TimeSpan.Zero || ClockDriftTolerance > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(ClockDriftTolerance), "Clock drift tolerance must be between zero and one minute.");
        }
    }
}
