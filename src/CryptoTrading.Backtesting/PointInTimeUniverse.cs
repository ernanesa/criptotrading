using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Backtesting;

/// <summary>Lineage and lifecycle metadata for an asset in point-in-time universe construction.</summary>
public sealed record PointInTimeAssetMetadata
{
    public PointInTimeAssetMetadata(
        Symbol symbol,
        DateTimeOffset listingDateUtc,
        DateTimeOffset? delistingDateUtc = null,
        decimal? delistingLiquidationPrice = null)
    {
        if (listingDateUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Listing timestamp must be UTC.", nameof(listingDateUtc));
        }

        if (delistingDateUtc.HasValue)
        {
            if (delistingDateUtc.Value.Offset != TimeSpan.Zero)
            {
                throw new ArgumentException("Delisting timestamp must be UTC.", nameof(delistingDateUtc));
            }

            if (delistingDateUtc.Value <= listingDateUtc)
            {
                throw new ArgumentException("Delisting date must be strictly after listing date.", nameof(delistingDateUtc));
            }
        }

        if (delistingLiquidationPrice.HasValue && delistingLiquidationPrice.Value < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(delistingLiquidationPrice), "Delisting liquidation price cannot be negative.");
        }

        Symbol = symbol;
        ListingDateUtc = listingDateUtc;
        DelistingDateUtc = delistingDateUtc;
        DelistingLiquidationPrice = delistingLiquidationPrice;
    }

    public Symbol Symbol { get; }
    public DateTimeOffset ListingDateUtc { get; }
    public DateTimeOffset? DelistingDateUtc { get; }
    public decimal? DelistingLiquidationPrice { get; }
}

/// <summary>Point-in-time snapshot of the tradeable universe at a historical moment.</summary>
public sealed record PointInTimeUniverseSnapshot(
    DateTimeOffset SnapshotTimeUtc,
    IReadOnlyList<Symbol> ActiveSymbols,
    IReadOnlyList<Symbol> DelistedSymbols,
    IReadOnlyList<Symbol> PendingListingSymbols);

/// <summary>Forced liquidation event generated when an asset reaches its delisting timestamp.</summary>
public sealed record ForcedLiquidationEvent(
    Symbol Symbol,
    DateTimeOffset DelistedAtUtc,
    decimal PositionQuantity,
    decimal LiquidationPrice,
    decimal RealizedLiquidationNotional,
    string Reason);

/// <summary>
/// Engine enforcing survivorship-free universe filtering, point-in-time asset visibility,
/// and forced liquidation upon delisting to prevent survivorship bias.
/// </summary>
public sealed class PointInTimeUniverseEngine
{
    private readonly Dictionary<Symbol, PointInTimeAssetMetadata> _assets;

    public PointInTimeUniverseEngine(IEnumerable<PointInTimeAssetMetadata> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        _assets = assets.ToDictionary(asset => asset.Symbol);
        if (_assets.Count == 0)
        {
            throw new ArgumentException("At least one asset is required in the universe.", nameof(assets));
        }
    }

    public PointInTimeUniverseSnapshot GetUniverseAt(DateTimeOffset timestampUtc)
    {
        if (timestampUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC.", nameof(timestampUtc));
        }

        var active = new List<Symbol>();
        var delisted = new List<Symbol>();
        var pending = new List<Symbol>();

        foreach (var (_, asset) in _assets)
        {
            if (timestampUtc < asset.ListingDateUtc)
            {
                pending.Add(asset.Symbol);
            }
            else if (asset.DelistingDateUtc.HasValue && timestampUtc >= asset.DelistingDateUtc.Value)
            {
                delisted.Add(asset.Symbol);
            }
            else
            {
                active.Add(asset.Symbol);
            }
        }

        return new PointInTimeUniverseSnapshot(timestampUtc, active, delisted, pending);
    }

    public bool IsActiveAt(Symbol symbol, DateTimeOffset timestampUtc)
    {
        if (!_assets.TryGetValue(symbol, out var asset))
        {
            return false;
        }

        if (timestampUtc < asset.ListingDateUtc)
        {
            return false;
        }

        if (asset.DelistingDateUtc.HasValue && timestampUtc >= asset.DelistingDateUtc.Value)
        {
            return false;
        }

        return true;
    }

    public IReadOnlyList<CloseBar> FilterBars(IReadOnlyList<CloseBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        return bars.Where(bar => IsActiveAt(bar.Symbol, bar.CloseTimeUtc)).ToArray();
    }

    public IReadOnlyList<ResearchBar> FilterResearchBars(IReadOnlyList<ResearchBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        return bars.Where(bar => IsActiveAt(bar.Symbol, bar.CloseTimeUtc)).ToArray();
    }

    public ForcedLiquidationEvent? EvaluateForcedLiquidation(
        Symbol symbol,
        DateTimeOffset currentBarTimeUtc,
        decimal currentPosition,
        decimal currentClosePrice)
    {
        if (currentPosition <= 0m || !_assets.TryGetValue(symbol, out var asset))
        {
            return null;
        }

        if (asset.DelistingDateUtc.HasValue && currentBarTimeUtc >= asset.DelistingDateUtc.Value)
        {
            var liquidationPrice = asset.DelistingLiquidationPrice ?? currentClosePrice;
            var notional = currentPosition * liquidationPrice;
            return new ForcedLiquidationEvent(
                symbol,
                asset.DelistingDateUtc.Value,
                currentPosition,
                liquidationPrice,
                notional,
                "Forced liquidation due to symbol delisting.");
        }

        return null;
    }
}

