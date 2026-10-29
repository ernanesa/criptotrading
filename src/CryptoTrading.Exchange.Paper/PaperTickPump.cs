namespace CryptoTrading.Exchange.Paper;

/// <summary>
/// Drives a <see cref="IPaperExchange"/> with market ticks and keeps the fills it
/// produces. Without a pump, submitted paper orders stay open forever because the
/// exchange only fills when a tick arrives.
/// </summary>
public sealed class PaperTickPump(IPaperExchange exchange)
{
    private readonly List<PaperFill> fills = [];
    private readonly Lock sync = new();

    /// <summary>Fills observed so far, in application order.</summary>
    public IReadOnlyList<PaperFill> Fills
    {
        get
        {
            lock (sync)
            {
                return Array.AsReadOnly(fills.ToArray());
            }
        }
    }

    /// <summary>
    /// Applies one market tick and records any resulting fills.
    /// Returns the number of new fills.
    /// </summary>
    public int Apply(PaperMarketTick tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        // The exchange must not invoke this pump while its own lock is held.
        lock (sync)
        {
            var produced = exchange.Apply(tick);
            fills.AddRange(produced);
            return produced.Count;
        }
    }

    /// <summary>
    /// Realized result of the recorded fills, or null when nothing filled.
    /// Null means "no execution", never "zero PnL".
    /// </summary>
    public PaperPnlSummary? Summarize(string quoteAsset = "USDT")
    {
        PaperFill[] snapshot;
        lock (sync)
        {
            snapshot = fills.ToArray();
        }

        return snapshot.Length == 0 ? null : PaperPnlCalculator.Calculate(snapshot, quoteAsset);
    }
}
