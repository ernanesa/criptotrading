using System.Globalization;
using CryptoTrading.Backtesting.Autonomy;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Backtesting.Validation;

public sealed record LookaheadViolation(
    int BarIndex,
    DateTimeOffset TimestampUtc,
    string StrategyOrComponentId,
    string ExpectedDecisionFromPrefix,
    string ActualDecisionFromFullHistory,
    string Details);

public sealed record LookaheadValidationReport(
    bool IsCausallyInvariant,
    int TotalTestedPoints,
    int LookaheadViolationsCount,
    IReadOnlyList<LookaheadViolation> Violations)
{
    public static LookaheadValidationReport Success(int testedPoints) =>
        new(true, testedPoints, 0, Array.Empty<LookaheadViolation>());

    public static LookaheadValidationReport Failure(int testedPoints, IReadOnlyList<LookaheadViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(violations);
        return new(false, testedPoints, violations.Count, violations);
    }
}

public sealed record WarmupStabilityMetric(
    string IndicatorName,
    decimal ShortWarmupValue,
    decimal LongWarmupValue,
    decimal AbsoluteDifference,
    decimal RelativeDifferencePercent,
    bool IsAcceptable);

public sealed record IndicatorWarmupStabilityReport(
    bool IsStable,
    int ShortWarmupBars,
    int LongWarmupBars,
    DateTimeOffset EvaluationTimestampUtc,
    IReadOnlyList<WarmupStabilityMetric> Metrics);

public static class TemporalCausalityValidator
{
    /// <summary>
    /// Verifies that a strategy or sequence evaluator does NOT look ahead into future bars.
    /// For every test point i in [minWarmupBars - 1, fullHistory.Count - 1], the decision
    /// evaluated using only prefix [0..i+1] must strictly match the decision for bar i
    /// when the entire history [0..N] is supplied.
    /// </summary>
    public static LookaheadValidationReport ValidateStrategyLookahead<TDecision>(
        IReadOnlyList<CloseBar> fullHistory,
        Func<IReadOnlyList<CloseBar>, IReadOnlyList<TDecision>> strategyEvaluator,
        Func<TDecision, string> decisionSerializer,
        int minWarmupBars = 20,
        int step = 1,
        string componentId = "Strategy")
    {
        ArgumentNullException.ThrowIfNull(fullHistory);
        ArgumentNullException.ThrowIfNull(strategyEvaluator);
        ArgumentNullException.ThrowIfNull(decisionSerializer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step);
        ArgumentOutOfRangeException.ThrowIfLessThan(minWarmupBars, 1);

        if (fullHistory.Count < minWarmupBars)
        {
            throw new ArgumentException($"Full history must contain at least {minWarmupBars} bars.", nameof(fullHistory));
        }

        var fullDecisions = strategyEvaluator(fullHistory);
        if (fullDecisions.Count != fullHistory.Count)
        {
            throw new InvalidOperationException($"Full strategy evaluation returned {fullDecisions.Count} decisions for {fullHistory.Count} bars.");
        }

        var violations = new List<LookaheadViolation>();
        var testedPoints = 0;

        for (var i = minWarmupBars - 1; i < fullHistory.Count; i += step)
        {
            testedPoints++;
            var prefixBars = new CloseBar[i + 1];
            for (var j = 0; j <= i; j++)
            {
                prefixBars[j] = fullHistory[j];
            }

            var prefixDecisions = strategyEvaluator(prefixBars);
            if (prefixDecisions.Count != prefixBars.Length)
            {
                throw new InvalidOperationException($"Prefix evaluation returned {prefixDecisions.Count} decisions for {prefixBars.Length} bars.");
            }

            var prefixDecisionAtT = prefixDecisions[^1];
            var fullDecisionAtT = fullDecisions[i];

            var serializedPrefix = decisionSerializer(prefixDecisionAtT);
            var serializedFull = decisionSerializer(fullDecisionAtT);

            if (!string.Equals(serializedPrefix, serializedFull, StringComparison.Ordinal))
            {
                violations.Add(new LookaheadViolation(
                    i,
                    fullHistory[i].CloseTimeUtc,
                    componentId,
                    serializedPrefix,
                    serializedFull,
                    $"Prefix evaluation at bar {i} ({fullHistory[i].CloseTimeUtc:o}) yielded '{serializedPrefix}' but full history evaluation yielded '{serializedFull}'."));
            }
        }

        return violations.Count == 0
            ? LookaheadValidationReport.Success(testedPoints)
            : LookaheadValidationReport.Failure(testedPoints, violations);
    }

    /// <summary>
    /// Verifies that TechnicalIndicatorCalculator values are stable and not distorted by varying
    /// recursive warmup lengths (e.g. Wilder's RMA in RSI, ATR, ADX) at evaluation point T.
    /// </summary>
    public static IndicatorWarmupStabilityReport ValidateIndicatorWarmupStability(
        IReadOnlyList<CloseBar> bars,
        int shortWarmupBars = 40,
        int longWarmupBars = 100,
        decimal tolerancePercent = 0.50m,
        decimal? adxTolerancePercent = null)
    {
        ArgumentNullException.ThrowIfNull(bars);

        if (shortWarmupBars < 28)
        {
            throw new ArgumentOutOfRangeException(nameof(shortWarmupBars), "Short warmup must have at least 28 bars for Wilder initialization.");
        }

        if (shortWarmupBars >= longWarmupBars)
        {
            throw new ArgumentException("shortWarmupBars must be strictly less than longWarmupBars.", nameof(shortWarmupBars));
        }

        if (bars.Count < longWarmupBars)
        {
            throw new ArgumentException($"Bars collection must contain at least {longWarmupBars} bars.", nameof(bars));
        }

        var evalTime = bars[^1].CloseTimeUtc;
        var shortSlice = bars.TakeLast(shortWarmupBars).ToArray();
        var longSlice = bars.TakeLast(longWarmupBars).ToArray();

        var shortSnapshot = TechnicalIndicatorCalculator.Evaluate(shortSlice);
        var longSnapshot = TechnicalIndicatorCalculator.Evaluate(longSlice);

        var metrics = new List<WarmupStabilityMetric>();

        void Compare(string name, decimal shortVal, decimal longVal, decimal maxTolerance, bool useAbsoluteToleranceForNearZero = false)
        {
            var absDiff = Math.Abs(shortVal - longVal);
            var refVal = Math.Abs(longVal);
            var relDiffPct = refVal > 0.0001m
                ? Math.Round(absDiff / refVal * 100m, 4)
                : (absDiff == 0m ? 0m : 100m);
            var acceptable = (useAbsoluteToleranceForNearZero && absDiff <= 0.05m) || relDiffPct <= maxTolerance;
            metrics.Add(new WarmupStabilityMetric(name, shortVal, longVal, absDiff, relDiffPct, acceptable));
        }

        Compare("RSI", shortSnapshot.Rsi, longSnapshot.Rsi, tolerancePercent);
        Compare("ATR_BPS", shortSnapshot.AtrBps, longSnapshot.AtrBps, tolerancePercent);
        Compare("ADX", shortSnapshot.Adx, longSnapshot.Adx, adxTolerancePercent ?? Math.Max(tolerancePercent * 2.5m, 5.0m));
        Compare("VWAP_DEV_BPS", shortSnapshot.VwapDeviationBps, longSnapshot.VwapDeviationBps, tolerancePercent, useAbsoluteToleranceForNearZero: true);

        var isStable = metrics.All(m => m.IsAcceptable);
        return new IndicatorWarmupStabilityReport(isStable, shortWarmupBars, longWarmupBars, evalTime, metrics);
    }
}
