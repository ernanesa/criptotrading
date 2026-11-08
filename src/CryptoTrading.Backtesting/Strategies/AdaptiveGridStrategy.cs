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
    MaxDrawdownStopTriggered,
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
        decimal slippageRate,
        bool enableVolatilityScaling = false,
        decimal? maxInventoryDrawdownBps = null)
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

        if (maxInventoryDrawdownBps is <= 0m or >= 10_000m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxInventoryDrawdownBps),
                "Max inventory drawdown must be between 0 and 10,000 bps.");
        }

        StrategyId = strategyId.Trim();
        Version = version.Trim();
        Symbol = symbol;
        GridSpacingRate = gridSpacingRate;
        MaxLevels = maxLevels;
        QuantityPerLevel = quantityPerLevel;
        MaxInventoryQuantity = maxInventoryQuantity;
        FeeRate = feeRate;
        SlippageRate = slippageRate;
        EnableVolatilityScaling = enableVolatilityScaling;
        MaxInventoryDrawdownBps = maxInventoryDrawdownBps;
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

    public bool EnableVolatilityScaling { get; }

    public decimal? MaxInventoryDrawdownBps { get; }

    /// <summary>SHA-256 canonical fingerprint of every parameter affecting grid execution.</summary>
    public string ConfigurationFingerprint
    {
        get
        {
            var parts = new List<string>
            {
                StrategyId,
                Version,
                Symbol.Value,
                GridSpacingRate.ToString("G29", CultureInfo.InvariantCulture),
                MaxLevels.ToString(CultureInfo.InvariantCulture),
                QuantityPerLevel.ToString("G29", CultureInfo.InvariantCulture),
                MaxInventoryQuantity.ToString("G29", CultureInfo.InvariantCulture),
                FeeRate.ToString("G29", CultureInfo.InvariantCulture),
                SlippageRate.ToString("G29", CultureInfo.InvariantCulture)
            };

            if (EnableVolatilityScaling || MaxInventoryDrawdownBps.HasValue)
            {
                parts.Add(EnableVolatilityScaling ? "1" : "0");
                parts.Add(MaxInventoryDrawdownBps?.ToString("G29", CultureInfo.InvariantCulture) ?? "none");
            }

            var canonical = string.Join('|', parts);
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
/// Implements IStatefulStrategy for fill-feedback-driven execution.
/// </summary>
public sealed class AdaptiveGridStrategy : IStatefulStrategy
{
    private decimal? _anchorPrice;
    private decimal _currentInventory;
    private int _activeGridLevel;
    private DateTimeOffset _lastTimestampUtc;
    private decimal _totalEntryNotional;
    private readonly List<decimal> _recentPrices = new();

    public AdaptiveGridStrategy(AdaptiveGridParameters parameters)
    {
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
    }

    public AdaptiveGridParameters Parameters { get; }

    public decimal CurrentInventory => _currentInventory;

    public int ActiveGridLevel => _activeGridLevel;

    public decimal? AnchorPrice => _anchorPrice;

    public StrategyExecutionSignal GenerateSignal(CloseBar bar, int barIndex)
    {
        ArgumentNullException.ThrowIfNull(bar);
        var close = bar.ClosePrice.Value;
        _lastTimestampUtc = bar.CloseTimeUtc;

        _recentPrices.Add(close);
        if (_recentPrices.Count > 20)
        {
            _recentPrices.RemoveAt(0);
        }

        if (_anchorPrice is null || barIndex == 0)
        {
            _anchorPrice = close;
            _activeGridLevel = 0;
            return new StrategyExecutionSignal(barIndex, StrategyExecutionAction.NoTrade, reasonCode: GridReason.Initialized.ToString());
        }

        // Check Max Drawdown Stop
        if (Parameters.MaxInventoryDrawdownBps is { } maxDd && _currentInventory > 0m && _totalEntryNotional > 0m)
        {
            var avgEntry = _totalEntryNotional / _currentInventory;
            var ddBps = (avgEntry - close) / avgEntry * 10_000m;
            if (ddBps >= maxDd)
            {
                var liquidateQty = _currentInventory;
                _anchorPrice = close;
                _activeGridLevel = 0;
                return new StrategyExecutionSignal(
                    barIndex,
                    StrategyExecutionAction.SellBaseQuantity,
                    liquidateQty,
                    reasonCode: GridReason.MaxDrawdownStopTriggered.ToString());
            }
        }

        var effectiveSpacing = Parameters.GridSpacingRate;
        if (Parameters.EnableVolatilityScaling && _recentPrices.Count >= 2)
        {
            decimal totalPctReturn = 0m;
            for (var i = 1; i < _recentPrices.Count; i++)
            {
                totalPctReturn += Math.Abs(_recentPrices[i] - _recentPrices[i - 1]) / _recentPrices[i - 1];
            }
            var avgVol = totalPctReturn / (_recentPrices.Count - 1);
            var expectedVol = Parameters.GridSpacingRate * 0.25m;
            var ratio = avgVol / expectedVol;
            effectiveSpacing = Parameters.GridSpacingRate * Math.Clamp(ratio, 0.5m, 2.5m);
        }

        var anchor = _anchorPrice.Value;
        var calculatedLevel = (int)Math.Floor((double)((close - anchor) / (anchor * effectiveSpacing)));
        calculatedLevel = Math.Clamp(calculatedLevel, -Parameters.MaxLevels, Parameters.MaxLevels);

        if (calculatedLevel < _activeGridLevel)
        {
            if (_currentInventory + Parameters.QuantityPerLevel <= Parameters.MaxInventoryQuantity)
            {
                _activeGridLevel = calculatedLevel;
                return new StrategyExecutionSignal(
                    barIndex,
                    StrategyExecutionAction.BuyBaseQuantity,
                    Parameters.QuantityPerLevel,
                    reasonCode: GridReason.BuyLevelTriggered.ToString());
            }

            _activeGridLevel = calculatedLevel;
            return new StrategyExecutionSignal(
                barIndex,
                StrategyExecutionAction.NoTrade,
                reasonCode: GridReason.InventoryCapReached.ToString());
        }

        if (calculatedLevel > _activeGridLevel)
        {
            if (_currentInventory >= Parameters.QuantityPerLevel)
            {
                _activeGridLevel = calculatedLevel;
                return new StrategyExecutionSignal(
                    barIndex,
                    StrategyExecutionAction.SellBaseQuantity,
                    Parameters.QuantityPerLevel,
                    reasonCode: GridReason.SellLevelTriggered.ToString());
            }

            _activeGridLevel = calculatedLevel;
            return new StrategyExecutionSignal(
                barIndex,
                StrategyExecutionAction.NoTrade,
                reasonCode: GridReason.InventoryDepleted.ToString());
        }

        return new StrategyExecutionSignal(
            barIndex,
            StrategyExecutionAction.NoTrade,
            reasonCode: GridReason.BetweenLevels.ToString());
    }

    public void ApplyExecutionFeedback(StrategyExecutionFeedback feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        _lastTimestampUtc = feedback.TimestampUtc;

        if (feedback.ExecutedQuantity <= 0m)
        {
            return;
        }

        if (feedback.Action is StrategyExecutionAction.BuyBaseQuantity or StrategyExecutionAction.EnterLong)
        {
            _currentInventory = Math.Min(Parameters.MaxInventoryQuantity, _currentInventory + feedback.ExecutedQuantity);
            _totalEntryNotional += feedback.ExecutedQuantity * (feedback.ExecutedPrice > 0m ? feedback.ExecutedPrice : (_anchorPrice ?? 0m));
        }
        else if (feedback.Action is StrategyExecutionAction.SellBaseQuantity or StrategyExecutionAction.ExitLong)
        {
            var reducedQty = Math.Min(_currentInventory, feedback.ExecutedQuantity);
            if (_currentInventory > 0m)
            {
                var avgPrice = _totalEntryNotional / _currentInventory;
                _totalEntryNotional = Math.Max(0m, _totalEntryNotional - (reducedQty * avgPrice));
            }
            _currentInventory = Math.Max(0m, _currentInventory - reducedQty);
            if (_currentInventory == 0m)
            {
                _totalEntryNotional = 0m;
            }
        }
    }

    public StrategyStateSnapshot CaptureState() => new(
        _lastTimestampUtc,
        _currentInventory,
        _activeGridLevel,
        new Dictionary<string, string>
        {
            ["AnchorPrice"] = (_anchorPrice ?? 0m).ToString("G29", CultureInfo.InvariantCulture),
            ["TotalEntryNotional"] = _totalEntryNotional.ToString("G29", CultureInfo.InvariantCulture),
            ["RecentPrices"] = string.Join(',', _recentPrices.Select(p => p.ToString("G29", CultureInfo.InvariantCulture))),
            ["StrategyId"] = Parameters.StrategyId,
            ["Version"] = Parameters.Version,
        });

    public void RestoreState(StrategyStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _currentInventory = snapshot.CurrentInventory;
        _activeGridLevel = snapshot.ActiveLevel;
        _lastTimestampUtc = snapshot.TimestampUtc;
        if (snapshot.Metadata != null)
        {
            if (snapshot.Metadata.TryGetValue("AnchorPrice", out var anchorText) &&
                decimal.TryParse(anchorText, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedAnchor))
            {
                _anchorPrice = parsedAnchor;
            }
            if (snapshot.Metadata.TryGetValue("TotalEntryNotional", out var entryNotionalText) &&
                decimal.TryParse(entryNotionalText, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedNotional))
            {
                _totalEntryNotional = parsedNotional;
            }
            if (snapshot.Metadata.TryGetValue("RecentPrices", out var recentText) && !string.IsNullOrWhiteSpace(recentText))
            {
                _recentPrices.Clear();
                foreach (var part in recentText.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (decimal.TryParse(part, NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
                    {
                        _recentPrices.Add(price);
                    }
                }
            }
        }
    }

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
        var totalEntryNotional = 0m;
        var activeGridLevel = 0;
        var recentPrices = new List<decimal>();

        for (var index = 0; index < bars.Count; index++)
        {
            var close = bars[index].ClosePrice.Value;
            recentPrices.Add(close);
            if (recentPrices.Count > 20)
            {
                recentPrices.RemoveAt(0);
            }

            var signal = GridSignal.NoTrade;
            var reason = GridReason.BetweenLevels;
            var targetQuantity = 0m;

            if (index == 0)
            {
                reason = GridReason.Initialized;
                activeGridLevel = 0;
            }
            else
            {
                // Check Max Drawdown Stop
                if (Parameters.MaxInventoryDrawdownBps is { } maxDd && currentInventory > 0m && totalEntryNotional > 0m)
                {
                    var avgEntry = totalEntryNotional / currentInventory;
                    var ddBps = (avgEntry - close) / avgEntry * 10_000m;
                    if (ddBps >= maxDd)
                    {
                        signal = GridSignal.SellGridLevel;
                        reason = GridReason.MaxDrawdownStopTriggered;
                        targetQuantity = currentInventory;
                        currentInventory = 0m;
                        totalEntryNotional = 0m;
                        anchorPrice = close;
                        activeGridLevel = 0;

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
                        continue;
                    }
                }

                var effectiveSpacing = Parameters.GridSpacingRate;
                if (Parameters.EnableVolatilityScaling && recentPrices.Count >= 2)
                {
                    decimal totalPctReturn = 0m;
                    for (var i = 1; i < recentPrices.Count; i++)
                    {
                        totalPctReturn += Math.Abs(recentPrices[i] - recentPrices[i - 1]) / recentPrices[i - 1];
                    }
                    var avgVol = totalPctReturn / (recentPrices.Count - 1);
                    var expectedVol = Parameters.GridSpacingRate * 0.25m;
                    var ratio = avgVol / expectedVol;
                    effectiveSpacing = Parameters.GridSpacingRate * Math.Clamp(ratio, 0.5m, 2.5m);
                }

                var calculatedLevel = (int)Math.Floor((double)((close - anchorPrice) / (anchorPrice * effectiveSpacing)));
                calculatedLevel = Math.Clamp(calculatedLevel, -Parameters.MaxLevels, Parameters.MaxLevels);

                if (calculatedLevel < activeGridLevel)
                {
                    // Price dropped down across a level boundary -> Buy grid level
                    if (currentInventory + Parameters.QuantityPerLevel <= Parameters.MaxInventoryQuantity)
                    {
                        signal = GridSignal.BuyGridLevel;
                        reason = GridReason.BuyLevelTriggered;
                        targetQuantity = Parameters.QuantityPerLevel;
                        currentInventory += Parameters.QuantityPerLevel;
                        totalEntryNotional += Parameters.QuantityPerLevel * close;
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
                        var avgEntry = currentInventory > 0m ? totalEntryNotional / currentInventory : close;
                        totalEntryNotional = Math.Max(0m, totalEntryNotional - (Parameters.QuantityPerLevel * avgEntry));
                        currentInventory -= Parameters.QuantityPerLevel;
                        if (currentInventory == 0m)
                        {
                            totalEntryNotional = 0m;
                        }
                        activeGridLevel = calculatedLevel;
                    }
                    else
                    {
                        signal = GridSignal.NoTrade;
                        reason = GridReason.InventoryDepleted;
                        activeGridLevel = calculatedLevel;
                    }
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
