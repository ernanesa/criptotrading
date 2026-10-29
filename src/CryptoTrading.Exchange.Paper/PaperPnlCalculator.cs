using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Orders;

namespace CryptoTrading.Exchange.Paper;

/// <summary>Realized result of a sequence of paper fills for a single symbol.</summary>
/// <param name="RealizedPnlQuote">
/// Sum of realized PnL after fees. Commissions are charged in the quote asset by
/// the paper exchange, so no third-asset valuation is involved.
/// </param>
/// <param name="FeesPaidQuote">Total fees charged by the simulated exchange.</param>
/// <param name="OpenQuantity">Base quantity still held at the end.</param>
/// <param name="OpenCostQuote">Cost basis of the open quantity.</param>
/// <param name="ClosedFillCount">Number of sell fills that realized PnL.</param>
public sealed record PaperPnlSummary(
    decimal RealizedPnlQuote,
    decimal FeesPaidQuote,
    decimal OpenQuantity,
    decimal OpenCostQuote,
    int ClosedFillCount);

/// <summary>
/// Computes realized PnL for paper fills using the exact average-cost arithmetic
/// used by live accounting. It is deliberately separate from the durable ledger:
/// paper fills cannot be written to <c>execution_fills</c> because their order
/// identity is a GUID, not the venue's numeric order ID.
/// </summary>
public static class PaperPnlCalculator
{
    public static PaperPnlSummary Calculate(IEnumerable<PaperFill> fills, string quoteAsset = "USDT")
    {
        ArgumentNullException.ThrowIfNull(fills);
        ArgumentException.ThrowIfNullOrWhiteSpace(quoteAsset);

        var quantity = 0m;
        var cost = 0m;
        var realized = 0m;
        var fees = 0m;
        var closed = 0;
        var expectedSequence = 1L;
        string? symbol = null;

        foreach (var fill in fills.OrderBy(static fill => fill.Sequence))
        {
            if (fill.Sequence != expectedSequence)
            {
                throw new InvalidOperationException("Paper PnL requires a complete, unique fill sequence beginning at one.");
            }
            expectedSequence = checked(expectedSequence + 1);
            if (symbol is null)
            {
                symbol = fill.Symbol.Value;
            }
            else if (!string.Equals(symbol, fill.Symbol.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Paper PnL cannot combine fills for different symbols.");
            }

            if (!string.Equals(fill.Fee.Asset.Value, quoteAsset, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Paper fills must charge fees in the quote asset; third-asset fees need an explicit valuation.");
            }

            fees += fill.Fee.Amount;
            var update = ExecutionAccounting.ApplyAverageCostCore(
                fill.Side,
                fill.Quantity.Value,
                fill.Price.Value,
                commissionInBase: 0m,
                commissionInQuote: fill.Fee.Amount,
                currentQuantity: quantity,
                currentCostQuote: cost);

            quantity = update.Quantity;
            cost = update.CostQuote;
            if (update.RealizedPnlQuote is { } fillRealized)
            {
                realized += fillRealized;
                closed++;
            }
        }

        return new PaperPnlSummary(realized, fees, quantity, cost, closed);
    }
}
