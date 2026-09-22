using System.Globalization;
using CryptoTrading.Backtesting.Autonomy;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Domain.Runtime;
using CryptoTrading.Exchange.Binance;
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
            NullLogger<AutonomyCycleHostedService>.Instance,
            openOrderStore);

        await hostedService.RunOnceAsync(options.Value, CancellationToken.None);

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
            NullLogger<AutonomyCycleHostedService>.Instance,
            openOrderStore);

        await hostedService.RunOnceAsync(options.Value, CancellationToken.None);

        Assert.NotEmpty(decisionCommitStore.Commits);
        var latestCommit = decisionCommitStore.Commits[^1];
        Assert.NotEqual("UNKNOWN_ORDER_BLOCKED", latestCommit.StrategyReason);
    }

    private static BinanceSpotKline Kline(DateTimeOffset open, decimal close) => new(
        open,
        open.AddMinutes(1).AddMilliseconds(-1),
        close,
        close,
        close,
        close,
        1m,
        "1m");

    private sealed class FakeMarketClient : IBinanceSpotMarketClient
    {
        public Task PingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<BinanceServerTime> GetServerTimeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BinanceServerTime(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        public Task<IReadOnlyList<BinanceSpotKline>> GetKlinesAsync(
            string symbol,
            string interval,
            DateTimeOffset? startTimeUtc = null,
            DateTimeOffset? endTimeUtc = null,
            int limit = 500,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var currentMinute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
            var list = new List<BinanceSpotKline>
            {
                Kline(currentMinute.AddMinutes(-4), 100m),
                Kline(currentMinute.AddMinutes(-3), 101m),
                Kline(currentMinute.AddMinutes(-2), 102m),
                Kline(currentMinute.AddMinutes(-1), 103m),
                Kline(currentMinute, 104m)
            };
            return Task.FromResult<IReadOnlyList<BinanceSpotKline>>(list);
        }

        public Task<IReadOnlyList<BinanceAggregateTrade>> GetAggregateTradesAsync(
            string symbol,
            DateTimeOffset? endTimeUtc = null,
            int limit = 500,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BinanceAggregateTrade>>([]);

        public Task<BinanceBookTicker> GetBookTickerAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BinanceBookTicker(symbol, 100m, 10m, 101m, 10m, DateTimeOffset.UtcNow));

        public Task<BinanceExchangeInfo> GetExchangeInfoAsync(string? symbol = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<BinanceSymbolTradingRules> GetTradingRulesAsync(string symbol, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class FakeAccountClient : IBinanceSpotAccountClient
    {
        public Task<BinanceAccountSnapshot> GetAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BinanceAccountSnapshot(0, 0, 0, 0, true, false, true, 0, "SPOT",
                [new BinanceBalance("BTC", "0", "0"), new BinanceBalance("USDT", "1000", "0")]));

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
}
