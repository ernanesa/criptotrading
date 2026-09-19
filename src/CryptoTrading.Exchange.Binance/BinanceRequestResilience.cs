using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace CryptoTrading.Exchange.Binance;

public sealed class BinanceRequestResilienceOptions
{
    public int MaxRetries { get; set; } = 3;

    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan ClockDriftTolerance { get; set; } = TimeSpan.FromSeconds(5);

    internal void Validate()
    {
        if (MaxRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetries), "Maximum retries cannot be negative.");
        }

        if (InitialRetryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialRetryDelay), "Initial retry delay must be greater than zero.");
        }

        if (MaxRetryDelay < InitialRetryDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetryDelay), "Maximum retry delay must be at least the initial delay.");
        }

        if (ClockDriftTolerance < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ClockDriftTolerance), "Clock drift tolerance cannot be negative.");
        }
    }
}

public interface IBinanceBackoffPolicy
{
    TimeSpan GetDelay(int attempt, TimeSpan? serverSuggestedDelay = null);
}

/// <summary>
/// Deterministic bounded exponential backoff. Jitter is deliberately not hidden in
/// this primitive so tests and replay runs can reproduce request timing exactly.
/// </summary>
public sealed class BinanceExponentialBackoffPolicy : IBinanceBackoffPolicy
{
    private readonly TimeSpan initialDelay;
    private readonly TimeSpan maximumDelay;

    public BinanceExponentialBackoffPolicy(
        TimeSpan initialDelay,
        TimeSpan maximumDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initialDelay, TimeSpan.Zero);

        if (maximumDelay < initialDelay)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumDelay, initialDelay);
        }

        this.initialDelay = initialDelay;
        this.maximumDelay = maximumDelay;
    }

    public TimeSpan GetDelay(int attempt, TimeSpan? serverSuggestedDelay = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);

        var exponential = ComputeExponentialDelay(initialDelay, maximumDelay, attempt);
        if (serverSuggestedDelay is not { } suggested || suggested <= TimeSpan.Zero)
        {
            return exponential;
        }

        return suggested > maximumDelay ? maximumDelay : suggested;
    }

    internal static TimeSpan ComputeExponentialDelay(TimeSpan initial, TimeSpan maximum, int attempt)
    {
        var multiplier = Math.Min(attempt, 30);
        var factor = 1L << multiplier;
        var ticks = initial.Ticks > maximum.Ticks / factor
            ? maximum.Ticks
            : initial.Ticks * factor;
        return TimeSpan.FromTicks(Math.Min(ticks, maximum.Ticks));
    }
}

public sealed record BinanceRateLimitSnapshot(
    IReadOnlyDictionary<string, long> Counters,
    DateTimeOffset? RetryAfterUtc)
{
    public long? GetCounter(string headerName) =>
        Counters.TryGetValue(headerName, out var value) ? value : null;
}

public static class BinanceRateLimitHeaders
{
    public static BinanceRateLimitSnapshot Parse(HttpResponseHeaders headers, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var counters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in headers)
        {
            if (!header.Key.StartsWith("X-MBX-", StringComparison.OrdinalIgnoreCase) ||
                !header.Key.Contains("USED", StringComparison.OrdinalIgnoreCase) &&
                !header.Key.Contains("COUNT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rawValue = header.Value.FirstOrDefault();
            if (long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                counters[header.Key] = value;
            }
        }

        DateTimeOffset? retryAfter = null;
        if (headers.TryGetValues("Retry-After", out var retryValues))
        {
            var rawRetryAfter = retryValues.FirstOrDefault();
            if (int.TryParse(rawRetryAfter, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                retryAfter = now.AddSeconds(Math.Max(0, seconds));
            }
            else if (DateTimeOffset.TryParse(rawRetryAfter, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var retryDate))
            {
                retryAfter = retryDate.ToUniversalTime();
            }
        }

        return new(counters, retryAfter);
    }
}

public sealed class BinanceRequestRetryPolicy
{
    private readonly BinanceRequestResilienceOptions options;
    private readonly IBinanceBackoffPolicy backoffPolicy;

    public BinanceRequestRetryPolicy(
        BinanceRequestResilienceOptions? options = null,
        IBinanceBackoffPolicy? backoffPolicy = null)
    {
        this.options = options ?? new BinanceRequestResilienceOptions();
        this.options.Validate();
        this.backoffPolicy = backoffPolicy ?? new BinanceExponentialBackoffPolicy(
            this.options.InitialRetryDelay,
            this.options.MaxRetryDelay);
    }

    public bool ShouldRetry(HttpStatusCode statusCode, int attempt) =>
        attempt < options.MaxRetries &&
        (statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout or
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or
            (HttpStatusCode)418);

    public TimeSpan GetDelay(HttpResponseMessage response, int attempt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(response);
        var snapshot = BinanceRateLimitHeaders.Parse(response.Headers, now);
        var suggested = snapshot.RetryAfterUtc is { } retryAfter
            ? retryAfter - now
            : (TimeSpan?)null;
        return backoffPolicy.GetDelay(attempt, suggested);
    }
}

public interface IBinanceClockDriftEstimator
{
    TimeSpan Offset { get; }

    DateTimeOffset EstimateServerNow();

    bool IsWithinTolerance(TimeSpan tolerance);

    void Observe(long serverTimeMilliseconds, DateTimeOffset localSentAt, DateTimeOffset localReceivedAt);
}

/// <summary>
/// Estimates exchange time from the midpoint of a request/response interval,
/// avoiding the one-sided latency bias of comparing against response arrival only.
/// </summary>
public sealed class BinanceClockDriftEstimator : IBinanceClockDriftEstimator
{
    private readonly TimeProvider timeProvider;
    private TimeSpan offset;

    public BinanceClockDriftEstimator(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public TimeSpan Offset => offset;

    public DateTimeOffset EstimateServerNow() => timeProvider.GetUtcNow() + offset;

    public bool IsWithinTolerance(TimeSpan tolerance)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tolerance, TimeSpan.Zero);

        return offset.Duration() <= tolerance;
    }

    public void Observe(long serverTimeMilliseconds, DateTimeOffset localSentAt, DateTimeOffset localReceivedAt)
    {
        if (localReceivedAt < localSentAt)
        {
            throw new ArgumentException("Local receive time cannot precede send time.", nameof(localReceivedAt));
        }

        var midpoint = localSentAt + ((localReceivedAt - localSentAt) / 2);
        var serverTime = DateTimeOffset.FromUnixTimeMilliseconds(serverTimeMilliseconds);
        offset = serverTime - midpoint;
    }
}
