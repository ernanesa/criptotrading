#pragma warning disable CA2007 // Tests retain the xUnit synchronization context.
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Domain.Runtime;
using CryptoTrading.Exchange.Binance;
using CryptoTrading.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class ExecutionSubmissionGuardTests
{
    [Theory]
    [InlineData(OperationalMode.SafeMode)]
    [InlineData(OperationalMode.KillSwitch)]
    public async Task OperationalStopBlocksBothGuardsBeforeAnyDependencyCall(OperationalMode mode)
    {
        var control = new InMemoryOperationalControl(new RejectReset());
        var stop = new OperationalControlCommand("test", "test stop", IdempotencyKey.New(), DateTimeOffset.UtcNow);
        if (mode == OperationalMode.KillSwitch) _ = await control.ActivateKillSwitchAsync(stop, cancellationToken: TestContext.Current.CancellationToken);
        else _ = await control.EnterSafeModeAsync(stop, cancellationToken: TestContext.Current.CancellationToken);
        await using var supervisor = new RuntimeSupervisor();
        var guard = Create(control, supervisor);
        var (command, message) = Command(DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => guard.ValidateAsync(command, message, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => guard.ValidateFinalAsync(command, message, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SafeModeAllowsSellOrderWhenRuntimeReconciledAndConnected()
    {
        var control = new InMemoryOperationalControl(new RejectReset());
        var stop = new OperationalControlCommand("test", "safe mode", IdempotencyKey.New(), DateTimeOffset.UtcNow);
        _ = await control.EnterSafeModeAsync(stop, cancellationToken: TestContext.Current.CancellationToken);
        await using var supervisor = new RuntimeSupervisor();
        await supervisor.BeginBootstrappingAsync("test", cancellationToken: TestContext.Current.CancellationToken);
        await supervisor.MarkReconciliationAsync(true, "test", cancellationToken: TestContext.Current.CancellationToken);
        await supervisor.MarkAccountStreamAsync(true, "test", cancellationToken: TestContext.Current.CancellationToken);

        var guard = Create(control, supervisor);
        var (command, message) = SellCommand(DateTimeOffset.UtcNow.AddMinutes(-2)); // Expired so ValidateCoreAsync fails on expiry, not on operational state

        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => guard.ValidateAsync(command, message, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("EXECUTION_EXPIRED_OR_INVALID_TIME", exception.Message); // Proves ValidateOperationalState passed!
    }

    [Fact]
    public async Task KillSwitchBlocksSellOrderEvenWhenRuntimeReconciledAndConnected()
    {
        var control = new InMemoryOperationalControl(new RejectReset());
        var stop = new OperationalControlCommand("test", "emergency stop", IdempotencyKey.New(), DateTimeOffset.UtcNow);
        _ = await control.ActivateKillSwitchAsync(stop, cancellationToken: TestContext.Current.CancellationToken);
        await using var supervisor = new RuntimeSupervisor();
        await supervisor.BeginBootstrappingAsync("test", cancellationToken: TestContext.Current.CancellationToken);
        await supervisor.MarkReconciliationAsync(true, "test", cancellationToken: TestContext.Current.CancellationToken);
        await supervisor.MarkAccountStreamAsync(true, "test", cancellationToken: TestContext.Current.CancellationToken);

        var guard = Create(control, supervisor);
        var (command, message) = SellCommand(DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => guard.ValidateAsync(command, message, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("OPERATIONAL_OR_RUNTIME_BLOCKED", exception.Message);
    }

    [Fact]
    public async Task ExpiredIntentBlocksBeforeMarketOrAccountIo()
    {
        await using var supervisor = new RuntimeSupervisor();
        await supervisor.BeginBootstrappingAsync("test", cancellationToken: TestContext.Current.CancellationToken);
        await supervisor.MarkReconciliationAsync(true, "test", cancellationToken: TestContext.Current.CancellationToken);
        await supervisor.MarkAccountStreamAsync(true, "test", cancellationToken: TestContext.Current.CancellationToken);
        await supervisor.MarkWarmupAsync(true, "test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(supervisor.Snapshot.CanCreateNewExposure);
        var guard = Create(new InMemoryOperationalControl(new RejectReset()), supervisor);
        var (command, message) = Command(DateTimeOffset.UtcNow.AddMinutes(-2));
        await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => guard.ValidateAsync(command, message, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => guard.ValidateFinalAsync(command, message, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static ExecutionSubmissionGuard Create(IOperationalControl control, IRuntimeSupervisor supervisor) =>
        new(control, supervisor, null!, null!, null!, null!, null!, null!,
            Options.Create(new WorkerRuntimeOptions()), NullLogger<ExecutionSubmissionGuard>.Instance);

    [Fact]
    public async Task FinalGuardBlocksAdverseFreshCommission()
    {
        await using var supervisor = await ReadySupervisorAsync(); var account = new GuardAccountClient { CommissionRate = "0.01" };
        var (command, message) = FreshCommand();
        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => ReadyGuard(supervisor, new GuardMarketClient(), account).ValidateFinalAsync(command, message, TestContext.Current.CancellationToken));
        Assert.Equal("FINAL_COMMISSION_RATE_ADVERSE_OR_ELIGIBILITY_UNPROVEN", exception.Message);
    }
    [Fact]
    public async Task FinalGuardBlocksEvenTinyFreshCommissionIncreaseWhenConfiguredToleranceIsHigh()
    {
        await using var supervisor = await ReadySupervisorAsync(); var (command, message) = FreshCommand();
        var account = new GuardAccountClient { CommissionRate = "0.002001" };
        var runtime = new WorkerRuntimeOptions { AutonomyFeeDriftToleranceBps = 10_000m };
        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => ReadyGuard(supervisor, new GuardMarketClient(), account, runtime).ValidateFinalAsync(command, message, TestContext.Current.CancellationToken));
        Assert.Equal("FINAL_COMMISSION_RATE_ADVERSE_OR_ELIGIBILITY_UNPROVEN", exception.Message);
    }
    [Fact]
    public async Task FinalGuardBlocksWhenFreshCommissionQueryFails()
    {
        await using var supervisor = await ReadySupervisorAsync(); var (command, message) = FreshCommand();
        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => ReadyGuard(supervisor, new GuardMarketClient(), new GuardAccountClient { ThrowOnCommission = true }).ValidateFinalAsync(command, message, TestContext.Current.CancellationToken));
        Assert.Equal("FINAL_REVALIDATION_UNAVAILABLE", exception.Message);
    }
    [Fact]
    public async Task FinalGuardBlocksAgedCommissionReceipt()
    {
        await using var supervisor = await ReadySupervisorAsync(); var (command, message) = FreshCommand();
        var account = new GuardAccountClient { CommissionObservedAt = DateTimeOffset.UtcNow.AddMinutes(-16) };
        await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => ReadyGuard(supervisor, new GuardMarketClient(), account).ValidateFinalAsync(command, message, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task FinalGuardBlocksTickerThatExpiresDuringFinalPolicyRead()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var supervisor = await ReadySupervisorAsync(); var (command, message) = FreshCommand(clock);
        var policies = new GuardPolicyStore(clock) { OnRead = read => { if (read > 1) clock.Advance(TimeSpan.FromSeconds(3)); } };
        var market = new GuardMarketClient(clock) { TickerAge = TimeSpan.FromMinutes(14).Add(TimeSpan.FromSeconds(58)) };
        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => ReadyGuard(supervisor, market, new GuardAccountClient(clock), policies: policies, timeProvider: clock).ValidateFinalAsync(command, message, TestContext.Current.CancellationToken));
        Assert.Equal("FINAL_EXECUTION_EXPIRED_OR_LEASE_LOST", exception.Message);
    }

    [Fact]
    public async Task FinalGuardBlocksOtherAssetTickerThatExpiresDuringFinalPolicyRead()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var supervisor = await ReadySupervisorAsync();
        var (command, message) = FreshCommand(clock);
        var policies = new GuardPolicyStore(clock) { OnRead = read => { if (read > 1) clock.Advance(TimeSpan.FromSeconds(3)); } };
        var market = new GuardMarketClient(clock)
        {
            OtherTicker = new BinanceBookTicker("ETHUSDT", 100m, 10m, 101m, 10m, clock.GetUtcNow().AddMinutes(-15).AddSeconds(2))
        };
        var account = new GuardAccountClient(clock) { ExtraBalances = [new BinanceBalance("ETH", "1", "0")] };
        var runtime = new WorkerRuntimeOptions { AutonomySymbols = "BTCUSDT,ETHUSDT" };

        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() =>
            ReadyGuard(supervisor, market, account, runtime, policies, clock).ValidateFinalAsync(command, message, TestContext.Current.CancellationToken));

        Assert.Equal("FINAL_EXECUTION_EXPIRED_OR_LEASE_LOST", exception.Message);
    }
    [Fact]
    public async Task FinalGuardBlocksAdverseAccountPosition()
    {
        await using var supervisor = await ReadySupervisorAsync(); var (command, message) = FreshCommand();
        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => ReadyGuard(supervisor, new GuardMarketClient(), new GuardAccountClient { BitcoinFree = "100" }).ValidateFinalAsync(command, message, TestContext.Current.CancellationToken));
        Assert.Equal("PRE_SUBMIT_ACCOUNTING_OR_LOSS_BREAKER", exception.Message);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuardRejectsUnvaluedBnbEvenInLargeTestnetWallet(bool testnet)
    {
        await using var supervisor = await ReadySupervisorAsync();
        var (command, message) = FreshCommand();
        var account = new GuardAccountClient { ExtraBalances = [new BinanceBalance("BNB", "1", "0"), .. Enumerable.Range(0, 20).Select(i => new BinanceBalance($"OTHER{i}", "0", "0"))] };
        var runtime = new WorkerRuntimeOptions { ExecutionMode = testnet ? "TESTNET" : "PAPER" };

        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() =>
            ReadyGuard(supervisor, new GuardMarketClient(), account, runtime).ValidateAsync(command, message, TestContext.Current.CancellationToken));

        Assert.Equal("PRE_SUBMIT_ACCOUNTING_OR_LOSS_BREAKER", exception.Message);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("crossed")]
    [InlineData("wrong-symbol")]
    public async Task GuardRejectsInvalidTickerForOtherPortfolioAsset(string failure)
    {
        await using var supervisor = await ReadySupervisorAsync();
        var (command, message) = FreshCommand();
        var now = DateTimeOffset.UtcNow;
        var otherTicker = new BinanceBookTicker(
            failure == "wrong-symbol" ? "SOLUSDT" : "ETHUSDT", 100m, 10m,
            failure == "crossed" ? 99m : 101m, 10m,
            failure == "stale" ? now.AddHours(-1) : failure == "future" ? now.AddHours(1) : now);
        var market = new GuardMarketClient { OtherTicker = otherTicker };
        var account = new GuardAccountClient { ExtraBalances = [new BinanceBalance("ETH", "1", "0")] };
        var runtime = new WorkerRuntimeOptions { AutonomySymbols = "BTCUSDT,ETHUSDT" };

        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() =>
            ReadyGuard(supervisor, market, account, runtime).ValidateAsync(command, message, TestContext.Current.CancellationToken));

        Assert.Equal("PRE_SUBMIT_MARKET_DATA_UNAVAILABLE", exception.Message);
    }
    [Fact]
    public async Task FinalGuardBlocksAdverseFreshMarketPrice()
    {
        await using var supervisor = await ReadySupervisorAsync(); var (command, message) = FreshCommand();
        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => ReadyGuard(supervisor, new GuardMarketClient { AskPrice = 20000m }, new GuardAccountClient()).ValidateFinalAsync(command, message, TestContext.Current.CancellationToken));
        Assert.Equal("PRE_SUBMIT_MARKET_OR_NOTIONAL_CHANGED", exception.Message);
    }
    [Fact]
    public async Task FinalGuardAcceptsConservativeCommissionAndRefetchesMarket()
    {
        await using var supervisor = await ReadySupervisorAsync(); var market = new GuardMarketClient(); var (command, message) = FreshCommand();
        var guard = ReadyGuard(supervisor, market, new GuardAccountClient());
        await guard.ValidateAsync(command, message, TestContext.Current.CancellationToken);
        await guard.ValidateFinalAsync(command, message, TestContext.Current.CancellationToken);
        Assert.Equal(2, market.BookTickerCalls);
    }
    [Fact]
    public async Task FinalGuardAcceptsPlannedMaximumMakerRateWhenItExceedsTakerRate()
    {
        await using var supervisor = await ReadySupervisorAsync(); var market = new GuardMarketClient();
        var account = new GuardAccountClient { CommissionRate = "0", MakerRate = "0.004", TakerRate = "0.002" };
        var (original, message) = FreshCommand(); var command = original with { EstimatedCommissionRate = .004m };
        await ReadyGuard(supervisor, market, account).ValidateFinalAsync(command, message, TestContext.Current.CancellationToken);
    }
    [Fact]
    public async Task FinalGuardBlocksWhenKlinesFailQualityGate()
    {
        await using var supervisor = await ReadySupervisorAsync();
        var (command, message) = FreshCommand();
        var market = new GuardMarketClient { ReturnInvalidKlines = true };
        var guard = ReadyGuard(supervisor, market, new GuardAccountClient());
        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => guard.ValidateFinalAsync(command, message, TestContext.Current.CancellationToken));
        Assert.StartsWith("MARKET_DATA_INVALID:", exception.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }
    [Fact]
    public async Task FinalGuardBlocksWhenFreshPolicyIsRevoked()
    {
        await using var supervisor = await ReadySupervisorAsync(); var (command, message) = FreshCommand();
        var policies = new GuardPolicyStore { ReturnNullAfterReads = 1 };
        var guard = ReadyGuard(supervisor, new GuardMarketClient(), new GuardAccountClient(), policies: policies);
        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => guard.ValidateFinalAsync(command, message, TestContext.Current.CancellationToken));
        Assert.Equal("FINAL_RISK_POLICY_NOT_CURRENT", exception.Message);
    }
    [Fact]
    public async Task FinalGuardBlocksWhenFreshPolicyDocumentChanges()
    {
        await using var supervisor = await ReadySupervisorAsync(); var (command, message) = FreshCommand();
        var policies = new GuardPolicyStore { ReturnChangedAfterReads = 1 };
        var guard = ReadyGuard(supervisor, new GuardMarketClient(), new GuardAccountClient(), policies: policies);
        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => guard.ValidateFinalAsync(command, message, TestContext.Current.CancellationToken));
        Assert.Equal("FINAL_RISK_POLICY_NOT_CURRENT", exception.Message);
    }
    [Fact]
    public async Task FinalGuardBlocksWhenOnlyLockedQuoteBalanceCoversBuy()
    {
        await using var supervisor = await ReadySupervisorAsync(); var (command, message) = FreshCommand();
        var account = new GuardAccountClient { QuoteFree = "0", QuoteLocked = "1000" };
        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => ReadyGuard(supervisor, new GuardMarketClient(), account).ValidateFinalAsync(command, message, TestContext.Current.CancellationToken));
        Assert.Equal("FINAL_INSUFFICIENT_FREE_QUOTE_BALANCE", exception.Message);
    }
    [Fact]
    public async Task FinalGuardBlocksWhenFreeQuoteBalanceDropsAfterInitialValidation()
    {
        await using var supervisor = await ReadySupervisorAsync(); var (command, message) = FreshCommand();
        var account = new GuardAccountClient { QuoteFreeAfterFirstRead = "1" };
        var guard = ReadyGuard(supervisor, new GuardMarketClient(), account);
        await guard.ValidateAsync(command, message, TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => guard.ValidateFinalAsync(command, message, TestContext.Current.CancellationToken));
        Assert.Equal("FINAL_INSUFFICIENT_FREE_QUOTE_BALANCE", exception.Message);
    }
    [Fact]
    public async Task FinalGuardBlocksWhenBasePositionGrowsAfterNormalPreflight()
    {
        await using var supervisor = await ReadySupervisorAsync(); var (command, message) = FreshCommand();
        var account = new GuardAccountClient { BitcoinFreeAfterFirstRead = "100" };
        var guard = ReadyGuard(supervisor, new GuardMarketClient(), account);
        await guard.ValidateAsync(command, message, TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => guard.ValidateFinalAsync(command, message, TestContext.Current.CancellationToken));
        Assert.Equal("PRE_SUBMIT_ACCOUNTING_OR_LOSS_BREAKER", exception.Message);
    }
    [Fact]
    public async Task FinalGuardBlocksSellWhenOnlyLockedBaseBalanceCoversQuantity()
    {
        await using var supervisor = await ReadySupervisorAsync(); var (buy, message) = FreshCommand();
        var command = buy with { Side = OrderSide.Sell };
        var account = new GuardAccountClient { BitcoinFree = "0", BitcoinLocked = "1" };
        var riskContexts = new GuardRiskContextStore(defaultPositionQuantity: 1m);
        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => ReadyGuard(supervisor, new GuardMarketClient(), account, riskContexts: riskContexts).ValidateFinalAsync(command, message, TestContext.Current.CancellationToken));
        Assert.Equal("FINAL_INSUFFICIENT_FREE_BASE_BALANCE", exception.Message);
    }
    [Fact]
    public async Task GuardBlocksSellWhenTrackedInventoryIsInsufficient()
    {
        await using var supervisor = await ReadySupervisorAsync();
        var (buy, message) = FreshCommand();
        var command = buy with { Side = OrderSide.Sell };
        var account = new GuardAccountClient { BitcoinFree = "1" };
        var riskContexts = new GuardRiskContextStore(defaultPositionQuantity: 0m, hasPositionBasis: true);
        var guard = ReadyGuard(supervisor, new GuardMarketClient(), account, riskContexts: riskContexts);
        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() =>
            guard.ValidateAsync(command, message, TestContext.Current.CancellationToken));
        Assert.Equal("PRE_SUBMIT_INSUFFICIENT_TRACKED_INVENTORY", exception.Message);
    }
    [Fact]
    public async Task GuardBlocksSellWhenTrackedPositionHasNoBasis()
    {
        await using var supervisor = await ReadySupervisorAsync();
        var (buy, message) = FreshCommand();
        var command = buy with { Side = OrderSide.Sell };
        var account = new GuardAccountClient { BitcoinFree = "1" };
        var riskContexts = new GuardRiskContextStore(defaultPositionQuantity: 1m, hasPositionBasis: false);
        var guard = ReadyGuard(supervisor, new GuardMarketClient(), account, riskContexts: riskContexts);
        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() =>
            guard.ValidateAsync(command, message, TestContext.Current.CancellationToken));
        Assert.Equal("PRE_SUBMIT_INSUFFICIENT_TRACKED_INVENTORY", exception.Message);
    }
    [Fact]
    public async Task GuardAcceptsSellWhenTrackedInventoryAndFreeBalanceAreSufficient()
    {
        await using var supervisor = await ReadySupervisorAsync();
        var (buy, message) = FreshCommand();
        var command = buy with { Side = OrderSide.Sell };
        var account = new GuardAccountClient { BitcoinFree = "1" };
        var riskContexts = new GuardRiskContextStore(defaultPositionQuantity: 1m, hasPositionBasis: true);
        var guard = ReadyGuard(supervisor, new GuardMarketClient(), account, riskContexts: riskContexts);

        await guard.ValidateAsync(command, message, TestContext.Current.CancellationToken);
        await guard.ValidateFinalAsync(command, message, TestContext.Current.CancellationToken);
    }
    private static async Task<RuntimeSupervisor> ReadySupervisorAsync()
    {
        var supervisor = new RuntimeSupervisor(); var cancellationToken = TestContext.Current.CancellationToken;
        await supervisor.BeginBootstrappingAsync("test", cancellationToken); await supervisor.MarkReconciliationAsync(true, "test", cancellationToken);
        await supervisor.MarkAccountStreamAsync(true, "test", cancellationToken); await supervisor.MarkWarmupAsync(true, "test", cancellationToken);
        return supervisor;
    }
    private static ExecutionSubmissionGuard ReadyGuard(
        RuntimeSupervisor supervisor,
        GuardMarketClient market,
        GuardAccountClient account,
        WorkerRuntimeOptions? runtime = null,
        GuardPolicyStore? policies = null,
        TimeProvider? timeProvider = null,
        GuardRiskContextStore? riskContexts = null) =>
        new(new InMemoryOperationalControl(new RejectReset()), supervisor, policies ?? new GuardPolicyStore(timeProvider), riskContexts ?? new GuardRiskContextStore(), new GuardOpenOrderStore(), new GuardSubmissionStore(), market, account, Options.Create(runtime ?? new WorkerRuntimeOptions()), NullLogger<ExecutionSubmissionGuard>.Instance, timeProvider);
    private static (SubmitOrderCommand Command, LeasedOutboxMessage Message) FreshCommand(TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow(); var correlation = CorrelationId.New(); var idempotency = IdempotencyKey.New();
        var command = new SubmitOrderCommand(OrderId.New(), ClientOrderId.New(), new Symbol("BTCUSDT"), OrderSide.Buy, OrderType.Market, new Quantity(.01m), new Price(100m), 100m, correlation, idempotency, "v1", now, .002m, "testhash", "test:commission", now, now.AddMinutes(1));
        return (command, new LeasedOutboxMessage(Guid.NewGuid(), OrderExecutionPipeline.SubmitOrderMessageType, correlation, idempotency, "{}", now, 1, now.AddMinutes(1), "test"));
    }
    private static BinanceSpotKline Kline(DateTimeOffset open, decimal close) =>
     new(open, open.AddMinutes(1).AddMilliseconds(-1), close, close, close, close, 1m, "1m");
    private sealed class GuardMarketClient(TimeProvider? timeProvider = null) : IBinanceSpotMarketClient
    {
        private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

        public int BookTickerCalls { get; private set; }
        public decimal AskPrice { get; init; } = 101m;
        public TimeSpan TickerAge { get; init; }
        public BinanceBookTicker? OtherTicker { get; init; }
        public bool ReturnInvalidKlines { get; init; }
        public Task PingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<BinanceServerTime> GetServerTimeAsync(CancellationToken cancellationToken = default) => Task.FromResult(new BinanceServerTime(timeProvider.GetUtcNow().ToUnixTimeMilliseconds()));
        public Task<IReadOnlyList<BinanceSpotKline>> GetKlinesAsync(string symbol, string interval, DateTimeOffset? startTimeUtc = null, DateTimeOffset? endTimeUtc = null, int limit = 500, CancellationToken cancellationToken = default)
        {
            var now = timeProvider.GetUtcNow(); var minute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
            if (ReturnInvalidKlines)
            {
                return Task.FromResult<IReadOnlyList<BinanceSpotKline>>([Kline(minute.AddMinutes(-4), 100m), Kline(minute.AddMinutes(-4), 101m)]);
            }
            return Task.FromResult<IReadOnlyList<BinanceSpotKline>>([Kline(minute.AddMinutes(-4), 100m), Kline(minute.AddMinutes(-3), 101m), Kline(minute.AddMinutes(-2), 102m), Kline(minute.AddMinutes(-1), 103m), Kline(minute, 104m)]);
        }
        public Task<IReadOnlyList<BinanceAggregateTrade>> GetAggregateTradesAsync(string symbol, DateTimeOffset? endTimeUtc = null, int limit = 500, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<BinanceAggregateTrade>>([]);
        public Task<BinanceBookTicker> GetBookTickerAsync(string symbol, CancellationToken cancellationToken = default) { BookTickerCalls++; return Task.FromResult(symbol == "ETHUSDT" && OtherTicker is not null ? OtherTicker : new BinanceBookTicker(symbol, 100m, 10m, AskPrice, 10m, timeProvider.GetUtcNow() - TickerAge)); }
        public Task<BinanceExchangeInfo> GetExchangeInfoAsync(string? symbol = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BinanceSymbolTradingRules> GetTradingRulesAsync(string symbol, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
    private sealed class GuardAccountClient(TimeProvider? timeProvider = null) : IBinanceSpotAccountClient
    {
        private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

        public string CommissionRate { get; init; } = "0.001";
        public string? MakerRate { get; init; }
        public string? TakerRate { get; init; }
        public string BitcoinFree { get; init; } = "0";
        public string BitcoinLocked { get; init; } = "0";
        public string? BitcoinFreeAfterFirstRead { get; init; }
        public string QuoteFree { get; init; } = "1000";
        public string QuoteLocked { get; init; } = "0";
        public string? QuoteFreeAfterFirstRead { get; init; }
        public IReadOnlyList<BinanceBalance> ExtraBalances { get; init; } = [];
        public bool ThrowOnCommission { get; init; }
        public DateTimeOffset? CommissionObservedAt { get; init; }
        private int accountReads;
        public Task<BinanceAccountSnapshot> GetAccountAsync(CancellationToken cancellationToken = default)
        {
            var isLaterRead = Interlocked.Increment(ref accountReads) > 1;
            var quoteFree = isLaterRead ? QuoteFreeAfterFirstRead ?? QuoteFree : QuoteFree;
            var bitcoinFree = isLaterRead ? BitcoinFreeAfterFirstRead ?? BitcoinFree : BitcoinFree;
            return Task.FromResult(new BinanceAccountSnapshot(0, 0, 0, 0, true, false, true, 0, "SPOT", [new BinanceBalance("BTC", bitcoinFree, BitcoinLocked), new BinanceBalance("USDT", quoteFree, QuoteLocked), .. ExtraBalances]));
        }
        public Task<BinanceCommissionSnapshot> GetCommissionAsync(string symbol, CancellationToken cancellationToken = default)
        {
            if (ThrowOnCommission) throw new InvalidOperationException("query failed");
            return Task.FromResult(new BinanceCommissionSnapshot(new BinanceCommissionResponse(symbol, new BinanceCommissionRateSet(MakerRate ?? CommissionRate, TakerRate ?? CommissionRate, CommissionRate, CommissionRate), new BinanceCommissionRateSet("0", "0", "0", "0"), new BinanceCommissionRateSet("0", "0", "0", "0"), new BinanceCommissionDiscount(false, false, null, null)), CommissionObservedAt ?? timeProvider.GetUtcNow()));
        }
        public Task<IReadOnlyList<BinanceOpenOrder>> GetOpenOrdersAsync(string? symbol = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<BinanceOpenOrder>>([]);
        public Task<IReadOnlyList<BinanceTrade>> GetMyTradesAsync(string symbol, BinanceTradeQuery? query = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<BinanceTrade>>([]);
        public Task<BinanceClockSnapshot> SynchronizeClockAsync(CancellationToken cancellationToken = default) => Task.FromResult(new BinanceClockSnapshot(timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), TimeSpan.Zero, true));
    }
    private sealed class GuardPolicyStore : IRiskPolicyStore
    {
        private readonly TimeProvider timeProvider;
        private readonly RiskPolicyDocument document;
        private readonly RiskPolicyDocument changedDocument;
        private int reads;
        public int? ReturnNullAfterReads { get; init; }
        public int? ReturnChangedAfterReads { get; init; }
        public Action<int>? OnRead { get; init; }
        public GuardPolicyStore(TimeProvider? timeProvider = null, TimeSpan? maxMarketDataAge = null)
        {
            this.timeProvider = timeProvider ?? TimeProvider.System;
            var policy = new RiskPolicy("v1", 1000m, 5000m, 500m, 1000m, maxMarketDataAge ?? TimeSpan.FromMinutes(15), withdrawalsEnabled: false);
            document = RiskPolicyDocument.CreateDraft(policy, this.timeProvider.GetUtcNow().AddDays(-1), this.timeProvider.GetUtcNow().AddDays(10), "creator").Approve("app", "approver", this.timeProvider.GetUtcNow().AddDays(-1)).Activate(this.timeProvider.GetUtcNow().AddDays(-1));
            changedDocument = RiskPolicyDocument.CreateDraft(policy, this.timeProvider.GetUtcNow().AddDays(-2), this.timeProvider.GetUtcNow().AddDays(10), "creator").Approve("app", "approver", this.timeProvider.GetUtcNow().AddDays(-2)).Activate(this.timeProvider.GetUtcNow().AddDays(-2));
        }
        public Task<RiskPolicyDocument?> GetActiveRiskPolicyAsync(DateTimeOffset atUtc, CancellationToken cancellationToken = default)
        {
            var read = Interlocked.Increment(ref reads);
            OnRead?.Invoke(read);
            return Task.FromResult<RiskPolicyDocument?>(ReturnNullAfterReads is { } maximum && read > maximum ? null : ReturnChangedAfterReads is { } changedAfter && read > changedAfter ? changedDocument : document);
        }
        public Task SaveRiskPolicyAsync(RiskPolicyDocument document, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<RiskPolicyDocument> ApproveRiskPolicyAsync(string policyVersion, string approvalId, string approver, DateTimeOffset approvedAtUtc, CancellationToken cancellationToken = default) => Task.FromResult(document);
        public Task<RiskPolicyDocument> ActivateRiskPolicyAsync(string policyVersion, DateTimeOffset activatedAtUtc, CancellationToken cancellationToken = default) => Task.FromResult(document);
    }
    private sealed class GuardRiskContextStore(decimal defaultPositionQuantity = 0m, bool hasPositionBasis = true) : IAutonomyRiskContextStore
    {
        public decimal PositionQuantity { get; set; } = defaultPositionQuantity;
        public bool HasPositionBasis { get; set; } = hasPositionBasis;
        public Task<AutonomyRiskContextSnapshot> ReadAutonomyRiskContextAsync(Symbol symbol, Asset quoteAsset, DateTimeOffset dayStartUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AutonomyRiskContextSnapshot(0m, 0m, PositionQuantity, HasPositionBasis, DateTimeOffset.UtcNow, 1000m, 1000m, 0m, true, true));
        public Task<AutonomyRiskContextSnapshot> RecordAutonomyRiskContextAsync(string scope, Symbol symbol, Asset quoteAsset, DateTimeOffset dayStartUtc, decimal? currentEquityNotional, DateTimeOffset valuationAtUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AutonomyRiskContextSnapshot(0m, 0m, PositionQuantity, HasPositionBasis, valuationAtUtc, currentEquityNotional, 1000m, 0m, true, currentEquityNotional.HasValue));
    }
    private sealed class GuardOpenOrderStore : ILocalOpenOrderStore
    {
        public Task<IReadOnlyList<LocalOpenOrderRecord>> ReadOpenOrdersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LocalOpenOrderRecord>>([]);
        public Task UpsertOpenOrderAsync(LocalOpenOrderRecord order, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveOpenOrderAsync(string clientOrderId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class GuardSubmissionStore : IDurableSubmissionStore
    {
        public Task<bool> HasSubmissionStartedAsync(LeasedOutboxMessage message, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> TryBeginSubmissionAsync(LeasedOutboxMessage message, TimeSpan minimumLeaseRemaining, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ValidateOutboxLeaseAsync(LeasedOutboxMessage message, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ValidateOutboxLeaseAsync(LeasedOutboxMessage message, TimeSpan minimumLeaseRemaining, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
    private sealed class RejectReset : IOperationalControlResetAuthorizer
    {
        public bool IsAuthorized(OperationalControlSnapshot current, OperationalControlCommand command,
            OperationalControlResetAuthorization authorization) => false;
    }

    private sealed class ManualTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset current = current;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan amount) => current += amount;
    }

    private static (SubmitOrderCommand Command, LeasedOutboxMessage Message) Command(DateTimeOffset created)
    {
        var correlation = CorrelationId.New();
        var idempotency = IdempotencyKey.New();
        var command = new SubmitOrderCommand(OrderId.New(), ClientOrderId.New(), new Symbol("BTCUSDT"),
            OrderSide.Buy, OrderType.Market, new Quantity(0.01m), new Price(10000m), 100m,
            correlation, idempotency, "risk-v1", created);
        return (command, new LeasedOutboxMessage(Guid.NewGuid(), OrderExecutionPipeline.SubmitOrderMessageType,
            correlation, idempotency, "{}", created, 1, created.AddMinutes(1), "test"));
    }

    private static (SubmitOrderCommand Command, LeasedOutboxMessage Message) SellCommand(DateTimeOffset created)
    {
        var correlation = CorrelationId.New();
        var idempotency = IdempotencyKey.New();
        var command = new SubmitOrderCommand(OrderId.New(), ClientOrderId.New(), new Symbol("BTCUSDT"),
            OrderSide.Sell, OrderType.Market, new Quantity(0.01m), new Price(10000m), 100m,
            correlation, idempotency, "risk-v1", created);
        return (command, new LeasedOutboxMessage(Guid.NewGuid(), OrderExecutionPipeline.SubmitOrderMessageType,
            correlation, idempotency, "{}", created, 1, created.AddMinutes(1), "test"));
    }
}
