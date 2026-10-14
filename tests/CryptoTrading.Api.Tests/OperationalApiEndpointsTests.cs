using System.Net;
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

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetHealthReadyFailsClosedWhenPostgresIsNotConfigured()
    {
        using var factory = CreateFactory(IPAddress.Loopback);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task GetHealthReadyReportsDegradedWhenOperationalModeIsSafeMode()
    {
        var events = new[]
        {
            new OperationalControlAuditEvent(
                Guid.NewGuid(),
                OperationalMode.Normal,
                OperationalMode.SafeMode,
                "worker",
                "test-low-disk",
                new IdempotencyKey("api-health-safe"),
                DateTimeOffset.UtcNow,
                null)
        };
        var store = new FakeOperationalControlAuditStore(events);
        using var factory = CreateFactory(IPAddress.Loopback, services =>
        {
            services.AddSingleton<IOperationalControlCurrentStateStore>(store);
        }, postgresConnectionString: "Host=127.0.0.1;Port=5432;Database=cryptotrading;Username=cryptotrading;Password=secret");
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        // Degraded health checks in ASP.NET Core return 200 OK by default
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Degraded", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetHealthReadyReportsHealthyWhenOperationalModeIsNormal()
    {
        var events = new[]
        {
            new OperationalControlAuditEvent(
                Guid.NewGuid(),
                OperationalMode.SafeMode,
                OperationalMode.Normal,
                "worker",
                "test-resumed",
                new IdempotencyKey("api-health-normal"),
                DateTimeOffset.UtcNow,
                "approval-123")
        };
        var store = new FakeOperationalControlAuditStore(events);
        using var factory = CreateFactory(IPAddress.Loopback, services =>
        {
            services.AddSingleton<IOperationalControlCurrentStateStore>(store);
        }, postgresConnectionString: "Host=127.0.0.1;Port=5432;Database=cryptotrading;Username=cryptotrading;Password=secret");
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Healthy", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetOperationalStatusFromLoopbackReturnsOkAndReadOnly()
    {
        var events = new[]
        {
            new OperationalControlAuditEvent(
                Guid.Parse("8e1d4e65-4c98-4878-9e1a-cad81858a890"),
                OperationalMode.Normal,
                OperationalMode.SafeMode,
                "worker",
                "test-safe-mode",
                new IdempotencyKey("api-operational-status-safe"),
                new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero),
                null),
            new OperationalControlAuditEvent(
                Guid.Parse("a1c6d42e-d7d5-4498-b4ab-3178ccf6e702"),
                OperationalMode.SafeMode,
                OperationalMode.KillSwitch,
                "worker",
                "latest-kill-switch",
                new IdempotencyKey("api-operational-status-kill"),
                new DateTimeOffset(2026, 10, 7, 12, 1, 0, TimeSpan.Zero),
                null)
        };
        var store = new FakeOperationalControlAuditStore(events);
        using var factory = CreateFactory(IPAddress.Loopback, services =>
        {
            services.AddSingleton<IOperationalControlAuditStore>(store);
            services.AddSingleton<IOperationalControlCurrentStateStore>(store);
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/operational/status", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(json);
        Assert.Equal("worker-durable-audit", json["source"]?.GetValue<string>());
        Assert.Equal("KillSwitch", json["mode"]?.GetValue<string>());
        Assert.True(json["blocksNewEntries"]?.GetValue<bool>());
        Assert.Equal("latest-kill-switch", json["reason"]?.GetValue<string>());
        Assert.False(json["mutatingEndpointsEnabled"]?.GetValue<bool>());
    }

    [Fact]
    public async Task GetOperationalStatusFailsClosedWhenDurableWorkerStateIsUnavailable()
    {
        using var factory = CreateFactory(IPAddress.Loopback);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/operational/status", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("urn:cryptotrading:error:OPERATIONAL_STATE_UNAVAILABLE", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetOperationalStatusFailsClosedWhenNoDurableWorkerStateHasBeenRecorded()
    {
        var store = new FakeOperationalControlAuditStore([]);
        using var factory = CreateFactory(IPAddress.Loopback, services =>
            services.AddSingleton<IOperationalControlCurrentStateStore>(store));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/operational/status", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("urn:cryptotrading:error:OPERATIONAL_STATE_UNAVAILABLE", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetOperationalStatusFailsClosedWhenDurableWorkerStateCannotBeRead()
    {
        using var factory = CreateFactory(IPAddress.Loopback, services =>
            services.AddSingleton<IOperationalControlCurrentStateStore>(new ThrowingOperationalControlCurrentStateStore()));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/operational/status", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("urn:cryptotrading:error:OPERATIONAL_STATE_UNAVAILABLE", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetOperationalStatusFromNonLoopbackReturnsForbidden()
    {
        using var factory = CreateFactory(IPAddress.Parse("192.168.1.100"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/operational/status", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ForwardedHeadersAndInvalidInternalTokenCannotBypassLocalDiagnosticsPolicy()
    {
        const string invalidToken = "invalid-token-value";
        using var factory = CreateFactory(IPAddress.Parse("192.168.1.100"));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/operational/status");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "127.0.0.1");
        request.Headers.TryAddWithoutValidation("X-Internal-Access-Token", invalidToken);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(invalidToken, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetOperationalDecisionWhenStoreUnavailableReturns503()
    {
        using var factory = CreateFactory(IPAddress.Loopback);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/operational/decisions/some-id", UriKind.Relative), TestContext.Current.CancellationToken);

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

        var response = await client.GetAsync(new Uri("/operational/decisions/ad-missing", UriKind.Relative), TestContext.Current.CancellationToken);

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

        var response = await client.GetAsync(new Uri($"/operational/decisions/{decisionId}", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<AutonomyDecisionReconstructionReport>(cancellationToken: TestContext.Current.CancellationToken);
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

        var response = await client.GetAsync(new Uri("/operational/checkpoints/BTCUSDT", UriKind.Relative), TestContext.Current.CancellationToken);

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

        var response = await client.GetAsync(new Uri("/operational/checkpoints/BTCUSDT", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(json);
        Assert.Equal("BTCUSDT", json["scope"]?.GetValue<string>());
        Assert.Equal(close, json["lastProcessedCloseUtc"]?.GetValue<DateTimeOffset>());
    }

    [Fact]
    public async Task GetRiskPolicyWhenStoreUnavailableReturns503()
    {
        using var factory = CreateFactory(IPAddress.Loopback);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/operational/risk-policy", UriKind.Relative), TestContext.Current.CancellationToken);

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

        var response = await client.GetAsync(new Uri("/operational/risk-policy", UriKind.Relative), TestContext.Current.CancellationToken);

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

        var response = await client.GetAsync(new Uri("/operational/risk-policy", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(json);
        Assert.Equal("v1.0", json["policy"]?["version"]?.GetValue<string>());
    }

    [Fact]
    public async Task GetOperationalRegimesCurrentReturns503WhenStoreUnavailable()
    {
        using var factory = CreateFactory(IPAddress.Loopback);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/operational/regimes/current?symbol=BTCUSDT", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task GetOperationalRegimesHistoryReturns503WhenStoreUnavailable()
    {
        using var factory = CreateFactory(IPAddress.Loopback);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/operational/regimes/history?symbol=BTCUSDT", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task GetOperationalModelsReturns503WhenStoreUnavailable()
    {
        using var factory = CreateFactory(IPAddress.Loopback);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/operational/models?strategyId=trend-following", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task GetOperationalDriftReturns503WhenStoreUnavailable()
    {
        using var factory = CreateFactory(IPAddress.Loopback);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/operational/drift", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task GetOperationalAllocationsReturns503WhenStoreUnavailable()
    {
        using var factory = CreateFactory(IPAddress.Loopback);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/operational/allocations", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task GetPortfolioPerformanceReturnsCalculatedMetrics()
    {
        var fakeMetrics = new AggregatedPortfolioPerformanceMetrics(
            TotalTradesCount: 10,
            TotalWinningCount: 9,
            TotalLosingCount: 1,
            TotalRealizedPnlQuote: 45.50m,
            TotalFeesPaidQuote: 1.20m,
            TotalGrossProfitQuote: 50.00m,
            TotalGrossLossQuote: 4.50m,
            Periods: new[]
            {
                new PerformancePeriodMetrics("Hoje (Dia / 24 Horas)", 5, 5, 0, 25.00m, 0.60m) { NetAssetValueDeltaQuote = 25m, UnrealizedPnlDeltaQuote = 2m, SnapshotAvailability = PerformanceSnapshotAvailability.Available },
                new PerformancePeriodMetrics("Semana (Últimos 7 Dias)", 10, 9, 1, 45.50m, 1.20m) { NetAssetValueDeltaQuote = 45.5m, UnrealizedPnlDeltaQuote = 2m, SnapshotAvailability = PerformanceSnapshotAvailability.Available },
                new PerformancePeriodMetrics("Mês (Últimos 30 Dias)", 10, 9, 1, 45.50m, 1.20m) { NetAssetValueDeltaQuote = 45.5m, UnrealizedPnlDeltaQuote = 2m, SnapshotAvailability = PerformanceSnapshotAvailability.Available },
                new PerformancePeriodMetrics("Ano (Acumulado / YTD)", 10, 9, 1, 45.50m, 1.20m) { NetAssetValueDeltaQuote = 45.5m, UnrealizedPnlDeltaQuote = 2m, SnapshotAvailability = PerformanceSnapshotAvailability.Available }
            },
            BtcPositionBasis: new PositionCostBasisRecord("BTCUSDT", "BTC", "USDT", 0.5m, 40000m, DateTimeOffset.UtcNow))
        {
            CurrentNetAssetValueQuote = 500m,
            CurrentUnrealizedPnlQuote = 2m,
            CurrentSnapshotObservedAtUtc = DateTimeOffset.UtcNow
        };

        using var factory = CreateFactory(IPAddress.Loopback, services =>
        {
            services.AddSingleton<IPortfolioNavSnapshotStore>(new FakePortfolioSnapshotStore(fakeMetrics));
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/portfolio/performance", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(json);
        Assert.Equal(10, json["totalTradesCount"]?.GetValue<int>());
        Assert.Null(json["initialCapitalUsd"]);
        Assert.Null(json["totalProfitPct"]);
        Assert.Equal(90.00m, json["winRatePercentage"]?.GetValue<decimal>());
        Assert.Equal(45.50m, json["totalRealizedPnlUsd"]?.GetValue<decimal>());
        Assert.Equal(11.11m, json["profitFactor"]?.GetValue<decimal>());
        var periods = json["periods"]?.AsArray();
        Assert.NotNull(periods);
        Assert.Equal(4, periods.Count);
        Assert.Equal(5, periods[0]?["tradesCount"]?.GetValue<int>());
        Assert.Equal(100.00m, periods[0]?["winRatePercentage"]?.GetValue<decimal>());
    }

    [Fact]
    public async Task GetPortfolioPerformanceRejectsStaleCurrentSnapshot()
    {
        var staleMetrics = new AggregatedPortfolioPerformanceMetrics(
            0, 0, 0, 0m, 0m, 0m, 0m, Array.Empty<PerformancePeriodMetrics>(), null)
        {
            CurrentNetAssetValueQuote = 500m,
            CurrentUnrealizedPnlQuote = 0m,
            CurrentSnapshotObservedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-3)
        };
        using var factory = CreateFactory(IPAddress.Loopback, services =>
        {
            services.AddSingleton<IPortfolioNavSnapshotStore>(new FakePortfolioSnapshotStore(staleMetrics));
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/portfolio/performance", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task GetPortfolioPerformanceLeavesProfitFactorUnknownWithoutRealizedLosses()
    {
        var metrics = new AggregatedPortfolioPerformanceMetrics(
            1, 1, 0, 5m, 0m, 5m, 0m, Array.Empty<PerformancePeriodMetrics>(), null)
        {
            CurrentNetAssetValueQuote = 505m,
            CurrentUnrealizedPnlQuote = 0m,
            CurrentSnapshotObservedAtUtc = DateTimeOffset.UtcNow
        };
        using var factory = CreateFactory(IPAddress.Loopback, services =>
        {
            services.AddSingleton<IPortfolioNavSnapshotStore>(new FakePortfolioSnapshotStore(metrics));
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/portfolio/performance", UriKind.Relative), TestContext.Current.CancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(json);
        Assert.Null(json["profitFactor"]);
        Assert.Equal("UNKNOWN_WITHOUT_REALIZED_LOSSES", json["profitFactorAvailabilityReason"]?.GetValue<string>());
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The caller disposes the created WebApplicationFactory.")]
    private static WebApplicationFactory<Program> CreateFactory(
        IPAddress remoteIp,
        Action<IServiceCollection>? configureServices = null,
        string? postgresConnectionString = null)
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
                    ["POSTGRES_CONNECTION_STRING"] = postgresConnectionString ?? "",
                    ["POSTGRES_PASSWORD_FILE"] = null,
                    ["POSTGRES_PASSWORD"] = null,
                    ["POSTGRES_HOST"] = null,
                    ["POSTGRES_PORT"] = null,
                    ["POSTGRES_DB"] = null,
                    ["POSTGRES_USER"] = null
                });
            });

            builder.ConfigureTestServices(services =>
            {
                var storesToRemove = services.Where(d =>
                    d.ServiceType == typeof(PostgresTradingStore) ||
                    d.ServiceType == typeof(IAutonomyCheckpointStore) ||
                    d.ServiceType == typeof(IAutonomyDecisionCommitStore) ||
                    d.ServiceType == typeof(IRiskPolicyStore) ||
                    d.ServiceType == typeof(ILocalOpenOrderStore) ||
                    d.ServiceType == typeof(IExpectedExecutionFeeStore) ||
                    d.ServiceType == typeof(IExecutionSubmissionContextStore) ||
                    d.ServiceType == typeof(IOrderLifecycleStore) ||
                    d.ServiceType == typeof(IRuntimeStateStore) ||
                    d.ServiceType == typeof(IOperationalControlAuditStore) ||
                    d.ServiceType == typeof(IMarketRegimeStore) ||
                    d.ServiceType == typeof(IModelStrategyRegistryStore) ||
                    d.ServiceType == typeof(IDriftObservationStore) ||
                    d.ServiceType == typeof(IStrategyRegimePerformanceStore) ||
                    d.ServiceType == typeof(IPortfolioPerformanceStore) ||
                    d.ServiceType == typeof(IPortfolioNavSnapshotStore)).ToList();
                foreach (var d in storesToRemove)
                {
                    services.Remove(d);
                }

                services.PostConfigure<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckServiceOptions>(hcOptions =>
                {
                    var postgresCheck = hcOptions.Registrations.FirstOrDefault(r => r.Name == "postgres");
                    if (postgresCheck is not null)
                    {
                        hcOptions.Registrations.Remove(postgresCheck);
                    }
                });

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

    private sealed class FakePortfolioSnapshotStore(AggregatedPortfolioPerformanceMetrics metrics) : IPortfolioNavSnapshotStore
    {
        public Task RecordNavSnapshotAsync(PortfolioNavSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<AggregatedPortfolioPerformanceMetrics> GetPerformanceMetricsAsync(
            DateTimeOffset asOfUtc,
            CancellationToken cancellationToken = default) => Task.FromResult(metrics);
    }

    private sealed class FakeOperationalControlAuditStore(
        IReadOnlyList<OperationalControlAuditEvent> events) : IOperationalControlAuditStore, IOperationalControlCurrentStateStore
    {
        public Task<bool> AppendOperationalControlAuditEventAsync(
            OperationalControlAuditEvent auditEvent,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<IReadOnlyList<OperationalControlAuditEvent>> ReadOperationalControlAuditEventsAsync(
            DateTimeOffset? sinceUtc = null,
            int limit = 1_000,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OperationalControlAuditEvent>>(
                events.Where(e => sinceUtc is null || e.OccurredAtUtc >= sinceUtc)
                    .OrderByDescending(e => e.OccurredAtUtc)
                    .Take(limit)
                    .ToArray());

        public Task<OperationalControlAuditEvent?> ReadLatestOperationalControlAuditEventAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(events.OrderByDescending(e => e.OccurredAtUtc).FirstOrDefault());
    }

    private sealed class ThrowingOperationalControlCurrentStateStore : IOperationalControlCurrentStateStore
    {
        public Task<OperationalControlAuditEvent?> ReadLatestOperationalControlAuditEventAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Synthetic persistence read failure.");
    }
}
