using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CryptoTrading.Api.Tests;

public sealed class OperationalApiEndpointsTests
{
    [Fact]
    public async Task GetHealthLiveReturnsOk()
    {
        using var factory = CreateFactory(IPAddress.Loopback);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetHealthReadyReturnsOk()
    {
        using var factory = CreateFactory(IPAddress.Loopback);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetOperationalStatusFromLoopbackReturnsOkAndReadOnly()
    {
        using var factory = CreateFactory(IPAddress.Loopback);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/operational/status", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(json);
        Assert.Equal("api-process", json["source"]?.GetValue<string>());
        Assert.Equal("Normal", json["mode"]?.GetValue<string>());
        Assert.False(json["mutatingEndpointsEnabled"]?.GetValue<bool>());
    }

    [Fact]
    public async Task GetOperationalStatusFromNonLoopbackReturnsForbidden()
    {
        using var factory = CreateFactory(IPAddress.Parse("192.168.1.100"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/operational/status", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetOperationalDecisionWhenStoreUnavailableReturns503()
    {
        using var factory = CreateFactory(IPAddress.Loopback);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/operational/decisions/some-id", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task GetOperationalDecisionWhenNotFoundReturns404()
    {
        using var factory = CreateFactory(IPAddress.Loopback, services =>
        {
            services.AddSingleton<IAutonomyDecisionCommitStore>(new FakeDecisionStore([]));
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/operational/decisions/ad-missing", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetOperationalDecisionWhenFoundReturnsReportWithAuditMatch()
    {
        var close = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var decisionId = AutonomyDecisionReconstructor.ComputeCanonicalDecisionId("TESTNET", "exchange-wallet", "BTCUSDT", "trend", "1.0.0", close);
        var commit = new AutonomyDecisionCommit(
            decisionId,
            "TESTNET",
            "exchange-wallet",
            "BTCUSDT:1m:1.0.0",
            new Symbol("BTCUSDT"),
            "trend",
            "1.0.0",
            close,
            close,
            "1.0",
            "cfg-fp",
            "feat-fp",
            "NoTrade",
            "BELOW_THRESHOLD",
            null,
            "NO_TRADE",
            "BELOW_THRESHOLD",
            "{}",
            null);

        using var factory = CreateFactory(IPAddress.Loopback, services =>
        {
            services.AddSingleton<IAutonomyDecisionCommitStore>(new FakeDecisionStore([commit]));
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri($"/operational/decisions/{decisionId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<AutonomyDecisionReconstructionReport>();
        Assert.NotNull(report);
        Assert.Equal(decisionId, report.Commit.DecisionId);
        Assert.True(report.CanonicalIdentityHashMatches);
        Assert.True(report.IsAuditVerified);
    }

    [Fact]
    public async Task GetOperationalCheckpointWhenStoreUnavailableReturns503()
    {
        using var factory = CreateFactory(IPAddress.Loopback);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/operational/checkpoints/BTCUSDT", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task GetOperationalCheckpointWhenFoundReturnsCheckpointInfo()
    {
        var close = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        using var factory = CreateFactory(IPAddress.Loopback, services =>
        {
            services.AddSingleton<IAutonomyDecisionCommitStore>(new FakeDecisionStore([], close));
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/operational/checkpoints/BTCUSDT", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(json);
        Assert.Equal("BTCUSDT", json["scope"]?.GetValue<string>());
        Assert.Equal(close, json["lastProcessedCloseUtc"]?.GetValue<DateTimeOffset>());
    }

    [Fact]
    public async Task GetRiskPolicyWhenStoreUnavailableReturns503()
    {
        using var factory = CreateFactory(IPAddress.Loopback);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/operational/risk-policy", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task GetRiskPolicyWhenNoActivePolicyReturns404()
    {
        using var factory = CreateFactory(IPAddress.Loopback, services =>
        {
            services.AddSingleton<IRiskPolicyStore>(new FakeRiskPolicyStore(null));
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/operational/risk-policy", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetRiskPolicyWhenActivePolicyExistsReturnsPolicy()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = new RiskPolicy("v1.0", 1000m, 5000m, 500m, 1000m, TimeSpan.FromSeconds(30), withdrawalsEnabled: false);
        var doc = RiskPolicyDocument.CreateDraft(policy, now.AddHours(-1), now.AddHours(23), "creator")
            .Approve("approver", "app-1", now.AddMinutes(-30))
            .Activate(now.AddMinutes(-10));

        using var factory = CreateFactory(IPAddress.Loopback, services =>
        {
            services.AddSingleton<IRiskPolicyStore>(new FakeRiskPolicyStore(doc));
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/operational/risk-policy", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(json);
        Assert.Equal("v1.0", json["policy"]?["version"]?.GetValue<string>());
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The caller disposes the created WebApplicationFactory.")]
    private static WebApplicationFactory<Program> CreateFactory(
        IPAddress remoteIp,
        Action<IServiceCollection>? configureServices = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TRADING_ENVIRONMENT"] = "TESTNET",
                    ["BINANCE_REST_BASE_URL"] = "https://testnet.binance.vision/api",
                    ["BINANCE_WS_BASE_URL"] = "wss://testnet.binance.vision/ws",
                    ["BINANCE_API_KEY"] = "test-api-key",
                    ["BINANCE_API_SECRET"] = "test-api-secret",
                    ["BINANCE_WITHDRAWALS_ENABLED"] = "false",
                    ["BINANCE_ALLOWED_SYMBOLS"] = "BTCUSDT",
                    ["POSTGRES_CONNECTION_STRING"] = ""
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new TestRemoteIpStartupFilter(remoteIp));
                configureServices?.Invoke(services);
            });
        });
    }

    private sealed class TestRemoteIpStartupFilter(IPAddress remoteIp) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = remoteIp;
                    return nextMiddleware();
                });
                next(app);
            };
    }

    private sealed class FakeDecisionStore(
        IReadOnlyList<AutonomyDecisionCommit> commits,
        DateTimeOffset? lastProcessedClose = null) : IAutonomyDecisionCommitStore
    {
        public Task<DateTimeOffset?> GetLastProcessedCloseAsync(string scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(lastProcessedClose);

        public Task CommitAsync(AutonomyDecisionCommit commit, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<AutonomyDecisionCommit?> ReadDecisionCommitAsync(string decisionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(commits.FirstOrDefault(c => string.Equals(c.DecisionId, decisionId, StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class FakeRiskPolicyStore(RiskPolicyDocument? activePolicy) : IRiskPolicyStore
    {
        public Task SaveRiskPolicyAsync(RiskPolicyDocument document, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<RiskPolicyDocument> ApproveRiskPolicyAsync(
            string policyVersion,
            string approvalId,
            string approver,
            DateTimeOffset approvedAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<RiskPolicyDocument> ActivateRiskPolicyAsync(
            string policyVersion,
            DateTimeOffset activatedAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<RiskPolicyDocument?> GetActiveRiskPolicyAsync(
            DateTimeOffset atUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(activePolicy);
    }
}
