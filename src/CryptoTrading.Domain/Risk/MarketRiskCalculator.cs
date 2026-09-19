namespace CryptoTrading.Domain.Risk;

/// <summary>Minimal candle input required to derive deterministic market risk.</summary>
public sealed record MarketCandle
{
    public MarketCandle(DateTimeOffset openTime, decimal close)
    {
        if (openTime.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Candle timestamps must be UTC.", nameof(openTime));
        }

        if (close <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(close), "Candle close must be positive.");
        }

        OpenTime = openTime;
        Close = close;
    }

    public DateTimeOffset OpenTime { get; }
    public decimal Close { get; }
}

/// <summary>Generic local order-book level, deliberately independent of an exchange SDK.</summary>
public sealed record MarketBookLevel
{
    public MarketBookLevel(decimal price, decimal quantity)
    {
        if (price <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(price), "Book price must be positive.");
        }

        if (quantity < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Book quantity cannot be negative.");
        }

        Price = price;
        Quantity = quantity;
    }

    public decimal Price { get; }
    public decimal Quantity { get; }
}

public enum MarketRiskSide
{
    Buy,
    Sell
}

/// <summary>
/// Derives conservative, point-in-time market-risk values from local candles and a
/// sequence-validated local book. It never calls an exchange and returns an incomplete
/// snapshot when inputs are insufficient, allowing the policy to fail closed.
/// </summary>
public static class MarketRiskCalculator
{
    public static MarketRiskSnapshot Calculate(
        IReadOnlyList<MarketCandle> candles,
        IReadOnlyList<MarketBookLevel> bids,
        IReadOnlyList<MarketBookLevel> asks,
        MarketRiskSide side,
        DateTimeOffset observedAt,
        int minimumCandles = 2)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(bids);
        ArgumentNullException.ThrowIfNull(asks);
        if (observedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Market-risk timestamps must be UTC.", nameof(observedAt));
        }

        if (minimumCandles < 2)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(minimumCandles, 2);
        }

        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        var book = side is MarketRiskSide.Buy ? asks : bids;
        var bookCount = side is MarketRiskSide.Buy ? asks.Count : bids.Count;
        if (ordered.Length < minimumCandles || bookCount == 0)
        {
            return new(0m, 0m, 0m, observedAt, isComplete: false);
        }

        var bestBid = bids.Count == 0 ? 0m : bids.Max(level => level.Price);
        var bestAsk = asks.Count == 0 ? 0m : asks.Min(level => level.Price);
        if (bestBid <= 0m || bestAsk <= 0m || bestAsk < bestBid)
        {
            return new(0m, 0m, 0m, observedAt, isComplete: false);
        }

        var mid = (bestBid + bestAsk) / 2m;
        var executionPrice = side is MarketRiskSide.Buy ? bestAsk : bestBid;
        var slippageBps = Math.Abs(executionPrice - mid) / mid * 10_000m;
        var volatilityBps = CalculateMeanAbsoluteReturnBps(ordered);
        var liquidityNotional = book.Sum(level => level.Price * level.Quantity);
        return new(slippageBps, volatilityBps, liquidityNotional, observedAt);
    }

    private static decimal CalculateMeanAbsoluteReturnBps(MarketCandle[] candles)
    {
        decimal total = 0m;
        for (var index = 1; index < candles.Length; index++)
        {
            total += Math.Abs(candles[index].Close - candles[index - 1].Close) /
                candles[index - 1].Close * 10_000m;
        }

        return total / (candles.Length - 1);
    }
}
