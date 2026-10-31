using System.Buffers;

namespace CryptoTrading.Exchange.Binance;

public enum BinanceOrderBookStatus
{
    Invalid,
    Healthy
}

public enum BinanceOrderBookApplyDisposition
{
    SnapshotApplied,
    DeltaApplied,
    DuplicateQuarantined,
    OutOfOrderQuarantined,
    GapDetected,
    InvalidQuarantined,
    ResyncRequired
}

public enum BinanceOrderBookInvalidReason
{
    NoSnapshot,
    InvalidSnapshot,
    Duplicate,
    OutOfOrder,
    SequenceGap,
    InvalidLevel,
    SymbolMismatch,
    CapacityExceeded,
    InvalidBookState
}

public sealed record BinanceOrderBookApplyResult(
    BinanceOrderBookApplyDisposition Disposition,
    BinanceOrderBookStatus Status,
    long? LastUpdateId,
    BinanceOrderBookInvalidReason? InvalidReason = null)
{
    public bool ResyncRequired => Status is BinanceOrderBookStatus.Invalid;

}

public sealed record BinanceOrderBookQuarantineEntry(
    long? FirstUpdateId,
    long? FinalUpdateId,
    BinanceOrderBookInvalidReason Reason);

public readonly record struct BinanceOrderBookDepthMetrics(decimal? Imbalance, decimal? LiquidityNotional);

/// <summary>
/// Applies Binance Spot depth snapshots and diff events with sequence validation.
/// A healthy book is never silently advanced across a gap; callers must fetch a
/// fresh REST snapshot and call <see cref="ApplySnapshot"/> before resuming.
/// </summary>
public sealed class BinanceLocalOrderBook
{
    private const int DefaultQuarantineCapacity = 128;
    private readonly int maxLevels;
    private readonly int quarantineCapacity;
    private readonly Dictionary<decimal, decimal> bids = new();
    private readonly Dictionary<decimal, decimal> asks = new();
    private readonly Queue<BinanceOrderBookQuarantineEntry> quarantine = new();
    private bool hasAppliedDelta;
    private System.Collections.ObjectModel.ReadOnlyCollection<BinanceOrderBookLevel>? cachedBids;
    private System.Collections.ObjectModel.ReadOnlyCollection<BinanceOrderBookLevel>? cachedAsks;
    private int cachedMetricLevels;
    private BinanceOrderBookDepthMetrics cachedMetrics;
    private static readonly Comparison<BinanceOrderBookLevel> BidComparison = static (a, b) => b.Price.CompareTo(a.Price);
    private static readonly Comparison<BinanceOrderBookLevel> AskComparison = static (a, b) => a.Price.CompareTo(b.Price);

    private readonly record struct DepthLevel(decimal Price, decimal Quantity) : IComparable<DepthLevel>
    {
        public int CompareTo(DepthLevel other) => Price.CompareTo(other.Price);
    }

    public BinanceLocalOrderBook(
        string symbol,
        int maxLevels = 1000,
        int quarantineCapacity = DefaultQuarantineCapacity)
    {
        Symbol = BinanceMarketSubscription.NormalizeSymbol(symbol);
        if (maxLevels is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLevels), maxLevels, "Maximum levels must be positive.");
        }

        if (quarantineCapacity is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quarantineCapacity), quarantineCapacity, "Quarantine capacity must be positive.");
        }

        this.maxLevels = maxLevels;
        this.quarantineCapacity = quarantineCapacity;
    }

    public string Symbol { get; }

    public BinanceOrderBookStatus Status { get; private set; } = BinanceOrderBookStatus.Invalid;

    public bool ResyncRequired => Status is BinanceOrderBookStatus.Invalid;

    /// <summary>True only after a valid snapshot has been bridged by a contiguous stream delta.</summary>
    public bool IsSynchronized => Status is BinanceOrderBookStatus.Healthy && hasAppliedDelta;

    public void InvalidateForStreamDisconnect()
    {
        Status = BinanceOrderBookStatus.Invalid;
        hasAppliedDelta = false;
        ClearDerivedState();
    }

    public long? LastUpdateId { get; private set; }

    public IReadOnlyList<BinanceOrderBookLevel> Bids => !IsSynchronized ? [] : cachedBids ??= SnapshotSide(bids, true);

    public IReadOnlyList<BinanceOrderBookLevel> Asks => !IsSynchronized ? [] : cachedAsks ??= SnapshotSide(asks, false);

    public IReadOnlyCollection<BinanceOrderBookQuarantineEntry> Quarantined => quarantine.ToArray();

    /// <summary>
    /// Computes the volume-weighted depth imbalance across the top <paramref name="levels"/> of the local L2 book.
    /// Returns <see langword="null"/> if the book is invalid (e.g. sequence gap / resync required) or empty.
    /// </summary>
    public decimal? CalculateTopDepthImbalance(int levels = 5) => CalculateTopDepthMetrics(levels).Imbalance;

    /// <summary>
    /// Computes the conservative available notional liquidity (minimum of cumulative bid notional and cumulative ask notional)
    /// across the top <paramref name="levels"/> of the local L2 book.
    /// Returns <see langword="null"/> if the book is invalid or empty.
    /// </summary>
    public decimal? CalculateTopDepthLiquidityNotional(int levels = 5) => CalculateTopDepthMetrics(levels).LiquidityNotional;

    public BinanceOrderBookDepthMetrics CalculateTopDepthMetrics(int levels = 5)
    {
        if (!IsSynchronized || levels <= 0 || bids.Count == 0 || asks.Count == 0) return default;
        if (cachedMetricLevels == levels) return cachedMetrics;
        try
        {
            var bid = SideTotals(bids, levels, true);
            var ask = SideTotals(asks, levels, false);
            var total = bid.Quantity + ask.Quantity;
            var liquidity = Math.Min(bid.Notional, ask.Notional);
            cachedMetrics = new(total > 0m ? Math.Clamp(Math.Round((bid.Quantity - ask.Quantity) / total, 4), -1m, 1m) : null,
                liquidity > 0m ? Math.Round(liquidity, 2) : null);
            cachedMetricLevels = levels;
            return cachedMetrics;
        }
        catch (OverflowException)
        {
            _ = Invalidate(BinanceOrderBookApplyDisposition.InvalidQuarantined, BinanceOrderBookInvalidReason.InvalidBookState,
                LastUpdateId, LastUpdateId, LastUpdateId);
            return default;
        }
    }

    private void ClearDerivedState()
    {
        cachedBids = null;
        cachedAsks = null;
        cachedMetricLevels = 0;
        cachedMetrics = default;
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<BinanceOrderBookLevel> SnapshotSide(Dictionary<decimal, decimal> side, bool isBid)
    {
        var levels = new BinanceOrderBookLevel[side.Count];
        var index = 0;
        foreach (var pair in side) levels[index++] = new(pair.Key, pair.Value);
        Array.Sort(levels, isBid ? BidComparison : AskComparison);
        return Array.AsReadOnly(levels);
    }

    private static (decimal Quantity, decimal Notional) SideTotals(Dictionary<decimal, decimal> side, int levels, bool isBid)
    {
        decimal quantity = 0m, notional = 0m;
        if (levels >= side.Count)
        {
            foreach (var pair in side) { quantity += pair.Value; notional += pair.Key * pair.Value; }
            return (quantity, notional);
        }
        if (levels <= 32)
        {
            Span<DepthLevel> top = stackalloc DepthLevel[levels];
            var count = 0;
            foreach (var pair in side)
            {
                var position = 0;
                while (position < count && (isBid ? top[position].Price >= pair.Key : top[position].Price <= pair.Key)) position++;
                if (position == levels) continue;
                for (var j = Math.Min(count, levels - 1); j > position; j--) top[j] = top[j - 1];
                top[position] = new(pair.Key, pair.Value);
                count = Math.Min(count + 1, levels);
            }
            for (var i = 0; i < count; i++) { quantity += top[i].Quantity; notional += top[i].Price * top[i].Quantity; }
            return (quantity, notional);
        }
        var rented = ArrayPool<DepthLevel>.Shared.Rent(side.Count);
        try
        {
            var count = 0;
            foreach (var pair in side) rented[count++] = new(pair.Key, pair.Value);
            Array.Sort(rented, 0, count);
            var start = isBid ? count - levels : 0;
            for (var i = start; i < start + levels; i++) { quantity += rented[i].Quantity; notional += rented[i].Price * rented[i].Quantity; }
            return (quantity, notional);
        }
        finally { ArrayPool<DepthLevel>.Shared.Return(rented); }
    }

    /// <summary>
    /// Computes Cont-Kukanov-Stoikov (2014) normalized Temporal Order Flow Imbalance (OFI)
    /// across two consecutive best-bid/best-ask observations in [-1, 1].
    /// </summary>
    public static decimal ComputeTemporalOfi(
        decimal prevBidPrice,
        decimal prevBidQty,
        decimal prevAskPrice,
        decimal prevAskQty,
        decimal currBidPrice,
        decimal currBidQty,
        decimal currAskPrice,
        decimal currAskQty)
    {
        var deltaBid = (currBidPrice >= prevBidPrice ? currBidQty : 0m) -
                       (currBidPrice <= prevBidPrice ? prevBidQty : 0m);
        var deltaAsk = (currAskPrice <= prevAskPrice ? currAskQty : 0m) -
                       (currAskPrice >= prevAskPrice ? prevAskQty : 0m);
        var rawOfi = deltaBid - deltaAsk;
        var denom = prevBidQty + prevAskQty + currBidQty + currAskQty;
        return denom > 0m ? Math.Clamp(Math.Round(rawOfi / denom, 4), -1m, 1m) : 0m;
    }

    public BinanceOrderBookApplyResult ApplySnapshot(BinanceOrderBookSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!IsSymbol(snapshot.Symbol))
        {
            return Invalidate(BinanceOrderBookApplyDisposition.InvalidQuarantined, BinanceOrderBookInvalidReason.SymbolMismatch, null, null, null);
        }

        if (snapshot.LastUpdateId < 0 || !TryBuildLevels(snapshot.Bids, snapshot.Asks, out var nextBids, out var nextAsks))
        {
            return Invalidate(BinanceOrderBookApplyDisposition.InvalidQuarantined, BinanceOrderBookInvalidReason.InvalidSnapshot, null, null, null);
        }

        bids.Clear();
        asks.Clear();
        Copy(nextBids, bids);
        Copy(nextAsks, asks);
        ClearDerivedState();
        LastUpdateId = snapshot.LastUpdateId;
        hasAppliedDelta = false;
        Status = BinanceOrderBookStatus.Healthy;
        return new(BinanceOrderBookApplyDisposition.SnapshotApplied, Status, LastUpdateId);
    }

    public BinanceOrderBookApplyResult ApplyDelta(BinanceDepthUpdateEvent update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!IsSymbol(update.Symbol))
        {
            return Invalidate(BinanceOrderBookApplyDisposition.InvalidQuarantined, BinanceOrderBookInvalidReason.SymbolMismatch, update.FirstUpdateId, update.FinalUpdateId, update.FinalUpdateId);
        }

        if (Status is not BinanceOrderBookStatus.Healthy || LastUpdateId is null)
        {
            return Quarantine(BinanceOrderBookApplyDisposition.ResyncRequired, BinanceOrderBookInvalidReason.NoSnapshot, update);
        }

        if (update.FirstUpdateId < 0 || update.FinalUpdateId < update.FirstUpdateId || !TryValidateLevels(update.Bids) || !TryValidateLevels(update.Asks))
        {
            return Invalidate(BinanceOrderBookApplyDisposition.InvalidQuarantined, BinanceOrderBookInvalidReason.InvalidLevel, update.FirstUpdateId, update.FinalUpdateId, update.FinalUpdateId);
        }

        var current = LastUpdateId.Value;
        if (update.FinalUpdateId <= current)
        {
            return Quarantine(BinanceOrderBookApplyDisposition.DuplicateQuarantined, BinanceOrderBookInvalidReason.Duplicate, update);
        }

        var expected = checked(current + 1);
        var isInitialDelta = !hasAppliedDelta && update.FirstUpdateId <= expected && update.FinalUpdateId >= expected;
        var contiguous = update.FirstUpdateId == expected;
        var previousMatches = update.PreviousFinalUpdateId is null || update.PreviousFinalUpdateId == current;
        if (!isInitialDelta && (!contiguous || !previousMatches))
        {
            var reason = update.FirstUpdateId < expected ? BinanceOrderBookInvalidReason.OutOfOrder : BinanceOrderBookInvalidReason.SequenceGap;
            var disposition = reason is BinanceOrderBookInvalidReason.OutOfOrder
                ? BinanceOrderBookApplyDisposition.OutOfOrderQuarantined
                : BinanceOrderBookApplyDisposition.GapDetected;
            return Invalidate(disposition, reason, update.FirstUpdateId, update.FinalUpdateId, update.FinalUpdateId);
        }

        ClearDerivedState();
        ApplyLevels(update.Bids, bids);
        ApplyLevels(update.Asks, asks);
        if (!HasValidSpread(bids, asks))
        {
            return Invalidate(BinanceOrderBookApplyDisposition.InvalidQuarantined, BinanceOrderBookInvalidReason.InvalidBookState,
                update.FirstUpdateId, update.FinalUpdateId, current);
        }
        LastUpdateId = update.FinalUpdateId;
        hasAppliedDelta = true;
        Status = BinanceOrderBookStatus.Healthy;
        return new(BinanceOrderBookApplyDisposition.DeltaApplied, Status, LastUpdateId);
    }

    private BinanceOrderBookApplyResult Quarantine(
        BinanceOrderBookApplyDisposition disposition,
        BinanceOrderBookInvalidReason reason,
        BinanceDepthUpdateEvent update)
    {
        AddQuarantine(new(update.FirstUpdateId, update.FinalUpdateId, reason));
        return new(disposition, Status, LastUpdateId, reason);
    }

    private BinanceOrderBookApplyResult Invalidate(
        BinanceOrderBookApplyDisposition disposition,
        BinanceOrderBookInvalidReason reason,
        long? firstUpdateId,
        long? finalUpdateId,
        long? lastUpdateId)
    {
        if (firstUpdateId is not null || finalUpdateId is not null)
        {
            AddQuarantine(new(firstUpdateId, finalUpdateId, reason));
        }

        Status = BinanceOrderBookStatus.Invalid;
        ClearDerivedState();
        return new(disposition, Status, lastUpdateId ?? LastUpdateId, reason);
    }

    private bool IsSymbol(string symbol) =>
        string.Equals(Symbol, BinanceMarketSubscription.NormalizeSymbol(symbol), StringComparison.Ordinal);

    private bool TryBuildLevels(
        IReadOnlyList<BinanceOrderBookLevel> sourceBids,
        IReadOnlyList<BinanceOrderBookLevel> sourceAsks,
        out Dictionary<decimal, decimal> nextBids,
        out Dictionary<decimal, decimal> nextAsks)
    {
        nextBids = new();
        nextAsks = new();
        if (!TryLoadSnapshotSide(sourceBids, nextBids) || !TryLoadSnapshotSide(sourceAsks, nextAsks))
        {
            return false;
        }

        if (!HasValidSpread(nextBids, nextAsks))
        {
            return false;
        }

        Prune(nextBids, isBid: true);
        Prune(nextAsks, isBid: false);
        return true;
    }

    private static bool HasValidSpread(Dictionary<decimal, decimal> bidLevels, Dictionary<decimal, decimal> askLevels)
    {
        if (bidLevels.Count == 0 || askLevels.Count == 0) return false;
        var bestBid = decimal.MinValue;
        var bestAsk = decimal.MaxValue;
        foreach (var price in bidLevels.Keys) bestBid = Math.Max(bestBid, price);
        foreach (var price in askLevels.Keys) bestAsk = Math.Min(bestAsk, price);
        return bestBid < bestAsk;
    }

    private static bool TryLoadSnapshotSide(IReadOnlyList<BinanceOrderBookLevel> levels, Dictionary<decimal, decimal> destination)
    {
        if (!TryValidateLevels(levels) || levels.Any(static level => level.Quantity == 0m))
        {
            return false;
        }

        foreach (var level in levels)
        {
            if (!destination.TryAdd(level.Price, level.Quantity))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateLevels(IReadOnlyList<BinanceOrderBookLevel> levels) =>
        levels.All(level => level.Price > 0 && level.Quantity >= 0);

    private void ApplyLevels(IReadOnlyList<BinanceOrderBookLevel> levels, Dictionary<decimal, decimal> destination)
    {
        foreach (var level in levels)
        {
            if (level.Quantity is 0)
            {
                destination.Remove(level.Price);
            }
            else
            {
                destination[level.Price] = level.Quantity;
            }
        }

        Prune(destination, ReferenceEquals(destination, bids));
    }

    private void Prune(Dictionary<decimal, decimal> levels, bool isBid)
    {
        var excess = levels.Count - maxLevels;
        if (excess <= 0)
        {
            return;
        }

        if (excess <= 8)
        {
            for (var i = 0; i < excess; i++)
            {
                var worst = isBid ? decimal.MaxValue : decimal.MinValue;
                foreach (var pair in levels) worst = isBid ? Math.Min(worst, pair.Key) : Math.Max(worst, pair.Key);
                levels.Remove(worst);
            }
            return;
        }
        var rented = ArrayPool<decimal>.Shared.Rent(levels.Count);
        try
        {
            var count = 0;
            foreach (var pair in levels) rented[count++] = pair.Key;
            Array.Sort(rented, 0, count);
            var start = isBid ? 0 : count - excess;
            for (var i = start; i < start + excess; i++) levels.Remove(rented[i]);
        }
        finally { ArrayPool<decimal>.Shared.Return(rented); }
    }

    private void AddQuarantine(BinanceOrderBookQuarantineEntry entry)
    {
        if (quarantine.Count == quarantineCapacity)
        {
            quarantine.Dequeue();
        }

        quarantine.Enqueue(entry);
    }

    private static void Copy(Dictionary<decimal, decimal> source, Dictionary<decimal, decimal> destination)
    {
        foreach (var pair in source)
        {
            destination.Add(pair.Key, pair.Value);
        }
    }
}
