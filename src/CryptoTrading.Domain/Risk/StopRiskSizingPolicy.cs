namespace CryptoTrading.Domain.Risk;

/// <summary>
/// Optional versioned sizing ceiling, not permission to raise hard risk limits
/// or a guarantee against gaps or execution beyond the modeled slippage.
/// </summary>
public sealed record StopRiskSizingPolicy
{
    public StopRiskSizingPolicy(string version, decimal maximumPlannedLossNotional)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPlannedLossNotional);
        Version = version;
        MaximumPlannedLossNotional = maximumPlannedLossNotional;
    }

    public string Version { get; }
    public decimal MaximumPlannedLossNotional { get; }

    public static decimal FloorQuantity(decimal notionalCeiling, decimal price, decimal? quantityStep = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(notionalCeiling);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(price);
        if (quantityStep is <= 0m) throw new ArgumentOutOfRangeException(nameof(quantityStep));
        var quantity = decimal.Round(notionalCeiling / price, 8, MidpointRounding.ToZero);
        if (quantityStep is { } step) quantity = decimal.Floor(quantity / step) * step;
        if (quantity * price > notionalCeiling) quantity = Math.Max(0m, quantity - (quantityStep ?? 0.00000001m));
        return quantity;
    }

    public decimal CapEntryNotional(decimal requestedNotional, decimal protectiveStopLossBps,
        decimal roundTripFeeBps, decimal roundTripSlippageBps)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requestedNotional);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(protectiveStopLossBps);
        ArgumentOutOfRangeException.ThrowIfNegative(roundTripFeeBps);
        ArgumentOutOfRangeException.ThrowIfNegative(roundTripSlippageBps);
        var rate = (protectiveStopLossBps + roundTripFeeBps + roundTripSlippageBps) / 10_000m;
        if (rate <= 0m) throw new ArgumentOutOfRangeException(nameof(protectiveStopLossBps));
        if (rate <= 1m && MaximumPlannedLossNotional >= requestedNotional * rate) return requestedNotional;
        var cap = MaximumPlannedLossNotional / rate;
        return cap >= requestedNotional ? requestedNotional : decimal.Round(cap, 8, MidpointRounding.ToZero);
    }
}
