using CryptoTrading.Exchange.Binance;
using CryptoTrading.Persistence;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class AccountStreamOrderProjectionTests
{
    [Fact]
    public async Task ActiveExecutionReportUpsertsOpenOrder()
    {
        var store = new RecordingStore();
        await AccountStreamOrderProjection.ApplyAsync(store, Event("NEW"), cancellationToken: TestContext.Current.CancellationToken);

        var record = Assert.Single(store.Records);
        Assert.Equal("BTCUSDT", record.Symbol);
        Assert.Equal(42, record.OrderId);
        Assert.Equal("client-42", record.ClientOrderId);
        Assert.Equal("NEW", record.Status);
        Assert.Equal(0.01m, record.OriginalQuantity);
        Assert.Equal(0m, record.CumulativeExecutedQuantity);
    }

    [Fact]
    public async Task AccountStreamSeparatesLocalReceiveTimeFromExchangeEventTime()
    {
        var store = new RecordingStore();
        var receivedAtUtc = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        var eventTimeMilliseconds = 1_700_000_000_123;
        var execution = Event("NEW", eventTimeMilliseconds: eventTimeMilliseconds) with { ReceivedAtUtc = receivedAtUtc };

        await AccountStreamOrderProjection.ApplyAsync(store, execution, cancellationToken: TestContext.Current.CancellationToken);

        var projected = Assert.Single(store.Records);
        Assert.Equal(receivedAtUtc, projected.UpdatedAtUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(eventTimeMilliseconds), projected.ExchangeUpdatedAtUtc);
    }

    [Fact]
    public async Task TerminalExecutionReportRemovesOpenOrder()
    {
        var store = new RecordingStore();
        await AccountStreamOrderProjection.ApplyAsync(store, Event("NEW"), cancellationToken: TestContext.Current.CancellationToken);
        await AccountStreamOrderProjection.ApplyAsync(store, Event("FILLED"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task ExpiredInMatchExecutionReportLeavesTerminalTombstone()
    {
        var store = new RecordingStore();
        await AccountStreamOrderProjection.ApplyAsync(store, Event("NEW"), cancellationToken: TestContext.Current.CancellationToken);

        await AccountStreamOrderProjection.ApplyAsync(
            store, Event("EXPIRED_IN_MATCH"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task DelayedActiveExecutionReportDoesNotResurrectTerminalOrder()
    {
        var store = new RecordingStore();
        await AccountStreamOrderProjection.ApplyAsync(store, Event("NEW", eventTimeMilliseconds: 1_700_000_000_123), cancellationToken: TestContext.Current.CancellationToken);
        await AccountStreamOrderProjection.ApplyAsync(store, Event("CANCELED", eventTimeMilliseconds: 1_700_000_000_125), cancellationToken: TestContext.Current.CancellationToken);

        await AccountStreamOrderProjection.ApplyAsync(store, Event("PARTIALLY_FILLED", cumulativeQuantity: 0.005m, eventTimeMilliseconds: 1_700_000_000_124), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task NewExecutionReportWithPriorFillIsRejectedWithoutRegressingProjection()
    {
        var store = new RecordingStore();
        await AccountStreamOrderProjection.ApplyAsync(store, Event("NEW", cumulativeQuantity: 0m), cancellationToken: TestContext.Current.CancellationToken);
        await AccountStreamOrderProjection.ApplyAsync(store, Event("PARTIALLY_FILLED", cumulativeQuantity: 0.005m), cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AccountStreamOrderProjection.ApplyAsync(
            store,
            Event("NEW", cumulativeQuantity: 0.005m),
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("PARTIALLY_FILLED", Assert.Single(store.Records).Status);
    }

    [Fact]
    public async Task PendingCancelExecutionReportRemainsOpenUntilTerminalOutcome()
    {
        var store = new RecordingStore();
        await AccountStreamOrderProjection.ApplyAsync(store, Event("PENDING_CANCEL"), cancellationToken: TestContext.Current.CancellationToken);

        var record = Assert.Single(store.Records);
        Assert.Equal("PENDING_CANCEL", record.Status);
    }

    [Fact]
    public async Task UnknownStatusFailsClosed()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AccountStreamOrderProjection.ApplyAsync(new RecordingStore(), Event("UNKNOWN"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CumulativeExecutionAboveOriginalQuantityFailsClosedWithoutChangingProjection()
    {
        var store = new RecordingStore();
        await AccountStreamOrderProjection.ApplyAsync(store, Event("PARTIALLY_FILLED", originalQuantity: 0.01m, cumulativeQuantity: 0.006m), cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AccountStreamOrderProjection.ApplyAsync(store, Event("PARTIALLY_FILLED", originalQuantity: 0.01m, cumulativeQuantity: 0.011m), cancellationToken: TestContext.Current.CancellationToken));

        var record = Assert.Single(store.Records);
        Assert.Equal(0.006m, record.CumulativeExecutedQuantity);
    }

    [Fact]
    public async Task CumulativeExecutionRegressionFailsClosedWithoutChangingProjection()
    {
        var store = new RecordingStore();
        await AccountStreamOrderProjection.ApplyAsync(store, Event("PARTIALLY_FILLED", originalQuantity: 0.01m, cumulativeQuantity: 0.006m), cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AccountStreamOrderProjection.ApplyAsync(store, Event("PARTIALLY_FILLED", originalQuantity: 0.01m, cumulativeQuantity: 0.005m), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0.006m, Assert.Single(store.Records).CumulativeExecutedQuantity);
    }

    [Fact]
    public async Task FilledExecutionMustMatchOriginalQuantity()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AccountStreamOrderProjection.ApplyAsync(
                new RecordingStore(),
                Event("FILLED", originalQuantity: 0.01m, cumulativeQuantity: 0.009m), cancellationToken: TestContext.Current.CancellationToken));
    }

    private static BinanceExecutionReportEvent Event(
        string status,
        decimal? originalQuantity = 0.01m,
        decimal? cumulativeQuantity = null,
        long eventTimeMilliseconds = 1_700_000_000_123) => new(
        DateTimeOffset.UtcNow,
        eventTimeMilliseconds,
        "BTCUSDT",
        42,
        7,
        "client-42",
        "BUY",
        "LIMIT",
        status == "FILLED" ? "TRADE" : "NEW",
        status,
        status == "FILLED" ? 0.01m : 0m,
        cumulativeQuantity ?? (status == "FILLED" ? 0.01m : 0m),
        100.50m,
        0.001m,
        "USDT",
        originalQuantity);

    private sealed class RecordingStore : IAccountStreamOrderProjectionStore
    {
        private readonly Dictionary<string, (LocalOpenOrderRecord Record, bool Active)> records = new(StringComparer.Ordinal);

        public IReadOnlyList<LocalOpenOrderRecord> Records => records.Values
            .Where(static item => item.Active)
            .Select(static item => item.Record)
            .ToArray();

        public Task<IReadOnlyList<LocalOpenOrderRecord>> ReadOpenOrdersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Records);

        public Task<bool> ApplyAccountStreamExecutionReportAsync(
            LocalOpenOrderRecord order,
            bool isTerminal,
            CancellationToken cancellationToken = default)
        {
            var observedExchangeUpdate = order.ExchangeUpdatedAtUtc ?? order.UpdatedAtUtc;
            if (records.TryGetValue(order.ClientOrderId, out var existing) &&
                (!existing.Active || observedExchangeUpdate < (existing.Record.ExchangeUpdatedAtUtc ?? existing.Record.UpdatedAtUtc) ||
                 (observedExchangeUpdate == (existing.Record.ExchangeUpdatedAtUtc ?? existing.Record.UpdatedAtUtc) &&
                  !string.Equals(order.Status, existing.Record.Status, StringComparison.OrdinalIgnoreCase) &&
                  !isTerminal &&
                  (order.CumulativeExecutedQuantity ?? 0m) <= (existing.Record.CumulativeExecutedQuantity ?? 0m))))
            {
                return Task.FromResult(false);
            }

            records[order.ClientOrderId] = (order, !isTerminal);
            return Task.FromResult(true);
        }

        public Task<bool> ApplyAuthoritativeOpenOrderSnapshotAsync(
            LocalOpenOrderRecord order,
            CancellationToken cancellationToken = default)
        {
            if (!records.TryGetValue(order.ClientOrderId, out var existing) || !existing.Active ||
                order.ExchangeUpdatedAtUtc is not { } exchangeUpdatedAtUtc ||
                exchangeUpdatedAtUtc < (existing.Record.ExchangeUpdatedAtUtc ?? existing.Record.UpdatedAtUtc))
            {
                return Task.FromResult(false);
            }

            records[order.ClientOrderId] = (order, true);
            return Task.FromResult(true);
        }

        public Task UpsertOpenOrderAsync(LocalOpenOrderRecord order, CancellationToken cancellationToken = default)
        {
            records[order.ClientOrderId] = (order, true);
            return Task.CompletedTask;
        }

        public Task RemoveOpenOrderAsync(string clientOrderId, CancellationToken cancellationToken = default)
        {
            if (records.TryGetValue(clientOrderId, out var existing))
            {
                records[clientOrderId] = (existing.Record, false);
            }
            return Task.CompletedTask;
        }
    }
}
