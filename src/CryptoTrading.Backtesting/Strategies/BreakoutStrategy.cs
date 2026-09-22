using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Backtesting.Strategies;

/// <summary>Long-only signals emitted by the Momentum / Channel Breakout strategy.</summary>
public enum BreakoutSignal
{
    NoTrade,
    EnterLong,
    ExitLong,
}

/// <summary>Reason codes for deterministic breakout strategy evaluation.</summary>
public enum BreakoutReason
{
    InsufficientHistory,
    ChannelUnchanged,
    ChannelBreakoutAbove,
    ChannelDroppedBelowExit,
}

/// <summary>
/// Versioned parameters for the Channel Breakout strategy.
/// Fee and slippage rates are decimal fractions of notional.
/// </summary>
public sealed record BreakoutParameters
{
    public BreakoutParameters(
        string strategyId,
        string version,
        Symbol symbol,
        int entryPeriod,
        int exitPeriod,
        decimal feeRate,
        decimal slippageRate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (entryPeriod < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(entryPeriod), "Entry period must be at least three bars.");
        }

        if (exitPeriod < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(exitPeriod), "Exit period must be at least two bars.");
        }

        if (exitPeriod >= entryPeriod)
        {
            throw new ArgumentOutOfRangeException(
                nameof(exitPeriod),
                "Exit period must be strictly shorter than entry period for responsive stop/trailing.");
        }

        ValidateRate(feeRate, nameof(feeRate));
        ValidateRate(slippageRate, nameof(slippageRate));

        StrategyId = strategyId.Trim();
        Version = version.Trim();
        Symbol = symbol;
        EntryPeriod = entryPeriod;
        ExitPeriod = exitPeriod;
        FeeRate = feeRate;
        SlippageRate = slippageRate;
    }

    /// <summary>Approved default baseline parameters for BTCUSDT Channel Breakout.</summary>
    public static BreakoutParameters BtcUsdtBaseline { get; } = new(
        "breakout-momentum-btcusdt",
        "1.0.0",
        new Symbol("BTCUSDT"),
        entryPeriod: 20,
        exitPeriod: 10,
        feeRate: 0.001m,
        slippageRate: 0.0005m);

    public string StrategyId { get; }

    public string Version { get; }

    public Symbol Symbol { get; }

    public int EntryPeriod { get; }

    public int ExitPeriod { get; }

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
                EntryPeriod.ToString(CultureInfo.InvariantCulture),
                ExitPeriod.ToString(CultureInfo.InvariantCulture),
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

/// <summary>One deterministic decision output for a closed bar in the Breakout strategy.</summary>
public sealed record BreakoutDecision(
    Symbol Symbol,
    DateTimeOffset CloseTimeUtc,
    int BarIndex,
    Price ClosePrice,
    decimal? EntryChannelHigh,
    decimal? ExitChannelLow,
    BreakoutSignal Signal,
    BreakoutReason Reason,
    decimal FeeRate,
    decimal SlippageRate,
    decimal EstimatedRoundTripCostRate,
    string StrategyId,
    string StrategyVersion,
    string ConfigurationFingerprint);

/// <summary>
/// Pure Donchian / Channel Breakout evaluator for the Momentum baseline.
/// Emits EnterLong upon break of the N-period high and ExitLong upon break below the M-period exit channel.
/// </summary>
public sealed class BreakoutStrategy
{
    public BreakoutStrategy(BreakoutParameters parameters)
    {
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
    }

    public BreakoutParameters Parameters { get; }

    public IReadOnlyList<BreakoutDecision> Evaluate(IReadOnlyList<CloseBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ValidateBars(bars);

        var decisions = new BreakoutDecision[bars.Count];
        var roundTripCost = (Parameters.FeeRate + Parameters.SlippageRate) * 2m;
        var inPosition = false;

        for (var index = 0; index < bars.Count; index++)
        {
            if (index < Parameters.EntryPeriod)
            {
                decisions[index] = new(
                    bars[index].Symbol,
                    bars[index].CloseTimeUtc,
                    index,
                    bars[index].ClosePrice,
                    EntryChannelHigh: null,
                    ExitChannelLow: null,
                    BreakoutSignal.NoTrade,
                    BreakoutReason.InsufficientHistory,
                    Parameters.FeeRate,
                    Parameters.SlippageRate,
                    roundTripCost,
                    Parameters.StrategyId,
                    Parameters.Version,
                    Parameters.ConfigurationFingerprint);
                continue;
            }

            // Entry Channel High over previous EntryPeriod bars [index - EntryPeriod .. index - 1]
            decimal entryHigh = decimal.MinValue;
            for (var k = 1; k <= Parameters.EntryPeriod; k++)
            {
                var price = bars[index - k].ClosePrice.Value;
                if (price > entryHigh)
                {
                    entryHigh = price;
                }
            }

            // Exit Channel Low over previous ExitPeriod bars [index - ExitPeriod .. index - 1]
            decimal exitLow = decimal.MaxValue;
            for (var k = 1; k <= Parameters.ExitPeriod; k++)
            {
                var price = bars[index - k].ClosePrice.Value;
                if (price < exitLow)
                {
                    exitLow = price;
                }
            }

            var currentPrice = bars[index].ClosePrice.Value;
            var signal = BreakoutSignal.NoTrade;
            var reason = BreakoutReason.ChannelUnchanged;

            if (inPosition)
            {
                if (currentPrice < exitLow)
                {
                    signal = BreakoutSignal.ExitLong;
                    reason = BreakoutReason.ChannelDroppedBelowExit;
                    inPosition = false;
                }
            }
            else
            {
                if (currentPrice > entryHigh)
                {
                    signal = BreakoutSignal.EnterLong;
                    reason = BreakoutReason.ChannelBreakoutAbove;
                    inPosition = true;
                }
            }

            decisions[index] = new(
                bars[index].Symbol,
                bars[index].CloseTimeUtc,
                index,
                bars[index].ClosePrice,
                entryHigh,
                exitLow,
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
