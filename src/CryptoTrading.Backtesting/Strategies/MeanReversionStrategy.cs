using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Backtesting.Strategies;

/// <summary>Long-only signals emitted by the Mean Reversion strategy.</summary>
public enum MeanReversionSignal
{
    NoTrade,
    EnterLong,
    ExitLong,
}

/// <summary>Reason codes for deterministic mean reversion evaluation.</summary>
public enum MeanReversionReason
{
    InsufficientHistory,
    WithinBands,
    ZScoreOversold,
    ZScoreRevertedToMean,
    RegimeGuardActive,
}

/// <summary>
/// Versioned parameters for the Mean Reversion strategy.
/// Fee and slippage rates are decimal fractions of notional.
/// </summary>
public sealed record MeanReversionParameters
{
    public MeanReversionParameters(
        string strategyId,
        string version,
        Symbol symbol,
        int period,
        decimal entryZScoreThreshold,
        decimal exitZScoreThreshold,
        decimal maxAllowedVolatilityRate,
        decimal feeRate,
        decimal slippageRate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (period < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(period), "Period must be at least three bars.");
        }

        if (entryZScoreThreshold >= exitZScoreThreshold)
        {
            throw new ArgumentOutOfRangeException(
                nameof(entryZScoreThreshold),
                "Entry Z-Score threshold must be strictly lower than exit threshold.");
        }

        if (maxAllowedVolatilityRate <= 0m || maxAllowedVolatilityRate > 1m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAllowedVolatilityRate),
                "Max allowed volatility rate must be between zero and one.");
        }

        ValidateRate(feeRate, nameof(feeRate));
        ValidateRate(slippageRate, nameof(slippageRate));

        StrategyId = strategyId.Trim();
        Version = version.Trim();
        Symbol = symbol;
        Period = period;
        EntryZScoreThreshold = entryZScoreThreshold;
        ExitZScoreThreshold = exitZScoreThreshold;
        MaxAllowedVolatilityRate = maxAllowedVolatilityRate;
        FeeRate = feeRate;
        SlippageRate = slippageRate;
    }

    /// <summary>Approved default baseline parameters for BTCUSDT Mean Reversion.</summary>
    public static MeanReversionParameters BtcUsdtBaseline { get; } = new(
        "mean-reversion-btcusdt",
        "1.0.0",
        new Symbol("BTCUSDT"),
        period: 20,
        entryZScoreThreshold: -1.8m,
        exitZScoreThreshold: 0.0m,
        maxAllowedVolatilityRate: 0.05m,
        feeRate: 0.001m,
        slippageRate: 0.0005m);

    public string StrategyId { get; }

    public string Version { get; }

    public Symbol Symbol { get; }

    public int Period { get; }

    public decimal EntryZScoreThreshold { get; }

    public decimal ExitZScoreThreshold { get; }

    public decimal MaxAllowedVolatilityRate { get; }

    public decimal FeeRate { get; }

    public decimal SlippageRate { get; }

    /// <summary>SHA-256 canonical fingerprint of every parameter affecting decision outputs.</summary>
    public string ConfigurationFingerprint
    {
        get
        {
            var canonical = string.Join(
                '|',
                StrategyId,
                Version,
                Symbol.Value,
                Period.ToString(CultureInfo.InvariantCulture),
                EntryZScoreThreshold.ToString("G29", CultureInfo.InvariantCulture),
                ExitZScoreThreshold.ToString("G29", CultureInfo.InvariantCulture),
                MaxAllowedVolatilityRate.ToString("G29", CultureInfo.InvariantCulture),
                FeeRate.ToString("G29", CultureInfo.InvariantCulture),
                SlippageRate.ToString("G29", CultureInfo.InvariantCulture));

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }
    }

    private static void ValidateRate(decimal value, string parameterName)
    {
        if (value is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Cost rates must be between zero and one.");
        }
    }
}

/// <summary>One deterministic decision output for a closed bar.</summary>
public sealed record MeanReversionDecision(
    Symbol Symbol,
    DateTimeOffset CloseTimeUtc,
    int BarIndex,
    Price ClosePrice,
    decimal? RollingMean,
    decimal? RollingStdDev,
    decimal? ZScore,
    bool RegimeGuardTriggered,
    MeanReversionSignal Signal,
    MeanReversionReason Reason,
    decimal FeeRate,
    decimal SlippageRate,
    decimal EstimatedRoundTripCostRate,
    string StrategyId,
    string StrategyVersion,
    string ConfigurationFingerprint);

/// <summary>
/// Pure Statistical Mean Reversion evaluator with Z-Score bands and a volatility Regime Guard.
/// Consumes closed bars deterministically and produces signals without direct exchange side-effects.
/// </summary>
public sealed class MeanReversionStrategy
{
    public MeanReversionStrategy(MeanReversionParameters parameters)
    {
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
    }

    public MeanReversionParameters Parameters { get; }

    public IReadOnlyList<MeanReversionDecision> Evaluate(IReadOnlyList<CloseBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ValidateBars(bars);

        var decisions = new MeanReversionDecision[bars.Count];
        var roundTripCost = (Parameters.FeeRate + Parameters.SlippageRate) * 2m;
        var inPosition = false;

        for (var index = 0; index < bars.Count; index++)
        {
            if (index < Parameters.Period - 1)
            {
                decisions[index] = new(
                    bars[index].Symbol,
                    bars[index].CloseTimeUtc,
                    index,
                    bars[index].ClosePrice,
                    RollingMean: null,
                    RollingStdDev: null,
                    ZScore: null,
                    RegimeGuardTriggered: false,
                    MeanReversionSignal.NoTrade,
                    MeanReversionReason.InsufficientHistory,
                    Parameters.FeeRate,
                    Parameters.SlippageRate,
                    roundTripCost,
                    Parameters.StrategyId,
                    Parameters.Version,
                    Parameters.ConfigurationFingerprint);
                continue;
            }

            // Calculate rolling mean and standard deviation over [index - Period + 1 .. index]
            decimal sum = 0m;
            for (var k = 0; k < Parameters.Period; k++)
            {
                sum += bars[index - k].ClosePrice.Value;
            }
            var mean = sum / Parameters.Period;

            decimal varianceSum = 0m;
            for (var k = 0; k < Parameters.Period; k++)
            {
                var diff = bars[index - k].ClosePrice.Value - mean;
                varianceSum += diff * diff;
            }

            var variance = varianceSum / (Parameters.Period - 1);
            var stdDev = (decimal)Math.Sqrt((double)variance);

            var volatilityRate = mean > 0m ? stdDev / mean : 0m;
            var regimeGuardTriggered = volatilityRate > Parameters.MaxAllowedVolatilityRate;

            decimal zScore = 0m;
            if (stdDev > 0m)
            {
                zScore = (bars[index].ClosePrice.Value - mean) / stdDev;
            }

            var signal = MeanReversionSignal.NoTrade;
            var reason = MeanReversionReason.WithinBands;

            if (inPosition)
            {
                // Exit condition: price reverted back to or above mean
                if (zScore >= Parameters.ExitZScoreThreshold)
                {
                    signal = MeanReversionSignal.ExitLong;
                    reason = MeanReversionReason.ZScoreRevertedToMean;
                    inPosition = false;
                }
            }
            else
            {
                // Entry condition: oversold, but only if regime guard is clear
                if (zScore <= Parameters.EntryZScoreThreshold)
                {
                    if (regimeGuardTriggered)
                    {
                        signal = MeanReversionSignal.NoTrade;
                        reason = MeanReversionReason.RegimeGuardActive;
                    }
                    else
                    {
                        signal = MeanReversionSignal.EnterLong;
                        reason = MeanReversionReason.ZScoreOversold;
                        inPosition = true;
                    }
                }
            }

            decisions[index] = new(
                bars[index].Symbol,
                bars[index].CloseTimeUtc,
                index,
                bars[index].ClosePrice,
                mean,
                stdDev,
                zScore,
                regimeGuardTriggered,
                signal,
                reason,
                Parameters.FeeRate,
                Parameters.SlippageRate,
                roundTripCost,
                Parameters.StrategyId,
                Parameters.Version,
                Parameters.ConfigurationFingerprint);
        }

        return decisions;
    }

    private void ValidateBars(IReadOnlyList<CloseBar> bars)
    {
        DateTimeOffset? previousTime = null;
        for (var index = 0; index < bars.Count; index++)
        {
            var bar = bars[index] ?? throw new ArgumentException("Bar collection cannot contain null values.", nameof(bars));
            if (bar.Symbol != Parameters.Symbol)
            {
                throw new ArgumentException(
                    $"Bar at index {index} has symbol {bar.Symbol}; expected {Parameters.Symbol}.",
                    nameof(bars));
            }

            if (previousTime.HasValue && bar.CloseTimeUtc <= previousTime.Value)
            {
                throw new ArgumentException("Bars must be strictly ordered by close time.", nameof(bars));
            }

            previousTime = bar.CloseTimeUtc;
        }
    }
}
