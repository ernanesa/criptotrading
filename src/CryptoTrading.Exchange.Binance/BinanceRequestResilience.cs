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
/// Deterministic exponential backoff bounded locally, with any server deadline as
/// a minimum. Jitter is deliberately not hidden so replay timing is reproducible.
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

        return suggested > exponential ? suggested : exponential;
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
        var delay = backoffPolicy.GetDelay(attempt, suggested);
        return suggested is { } minimum && minimum > delay ? minimum : delay;
    }
}

/// <summary>
/// Process-local gate shared by callers so that a server rate-limit deadline is
/// honored by concurrent requests, not only by the request that received it.
/// It cannot coordinate separate processes or clients sharing one IP.
/// </summary>
public sealed class BinanceRateLimitGate
{
    private long blockedUntilUtcTicks;

    public TimeSpan Remaining(DateTimeOffset now)
    {
        var blockedUntil = Interlocked.Read(ref blockedUntilUtcTicks);
        if (blockedUntil == 0)
        {
            return TimeSpan.Zero;
        }

        var remaining = new DateTimeOffset(blockedUntil, TimeSpan.Zero) - now.ToUniversalTime();
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>Extends, never shortens, the current block from a rate-limited response.</summary>
    public void ObserveResponse(HttpResponseMessage response, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.StatusCode is not (HttpStatusCode.TooManyRequests or (HttpStatusCode)418))
        {
            return;
        }

        if (BinanceRateLimitHeaders.Parse(response.Headers, now).RetryAfterUtc is not { } retryAfter)
        {
            return;
        }

        var candidate = retryAfter.UtcDateTime.Ticks;
        long current;
        do
        {
            current = Interlocked.Read(ref blockedUntilUtcTicks);
            if (candidate <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref blockedUntilUtcTicks, candidate, current) != current);
    }

    public async Task WaitAsync(TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        while (true)
        {
            var remaining = Remaining(timeProvider.GetUtcNow());
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(remaining, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }
}

public interface IBinanceClockDriftEstimator
{
    TimeSpan Offset { get; }

    DateTimeOffset? LastObservedAtUtc { get; }

    DateTimeOffset EstimateServerNow();

    bool IsWithinTolerance(TimeSpan tolerance);

    bool HasFreshObservation(TimeSpan maxAge, TimeSpan tolerance);

    void Observe(long serverTimeMilliseconds, DateTimeOffset localSentAt, DateTimeOffset localReceivedAt);
}

/// <summary>
/// Estimates exchange time from the midpoint of a request/response interval,
/// avoiding the one-sided latency bias of comparing against response arrival only.
/// </summary>
public sealed class BinanceClockDriftEstimator : IBinanceClockDriftEstimator
{
    private readonly TimeProvider timeProvider;
    private long offsetTicks;
    private long lastObservedUtcTicks;

    public BinanceClockDriftEstimator(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public TimeSpan Offset => TimeSpan.FromTicks(Interlocked.Read(ref offsetTicks));

    public DateTimeOffset? LastObservedAtUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref lastObservedUtcTicks);
            return ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
        }
    }

    public DateTimeOffset EstimateServerNow() => timeProvider.GetUtcNow() + Offset;

    public bool IsWithinTolerance(TimeSpan tolerance)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tolerance, TimeSpan.Zero);

        return Offset.Duration() <= tolerance;
    }

    public bool HasFreshObservation(TimeSpan maxAge, TimeSpan tolerance)
    {
        if (maxAge <= TimeSpan.Zero || tolerance < TimeSpan.Zero)
        {
            return false;
        }

        if (LastObservedAtUtc is not { } observedAt)
        {
            return false;
        }

        var age = timeProvider.GetUtcNow() - observedAt;
        return age >= TimeSpan.Zero && age <= maxAge && IsWithinTolerance(tolerance);
    }

    public void Observe(long serverTimeMilliseconds, DateTimeOffset localSentAt, DateTimeOffset localReceivedAt)
    {
        if (localReceivedAt < localSentAt)
        {
            throw new ArgumentException("Local receive time cannot precede send time.", nameof(localReceivedAt));
        }

        var midpoint = localSentAt + ((localReceivedAt - localSentAt) / 2);
        var serverTime = DateTimeOffset.FromUnixTimeMilliseconds(serverTimeMilliseconds);
        Interlocked.Exchange(ref offsetTicks, (serverTime - midpoint).Ticks);
        Interlocked.Exchange(ref lastObservedUtcTicks, localReceivedAt.UtcDateTime.Ticks);
    }
}
