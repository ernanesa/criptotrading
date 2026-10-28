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
    /// Optional TTL for reusing a recently fetched GET /api/v3/account snapshot
    /// across concurrent symbol evaluations or back-to-back pre-submit checks.
    /// Defaults to zero (always fetch fresh).
    /// </summary>
    public TimeSpan AccountSnapshotCacheDuration { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Optional TTL for reusing a recently fetched GET /api/v3/account/commission
    /// snapshot per symbol. Defaults to zero (always fetch fresh).
    /// </summary>
    public TimeSpan CommissionCacheDuration { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Optional TTL for reusing GET /api/v3/myFilters per symbol. Account filter
    /// configuration changes rarely, and a stale decision is still rejected by
    /// the venue, so a bounded cache saves 40 weight units per submission.
    /// Defaults to five minutes; zero always fetches fresh.
    /// </summary>
    public TimeSpan AccountFiltersCacheDuration { get; set; } = TimeSpan.FromMinutes(5);

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

        if (AccountFiltersCacheDuration < TimeSpan.Zero || AccountFiltersCacheDuration > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(AccountFiltersCacheDuration),
                "Account filters cache duration must be between zero and one hour.");
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
