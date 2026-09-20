using CryptoTrading.Exchange.Binance;
using CryptoTrading.Persistence;

namespace CryptoTrading.Worker;

/// <summary>Projects durable Binance execution reports into the local open-order read model.</summary>
internal static class AccountStreamOrderProjection
{
    public static Task ApplyAsync(
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

        var status = execution.OrderStatus.Trim().ToUpperInvariant();
        return status switch
        {
            "NEW" or "PARTIALLY_FILLED" => store.UpsertOpenOrderAsync(
                new LocalOpenOrderRecord(
                    execution.Symbol,
                    execution.OrderId,
                    execution.ClientOrderId,
                    status,
                    ToUtc(execution.EventTimeMilliseconds)),
                cancellationToken),
            "FILLED" or "CANCELED" or "CANCELLED" or "REJECTED" or "EXPIRED" =>
                store.RemoveOpenOrderAsync(execution.ClientOrderId, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported Binance execution order status '{execution.OrderStatus}'.")
        };
    }

    private static DateTimeOffset ToUtc(long milliseconds) =>
        milliseconds < 0
            ? throw new InvalidOperationException("Execution report event time cannot be negative.")
            : DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
}
