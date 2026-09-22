using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Backtesting.Strategies;

/// <summary>Signals emitted by the Adaptive Grid strategy.</summary>
public enum GridSignal
{
    NoTrade,
    BuyGridLevel,
    SellGridLevel,
}

/// <summary>Reason codes for deterministic grid evaluation.</summary>
public enum GridReason
{
    Initialized,
    BetweenLevels,
    BuyLevelTriggered,
    SellLevelTriggered,
    InventoryCapReached,
    InventoryDepleted,
}

/// <summary>
/// Versioned parameters for the Adaptive Grid strategy.
/// </summary>
public sealed record AdaptiveGridParameters
{
    public AdaptiveGridParameters(
        string strategyId,
        string version,
        Symbol symbol,
        decimal gridSpacingRate,
        int maxLevels,
        decimal quantityPerLevel,
        decimal maxInventoryQuantity,
        decimal feeRate,
        decimal slippageRate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (gridSpacingRate <= 0m || gridSpacingRate >= 0.5m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(gridSpacingRate),
                "Grid spacing rate must be between 0 and 50%.");
        }

        if (maxLevels < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLevels), "Max levels must be at least one.");
        }

        if (quantityPerLevel <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantityPerLevel), "Quantity per level must be positive.");
        }

        if (maxInventoryQuantity < quantityPerLevel)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxInventoryQuantity),
                "Max inventory quantity must be at least one level quantity.");
        }

        ValidateRate(feeRate, nameof(feeRate));
        ValidateRate(slippageRate, nameof(slippageRate));

        StrategyId = strategyId.Trim();
        Version = version.Trim();
        Symbol = symbol;
        GridSpacingRate = gridSpacingRate;
        MaxLevels = maxLevels;
        QuantityPerLevel = quantityPerLevel;
        MaxInventoryQuantity = maxInventoryQuantity;
        FeeRate = feeRate;
        SlippageRate = slippageRate;
    }

    /// <summary>Approved default baseline parameters for BTCUSDT Adaptive Grid.</summary>
    public static AdaptiveGridParameters BtcUsdtBaseline { get; } = new(
        "adaptive-grid-btcusdt",
        "1.0.0",
        new Symbol("BTCUSDT"),
        gridSpacingRate: 0.015m, // 1.5% spacing
        maxLevels: 5,
        quantityPerLevel: 0.01m,
        maxInventoryQuantity: 0.05m,
        feeRate: 0.001m,
        slippageRate: 0.0005m);

    public string StrategyId { get; }

    public string Version { get; }

    public Symbol Symbol { get; }

    public decimal GridSpacingRate { get; }

    public int MaxLevels { get; }

    public decimal QuantityPerLevel { get; }

    public decimal MaxInventoryQuantity { get; }

    public decimal FeeRate { get; }

    public decimal SlippageRate { get; }

    /// <summary>SHA-256 canonical fingerprint of every parameter affecting grid execution.</summary>
    public string ConfigurationFingerprint
    {
        get
        {
            var canonical = string.Join(
                '|',
                StrategyId,
                Version,
                Symbol.Value,
                GridSpacingRate.ToString("G29", CultureInfo.InvariantCulture),
                MaxLevels.ToString(CultureInfo.InvariantCulture),
                QuantityPerLevel.ToString("G29", CultureInfo.InvariantCulture),
                MaxInventoryQuantity.ToString("G29", CultureInfo.InvariantCulture),
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

/// <summary>One deterministic decision output for a closed bar in the Adaptive Grid strategy.</summary>
public sealed record AdaptiveGridDecision(
    Symbol Symbol,
    DateTimeOffset CloseTimeUtc,
    int BarIndex,
    Price ClosePrice,
    decimal AnchorPrice,
    int CurrentGridLevel,
    decimal CurrentInventory,
    GridSignal Signal,
    GridReason Reason,
    decimal TargetQuantity,
    decimal FeeRate,
    decimal SlippageRate,
    decimal EstimatedRoundTripCostRate,
    string StrategyId,
    string StrategyVersion,
    string ConfigurationFingerprint);

/// <summary>
/// Pure Adaptive Grid evaluator. Manages geometric grid levels anchored to initial price,
/// enforcing hard inventory caps and generating deterministic buy/sell level transitions.
/// </summary>
public sealed class AdaptiveGridStrategy
{
    public AdaptiveGridStrategy(AdaptiveGridParameters parameters)
    {
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
    }

    public AdaptiveGridParameters Parameters { get; }

    public IReadOnlyList<AdaptiveGridDecision> Evaluate(IReadOnlyList<CloseBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ValidateBars(bars);

        var decisions = new AdaptiveGridDecision[bars.Count];
        if (bars.Count == 0)
        {
            return decisions;
        }

        var anchorPrice = bars[0].ClosePrice.Value;
        var roundTripCost = (Parameters.FeeRate + Parameters.SlippageRate) * 2m;
        var currentInventory = 0m;
        var activeGridLevel = 0;

        for (var index = 0; index < bars.Count; index++)
        {
            var close = bars[index].ClosePrice.Value;
            var calculatedLevel = (int)Math.Floor((double)((close - anchorPrice) / (anchorPrice * Parameters.GridSpacingRate)));

            // Constrain within [-maxLevels, +maxLevels]
            calculatedLevel = Math.Clamp(calculatedLevel, -Parameters.MaxLevels, Parameters.MaxLevels);

            var signal = GridSignal.NoTrade;
            var reason = GridReason.BetweenLevels;
            var targetQuantity = 0m;

            if (index == 0)
            {
                reason = GridReason.Initialized;
                activeGridLevel = 0;
            }
            else if (calculatedLevel < activeGridLevel)
            {
                // Price dropped down across a level boundary -> Buy grid level
                if (currentInventory + Parameters.QuantityPerLevel <= Parameters.MaxInventoryQuantity)
                {
                    signal = GridSignal.BuyGridLevel;
                    reason = GridReason.BuyLevelTriggered;
                    targetQuantity = Parameters.QuantityPerLevel;
                    currentInventory += Parameters.QuantityPerLevel;
                    activeGridLevel = calculatedLevel;
                }
                else
                {
                    signal = GridSignal.NoTrade;
                    reason = GridReason.InventoryCapReached;
                    activeGridLevel = calculatedLevel;
                }
            }
            else if (calculatedLevel > activeGridLevel)
            {
                // Price rose up across a level boundary -> Sell grid level
                if (currentInventory >= Parameters.QuantityPerLevel)
                {
                    signal = GridSignal.SellGridLevel;
                    reason = GridReason.SellLevelTriggered;
                    targetQuantity = Parameters.QuantityPerLevel;
                    currentInventory -= Parameters.QuantityPerLevel;
                    activeGridLevel = calculatedLevel;
                }
                else
                {
                    signal = GridSignal.NoTrade;
                    reason = GridReason.InventoryDepleted;
                    activeGridLevel = calculatedLevel;
                }
            }

            decisions[index] = new(
                bars[index].Symbol,
                bars[index].CloseTimeUtc,
                index,
                bars[index].ClosePrice,
                anchorPrice,
                activeGridLevel,
                currentInventory,
                signal,
                reason,
                targetQuantity,
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
