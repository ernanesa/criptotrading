using System.Globalization;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Persistence;

/// <summary>
/// Applies an OMS lifecycle transition and projects its current open-order state.
/// The projection is deliberately retryable: if persistence fails after the in-memory
/// transition, replaying the same transition returns the idempotent transition and
/// retries the projection without issuing exchange I/O.
/// </summary>
public sealed class OrderProjectionService
{
    private readonly ILocalOpenOrderStore store;
    private readonly IOrderLifecycleStore? lifecycleStore;

    public OrderProjectionService(ILocalOpenOrderStore store, IOrderLifecycleStore? lifecycleStore = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        this.lifecycleStore = lifecycleStore;
    }

    public async Task<OrderTransition> TransitionAndProjectAsync(
        Order order,
        OrderStatus target,
        IdempotencyKey idempotencyKey,
        DateTimeOffset occurredAtUtc,
        string? exchangeOrderId = null,
        bool projectBeforeSubmission = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        _ = ParseExchangeOrderId(exchangeOrderId);
        var transition = order.TransitionTo(target, idempotencyKey, occurredAtUtc, exchangeOrderId);
        await PersistLifecycleAsync(order, transition, cancellationToken).ConfigureAwait(false);
        await ProjectAsync(order, transition, projectBeforeSubmission, cancellationToken).ConfigureAwait(false);
        return transition;
    }

    /// <summary>Retries projection of the aggregate's current state after a persistence failure.</summary>
    public async Task ProjectCurrentAsync(
        Order order,
        bool projectBeforeSubmission = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        var transition = order.Transitions
            .OrderByDescending(static item => item.OccurredAtUtc)
            .First();
        await PersistLifecycleAsync(order, transition, cancellationToken).ConfigureAwait(false);
        await ProjectAsync(order, transition, projectBeforeSubmission, cancellationToken).ConfigureAwait(false);
    }

    private Task PersistLifecycleAsync(Order order, OrderTransition transition, CancellationToken cancellationToken) =>
        lifecycleStore?.AppendOrderLifecycleEventAsync(
            OrderLifecycleEvent.Create(order.ClientOrderId.Value, transition), cancellationToken)
        ?? Task.CompletedTask;

    private async Task ProjectAsync(
        Order order,
        OrderTransition transition,
        bool projectBeforeSubmission,
        CancellationToken cancellationToken)
    {
        if (!projectBeforeSubmission && order.Status is (OrderStatus.Created or OrderStatus.Validated))
        {
            return;
        }

        if (IsTerminal(order.Status))
        {
            await store.RemoveOpenOrderAsync(order.ClientOrderId.Value, cancellationToken).ConfigureAwait(false);
            return;
        }

        var exchangeOrderId = ParseExchangeOrderId(order.ExchangeOrderId ?? transition.ExchangeOrderId);
        var record = new LocalOpenOrderRecord(
            order.Symbol.Value,
            exchangeOrderId,
            order.ClientOrderId.Value,
            ToExchangeStatus(order.Status),
            order.UpdatedAtUtc);
        await store.UpsertOpenOrderAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private static string ToExchangeStatus(OrderStatus status) => status switch
    {
        OrderStatus.Created or OrderStatus.Validated or OrderStatus.Submitted or OrderStatus.Acknowledged => "NEW",
        OrderStatus.PartiallyFilled => "PARTIALLY_FILLED",
        OrderStatus.Unknown => "UNKNOWN",
        OrderStatus.CancelPending => "PENDING_CANCEL",
        _ => throw new InvalidOperationException($"Terminal order status '{status}' must be removed, not projected."),
    };

    private static long? ParseExchangeOrderId(string? exchangeOrderId)
    {
        if (string.IsNullOrWhiteSpace(exchangeOrderId))
        {
            return null;
        }

        if (!long.TryParse(exchangeOrderId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            throw new FormatException("Exchange order IDs projected to the local store must be non-negative integers.");
        }

        return parsed;
    }

    private static bool IsTerminal(OrderStatus status) => status is
        OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Expired;
}
