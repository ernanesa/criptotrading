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
    public async Task RunOnceFailsClosedWhenTradingRulesAreIncomplete()
    {
        var decisionCommitStore = new FakeDecisionCommitStore();
        using var fixture = CreateHostedService(
            decisionCommitStore,
            new FixedRulesProvider(new BinanceSymbolTradingRules(
                "BTCUSDT", "TRADING", 0.01m, 1_000_000m, 0m, 0.0001m, 1_000m, 0.0001m, 10m)));

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
            return Task.FromResult(new BinanceBookTicker(symbol, 100m, 10m, 101m, 10m, DateTimeOffset.UtcNow));
        }

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

        public FakePolicyStore()
        {
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
        public Task<AutonomyRiskContextSnapshot> ReadAutonomyRiskContextAsync(Symbol symbol, Asset quoteAsset, DateTimeOffset dayStartUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AutonomyRiskContextSnapshot(0m, 0m, 0m, true, DateTimeOffset.UtcNow));

        public Task<AutonomyRiskContextSnapshot> RecordAutonomyRiskContextAsync(string scope, Symbol symbol, Asset quoteAsset, DateTimeOffset dayStartUtc, decimal? currentEquityNotional, DateTimeOffset valuationAtUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AutonomyRiskContextSnapshot(0m, 0m, 0m, true, valuationAtUtc, currentEquityNotional, null, null, false, true));
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
    public async Task RunOnceAllowsMultiAssetScanningWhileHoldingPositionsInUniverseAssets()
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
        Assert.DoesNotContain(RiskReasonCodes.RiskContextUnavailable, decisionCommitStore.Commits[0].RiskDecision?.ReasonCodes ?? []);
        Assert.NotEqual(RiskReasonCodes.RiskContextUnavailable, decisionCommitStore.Commits[0].NoTradeReason);
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

    [Fact]
    public async Task RunOnceDeductsMultiAssetTestnetFaucetBalancesContinuouslyWithoutCliffEdge()
    {
        var marketClient = new FakeMarketClient();
        var balances = new List<BinanceBalance>
        {
            new("USDT", "10000", "0"),
            new("BTC", "1.0", "0"),
            new("ETH", "1.0", "0"),
            new("SOL", "4.0", "0"),
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
        Assert.NotEqual("POSITION_BASIS_DRIFT", latestCommit.NoTradeReason);
        Assert.DoesNotContain("POSITION_BASIS_DRIFT", latestCommit.NoTradeReason ?? string.Empty, StringComparison.Ordinal);
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
