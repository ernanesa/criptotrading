using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Risk;
using Xunit;

namespace CryptoTrading.Reconciliation.Tests;

public sealed class AccountReconciliationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

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

    private static AccountReconciliationService CreateService(
        ReconciliationSnapshot snapshot,
        IReadOnlyDictionary<string, decimal> balances,
        IOperationalControl control,
        IReconciliationEventSink sink) => new(
        new FakeSnapshotProvider(snapshot),
        new FakeBalanceReader(balances),
        new FakeKnownTradeReader(new HashSet<long>()),
        sink,
        control,
        new ReconciliationOptions(new AccountId("exchange-wallet"), ["BTCUSDT"]),
        new FixedTimeProvider(Now));

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
    }

    private sealed class FakeKnownTradeReader(IReadOnlySet<long> ids) : IKnownTradeReader
    {
        public Task<IReadOnlySet<long>> GetKnownTradeIdsAsync(CancellationToken cancellationToken = default) => Task.FromResult(ids);
    }

    private sealed class AllowResetAuthorizer : IOperationalControlResetAuthorizer
    {
        public bool IsAuthorized(OperationalControlSnapshot current, OperationalControlCommand resetCommand, OperationalControlResetAuthorization authorization) => true;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
