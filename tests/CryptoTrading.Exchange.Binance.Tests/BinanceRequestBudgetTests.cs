using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using CryptoTrading.Exchange.Binance;
using Xunit;

namespace CryptoTrading.Exchange.Binance.Tests;

public sealed class BinanceRequestBudgetTests
{
    private static readonly string[] BootstrapAndOrderPaths = ["/api/v3/exchangeInfo", "/api/v3/order"];
    [Fact]
    public async Task TwoInstancesSharePreventiveAdmissionBeforeSendingHttp()
    {
        using var fixture = new Fixture(weightLimit: 6);
        var second = new BinanceRequestBudget(fixture.Directory, fixture.Clock, fixture.Limits);
        for (var i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
            using var response = await (i % 2 == 0 ? fixture.Budget : second).SendAsync(fixture.Http, request, TestContext.Current.CancellationToken);
        }
        using var rejected = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        await Assert.ThrowsAsync<BinanceRequestBudgetRejectedException>(() => second.SendAsync(fixture.Http, rejected, TestContext.Current.CancellationToken));
        Assert.Equal(3, fixture.Handler.Count);
        fixture.Clock.Advance(TimeSpan.FromSeconds(62));
        using var next = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        using var accepted = await second.SendAsync(fixture.Http, next, TestContext.Current.CancellationToken);
        Assert.Equal(4, fixture.Handler.Count);
    }

    [Fact]
    public async Task PublicSdkUsesInjectedBudgetBeforeItsHttpBoundary()
    {
        using var fixture = new Fixture(weightLimit: 1);
        var client = new BinanceSpotMarketClient(fixture.Http, new BinanceClientOptions(), requestBudget: fixture.Budget);
        await client.PingAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<BinanceRequestBudgetRejectedException>(() => client.PingAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Handler.Count);
        Assert.Equal("/api/v3/ping", Assert.Single(fixture.Handler.Paths));
    }

    [Fact]
    public async Task SignedReadAndClockSynchronizationUseTheSameInjectedBudget()
    {
        using var fixture = new Fixture(weightLimit: 81);
        fixture.Handler.Response = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(fixture.Handler.Paths[^1].EndsWith("/time", StringComparison.Ordinal)
                ? "{\"serverTime\":100000}" : "[]", Encoding.UTF8, "application/json")
        };
        var client = new BinanceSpotAccountClient(fixture.Http, new BinanceAuthenticatedClientOptions
        {
            Environment = BinanceEnvironment.Testnet, ApiKey = "synthetic-unit-key", ApiSecret = "synthetic-unit-secret"
        }, timeProvider: fixture.Clock, requestBudget: fixture.Budget);
        Assert.Empty(await client.GetOpenOrdersAsync("BTCUSDT", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<BinanceRequestBudgetRejectedException>(() => client.GetOpenOrdersAsync("BTCUSDT", TestContext.Current.CancellationToken));
        Assert.Equal(2, fixture.Handler.Count);
    }

    [Fact]
    public async Task TradingStateReadsDoNotCompeteWhileTheAccountResponseIsPending()
    {
        using var fixture = new Fixture(weightLimit: 1000);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.AsyncResponse = async token =>
        {
            var path = fixture.Handler.Paths[^1];
            if (path.EndsWith("/account", StringComparison.Ordinal))
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token).ConfigureAwait(false);
            }
            var content = path.EndsWith("/time", StringComparison.Ordinal) ? "{\"serverTime\":100000}"
                : path.EndsWith("/account", StringComparison.Ordinal)
                    ? """{"makerCommission":15,"takerCommission":15,"buyerCommission":0,"sellerCommission":0,"canTrade":true,"canWithdraw":false,"canDeposit":true,"updateTime":1,"accountType":"SPOT","balances":[{"asset":"BTC","free":"1","locked":"0"}]}"""
                    : "[]";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
        };
        var client = new BinanceSpotAccountClient(fixture.Http, new BinanceAuthenticatedClientOptions
        { Environment = BinanceEnvironment.Testnet, ApiKey = "synthetic-unit-key", ApiSecret = "synthetic-unit-secret", ClockSyncCacheDuration = TimeSpan.FromMinutes(1) },
            timeProvider: fixture.Clock, requestBudget: fixture.Budget);
        await client.SynchronizeClockAsync(TestContext.Current.CancellationToken);
        var reading = client.GetTradingStateAsync("BTCUSDT", "BTC", TestContext.Current.CancellationToken);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); }
        finally { release.TrySetResult(); }
        var state = await reading.ConfigureAwait(true);
        Assert.True(state.IsConsistent);
        Assert.Equal(3, fixture.Handler.Count);
    }

    [Fact]
    public async Task ServerCountersAndUnknownRetryDeadlineBlockOtherInstances()
    {
        using var fixture = new Fixture(weightLimit: 10);
        fixture.Handler.Response = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Add("X-MBX-USED-WEIGHT-1M", "9");
            return response;
        };
        using var first = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        using var received = await fixture.Budget.SendAsync(fixture.Http, first, TestContext.Current.CancellationToken);
        using var next = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        var second = new BinanceRequestBudget(fixture.Directory, fixture.Clock, fixture.Limits);
        await Assert.ThrowsAsync<BinanceRequestBudgetRejectedException>(() => second.SendAsync(fixture.Http, next, TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Handler.Count);
    }

    [Fact]
    public async Task RateLimitedResponseWithoutRetryAfterIsStickyAcrossInstances()
    {
        using var fixture = new Fixture(weightLimit: 100);
        fixture.Handler.Response = () => new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        using var request = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        using var limited = await fixture.Budget.SendAsync(fixture.Http, request, TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        using var next = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        var second = new BinanceRequestBudget(fixture.Directory, fixture.Clock, fixture.Limits);
        await Assert.ThrowsAsync<BinanceRequestBudgetRejectedException>(() => second.SendAsync(fixture.Http, next, TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Handler.Count);
    }

    [Fact]
    public async Task RecentLowerServerCounterRenewsTheConservativePeakLifetime()
    {
        using var fixture = new Fixture(weightLimit: 100);
        fixture.Handler.Response = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Add("X-MBX-USED-WEIGHT-1M", fixture.Handler.Count == 1 ? "90" : "80");
            return response;
        };
        using var first = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        using var initial = await fixture.Budget.SendAsync(fixture.Http, first, TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromSeconds(55));
        using var second = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        using var recent = await fixture.Budget.SendAsync(fixture.Http, second, TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromSeconds(7));
        using var rejected = new HttpRequestMessage(HttpMethod.Get, "v3/exchangeInfo");
        await Assert.ThrowsAsync<BinanceRequestBudgetRejectedException>(() => fixture.Budget.SendAsync(fixture.Http, rejected, TestContext.Current.CancellationToken));
        Assert.Equal(2, fixture.Handler.Count);
    }

    [Fact]
    public async Task CallerCancellationAfterHeadersCannotEraseAnUnknownRateLimitDeadline()
    {
        using var fixture = new Fixture(weightLimit: 100);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        fixture.Handler.Response = () =>
        {
            cancellation.Cancel();
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        };
        using var first = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        try
        {
            using var response = await fixture.Budget.SendAsync(fixture.Http, first, cancellation.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or HttpRequestException) { }
        using var second = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        var peer = new BinanceRequestBudget(fixture.Directory, fixture.Clock, fixture.Limits);
        await Assert.ThrowsAsync<BinanceRequestBudgetRejectedException>(() => peer.SendAsync(fixture.Http, second, TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Handler.Count);
    }

    [Fact]
    public async Task UnknownEndpointAndCorruptedJournalNeverFallBackToAllow()
    {
        using var fixture = new Fixture(weightLimit: 100);
        using var unknown = new HttpRequestMessage(HttpMethod.Get, "v3/unsupported");
        await Assert.ThrowsAsync<BinanceRequestBudgetRejectedException>(() => fixture.Budget.SendAsync(fixture.Http, unknown, TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Handler.Count);
        using var first = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        using var accepted = await fixture.Budget.SendAsync(fixture.Http, first, TestContext.Current.CancellationToken);
        var journal = Assert.Single(System.IO.Directory.EnumerateFiles(fixture.Directory, "*.json"));
        await File.WriteAllTextAsync(journal, "{}", TestContext.Current.CancellationToken);
        using var next = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        await Assert.ThrowsAsync<BinanceRequestBudgetRejectedException>(() => fixture.Budget.SendAsync(fixture.Http, next, TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Handler.Count);
    }

    [Theory]
    [InlineData("peak-negative")]
    [InlineData("peak-future")]
    [InlineData("bootstrap-future")]
    [InlineData("bucket-negative")]
    [InlineData("bucket-unaligned")]
    [InlineData("header")]
    public async Task SyntacticallyValidButInconsistentJournalIsRejectedBeforeHttp(string mutation)
    {
        using var fixture = new Fixture(weightLimit: 100);
        using var first = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        using var accepted = await fixture.Budget.SendAsync(fixture.Http, first, TestContext.Current.CancellationToken);
        var path = Assert.Single(System.IO.Directory.EnumerateFiles(fixture.Directory, "*.json"));
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!;
        var window = json["Windows"]![0]!;
        switch (mutation)
        {
            case "peak-negative": window["ServerPeakAt"] = -1; break;
            case "peak-future": window["ServerPeakAt"] = 1_000_000; break;
            case "bootstrap-future": json["BootstrapAt"] = 1_000_000; break;
            case "bucket-negative": window["Buckets"]![0]!["Stamp"] = -1; break;
            case "bucket-unaligned": window["Buckets"]![0]!["Stamp"] = 1; break;
            case "header": window["Header"] = "ignored-counter"; break;
        }
        await File.WriteAllTextAsync(path, json.ToJsonString(), TestContext.Current.CancellationToken);
        using var next = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        await Assert.ThrowsAsync<BinanceRequestBudgetRejectedException>(() => fixture.Budget.SendAsync(fixture.Http, next, TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Handler.Count);
    }

    [Fact]
    public async Task UnknownMetadataBootstrapsOnlyPublicExchangeInfoAndDoesNotRetryPost()
    {
        using var fixture = new Fixture(weightLimit: 100);
        var budget = new BinanceRequestBudget(Path.Combine(fixture.Directory, "discovery"), fixture.Clock);
        fixture.Handler.Response = () => fixture.Handler.Count == 1 ? new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"rateLimits":[{"rateLimitType":"REQUEST_WEIGHT","interval":"MINUTE","intervalNum":1,"limit":100},{"rateLimitType":"RAW_REQUESTS","interval":"MINUTE","intervalNum":1,"limit":100},{"rateLimitType":"ORDERS","interval":"MINUTE","intervalNum":1,"limit":100}]}""", Encoding.UTF8, "application/json")
        } : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        using var request = new HttpRequestMessage(HttpMethod.Post, "v3/order");
        using var response = await budget.SendAsync(fixture.Http, request, TestContext.Current.CancellationToken);
        Assert.Equal(BootstrapAndOrderPaths, fixture.Handler.Paths);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(2, fixture.Handler.Count);
    }

    [Fact]
    public async Task FailedDiscoveryRemainsChargedWhenMetadataIsEventuallyObtained()
    {
        using var fixture = new Fixture(weightLimit: 100);
        var budget = new BinanceRequestBudget(fixture.Directory, fixture.Clock);
        fixture.Handler.Response = () => fixture.Handler.Count == 1
            ? throw new HttpRequestException("synthetic ambiguous discovery") : MetadataResponse(25);
        using var first = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        await Assert.ThrowsAsync<HttpRequestException>(() => budget.SendAsync(fixture.Http, first, TestContext.Current.CancellationToken));
        // Explicitly simulate operator reconciliation for this accounting test;
        // unknown network outcomes must not be retried automatically.
        var journal = Assert.Single(System.IO.Directory.EnumerateFiles(fixture.Directory, "*.json"));
        var reconciled = JsonNode.Parse(await File.ReadAllTextAsync(journal, TestContext.Current.CancellationToken))!;
        reconciled["PendingObservation"] = false;
        await File.WriteAllTextAsync(journal, reconciled.ToJsonString(), TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        using var second = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        await Assert.ThrowsAsync<BinanceRequestBudgetRejectedException>(() => budget.SendAsync(fixture.Http, second, TestContext.Current.CancellationToken));
        Assert.Equal(2, fixture.Handler.Count);
        Assert.All(fixture.Handler.Paths, path => Assert.Equal("/api/v3/exchangeInfo", path));
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025:Do not pass IDisposable instances into unawaited tasks", Justification = "The in-flight request is always released and awaited in finally before its request, client and handler leave scope.")]
    public async Task LateDiscoveryCannotReplaceReservationsMadeByAnotherInstance()
    {
        using var fixture = new Fixture(weightLimit: 24);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var slowHandler = new Handler
        {
            Response = () => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new DeferredMetadataContent(entered, release.Task) }
        };
        using var slowHttp = new HttpClient(slowHandler) { BaseAddress = fixture.Http.BaseAddress };
        var discovering = new BinanceRequestBudget(fixture.Directory, fixture.Clock);
        using var first = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        var pending = discovering.SendAsync(slowHttp, first, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            using var other = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
            using var reserved = await fixture.Budget.SendAsync(fixture.Http, other, TestContext.Current.CancellationToken);
        }
        finally
        {
            release.TrySetResult();
            using var completed = await pending.ConfigureAwait(true);
        }
        using var rejected = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        await Assert.ThrowsAsync<BinanceRequestBudgetRejectedException>(() => fixture.Budget.SendAsync(fixture.Http, rejected, TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Handler.Count);
    }

    private static HttpResponseMessage MetadataResponse(int limit) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($$"""{"rateLimits":[{"rateLimitType":"REQUEST_WEIGHT","interval":"MINUTE","intervalNum":1,"limit":{{limit}}},{"rateLimitType":"RAW_REQUESTS","interval":"MINUTE","intervalNum":1,"limit":1000},{"rateLimitType":"ORDERS","interval":"MINUTE","intervalNum":1,"limit":1000}]}""", Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task UnknownHttpOutcomeBlocksPeersInsteadOfTrustingAnOldHealthyJournal()
    {
        using var fixture = new Fixture(weightLimit: 100);
        fixture.Handler.Response = () => fixture.Handler.Count == 1 ? throw new HttpRequestException("synthetic lost response") : new(HttpStatusCode.OK);
        using var request = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Budget.SendAsync(fixture.Http, request, TestContext.Current.CancellationToken));
        using var peerRequest = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        var peer = new BinanceRequestBudget(fixture.Directory, fixture.Clock, fixture.Limits);
        await Assert.ThrowsAsync<BinanceRequestBudgetRejectedException>(() => peer.SendAsync(fixture.Http, peerRequest, TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Handler.Count);
    }

    private sealed class DeferredMetadataContent(TaskCompletionSource entered, Task released) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            entered.TrySetResult();
            await released.ConfigureAwait(false);
            using var response = MetadataResponse(24);
            await response.Content.CopyToAsync(stream).ConfigureAwait(false);
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    [Fact]
    public async Task AncestorSymlinkCannotRedirectTheQuotaJournal()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture(weightLimit: 100);
        System.IO.Directory.CreateDirectory(fixture.Directory);
        var actual = Path.Combine(fixture.Directory, "actual");
        System.IO.Directory.CreateDirectory(actual);
        var alias = Path.Combine(fixture.Directory, "alias");
        System.IO.Directory.CreateSymbolicLink(alias, actual);
        var redirected = new BinanceRequestBudget(Path.Combine(alias, "quota"), fixture.Clock, fixture.Limits);
        using var request = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        await Assert.ThrowsAsync<BinanceRequestBudgetRejectedException>(() => redirected.SendAsync(fixture.Http, request, TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Handler.Count);
    }

    [Fact]
    public async Task PreexistingJournalWithPermissiveModeCannotAuthorizeHttp()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture(weightLimit: 100);
        using var first = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        using var completed = await fixture.Budget.SendAsync(fixture.Http, first, TestContext.Current.CancellationToken);
        var journal = Assert.Single(System.IO.Directory.EnumerateFiles(fixture.Directory, "*.json"));
        File.SetUnixFileMode(journal, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        using var second = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        await Assert.ThrowsAsync<BinanceRequestBudgetRejectedException>(() => fixture.Budget.SendAsync(fixture.Http, second, TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Handler.Count);
    }

    [Fact]
    public async Task FailedPostResponseObservationPermanentlyPoisonsAdmissionEvenAfterJournalRepair()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture(weightLimit: 100);
        string? journal = null;
        fixture.Handler.Response = () =>
        {
            journal = Assert.Single(System.IO.Directory.EnumerateFiles(fixture.Directory, "*.json"));
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(journal, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Budget.SendAsync(fixture.Http, request, TestContext.Current.CancellationToken));
        Assert.NotNull(journal);
        File.SetUnixFileMode(journal, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var next = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        var peer = new BinanceRequestBudget(fixture.Directory, fixture.Clock, fixture.Limits);
        await Assert.ThrowsAsync<BinanceRequestBudgetRejectedException>(() => peer.SendAsync(fixture.Http, next, TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Handler.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentProcessesCannotCollectivelyExceedTheSameJournalCapacity(bool separateTimeNamespace)
    {
        using var fixture = new Fixture(weightLimit: 6);
        using var first = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        using var initial = await fixture.Budget.SendAsync(fixture.Http, first, TestContext.Current.CancellationToken);
        var resultPath = Path.Combine(fixture.Directory, "peer-count.txt");
        var start = new ProcessStartInfo(separateTimeNamespace ? "unshare" : "dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (separateTimeNamespace)
        {
            foreach (var argument in new[] { "--user", "--map-root-user", "--time", "--fork", "--monotonic", "120", "dotnet" }) start.ArgumentList.Add(argument);
        }
        start.ArgumentList.Add(typeof(BinanceRequestBudgetTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add("CryptoTrading.Exchange.Binance.Tests.BinanceRequestBudgetTests.BudgetSubprocessPeer");
        start.Environment["CT_QUOTA_TEST_SHARED_DIR"] = fixture.Directory;
        start.Environment["CT_QUOTA_TEST_RESULT"] = resultPath;
        start.Environment["CT_QUOTA_TEST_DIFFERENT_TIME_NS"] = separateTimeNamespace ? "1" : "0";
        start.Environment["CT_QUOTA_TEST_PARENT_TIME_NS"] = BinanceQuotaJournal.TimeNamespaceIdentifier;
        start.Environment["CT_QUOTA_TEST_PARENT_BOOT"] = BinanceQuotaJournal.BootIdentifier;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var parentCount = 1;
        for (var i = 0; i < 10; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
            try
            {
                using var accepted = await fixture.Budget.SendAsync(fixture.Http, request, TestContext.Current.CancellationToken);
                parentCount++;
            }
            catch (BinanceRequestBudgetRejectedException) { }
        }
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, (await stdout.ConfigureAwait(true)) + (await stderr.ConfigureAwait(true)));
        var peerCount = int.Parse(await File.ReadAllTextAsync(resultPath, TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(3, parentCount + peerCount);
    }

    [Fact]
    public async Task BudgetSubprocessPeer()
    {
        var directory = Environment.GetEnvironmentVariable("CT_QUOTA_TEST_SHARED_DIR");
        var resultPath = Environment.GetEnvironmentVariable("CT_QUOTA_TEST_RESULT");
        if (directory is null || resultPath is null) return;
        var differentNamespace = Environment.GetEnvironmentVariable("CT_QUOTA_TEST_DIFFERENT_TIME_NS") == "1";
        if (differentNamespace)
        {
            Assert.NotEqual(Environment.GetEnvironmentVariable("CT_QUOTA_TEST_PARENT_TIME_NS"), BinanceQuotaJournal.TimeNamespaceIdentifier);
            Assert.Equal(Environment.GetEnvironmentVariable("CT_QUOTA_TEST_PARENT_BOOT"), BinanceQuotaJournal.BootIdentifier);
        }
        using var handler = new Handler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.binance.com/api/") };
        BinanceRateLimitDefinition[] limits = [new("REQUEST_WEIGHT", "MINUTE", 1, 6), new("RAW_REQUESTS", "MINUTE", 1, 1000), new("ORDERS", "MINUTE", 1, 1000)];
        var budget = new BinanceRequestBudget(directory, new Clock(), limits);
        var count = 0;
        for (var i = 0; i < 10; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
            try
            {
                using var accepted = await budget.SendAsync(client, request, TestContext.Current.CancellationToken);
                count++;
            }
            catch (BinanceRequestBudgetRejectedException) { }
        }
        if (differentNamespace) Assert.Equal(0, count);
        await File.WriteAllTextAsync(resultPath, count.ToString(System.Globalization.CultureInfo.InvariantCulture), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EstablishedMarkerNeverReinitializesAMissingJournalAfterInterruption(bool hadCommittedState)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture(weightLimit: 100);
        if (hadCommittedState)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
            using var result = await fixture.Budget.SendAsync(fixture.Http, request, TestContext.Current.CancellationToken);
            File.Delete(Assert.Single(System.IO.Directory.EnumerateFiles(fixture.Directory, "*.json")));
        }
        else
        {
            var name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("spot-live")));
            var opened = await BinanceQuotaJournal.OpenAsync(fixture.Directory, name, fixture.Clock, TestContext.Current.CancellationToken);
            await using var lifetime = opened.ConfigureAwait(true);
        }
        var peer = new BinanceRequestBudget(fixture.Directory, fixture.Clock, fixture.Limits);
        using var rejected = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        await Assert.ThrowsAsync<BinanceRequestBudgetRejectedException>(() => peer.SendAsync(fixture.Http, rejected, TestContext.Current.CancellationToken));
        Assert.Equal(hadCommittedState ? 1 : 0, fixture.Handler.Count);
    }

    [Fact]
    public async Task NewBootCannotReuseAnOldJournalEvenWhenItsUptimeIsGreater()
    {
        using var fixture = new Fixture(weightLimit: 100);
        using var first = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        using var accepted = await fixture.Budget.SendAsync(fixture.Http, first, TestContext.Current.CancellationToken);
        var path = Assert.Single(System.IO.Directory.EnumerateFiles(fixture.Directory, "*.json"));
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!;
        json["BootIdentifier"] = "00000000-0000-0000-0000-000000000001";
        await File.WriteAllTextAsync(path, json.ToJsonString(), TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        using var next = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        await Assert.ThrowsAsync<BinanceRequestBudgetRejectedException>(() => fixture.Budget.SendAsync(fixture.Http, next, TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Handler.Count);
    }

    [Fact]
    public async Task DifferentTimeNamespaceCannotReuseTheSameBootAndFrequency()
    {
        using var fixture = new Fixture(weightLimit: 100);
        using var first = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        using var accepted = await fixture.Budget.SendAsync(fixture.Http, first, TestContext.Current.CancellationToken);
        var path = Assert.Single(System.IO.Directory.EnumerateFiles(fixture.Directory, "*.json"));
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!;
        json["TimeNamespaceIdentifier"] = "time:[1]";
        await File.WriteAllTextAsync(path, json.ToJsonString(), TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        using var next = new HttpRequestMessage(HttpMethod.Get, "v3/klines");
        await Assert.ThrowsAsync<BinanceRequestBudgetRejectedException>(() => fixture.Budget.SendAsync(fixture.Http, next, TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Handler.Count);
    }

    [Fact]
    public void TimestampFrequencyCannotOverflowBootstrapOrWindowArithmetic()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BinanceRequestBudget("/tmp/unused-quota", new ExtremeFrequencyClock()));
    }

    private sealed class ExtremeFrequencyClock : TimeProvider
    {
        public override long TimestampFrequency => long.MaxValue / 30 + 1;
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "ct-quota-" + Guid.NewGuid().ToString("N"));
        public Clock Clock { get; } = new();
        public Handler Handler { get; } = new();
        public HttpClient Http { get; }
        public BinanceRequestBudget Budget { get; }
        public BinanceRateLimitDefinition[] Limits { get; }
        public Fixture(long weightLimit)
        {
            Limits = [new("REQUEST_WEIGHT", "MINUTE", 1, weightLimit), new("RAW_REQUESTS", "MINUTE", 1, 1000), new("ORDERS", "MINUTE", 1, 1000)];
            Http = new HttpClient(Handler) { BaseAddress = new Uri("https://api.binance.com/api/") };
            Budget = new BinanceRequestBudget(Directory, Clock, Limits);
        }
        public void Dispose()
        {
            Http.Dispose();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Count { get; private set; }
        public List<string> Paths { get; } = [];
        public Func<HttpResponseMessage> Response { get; set; } = () => new(HttpStatusCode.OK);
        public Func<CancellationToken, Task<HttpResponseMessage>>? AsyncResponse { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            Paths.Add(request.RequestUri!.AbsolutePath);
            return AsyncResponse is null ? Response() : await AsyncResponse(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class Clock : TimeProvider, IBinanceBudgetClockIdentity
    {
        public string MonotonicOrigin => "quota-test-clock-v1";
        private long stamp = 100_000;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => stamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(stamp);
        public void Advance(TimeSpan amount) => stamp += (long)amount.TotalMilliseconds;
    }
}
