using System.Net;
using Xunit;

namespace CryptoTrading.Exchange.Binance.Tests;

public sealed class BinanceRequestResilienceTests
{
    [Fact]
    public void ExponentialBackoffIsBoundedAndHonorsServerRetryAfter()
    {
        var policy = new BinanceExponentialBackoffPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(1), policy.GetDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.GetDelay(10));
        Assert.Equal(TimeSpan.FromSeconds(3), policy.GetDelay(0, TimeSpan.FromSeconds(3)));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.GetDelay(0, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void RateLimitHeadersCaptureWeightOrderCountersAndRetryAfter()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("X-MBX-USED-WEIGHT-1M", "42");
        response.Headers.TryAddWithoutValidation("X-MBX-ORDER-COUNT-10S", "3");
        response.Headers.TryAddWithoutValidation("Retry-After", "2");
        var now = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

        var snapshot = BinanceRateLimitHeaders.Parse(response.Headers, now);

        Assert.Equal(42, snapshot.GetCounter("X-MBX-USED-WEIGHT-1M"));
        Assert.Equal(3, snapshot.GetCounter("X-MBX-ORDER-COUNT-10S"));
        Assert.Equal(now.AddSeconds(2), snapshot.RetryAfterUtc);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData((HttpStatusCode)418, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public void RetryPolicyOnlyRetriesTransientOrRateLimitedResponses(HttpStatusCode status, bool expected)
    {
        var policy = new BinanceRequestRetryPolicy(new BinanceRequestResilienceOptions { MaxRetries = 2 });

        Assert.Equal(expected, policy.ShouldRetry(status, 0));
        Assert.False(policy.ShouldRetry(status, 2));
    }

    [Fact]
    public void ClockDriftUsesRequestMidpointAndTolerance()
    {
        var localSent = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        var localReceived = localSent.AddSeconds(2);
        var estimator = new BinanceClockDriftEstimator(new FixedTimeProvider(localReceived));

        estimator.Observe(
            new DateTimeOffset(2026, 9, 19, 12, 0, 5, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            localSent,
            localReceived);

        Assert.Equal(TimeSpan.FromSeconds(4), estimator.Offset);
        Assert.Equal(localReceived.AddSeconds(4), estimator.EstimateServerNow());
        Assert.True(estimator.IsWithinTolerance(TimeSpan.FromSeconds(4)));
        Assert.False(estimator.IsWithinTolerance(TimeSpan.FromSeconds(3.999)));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
