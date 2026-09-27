using CryptoTrading.Domain.Primitives;

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
    decimal FeesPaidQuote);

public sealed record AggregatedPortfolioPerformanceMetrics(
    int TotalTradesCount,
    int TotalWinningCount,
    int TotalLosingCount,
    decimal TotalRealizedPnlQuote,
    decimal TotalFeesPaidQuote,
    decimal TotalGrossProfitQuote,
    decimal TotalGrossLossQuote,
    IReadOnlyList<PerformancePeriodMetrics> Periods,
    PositionCostBasisRecord? BtcPositionBasis);

public interface IPortfolioPerformanceStore
{
    Task<AggregatedPortfolioPerformanceMetrics> GetPerformanceMetricsAsync(CancellationToken cancellationToken = default);
}
