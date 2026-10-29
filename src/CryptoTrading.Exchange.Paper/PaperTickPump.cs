namespace CryptoTrading.Exchange.Paper;

/// <summary>
/// Drives a <see cref="IPaperExchange"/> with market ticks and keeps the fills it
/// produces. Without a pump, submitted paper orders stay open forever because the
/// exchange only fills when a tick arrives.
/// </summary>
public sealed class PaperTickPump(IPaperExchange exchange)
{
    private readonly List<PaperFill> fills = [];

    /// <summary>Fills observed so far, in application order.</summary>
    public IReadOnlyList<PaperFill> Fills => fills;

    /// <summary>
    /// Applies one market tick and records any resulting fills.
    /// Returns the number of new fills.
    /// </summary>
    public int Apply(PaperMarketTick tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        var produced = exchange.Apply(tick);
        if (produced.Count > 0)
        {
            fills.AddRange(produced);
        }

        return produced.Count;
    }

    /// <summary>
    /// Realized result of the recorded fills, or null when nothing filled.
    /// Null means "no execution", never "zero PnL".
    /// </summary>
    public PaperPnlSummary? Summarize(string quoteAsset = "USDT") =>
        fills.Count == 0 ? null : PaperPnlCalculator.Calculate(fills, quoteAsset);
}
