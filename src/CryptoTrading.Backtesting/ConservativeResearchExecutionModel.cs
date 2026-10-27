using System.Globalization;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Backtesting;

/// <summary>
/// Intrabar OHLCV market bar for conservative research execution modeling.
/// </summary>
public sealed record ResearchBar
{
    public ResearchBar(
        Symbol symbol,
        DateTimeOffset openTimeUtc,
        DateTimeOffset closeTimeUtc,
        decimal openPrice,
        decimal highPrice,
        decimal lowPrice,
        decimal closePrice,
        decimal volume)
    {
        if (openTimeUtc.Offset != TimeSpan.Zero || closeTimeUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Bar timestamps must be UTC.");
        }

        if (closeTimeUtc <= openTimeUtc)
        {
            throw new ArgumentException("Bar close time must be strictly greater than open time.");
        }

        if (openPrice <= 0m || highPrice <= 0m || lowPrice <= 0m || closePrice <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(openPrice), "Prices must be positive.");
        }

        if (lowPrice > highPrice || openPrice < lowPrice || openPrice > highPrice || closePrice < lowPrice || closePrice > highPrice)
        {
            throw new ArgumentException("Invalid OHLC relationship: high must be maximum and low must be minimum.");
        }

        if (volume < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(volume), "Volume cannot be negative.");
        }

        Symbol = symbol;
        OpenTimeUtc = openTimeUtc;
        CloseTimeUtc = closeTimeUtc;
        OpenPrice = new Price(openPrice);
        HighPrice = new Price(highPrice);
        LowPrice = new Price(lowPrice);
        ClosePrice = new Price(closePrice);
        Volume = volume;
    }

    public Symbol Symbol { get; }
    public DateTimeOffset OpenTimeUtc { get; }
    public DateTimeOffset CloseTimeUtc { get; }
    public Price OpenPrice { get; }
    public Price HighPrice { get; }
    public Price LowPrice { get; }
    public Price ClosePrice { get; }
    public decimal Volume { get; }

    public CloseBar ToCloseBar() => new(Symbol, CloseTimeUtc, ClosePrice.Value);
}

/// <summary>
/// Simulated exchange filter constraints for spot symbol execution.
/// </summary>
public sealed record ResearchExchangeFilters
{
    public ResearchExchangeFilters(
        decimal priceTickSize,
        decimal quantityStepSize,
        decimal minimumQuantity,
        decimal maximumQuantity,
        decimal minimumNotional)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(priceTickSize, 0m);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(quantityStepSize, 0m);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(minimumQuantity, 0m);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumQuantity, minimumQuantity);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumNotional, 0m);

        PriceTickSize = priceTickSize;
        QuantityStepSize = quantityStepSize;
        MinimumQuantity = minimumQuantity;
        MaximumQuantity = maximumQuantity;
        MinimumNotional = minimumNotional;
    }

    public decimal PriceTickSize { get; }
    public decimal QuantityStepSize { get; }
    public decimal MinimumQuantity { get; }
    public decimal MaximumQuantity { get; }
    public decimal MinimumNotional { get; }

    public decimal QuantizePrice(decimal price)
    {
        if (PriceTickSize <= 0m) return price;
        return Math.Floor(price / PriceTickSize) * PriceTickSize;
    }

    public decimal QuantizeQuantity(decimal quantity)
    {
        if (QuantityStepSize <= 0m) return quantity;
        return Math.Floor(quantity / QuantityStepSize) * QuantityStepSize;
    }
}

/// <summary>Order requested for simulation under conservative execution rules.</summary>
public sealed record ConservativeExecutionOrder(
    int SubmissionBarIndex,
    StrategyExecutionAction Action,
    decimal RequestedQuantity,
    decimal? LimitPrice = null);

/// <summary>Result of simulating order execution against intrabar OHLCV data.</summary>
public sealed record ConservativeExecutionFillResult(
    bool IsFilled,
    StrategyExecutionOutcome Outcome,
    int SubmissionBarIndex,
    int ExecutionBarIndex,
    decimal ExecutedQuantity,
    decimal ExecutedPrice,
    decimal ExecutedNotional,
    decimal Fee,
    decimal SpreadCost,
    decimal SlippageCost,
    string? RejectionReason = null);

/// <summary>
/// Conservative execution simulator that models intrabar price boundaries,
/// exchange filter quantization, execution latency, and volume participation caps.
/// </summary>
public sealed class ConservativeResearchExecutionModel
{
    public ConservativeResearchExecutionModel(
        ResearchExchangeFilters filters,
        int latencyBars = 1,
        decimal maxVolumeParticipationRate = 0.05m,
        decimal feeRate = 0.0010m,
        decimal halfSpreadRate = 0.0005m,
        decimal slippageRate = 0.0005m)
    {
        Filters = filters ?? throw new ArgumentNullException(nameof(filters));
        ArgumentOutOfRangeException.ThrowIfLessThan(latencyBars, 1);
        if (maxVolumeParticipationRate is <= 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(maxVolumeParticipationRate));
        }

        LatencyBars = latencyBars;
        MaxVolumeParticipationRate = maxVolumeParticipationRate;
        FeeRate = feeRate;
        HalfSpreadRate = halfSpreadRate;
        SlippageRate = slippageRate;
    }

    public ResearchExchangeFilters Filters { get; }
    public int LatencyBars { get; }
    public decimal MaxVolumeParticipationRate { get; }
    public decimal FeeRate { get; }
    public decimal HalfSpreadRate { get; }
    public decimal SlippageRate { get; }

    public ConservativeExecutionFillResult SimulateFill(
        ConservativeExecutionOrder order,
        IReadOnlyList<ResearchBar> bars,
        decimal availableCash,
        decimal availablePosition)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(bars);

        var executionBarIndex = order.SubmissionBarIndex + LatencyBars;
        if (executionBarIndex >= bars.Count)
        {
            return new(
                IsFilled: false,
                Outcome: StrategyExecutionOutcome.NoTrade,
                SubmissionBarIndex: order.SubmissionBarIndex,
                ExecutionBarIndex: executionBarIndex,
                ExecutedQuantity: 0m,
                ExecutedPrice: 0m,
                ExecutedNotional: 0m,
                Fee: 0m,
                SpreadCost: 0m,
                SlippageCost: 0m,
                RejectionReason: "Order latency extends beyond available bars.");
        }

        var bar = bars[executionBarIndex];
        var isBuy = order.Action is StrategyExecutionAction.EnterLong or StrategyExecutionAction.BuyBaseQuantity;
        var isSell = order.Action is StrategyExecutionAction.ExitLong or StrategyExecutionAction.SellBaseQuantity;

        if (!isBuy && !isSell)
        {
            return new(
                IsFilled: false,
                Outcome: StrategyExecutionOutcome.NoTrade,
                SubmissionBarIndex: order.SubmissionBarIndex,
                ExecutionBarIndex: executionBarIndex,
                ExecutedQuantity: 0m,
                ExecutedPrice: 0m,
                ExecutedNotional: 0m,
                Fee: 0m,
                SpreadCost: 0m,
                SlippageCost: 0m);
        }

        // Intrabar price checks
        decimal executedPrice;
        var adverseRate = HalfSpreadRate + SlippageRate;

        if (order.LimitPrice.HasValue)
        {
            var limit = order.LimitPrice.Value;
            if (isBuy && limit < bar.LowPrice.Value)
            {
                // Market never traded low enough to trigger buy limit
                return Unfilled(order.SubmissionBarIndex, executionBarIndex, "Price never touched limit price intrabar.");
            }

            if (isSell && limit > bar.HighPrice.Value)
            {
                // Market never traded high enough to trigger sell limit
                return Unfilled(order.SubmissionBarIndex, executionBarIndex, "Price never touched limit price intrabar.");
            }

            // Fill at limit or intrabar conservative price
            executedPrice = isBuy
                ? Math.Clamp(limit * (1m + adverseRate), bar.LowPrice.Value, bar.HighPrice.Value)
                : Math.Clamp(limit * (1m - adverseRate), bar.LowPrice.Value, bar.HighPrice.Value);
        }
        else
        {
            executedPrice = isBuy
                ? Math.Clamp(bar.ClosePrice.Value * (1m + adverseRate), bar.LowPrice.Value, bar.HighPrice.Value)
                : Math.Clamp(bar.ClosePrice.Value * (1m - adverseRate), bar.LowPrice.Value, bar.HighPrice.Value);
        }

        executedPrice = Filters.QuantizePrice(executedPrice);

        if (order.LimitPrice is { } orderLimit &&
            ((isBuy && executedPrice > orderLimit) || (isSell && executedPrice < orderLimit)))
        {
            return Unfilled(order.SubmissionBarIndex, executionBarIndex, "Simulated execution price violates the order limit.");
        }

        // Filter: quantity quantization
        var requestedQuantized = Filters.QuantizeQuantity(order.RequestedQuantity);
        if (requestedQuantized < Filters.MinimumQuantity || requestedQuantized > Filters.MaximumQuantity)
        {
            return Rejected(
                StrategyExecutionOutcome.RejectedFilterViolation,
                order.SubmissionBarIndex,
                executionBarIndex,
                $"Quantity {requestedQuantized} outside filter limits [{Filters.MinimumQuantity}, {Filters.MaximumQuantity}].");
        }

        if (requestedQuantized * executedPrice < Filters.MinimumNotional)
        {
            return Rejected(
                StrategyExecutionOutcome.RejectedMinimumNotional,
                order.SubmissionBarIndex,
                executionBarIndex,
                $"Notional {requestedQuantized * executedPrice} below minimum notional {Filters.MinimumNotional}.");
        }

        // Volume participation cap
        var volumeParticipationCap = Filters.QuantizeQuantity(bar.Volume * MaxVolumeParticipationRate);
        var executableQuantity = Math.Min(requestedQuantized, volumeParticipationCap);

        // Cash / Position headroom
        if (isBuy)
        {
            var maxAffordable = Filters.QuantizeQuantity(availableCash / (executedPrice * (1m + FeeRate)));
            executableQuantity = Math.Min(executableQuantity, maxAffordable);
        }
        else
        {
            var maxSellable = Filters.QuantizeQuantity(availablePosition);
            executableQuantity = Math.Min(executableQuantity, maxSellable);
        }

        if (executableQuantity <= 0m)
        {
            return Rejected(
                StrategyExecutionOutcome.RejectedInsufficientLiquidity,
                order.SubmissionBarIndex,
                executionBarIndex,
                "Insufficient capital, position or volume participation headroom.");
        }

        var executedNotional = executableQuantity * executedPrice;
        if (executedNotional < Filters.MinimumNotional)
        {
            return Rejected(
                StrategyExecutionOutcome.RejectedMinimumNotional,
                order.SubmissionBarIndex,
                executionBarIndex,
                $"Partially filled notional {executedNotional} falls below minimum notional {Filters.MinimumNotional}.");
        }

        var isPartial = executableQuantity < requestedQuantized;
        var outcome = isPartial ? StrategyExecutionOutcome.PartiallyFilled : StrategyExecutionOutcome.Filled;
        var fee = executedNotional * FeeRate;
        var spreadCost = executableQuantity * bar.ClosePrice.Value * HalfSpreadRate;
        var slippageCost = executableQuantity * bar.ClosePrice.Value * SlippageRate;

        return new(
            IsFilled: true,
            Outcome: outcome,
            SubmissionBarIndex: order.SubmissionBarIndex,
            ExecutionBarIndex: executionBarIndex,
            ExecutedQuantity: executableQuantity,
            ExecutedPrice: executedPrice,
            ExecutedNotional: executedNotional,
            Fee: fee,
            SpreadCost: spreadCost,
            SlippageCost: slippageCost);
    }

    private static ConservativeExecutionFillResult Unfilled(int submissionIndex, int executionIndex, string reason) =>
        new(false, StrategyExecutionOutcome.NoTrade, submissionIndex, executionIndex, 0m, 0m, 0m, 0m, 0m, 0m, reason);

    private static ConservativeExecutionFillResult Rejected(
        StrategyExecutionOutcome outcome, int submissionIndex, int executionIndex, string reason) =>
        new(false, outcome, submissionIndex, executionIndex, 0m, 0m, 0m, 0m, 0m, 0m, reason);
}
