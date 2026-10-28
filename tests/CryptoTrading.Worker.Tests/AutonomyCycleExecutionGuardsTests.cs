using CryptoTrading.Backtesting.Autonomy;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Domain.Runtime;
using CryptoTrading.Exchange.Binance;
using CryptoTrading.Observability;
using CryptoTrading.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class AutonomyCycleExecutionGuardsTests
{
    [Fact]
    public async Task RunOnceBlocksWhenUnknownOpenOrderExists()
    {
        var marketClient = new FakeMarketClient();
        var accountClient = new FakeAccountClient();
        var policyStore = new FakePolicyStore();
        var riskContextStore = new FakeRiskContextStore();
        var decisionCommitStore = new FakeDecisionCommitStore();
        var operationalControl = new InMemoryOperationalControl(new AllowAllResetAuthorizer());
        using var supervisor = new RuntimeSupervisor(new InMemoryRuntimeTransitionSink(), TimeProvider.System, 0, RuntimeState.Ready);
        var cycle = new BaselineDecisionCycle(new TrendFollowingParameters("test", "1.0.0", new Symbol("BTCUSDT"), 2, 3, 0m, 0m));
        var openOrderStore = new FakeOpenOrderStore([
            new LocalOpenOrderRecord("BTCUSDT", 12345, "ord-unknown-1", "UNKNOWN", DateTimeOffset.UtcNow.AddMinutes(-5))
        ]);

        var options = Options.Create(new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            AutonomySymbol = "BTCUSDT",
            AutonomyInterval = "1m",
            AutonomyKlineLimit = 50,
            PollIntervalMilliseconds = 100,
            ExecutionMode = "PAPER",
            NoTrade = true,
            ReconciliationAccount = "test-account",
            PersistenceEnabled = true,
            PostgresConnectionString = "Host=localhost;Database=test;Username=test;Password=test",
            ApplyMigrations = true,
            RiskPolicyRequired = true,
            ReconciliationEnabled = true,
            AccountStreamEnabled = true
        });

        using var hostedService = new AutonomyCycleHostedService(
            options,
            marketClient,
            accountClient,
            policyStore,
            riskContextStore,
            decisionCommitStore,
            operationalControl,
            supervisor,
            cycle,
            NullLogger<AutonomyCycleHostedService>.Instance,
            openOrderStore);

        await hostedService.RunOnceAsync(options.Value, TestContext.Current.CancellationToken);

        Assert.NotEmpty(decisionCommitStore.Commits);
        var latestCommit = decisionCommitStore.Commits[^1];
        Assert.Equal("BLOCKED", latestCommit.Outcome);
        Assert.Contains("UNKNOWN_ORDER_EXISTS:ord-unknown-1", latestCommit.NoTradeReason, StringComparison.Ordinal);
        Assert.Null(latestCommit.OutboxRecord);
    }

    [Fact]
    public async Task RunOnceDoesNotBlockWhenOrdersAreKnown()
    {
        var marketClient = new FakeMarketClient();
        var accountClient = new FakeAccountClient();
        var policyStore = new FakePolicyStore();
        var riskContextStore = new FakeRiskContextStore();
        var decisionCommitStore = new FakeDecisionCommitStore();
        var operationalControl = new InMemoryOperationalControl(new AllowAllResetAuthorizer());
        using var supervisor = new RuntimeSupervisor(new InMemoryRuntimeTransitionSink(), TimeProvider.System, 0, RuntimeState.Ready);
        var cycle = new BaselineDecisionCycle(new TrendFollowingParameters("test", "1.0.0", new Symbol("BTCUSDT"), 2, 3, 0m, 0m));
        var openOrderStore = new FakeOpenOrderStore([
            new LocalOpenOrderRecord("BTCUSDT", 12345, "ord-known-1", "NEW", DateTimeOffset.UtcNow.AddMinutes(-5))
        ]);

        var options = Options.Create(new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            AutonomySymbol = "BTCUSDT",
            AutonomyInterval = "1m",
            AutonomyKlineLimit = 50,
            PollIntervalMilliseconds = 100,
            ExecutionMode = "PAPER",
            NoTrade = true,
            ReconciliationAccount = "test-account",
            PersistenceEnabled = true,
            PostgresConnectionString = "Host=localhost;Database=test;Username=test;Password=test",
            ApplyMigrations = true,
            RiskPolicyRequired = true,
            ReconciliationEnabled = true,
            AccountStreamEnabled = true
        });

        using var hostedService = new AutonomyCycleHostedService(
            options,
            marketClient,
            accountClient,
            policyStore,
            riskContextStore,
            decisionCommitStore,
            operationalControl,
            supervisor,
            cycle,
            NullLogger<AutonomyCycleHostedService>.Instance,
            openOrderStore);

        await hostedService.RunOnceAsync(options.Value, TestContext.Current.CancellationToken);

        Assert.NotEmpty(decisionCommitStore.Commits);
        var latestCommit = decisionCommitStore.Commits[^1];
        Assert.NotEqual("UNKNOWN_ORDER_BLOCKED", latestCommit.StrategyReason);
    }

    [Fact]
    public async Task RunOnceFailsClosedWhenTradingRulesCannotBeFetched()
    {
        var decisionCommitStore = new FakeDecisionCommitStore();
        using var fixture = CreateHostedService(
            decisionCommitStore,
            new ThrowingRulesProvider());

        await fixture.HostedService.RunOnceAsync(CreateOptions().Value, TestContext.Current.CancellationToken);

        var latestCommit = Assert.Single(decisionCommitStore.Commits);
        Assert.Equal("NO_TRADE", latestCommit.Outcome);
        Assert.Equal("BINANCE_TRADING_RULES_UNAVAILABLE_OR_INCOMPLETE", latestCommit.NoTradeReason);
        Assert.Null(latestCommit.OutboxRecord);
    }

    [Fact]
    public async Task RunOnceSkipsCandidateGenerationWhenSymbolOutsideBudgetPolicy()
    {
        var marketClient = new FakeMarketClient();
        var accountClient = new FakeAccountClient();
        var budgetPolicy = new BudgetPolicy(allowedSymbol: "ETHUSDT");
        var policy = new RiskPolicy("v1", 1000m, 5000m, 500m, 1000m, TimeSpan.FromMinutes(15), withdrawalsEnabled: false, budgetPolicy: budgetPolicy);
        var draft = RiskPolicyDocument.CreateDraft(policy, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), "creator");
        draft = draft.Approve("app-1", "approver", DateTimeOffset.UtcNow.AddDays(-1));
        var doc = draft.Activate(DateTimeOffset.UtcNow.AddDays(-1));
        var policyStore = new FakePolicyStore(doc);
        var riskContextStore = new FakeRiskContextStore();
        var decisionCommitStore = new FakeDecisionCommitStore();
        var operationalControl = new InMemoryOperationalControl(new AllowAllResetAuthorizer());
        using var supervisor = new RuntimeSupervisor(new InMemoryRuntimeTransitionSink(), TimeProvider.System, 0, RuntimeState.Ready);
        var cycle = new BaselineDecisionCycle(new TrendFollowingParameters("test", "1.0.0", new Symbol("BTCUSDT"), 2, 3, 0m, 0m));

        var options = CreateOptions();

        using var hostedService = new AutonomyCycleHostedService(
            options,
            marketClient,
            accountClient,
            policyStore,
            riskContextStore,
            decisionCommitStore,
            operationalControl,
            supervisor,
            cycle,
            NullLogger<AutonomyCycleHostedService>.Instance);

        await hostedService.RunOnceAsync(options.Value, TestContext.Current.CancellationToken);

        // Commits a NO_TRADE record to advance checkpoint without creating an outbox dead-letter
        var latestCommit = Assert.Single(decisionCommitStore.Commits);
        Assert.Equal("NO_TRADE", latestCommit.Outcome);
        Assert.Equal("SYMBOL_NOT_AUTHORIZED_IN_BUDGET_POLICY:BTCUSDT", latestCommit.NoTradeReason);
        Assert.Null(latestCommit.OutboxRecord);
    }

    [Fact]
    public async Task RunOnceFailsClosedWhenTradingRulesAreIncomplete()
    {
        var decisionCommitStore = new FakeDecisionCommitStore();
        using var fixture = CreateHostedService(
            decisionCommitStore,
            // Zero quantity step is never a valid venue rule; zero tick size is not used
            // here because Binance documents it as "rule disabled", not "incomplete".
            new FixedRulesProvider(new BinanceSymbolTradingRules(
                "BTCUSDT", "TRADING", 0.01m, 1_000_000m, 0.01m, 0.0001m, 1_000m, 0m, 10m)));

        await fixture.HostedService.RunOnceAsync(CreateOptions().Value, TestContext.Current.CancellationToken);

        var latestCommit = Assert.Single(decisionCommitStore.Commits);
        Assert.Equal("NO_TRADE", latestCommit.Outcome);
        Assert.Equal("BINANCE_TRADING_RULES_UNAVAILABLE_OR_INCOMPLETE", latestCommit.NoTradeReason);
        Assert.Null(latestCommit.OutboxRecord);
    }

    private static IOptions<WorkerRuntimeOptions> CreateOptions() => Options.Create(new WorkerRuntimeOptions
    {
        AutonomyEnabled = true,
        AutonomySymbol = "BTCUSDT",
        AutonomyInterval = "1m",
        AutonomyKlineLimit = 50,
        PollIntervalMilliseconds = 50,
        ExecutionMode = "PAPER",
        NoTrade = true,
        ReconciliationAccount = "test-account",
        PersistenceEnabled = true,
        PostgresConnectionString = "Host=localhost;Database=test;Username=test;Password=test",
        ApplyMigrations = true,
        RiskPolicyRequired = true,
        ReconciliationEnabled = true,
        AccountStreamEnabled = true
    });

    private static HostedServiceFixture CreateHostedService(
        IAutonomyDecisionCommitStore decisionCommitStore,
        IBinanceSymbolTradingRulesProvider rulesProvider) => new(decisionCommitStore, rulesProvider);

    private static BinanceSpotKline Kline(DateTimeOffset open, decimal close) => new(
        open,
        open.AddMinutes(1).AddMilliseconds(-1),
        close,
        close,
        close,
        close,
        1m,
        "1m");

    private class FakeMarketClient : IBinanceSpotMarketClient
    {
        public int GetKlinesCallCount { get; private set; }
        public int GetBookTickerCallCount { get; private set; }
        public BinanceBookTicker? OtherTicker { get; init; }
        public Func<string, BinanceBookTicker?>? OnBookTicker { get; init; }
        public Func<string, CancellationToken, Task<BinanceOrderBookSnapshot>>? OnDepthSnapshot { get; init; }
        public int MinuteOffset { get; set; }

        public Task PingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<BinanceServerTime> GetServerTimeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BinanceServerTime(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        public virtual Task<IReadOnlyList<BinanceSpotKline>> GetKlinesAsync(
            string symbol,
            string interval,
            DateTimeOffset? startTimeUtc = null,
            DateTimeOffset? endTimeUtc = null,
            int limit = 500,
            CancellationToken cancellationToken = default)
        {
            GetKlinesCallCount++;
            var now = DateTimeOffset.UtcNow;
            var currentMinute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero).AddMinutes(MinuteOffset);
            var list = new List<BinanceSpotKline>
            {
                Kline(currentMinute.AddMinutes(-4), 100m),
                Kline(currentMinute.AddMinutes(-3), 99m),
                Kline(currentMinute.AddMinutes(-2), 98m),
                Kline(currentMinute.AddMinutes(-1), 200m),
                Kline(currentMinute, 201m)
            };
            return Task.FromResult<IReadOnlyList<BinanceSpotKline>>(list);
        }

        public Task<IReadOnlyList<BinanceAggregateTrade>> GetAggregateTradesAsync(
            string symbol,
            DateTimeOffset? endTimeUtc = null,
            int limit = 500,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BinanceAggregateTrade>>([]);

        public Task<BinanceBookTicker> GetBookTickerAsync(string symbol, CancellationToken cancellationToken = default)
        {
            GetBookTickerCallCount++;
            var custom = OnBookTicker?.Invoke(symbol);
            if (custom is not null) return Task.FromResult(custom);
            return Task.FromResult(symbol == "BTCUSDT" && OtherTicker is not null
                ? OtherTicker
                : new BinanceBookTicker(symbol, 100m, 10m, 101m, 10m, DateTimeOffset.UtcNow));
        }

        public Task<BinanceOrderBookSnapshot> GetOrderBookSnapshotAsync(string symbol, int limit = 20, CancellationToken cancellationToken = default) =>
            OnDepthSnapshot?.Invoke(symbol, cancellationToken) ?? Task.FromResult(new BinanceOrderBookSnapshot(symbol, 0, [], []));

        public Task<BinanceExchangeInfo> GetExchangeInfoAsync(string? symbol = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<BinanceSymbolTradingRules> GetTradingRulesAsync(string symbol, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class FakeAccountClient : IBinanceSpotAccountClient
    {
        private readonly IReadOnlyList<BinanceBalance> balances;

        public FakeAccountClient(IReadOnlyList<BinanceBalance>? balances = null)
        {
            this.balances = balances ?? [new BinanceBalance("BTC", "0", "0"), new BinanceBalance("USDT", "1000", "0")];
        }

        public Task<BinanceAccountSnapshot> GetAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BinanceAccountSnapshot(0, 0, 0, 0, true, false, true, 0, "SPOT", balances));

        public Task<BinanceCommissionSnapshot> GetCommissionAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BinanceCommissionSnapshot(
                new BinanceCommissionResponse(
                    symbol,
                    new BinanceCommissionRateSet("0.001", "0.001", "0.001", "0.001"),
                    new BinanceCommissionRateSet("0", "0", "0", "0"),
                    new BinanceCommissionRateSet("0", "0", "0", "0"),
                    new BinanceCommissionDiscount(false, false, null, null)),
                DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<BinanceOpenOrder>> GetOpenOrdersAsync(string? symbol = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BinanceOpenOrder>>([]);

        public Task<IReadOnlyList<BinanceTrade>> GetMyTradesAsync(string symbol, BinanceTradeQuery? query = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BinanceTrade>>([]);

        public Task<BinanceClockSnapshot> SynchronizeClockAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BinanceClockSnapshot(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), TimeSpan.Zero, true));
    }

    private sealed class AllowAllResetAuthorizer : IOperationalControlResetAuthorizer
    {
        public bool IsAuthorized(OperationalControlSnapshot current, OperationalControlCommand command, OperationalControlResetAuthorization authorization) => true;
    }

    private sealed class FakePolicyStore : IRiskPolicyStore
    {
        private readonly RiskPolicyDocument document;

        public FakePolicyStore(RiskPolicyDocument? customDocument = null)
        {
            if (customDocument is not null)
            {
                document = customDocument;
                return;
            }

            var policy = new RiskPolicy("v1", 1000m, 5000m, 500m, 1000m, TimeSpan.FromMinutes(15), withdrawalsEnabled: false);
            var draft = RiskPolicyDocument.CreateDraft(policy, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), "creator");
            draft = draft.Approve("app-1", "approver", DateTimeOffset.UtcNow.AddDays(-1));
            document = draft.Activate(DateTimeOffset.UtcNow.AddDays(-1));
        }

        public Task<RiskPolicyDocument?> GetActiveRiskPolicyAsync(DateTimeOffset atUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<RiskPolicyDocument?>(document);

        public Task SaveRiskPolicyAsync(RiskPolicyDocument document, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<RiskPolicyDocument> ApproveRiskPolicyAsync(string policyVersion, string approvalId, string approver, DateTimeOffset approvedAtUtc, CancellationToken cancellationToken = default) => Task.FromResult(document);
        public Task<RiskPolicyDocument> ActivateRiskPolicyAsync(string policyVersion, DateTimeOffset activatedAtUtc, CancellationToken cancellationToken = default) => Task.FromResult(document);
    }

    private sealed class FakeRiskContextStore : IAutonomyRiskContextStore
    {
        public decimal? LastRecordedEquity { get; private set; }
        public int RecordCount { get; private set; }
        public Action? OnRecord { get; init; }

        public Task<AutonomyRiskContextSnapshot> ReadAutonomyRiskContextAsync(Symbol symbol, Asset quoteAsset, DateTimeOffset dayStartUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AutonomyRiskContextSnapshot(0m, 0m, 0m, true, DateTimeOffset.UtcNow));

        public Task<AutonomyRiskContextSnapshot> RecordAutonomyRiskContextAsync(string scope, Symbol symbol, Asset quoteAsset, DateTimeOffset dayStartUtc, decimal? currentEquityNotional, DateTimeOffset valuationAtUtc, CancellationToken cancellationToken = default)
        {
            LastRecordedEquity = currentEquityNotional;
            RecordCount++;
            OnRecord?.Invoke();
            return Task.FromResult(new AutonomyRiskContextSnapshot(0m, 0m, 0m, true, valuationAtUtc, currentEquityNotional, null, null, false, currentEquityNotional.HasValue));
        }
    }

    private sealed class ManualValuationTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class RecordingNavStore : IPortfolioNavSnapshotStore
    {
        public List<PortfolioNavSnapshot> Snapshots { get; } = [];
        public Task RecordNavSnapshotAsync(PortfolioNavSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Snapshots.Add(snapshot);
            return Task.CompletedTask;
        }
        public Task<AggregatedPortfolioPerformanceMetrics> GetPerformanceMetricsAsync(DateTimeOffset asOfUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AggregatedPortfolioPerformanceMetrics> GetPerformanceMetricsAsync(DateTimeOffset asOfUtc, string? symbol, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeDecisionCommitStore : IAutonomyDecisionCommitStore
    {
        public List<AutonomyDecisionCommit> Commits { get; } = [];

        public Task<DateTimeOffset?> GetLastProcessedCloseAsync(string scope, CancellationToken cancellationToken = default) =>
            Task.FromResult<DateTimeOffset?>(null);

        public Task CommitAsync(AutonomyDecisionCommit commit, CancellationToken cancellationToken = default)
        {
            Commits.Add(commit);
            return Task.CompletedTask;
        }

        public Task<AutonomyDecisionCommit?> ReadDecisionCommitAsync(string decisionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Commits.FirstOrDefault(c => string.Equals(c.DecisionId, decisionId, StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class FakeOpenOrderStore(IReadOnlyList<LocalOpenOrderRecord> orders) : ILocalOpenOrderStore
    {
        public Task<IReadOnlyList<LocalOpenOrderRecord>> ReadOpenOrdersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(orders);

        public Task UpsertOpenOrderAsync(LocalOpenOrderRecord order, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveOpenOrderAsync(string clientOrderId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ThrowingRulesProvider : IBinanceSymbolTradingRulesProvider
    {
        public Task<BinanceSymbolTradingRules> GetAsync(string symbol, CancellationToken cancellationToken = default) =>
            throw new BinanceTradingRulesUnavailableException(symbol, new IOException("test"));

        public Task<BinanceSymbolTradingRules> RefreshAsync(string symbol, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedRulesProvider(BinanceSymbolTradingRules rules) : IBinanceSymbolTradingRulesProvider
    {
        public Task<BinanceSymbolTradingRules> GetAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(rules);

        public Task<BinanceSymbolTradingRules> RefreshAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(rules);
    }

    [Fact]
    public async Task ConsumesMarketDataStreamAndProcessesClosedKlinesImmediately()
    {
        var marketClient = new FakeMarketClient();
        var accountClient = new FakeAccountClient();
        var policyStore = new FakePolicyStore();
        var riskContextStore = new FakeRiskContextStore();
        var decisionCommitStore = new FakeDecisionCommitStore();
        var operationalControl = new InMemoryOperationalControl(new AllowAllResetAuthorizer());
        using var supervisor = new RuntimeSupervisor(new InMemoryRuntimeTransitionSink(), TimeProvider.System, 0, RuntimeState.Ready);
        var cycle = new BaselineDecisionCycle(new TrendFollowingParameters("test", "1.0.0", new Symbol("BTCUSDT"), 2, 3, 0m, 0m));
        var streamClient = new FakeMarketDataStreamClient();

        var options = Options.Create(new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            AutonomySymbol = "BTCUSDT",
            AutonomyInterval = "1m",
            AutonomyKlineLimit = 50,
            PollIntervalMilliseconds = 10_000,
            ExecutionMode = "PAPER",
            NoTrade = true,
            ReconciliationAccount = "test-account",
            PersistenceEnabled = true,
            PostgresConnectionString = "Host=localhost;Database=test;Username=test;Password=test",
            ApplyMigrations = true,
            RiskPolicyRequired = true,
            ReconciliationEnabled = true,
            AccountStreamEnabled = true
        });

        using var hostedService = new AutonomyCycleHostedService(
            options,
            marketClient,
            accountClient,
            policyStore,
            riskContextStore,
            decisionCommitStore,
            operationalControl,
            supervisor,
            cycle,
            NullLogger<AutonomyCycleHostedService>.Instance,
            marketDataStreamClient: streamClient);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = hostedService.StartAsync(cts.Token);

        var startupTriggered = SpinWait.SpinUntil(() => marketClient.GetKlinesCallCount >= 1, TimeSpan.FromSeconds(2));
        Assert.True(startupTriggered, "AutonomyCycleHostedService did not execute startup RunOnceAsync.");
        var initialCallCount = marketClient.GetKlinesCallCount;

        streamClient.EmitKline(new BinanceKlineEvent(
            "btcusdt@kline_1m",
            "BTCUSDT",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "1m",
            84_000m,
            84_100m,
            84_150m,
            83_950m,
            10m,
            IsClosed: true));

        var streamTriggered = SpinWait.SpinUntil(() => marketClient.GetKlinesCallCount > initialCallCount, TimeSpan.FromSeconds(2));
        Assert.True(streamTriggered, "Closed kline stream event did not immediately trigger subsequent autonomy cycle execution.");

        await hostedService.StopAsync(CancellationToken.None).ConfigureAwait(true);
        await runTask.ConfigureAwait(true);

        Assert.True(marketClient.GetKlinesCallCount >= 2);
    }

    [Fact]
    public async Task OperatesInRestOnlyModeWhenMarketDataStreamClientIsNullWithoutLeakingChannelWaiters()
    {
        var marketClient = new FakeMarketClient();
        var accountClient = new FakeAccountClient();
        var policyStore = new FakePolicyStore();
        var riskContextStore = new FakeRiskContextStore();
        var decisionCommitStore = new FakeDecisionCommitStore();
        var operationalControl = new InMemoryOperationalControl(new AllowAllResetAuthorizer());
        using var supervisor = new RuntimeSupervisor(new InMemoryRuntimeTransitionSink(), TimeProvider.System, 0, RuntimeState.Ready);
        var cycle = new BaselineDecisionCycle(new TrendFollowingParameters("test", "1.0.0", new Symbol("BTCUSDT"), 2, 3, 0m, 0m));

        var options = Options.Create(new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            AutonomySymbol = "BTCUSDT",
            AutonomyInterval = "1m",
            AutonomyKlineLimit = 50,
            PollIntervalMilliseconds = 100,
            ExecutionMode = "PAPER",
            NoTrade = true,
            ReconciliationAccount = "test-account",
            PersistenceEnabled = true,
            PostgresConnectionString = "Host=localhost;Database=test;Username=test;Password=test",
            ApplyMigrations = true,
            RiskPolicyRequired = true,
            ReconciliationEnabled = true,
            AccountStreamEnabled = true
        });

        using var hostedService = new AutonomyCycleHostedService(
            options,
            marketClient,
            accountClient,
            policyStore,
            riskContextStore,
            decisionCommitStore,
            operationalControl,
            supervisor,
            cycle,
            NullLogger<AutonomyCycleHostedService>.Instance,
            marketDataStreamClient: null);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = hostedService.StartAsync(cts.Token);

        var executedMultipleTicks = SpinWait.SpinUntil(() => marketClient.GetKlinesCallCount >= 3, TimeSpan.FromSeconds(2));
        Assert.True(executedMultipleTicks, "REST-only autonomy cycle did not complete multiple periodic poll cycles.");

        await hostedService.StopAsync(CancellationToken.None).ConfigureAwait(true);
        await runTask.ConfigureAwait(true);

        Assert.True(marketClient.GetKlinesCallCount >= 3);
    }

    [Fact]
    public async Task RunOnceExecutesMultiAssetScanningAcrossConfiguredSymbols()
    {
        var marketClient = new FakeMarketClient();
        var accountClient = new FakeAccountClient();
        var policyStore = new FakePolicyStore();
        var riskContextStore = new FakeRiskContextStore();
        var decisionCommitStore = new FakeDecisionCommitStore();
        var operationalControl = new InMemoryOperationalControl(new AllowAllResetAuthorizer());
        using var supervisor = new RuntimeSupervisor(new InMemoryRuntimeTransitionSink(), TimeProvider.System, 0, RuntimeState.Ready);
        var cycle = new BaselineDecisionCycle(new TrendFollowingParameters("test", "1.0.0", new Symbol("BTCUSDT"), 2, 3, 0m, 0m));

        var options = Options.Create(new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            AutonomySymbols = "BTCUSDT, ETHUSDT",
            AutonomyInterval = "1m",
            AutonomyKlineLimit = 50,
            PollIntervalMilliseconds = 50,
            ExecutionMode = "PAPER",
            NoTrade = true,
            ReconciliationAccount = "test-account",
            PersistenceEnabled = true,
            PostgresConnectionString = "Host=localhost;Database=test;Username=test;Password=test",
            ApplyMigrations = true,
            RiskPolicyRequired = true,
            ReconciliationEnabled = true,
            AccountStreamEnabled = true
        });

        using var hostedService = new AutonomyCycleHostedService(
            options,
            marketClient,
            accountClient,
            policyStore,
            riskContextStore,
            decisionCommitStore,
            operationalControl,
            supervisor,
            cycle,
            NullLogger<AutonomyCycleHostedService>.Instance);

        var symbols = options.Value.ResolveAutonomySymbols();
        foreach (var sym in symbols)
        {
            await hostedService.RunOnceAsync(options.Value, sym, TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, decisionCommitStore.Commits.Count);
        Assert.Equal("BTCUSDT", decisionCommitStore.Commits[0].Symbol.Value);
        Assert.Equal("ETHUSDT", decisionCommitStore.Commits[1].Symbol.Value);
    }

    [Fact]
    public async Task RunOnceDoesNotValuePortfolioWhenBnbIsOutsideUniverse()
    {
        var marketClient = new FakeMarketClient();
        var accountClient = new FakeAccountClient([
            new BinanceBalance("USDT", "5000", "0"),
            new BinanceBalance("BTC", "0.05", "0"),
            new BinanceBalance("BNB", "1.5", "0"),
            new BinanceBalance("ETH", "0", "0")
        ]);
        var policyStore = new FakePolicyStore();
        var riskContextStore = new FakeRiskContextStore();
        var decisionCommitStore = new FakeDecisionCommitStore();
        var operationalControl = new InMemoryOperationalControl(new AllowAllResetAuthorizer());
        using var supervisor = new RuntimeSupervisor(new InMemoryRuntimeTransitionSink(), TimeProvider.System, 0, RuntimeState.Ready);
        var cycle = new BaselineDecisionCycle(new TrendFollowingParameters("test", "1.0.0", new Symbol("ETHUSDT"), 2, 3, 0m, 0m));

        var options = Options.Create(new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            AutonomySymbols = "BTCUSDT, ETHUSDT",
            AutonomyInterval = "1m",
            AutonomyKlineLimit = 50,
            PollIntervalMilliseconds = 50,
            ExecutionMode = "PAPER",
            NoTrade = true,
            ReconciliationAccount = "test-account",
            PersistenceEnabled = true,
            PostgresConnectionString = "Host=localhost;Database=test;Username=test;Password=test",
            ApplyMigrations = true,
            RiskPolicyRequired = true,
            ReconciliationEnabled = true,
            AccountStreamEnabled = true
        });

        using var hostedService = new AutonomyCycleHostedService(
            options,
            marketClient,
            accountClient,
            policyStore,
            riskContextStore,
            decisionCommitStore,
            operationalControl,
            supervisor,
            cycle,
            NullLogger<AutonomyCycleHostedService>.Instance);

        await hostedService.RunOnceAsync(options.Value, "ETHUSDT", TestContext.Current.CancellationToken);

        Assert.Single(decisionCommitStore.Commits);
        Assert.Equal("ETHUSDT", decisionCommitStore.Commits[0].Symbol.Value);
        Assert.Equal(1, riskContextStore.RecordCount);
        Assert.Null(riskContextStore.LastRecordedEquity);
        Assert.Null(decisionCommitStore.Commits[0].OutboxRecord);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("crossed")]
    [InlineData("wrong-symbol")]
    public async Task RunOnceDoesNotValueOtherPositionWithInvalidTicker(string failure)
    {
        var now = DateTimeOffset.UtcNow;
        var ticker = new BinanceBookTicker(
            failure == "wrong-symbol" ? "SOLUSDT" : "BTCUSDT", 100m, 10m,
            failure == "crossed" ? 99m : 101m, 10m,
            failure == "stale" ? now.AddHours(-1) : failure == "future" ? now.AddHours(1) : now);
        var market = new FakeMarketClient { OtherTicker = ticker };
        var account = new FakeAccountClient([new BinanceBalance("BTC", "1", "0"), new BinanceBalance("ETH", "0", "0"), new BinanceBalance("USDT", "5000", "0")]);
        var risk = new FakeRiskContextStore();
        var commits = new FakeDecisionCommitStore();
        var control = new InMemoryOperationalControl(new AllowAllResetAuthorizer());
        using var supervisor = new RuntimeSupervisor(new InMemoryRuntimeTransitionSink(), TimeProvider.System, 0, RuntimeState.Ready);
        var cycle = new BaselineDecisionCycle(new TrendFollowingParameters("test", "1.0.0", new Symbol("ETHUSDT"), 2, 3, 0m, 0m));
        var options = Options.Create(new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            AutonomySymbol = "ETHUSDT",
            AutonomySymbols = "BTCUSDT,ETHUSDT",
            AutonomyInterval = "1m",
            AutonomyKlineLimit = 50,
            PollIntervalMilliseconds = 100,
            ExecutionMode = "PAPER",
            NoTrade = true,
            ReconciliationAccount = "test-account"
        });
        using var service = new AutonomyCycleHostedService(options, market, account, new FakePolicyStore(), risk,
            commits, control, supervisor, cycle, NullLogger<AutonomyCycleHostedService>.Instance);
        service.StreamBookTickers["BTCUSDT"] = ticker;

        await service.RunOnceAsync(options.Value, "ETHUSDT", TestContext.Current.CancellationToken);

        Assert.Equal(1, risk.RecordCount);
        Assert.Null(risk.LastRecordedEquity);
        Assert.Null(Assert.Single(commits.Commits).OutboxRecord);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunOnceRevalidatesOtherTickerBeforeNavAndDecision(bool expires)
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new ManualValuationTimeProvider(now);
        var ticker = new BinanceBookTicker("BTCUSDT", 100m, 10m, 101m, 10m, now.AddMinutes(-15).AddSeconds(2));
        var market = new FakeMarketClient { OtherTicker = ticker };
        var account = new FakeAccountClient([new BinanceBalance("BTC", "1", "0"), new BinanceBalance("ETH", "0", "0"), new BinanceBalance("USDT", "5000", "0")]);
        var risk = new FakeRiskContextStore { OnRecord = () => clock.Advance(TimeSpan.FromSeconds(expires ? 3 : 1)) };
        var nav = new RecordingNavStore();
        var commits = new FakeDecisionCommitStore();
        var control = new InMemoryOperationalControl(new AllowAllResetAuthorizer());
        using var supervisor = new RuntimeSupervisor();
        await supervisor.BeginBootstrappingAsync("test", TestContext.Current.CancellationToken);
        await supervisor.MarkReconciliationAsync(true, "test", TestContext.Current.CancellationToken);
        await supervisor.MarkAccountStreamAsync(true, "test", TestContext.Current.CancellationToken);
        await supervisor.MarkWarmupAsync(true, "test", TestContext.Current.CancellationToken);
        var cycle = new BaselineDecisionCycle(new TrendFollowingParameters("test", "1.0.0", new Symbol("ETHUSDT"), 2, 3, 0m, 0m));
        var options = Options.Create(new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            AutonomySymbol = "ETHUSDT",
            AutonomySymbols = "BTCUSDT,ETHUSDT",
            AutonomyInterval = "1m",
            AutonomyKlineLimit = 50,
            PollIntervalMilliseconds = 100,
            ExecutionMode = "PAPER",
            NoTrade = false,
            ReconciliationAccount = "test-account"
        });
        using var service = new AutonomyCycleHostedService(options, market, account, new FakePolicyStore(), risk,
            commits, control, supervisor, cycle, NullLogger<AutonomyCycleHostedService>.Instance,
            navSnapshots: nav, timeProvider: clock);
        service.StreamBookTickers["BTCUSDT"] = ticker;
        service.StreamBookTickers["ETHUSDT"] = new BinanceBookTicker("ETHUSDT", 100m, 10m, 101m, 10m, now);

        await service.RunOnceAsync(options.Value, "ETHUSDT", TestContext.Current.CancellationToken);

        Assert.NotNull(risk.LastRecordedEquity);
        var commit = Assert.Single(commits.Commits);
        if (expires)
        {
            Assert.Empty(nav.Snapshots);
            Assert.Contains(RiskReasonCodes.RiskContextUnavailable, commit.RiskDecision?.ReasonCodes ?? []);
            Assert.Null(commit.OutboxRecord);
        }
        else
        {
            Assert.Equal(clock.GetUtcNow(), Assert.Single(nav.Snapshots).ObservedAtUtc);
            Assert.DoesNotContain(RiskReasonCodes.RiskContextUnavailable, commit.RiskDecision?.ReasonCodes ?? []);
        }
    }

    [Theory]
    [InlineData("wrong-symbol")]
    [InlineData("zero-bid")]
    [InlineData("crossed")]
    [InlineData("future")]
    [InlineData("stale")]
    public async Task RunOnceRejectsInvalidPrimaryTickerFromCacheAndRest(string failure)
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new ManualValuationTimeProvider(now);
        var ticker = new BinanceBookTicker(failure == "wrong-symbol" ? "BTCUSDT" : "ETHUSDT",
            failure == "zero-bid" ? 0m : 100m, 10m, failure == "crossed" ? 99m : 101m, 10m,
            failure == "future" ? now.AddHours(1) : failure == "stale" ? now.AddHours(-1) : now);
        var market = new FakeMarketClient { OnBookTicker = symbol => symbol == "ETHUSDT" ? ticker : null };
        var account = new FakeAccountClient([new BinanceBalance("ETH", "0", "0"), new BinanceBalance("USDT", "5000", "0")]);
        var nav = new RecordingNavStore();
        var commits = new FakeDecisionCommitStore();
        var control = new InMemoryOperationalControl(new AllowAllResetAuthorizer());
        using var supervisor = new RuntimeSupervisor(new InMemoryRuntimeTransitionSink(), TimeProvider.System, 0, RuntimeState.Ready);
        var cycle = new BaselineDecisionCycle(new TrendFollowingParameters("test", "1.0.0", new Symbol("ETHUSDT"), 2, 3, 0m, 0m));
        var options = Options.Create(new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            AutonomySymbol = "ETHUSDT",
            AutonomySymbols = "ETHUSDT",
            AutonomyInterval = "1m",
            AutonomyKlineLimit = 50,
            PollIntervalMilliseconds = 100,
            ExecutionMode = "PAPER",
            NoTrade = true,
            ReconciliationAccount = "test-account"
        });
        using var service = new AutonomyCycleHostedService(options, market, account, new FakePolicyStore(), new FakeRiskContextStore(),
            commits, control, supervisor, cycle, NullLogger<AutonomyCycleHostedService>.Instance, navSnapshots: nav, timeProvider: clock);
        service.StreamBookTickers["ETHUSDT"] = ticker;

        await service.RunOnceAsync(options.Value, "ETHUSDT", TestContext.Current.CancellationToken);

        Assert.Empty(nav.Snapshots);
        Assert.Empty(commits.Commits);
    }

    [Fact]
    public async Task RunOnceDoesNotRecordEquityWhenEarlierTickerExpiresDuringLaterFetch()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new ManualValuationTimeProvider(now);
        var btc = new BinanceBookTicker("BTCUSDT", 100m, 10m, 101m, 10m, now.AddMinutes(-15).AddSeconds(2));
        var market = new FakeMarketClient
        {
            OnBookTicker = symbol =>
            {
                if (symbol != "SOLUSDT") return null;
                clock.Advance(TimeSpan.FromSeconds(3));
                return new BinanceBookTicker("SOLUSDT", 100m, 10m, 101m, 10m, clock.GetUtcNow());
            }
        };
        var account = new FakeAccountClient([new BinanceBalance("BTC", "1", "0"), new BinanceBalance("ETH", "0", "0"), new BinanceBalance("SOL", "1", "0"), new BinanceBalance("USDT", "5000", "0")]);
        var risk = new FakeRiskContextStore();
        var nav = new RecordingNavStore();
        var control = new InMemoryOperationalControl(new AllowAllResetAuthorizer());
        using var supervisor = new RuntimeSupervisor(new InMemoryRuntimeTransitionSink(), TimeProvider.System, 0, RuntimeState.Ready);
        var cycle = new BaselineDecisionCycle(new TrendFollowingParameters("test", "1.0.0", new Symbol("ETHUSDT"), 2, 3, 0m, 0m));
        var options = Options.Create(new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            AutonomySymbol = "ETHUSDT",
            AutonomySymbols = "BTCUSDT,ETHUSDT,SOLUSDT",
            AutonomyInterval = "1m",
            AutonomyKlineLimit = 50,
            PollIntervalMilliseconds = 100,
            ExecutionMode = "PAPER",
            NoTrade = true,
            ReconciliationAccount = "test-account"
        });
        using var service = new AutonomyCycleHostedService(options, market, account, new FakePolicyStore(), risk,
            new FakeDecisionCommitStore(), control, supervisor, cycle, NullLogger<AutonomyCycleHostedService>.Instance,
            navSnapshots: nav, timeProvider: clock);
        service.StreamBookTickers["BTCUSDT"] = btc;
        service.StreamBookTickers["ETHUSDT"] = new BinanceBookTicker("ETHUSDT", 100m, 10m, 101m, 10m, now);

        await service.RunOnceAsync(options.Value, "ETHUSDT", TestContext.Current.CancellationToken);

        Assert.Equal(1, risk.RecordCount);
        Assert.Null(risk.LastRecordedEquity);
        Assert.Empty(nav.Snapshots);
    }

    [Fact]
    public async Task RunOnceUsesStreamBookTickerWhenFreshAndFallsBackToRestWhenStale()
    {
        var marketClient = new FakeMarketClient();
        var accountClient = new FakeAccountClient([
            new BinanceBalance("USDT", "5000", "0"),
            new BinanceBalance("BTC", "0", "0"),
            new BinanceBalance("ETH", "0", "0"),
            new BinanceBalance("SOL", "0", "0")
        ]);
        var policyStore = new FakePolicyStore();
        var riskContextStore = new FakeRiskContextStore();
        var decisionCommitStore = new FakeDecisionCommitStore();
        var operationalControl = new InMemoryOperationalControl(new AllowAllResetAuthorizer());
        using var supervisor = new RuntimeSupervisor(new InMemoryRuntimeTransitionSink(), TimeProvider.System, 0, RuntimeState.Ready);
        var cycle = new BaselineDecisionCycle(new TrendFollowingParameters("test", "1.0.0", new Symbol("BTCUSDT"), 2, 3, 0m, 0m));

        var options = Options.Create(new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            AutonomySymbols = "BTCUSDT, ETHUSDT, SOLUSDT",
            AutonomyInterval = "1m",
            AutonomyKlineLimit = 50,
            PollIntervalMilliseconds = 50,
            ExecutionMode = "PAPER",
            NoTrade = true,
            ReconciliationAccount = "test-account",
            PersistenceEnabled = true,
            PostgresConnectionString = "Host=localhost;Database=test;Username=test;Password=test",
            ApplyMigrations = true,
            RiskPolicyRequired = true,
            ReconciliationEnabled = true,
            AccountStreamEnabled = true
        });

        using var hostedService = new AutonomyCycleHostedService(
            options,
            marketClient,
            accountClient,
            policyStore,
            riskContextStore,
            decisionCommitStore,
            operationalControl,
            supervisor,
            cycle,
            NullLogger<AutonomyCycleHostedService>.Instance);

        // Scenario 1: Fresh in-memory stream book ticker -> REST GetBookTickerAsync should NOT be called.
        hostedService.StreamBookTickers["BTCUSDT"] = new BinanceBookTicker("BTCUSDT", 60000m, 1m, 60001m, 1m, DateTimeOffset.UtcNow);
        await hostedService.RunOnceAsync(options.Value, "BTCUSDT", TestContext.Current.CancellationToken);
        Assert.Equal(0, marketClient.GetBookTickerCallCount);

        // Scenario 2: Stale in-memory stream book ticker (> 15m) -> falls back to REST GetBookTickerAsync.
        hostedService.StreamBookTickers["ETHUSDT"] = new BinanceBookTicker("ETHUSDT", 3000m, 1m, 3001m, 1m, DateTimeOffset.UtcNow.AddMinutes(-20));
        await hostedService.RunOnceAsync(options.Value, "ETHUSDT", TestContext.Current.CancellationToken);
        Assert.Equal(1, marketClient.GetBookTickerCallCount);

        // Scenario 3: Cache miss -> falls back to REST GetBookTickerAsync.
        await hostedService.RunOnceAsync(options.Value, "SOLUSDT", TestContext.Current.CancellationToken);
        Assert.Equal(2, marketClient.GetBookTickerCallCount);

        // Scenario 4: Fresh in-memory stream klines (>= 50) -> REST GetKlinesAsync is bypassed.
        var klines = new List<BinanceSpotKline>();
        for (var i = 0; i < 50; i++)
        {
            klines.Add(new BinanceSpotKline(
                DateTimeOffset.UtcNow.AddMinutes(-50 + i),
                DateTimeOffset.UtcNow.AddMinutes(-49 + i),
                100m, 101m, 99m, 100m, 10m, "1m"));
        }
        hostedService.KlineCache["BTCUSDT"] = klines;
        var initialKlinesCount = marketClient.GetKlinesCallCount;
        await hostedService.RunOnceAsync(options.Value, "BTCUSDT", TestContext.Current.CancellationToken);
        Assert.Equal(initialKlinesCount, marketClient.GetKlinesCallCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunOnceDoesNotTreatTestnetEndowmentsAsZeroOrIgnoreOtherAssets(bool unsupportedBalances)
    {
        var marketClient = new FakeMarketClient();
        var balances = new List<BinanceBalance>
        {
            new("USDT", "10000", "0"),
            new("BTC", unsupportedBalances ? "1.0" : "0", "0"),
            new("ETH", unsupportedBalances ? "1.0" : "0", "0"),
            new("SOL", unsupportedBalances ? "4.0" : "0", "0"),
            new("BNB", "0.985", "0") // BNB burned from 1.0 down to 0.985 for fee discount
        };
        for (var i = 0; i < 20; i++)
        {
            balances.Add(new BinanceBalance($"FAUCET{i}", "0", "0"));
        }
        var accountClient = new FakeAccountClient(balances);
        var policyStore = new FakePolicyStore();
        var riskContextStore = new FakeRiskContextStore();
        var decisionCommitStore = new FakeDecisionCommitStore();
        var operationalControl = new InMemoryOperationalControl(new AllowAllResetAuthorizer());
        using var supervisor = new RuntimeSupervisor(new InMemoryRuntimeTransitionSink(), TimeProvider.System, 0, RuntimeState.Ready);
        var cycle = new BaselineDecisionCycle(new TrendFollowingParameters("test", "1.0.0", new Symbol("BNBUSDT"), 2, 3, 0m, 0m));

        var options = Options.Create(new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            AutonomySymbols = "BNBUSDT",
            AutonomyInterval = "1m",
            AutonomyKlineLimit = 50,
            PollIntervalMilliseconds = 50,
            ExecutionMode = "TESTNET",
            NoTrade = true,
            ReconciliationAccount = "test-account",
            PersistenceEnabled = true,
            PostgresConnectionString = "Host=localhost;Database=test;Username=test;Password=test",
            ApplyMigrations = true,
            RiskPolicyRequired = true,
            ReconciliationEnabled = true,
            AccountStreamEnabled = true
        });

        using var hostedService = new AutonomyCycleHostedService(
            options,
            marketClient,
            accountClient,
            policyStore,
            riskContextStore,
            decisionCommitStore,
            operationalControl,
            supervisor,
            cycle,
            NullLogger<AutonomyCycleHostedService>.Instance);

        await hostedService.RunOnceAsync(options.Value, "BNBUSDT", TestContext.Current.CancellationToken);

        Assert.NotEmpty(decisionCommitStore.Commits);
        var latestCommit = decisionCommitStore.Commits[^1];
        Assert.Equal(1, riskContextStore.RecordCount);
        if (unsupportedBalances)
        {
            Assert.Null(riskContextStore.LastRecordedEquity);
        }
        else
        {
            Assert.True(riskContextStore.LastRecordedEquity > 10_000m);
        }
        Assert.Null(latestCommit.OutboxRecord);
    }

    [Fact]
    public async Task AutonomyCycleDispatchesAlertOnSafeModeTransition()
    {
        var marketClient = new FakeMarketClient();
        var accountClient = new FakeAccountClient();
        var policyStore = new FakePolicyStore();
        var riskContextStore = new FakeRiskContextStore();
        var decisionCommitStore = new FakeDecisionCommitStore();
        var operationalControl = new InMemoryOperationalControl(new AllowAllResetAuthorizer());
        using var supervisor = new RuntimeSupervisor(new InMemoryRuntimeTransitionSink(), TimeProvider.System, 0, RuntimeState.Ready);
        var cycle = new BaselineDecisionCycle(new TrendFollowingParameters("test", "1.0.0", new Symbol("BTCUSDT"), 2, 3, 0m, 0m));

        var alertSink = new InMemoryTradingAlertSink();
        using var alertDispatcher = new TradingAlertDispatcher(alertSink);

        var options = Options.Create(new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            AutonomySymbol = "BTCUSDT",
            AutonomyInterval = "1m",
            AutonomyKlineLimit = 50,
            PollIntervalMilliseconds = 100,
            ExecutionMode = "PAPER",
            NoTrade = true,
            ReconciliationAccount = "test-account",
            PersistenceEnabled = true,
            PostgresConnectionString = "Host=localhost;Database=test;Username=test;Password=test",
            ApplyMigrations = true,
            RiskPolicyRequired = true,
            ReconciliationEnabled = true,
            AccountStreamEnabled = true
        });

        // Simulating a failure by passing a market client that throws an unhandled exception
        var throwingMarketClient = new ThrowingMarketClient(new InvalidOperationException("POSITION_BASIS_DRIFT unexpected mismatch"));

        using var hostedService = new AutonomyCycleHostedService(
            options,
            throwingMarketClient,
            accountClient,
            policyStore,
            riskContextStore,
            decisionCommitStore,
            operationalControl,
            supervisor,
            cycle,
            NullLogger<AutonomyCycleHostedService>.Instance,
            alertDispatcher: alertDispatcher);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = hostedService.StartAsync(cts.Token);

        var alertReceived = SpinWait.SpinUntil(() => alertSink.Alerts.Count > 0, TimeSpan.FromSeconds(3));
        await hostedService.StopAsync(CancellationToken.None).ConfigureAwait(true);
        await runTask.ConfigureAwait(true);

        Assert.True(alertReceived, "AutonomyCycle did not dispatch alert on SafeMode transition.");
        var alert = Assert.Single(alertSink.Alerts);
        Assert.Equal(TradingAlertSeverity.Critical, alert.Severity);
        Assert.Equal("autonomy/operational_control_escalation", alert.Scope);
        Assert.Equal(OperationalMode.SafeMode, operationalControl.Snapshot.Mode);
    }

    [Fact]
    public async Task RunOnceCatchesKlineQualityGateRejectionWithoutSafeMode()
    {
        var marketClient = new InvalidKlinesMarketClient();
        var accountClient = new FakeAccountClient();
        var policyStore = new FakePolicyStore();
        var riskContextStore = new FakeRiskContextStore();
        var decisionCommitStore = new FakeDecisionCommitStore();
        var operationalControl = new InMemoryOperationalControl(new AllowAllResetAuthorizer());
        using var supervisor = new RuntimeSupervisor(new InMemoryRuntimeTransitionSink(), TimeProvider.System, 0, RuntimeState.Ready);
        var cycle = new BaselineDecisionCycle(new TrendFollowingParameters("test", "1.0.0", new Symbol("BTCUSDT"), 2, 3, 0m, 0m));

        var options = Options.Create(new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            AutonomySymbol = "BTCUSDT",
            AutonomyInterval = "1m",
            AutonomyKlineLimit = 50,
            PollIntervalMilliseconds = 100,
            ExecutionMode = "PAPER",
            NoTrade = true,
            ReconciliationAccount = "test-account",
            PersistenceEnabled = true,
            PostgresConnectionString = "Host=localhost;Database=test;Username=test;Password=test",
            ApplyMigrations = true,
            RiskPolicyRequired = true,
            ReconciliationEnabled = true,
            AccountStreamEnabled = true
        });

        using var hostedService = new AutonomyCycleHostedService(
            options,
            marketClient,
            accountClient,
            policyStore,
            riskContextStore,
            decisionCommitStore,
            operationalControl,
            supervisor,
            cycle,
            NullLogger<AutonomyCycleHostedService>.Instance);

        var exception = await Record.ExceptionAsync(() => hostedService.RunOnceAsync(options.Value, "BTCUSDT", TestContext.Current.CancellationToken));

        Assert.Null(exception);
        Assert.Equal(OperationalMode.Normal, operationalControl.Snapshot.Mode);
        Assert.Equal(RuntimeState.Ready, supervisor.Snapshot.State);
        Assert.Empty(decisionCommitStore.Commits);
    }

    [Fact]
    public async Task AutonomyCycleHostedServicePopulatesDynamicThresholdsFromClosedBars()
    {
        var options = Options.Create(new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            AutonomySymbol = "BTCUSDT",
            AutonomySymbols = "BTCUSDT",
            AutonomyInterval = "1m",
            AutonomyKlineLimit = 50,
            PollIntervalMilliseconds = 100,
            ExecutionMode = "TESTNET",
            NoTrade = true,
            ReconciliationAccount = "test-account",
            PersistenceEnabled = false,
            RiskPolicyRequired = false,
            ReconciliationEnabled = false,
            AccountStreamEnabled = false
        });

        var marketClient = new FakeMarketClient();
        var accountClient = new FakeAccountClient();
        var policyStore = new FakePolicyStore();
        var riskContextStore = new FakeRiskContextStore();
        var decisionCommitStore = new FakeDecisionCommitStore();
        var operationalControl = new InMemoryOperationalControl(new AllowAllResetAuthorizer());
        using var supervisor = new RuntimeSupervisor(new InMemoryRuntimeTransitionSink(), TimeProvider.System, 0, RuntimeState.Ready);
        var cycle = new BaselineDecisionCycle(new TrendFollowingParameters("test", "1.0.0", new Symbol("BTCUSDT"), 2, 3, 0m, 0m));

        using var hostedService = new AutonomyCycleHostedService(
            options,
            marketClient,
            accountClient,
            policyStore,
            riskContextStore,
            decisionCommitStore,
            operationalControl,
            supervisor,
            cycle,
            NullLogger<AutonomyCycleHostedService>.Instance);

        await hostedService.RunOnceAsync(options.Value, "BTCUSDT", TestContext.Current.CancellationToken);

        Assert.True(hostedService.DynamicExitThresholdsBySymbol.TryGetValue("BTCUSDT", out var thresholds));
        Assert.True(thresholds.TargetProfitBps >= 180.0m);
        Assert.True(thresholds.StopLossBps >= 100.0m);
        Assert.True(thresholds.ChandelierActivationBps >= 75.0m);
    }

    private sealed class InvalidKlinesMarketClient : FakeMarketClient
    {
        public override Task<IReadOnlyList<BinanceSpotKline>> GetKlinesAsync(
            string symbol,
            string interval,
            DateTimeOffset? startTimeUtc = null,
            DateTimeOffset? endTimeUtc = null,
            int limit = 500,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var list = new List<BinanceSpotKline>
            {
                Kline(now.AddMinutes(-5), 100m),
                Kline(now.AddMinutes(-5), 101m)
            };
            return Task.FromResult<IReadOnlyList<BinanceSpotKline>>(list);
        }
    }

    private sealed class ThrowingMarketClient(Exception exception) : FakeMarketClient
    {
        public override Task<IReadOnlyList<BinanceSpotKline>> GetKlinesAsync(string symbol, string interval, DateTimeOffset? startTimeUtc = null, DateTimeOffset? endTimeUtc = null, int limit = 500, CancellationToken cancellationToken = default) =>
            throw exception;
    }

    private sealed class FakeMarketDataStreamClient : IBinanceSpotMarketDataClient
    {
        private readonly System.Threading.Channels.Channel<BinanceMarketDataEvent> events =
            System.Threading.Channels.Channel.CreateUnbounded<BinanceMarketDataEvent>();

        public void EmitKline(BinanceKlineEvent kline) => events.Writer.TryWrite(kline);

        public async IAsyncEnumerable<BinanceMarketDataEvent> ReadAsync(
            IEnumerable<BinanceMarketSubscription> subscriptions,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (await events.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (events.Reader.TryRead(out var item))
                {
                    yield return item;
                }
            }
        }
    }

    private sealed class DisconnectTestStream : IBinanceSpotMarketDataClient
    {
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<BinanceMarketDataEvent> ReadAsync(
            IEnumerable<BinanceMarketSubscription> subscriptions,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new BinanceMarketStreamDisconnectedEvent(DateTimeOffset.UtcNow, BinanceMarketDisconnectReason.ServerShutdown);
            Delivered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task MarketDisconnectInvalidatesTickerAndBridgedBookBeforeNextEvent()
    {
        var stream = new DisconnectTestStream();
        var options = Options.Create(new WorkerRuntimeOptions { AutonomySymbol = "BTCUSDT", AutonomySymbols = "BTCUSDT", AutonomyInterval = "1m" });
        var control = new InMemoryOperationalControl(new AllowAllResetAuthorizer());
        using var supervisor = new RuntimeSupervisor(new InMemoryRuntimeTransitionSink(), TimeProvider.System, 0, RuntimeState.Ready);
        var cycle = new BaselineDecisionCycle(new TrendFollowingParameters("test", "1.0.0", new Symbol("BTCUSDT"), 2, 3, 0m, 0m));
        using var service = new AutonomyCycleHostedService(options, new FakeMarketClient(), new FakeAccountClient(),
            new FakePolicyStore(), new FakeRiskContextStore(), new FakeDecisionCommitStore(), control, supervisor,
            cycle, NullLogger<AutonomyCycleHostedService>.Instance, marketDataStreamClient: stream);
        var now = DateTimeOffset.UtcNow;
        service.StreamBookTickers["BTCUSDT"] = new BinanceBookTicker("BTCUSDT", 100m, 1m, 101m, 1m, now);
        var book = new BinanceLocalOrderBook("BTCUSDT");
        book.ApplySnapshot(new BinanceOrderBookSnapshot("BTCUSDT", 100, [new BinanceOrderBookLevel(100m, 1m)], [new BinanceOrderBookLevel(101m, 1m)]));
        book.ApplyDelta(new BinanceDepthUpdateEvent("btcusdt@depth", "BTCUSDT", 1, now, 101, 101, null, [], []));
        Assert.NotNull(book.CalculateTopDepthLiquidityNotional());
        service.LocalOrderBooks["BTCUSDT"] = book;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var consumer = service.ConsumeMarketDataStreamAsync(options.Value, cancellation.Token);
        try
        {
            await stream.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Empty(service.StreamBookTickers);
            Assert.Equal(BinanceOrderBookStatus.Invalid, book.Status);
            Assert.Null(book.CalculateTopDepthLiquidityNotional());
        }
        finally
        {
            await cancellation.CancelAsync();
            await consumer.ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task SnapshotRequestedBeforeDisconnectCannotReviveBookAfterDisconnect()
    {
        var snapshotStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotResult = new TaskCompletionSource<BinanceOrderBookSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var market = new FakeMarketClient
        {
            OnDepthSnapshot = (_, token) =>
            {
                snapshotStarted.TrySetResult();
                return snapshotResult.Task.WaitAsync(token);
            }
        };
        var stream = new DisconnectTestStream();
        var options = Options.Create(new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            AutonomySymbol = "BTCUSDT",
            AutonomySymbols = "BTCUSDT",
            AutonomyInterval = "1m",
            AutonomyKlineLimit = 50,
            PollIntervalMilliseconds = 100,
            ExecutionMode = "PAPER",
            NoTrade = true,
            ReconciliationAccount = "test-account"
        });
        var control = new InMemoryOperationalControl(new AllowAllResetAuthorizer());
        using var supervisor = new RuntimeSupervisor(new InMemoryRuntimeTransitionSink(), TimeProvider.System, 0, RuntimeState.Ready);
        var cycle = new BaselineDecisionCycle(new TrendFollowingParameters("test", "1.0.0", new Symbol("BTCUSDT"), 2, 3, 0m, 0m));
        using var service = new AutonomyCycleHostedService(options, market, new FakeAccountClient(), new FakePolicyStore(),
            new FakeRiskContextStore(), new FakeDecisionCommitStore(), control, supervisor, cycle,
            NullLogger<AutonomyCycleHostedService>.Instance, marketDataStreamClient: stream);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = service.RunOnceAsync(options.Value, "BTCUSDT", cancellation.Token);
        await snapshotStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var consumer = service.ConsumeMarketDataStreamAsync(options.Value, cancellation.Token);
        try
        {
            await stream.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            snapshotResult.TrySetResult(new BinanceOrderBookSnapshot("BTCUSDT", 100, [new BinanceOrderBookLevel(100m, 1m)], [new BinanceOrderBookLevel(101m, 1m)]));
            await run.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(BinanceOrderBookStatus.Invalid, service.LocalOrderBooks["BTCUSDT"].Status);
        }
        finally
        {
            await cancellation.CancelAsync();
            await consumer.ConfigureAwait(true);
        }
    }

    private sealed class HostedServiceFixture : IDisposable
    {
        private readonly RuntimeSupervisor supervisor = new(new InMemoryRuntimeTransitionSink(), TimeProvider.System, 0, RuntimeState.Ready);

        public HostedServiceFixture(
            IAutonomyDecisionCommitStore decisionCommitStore,
            IBinanceSymbolTradingRulesProvider rulesProvider)
        {
            HostedService = new AutonomyCycleHostedService(
                CreateOptions(),
                new FakeMarketClient(),
                new FakeAccountClient(),
                new FakePolicyStore(),
                new FakeRiskContextStore(),
                decisionCommitStore,
                new InMemoryOperationalControl(new AllowAllResetAuthorizer()),
                supervisor,
                new BaselineDecisionCycle(new TrendFollowingParameters("test", "1.0.0", new Symbol("BTCUSDT"), 2, 3, 0m, 0m)),
                NullLogger<AutonomyCycleHostedService>.Instance,
                rulesProvider: rulesProvider);
        }

        public AutonomyCycleHostedService HostedService { get; }

        public void Dispose()
        {
            HostedService.Dispose();
            supervisor.Dispose();
        }
    }
}
