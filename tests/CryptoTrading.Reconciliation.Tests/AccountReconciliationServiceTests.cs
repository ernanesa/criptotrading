using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Exchange.Binance;
using Xunit;

namespace CryptoTrading.Reconciliation.Tests;

public sealed class AccountReconciliationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReconciliationOptionsRejectsUnsafeSnapshotAge()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReconciliationOptions(new AccountId("exchange-wallet"), ["BTCUSDT"], maxSnapshotAge: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReconciliationOptions(new AccountId("exchange-wallet"), ["BTCUSDT"], maxSnapshotAge: TimeSpan.FromDays(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReconciliationOptions(new AccountId("exchange-wallet"), ["BTCUSDT"], maxTradePages: 0));
    }

    [Fact]
    public void SnapshotPreservesOpenOrderIdentityAndStatus()
    {
        var openOrder = ReconciliationOpenOrder.FromBinance(new BinanceOpenOrder(
            "btcusdt", 42, "client-42", "100", "1", "0", "0", "NEW",
            1_700_000_000_000, 1_700_000_000_001, true, "LIMIT", "BUY"));

        var snapshot = new ReconciliationSnapshot(
            Now,
            [new ReconciliationBalance("USDT", 100m, 0m)],
            [],
            [openOrder]);

        Assert.Equal("BTCUSDT", snapshot.OpenOrders.Single().Symbol);
        Assert.Equal(42, snapshot.OpenOrders.Single().OrderId);
        Assert.Equal("NEW", snapshot.OpenOrders.Single().Status);
    }

    [Fact]
    public async Task MatchingBalancesPublishReconciledEventsAndKeepNormalMode()
    {
        var control = CreateControl();
        var sink = new InMemoryReconciliationEventSink();
        var service = CreateService(
            new ReconciliationSnapshot(Now, [new ReconciliationBalance("USDT", 100m, 0m)], []),
            new Dictionary<string, decimal> { ["USDT"] = 100m },
            control,
            sink);

        var result = await service.ReconcileAsync();

        Assert.True(result.IsHealthy);
        Assert.IsType<BalanceReconciled>(Assert.Single(result.Events));
        Assert.Equal(OperationalMode.Normal, control.Snapshot.Mode);
        Assert.Single(sink.Events);
    }

    [Fact]
    public async Task DriftPublishesMismatchAndEntersSafeModeWithoutResettingIt()
    {
        var control = CreateControl();
        var service = CreateService(
            new ReconciliationSnapshot(Now, [new ReconciliationBalance("USDT", 100m, 0m)], []),
            new Dictionary<string, decimal> { ["USDT"] = 99m },
            control,
            new InMemoryReconciliationEventSink());

        var result = await service.ReconcileAsync();

        Assert.False(result.IsHealthy);
        var mismatch = Assert.IsType<ReconciliationMismatch>(Assert.Single(result.Events));
        Assert.Equal(1m, mismatch.Delta);
        Assert.True(control.Snapshot.BlocksNewEntries);
        Assert.Equal(OperationalMode.SafeMode, control.Snapshot.Mode);
    }

    [Fact]
    public async Task UnknownExchangeFillIsDriftEvenWhenBalancesMatch()
    {
        var control = CreateControl();
        var service = CreateService(
            new ReconciliationSnapshot(Now, [new ReconciliationBalance("BTC", 1m, 0m)], [new("BTCUSDT", 42, 7)]),
            new Dictionary<string, decimal> { ["BTC"] = 1m },
            control,
            new InMemoryReconciliationEventSink());

        var result = await service.ReconcileAsync();

        Assert.False(result.IsHealthy);
        Assert.Equal([42L], result.UnknownTradeIds);
        Assert.True(control.Snapshot.BlocksNewEntries);
        Assert.IsType<ReconciliationMismatch>(Assert.Single(result.Events));
    }

    [Fact]
    public async Task UnknownExchangeOpenOrderEntersSafeMode()
    {
        var control = CreateControl();
        var sink = new InMemoryReconciliationEventSink();
        var service = CreateService(
            new ReconciliationSnapshot(
                Now,
                [new ReconciliationBalance("USDT", 100m, 0m)],
                [],
                [new ReconciliationOpenOrder("BTCUSDT", 42, "client-42", "NEW")]),
            new Dictionary<string, decimal> { ["USDT"] = 100m },
            control,
            sink,
            [new ReconciliationOpenOrder("BTCUSDT", 0, "other-order", "NEW")]);

        var result = await service.ReconcileAsync();

        Assert.False(result.IsHealthy);
        Assert.Contains(result.Events, static item => item is OpenOrderReconciliationMismatch mismatch
            && mismatch.ReasonCode == "EXCHANGE_OPEN_ORDER_UNKNOWN_LOCALLY");
        Assert.True(control.Snapshot.BlocksNewEntries);
    }

    [Fact]
    public async Task LocalOnlyAssetIsReconciledAgainstZeroExchangeBalance()
    {
        var control = CreateControl();
        var service = CreateService(
            new ReconciliationSnapshot(Now, [new ReconciliationBalance("BTC", 1m, 0m)], []),
            new Dictionary<string, decimal> { ["BTC"] = 1m, ["USDT"] = 25m },
            control,
            new InMemoryReconciliationEventSink());

        var result = await service.ReconcileAsync();

        Assert.False(result.IsHealthy);
        var usdtMismatch = Assert.IsType<ReconciliationMismatch>(result.Events.Single(item => item is ReconciliationMismatch mismatch && mismatch.Asset == "USDT"));
        Assert.Equal(-25m, usdtMismatch.Delta);
        Assert.Equal(OperationalMode.SafeMode, control.Snapshot.Mode);
    }

    [Fact]
    public async Task RepeatedDriftUsesIdempotentSafeModeCommand()
    {
        var control = CreateControl();
        var snapshot = new ReconciliationSnapshot(Now, [new ReconciliationBalance("USDT", 100m, 0m)], []);
        var first = CreateService(snapshot, new Dictionary<string, decimal> { ["USDT"] = 99m }, control, new InMemoryReconciliationEventSink());
        var second = CreateService(snapshot, new Dictionary<string, decimal> { ["USDT"] = 99m }, control, new InMemoryReconciliationEventSink());

        await first.ReconcileAsync();
        var revision = control.Snapshot.Revision;
        await second.ReconcileAsync();

        Assert.Equal(revision, control.Snapshot.Revision);
        Assert.Equal(OperationalMode.SafeMode, control.Snapshot.Mode);
    }

    [Fact]
    public async Task StaleSnapshotFailsClosedAndEntersSafeMode()
    {
        var control = CreateControl();
        var service = CreateService(
            new ReconciliationSnapshot(Now.AddMinutes(-1), [new ReconciliationBalance("USDT", 100m, 0m)], []),
            new Dictionary<string, decimal> { ["USDT"] = 100m },
            control,
            new InMemoryReconciliationEventSink());

        var result = await service.ReconcileAsync();

        Assert.False(result.IsHealthy);
        var mismatch = Assert.IsType<ReconciliationMismatch>(Assert.Single(result.Events));
        Assert.Equal("STALE_SNAPSHOT", mismatch.ReasonCode);
        Assert.Equal(OperationalMode.SafeMode, control.Snapshot.Mode);
    }

    [Fact]
    public async Task FutureSnapshotFailsClosedInsteadOfBeingTrusted()
    {
        var control = CreateControl();
        var service = CreateService(
            new ReconciliationSnapshot(Now.AddSeconds(1), [new ReconciliationBalance("USDT", 100m, 0m)], []),
            new Dictionary<string, decimal> { ["USDT"] = 100m },
            control,
            new InMemoryReconciliationEventSink());

        var result = await service.ReconcileAsync();

        Assert.False(result.IsHealthy);
        Assert.Equal("STALE_SNAPSHOT", Assert.IsType<ReconciliationMismatch>(Assert.Single(result.Events)).ReasonCode);
        Assert.Equal(OperationalMode.SafeMode, control.Snapshot.Mode);
    }

    [Fact]
    public async Task BinanceSnapshotProviderPaginatesTradesAndDeduplicatesBoundaryRows()
    {
        var firstPage = Enumerable.Range(1, 1_000).Select(static id => CreateTrade(id)).ToArray();
        var secondPage = new[] { CreateTrade(1_000), CreateTrade(1_001) };
        var client = new FakeBinanceClient(firstPage, secondPage);
        var provider = new BinanceReadOnlySnapshotProvider(
            client,
            new ReconciliationOptions(new AccountId("exchange-wallet"), ["BTCUSDT"]),
            new FixedTimeProvider(Now));

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(1_001, snapshot.Fills.Count);
        Assert.Equal([null, 1_001L], client.FromIds);
    }

    private static AccountReconciliationService CreateService(
        ReconciliationSnapshot snapshot,
        IReadOnlyDictionary<string, decimal> balances,
        IOperationalControl control,
        IReconciliationEventSink sink,
        IReadOnlyList<ReconciliationOpenOrder>? localOpenOrders = null) => new(
        new FakeSnapshotProvider(snapshot),
        new FakeBalanceReader(balances),
        new FakeKnownTradeReader(new HashSet<long>()),
        sink,
        control,
        new ReconciliationOptions(new AccountId("exchange-wallet"), ["BTCUSDT"]),
        new FixedTimeProvider(Now),
        new FakeLocalOpenOrderReader(localOpenOrders ?? []));

    private static InMemoryOperationalControl CreateControl() => new(new AllowResetAuthorizer());

    private sealed class FakeSnapshotProvider(ReconciliationSnapshot snapshot) : IReadOnlyAccountSnapshotProvider
    {
        public Task<ReconciliationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class FakeBalanceReader(IReadOnlyDictionary<string, decimal> balances) : ILocalBalanceReader
    {
        public Task<decimal> GetBalanceAsync(AccountId account, string asset, CancellationToken cancellationToken = default) =>
            Task.FromResult(balances.GetValueOrDefault(asset));

        public Task<IReadOnlySet<string>> GetAssetsAsync(AccountId account, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(balances.Keys.ToHashSet(StringComparer.Ordinal));
    }

    private sealed class FakeKnownTradeReader(IReadOnlySet<long> ids) : IKnownTradeReader
    {
        public Task<IReadOnlySet<long>> GetKnownTradeIdsAsync(CancellationToken cancellationToken = default) => Task.FromResult(ids);
    }

    private sealed class FakeLocalOpenOrderReader(IReadOnlyList<ReconciliationOpenOrder> orders) : ILocalOpenOrderReader
    {
        public Task<IReadOnlyList<ReconciliationOpenOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken = default) => Task.FromResult(orders);
    }

    private sealed class AllowResetAuthorizer : IOperationalControlResetAuthorizer
    {
        public bool IsAuthorized(OperationalControlSnapshot current, OperationalControlCommand resetCommand, OperationalControlResetAuthorization authorization) => true;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static BinanceTrade CreateTrade(long id) => new(
        "BTCUSDT", id, id, "100", "0.01", "1", "0.001", "USDT", 1, true, false, true);

    private sealed class FakeBinanceClient(
        IReadOnlyList<BinanceTrade> firstPage,
        IReadOnlyList<BinanceTrade> secondPage) : IBinanceSpotAccountClient
    {
        public List<long?> FromIds { get; } = [];

        public Task<BinanceAccountSnapshot> GetAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BinanceAccountSnapshot(15, 15, 0, 0, true, false, true, 1, "SPOT", []));

        public Task<IReadOnlyList<BinanceOpenOrder>> GetOpenOrdersAsync(string? symbol = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BinanceOpenOrder>>([]);

        public Task<IReadOnlyList<BinanceTrade>> GetMyTradesAsync(string symbol, BinanceTradeQuery? query = null, CancellationToken cancellationToken = default)
        {
            FromIds.Add(query?.FromId);
            return Task.FromResult<IReadOnlyList<BinanceTrade>>(query?.FromId is null ? firstPage : secondPage);
        }

        public Task<BinanceClockSnapshot> SynchronizeClockAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BinanceClockSnapshot(1, TimeSpan.Zero, true));
    }
}
