using CryptoTrading.Persistence;
using Xunit;

namespace CryptoTrading.Persistence.Tests;

public sealed class CapitalReservationStoreTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TryReserveCapitalPersistsAndTracksActiveHeadroom()
    {
        var store = new InMemoryTradingStore();
        var request = new CapitalReservationRequest(
            reservationId: "res-1",
            accountId: "acc-spot",
            symbol: "BTCUSDT",
            clientOrderId: "client-ord-1",
            asset: "USDT",
            requestedAmount: 10.00m,
            effectiveBudgetCeilingAmount: 20.00m,
            openPositionsCommittedAmount: 0m,
            realizedLossesAmount: 0m,
            workerInstanceId: "worker-1",
            fencingToken: 1L,
            occurredAtUtc: NowUtc);

        var result = await store.TryReserveCapitalAsync(request, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("RESERVED", result.ReasonCode);
        Assert.NotNull(result.Reservation);
        Assert.Equal(10.00m, result.TotalActiveReservedAmount);
        Assert.Equal(CapitalReservationState.Pending, result.Reservation!.State);
        Assert.Equal(CapitalReservationStatus.Reserved, result.Reservation.Status);

        var active = await store.GetActiveReservationsAsync("acc-spot", TestContext.Current.CancellationToken);
        Assert.Single(active);
        Assert.Equal("client-ord-1", active[0].ClientOrderId);
    }

    [Fact]
    public async Task IdempotentReplayReturnsExistingReservationAndConflictingReplayThrows()
    {
        var store = new InMemoryTradingStore();
        var request = new CapitalReservationRequest(
            reservationId: "res-1",
            accountId: "acc-spot",
            symbol: "BTCUSDT",
            clientOrderId: "client-ord-1",
            asset: "USDT",
            requestedAmount: 10.00m,
            effectiveBudgetCeilingAmount: 20.00m,
            openPositionsCommittedAmount: 0m,
            realizedLossesAmount: 0m,
            workerInstanceId: "worker-1",
            fencingToken: 1L,
            occurredAtUtc: NowUtc);

        _ = await store.TryReserveCapitalAsync(request, TestContext.Current.CancellationToken);

        // Exact replay
        var replayResult = await store.TryReserveCapitalAsync(request, TestContext.Current.CancellationToken);
        Assert.True(replayResult.Succeeded);
        Assert.Equal("IDEMPOTENT_REPLAY", replayResult.ReasonCode);
        Assert.Equal("client-ord-1", replayResult.Reservation!.ClientOrderId);

        // Conflicting replay with different amount
        var conflictingRequest = new CapitalReservationRequest(
            reservationId: "res-conflict",
            accountId: "acc-spot",
            symbol: "BTCUSDT",
            clientOrderId: "client-ord-1",
            asset: "USDT",
            requestedAmount: 15.00m,
            effectiveBudgetCeilingAmount: 20.00m,
            openPositionsCommittedAmount: 0m,
            realizedLossesAmount: 0m,
            workerInstanceId: "worker-1",
            fencingToken: 1L,
            occurredAtUtc: NowUtc);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.TryReserveCapitalAsync(conflictingRequest, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MultiWorkerFencingRejectsStaleTokens()
    {
        var store = new InMemoryTradingStore();
        var request1 = new CapitalReservationRequest(
            reservationId: "res-1",
            accountId: "acc-spot",
            symbol: "BTCUSDT",
            clientOrderId: "client-ord-1",
            asset: "USDT",
            requestedAmount: 5.00m,
            effectiveBudgetCeilingAmount: 20.00m,
            openPositionsCommittedAmount: 0m,
            realizedLossesAmount: 0m,
            workerInstanceId: "worker-1",
            fencingToken: 10L,
            occurredAtUtc: NowUtc);

        var result1 = await store.TryReserveCapitalAsync(request1, TestContext.Current.CancellationToken);
        Assert.True(result1.Succeeded);

        // Stale worker fencing token (token 5 < 10)
        var staleRequest = new CapitalReservationRequest(
            reservationId: "res-2",
            accountId: "acc-spot",
            symbol: "BTCUSDT",
            clientOrderId: "client-ord-2",
            asset: "USDT",
            requestedAmount: 5.00m,
            effectiveBudgetCeilingAmount: 20.00m,
            openPositionsCommittedAmount: 0m,
            realizedLossesAmount: 0m,
            workerInstanceId: "worker-2",
            fencingToken: 5L,
            occurredAtUtc: NowUtc);

        var staleResult = await store.TryReserveCapitalAsync(staleRequest, TestContext.Current.CancellationToken);
        Assert.False(staleResult.Succeeded);
        Assert.Equal("STALE_WORKER_FENCING_TOKEN", staleResult.ReasonCode);
    }

    [Fact]
    public async Task AggregateCommitmentInvariantEnforcesCeilingAndPositions()
    {
        var store = new InMemoryTradingStore();
        // 1. Ceiling exceeded
        var overCeilingRequest = new CapitalReservationRequest(
            reservationId: "res-big",
            accountId: "acc-spot",
            symbol: "BTCUSDT",
            clientOrderId: "client-ord-big",
            asset: "USDT",
            requestedAmount: 25.00m,
            effectiveBudgetCeilingAmount: 20.00m,
            openPositionsCommittedAmount: 0m,
            realizedLossesAmount: 0m,
            workerInstanceId: "worker-1",
            fencingToken: 1L,
            occurredAtUtc: NowUtc);

        var ceilingResult = await store.TryReserveCapitalAsync(overCeilingRequest, TestContext.Current.CancellationToken);
        Assert.False(ceilingResult.Succeeded);
        Assert.Equal("BUDGET_CEILING_EXCEEDED", ceilingResult.ReasonCode);

        // 2. Max open positions exceeded
        var positionExceededRequest = new CapitalReservationRequest(
            reservationId: "res-pos",
            accountId: "acc-spot",
            symbol: "BTCUSDT",
            clientOrderId: "client-ord-pos",
            asset: "USDT",
            requestedAmount: 5.00m,
            effectiveBudgetCeilingAmount: 20.00m,
            openPositionsCommittedAmount: 10.00m,
            realizedLossesAmount: 0m,
            workerInstanceId: "worker-1",
            fencingToken: 2L,
            occurredAtUtc: NowUtc,
            openPositionsCount: 1,
            maxOpenPositions: 1);

        var positionResult = await store.TryReserveCapitalAsync(positionExceededRequest, TestContext.Current.CancellationToken);
        Assert.False(positionResult.Succeeded);
        Assert.Equal("MAX_OPEN_POSITIONS_EXCEEDED", positionResult.ReasonCode);
    }

    [Fact]
    public async Task LockedUnknownReservationsCannotBeReleasedByNormalTransitionAndBlockNewCapital()
    {
        var store = new InMemoryTradingStore();
        var request = new CapitalReservationRequest(
            reservationId: "res-unknown",
            accountId: "acc-spot",
            symbol: "BTCUSDT",
            clientOrderId: "client-ord-unknown",
            asset: "USDT",
            requestedAmount: 10.00m,
            effectiveBudgetCeilingAmount: 20.00m,
            openPositionsCommittedAmount: 0m,
            realizedLossesAmount: 0m,
            workerInstanceId: "worker-1",
            fencingToken: 1L,
            occurredAtUtc: NowUtc);

        var initial = await store.ReserveCapitalAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(CapitalReservationState.Pending, initial.State);

        // Mark as LockedUnknown
        var unknown = await store.MarkReservationLockedUnknownAsync(
            "client-ord-unknown", "worker-1", 2L, NowUtc, "EXCHANGE_TIMEOUT", TestContext.Current.CancellationToken);
        Assert.Equal(CapitalReservationState.LockedUnknown, unknown.State);

        // Standard transition/release MUST fail
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.TransitionReservationStateAsync(
                "client-ord-unknown", CapitalReservationState.Released, 0m, "worker-1", 3L, NowUtc, null, TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ReleaseReservationOnTerminalOrderAsync(
                "client-ord-unknown", 0m, "worker-1", 4L, NowUtc, null, TestContext.Current.CancellationToken));

        // New reservations are blocked while LockedUnknown exists
        var nextRequest = new CapitalReservationRequest(
            reservationId: "res-next",
            accountId: "acc-spot",
            symbol: "BTCUSDT",
            clientOrderId: "client-ord-next",
            asset: "USDT",
            requestedAmount: 5.00m,
            effectiveBudgetCeilingAmount: 20.00m,
            openPositionsCommittedAmount: 0m,
            realizedLossesAmount: 0m,
            workerInstanceId: "worker-1",
            fencingToken: 5L,
            occurredAtUtc: NowUtc);

        var blockedResult = await store.TryReserveCapitalAsync(nextRequest, TestContext.Current.CancellationToken);
        Assert.False(blockedResult.Succeeded);
        Assert.Equal("LOCKED_UNKNOWN_CAPITAL_ACTIVE", blockedResult.ReasonCode);

        // Only authoritative reconciliation can resolve LockedUnknown
        var reconciled = await store.ReconcileLockedUnknownAsync(
            "client-ord-unknown", CapitalReservationState.Released, 0m, "reconciliation-batch-1", NowUtc, TestContext.Current.CancellationToken);
        Assert.Equal(CapitalReservationState.Released, reconciled.State);
        Assert.False(reconciled.IsActive);

        // Now new reservation succeeds
        var allowedResult = await store.TryReserveCapitalAsync(nextRequest, TestContext.Current.CancellationToken);
        Assert.True(allowedResult.Succeeded);
    }
}
