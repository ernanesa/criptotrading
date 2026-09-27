#pragma warning disable CA2007 // Npgsql async disposal/commands are intentionally awaited with ConfigureAwait(false) at each I/O boundary.
#pragma warning disable CA2100 // Constant query strings with parameterized values.

using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace CryptoTrading.Persistence;

public sealed partial class PostgresTradingStore : IPortfolioPerformanceStore
{
    public async Task<AggregatedPortfolioPerformanceMetrics> GetPerformanceMetricsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // 1. Position cost basis for BTCUSDT
        PositionCostBasisRecord? btcPositionBasis = null;
        await using (var basisCommand = new NpgsqlCommand(
            """
            SELECT symbol, base_asset, quote_asset, quantity, cost_quote, updated_at
            FROM trading.position_cost_basis
            WHERE symbol = 'BTCUSDT';
            """, connection))
        {
            await using var reader = await basisCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                btcPositionBasis = new PositionCostBasisRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetDecimal(3),
                    reader.GetDecimal(4),
                    reader.GetDateTime(5));
            }
        }

        // 2. Aggregated metrics across all execution fills
        int totalTrades = 0;
        int totalWinning = 0;
        int totalLosing = 0;
        decimal totalRealizedPnl = 0m;
        decimal totalFees = 0m;
        decimal totalGrossProfit = 0m;
        decimal totalGrossLoss = 0m;

        await using (var fillsCommand = new NpgsqlCommand(
            """
            SELECT 
                COUNT(*) as total_trades,
                COUNT(CASE WHEN realized_pnl_quote > 0 THEN 1 END) as win_count,
                COUNT(CASE WHEN realized_pnl_quote < 0 THEN 1 END) as loss_count,
                COALESCE(SUM(realized_pnl_quote), 0) as total_realized_pnl,
                COALESCE(SUM(commission), 0) as total_fees,
                COALESCE(SUM(CASE WHEN realized_pnl_quote > 0 THEN realized_pnl_quote ELSE 0 END), 0) as gross_profit,
                COALESCE(SUM(CASE WHEN realized_pnl_quote < 0 THEN -realized_pnl_quote ELSE 0 END), 0) as gross_loss
            FROM trading.execution_fills;
            """, connection))
        {
            await using var reader = await fillsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                totalTrades = reader.GetInt32(0);
                totalWinning = reader.GetInt32(1);
                totalLosing = reader.GetInt32(2);
                totalRealizedPnl = reader.GetDecimal(3);
                totalFees = reader.GetDecimal(4);
                totalGrossProfit = reader.GetDecimal(5);
                totalGrossLoss = reader.GetDecimal(6);
            }
        }

        // 3. Periodic metrics (Hoje, Semana, Mês, Ano)
        var periods = new List<PerformancePeriodMetrics>();
        await using (var periodsCommand = new NpgsqlCommand(
            """
            SELECT 
                'Hoje (Dia / 24 Horas)' as period_name,
                1 as sort_order,
                COUNT(*) as trades_count,
                COUNT(CASE WHEN realized_pnl_quote > 0 THEN 1 END) as win_count,
                COUNT(CASE WHEN realized_pnl_quote < 0 THEN 1 END) as loss_count,
                COALESCE(SUM(realized_pnl_quote), 0) as realized_pnl,
                COALESCE(SUM(commission), 0) as fees_paid
            FROM trading.execution_fills
            WHERE occurred_at >= DATE_TRUNC('day', NOW() AT TIME ZONE 'UTC')
            UNION ALL
            SELECT 
                'Semana (Últimos 7 Dias)',
                2,
                COUNT(*),
                COUNT(CASE WHEN realized_pnl_quote > 0 THEN 1 END),
                COUNT(CASE WHEN realized_pnl_quote < 0 THEN 1 END),
                COALESCE(SUM(realized_pnl_quote), 0),
                COALESCE(SUM(commission), 0)
            FROM trading.execution_fills
            WHERE occurred_at >= NOW() - INTERVAL '7 days'
            UNION ALL
            SELECT 
                'Mês (Últimos 30 Dias)',
                3,
                COUNT(*),
                COUNT(CASE WHEN realized_pnl_quote > 0 THEN 1 END),
                COUNT(CASE WHEN realized_pnl_quote < 0 THEN 1 END),
                COALESCE(SUM(realized_pnl_quote), 0),
                COALESCE(SUM(commission), 0)
            FROM trading.execution_fills
            WHERE occurred_at >= NOW() - INTERVAL '30 days'
            UNION ALL
            SELECT 
                'Ano (Acumulado / YTD)',
                4,
                COUNT(*),
                COUNT(CASE WHEN realized_pnl_quote > 0 THEN 1 END),
                COUNT(CASE WHEN realized_pnl_quote < 0 THEN 1 END),
                COALESCE(SUM(realized_pnl_quote), 0),
                COALESCE(SUM(commission), 0)
            FROM trading.execution_fills
            WHERE occurred_at >= DATE_TRUNC('year', NOW() AT TIME ZONE 'UTC')
            ORDER BY sort_order;
            """, connection))
        {
            await using var reader = await periodsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                periods.Add(new PerformancePeriodMetrics(
                    reader.GetString(0),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetDecimal(5),
                    reader.GetDecimal(6)));
            }
        }

        return new AggregatedPortfolioPerformanceMetrics(
            totalTrades,
            totalWinning,
            totalLosing,
            totalRealizedPnl,
            totalFees,
            totalGrossProfit,
            totalGrossLoss,
            periods,
            btcPositionBasis);
    }
}
