using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CryptoTrading.Backtesting.Strategies;

namespace CryptoTrading.Backtesting;

/// <summary>Deterministic, versioned synthetic BTCUSDT candles for local validation only.</summary>
public static class SyntheticMarketDataset
{
    public static (DatasetMetadata Metadata, IReadOnlyList<CloseBar> Bars) CreateBtcUsdtV1()
    {
        var bars = Enumerable.Range(0, 180)
            .Select(index => new CloseBar(
                new Domain.Primitives.Symbol("BTCUSDT"),
                DateTimeOffset.UnixEpoch.AddDays(index),
                PriceAt(index)))
            .ToArray();

        var canonical = string.Join('\n', bars.Select((bar, index) => string.Join(
            ',',
            index.ToString(CultureInfo.InvariantCulture),
            bar.CloseTimeUtc.ToString("O", CultureInfo.InvariantCulture),
            bar.ClosePrice.Value.ToString("G29", CultureInfo.InvariantCulture))));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var metadata = new DatasetMetadata(
            "synthetic-btcusdt-oos",
            "1.0.0",
            "CryptoTrading.Backtesting.SyntheticMarketDataset",
            "close-bars.v1",
            hash,
            DateTimeOffset.UnixEpoch,
            "UTC");

        return (metadata, bars);
    }

    private static decimal PriceAt(int index)
    {
        // Fixed piecewise regimes exercise both trend directions without implying a forecast.
        var regime = index / 30;
        var offset = index % 30;
        var anchor = regime switch
        {
            0 => 100m,
            1 => 145m,
            2 => 92m,
            3 => 154m,
            4 => 108m,
            _ => 166m
        };
        var slope = regime % 2 == 0 ? 1.1m : -0.9m;
        var oscillation = (offset % 5 - 2) * 0.35m;
        return anchor + (slope * offset) + oscillation;
    }
}

public sealed record OutOfSampleWindowResult(
    WalkForwardWindow Window,
    decimal StartingEquity,
    decimal FinalEquity,
    decimal NetReturn,
    decimal MaximumDrawdown,
    decimal TotalFees,
    decimal TotalSlippageCost,
    int TradeCount,
    int PeriodCount,
    BenchmarkComparison Benchmark);

public sealed record WalkForwardEvaluationResult(
    DatasetMetadata Dataset,
    string StrategyId,
    string StrategyVersion,
    string StrategyConfigurationFingerprint,
    IReadOnlyList<OutOfSampleWindowResult> Windows);

/// <summary>
/// Evaluates explicit walk-forward OOS windows using training bars only as indicator warm-up.
/// No parameter fitting, winner selection, exchange access or order submission occurs here.
/// </summary>
public static class WalkForwardEvaluator
{
    public static WalkForwardEvaluationResult Evaluate(
        DatasetMetadata dataset,
        IReadOnlyList<CloseBar> bars,
        TrendFollowingParameters parameters,
        BacktestEvaluationOptions options,
        IReadOnlyList<WalkForwardWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(windows);
        if (windows.Count == 0)
        {
            throw new ArgumentException("At least one walk-forward window is required.", nameof(windows));
        }

        var results = new List<OutOfSampleWindowResult>(windows.Count);
        foreach (var window in windows)
        {
            var endExclusive = checked(window.OutOfSampleStartIndex + window.OutOfSampleLength);
            if (window.InSampleStartIndex + window.InSampleLength < 0 || endExclusive > bars.Count)
            {
                throw new ArgumentException("Walk-forward window is outside the dataset.", nameof(windows));
            }

            // Include the training range so EMA state is warmed up, but score only OOS bars.
            var prefix = bars.Take(endExclusive).ToArray();
            var evaluation = TrendFollowingBacktestEvaluator.Evaluate(prefix, parameters, options);
            var oosPoints = evaluation.EquityCurve
                .Skip(window.OutOfSampleStartIndex)
                .Take(window.OutOfSampleLength)
                .ToArray();
            var startingEquity = window.OutOfSampleStartIndex == 0
                ? options.InitialCapital
                : evaluation.EquityCurve[window.OutOfSampleStartIndex - 1].Equity;
            var finalEquity = oosPoints[^1].Equity;
            var peak = startingEquity;
            var maximumDrawdown = 0m;
            foreach (var point in oosPoints)
            {
                peak = Math.Max(peak, point.Equity);
                maximumDrawdown = Math.Max(maximumDrawdown, peak == 0m ? 0m : 1m - point.Equity / peak);
            }

            var trades = evaluation.Trades
                .Where(trade => trade.BarIndex >= window.OutOfSampleStartIndex && trade.BarIndex < endExclusive)
                .ToArray();
            var netReturn = startingEquity == 0m ? 0m : finalEquity / startingEquity - 1m;
            var oosBars = bars
                .Skip(window.OutOfSampleStartIndex)
                .Take(window.OutOfSampleLength)
                .ToArray();
            results.Add(new OutOfSampleWindowResult(
                window,
                startingEquity,
                finalEquity,
                netReturn,
                maximumDrawdown,
                trades.Sum(trade => trade.Fee),
                trades.Sum(trade => trade.SlippageCost),
                trades.Length,
                oosPoints.Length,
                BenchmarkComparisonEvaluator.CompareOutOfSample(
                    $"{parameters.StrategyId}/{parameters.Version}",
                    startingEquity,
                    finalEquity,
                    netReturn,
                    maximumDrawdown,
                    oosPoints.Length,
                    oosBars)));
        }

        return new WalkForwardEvaluationResult(
            dataset,
            parameters.StrategyId,
            parameters.Version,
            parameters.ConfigurationFingerprint,
            results);
    }
}
