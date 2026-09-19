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
    CapacityExceeded
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

    public long? LastUpdateId { get; private set; }

    public IReadOnlyList<BinanceOrderBookLevel> Bids =>
        bids.OrderByDescending(pair => pair.Key)
            .Select(pair => new BinanceOrderBookLevel(pair.Key, pair.Value))
            .ToArray();

    public IReadOnlyList<BinanceOrderBookLevel> Asks =>
        asks.OrderBy(pair => pair.Key)
            .Select(pair => new BinanceOrderBookLevel(pair.Key, pair.Value))
            .ToArray();

    public IReadOnlyCollection<BinanceOrderBookQuarantineEntry> Quarantined => quarantine.ToArray();

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

        ApplyLevels(update.Bids, bids);
        ApplyLevels(update.Asks, asks);
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

        Prune(nextBids, isBid: true);
        Prune(nextAsks, isBid: false);
        return true;
    }

    private static bool TryLoadSnapshotSide(IReadOnlyList<BinanceOrderBookLevel> levels, Dictionary<decimal, decimal> destination)
    {
        if (!TryValidateLevels(levels))
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

        var toRemove = (isBid
                ? levels.Keys.OrderBy(price => price)
                : levels.Keys.OrderByDescending(price => price))
            .Take(excess)
            .ToArray();
        foreach (var price in toRemove)
        {
            levels.Remove(price);
        }
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
