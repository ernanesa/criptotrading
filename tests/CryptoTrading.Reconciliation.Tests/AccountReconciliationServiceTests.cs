using CryptoTrading.Domain.Accounting;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Exchange.Binance;
using CryptoTrading.Observability;
using Xunit;

namespace CryptoTrading.Reconciliation.Tests;

public sealed class AccountReconciliationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("usdt", "USDT")]
    [InlineData(" testasset1 ", "TESTASSET1")]
    public void BinanceBalanceFlowsIntoReconciliationWithSharedAssetIdentity(string exchangeAsset, string expectedAsset)
    {
        var balance = new BinanceBalance(exchangeAsset, "100.00", "0.00");

        var reconciliationBalance = ReconciliationBalance.FromBinance(balance);

        Assert.Equal(expectedAsset, reconciliationBalance.Asset);
        Assert.Equal(100m, reconciliationBalance.Total);
    }

    [Theory]
    [InlineData("TEST-ASSET_1")]
    [InlineData("X")]
    [InlineData("ASSET\u0001")]
    public void BinanceBalanceRejectsAssetOutsideSharedContract(string exchangeAsset)
    {
        var balance = new BinanceBalance(exchangeAsset, "1", "0");

        Assert.Throws<ArgumentException>(() => ReconciliationBalance.FromBinance(balance));
    }

    [Fact]
    public async Task ReconciliationActivityContainsOnlyBoundedOutcomeTags()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TradingTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);
        var service = CreateService(
            new ReconciliationSnapshot(Now, [new ReconciliationBalance("USDT", 100m, 0m)], []),
            new Dictionary<string, decimal> { ["USDT"] = 100m },
            CreateControl(),
            new InMemoryReconciliationEventSink());

        var result = await service.ReconcileAsync(cancellationToken: TestContext.Current.CancellationToken);

        var activity = Assert.Single(activities);
        Assert.Equal("reconciliation", activity.GetTagItem("crypto.operation"));
        Assert.Equal("healthy", activity.GetTagItem("crypto.outcome"));
        Assert.Equal(0, activity.GetTagItem("crypto.mismatch_count"));
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
        Assert.DoesNotContain(activity.Tags, tag => tag.Key is "asset" or "symbol" or "clientOrderId");
        Assert.True(result.IsHealthy);
    }

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
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_001), snapshot.OpenOrders.Single().ExchangeUpdatedAtUtc);
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

        var result = await service.ReconcileAsync(cancellationToken: TestContext.Current.CancellationToken);

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

        var result = await service.ReconcileAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsHealthy);
        var mismatch = Assert.IsType<ReconciliationMismatch>(Assert.Single(result.Events));
        Assert.Equal(1m, mismatch.Delta);
        Assert.True(control.Snapshot.BlocksNewEntries);
        Assert.Equal(OperationalMode.SafeMode, control.Snapshot.Mode);
    }

    [Fact]
    public async Task UnmodeledExchangeLockedBalanceEntersSafeModeEvenWhenTotalsMatch()
    {
        var control = CreateControl();
        var service = CreateService(
            new ReconciliationSnapshot(Now, [new ReconciliationBalance("USDT", 90m, 10m)], []),
            new Dictionary<string, decimal> { ["USDT"] = 100m },
            control,
            new InMemoryReconciliationEventSink());

        var result = await service.ReconcileAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsHealthy);
        Assert.Contains(result.Events, static item => item is ReconciliationMismatch mismatch
            && mismatch.ReasonCode == "EXCHANGE_LOCKED_BALANCE_UNMODELED");
        Assert.Equal(OperationalMode.SafeMode, control.Snapshot.Mode);
    }

    [Fact]
    public async Task EventPersistenceFailureEntersSafeModeBeforePropagatingFailure()
    {
        var control = CreateControl();
        var service = CreateService(
            new ReconciliationSnapshot(Now, [new ReconciliationBalance("USDT", 100m, 0m)], []),
            new Dictionary<string, decimal> { ["USDT"] = 99m },
            control,
            new FailingEventSink());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReconcileAsync(cancellationToken: TestContext.Current.CancellationToken));

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

        var result = await service.ReconcileAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsHealthy);
        Assert.Equal([42L], result.UnknownTradeIds);
        Assert.True(control.Snapshot.BlocksNewEntries);
        Assert.IsType<ReconciliationMismatch>(Assert.Single(result.Events));
    }

    [Fact]
    public async Task SameTradeIdForDifferentSymbolsPreservesUnknownTradeIdentity()
    {
        var control = CreateControl();
        var service = CreateService(
            new ReconciliationSnapshot(
                Now,
                [new ReconciliationBalance("BTC", 1m, 0m)],
                [new("BTCUSDT", 42, 7), new("ETHUSDT", 42, 8)]),
            new Dictionary<string, decimal> { ["BTC"] = 1m },
            control,
            new InMemoryReconciliationEventSink(),
            knownTrades: new HashSet<KnownTradeIdentity> { new("BTCUSDT", 42) });

        var result = await service.ReconcileAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([42L], result.UnknownTradeIds);
        Assert.Equal([new KnownTradeIdentity("ETHUSDT", 42)], result.UnknownTrades);
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

        var result = await service.ReconcileAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsHealthy);
        Assert.Contains(result.Events, static item => item is OpenOrderReconciliationMismatch mismatch
            && mismatch.ReasonCode == "EXCHANGE_OPEN_ORDER_UNKNOWN_LOCALLY");
        Assert.Equal(2, result.MismatchCount);
        Assert.True(control.Snapshot.BlocksNewEntries);
    }

    [Fact]
    public async Task FreshExchangeSnapshotResolvesUnknownOrderIdempotently()
    {
        long mismatches = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == TradingTelemetry.MeterName && instrument.Name == "cryptotrading.reconciliation.mismatches")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref mismatches, value));
        listener.Start();
        var control = CreateControl();
        var sink = new InMemoryReconciliationEventSink();
        var resolver = new RecordingOpenOrderResolutionSink();
        var service = CreateService(
            new ReconciliationSnapshot(
                Now,
                [new ReconciliationBalance("USDT", 100m, 0m)],
                [],
                [new ReconciliationOpenOrder("BTCUSDT", 42, "client-42", "NEW")]),
            new Dictionary<string, decimal> { ["USDT"] = 100m },
            control,
            sink,
            [new ReconciliationOpenOrder("BTCUSDT", 42, "client-42", "UNKNOWN")],
            resolver);

        var result = await service.ReconcileAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsHealthy);
        Assert.Equal(0, mismatches);
        var resolved = Assert.Single(result.Events.OfType<OpenOrderReconciled>());
        Assert.Equal("UNKNOWN", resolved.PreviousLocalStatus);
        Assert.Equal("NEW", resolved.AuthoritativeStatus);
        Assert.Single(resolver.Resolutions);
        Assert.Equal("NEW", resolver.Resolutions[0].Status);
    }

    [Fact]
    public async Task RejectedStaleOpenOrderResolutionBecomesMismatchAndEntersSafeMode()
    {
        var control = CreateControl();
        var service = CreateService(
            new ReconciliationSnapshot(
                Now,
                [new ReconciliationBalance("USDT", 100m, 0m)],
                [],
                [new ReconciliationOpenOrder("BTCUSDT", 42, "client-42", "NEW")]),
            new Dictionary<string, decimal> { ["USDT"] = 100m },
            control,
            new InMemoryReconciliationEventSink(),
            [new ReconciliationOpenOrder("BTCUSDT", 42, "client-42", "UNKNOWN")],
            new RejectingOpenOrderResolutionSink());

        var result = await service.ReconcileAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsHealthy);
        Assert.Contains(result.Events, static item => item is OpenOrderReconciliationMismatch mismatch
            && mismatch.ReasonCode == "AUTHORITATIVE_ORDER_SNAPSHOT_REJECTED");
        Assert.Equal(OperationalMode.SafeMode, control.Snapshot.Mode);
    }

    [Fact]
    public async Task MissingExchangeOrderDoesNotInferCancellationFromUnknown()
    {
        var control = CreateControl();
        var sink = new InMemoryReconciliationEventSink();
        var service = CreateService(
            new ReconciliationSnapshot(Now, [new ReconciliationBalance("USDT", 100m, 0m)], []),
            new Dictionary<string, decimal> { ["USDT"] = 100m },
            control,
            sink,
            [new ReconciliationOpenOrder("BTCUSDT", 42, "client-42", "UNKNOWN")],
            new RecordingOpenOrderResolutionSink());

        var result = await service.ReconcileAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsHealthy);
        Assert.Contains(result.Events, static item => item is OpenOrderReconciliationMismatch mismatch
            && mismatch.ReasonCode == "UNKNOWN_ORDER_NOT_FOUND_ON_EXCHANGE");
        Assert.True(control.Snapshot.BlocksNewEntries);
    }

    [Fact]
    public async Task PendingCancelRemainsBlockedWhileExchangeReportsPendingCancel()
    {
        var control = CreateControl();
        var sink = new InMemoryReconciliationEventSink();
        var service = CreateService(
            new ReconciliationSnapshot(
                Now,
                [new ReconciliationBalance("USDT", 100m, 0m)],
                [],
                [new ReconciliationOpenOrder("BTCUSDT", 42, "client-42", "PENDING_CANCEL")]),
            new Dictionary<string, decimal> { ["USDT"] = 100m },
            control,
            sink,
            [new ReconciliationOpenOrder("BTCUSDT", 42, "client-42", "PENDING_CANCEL")],
            new RecordingOpenOrderResolutionSink());

        var result = await service.ReconcileAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsHealthy);
        Assert.Contains(result.Events, static item => item is OpenOrderReconciliationMismatch mismatch
            && mismatch.ReasonCode == "ORDER_CANCEL_PENDING");
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

        var result = await service.ReconcileAsync(cancellationToken: TestContext.Current.CancellationToken);

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

        await first.ReconcileAsync(cancellationToken: TestContext.Current.CancellationToken);
        var revision = control.Snapshot.Revision;
        await second.ReconcileAsync(cancellationToken: TestContext.Current.CancellationToken);

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

        var result = await service.ReconcileAsync(cancellationToken: TestContext.Current.CancellationToken);

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

        var result = await service.ReconcileAsync(cancellationToken: TestContext.Current.CancellationToken);

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

        var snapshot = await provider.GetSnapshotAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1_001, snapshot.Fills.Count);
        Assert.Equal([null, 1_001L], client.FromIds);
    }

    [Fact]
    public async Task BinanceSnapshotTimestampUsesConservativeStartOfSequentialFetchWindow()
    {
        var timeProvider = new AdvancingTimeProvider(Now);
        var client = new FakeBinanceClient([], [], () => timeProvider.Advance(TimeSpan.FromMinutes(5)));
        var provider = new BinanceReadOnlySnapshotProvider(
            client,
            new ReconciliationOptions(new AccountId("exchange-wallet"), ["BTCUSDT"]),
            timeProvider);

        var snapshot = await provider.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Now, snapshot.ObservedAtUtc);
    }

    [Fact]
    public async Task BinanceSnapshotProviderPreservesSameTradeIdAcrossSymbols()
    {
        var trades = new[]
        {
            CreateTrade(42) with { Symbol = "BTCUSDT" },
            CreateTrade(42) with { Symbol = "ETHUSDT" },
        };
        var client = new FakeBinanceClient(trades, []);
        var provider = new BinanceReadOnlySnapshotProvider(
            client,
            new ReconciliationOptions(new AccountId("exchange-wallet"), ["BTCUSDT", "ETHUSDT"]),
            new FixedTimeProvider(Now));

        var snapshot = await provider.GetSnapshotAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            [new ReconciliationFill("BTCUSDT", 42, 42), new ReconciliationFill("ETHUSDT", 42, 42)],
            snapshot.Fills.OrderBy(static fill => fill.Symbol, StringComparer.Ordinal));
    }

    [Fact]
    public async Task BinanceSnapshotProviderPreservesSameOrderIdAcrossSymbols()
    {
        var orders = new[]
        {
            new BinanceOpenOrder("BTCUSDT", 42, "btc-client", "100", "1", "0", "0", "NEW", 1, 1, true, "LIMIT", "BUY"),
            new BinanceOpenOrder("ETHUSDT", 42, "eth-client", "100", "1", "0", "0", "NEW", 1, 1, true, "LIMIT", "BUY")
        };
        var client = new FakeBinanceClient([], [], openOrders: orders);
        var provider = new BinanceReadOnlySnapshotProvider(
            client,
            new ReconciliationOptions(new AccountId("exchange-wallet"), ["BTCUSDT", "ETHUSDT"]),
            new FixedTimeProvider(Now));

        var snapshot = await provider.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, snapshot.OpenOrders.Count);
        Assert.Contains(snapshot.OpenOrders, order => order.Symbol == "BTCUSDT" && order.OrderId == 42);
        Assert.Contains(snapshot.OpenOrders, order => order.Symbol == "ETHUSDT" && order.OrderId == 42);
    }

    [Fact]
    public async Task BinanceSnapshotProviderRejectsConflictingDuplicateOrderSnapshots()
    {
        var orders = new[]
        {
            new BinanceOpenOrder("BTCUSDT", 42, "first-client", "100", "1", "0", "0", "NEW", 1, 1, true, "LIMIT", "BUY"),
            new BinanceOpenOrder("BTCUSDT", 42, "other-client", "100", "1", "0", "0", "NEW", 1, 1, true, "LIMIT", "BUY")
        };
        var provider = new BinanceReadOnlySnapshotProvider(
            new FakeBinanceClient([], [], openOrders: orders),
            new ReconciliationOptions(new AccountId("exchange-wallet"), ["BTCUSDT"]),
            new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetSnapshotAsync(TestContext.Current.CancellationToken));
    }

    private static AccountReconciliationService CreateService(
        ReconciliationSnapshot snapshot,
        IReadOnlyDictionary<string, decimal> balances,
        IAsyncOperationalControl control,
    IReconciliationEventSink sink,
    IReadOnlyList<ReconciliationOpenOrder>? localOpenOrders = null,
    ILocalOpenOrderResolutionSink? resolver = null,
    IReadOnlySet<KnownTradeIdentity>? knownTrades = null) => new(
        new FakeSnapshotProvider(snapshot),
        new FakeBalanceReader(balances),
    new FakeKnownTradeReader(knownTrades ?? new HashSet<KnownTradeIdentity>()),
        sink,
        control,
        new ReconciliationOptions(new AccountId("exchange-wallet"), ["BTCUSDT"]),
        new FixedTimeProvider(Now),
        new FakeLocalOpenOrderReader(localOpenOrders ?? []),
        resolver);

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

    private sealed class FakeKnownTradeReader(IReadOnlySet<KnownTradeIdentity> ids) : IKnownTradeReader
    {
        public Task<IReadOnlySet<KnownTradeIdentity>> GetKnownTradeIdsAsync(CancellationToken cancellationToken = default) => Task.FromResult(ids);
    }

    private sealed class FakeLocalOpenOrderReader(IReadOnlyList<ReconciliationOpenOrder> orders) : ILocalOpenOrderReader
    {
        public Task<IReadOnlyList<ReconciliationOpenOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken = default) => Task.FromResult(orders);
    }

    private sealed class FailingEventSink : IReconciliationEventSink
    {
        public Task PublishAsync(ReconciliationEvent reconciliationEvent, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("synthetic persistence failure"));
    }

    private sealed class RecordingOpenOrderResolutionSink : ILocalOpenOrderResolutionSink
    {
        public List<ReconciliationOpenOrder> Resolutions { get; } = [];

        public Task ResolveAsync(
            ReconciliationOpenOrder authoritativeOrder,
            string previousLocalStatus,
            CorrelationId correlationId,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken = default)
        {
            Resolutions.Add(authoritativeOrder);
            return Task.CompletedTask;
        }
    }

    private sealed class RejectingOpenOrderResolutionSink : ILocalOpenOrderResolutionSink
    {
        public Task ResolveAsync(
            ReconciliationOpenOrder authoritativeOrder,
            string previousLocalStatus,
            CorrelationId correlationId,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("snapshot older than persisted order state"));
    }

    private sealed class AllowResetAuthorizer : IOperationalControlResetAuthorizer
    {
        public bool IsAuthorized(OperationalControlSnapshot current, OperationalControlCommand resetCommand, OperationalControlResetAuthorization authorization) => true;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class AdvancingTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan duration) => current += duration;
    }

    private static BinanceTrade CreateTrade(long id) => new(
        "BTCUSDT", id, id, "100", "0.01", "1", "0.001", "USDT", 1, true, false, true);

    private sealed class FakeBinanceClient(
        IReadOnlyList<BinanceTrade> firstPage,
        IReadOnlyList<BinanceTrade> secondPage,
        Action? onGetAccount = null,
        IReadOnlyList<BinanceOpenOrder>? openOrders = null) : IBinanceSpotAccountClient
    {
        public List<long?> FromIds { get; } = [];

        public Task<BinanceAccountSnapshot> GetAccountAsync(CancellationToken cancellationToken = default)
        {
            onGetAccount?.Invoke();
            return Task.FromResult(new BinanceAccountSnapshot(15, 15, 0, 0, true, false, true, 1, "SPOT", []));
        }

        public Task<IReadOnlyList<BinanceOpenOrder>> GetOpenOrdersAsync(string? symbol = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BinanceOpenOrder>>(openOrders?.Where(order => order.Symbol == symbol).ToArray() ?? []);

        public Task<IReadOnlyList<BinanceTrade>> GetMyTradesAsync(string symbol, BinanceTradeQuery? query = null, CancellationToken cancellationToken = default)
        {
            FromIds.Add(query?.FromId);
            return Task.FromResult<IReadOnlyList<BinanceTrade>>(query?.FromId is null ? firstPage : secondPage);
        }

        public Task<BinanceCommissionSnapshot> GetCommissionAsync(string symbol, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Commission is outside this reconciliation fake.");

        public Task<BinanceClockSnapshot> SynchronizeClockAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BinanceClockSnapshot(1, TimeSpan.Zero, true));
    }

    [Fact]
    public async Task LockedBalanceMatchingActiveCapitalReservationReconcilesCleanly()
    {
        var control = CreateControl();
        var sink = new InMemoryReconciliationEventSink();
        var lockedReader = new FakeLockedBalanceReader(
            new Dictionary<string, LocalLockedBalanceExpectation>(StringComparer.Ordinal)
            {
                ["USDT"] = new("USDT", ExpectedLockedAmount: 12.50m, HasLockedUnknownReservation: false),
            });
        var service = new AccountReconciliationService(
            new FakeSnapshotProvider(new ReconciliationSnapshot(Now, [new ReconciliationBalance("USDT", 7.50m, 12.50m)], [])),
            new FakeBalanceReader(new Dictionary<string, decimal> { ["USDT"] = 20.00m }),
            new NoKnownTradesReader(),
            sink,
            control,
            new ReconciliationOptions(new AccountId("exchange-wallet"), ["BTCUSDT"]),
            new FixedTimeProvider(Now),
            lockedBalanceReader: lockedReader);

        var result = await service.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsHealthy);
        Assert.IsType<BalanceReconciled>(Assert.Single(result.Events));
        Assert.Equal(OperationalMode.Normal, control.Snapshot.Mode);
    }

    [Fact]
    public async Task LockedBalanceDriftOrLockedUnknownReservationEntersSafeMode()
    {
        var control = CreateControl();
        var sink = new InMemoryReconciliationEventSink();
        var lockedReader = new FakeLockedBalanceReader(
            new Dictionary<string, LocalLockedBalanceExpectation>(StringComparer.Ordinal)
            {
                ["USDT"] = new("USDT", ExpectedLockedAmount: 10.00m, HasLockedUnknownReservation: true),
            });
        var service = new AccountReconciliationService(
            new FakeSnapshotProvider(new ReconciliationSnapshot(Now, [new ReconciliationBalance("USDT", 10.00m, 10.00m)], [])),
            new FakeBalanceReader(new Dictionary<string, decimal> { ["USDT"] = 20.00m }),
            new NoKnownTradesReader(),
            sink,
            control,
            new ReconciliationOptions(new AccountId("exchange-wallet"), ["BTCUSDT"]),
            new FixedTimeProvider(Now),
            lockedBalanceReader: lockedReader);

        var result = await service.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsHealthy);
        var mismatch = Assert.IsType<ReconciliationMismatch>(Assert.Single(result.Events));
        Assert.Equal("LOCKED_UNKNOWN_CAPITAL_UNRESOLVED", mismatch.ReasonCode);
        Assert.Equal(OperationalMode.SafeMode, control.Snapshot.Mode);
    }

    private sealed class FakeLockedBalanceReader(
        IReadOnlyDictionary<string, LocalLockedBalanceExpectation> expectations) : ILocalLockedBalanceReader
    {
        public Task<LocalLockedBalanceExpectation> GetLockedExpectationAsync(
            AccountId account,
            string asset,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                expectations.TryGetValue(asset, out var expectation)
                    ? expectation
                    : new LocalLockedBalanceExpectation(asset, 0m, false));
    }
}
