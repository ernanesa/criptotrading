using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Backtesting.Strategies;

/// <summary>One closed market bar consumed by a deterministic strategy evaluation.</summary>
public sealed record CloseBar
{
    public CloseBar(
        Symbol symbol,
        DateTimeOffset closeTimeUtc,
        decimal closePrice,
        decimal? openPrice = null,
        decimal? highPrice = null,
        decimal? lowPrice = null,
        decimal volume = 0m,
        decimal takerBuyBaseVolume = 0m,
        decimal quoteVolume = 0m,
        long numberOfTrades = 0)
    {
        if (closeTimeUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Bar close time must be UTC.", nameof(closeTimeUtc));
        }

        if (closePrice <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(closePrice), "Bar close price must be greater than zero.");
        }

        var effectiveOpen = openPrice is > 0m ? openPrice.Value : closePrice;
        var rawHigh = highPrice is > 0m ? highPrice.Value : Math.Max(effectiveOpen, closePrice);
        var rawLow = lowPrice is > 0m ? lowPrice.Value : Math.Min(effectiveOpen, closePrice);
        var effectiveHigh = Math.Max(rawHigh, Math.Max(effectiveOpen, closePrice));
        var effectiveLow = Math.Min(rawLow, Math.Min(effectiveOpen, closePrice));

        Symbol = symbol;
        CloseTimeUtc = closeTimeUtc;
        ClosePrice = new Price(closePrice);
        OpenPrice = new Price(effectiveOpen);
        HighPrice = new Price(effectiveHigh);
        LowPrice = new Price(effectiveLow);
        Volume = Math.Max(0m, volume);
        TakerBuyBaseVolume = Math.Clamp(takerBuyBaseVolume, 0m, Volume > 0m ? Volume : Math.Max(0m, takerBuyBaseVolume));
        QuoteVolume = Math.Max(0m, quoteVolume);
        NumberOfTrades = Math.Max(0L, numberOfTrades);
    }

    public Symbol Symbol { get; }

    public DateTimeOffset CloseTimeUtc { get; }

    public Price ClosePrice { get; }

    public Price OpenPrice { get; }

    public Price HighPrice { get; }

    public Price LowPrice { get; }

    public decimal Volume { get; }

    public decimal TakerBuyBaseVolume { get; }

    public decimal QuoteVolume { get; }

    public long NumberOfTrades { get; }

    public bool Equals(CloseBar? other) =>
        other is not null &&
        Symbol == other.Symbol &&
        CloseTimeUtc == other.CloseTimeUtc &&
        ClosePrice == other.ClosePrice;

    public override int GetHashCode() =>
        HashCode.Combine(Symbol, CloseTimeUtc, ClosePrice);
}

/// <summary>Long-only signals used by the Spot baseline. No signal is an intentional outcome.</summary>
public enum TrendFollowingSignal
{
    NoTrade,
    EnterLong,
    ExitLong,
}

/// <summary>Stable reason codes for audit and reproducible research output.</summary>
public enum TrendFollowingReason
{
    InsufficientHistory,
    TrendUnchanged,
    FastCrossedAboveSlow,
    FastCrossedBelowSlow,
}

/// <summary>
/// Versioned assumptions for the BTCUSDT Spot trend baseline.
/// Fee and slippage rates are decimal fractions of notional (for example, 0.001 = 10 bps).
/// </summary>
public sealed record TrendFollowingParameters
{
    public TrendFollowingParameters(
        string strategyId,
        string version,
        Symbol symbol,
        int fastPeriod,
        int slowPeriod,
        decimal feeRate,
        decimal slippageRate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (fastPeriod < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(fastPeriod), "Fast period must be at least two bars.");
        }

        if (slowPeriod <= fastPeriod)
        {
            throw new ArgumentOutOfRangeException(nameof(slowPeriod), "Slow period must be greater than fast period.");
        }

        ValidateRate(feeRate, nameof(feeRate));
        ValidateRate(slippageRate, nameof(slippageRate));

        StrategyId = strategyId.Trim();
        Version = version.Trim();
        Symbol = symbol;
        FastPeriod = fastPeriod;
        SlowPeriod = slowPeriod;
        FeeRate = feeRate;
        SlippageRate = slippageRate;
    }

    /// <summary>Approved starting parameters for the BTCUSDT Spot baseline.</summary>
    public static TrendFollowingParameters BtcUsdtBaseline { get; } = new(
        "trend-following-btcusdt",
        "1.0.0",
        new Symbol("BTCUSDT"),
        fastPeriod: 20,
        slowPeriod: 50,
        feeRate: 0.001m,
        slippageRate: 0.0005m);

    public string StrategyId { get; }

    public string Version { get; }

    public Symbol Symbol { get; }

    public int FastPeriod { get; }

    public int SlowPeriod { get; }

    public decimal FeeRate { get; }

    public decimal SlippageRate { get; }

    /// <summary>SHA-256 identity of every parameter affecting the strategy output or cost assumptions.</summary>
    public string ConfigurationFingerprint
    {
        get
        {
            var canonical = string.Join(
                '|',
                StrategyId,
                Version,
                Symbol.Value,
                FastPeriod.ToString(CultureInfo.InvariantCulture),
                SlowPeriod.ToString(CultureInfo.InvariantCulture),
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

/// <summary>One deterministic decision for one closed bar.</summary>
public sealed record TrendFollowingDecision(
    Symbol Symbol,
    DateTimeOffset CloseTimeUtc,
    int BarIndex,
    Price ClosePrice,
    decimal? FastEma,
    decimal? SlowEma,
    TrendFollowingSignal Signal,
    TrendFollowingReason Reason,
    decimal FeeRate,
    decimal SlippageRate,
    decimal EstimatedRoundTripCostRate,
    string StrategyId,
    string StrategyVersion,
    string ConfigurationFingerprint);

/// <summary>
/// Pure EMA crossover evaluator for the Spot baseline. It consumes closed bars only and never submits orders.
/// </summary>
public sealed class TrendFollowingStrategy
{
    public TrendFollowingStrategy(TrendFollowingParameters parameters)
    {
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
    }

    public TrendFollowingParameters Parameters { get; }

    public IReadOnlyList<TrendFollowingDecision> Evaluate(IReadOnlyList<CloseBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ValidateBars(bars);

        var fastEma = CalculateEma(bars, Parameters.FastPeriod);
        var slowEma = CalculateEma(bars, Parameters.SlowPeriod);
        var decisions = new TrendFollowingDecision[bars.Count];
        var roundTripCost = (Parameters.FeeRate + Parameters.SlippageRate) * 2m;

        for (var index = 0; index < bars.Count; index++)
        {
            var signal = TrendFollowingSignal.NoTrade;
            var reason = TrendFollowingReason.InsufficientHistory;

            if (index >= Parameters.SlowPeriod && fastEma[index].HasValue && slowEma[index].HasValue &&
                fastEma[index - 1].HasValue && slowEma[index - 1].HasValue)
            {
                if (fastEma[index - 1] <= slowEma[index - 1] && fastEma[index] > slowEma[index])
                {
                    signal = TrendFollowingSignal.EnterLong;
                    reason = TrendFollowingReason.FastCrossedAboveSlow;
                }
                else if (fastEma[index - 1] >= slowEma[index - 1] && fastEma[index] < slowEma[index])
                {
                    signal = TrendFollowingSignal.ExitLong;
                    reason = TrendFollowingReason.FastCrossedBelowSlow;
                }
                else
                {
                    reason = TrendFollowingReason.TrendUnchanged;
                }
            }

            decisions[index] = new(
                bars[index].Symbol,
                bars[index].CloseTimeUtc,
                index,
                bars[index].ClosePrice,
                fastEma[index],
                slowEma[index],
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

    private static decimal?[] CalculateEma(IReadOnlyList<CloseBar> bars, int period)
    {
        var values = new decimal?[bars.Count];
        if (bars.Count < period)
        {
            return values;
        }

        decimal sum = 0m;
        for (var index = 0; index < period; index++)
        {
            sum += bars[index].ClosePrice.Value;
        }

        values[period - 1] = sum / period;
        var smoothing = 2m / (period + 1m);
        for (var index = period; index < bars.Count; index++)
        {
            values[index] = ((bars[index].ClosePrice.Value - values[index - 1]!.Value) * smoothing) + values[index - 1]!.Value;
        }

        return values;
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
