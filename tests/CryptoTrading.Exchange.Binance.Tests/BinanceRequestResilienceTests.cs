using System.Net;
using Xunit;

namespace CryptoTrading.Exchange.Binance.Tests;

[Collection(TelemetryActivityFixture.Name)]
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
        Assert.Equal(TimeSpan.FromSeconds(30), policy.GetDelay(0, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void RateLimitGateSharesServerDeadlineAcrossCallers()
    {
        var gate = new BinanceRateLimitGate();
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", "30");

        gate.ObserveResponse(response, now);

        Assert.Equal(TimeSpan.FromSeconds(30), gate.Remaining(now));
        Assert.Equal(TimeSpan.FromSeconds(29), gate.Remaining(now.AddSeconds(1)));
        Assert.Equal(TimeSpan.Zero, gate.Remaining(now.AddSeconds(30)));
    }

    [Fact]
    public void RateLimitGateNeverShortensAnExistingBlock()
    {
        var gate = new BinanceRateLimitGate();
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        using var longBlock = new HttpResponseMessage((HttpStatusCode)418);
        longBlock.Headers.TryAddWithoutValidation("Retry-After", "120");
        using var shortBlock = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        shortBlock.Headers.TryAddWithoutValidation("Retry-After", "5");

        gate.ObserveResponse(longBlock, now);
        gate.ObserveResponse(shortBlock, now);

        Assert.Equal(TimeSpan.FromSeconds(120), gate.Remaining(now));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("2026-01-01")]
    [InlineData("not-a-date")]
    [InlineData("1, 3")]
    public void RetryAfterRejectsNonHttpOrNegativeValues(string raw)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", raw);
        var now = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

        Assert.Null(BinanceRateLimitHeaders.Parse(response.Headers, now).RetryAfterUtc);
    }

    [Fact]
    public void RetryAfterRejectsMultipleDeadlines()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", ["5", "30"]);
        var now = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

        Assert.Null(BinanceRateLimitHeaders.Parse(response.Headers, now).RetryAfterUtc);
    }

    [Fact]
    public void RetryAfterAcceptsValidHttpDateWithoutShorteningIt()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        var now = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        response.Headers.TryAddWithoutValidation("Retry-After", now.AddSeconds(30).ToString("R", System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(now.AddSeconds(30), BinanceRateLimitHeaders.Parse(response.Headers, now).RetryAfterUtc);
    }

    [Fact]
    public async Task RateLimitGateIgnoresSuccessAndResponsesWithoutDeadline()
    {
        var gate = new BinanceRateLimitGate();
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        using var success = new HttpResponseMessage(HttpStatusCode.OK);
        using var withoutHeader = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        gate.ObserveResponse(success, now);
        gate.ObserveResponse(withoutHeader, now);

        Assert.Equal(TimeSpan.Zero, gate.Remaining(now));
        await gate.WaitAsync(TimeProvider.System, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData((HttpStatusCode)418)]
    public async Task RateLimitGateRejectsFurtherRequestsWhenDeadlineIsUnknown(HttpStatusCode status)
    {
        var gate = new BinanceRateLimitGate();
        using var response = new HttpResponseMessage(status);
        gate.ObserveResponse(response, DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.WaitAsync(TimeProvider.System, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData((HttpStatusCode)418)]
    public void RateLimitRetryNeverPrecedesServerDeadline(HttpStatusCode status)
    {
        using var response = new HttpResponseMessage(status);
        response.Headers.TryAddWithoutValidation("Retry-After", "30");
        var now = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        var policy = new BinanceRequestRetryPolicy(new BinanceRequestResilienceOptions
        {
            InitialRetryDelay = TimeSpan.FromSeconds(1),
            MaxRetryDelay = TimeSpan.FromSeconds(5)
        });

        Assert.True(policy.ShouldRetry(status, 0));
        Assert.True(policy.GetDelay(response, 0, now) >= TimeSpan.FromSeconds(30));
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
        Assert.True(estimator.HasFreshObservation(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)));
        Assert.False(estimator.HasFreshObservation(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3.999)));
    }

    [Fact]
    public async Task SpotOrderClientSkipsRedundantTimeSyncWhenClockObservationIsFresh()
    {
        var now = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);
        var clock = new BinanceClockDriftEstimator(timeProvider);
        clock.Observe(now.ToUnixTimeMilliseconds(), now.AddMilliseconds(-20), now);

        var requests = new List<HttpRequestMessage>();
        using var handler = new DelegatingCallbackHandler(req =>
        {
            requests.Add(req);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = req,
                Content = new StringContent(
                    "{\"symbol\":\"BTCUSDT\",\"orderId\":42,\"clientOrderId\":\"ct-fast-1\",\"price\":\"100.50\",\"origQty\":\"0.010\",\"executedQty\":\"0\",\"status\":\"NEW\",\"transactTime\":1789905600000}",
                    System.Text.Encoding.UTF8,
                    "application/json")
            };
        });
        using var httpClient = new HttpClient(handler);
        var client = new BinanceSpotOrderClient(
            httpClient,
            new BinanceSpotOrderClientOptions
            {
                RestBaseAddress = new Uri("https://testnet.binance.vision/api/"),
                ApiKey = "test-api-key",
                ApiSecret = "test-api-secret",
                EnableOrderExecution = true,
                ClockSyncCacheDuration = TimeSpan.FromSeconds(30)
            },
            timeProvider,
            clock);

        var result = await client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.01m, "ct-fast-1", 100.50m),
            TestContext.Current.CancellationToken);

        Assert.Equal(42, result.OrderId);
        Assert.Single(requests);
        Assert.Equal("/api/v3/order", requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task RateLimitedSubmitDoesNotRepeatPost()
    {
        var now = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);
        var clock = new BinanceClockDriftEstimator(timeProvider);
        clock.Observe(now.ToUnixTimeMilliseconds(), now.AddMilliseconds(-20), now);
        var requests = new List<HttpMethod>();
        using var handler = new DelegatingCallbackHandler(request =>
        {
            requests.Add(request.Method);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                RequestMessage = request,
                Content = new StringContent("{\"code\":-1003,\"msg\":\"rate limited\"}")
            };
            response.Headers.TryAddWithoutValidation("Retry-After", "30");
            return response;
        });
        using var httpClient = new HttpClient(handler);
        var client = new BinanceSpotOrderClient(httpClient, new BinanceSpotOrderClientOptions
        {
            RestBaseAddress = new Uri("https://testnet.binance.vision/api/"),
            ApiKey = "test-api-key",
            ApiSecret = "test-api-secret",
            EnableOrderExecution = true,
            ClockSyncCacheDuration = TimeSpan.FromSeconds(30)
        }, timeProvider, clock);

        var error = await Assert.ThrowsAsync<BinanceOrderSubmissionAmbiguousException>(() => client.SubmitAsync(
            new BinanceSpotOrderRequest("BTCUSDT", BinanceSpotOrderSide.Buy, BinanceSpotOrderType.Limit, 0.01m, "ct-rate-limit", 100.50m),
            TestContext.Current.CancellationToken));

        Assert.Equal("ct-rate-limit", error.ClientOrderId);
        Assert.Equal([HttpMethod.Post], requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, null)]
    [InlineData(HttpStatusCode.TooManyRequests, "not-a-date")]
    [InlineData(HttpStatusCode.TooManyRequests, "-1")]
    [InlineData(HttpStatusCode.TooManyRequests, "Sat, 19 Sep 2026 11:59:59 GMT")]
    [InlineData((HttpStatusCode)418, null)]
    [InlineData((HttpStatusCode)418, "not-a-date")]
    [InlineData((HttpStatusCode)418, "-1")]
    [InlineData((HttpStatusCode)418, "Sat, 19 Sep 2026 11:59:59 GMT")]
    public async Task SignedReadDoesNotRetryRateLimitWithoutUsableDeadline(HttpStatusCode status, string? retryAfter)
    {
        var now = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);
        var clock = new BinanceClockDriftEstimator(timeProvider);
        clock.Observe(now.ToUnixTimeMilliseconds(), now.AddMilliseconds(-20), now);
        var requests = 0;
        using var handler = new DelegatingCallbackHandler(request =>
        {
            requests++;
            var response = new HttpResponseMessage(status) { RequestMessage = request, Content = new StringContent("{}") };
            if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return response;
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://testnet.binance.vision/api/") };
        var client = new BinanceSpotAccountClient(httpClient, new BinanceAuthenticatedClientOptions
        {
            RestBaseAddress = new Uri("https://testnet.binance.vision/api/"),
            ApiKey = "test-api-key",
            ApiSecret = "test-api-secret",
            ClockSyncCacheDuration = TimeSpan.FromSeconds(30)
        }, clock, timeProvider, new BinanceRequestRetryPolicy(new BinanceRequestResilienceOptions
        {
            MaxRetries = 2,
            InitialRetryDelay = TimeSpan.FromMilliseconds(1),
            MaxRetryDelay = TimeSpan.FromMilliseconds(2)
        }));

        await Assert.ThrowsAsync<BinanceApiException>(() => client.GetAccountAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task SignedReadBackoffUsesInjectedTimeProvider()
    {
        var now = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        using var timeProvider = new ManualRetryTimeProvider(now);
        var clock = new BinanceClockDriftEstimator(timeProvider);
        clock.Observe(now.ToUnixTimeMilliseconds(), now.AddMilliseconds(-20), now);
        var requests = 0;
        using var handler = new DelegatingCallbackHandler(request =>
        {
            var attempt = Interlocked.Increment(ref requests);
            return new HttpResponseMessage(attempt == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(attempt == 1 ? "{}" : "{\"makerCommission\":0,\"takerCommission\":0,\"buyerCommission\":0,\"sellerCommission\":0,\"canTrade\":true,\"canWithdraw\":false,\"canDeposit\":true,\"updateTime\":1,\"accountType\":\"SPOT\",\"balances\":[]}")
            };
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://testnet.binance.vision/api/") };
        var client = new BinanceSpotAccountClient(httpClient, new BinanceAuthenticatedClientOptions
        {
            RestBaseAddress = new Uri("https://testnet.binance.vision/api/"),
            ApiKey = "test-api-key",
            ApiSecret = "test-api-secret",
            ClockSyncCacheDuration = TimeSpan.FromSeconds(30)
        }, clock, timeProvider, new BinanceRequestRetryPolicy(new BinanceRequestResilienceOptions
        {
            MaxRetries = 1,
            InitialRetryDelay = TimeSpan.FromSeconds(10),
            MaxRetryDelay = TimeSpan.FromSeconds(10)
        }));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = client.GetAccountAsync(cancellation.Token);
        try
        {
            await timeProvider.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(1, Volatile.Read(ref requests));
            timeProvider.Fire();
            var account = await pending.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.True(account.CanTrade);
            Assert.Equal(2, requests);
        }
        finally
        {
            await cancellation.CancelAsync();
        }
    }

    [Fact]
    public async Task UnknownRateLimitStopsLaterSignedAndClockRequestsForTheSameClient()
    {
        var now = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);
        var clock = new BinanceClockDriftEstimator(timeProvider);
        clock.Observe(now.ToUnixTimeMilliseconds(), now.AddMilliseconds(-20), now);
        var requests = 0;
        using var handler = new DelegatingCallbackHandler(request =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request, Content = new StringContent("{}") };
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://testnet.binance.vision/api/") };
        var client = new BinanceSpotAccountClient(httpClient, new BinanceAuthenticatedClientOptions
        {
            RestBaseAddress = new Uri("https://testnet.binance.vision/api/"),
            ApiKey = "test-api-key",
            ApiSecret = "test-api-secret",
            ClockSyncCacheDuration = TimeSpan.FromSeconds(30)
        }, clock, timeProvider);

        await Assert.ThrowsAsync<BinanceApiException>(() => client.GetAccountAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAccountAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SynchronizeClockAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task UnknownRateLimitFromClockEndpointBlocksLaterClockRequests()
    {
        var requests = 0;
        using var handler = new DelegatingCallbackHandler(request =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request, Content = new StringContent("{}") };
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://testnet.binance.vision/api/") };
        var client = new BinanceSpotAccountClient(httpClient, new BinanceAuthenticatedClientOptions
        {
            RestBaseAddress = new Uri("https://testnet.binance.vision/api/"),
            ApiKey = "test-api-key",
            ApiSecret = "test-api-secret"
        });

        await Assert.ThrowsAsync<BinanceApiException>(() => client.SynchronizeClockAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SynchronizeClockAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, requests);
    }

    private sealed class DelegatingCallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(callback(request));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class ManualRetryTimeProvider(DateTimeOffset now) : TimeProvider, IDisposable
    {
        private ManualRetryTimer? timer;
        public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            timer = new ManualRetryTimer(callback, state);
            TimerCreated.TrySetResult();
            return timer;
        }
        public void Fire() => timer!.Fire();
        public void Dispose() => timer?.Dispose();
    }

    private sealed class ManualRetryTimer(TimerCallback callback, object? state) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Fire() => callback(state);
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
