using System.Globalization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
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
    BenchmarkComparison Benchmark)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EndPositionOpen { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ObservedWindowFingerprint { get; init; }
}

public sealed record WalkForwardBenchmarkSummary(
    decimal StrategyNetReturn,
    decimal NoTradeCashNetReturn,
    decimal BuyAndHoldNetReturn,
    bool BeatsNoTradeCash,
    bool BeatsBuyAndHold,
    string Fingerprint);

public sealed record WalkForwardEvaluationResult(
    DatasetMetadata Dataset,
    string StrategyId,
    string StrategyVersion,
    string StrategyConfigurationFingerprint,
    IReadOnlyList<OutOfSampleWindowResult> Windows)
{
    public BacktestExecutionTiming ExecutionTiming { get; init; } = BacktestExecutionTiming.LegacySignalBarClose;
    public string? EvaluationAssumptionsFingerprint { get; init; }
    public string? ObservedSeriesFingerprint { get; init; }
    public string? ObservedSeriesFingerprintVersion { get; init; }

    public WalkForwardBenchmarkSummary GetBenchmarkSummary()
    {
        if (ExecutionTiming is not (BacktestExecutionTiming.NextBarClose or BacktestExecutionTiming.LegacySignalBarClose))
        {
            throw new InvalidOperationException("Execution timing is unknown.");
        }

        if ((ExecutionTiming == BacktestExecutionTiming.NextBarClose &&
             (ObservedSeriesFingerprint is null || ObservedSeriesFingerprintVersion is null)) ||
            (ObservedSeriesFingerprint is null && ObservedSeriesFingerprintVersion is not null))
        {
            throw new InvalidOperationException("Causal evaluation is missing window identity metadata.");
        }

        if (ObservedSeriesFingerprint is not null &&
            (ObservedSeriesFingerprintVersion != WalkForwardObservedIdentity.Version ||
             !string.Equals(ObservedSeriesFingerprint, WalkForwardObservedIdentity.CalculateWindowSet(
                 Windows, StrategyId, StrategyVersion, StrategyConfigurationFingerprint, EvaluationAssumptionsFingerprint), StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Observed-series identity does not match the recorded evaluation windows.");
        }

        if ((ExecutionTiming == BacktestExecutionTiming.LegacySignalBarClose && Windows.Any(static window => window.EndPositionOpen.HasValue)) ||
            (ExecutionTiming == BacktestExecutionTiming.NextBarClose &&
             (EvaluationAssumptionsFingerprint is null || Windows.Any(static window => !window.EndPositionOpen.HasValue))))
        {
            throw new InvalidOperationException("Execution timing is inconsistent with the recorded window evidence.");
        }

        var summary = ExecutionTiming == BacktestExecutionTiming.LegacySignalBarClose
            ? BenchmarkComparisonEvaluator.AggregateOutOfSampleLegacy(Windows)
            : BenchmarkComparisonEvaluator.AggregateOutOfSample(Windows);
        if (EvaluationAssumptionsFingerprint is null) return summary;

        var endStates = string.Join(',', Windows.Select(static window => window.EndPositionOpen switch
        {
            true => "open",
            false => "closed",
            null => "unknown"
        }));
        var canonical = ObservedSeriesFingerprint is null
            ? string.Join('\n', "walk-forward-evidence-v2", summary.Fingerprint, EvaluationAssumptionsFingerprint, endStates)
            : string.Join('\n', "walk-forward-evidence-v3", summary.Fingerprint, EvaluationAssumptionsFingerprint,
                Dataset.ContentHash, ObservedSeriesFingerprint, endStates);
        return summary with { Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))) };
    }
}

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
        var assumptionsFingerprint = TrendFollowingBacktestEvaluator.CalculateEvaluationAssumptionsFingerprint(
            parameters, options, options.ExecutionTiming == BacktestExecutionTiming.LegacySignalBarClose
                ? "walk-forward-legacy-continuous-v1" : "walk-forward-warmup-only-v2");
        foreach (var window in windows)
        {
            _ = window.Split;
            var endExclusive = checked(window.OutOfSampleStartIndex + window.OutOfSampleLength);
            if (window.InSampleStartIndex + window.InSampleLength < 0 || endExclusive > bars.Count)
            {
                throw new ArgumentException("Walk-forward window is outside the dataset.", nameof(windows));
            }

            var legacy = options.ExecutionTiming == BacktestExecutionTiming.LegacySignalBarClose;
            if (!legacy && window.OutOfSampleStartIndex != checked(window.InSampleStartIndex + window.InSampleLength))
            {
                throw new ArgumentException("Next-bar walk-forward evaluation requires adjacent training and OOS windows.", nameof(windows));
            }

            // Preserve the old continuous simulation only when explicitly requested for reproduction.
            // In the causal mode training warms indicators, not the portfolio.
            var seriesStart = legacy ? 0 : window.InSampleStartIndex;
            var oosStart = window.OutOfSampleStartIndex - seriesStart;
            var prefix = bars.Skip(seriesStart).Take(endExclusive - seriesStart).ToArray();
            var observedWindowFingerprint = WalkForwardObservedIdentity.CalculateWindow(window, seriesStart, endExclusive, prefix);
            var evaluation = legacy
                ? TrendFollowingBacktestEvaluator.Evaluate(prefix, parameters, options)
                : TrendFollowingBacktestEvaluator.EvaluateWindow(prefix, parameters, options, oosStart);
            var oosPoints = evaluation.EquityCurve
                .Skip(oosStart)
                .Take(window.OutOfSampleLength)
                .ToArray();
            var startingEquity = oosStart == 0
                ? options.InitialCapital
                : evaluation.EquityCurve[oosStart - 1].Equity;
            var finalEquity = oosPoints[^1].Equity;
            var peak = startingEquity;
            var maximumDrawdown = 0m;
            foreach (var point in oosPoints)
            {
                peak = Math.Max(peak, point.Equity);
                maximumDrawdown = Math.Max(maximumDrawdown, peak == 0m ? 0m : 1m - point.Equity / peak);
            }

            var trades = evaluation.Trades
                .Where(trade => trade.BarIndex >= oosStart && trade.BarIndex < prefix.Length)
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
                BenchmarkComparisonEvaluator.CompareOutOfSampleWithTiming(
                    BenchmarkComparisonEvaluator.FormatStrategyName(parameters.StrategyId, parameters.Version, options.ExecutionTiming),
                    startingEquity,
                    finalEquity,
                    netReturn,
                    maximumDrawdown,
                    oosPoints.Length,
                    oosBars,
                    benchmarkEntryPrice: legacy && window.OutOfSampleStartIndex > 0
                        ? bars[window.OutOfSampleStartIndex - 1].ClosePrice.Value
                        : oosBars[0].ClosePrice.Value,
                    feeRate: parameters.FeeRate,
                    halfSpreadRate: 0m,
                    slippageRate: parameters.SlippageRate,
                    entryConvention: legacy ? "prior-close-entry" : "first-oos-close-entry",
                    markOpenAtEnd: !legacy,
                    strategyConfigurationFingerprint: parameters.ConfigurationFingerprint,
                    evaluationAssumptionsFingerprint: assumptionsFingerprint))
            {
                EndPositionOpen = legacy ? null : oosPoints[^1].InPosition,
                ObservedWindowFingerprint = observedWindowFingerprint
            });
        }

        return new WalkForwardEvaluationResult(
            dataset,
            parameters.StrategyId,
            parameters.Version,
            parameters.ConfigurationFingerprint,
            results)
        {
            ExecutionTiming = options.ExecutionTiming,
            EvaluationAssumptionsFingerprint = assumptionsFingerprint,
            ObservedSeriesFingerprint = WalkForwardObservedIdentity.CalculateWindowSet(
                results, parameters.StrategyId, parameters.Version, parameters.ConfigurationFingerprint, assumptionsFingerprint),
            ObservedSeriesFingerprintVersion = WalkForwardObservedIdentity.Version
        };
    }
}

internal static class WalkForwardObservedIdentity
{
    internal const string Version = "walk-forward-window-set-v4";

    internal static string CalculateWindow(WalkForwardWindow window, int seriesStart, int endExclusive, IReadOnlyList<CloseBar> bars)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"walk-forward-consumed-window-v2|{window.WindowNumber}|{seriesStart}|{window.OutOfSampleStartIndex}|{endExclusive}\n")));
        foreach (var bar in bars)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
                $"{bar.Symbol.Value}|{bar.CloseTimeUtc.UtcDateTime.Ticks}|{bar.ClosePrice.Value:G29}\n")));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static string CalculateWindowSet(IReadOnlyList<OutOfSampleWindowResult> windows,
        string strategyId, string strategyVersion, string strategyFingerprint, string? assumptionsFingerprint)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(Version + "\n"));
        AppendField(hash, strategyId);
        AppendField(hash, strategyVersion);
        AppendField(hash, strategyFingerprint);
        AppendField(hash, assumptionsFingerprint ?? throw new InvalidOperationException("Evaluation assumptions fingerprint is missing."));
        foreach (var item in windows)
        {
            if (string.IsNullOrWhiteSpace(item.ObservedWindowFingerprint))
            {
                throw new InvalidOperationException("Window is missing its observed-series fingerprint.");
            }
            var endState = item.EndPositionOpen switch { true => "open", false => "closed", null => "unknown" };
            hash.AppendData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
                $"{item.Window.WindowNumber}|{item.Window.InSampleStartIndex}|{item.Window.InSampleLength}|{item.Window.OutOfSampleStartIndex}|{item.Window.OutOfSampleLength}|{item.ObservedWindowFingerprint}|{item.StartingEquity:G29}|{item.FinalEquity:G29}|{item.NetReturn:G29}|{item.MaximumDrawdown:G29}|{item.TotalFees:G29}|{item.TotalSlippageCost:G29}|{item.TradeCount}|{item.PeriodCount}|{endState}|{item.Benchmark.Fingerprint}\n")));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendField(IncrementalHash hash, string value)
    {
        var bytes = new UTF8Encoding(false, true).GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
