namespace CryptoTrading.Persistence;

public sealed record PositionCostBasisRecord(
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    decimal Quantity,
    decimal CostQuote,
    DateTimeOffset UpdatedAtUtc);

public sealed record PerformancePeriodMetrics(
    string PeriodName,
    int TradesCount,
    int WinningCount,
    int LosingCount,
    decimal RealizedPnlQuote,
    decimal FeesPaidQuote)
{
    public decimal? NetAssetValueDeltaQuote { get; init; }

    public decimal? UnrealizedPnlDeltaQuote { get; init; }

    public PerformanceSnapshotAvailability SnapshotAvailability { get; init; } = PerformanceSnapshotAvailability.MissingBaselineSnapshot;

    public decimal? RealisticNetProfitQuote { get; init; }

    public decimal? AverageReturnBps { get; init; }

    public decimal? AverageWinBps { get; init; }

    public decimal? AverageLossBps { get; init; }
}

public enum PerformanceSnapshotAvailability
{
    Available,
    MissingBaselineSnapshot,
    MissingLatestSnapshot,
    UnknownExternalCashFlow,
    UnknownUnrealizedPnl,
}

public sealed record AssetAmount(string Asset, decimal Amount);

/// <summary>Append-only portfolio valuation for one explicitly scoped quote currency.</summary>
public sealed record PortfolioNavSnapshot(
    string Symbol,
    string QuoteAsset,
    DateTimeOffset ObservedAtUtc,
    decimal NetAssetValueQuote,
    decimal? UnrealizedPnlQuote,
    decimal? ExternalCashFlowQuote,
    string Source);

public sealed record AggregatedPortfolioPerformanceMetrics(
    int TotalTradesCount,
    int TotalWinningCount,
    int TotalLosingCount,
    decimal TotalRealizedPnlQuote,
    decimal TotalFeesPaidQuote,
    decimal TotalGrossProfitQuote,
    decimal TotalGrossLossQuote,
    IReadOnlyList<PerformancePeriodMetrics> Periods,
    PositionCostBasisRecord? BtcPositionBasis)
{
    public string ScopeSymbol { get; init; } = "BTCUSDT";

    public PositionCostBasisRecord? PositionBasis { get; init; }

    public string QuoteAsset { get; init; } = "USDT";

    public DateTimeOffset AsOfUtc { get; init; }
    public DateTimeOffset? CurrentSnapshotObservedAtUtc { get; init; }

    public decimal? CurrentNetAssetValueQuote { get; init; }

    public decimal? CurrentUnrealizedPnlQuote { get; init; }

    public IReadOnlyList<AssetAmount> UnvaluedFeeAmounts { get; init; } = [];

    public bool FeesAreComplete => UnvaluedFeeAmounts.Count == 0;

    public decimal? RealisticNetPnlQuote { get; init; }

    public decimal? AverageReturnBps { get; init; }

    public decimal? AverageWinBps { get; init; }

    public decimal? AverageLossBps { get; init; }
}

public interface IPortfolioPerformanceStore
{
    Task<AggregatedPortfolioPerformanceMetrics> GetPerformanceMetricsAsync(CancellationToken cancellationToken = default);

    Task<AggregatedPortfolioPerformanceMetrics> GetPerformanceMetricsAsync(
        string? symbol,
        CancellationToken cancellationToken = default);
}

public interface IPortfolioNavSnapshotStore
{
    Task RecordNavSnapshotAsync(PortfolioNavSnapshot snapshot, CancellationToken cancellationToken = default);

    Task<AggregatedPortfolioPerformanceMetrics> GetPerformanceMetricsAsync(
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default);

    Task<AggregatedPortfolioPerformanceMetrics> GetPerformanceMetricsAsync(
        DateTimeOffset asOfUtc,
        string? symbol,
        CancellationToken cancellationToken = default);
}
