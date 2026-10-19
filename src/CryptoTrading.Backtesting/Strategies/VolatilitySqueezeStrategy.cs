using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Backtesting.Strategies;

/// <summary>Long-only signals emitted by the Volatility Squeeze strategy.</summary>
public enum VolatilitySqueezeSignal
{
    NoTrade,
    EnterLong,
    ExitLong,
}

/// <summary>Reason codes for deterministic volatility squeeze evaluation.</summary>
public enum VolatilitySqueezeReason
{
    InsufficientHistory,
    WithinNormalBand,
    SqueezeActive,
    SqueezeFiredLong,
    MomentumDecayExit,
}

/// <summary>
/// Versioned parameters for the Volatility Squeeze strategy (Bollinger / Keltner compression).
/// </summary>
public sealed record VolatilitySqueezeParameters
{
    public VolatilitySqueezeParameters(
        string strategyId,
        string version,
        Symbol symbol,
        int period = 20,
        decimal bollingerMultiplier = 2.0m,
        decimal keltnerMultiplier = 1.5m,
        decimal feeRate = 0.001m,
        decimal slippageRate = 0.0005m)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (period < 5)
        {
            throw new ArgumentOutOfRangeException(nameof(period), "Period must be at least five bars.");
        }

        if (bollingerMultiplier <= 0m || bollingerMultiplier > 10m)
        {
            throw new ArgumentOutOfRangeException(nameof(bollingerMultiplier), "Bollinger multiplier must be positive.");
        }

        if (keltnerMultiplier <= 0m || keltnerMultiplier > 10m)
        {
            throw new ArgumentOutOfRangeException(nameof(keltnerMultiplier), "Keltner multiplier must be positive.");
        }

        ValidateRate(feeRate, nameof(feeRate));
        ValidateRate(slippageRate, nameof(slippageRate));

        StrategyId = strategyId.Trim();
        Version = version.Trim();
        Symbol = symbol;
        Period = period;
        BollingerMultiplier = bollingerMultiplier;
        KeltnerMultiplier = keltnerMultiplier;
        FeeRate = feeRate;
        SlippageRate = slippageRate;
    }

    /// <summary>Approved default baseline parameters for BTCUSDT Volatility Squeeze.</summary>
    public static VolatilitySqueezeParameters BtcUsdtBaseline { get; } = new(
        "volatility-squeeze-btcusdt",
        "1.0.0",
        new Symbol("BTCUSDT"),
        period: 20,
        bollingerMultiplier: 2.0m,
        keltnerMultiplier: 1.5m,
        feeRate: 0.001m,
        slippageRate: 0.0005m);

    public string StrategyId { get; }
    public string Version { get; }
    public Symbol Symbol { get; }
    public int Period { get; }
    public decimal BollingerMultiplier { get; }
    public decimal KeltnerMultiplier { get; }
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
                BollingerMultiplier.ToString("G29", CultureInfo.InvariantCulture),
                KeltnerMultiplier.ToString("G29", CultureInfo.InvariantCulture),
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

/// <summary>One deterministic decision output for a closed bar under Volatility Squeeze.</summary>
public sealed record VolatilitySqueezeDecision(
    Symbol Symbol,
    DateTimeOffset CloseTimeUtc,
    int BarIndex,
    Price ClosePrice,
    decimal? RollingMean,
    decimal? RollingStdDev,
    decimal? AverageAbsoluteDeviation,
    decimal? BollingerBandWidth,
    decimal? KeltnerChannelWidth,
    bool IsSqueezed,
    decimal? Momentum,
    VolatilitySqueezeSignal Signal,
    VolatilitySqueezeReason Reason,
    decimal FeeRate,
    decimal SlippageRate,
    decimal EstimatedRoundTripCostRate,
    string StrategyId,
    string StrategyVersion,
    string ConfigurationFingerprint);

/// <summary>
/// Quantitative Volatility Squeeze strategy based on Carter (2012) compression bands.
/// Identifies volatility compression phases and captures directional expansion breakouts.
/// </summary>
public sealed class VolatilitySqueezeStrategy
{
    public VolatilitySqueezeStrategy(VolatilitySqueezeParameters parameters)
    {
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
    }

    public VolatilitySqueezeParameters Parameters { get; }

    public IReadOnlyList<VolatilitySqueezeDecision> Evaluate(IReadOnlyList<CloseBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ValidateBars(bars);

        var decisions = new VolatilitySqueezeDecision[bars.Count];
        var roundTripCost = (Parameters.FeeRate + Parameters.SlippageRate) * 2m;
        var inPosition = false;
        var wasRecentlySqueezed = false;

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
                    AverageAbsoluteDeviation: null,
                    BollingerBandWidth: null,
                    KeltnerChannelWidth: null,
                    IsSqueezed: false,
                    Momentum: null,
                    VolatilitySqueezeSignal.NoTrade,
                    VolatilitySqueezeReason.InsufficientHistory,
                    Parameters.FeeRate,
                    Parameters.SlippageRate,
                    roundTripCost,
                    Parameters.StrategyId,
                    Parameters.Version,
                    Parameters.ConfigurationFingerprint);
                continue;
            }

            // 1. Calculate Rolling Mean and Min/Max
            decimal sum = 0m;
            decimal minVal = decimal.MaxValue;
            decimal maxVal = decimal.MinValue;
            for (var k = 0; k < Parameters.Period; k++)
            {
                var price = bars[index - k].ClosePrice.Value;
                sum += price;
                if (price < minVal) minVal = price;
                if (price > maxVal) maxVal = price;
            }
            var mean = sum / Parameters.Period;

            // 2. Calculate Rolling Standard Deviation, Average Absolute Deviation (AAD), and True OHLC ATR
            decimal varianceSum = 0m;
            decimal aadSum = 0m;
            decimal trSum = 0m;
            var hasOhlcRange = false;
            for (var k = 0; k < Parameters.Period; k++)
            {
                var bar = bars[index - k];
                var diff = bar.ClosePrice.Value - mean;
                varianceSum += diff * diff;
                aadSum += Math.Abs(diff);
                var highLow = bar.HighPrice.Value - bar.LowPrice.Value;
                if (highLow > 0m)
                {
                    hasOhlcRange = true;
                }

                if (index - k > 0)
                {
                    var prevClose = bars[index - k - 1].ClosePrice.Value;
                    var tr = Math.Max(highLow, Math.Max(Math.Abs(bar.HighPrice.Value - prevClose), Math.Abs(bar.LowPrice.Value - prevClose)));
                    trSum += tr;
                }
                else
                {
                    trSum += highLow;
                }
            }

            var variance = varianceSum / (Parameters.Period - 1);
            var stdDev = (decimal)Math.Sqrt((double)variance);
            var aad = aadSum / Parameters.Period;
            var atr = trSum / Parameters.Period;

            var bbWidth = 2m * Parameters.BollingerMultiplier * stdDev;
            var keltnerRange = hasOhlcRange && atr > 0m
                ? atr
                : (aad > 0m ? aad * 1.75m : stdDev * 1.25m);
            var kcWidth = 2m * Parameters.KeltnerMultiplier * keltnerRange;
            var isSqueezed = bbWidth <= kcWidth;

            // 3. Carter Squeeze Momentum: Close - (Mean + (Max + Min)/2) / 2
            var donchianMid = (maxVal + minVal) / 2m;
            var baseline = (mean + donchianMid) / 2m;
            var momentum = bars[index].ClosePrice.Value - baseline;

            var signal = VolatilitySqueezeSignal.NoTrade;
            var reason = isSqueezed ? VolatilitySqueezeReason.SqueezeActive : VolatilitySqueezeReason.WithinNormalBand;

            if (inPosition)
            {
                // Exit when momentum decays or turns negative or price closes below baseline
                if (momentum < 0m || bars[index].ClosePrice.Value < mean)
                {
                    signal = VolatilitySqueezeSignal.ExitLong;
                    reason = VolatilitySqueezeReason.MomentumDecayExit;
                    inPosition = false;
                }
            }
            else
            {
                // Enter Long when squeeze was recently active and has just fired/released, and momentum is distinctly positive
                if (wasRecentlySqueezed && !isSqueezed && momentum > 0m && bars[index].ClosePrice.Value > mean)
                {
                    signal = VolatilitySqueezeSignal.EnterLong;
                    reason = VolatilitySqueezeReason.SqueezeFiredLong;
                    inPosition = true;
                }
            }

            wasRecentlySqueezed = isSqueezed;

            decisions[index] = new(
                bars[index].Symbol,
                bars[index].CloseTimeUtc,
                index,
                bars[index].ClosePrice,
                mean,
                stdDev,
                aad,
                bbWidth,
                kcWidth,
                isSqueezed,
                momentum,
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
        for (var i = 0; i < bars.Count; i++)
        {
            if (bars[i].Symbol != Parameters.Symbol)
            {
                throw new ArgumentException(
                    $"All bars must have symbol '{Parameters.Symbol.Value}', but bar {i} has '{bars[i].Symbol.Value}'.",
                    nameof(bars));
            }
        }
    }
}
