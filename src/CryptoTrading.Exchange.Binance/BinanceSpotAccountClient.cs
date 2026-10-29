using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CryptoTrading.Exchange.Binance;

/// <summary>
/// Binance Spot signed client restricted to account-state reads.
/// The request builder only emits GET requests and the public surface has no mutation endpoint.
/// </summary>
public sealed class BinanceSpotAccountClient : IBinanceSpotAccountClient, IBinanceAccountFiltersProvider, IBinanceAccountTradingStateProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;
    private readonly BinanceAuthenticatedClientOptions options;
    private readonly IBinanceClockDriftEstimator clock;
    private readonly TimeProvider timeProvider;
    private readonly BinanceRequestRetryPolicy retryPolicy;
    private readonly BinanceRateLimitGate rateLimitGate;
    private volatile CachedAccountEntry? cachedAccount;
    private readonly ConcurrentDictionary<string, CachedCommissionEntry> cachedCommissions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BinanceAccountFilters> cachedFilters = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CachedAccountEntry(BinanceAccountSnapshot Snapshot, DateTimeOffset FetchedAtUtc);
    private sealed record CachedCommissionEntry(BinanceCommissionSnapshot Snapshot, DateTimeOffset FetchedAtUtc);

    public BinanceSpotAccountClient(
        HttpClient httpClient,
        BinanceAuthenticatedClientOptions options,
        IBinanceClockDriftEstimator? clock = null,
        TimeProvider? timeProvider = null,
        BinanceRequestRetryPolicy? retryPolicy = null,
        BinanceRateLimitGate? rateLimitGate = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.options.Validate();
        this.clock = clock ?? new BinanceClockDriftEstimator(timeProvider);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.retryPolicy = retryPolicy ?? new BinanceRequestRetryPolicy();
        this.rateLimitGate = rateLimitGate ?? new BinanceRateLimitGate();
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public BinanceSpotAccountClient(
        HttpClient httpClient,
        IOptions<BinanceAuthenticatedClientOptions> options,
        IBinanceClockDriftEstimator? clock = null,
        BinanceRequestRetryPolicy? retryPolicy = null)
        : this(httpClient, options?.Value ?? throw new ArgumentNullException(nameof(options)), clock, retryPolicy: retryPolicy)
    {
    }

    public async Task<BinanceAccountSnapshot> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        if (options.AccountSnapshotCacheDuration > TimeSpan.Zero &&
            cachedAccount is { } cached &&
            now >= cached.FetchedAtUtc &&
            now - cached.FetchedAtUtc <= options.AccountSnapshotCacheDuration)
        {
            return cached.Snapshot;
        }

        await EnsureClockSynchronizedAsync(cancellationToken).ConfigureAwait(false);
        var account = await SendSignedAsync<BinanceAccountSnapshot>("v3/account", [], cancellationToken).ConfigureAwait(false);
        var allowTestnetWithdrawCapability = options.Environment == BinanceEnvironment.Testnet &&
                                             options.AllowTestnetWithdrawCapability;
        var validated = BinanceAuthenticatedResponseValidator.Validate(account, allowTestnetWithdrawCapability);
        if (options.AccountSnapshotCacheDuration > TimeSpan.Zero)
        {
            cachedAccount = new CachedAccountEntry(validated, timeProvider.GetUtcNow());
        }

        return validated;
    }

    public async Task<IReadOnlyList<BinanceOpenOrder>> GetOpenOrdersAsync(
        string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureClockSynchronizedAsync(cancellationToken).ConfigureAwait(false);
        var parameters = new List<KeyValuePair<string, string>>();
        if (symbol is not null)
        {
            parameters.Add(new("symbol", NormalizeSymbol(symbol)));
        }

        var orders = await SendSignedAsync<List<BinanceOpenOrder>>("v3/openOrders", parameters, cancellationToken).ConfigureAwait(false);
        return BinanceAuthenticatedResponseValidator.Validate(orders);
    }

    /// <summary>
    /// Queries <c>GET /api/v3/myFilters</c> (USER_DATA, weight 40). This is the
    /// only endpoint that reports whether the account has MAX_ASSET filters.
    /// The result is evidence only; it does not authorize an order by itself.
    /// </summary>
    public async Task<BinanceAccountFilters> GetAccountFiltersAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        if (options.AccountFiltersCacheDuration > TimeSpan.Zero &&
            cachedFilters.TryGetValue(normalizedSymbol, out var cached) &&
            timeProvider.GetUtcNow() >= cached.ObservedAtUtc &&
            timeProvider.GetUtcNow() - cached.ObservedAtUtc <= options.AccountFiltersCacheDuration)
        {
            // ObservedAtUtc is preserved so callers can see the evidence age.
            return cached;
        }

        await EnsureClockSynchronizedAsync(cancellationToken).ConfigureAwait(false);
        var filters = await SendSignedAsync<BinanceAccountFilters>(
            "v3/myFilters",
            [new("symbol", normalizedSymbol)],
            cancellationToken).ConfigureAwait(false) with
        {
            ObservedAtUtc = timeProvider.GetUtcNow()
        };

        if (options.AccountFiltersCacheDuration > TimeSpan.Zero)
        {
            cachedFilters[normalizedSymbol] = filters;
        }

        return filters;
    }

    /// <summary>
    /// Account-wide open order count. Binance caps this per interval; an
    /// unavailable count must be treated as unknown, never as zero.
    /// </summary>
    /// <summary>
    /// Collects inputs for order-count and MAX_POSITION checks. The account and
    /// open-orders endpoints are read independently and are not an atomic
    /// snapshot. Their result is diagnostic and cannot authorize a submission.
    /// </summary>
    public async Task<BinanceAccountTradingState> GetTradingStateAsync(
        string symbol,
        string baseAsset,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseAsset);
        var normalizedSymbol = NormalizeSymbol(symbol);
        var normalizedBaseAsset = BinanceAssetNormalizer.Normalize(baseAsset, nameof(baseAsset));

        var accountTask = GetAccountAsync(cancellationToken);
        var ordersTask = GetOpenOrdersAsync(null, cancellationToken);
        await Task.WhenAll(accountTask, ordersTask).ConfigureAwait(false);

        var account = await accountTask.ConfigureAwait(false);
        var orders = await ordersTask.ConfigureAwait(false);
        var balance = account.Balances.SingleOrDefault(item =>
            string.Equals(BinanceAssetNormalizer.Normalize(item.Asset, "asset"), normalizedBaseAsset, StringComparison.Ordinal))
            ?? throw new JsonException("Base asset balance is unavailable.");
        var position = ParseExact(balance.Free) + ParseExact(balance.Locked);
        var symbolOrderCount = 0;
        foreach (var order in orders)
        {
            var original = ParseExact(order.OriginalQuantity);
            var executed = ParseExact(order.ExecutedQuantity);
            if (executed > original) throw new JsonException("Executed quantity exceeds original quantity.");
            var orderSymbol = BinanceAuthenticatedResponseValidator.NormalizeSymbol(order.Symbol, "symbol");
            if (!string.Equals(orderSymbol, normalizedSymbol, StringComparison.Ordinal)) continue;
            symbolOrderCount++;
            if (string.Equals(order.Side.Trim(), "BUY", StringComparison.OrdinalIgnoreCase)) position += original - executed;
        }
        return new BinanceAccountTradingState(
            orders.Count, symbolOrderCount, position, timeProvider.GetUtcNow(), account.CanTrade);
    }

    private static decimal ParseExact(string? raw)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text) ||
            !decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value < 0m)
        {
            throw new JsonException("Account quantity is not a non-negative invariant decimal.");
        }

        var parts = text.Split('.');
        if (parts.Length > 2 || parts.Any(static part => part.Any(static character => !char.IsAsciiDigit(character))))
        {
            throw new JsonException("Account quantity has an invalid decimal representation.");
        }

        var integerPart = parts[0].TrimStart('0');
        if (integerPart.Length == 0) integerPart = "0";
        var fractionalPart = parts.Length == 2 ? parts[1].TrimEnd('0') : string.Empty;
        var normalized = fractionalPart.Length == 0 ? integerPart : integerPart + "." + fractionalPart;
        if (!string.Equals(normalized, value.ToString("0.############################", CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw new JsonException("Account quantity cannot be represented exactly as decimal.");
        }

        return value;
    }

    public async Task<IReadOnlyList<BinanceTrade>> GetMyTradesAsync(
        string symbol,
        BinanceTradeQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        query ??= new BinanceTradeQuery();
        query.Validate();
        await EnsureClockSynchronizedAsync(cancellationToken).ConfigureAwait(false);

        var parameters = new List<KeyValuePair<string, string>> { new("symbol", normalizedSymbol) };
        if (query.FromId is { } fromId)
        {
            parameters.Add(new("fromId", fromId.ToString(CultureInfo.InvariantCulture)));
        }

        if (query.StartTimeUtc is { } startTime)
        {
            parameters.Add(new("startTime", startTime.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));
        }

        if (query.EndTimeUtc is { } endTime)
        {
            parameters.Add(new("endTime", endTime.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));
        }

        if (query.Limit is { } limit)
        {
            parameters.Add(new("limit", limit.ToString(CultureInfo.InvariantCulture)));
        }

        var trades = await SendSignedAsync<List<BinanceTrade>>("v3/myTrades", parameters, cancellationToken).ConfigureAwait(false);
        return BinanceAuthenticatedResponseValidator.Validate(trades);
    }

    public async Task<BinanceCommissionSnapshot> GetCommissionAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        var now = timeProvider.GetUtcNow();
        if (options.CommissionCacheDuration > TimeSpan.Zero &&
            cachedCommissions.TryGetValue(normalizedSymbol, out var cached) &&
            now >= cached.FetchedAtUtc &&
            now - cached.FetchedAtUtc <= options.CommissionCacheDuration)
        {
            return cached.Snapshot;
        }

        await EnsureClockSynchronizedAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendSignedAsync<BinanceCommissionResponse>(
            "v3/account/commission",
            [new("symbol", normalizedSymbol)],
            cancellationToken).ConfigureAwait(false);
        var fetchedAt = timeProvider.GetUtcNow();
        var snapshot = new BinanceCommissionSnapshot(
            BinanceAuthenticatedResponseValidator.Validate(response, normalizedSymbol),
            fetchedAt);
        if (options.CommissionCacheDuration > TimeSpan.Zero)
        {
            cachedCommissions[normalizedSymbol] = new CachedCommissionEntry(snapshot, fetchedAt);
        }

        return snapshot;
    }

    private ValueTask EnsureClockSynchronizedAsync(CancellationToken cancellationToken)
    {
        if (options.ClockSyncCacheDuration > TimeSpan.Zero &&
            clock.HasFreshObservation(options.ClockSyncCacheDuration, options.ClockDriftTolerance))
        {
            return ValueTask.CompletedTask;
        }

        return new ValueTask(SynchronizeClockAsync(cancellationToken));
    }

    public async Task<BinanceClockSnapshot> SynchronizeClockAsync(CancellationToken cancellationToken = default)
    {
        await rateLimitGate.WaitAsync(timeProvider, cancellationToken).ConfigureAwait(false);
        var sentAt = timeProvider.GetUtcNow();
        using var response = await httpClient.GetAsync(new Uri("v3/time", UriKind.Relative), cancellationToken).ConfigureAwait(false);
        var receivedAt = timeProvider.GetUtcNow();
        if (!response.IsSuccessStatusCode)
        {
            rateLimitGate.ObserveResponse(response, receivedAt);
        }
        await EnsureSuccessAsync(response, "v3/time").ConfigureAwait(false);
        var serverTime = await response.Content.ReadFromJsonAsync<BinanceServerTime>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("Binance returned an empty server-time response.");

        clock.Observe(serverTime.ServerTime, sentAt, receivedAt);
        var withinTolerance = clock.IsWithinTolerance(options.ClockDriftTolerance);
        if (!withinTolerance)
        {
            throw new BinanceClockDriftException(clock.Offset, options.ClockDriftTolerance);
        }

        return new(serverTime.ServerTime, clock.Offset, withinTolerance);
    }

    private async Task<T> SendSignedAsync<T>(
        string endpoint,
        List<KeyValuePair<string, string>> parameters,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            await rateLimitGate.WaitAsync(timeProvider, cancellationToken).ConfigureAwait(false);
            var queryParameters = new List<KeyValuePair<string, string>>(parameters.Count + 2);
            queryParameters.AddRange(parameters);
            queryParameters.Add(new("recvWindow", options.RecvWindowMilliseconds.ToString(CultureInfo.InvariantCulture)));
            queryParameters.Add(new("timestamp", clock.EstimateServerNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));
            var query = BuildQuery(queryParameters);
            var signature = Sign(query, options.ApiSecret);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{endpoint}?{query}&signature={signature}", UriKind.Relative));
            request.Headers.TryAddWithoutValidation("X-MBX-APIKEY", options.ApiKey);

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var observedAt = timeProvider.GetUtcNow();
                rateLimitGate.ObserveResponse(response, observedAt);
                var retryAfter = BinanceRateLimitHeaders.Parse(response.Headers, observedAt).RetryAfterUtc;
                if ((response.StatusCode is HttpStatusCode.TooManyRequests or (HttpStatusCode)418) &&
                    (retryAfter is null || retryAfter < observedAt))
                {
                    throw new BinanceApiException(response.StatusCode, endpoint);
                }
                if (!retryPolicy.ShouldRetry(response.StatusCode, attempt))
                {
                    throw new BinanceApiException(response.StatusCode, endpoint);
                }

                var delay = retryPolicy.GetDelay(response, attempt, observedAt);
                await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
                continue;
            }

            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new JsonException($"Binance returned an empty response for '{endpoint}'.");
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string endpoint)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new BinanceApiException(response.StatusCode, endpoint);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static string BuildQuery(IReadOnlyList<KeyValuePair<string, string>> parameters) =>
        string.Join('&', parameters.Select(parameter =>
            $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308", Justification = "Binance accepts and documents lowercase hexadecimal HMAC signatures.")]
    internal static string Sign(string query, string secret)
    {
        var queryBytes = Encoding.UTF8.GetBytes(query);
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            var signatureBytes = HMACSHA256.HashData(secretBytes, queryBytes);
            try
            {
                return Convert.ToHexString(signatureBytes).ToLowerInvariant();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signatureBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(queryBytes);
            CryptographicOperations.ZeroMemory(secretBytes);
        }
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

public sealed class BinanceClockDriftException : InvalidOperationException
{
    public BinanceClockDriftException()
        : this(TimeSpan.Zero, TimeSpan.Zero)
    {
    }

    public BinanceClockDriftException(string message)
        : base(message)
    {
    }

    public BinanceClockDriftException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public BinanceClockDriftException(TimeSpan observedOffset, TimeSpan tolerance)
        : base($"Binance server clock drift ({observedOffset.TotalMilliseconds:0} ms) exceeds the configured tolerance ({tolerance.TotalMilliseconds:0} ms).")
    {
        ObservedOffset = observedOffset;
        Tolerance = tolerance;
    }

    public TimeSpan ObservedOffset { get; }

    public TimeSpan Tolerance { get; }
}
