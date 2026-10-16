#pragma warning disable CA2007

using CryptoTrading.Domain.Accounting;
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

public sealed class ExecutionSubmissionGuardBudgetTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DisallowedSymbolBlocksWhenBudgetPolicyIsConfigured()
    {
        await using var supervisor = await CreateReadySupervisorAsync();
        var guard = CreateGuard(supervisor, new BudgetPolicy(allowedSymbol: "BTCUSDT"));
        var (command, message) = CreateCommand("ETHUSDT", 10m);

        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() =>
            guard.ValidateAsync(command, message, TestContext.Current.CancellationToken));

        Assert.Equal(RiskReasonCodes.DisallowedSymbol, exception.Message);
    }

    [Fact]
    public async Task MultiAssetBudgetPolicyAuthorizesConfiguredSymbols()
    {
        await using var supervisor = await CreateReadySupervisorAsync();
        var guard = CreateGuard(supervisor, new BudgetPolicy(allowedSymbol: "BTCUSDT,ETHUSDT,SOLUSDT,BNBUSDT"));
        var (command, message) = CreateCommand("ETHUSDT", 10m);

        // Must not throw ExecutionDispatchBlockedException:
        await guard.ValidateAsync(command, message, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExistingPositionInOtherUniverseAssetEnforcesMaxOpenPositions()
    {
        await using var supervisor = await CreateReadySupervisorAsync();
        var account = new TestAccountClient { BtcFree = "0.00019" }; // Holding BTC position
        var runtimeOptions = new WorkerRuntimeOptions
        {
            ExecutionMode = "PAPER",
            AutonomySymbols = "BTCUSDT,ETHUSDT"
        };
        var guard = CreateGuard(
            supervisor,
            new BudgetPolicy(allowedSymbol: "BTCUSDT,ETHUSDT", maxOpenPositions: 1),
            account: account,
            runtimeOptions: runtimeOptions);
        var (command, message) = CreateCommand("ETHUSDT", 10m);

        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() =>
            guard.ValidateAsync(command, message, TestContext.Current.CancellationToken));

        Assert.Equal(RiskReasonCodes.MaxOpenPositionsExceeded, exception.Message);
    }

    [Fact]
    public async Task OtherUniverseAssetTickerFailureFailsClosed()
    {
        await using var supervisor = await CreateReadySupervisorAsync();
        var account = new TestAccountClient { BtcFree = "0.00019" }; // Holding BTC position
        var runtimeOptions = new WorkerRuntimeOptions
        {
            ExecutionMode = "PAPER",
            AutonomySymbols = "BTCUSDT,ETHUSDT"
        };
        var market = new TestMarketClient
        {
            FailTickerForSymbol = s => string.Equals(s, "BTCUSDT", StringComparison.OrdinalIgnoreCase)
        };
        var guard = CreateGuard(
            supervisor,
            new BudgetPolicy(allowedSymbol: "BTCUSDT,ETHUSDT", maxOpenPositions: 2),
            market: market,
            account: account,
            runtimeOptions: runtimeOptions);
        var (command, message) = CreateCommand("ETHUSDT", 10m);

        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() =>
            guard.ValidateAsync(command, message, TestContext.Current.CancellationToken));

        Assert.Equal("PRE_SUBMIT_MARKET_DATA_UNAVAILABLE", exception.Message);
    }

    [Fact]
    public async Task StaleFxQuoteBlocksPreValidation()
    {
        await using var supervisor = await CreateReadySupervisorAsync();
        var market = new TestMarketClient { TickerAge = TimeSpan.FromSeconds(65) };
        var guard = CreateGuard(supervisor, new BudgetPolicy(), market: market);
        var (command, message) = CreateCommand("BTCUSDT", 10m);

        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() =>
            guard.ValidateAsync(command, message, TestContext.Current.CancellationToken));

        Assert.Equal(RiskReasonCodes.FxQuoteStale, exception.Message);
    }

    [Fact]
    public async Task NotionalExceedingEffectiveBudgetCeilingBlocksSubmission()
    {
        await using var supervisor = await CreateReadySupervisorAsync();
        // Ceiling is <= 20 USDT. Requesting 30 USDT must fail.
        var guard = CreateGuard(supervisor, new BudgetPolicy());
        var (command, message) = CreateCommand("BTCUSDT", 30m);

        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() =>
            guard.ValidateAsync(command, message, TestContext.Current.CancellationToken));

        Assert.Equal(RiskReasonCodes.BudgetCeilingExceeded, exception.Message);
    }

    [Fact]
    public async Task ExistingOpenPositionBlocksNewBuyOrderUnderSinglePositionPolicy()
    {
        await using var supervisor = await CreateReadySupervisorAsync();
        var account = new TestAccountClient { BtcFree = "0.001" }; // position exists
        var guard = CreateGuard(supervisor, new BudgetPolicy(maxOpenPositions: 1), account: account);
        var (command, message) = CreateCommand("BTCUSDT", 10m);

        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() =>
            guard.ValidateAsync(command, message, TestContext.Current.CancellationToken));

        Assert.Equal(RiskReasonCodes.MaxOpenPositionsExceeded, exception.Message);
    }

    [Fact]
    public async Task ActiveLockedUnknownCapitalBlocksNewOrderSubmission()
    {
        await using var supervisor = await CreateReadySupervisorAsync();
        var reservations = new InMemoryTradingStore();
        // Seed a LockedUnknown reservation
        await reservations.ReserveCapitalAsync(new CapitalReservationRequest(
            reservationId: "res-seed",
            accountId: "test-reconciliation-account",
            symbol: "BTCUSDT",
            clientOrderId: "seed-order",
            asset: "USDT",
            requestedAmount: 5m,
            effectiveBudgetCeilingAmount: 20m,
            openPositionsCommittedAmount: 0m,
            realizedLossesAmount: 0m,
            workerInstanceId: "w1",
            fencingToken: 1L,
            occurredAtUtc: NowUtc), TestContext.Current.CancellationToken);

        await reservations.MarkReservationLockedUnknownAsync(
            "seed-order", "w1", 2L, NowUtc, "TESTNET_TIMEOUT", TestContext.Current.CancellationToken);

        var guard = CreateGuard(supervisor, new BudgetPolicy(), reservations: reservations);
        var (command, message) = CreateCommand("BTCUSDT", 10m);

        var exception = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() =>
            guard.ValidateAsync(command, message, TestContext.Current.CancellationToken));

        Assert.Equal(RiskReasonCodes.LockedUnknownCapitalActive, exception.Message);
    }

    [Fact]
    public async Task ValidOrderWithinBudgetApprovesAndRecordsDurableReservationOnValidateFinal()
    {
        await using var supervisor = await CreateReadySupervisorAsync();
        var reservations = new InMemoryTradingStore();
        var guard = CreateGuard(supervisor, new BudgetPolicy(), reservations: reservations);
        var (command, message) = CreateCommand("BTCUSDT", 10m);

        // Pre-validation succeeds
        await guard.ValidateAsync(command, message, TestContext.Current.CancellationToken);

        // Final validation succeeds and commits reservation
        await guard.ValidateFinalAsync(command, message, TestContext.Current.CancellationToken);

        var active = await reservations.GetActiveReservationsAsync("test-reconciliation-account", TestContext.Current.CancellationToken);
        var orderReservation = Assert.Single(active);
        Assert.Equal(command.ClientOrderId.Value, orderReservation.ClientOrderId);
        Assert.True(orderReservation.ReservedAmount > 10m); // includes fee buffer
    }

    private static ExecutionSubmissionGuard CreateGuard(
        IRuntimeSupervisor supervisor,
        BudgetPolicy budgetPolicy,
        TestMarketClient? market = null,
        TestAccountClient? account = null,
        ICapitalReservationStore? reservations = null,
        WorkerRuntimeOptions? runtimeOptions = null)
    {
        var policyStore = new TestPolicyStore(budgetPolicy);
        var control = new InMemoryOperationalControl(new AllowResetAuthorizer());
        var options = Options.Create(runtimeOptions ?? new WorkerRuntimeOptions
        {
            ReconciliationAccount = "test-reconciliation-account",
        });

        var accountClient = account ?? new TestAccountClient();
        var positionQuantity = decimal.Parse(accountClient.BtcFree, System.Globalization.CultureInfo.InvariantCulture);

        return new ExecutionSubmissionGuard(
            control,
            supervisor,
            policyStore,
            new TestRiskContextStore { PositionQuantity = positionQuantity },
            new TestOpenOrderStore(),
            new TestSubmissionStore(),
            market ?? new TestMarketClient(),
            accountClient,
            options,
            NullLogger<ExecutionSubmissionGuard>.Instance,
            new FixedTimeProvider(NowUtc),
            capitalReservations: reservations);
    }

    private static async Task<RuntimeSupervisor> CreateReadySupervisorAsync()
    {
        var supervisor = new RuntimeSupervisor();
        var token = TestContext.Current.CancellationToken;
        await supervisor.BeginBootstrappingAsync("test", token);
        await supervisor.MarkReconciliationAsync(true, "test", token);
        await supervisor.MarkAccountStreamAsync(true, "test", token);
        await supervisor.MarkWarmupAsync(true, "test", token);
        return supervisor;
    }

    private static (SubmitOrderCommand Command, LeasedOutboxMessage Message) CreateCommand(string symbol, decimal notional)
    {
        var correlation = CorrelationId.New();
        var idempotency = IdempotencyKey.New();
        var clientOrderId = ClientOrderId.New();
        var price = 100m;
        var quantity = notional / price;

        var command = new SubmitOrderCommand(
            OrderId.New(),
            clientOrderId,
            new Symbol(symbol),
            OrderSide.Buy,
            OrderType.Limit,
            new Quantity(quantity),
            new Price(price),
            notional,
            correlation,
            idempotency,
            "v1",
            NowUtc,
            0.002m,
            "hash-test",
            "commission-test",
            NowUtc,
            NowUtc.AddMinutes(1));

        var message = new LeasedOutboxMessage(
            Guid.NewGuid(),
            OrderExecutionPipeline.SubmitOrderMessageType,
            correlation,
            idempotency,
            "{}",
            NowUtc,
            1,
            NowUtc.AddMinutes(1),
            "test-worker");

        return (command, message);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class AllowResetAuthorizer : IOperationalControlResetAuthorizer
    {
        public bool IsAuthorized(
            OperationalControlSnapshot current,
            OperationalControlCommand resetCommand,
            OperationalControlResetAuthorization authorization) => true;
    }

    private sealed class TestMarketClient : IBinanceSpotMarketClient
    {
        public TimeSpan TickerAge { get; init; } = TimeSpan.Zero;
        public decimal AskPrice { get; init; } = 100m;
        public Func<string, bool>? FailTickerForSymbol { get; init; }

        public Task PingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<BinanceServerTime> GetServerTimeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BinanceServerTime(NowUtc.ToUnixTimeMilliseconds()));

        public Task<IReadOnlyList<BinanceSpotKline>> GetKlinesAsync(
            string symbol, string interval, DateTimeOffset? startTimeUtc = null, DateTimeOffset? endTimeUtc = null, int limit = 500, CancellationToken cancellationToken = default)
        {
            var minute = new DateTimeOffset(NowUtc.Year, NowUtc.Month, NowUtc.Day, NowUtc.Hour, NowUtc.Minute, 0, TimeSpan.Zero);
            return Task.FromResult<IReadOnlyList<BinanceSpotKline>>([
                new BinanceSpotKline(minute.AddMinutes(-4), minute.AddMinutes(-3), 100m, 100m, 100m, 100m, 1m, "1m"),
                new BinanceSpotKline(minute.AddMinutes(-3), minute.AddMinutes(-2), 100m, 100m, 100m, 100m, 1m, "1m"),
                new BinanceSpotKline(minute.AddMinutes(-2), minute.AddMinutes(-1), 100m, 100m, 100m, 100m, 1m, "1m"),
                new BinanceSpotKline(minute.AddMinutes(-1), minute, 100m, 100m, 100m, 100m, 1m, "1m"),
            ]);
        }

        public Task<IReadOnlyList<BinanceAggregateTrade>> GetAggregateTradesAsync(string symbol, DateTimeOffset? endTimeUtc = null, int limit = 500, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BinanceAggregateTrade>>([]);

        public Task<BinanceBookTicker> GetBookTickerAsync(string symbol, CancellationToken cancellationToken = default)
        {
            if (FailTickerForSymbol?.Invoke(symbol) == true)
            {
                throw new HttpRequestException("Simulated exchange ticker failure.");
            }

            if (string.Equals(symbol, "USDTBRL", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new BinanceBookTicker(symbol, 5.50m, 100m, 5.50m, 100m, NowUtc - TickerAge));
            }

            return Task.FromResult(new BinanceBookTicker(symbol, 100m, 10m, AskPrice, 10m, NowUtc - TickerAge));
        }

        public Task<BinanceExchangeInfo> GetExchangeInfoAsync(string? symbol = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BinanceSymbolTradingRules> GetTradingRulesAsync(string symbol, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class TestAccountClient : IBinanceSpotAccountClient
    {
        public string BtcFree { get; init; } = "0";
        public string UsdtFree { get; init; } = "1000";

        public Task<BinanceAccountSnapshot> GetAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BinanceAccountSnapshot(0, 0, 0, 0, true, false, true, 0, "SPOT", [
                new BinanceBalance("BTC", BtcFree, "0"),
                new BinanceBalance("USDT", UsdtFree, "0"),
            ]));

        public Task<BinanceCommissionSnapshot> GetCommissionAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BinanceCommissionSnapshot(
                new BinanceCommissionResponse(
                    symbol,
                    new BinanceCommissionRateSet("0.001", "0.001", "0.001", "0.001"),
                    new BinanceCommissionRateSet("0", "0", "0", "0"),
                    new BinanceCommissionRateSet("0", "0", "0", "0"),
                    new BinanceCommissionDiscount(false, false, null, null)),
                NowUtc));

        public Task<IReadOnlyList<BinanceOpenOrder>> GetOpenOrdersAsync(string? symbol = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BinanceOpenOrder>>([]);
        public Task<IReadOnlyList<BinanceTrade>> GetMyTradesAsync(string symbol, BinanceTradeQuery? query = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BinanceTrade>>([]);
        public Task<BinanceClockSnapshot> SynchronizeClockAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BinanceClockSnapshot(NowUtc.ToUnixTimeMilliseconds(), TimeSpan.Zero, true));
    }

    private sealed class TestPolicyStore : IRiskPolicyStore
    {
        private readonly RiskPolicyDocument document;

        public TestPolicyStore(BudgetPolicy budgetPolicy)
        {
            var policy = new RiskPolicy(
                version: "v1",
                maxOrderNotional: 50m,
                maxExposureNotional: 100m,
                maxDailyLoss: 25m,
                maxDrawdown: 50m,
                maxMarketDataAge: TimeSpan.FromMinutes(15),
                withdrawalsEnabled: false,
                budgetPolicy: budgetPolicy);

            document = RiskPolicyDocument.CreateDraft(
                policy,
                NowUtc.AddDays(-1),
                NowUtc.AddDays(10),
                "creator")
                .Approve("approver", "app-1", NowUtc.AddDays(-1))
                .Activate(NowUtc.AddDays(-1));
        }

        public Task<RiskPolicyDocument?> GetActiveRiskPolicyAsync(DateTimeOffset asOfUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<RiskPolicyDocument?>(document);

        public Task SaveRiskPolicyAsync(RiskPolicyDocument policyDocument, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<RiskPolicyDocument> ApproveRiskPolicyAsync(string policyVersion, string approvalId, string approver, DateTimeOffset approvedAtUtc, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<RiskPolicyDocument> ActivateRiskPolicyAsync(string policyVersion, DateTimeOffset activatedAtUtc, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class TestRiskContextStore : IAutonomyRiskContextStore
    {
        public decimal PositionQuantity { get; init; }

        public Task<AutonomyRiskContextSnapshot> RecordAutonomyRiskContextAsync(string scope, Symbol symbol, Asset quoteAsset, DateTimeOffset dayStartUtc, decimal? currentEquityNotional, DateTimeOffset valuationAtUtc, CancellationToken cancellationToken = default)
        {
            var qty = string.Equals(symbol.Value, "BTCUSDT", StringComparison.OrdinalIgnoreCase) ? PositionQuantity : 0m;
            return Task.FromResult(new AutonomyRiskContextSnapshot(0m, 0m, qty, true, valuationAtUtc, currentEquityNotional, currentEquityNotional, 0m, true, true));
        }

        public Task<AutonomyRiskContextSnapshot> ReadAutonomyRiskContextAsync(Symbol symbol, Asset quoteAsset, DateTimeOffset dayStartUtc, CancellationToken cancellationToken = default)
        {
            var qty = string.Equals(symbol.Value, "BTCUSDT", StringComparison.OrdinalIgnoreCase) ? PositionQuantity : 0m;
            return Task.FromResult(new AutonomyRiskContextSnapshot(0m, 0m, qty, true, dayStartUtc, 1000m, 1000m, 0m, true, true));
        }
    }

    private sealed class TestOpenOrderStore : ILocalOpenOrderStore
    {
        public Task<IReadOnlyList<LocalOpenOrderRecord>> ReadOpenOrdersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalOpenOrderRecord>>([]);
        public Task UpsertOpenOrderAsync(LocalOpenOrderRecord order, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveOpenOrderAsync(string clientOrderId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestSubmissionStore : IDurableSubmissionStore
    {
        public Task<bool> ValidateOutboxLeaseAsync(LeasedOutboxMessage message, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
        public Task<bool> ValidateOutboxLeaseAsync(LeasedOutboxMessage message, TimeSpan requiredLeaseDuration, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
        public Task<bool> HasSubmissionStartedAsync(LeasedOutboxMessage message, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
        public Task<bool> TryBeginSubmissionAsync(LeasedOutboxMessage message, TimeSpan minimumRemainingLease, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
