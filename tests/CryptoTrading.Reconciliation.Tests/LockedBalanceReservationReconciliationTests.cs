using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using Xunit;

namespace CryptoTrading.Reconciliation.Tests;

public sealed class LockedBalanceReservationReconciliationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExchangeLockedBalanceWithoutLocalReaderFailsClosedAsUnmodeled()
    {
        var snapshot = new ReconciliationSnapshot(
            Now,
            [new ReconciliationBalance("USDT", 90m, 10m)], // Total 100m, Locked 10m
            []);
        var control = new InMemoryOperationalControl(new AllowResetAuthorizer());
        var sink = new InMemoryReconciliationEventSink();

        var service = new AccountReconciliationService(
            new FakeSnapshotProvider(snapshot),
            new FakeBalanceReader(new Dictionary<string, decimal> { ["USDT"] = 100m }),
            new FakeKnownTradeReader(new HashSet<KnownTradeIdentity>()),
            sink,
            control,
            new ReconciliationOptions(new AccountId("exchange-wallet"), ["BTCUSDT"]),
            new FixedTimeProvider(Now),
            lockedBalanceReader: null);

        var result = await service.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsHealthy);
        var mismatch = Assert.Single(sink.Events.OfType<ReconciliationMismatch>());
        Assert.Equal("EXCHANGE_LOCKED_BALANCE_UNMODELED", mismatch.ReasonCode);
        Assert.Equal("USDT", mismatch.Asset);
        Assert.True(control.Snapshot.BlocksNewEntries);
    }

    [Fact]
    public async Task MatchingLocalLockedBalanceReconcilesCleanlyAsHealthy()
    {
        var snapshot = new ReconciliationSnapshot(
            Now,
            [new ReconciliationBalance("USDT", 90m, 10m)], // Total 100m, Locked 10m
            []);
        var control = new InMemoryOperationalControl(new AllowResetAuthorizer());
        var sink = new InMemoryReconciliationEventSink();
        var lockedReader = new FakeLockedBalanceReader(new Dictionary<string, LocalLockedBalanceExpectation>
        {
            ["USDT"] = new LocalLockedBalanceExpectation("USDT", 10m, HasLockedUnknownReservation: false),
        });

        var service = new AccountReconciliationService(
            new FakeSnapshotProvider(snapshot),
            new FakeBalanceReader(new Dictionary<string, decimal> { ["USDT"] = 100m }),
            new FakeKnownTradeReader(new HashSet<KnownTradeIdentity>()),
            sink,
            control,
            new ReconciliationOptions(new AccountId("exchange-wallet"), ["BTCUSDT"]),
            new FixedTimeProvider(Now),
            lockedBalanceReader: lockedReader);

        var result = await service.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsHealthy);
        var reconciled = Assert.Single(sink.Events.OfType<BalanceReconciled>());
        Assert.Equal("USDT", reconciled.Asset);
        Assert.False(control.Snapshot.BlocksNewEntries);
    }

    [Fact]
    public async Task LockedBalanceDriftFailsClosedAndEntersSafeMode()
    {
        var snapshot = new ReconciliationSnapshot(
            Now,
            [new ReconciliationBalance("USDT", 90m, 10m)], // Exchange Locked = 10m
            []);
        var control = new InMemoryOperationalControl(new AllowResetAuthorizer());
        var sink = new InMemoryReconciliationEventSink();
        // Local expected locked is only 5m
        var lockedReader = new FakeLockedBalanceReader(new Dictionary<string, LocalLockedBalanceExpectation>
        {
            ["USDT"] = new LocalLockedBalanceExpectation("USDT", 5m, HasLockedUnknownReservation: false),
        });

        var service = new AccountReconciliationService(
            new FakeSnapshotProvider(snapshot),
            new FakeBalanceReader(new Dictionary<string, decimal> { ["USDT"] = 100m }),
            new FakeKnownTradeReader(new HashSet<KnownTradeIdentity>()),
            sink,
            control,
            new ReconciliationOptions(new AccountId("exchange-wallet"), ["BTCUSDT"]),
            new FixedTimeProvider(Now),
            lockedBalanceReader: lockedReader);

        var result = await service.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsHealthy);
        var mismatch = Assert.Single(sink.Events.OfType<ReconciliationMismatch>());
        Assert.Equal("LOCKED_BALANCE_RESERVATION_DRIFT", mismatch.ReasonCode);
        Assert.True(control.Snapshot.BlocksNewEntries);
    }

    [Fact]
    public async Task LockedUnknownLocalReservationFailsClosedEvenIfLockedAmountsMatch()
    {
        var snapshot = new ReconciliationSnapshot(
            Now,
            [new ReconciliationBalance("USDT", 90m, 10m)],
            []);
        var control = new InMemoryOperationalControl(new AllowResetAuthorizer());
        var sink = new InMemoryReconciliationEventSink();
        var lockedReader = new FakeLockedBalanceReader(new Dictionary<string, LocalLockedBalanceExpectation>
        {
            ["USDT"] = new LocalLockedBalanceExpectation("USDT", 10m, HasLockedUnknownReservation: true),
        });

        var service = new AccountReconciliationService(
            new FakeSnapshotProvider(snapshot),
            new FakeBalanceReader(new Dictionary<string, decimal> { ["USDT"] = 100m }),
            new FakeKnownTradeReader(new HashSet<KnownTradeIdentity>()),
            sink,
            control,
            new ReconciliationOptions(new AccountId("exchange-wallet"), ["BTCUSDT"]),
            new FixedTimeProvider(Now),
            lockedBalanceReader: lockedReader);

        var result = await service.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsHealthy);
        var mismatch = Assert.Single(sink.Events.OfType<ReconciliationMismatch>());
        Assert.Equal("LOCKED_UNKNOWN_CAPITAL_UNRESOLVED", mismatch.ReasonCode);
        Assert.True(control.Snapshot.BlocksNewEntries);
    }

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
        public Task<IReadOnlySet<KnownTradeIdentity>> GetKnownTradeIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ids);
    }

    private sealed class FakeLockedBalanceReader(IReadOnlyDictionary<string, LocalLockedBalanceExpectation> expectations) : ILocalLockedBalanceReader
    {
        public Task<LocalLockedBalanceExpectation> GetLockedExpectationAsync(AccountId account, string asset, CancellationToken cancellationToken = default) =>
            Task.FromResult(expectations.GetValueOrDefault(asset, new LocalLockedBalanceExpectation(asset, 0m, false)));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class AllowResetAuthorizer : IOperationalControlResetAuthorizer
    {
        public bool IsAuthorized(
            OperationalControlSnapshot current,
            OperationalControlCommand resetCommand,
            OperationalControlResetAuthorization authorization) => true;
    }
}
