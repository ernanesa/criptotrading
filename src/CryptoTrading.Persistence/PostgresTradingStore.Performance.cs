#pragma warning disable CA2007
#pragma warning disable CA2100

using Npgsql;
using NpgsqlTypes;

namespace CryptoTrading.Persistence;

public sealed partial class PostgresTradingStore : IPortfolioPerformanceStore, IPortfolioNavSnapshotStore
{
    private const string PerformanceSymbol = "BTCUSDT";
    private const string PerformanceQuoteAsset = "USDT";

    public Task<AggregatedPortfolioPerformanceMetrics> GetPerformanceMetricsAsync(CancellationToken cancellationToken = default) =>
        GetPerformanceMetricsAsync(DateTimeOffset.UtcNow, null, cancellationToken);

    public Task<AggregatedPortfolioPerformanceMetrics> GetPerformanceMetricsAsync(string? symbol, CancellationToken cancellationToken = default) =>
        GetPerformanceMetricsAsync(DateTimeOffset.UtcNow, symbol, cancellationToken);

    public Task<AggregatedPortfolioPerformanceMetrics> GetPerformanceMetricsAsync(DateTimeOffset asOfUtc, CancellationToken cancellationToken = default) =>
        GetPerformanceMetricsAsync(asOfUtc, null, cancellationToken);

    public async Task<AggregatedPortfolioPerformanceMetrics> GetPerformanceMetricsAsync(DateTimeOffset asOfUtc, string? symbol, CancellationToken cancellationToken = default)
    {
        if (asOfUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Performance as-of timestamp must be UTC.", nameof(asOfUtc));

        string? targetSymbol = null;
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            targetSymbol = symbol.Trim().ToUpperInvariant();
            if (!AllowedUniverseSymbols.Contains(targetSymbol))
            {
                throw new ArgumentException($"Symbol '{symbol}' is not part of the allowed performance universe.", nameof(symbol));
            }
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var basis = await ReadPositionBasisAsync(connection, targetSymbol, cancellationToken).ConfigureAwait(false);
        var totals = await ReadFillMetricsAsync(connection, null, asOfUtc, targetSymbol, cancellationToken).ConfigureAwait(false);
        var unvaluedFees = await ReadUnvaluedFeesAsync(connection, asOfUtc, targetSymbol, cancellationToken).ConfigureAwait(false);
        var currentSnapshot = await ReadSnapshotAtOrBeforeAsync(connection, asOfUtc, targetSymbol, cancellationToken).ConfigureAwait(false);
        var periods = new List<PerformancePeriodMetrics>(4);

        foreach (var period in Periods(asOfUtc))
        {
            var fills = await ReadFillMetricsAsync(connection, period.StartUtc, asOfUtc, targetSymbol, cancellationToken).ConfigureAwait(false);
            var snapshots = await ReadSnapshotPeriodMetricsAsync(connection, period.StartUtc, asOfUtc, targetSymbol, cancellationToken).ConfigureAwait(false);
            periods.Add(new(period.Name, fills.TradesCount, fills.WinningCount, fills.LosingCount, fills.RealizedPnlQuote, fills.FeesPaidQuote)
            {
                NetAssetValueDeltaQuote = snapshots.NetAssetValueDeltaQuote,
                UnrealizedPnlDeltaQuote = snapshots.UnrealizedPnlDeltaQuote,
                SnapshotAvailability = snapshots.Availability,
                RealisticNetProfitQuote = fills.RealisticNetPnlQuote,
                AverageReturnBps = fills.AvgReturnBps,
                AverageWinBps = fills.AvgWinBps,
                AverageLossBps = fills.AvgLossBps,
            });
        }

        var btcBasis = targetSymbol is null || string.Equals(targetSymbol, PerformanceSymbol, StringComparison.OrdinalIgnoreCase)
            ? (string.Equals(basis?.Symbol, PerformanceSymbol, StringComparison.OrdinalIgnoreCase) ? basis : await ReadPositionBasisAsync(connection, PerformanceSymbol, cancellationToken).ConfigureAwait(false))
            : null;

        return new(totals.TradesCount, totals.WinningCount, totals.LosingCount, totals.RealizedPnlQuote, totals.FeesPaidQuote,
            totals.GrossProfitQuote, totals.GrossLossQuote, periods, btcBasis)
        {
            ScopeSymbol = targetSymbol ?? "PORTFOLIO",
            QuoteAsset = PerformanceQuoteAsset,
            PositionBasis = basis,
            AsOfUtc = asOfUtc,
            CurrentSnapshotObservedAtUtc = currentSnapshot?.ObservedAtUtc,
            CurrentNetAssetValueQuote = currentSnapshot?.NetAssetValueQuote,
            CurrentUnrealizedPnlQuote = currentSnapshot?.UnrealizedPnlQuote,
            UnvaluedFeeAmounts = unvaluedFees,
            RealisticNetPnlQuote = totals.RealisticNetPnlQuote,
            AverageReturnBps = totals.AvgReturnBps,
            AverageWinBps = totals.AvgWinBps,
            AverageLossBps = totals.AvgLossBps,
        };
    }

    public async Task RecordNavSnapshotAsync(PortfolioNavSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            INSERT INTO trading.portfolio_nav_snapshots
                (symbol, quote_asset, observed_at, nav_quote, unrealized_pnl_quote, external_cash_flow_quote, source)
            VALUES (@symbol, @quote_asset, @observed_at, @nav_quote, @unrealized_pnl_quote, @external_cash_flow_quote, @source)
            ON CONFLICT (symbol, quote_asset, observed_at) DO NOTHING;
            """;
        await ExecuteAsync(connection, null, sql,
        [
            Parameter("symbol", NpgsqlDbType.Varchar, snapshot.Symbol.Trim().ToUpperInvariant()),
            Parameter("quote_asset", NpgsqlDbType.Varchar, snapshot.QuoteAsset.Trim().ToUpperInvariant()),
            Parameter("observed_at", NpgsqlDbType.TimestampTz, snapshot.ObservedAtUtc),
            Parameter("nav_quote", NpgsqlDbType.Numeric, snapshot.NetAssetValueQuote),
            Parameter("unrealized_pnl_quote", NpgsqlDbType.Numeric, snapshot.UnrealizedPnlQuote),
            Parameter("external_cash_flow_quote", NpgsqlDbType.Numeric, snapshot.ExternalCashFlowQuote),
            Parameter("source", NpgsqlDbType.Varchar, snapshot.Source.Trim()),
        ], cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PositionCostBasisRecord?> ReadPositionBasisAsync(NpgsqlConnection connection, string? symbol, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT symbol, base_asset, quote_asset, quantity, cost_quote, updated_at
            FROM trading.position_cost_basis WHERE (@symbol IS NULL OR symbol = @symbol) AND quote_asset = @quote_asset
            ORDER BY (quantity > 0) DESC, updated_at DESC;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(Parameter("symbol", NpgsqlDbType.Varchar, symbol ?? (object)DBNull.Value));
        command.Parameters.Add(Parameter("quote_asset", NpgsqlDbType.Varchar, PerformanceQuoteAsset));
        var rows = new List<PositionCostBasisRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetDecimal(3), reader.GetDecimal(4), Utc(reader.GetDateTime(5))));
        }

        if (rows.Count == 0)
        {
            return null;
        }

        if (symbol is not null)
        {
            return rows[0];
        }

        var openRows = rows.Where(static r => r.Quantity > 0m).ToArray();
        if (openRows.Length <= 1)
        {
            return rows[0];
        }

        return new PositionCostBasisRecord(
            "PORTFOLIO",
            "MULTI",
            PerformanceQuoteAsset,
            openRows.Sum(static r => r.Quantity),
            openRows.Sum(static r => r.CostQuote),
            openRows.Max(static r => r.UpdatedAtUtc));
    }

    private static async Task<FillMetrics> ReadFillMetricsAsync(NpgsqlConnection connection, DateTimeOffset? startUtc, DateTimeOffset asOfUtc, string? symbol, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*) FILTER (WHERE realized_pnl_quote IS NOT NULL),
                   COUNT(*) FILTER (WHERE realized_pnl_quote > 0),
                   COUNT(*) FILTER (WHERE realized_pnl_quote < 0),
                   COALESCE(SUM(realized_pnl_quote), 0),
                   COALESCE(SUM(CASE WHEN actual_commission_quote IS NOT NULL AND commission_valuation_source IS NOT NULL AND commission_valuation_at IS NOT NULL THEN actual_commission_quote ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN realized_pnl_quote > 0 THEN realized_pnl_quote ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN realized_pnl_quote < 0 THEN -realized_pnl_quote ELSE 0 END), 0),
                   ROUND(COALESCE(SUM(realized_pnl_quote - 0.0018 * (price * quantity)) FILTER (WHERE realized_pnl_quote IS NOT NULL), 0), 8),
                   ROUND(AVG(realized_pnl_quote / NULLIF(price * quantity, 0) * 10000) FILTER (WHERE realized_pnl_quote IS NOT NULL), 2),
                   ROUND(AVG(realized_pnl_quote / NULLIF(price * quantity, 0) * 10000) FILTER (WHERE realized_pnl_quote > 0), 2),
                   ROUND(AVG(realized_pnl_quote / NULLIF(price * quantity, 0) * 10000) FILTER (WHERE realized_pnl_quote < 0), 2)
            FROM trading.execution_fills
            WHERE (@symbol IS NULL OR symbol = @symbol) AND quote_asset = @quote_asset AND occurred_at <= @as_of_utc
              AND (@start_utc IS NULL OR occurred_at >= @start_utc);
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(Parameter("symbol", NpgsqlDbType.Varchar, symbol ?? (object)DBNull.Value));
        command.Parameters.Add(Parameter("quote_asset", NpgsqlDbType.Varchar, PerformanceQuoteAsset));
        command.Parameters.Add(Parameter("as_of_utc", NpgsqlDbType.TimestampTz, asOfUtc));
        command.Parameters.Add(Parameter("start_utc", NpgsqlDbType.TimestampTz, startUtc.HasValue ? startUtc.Value : DBNull.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var avgReturnBps = await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false) ? (decimal?)null : reader.GetDecimal(8);
        var avgWinBps = await reader.IsDBNullAsync(9, cancellationToken).ConfigureAwait(false) ? (decimal?)null : reader.GetDecimal(9);
        var avgLossBps = await reader.IsDBNullAsync(10, cancellationToken).ConfigureAwait(false) ? (decimal?)null : reader.GetDecimal(10);
        return new(
            checked((int)reader.GetInt64(0)),
            checked((int)reader.GetInt64(1)),
            checked((int)reader.GetInt64(2)),
            reader.GetDecimal(3),
            reader.GetDecimal(4),
            reader.GetDecimal(5),
            reader.GetDecimal(6),
            reader.GetDecimal(7),
            avgReturnBps,
            avgWinBps,
            avgLossBps);
    }

    private static async Task<IReadOnlyList<AssetAmount>> ReadUnvaluedFeesAsync(NpgsqlConnection connection, DateTimeOffset asOfUtc, string? symbol, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT commission_asset, SUM(commission) FROM trading.execution_fills
            WHERE (@symbol IS NULL OR symbol = @symbol) AND quote_asset = @quote_asset AND occurred_at <= @as_of_utc
              AND (actual_commission_quote IS NULL OR commission_valuation_source IS NULL OR commission_valuation_at IS NULL)
            GROUP BY commission_asset ORDER BY commission_asset;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(Parameter("symbol", NpgsqlDbType.Varchar, symbol ?? (object)DBNull.Value));
        command.Parameters.Add(Parameter("quote_asset", NpgsqlDbType.Varchar, PerformanceQuoteAsset));
        command.Parameters.Add(Parameter("as_of_utc", NpgsqlDbType.TimestampTz, asOfUtc));
        var amounts = new List<AssetAmount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) amounts.Add(new(reader.GetString(0), reader.GetDecimal(1)));
        return amounts;
    }

    private static async Task<SnapshotPeriodMetrics> ReadSnapshotPeriodMetricsAsync(NpgsqlConnection connection, DateTimeOffset startUtc, DateTimeOffset asOfUtc, string? symbol, CancellationToken cancellationToken)
    {
        var baseline = await ReadSnapshotAtOrBeforeAsync(connection, startUtc, symbol, cancellationToken).ConfigureAwait(false);
        if (baseline is null) return new(null, null, PerformanceSnapshotAvailability.MissingBaselineSnapshot);
        var latest = await ReadSnapshotAtOrBeforeAsync(connection, asOfUtc, symbol, cancellationToken).ConfigureAwait(false);
        if (latest is null || latest.ObservedAtUtc <= baseline.ObservedAtUtc) return new(null, null, PerformanceSnapshotAvailability.MissingLatestSnapshot);
        var flows = await ReadExternalFlowAsync(connection, baseline.ObservedAtUtc, latest.ObservedAtUtc, symbol, cancellationToken).ConfigureAwait(false);
        if (!flows.Known) return new(null, null, PerformanceSnapshotAvailability.UnknownExternalCashFlow);
        var navDelta = latest.NetAssetValueQuote - baseline.NetAssetValueQuote - flows.Amount;
        return baseline.UnrealizedPnlQuote is null || latest.UnrealizedPnlQuote is null
            ? new(navDelta, null, PerformanceSnapshotAvailability.UnknownUnrealizedPnl)
            : new(navDelta, latest.UnrealizedPnlQuote.Value - baseline.UnrealizedPnlQuote.Value, PerformanceSnapshotAvailability.Available);
    }

    private static async Task<PortfolioNavSnapshot?> ReadSnapshotAtOrBeforeAsync(NpgsqlConnection connection, DateTimeOffset timestampUtc, string? symbol, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT symbol, quote_asset, observed_at, nav_quote, unrealized_pnl_quote, external_cash_flow_quote, source
            FROM trading.portfolio_nav_snapshots WHERE (@symbol IS NULL OR symbol = @symbol) AND quote_asset = @quote_asset AND observed_at <= @timestamp_utc
            ORDER BY observed_at DESC LIMIT 1;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(Parameter("symbol", NpgsqlDbType.Varchar, symbol ?? (object)DBNull.Value));
        command.Parameters.Add(Parameter("quote_asset", NpgsqlDbType.Varchar, PerformanceQuoteAsset));
        command.Parameters.Add(Parameter("timestamp_utc", NpgsqlDbType.TimestampTz, timestampUtc));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        decimal? unrealized = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetDecimal(4);
        decimal? externalFlow = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetDecimal(5);
        return new(reader.GetString(0), reader.GetString(1), Utc(reader.GetDateTime(2)), reader.GetDecimal(3), unrealized, externalFlow, reader.GetString(6));
    }

    private static async Task<(bool Known, decimal Amount)> ReadExternalFlowAsync(NpgsqlConnection connection, DateTimeOffset afterUtc, DateTimeOffset untilUtc, string? symbol, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*) FILTER (WHERE external_cash_flow_quote IS NULL), COALESCE(SUM(external_cash_flow_quote), 0)
            FROM trading.portfolio_nav_snapshots
            WHERE (@symbol IS NULL OR symbol = @symbol) AND quote_asset = @quote_asset AND observed_at > @after_utc AND observed_at <= @until_utc;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(Parameter("symbol", NpgsqlDbType.Varchar, symbol ?? (object)DBNull.Value));
        command.Parameters.Add(Parameter("quote_asset", NpgsqlDbType.Varchar, PerformanceQuoteAsset));
        command.Parameters.Add(Parameter("after_utc", NpgsqlDbType.TimestampTz, afterUtc));
        command.Parameters.Add(Parameter("until_utc", NpgsqlDbType.TimestampTz, untilUtc));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (reader.GetInt64(0) == 0, reader.GetDecimal(1));
    }

    private static IEnumerable<(string Name, DateTimeOffset StartUtc)> Periods(DateTimeOffset asOfUtc)
    {
        var dayStart = new DateTimeOffset(asOfUtc.UtcDateTime.Date, TimeSpan.Zero);
        var yearStart = new DateTimeOffset(asOfUtc.Year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return [("Hoje (Dia / 24 Horas)", dayStart), ("Semana (Últimos 7 Dias)", asOfUtc.AddDays(-7)), ("Mês (Últimos 30 Dias)", asOfUtc.AddDays(-30)), ("Ano (Acumulado / YTD)", yearStart)];
    }

    private static readonly HashSet<string> AllowedUniverseSymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "ADAUSDT", "DOGEUSDT",
        "AVAXUSDT", "LINKUSDT", "NEARUSDT", "SUIUSDT", "DOTUSDT", "UNIUSDT", "AAVEUSDT",
        "INJUSDT", "ARBUSDT", "OPUSDT", "TIAUSDT", "APTUSDT", "SHIBUSDT"
    };

    private static void ValidateSnapshot(PortfolioNavSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.QuoteAsset);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Source);
        if (!AllowedUniverseSymbols.Contains(snapshot.Symbol.Trim()) ||
            !string.Equals(snapshot.QuoteAsset, PerformanceQuoteAsset, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Portfolio snapshots are limited to the allowed universe performance scope.", nameof(snapshot));
        }
        if (snapshot.ObservedAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Portfolio snapshot timestamp must be UTC.", nameof(snapshot));
        if (snapshot.NetAssetValueQuote < 0m) throw new ArgumentOutOfRangeException(nameof(snapshot), "Net asset value cannot be negative.");
    }

    private sealed record FillMetrics(
        int TradesCount,
        int WinningCount,
        int LosingCount,
        decimal RealizedPnlQuote,
        decimal FeesPaidQuote,
        decimal GrossProfitQuote,
        decimal GrossLossQuote,
        decimal RealisticNetPnlQuote,
        decimal? AvgReturnBps,
        decimal? AvgWinBps,
        decimal? AvgLossBps);
    private sealed record SnapshotPeriodMetrics(decimal? NetAssetValueDeltaQuote, decimal? UnrealizedPnlDeltaQuote, PerformanceSnapshotAvailability Availability);
}
