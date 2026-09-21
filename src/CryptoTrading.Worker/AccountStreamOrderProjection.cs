using CryptoTrading.Exchange.Binance;
using CryptoTrading.Persistence;

namespace CryptoTrading.Worker;

/// <summary>Projects durable Binance execution reports into the local open-order read model.</summary>
internal static class AccountStreamOrderProjection
{
    public static async Task ApplyAsync(
        ILocalOpenOrderStore store,
        BinanceExecutionReportEvent execution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(execution);
        if (execution.OrderId < 0 || string.IsNullOrWhiteSpace(execution.ClientOrderId))
        {
            throw new InvalidOperationException("Execution report has invalid order identity.");
        }

        ValidateQuantities(execution);
        var existing = (await store.ReadOpenOrdersAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(order => string.Equals(order.ClientOrderId, execution.ClientOrderId, StringComparison.Ordinal));
        ValidateMonotonicity(existing, execution);

        var status = execution.OrderStatus.Trim().ToUpperInvariant();
        await (status switch
        {
            "NEW" or "PARTIALLY_FILLED" or "PENDING_CANCEL" => store.UpsertOpenOrderAsync(
                new LocalOpenOrderRecord(
                    execution.Symbol,
                    execution.OrderId,
                    execution.ClientOrderId,
                    status,
                    ToUtc(execution.EventTimeMilliseconds),
                    execution.OriginalQuantity,
                    execution.CumulativeExecutedQuantity),
                cancellationToken),
            "FILLED" or "CANCELED" or "CANCELLED" or "REJECTED" or "EXPIRED" =>
                store.RemoveOpenOrderAsync(execution.ClientOrderId, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported Binance execution order status '{execution.OrderStatus}'."),
        }).ConfigureAwait(false);
    }

    private static void ValidateQuantities(BinanceExecutionReportEvent execution)
    {
        if (execution.LastExecutedQuantity < 0 || execution.CumulativeExecutedQuantity < 0 ||
            execution.OriginalQuantity is <= 0 ||
            (execution.OriginalQuantity is { } original && execution.CumulativeExecutedQuantity > original))
        {
            throw new InvalidOperationException("Execution report quantities are invalid or exceed the original order quantity.");
        }

        if (string.Equals(execution.OrderStatus, "FILLED", StringComparison.OrdinalIgnoreCase) &&
            execution.OriginalQuantity is { } target && execution.CumulativeExecutedQuantity != target)
        {
            throw new InvalidOperationException("Filled execution report cumulative quantity does not equal its original order quantity.");
        }
    }

    private static void ValidateMonotonicity(LocalOpenOrderRecord? existing, BinanceExecutionReportEvent execution)
    {
        if (existing is null)
        {
            return;
        }

        if (!string.Equals(existing.Symbol, execution.Symbol, StringComparison.OrdinalIgnoreCase) ||
            (existing.OrderId.HasValue && existing.OrderId != execution.OrderId) ||
            (existing.OriginalQuantity.HasValue && execution.OriginalQuantity.HasValue && existing.OriginalQuantity != execution.OriginalQuantity) ||
            (existing.CumulativeExecutedQuantity.HasValue && existing.CumulativeExecutedQuantity > execution.CumulativeExecutedQuantity))
        {
            throw new InvalidOperationException("Execution report conflicts with the durable open-order projection.");
        }
    }

    private static DateTimeOffset ToUtc(long milliseconds) =>
        milliseconds < 0
            ? throw new InvalidOperationException("Execution report event time cannot be negative.")
            : DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
}
