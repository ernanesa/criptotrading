using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;

namespace CryptoTrading.Domain.Execution;

/// <summary>Best bid/ask evidence used to plan one bounded passive execution attempt.</summary>
public sealed record ExecutionMarketSnapshot
{
    public ExecutionMarketSnapshot(
        Price bestBid,
        Price bestAsk,
        decimal availableLiquidityNotional,
        DateTimeOffset observedAtUtc,
        bool isComplete = true)
    {
        if (bestAsk.Value < bestBid.Value)
        {
            throw new ArgumentException("Best ask cannot be below best bid.", nameof(bestAsk));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(availableLiquidityNotional);

        if (observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Execution market timestamps must be UTC.", nameof(observedAtUtc));
        }

        BestBid = bestBid;
        BestAsk = bestAsk;
        AvailableLiquidityNotional = availableLiquidityNotional;
        ObservedAtUtc = observedAtUtc;
        IsComplete = isComplete;
    }

    public Price BestBid { get; }
    public Price BestAsk { get; }
    public decimal AvailableLiquidityNotional { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public bool IsComplete { get; }
}

/// <summary>Inputs for one deterministic execution attempt.</summary>
public sealed record ExecutionPlanRequest
{
    public ExecutionPlanRequest(
        OrderIntent intent,
        RiskDecision decision,
        ExecutionMarketSnapshot market,
        DateTimeOffset evaluatedAtUtc,
        TimeSpan maxMarketDataAge,
        TimeSpan timeToLive,
        int attempt = 0,
        decimal? availableAccountLiquidityNotional = null)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(market);
        if (evaluatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Execution evaluation time must be UTC.", nameof(evaluatedAtUtc));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxMarketDataAge, TimeSpan.Zero);

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeToLive, TimeSpan.Zero);
        if (timeToLive > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(timeToLive), "Execution TTL cannot exceed 24 hours.");
        }

        if (attempt < 0 || attempt > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        if (availableAccountLiquidityNotional is < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(availableAccountLiquidityNotional));
        }

        Intent = intent;
        Decision = decision;
        Market = market;
        EvaluatedAtUtc = evaluatedAtUtc;
        MaxMarketDataAge = maxMarketDataAge;
        TimeToLive = timeToLive;
        Attempt = attempt;
        AvailableAccountLiquidityNotional = availableAccountLiquidityNotional;
    }

    public OrderIntent Intent { get; }
    public RiskDecision Decision { get; }
    public ExecutionMarketSnapshot Market { get; }
    public DateTimeOffset EvaluatedAtUtc { get; }
    public TimeSpan MaxMarketDataAge { get; }
    public TimeSpan TimeToLive { get; }
    public int Attempt { get; }
    public decimal? AvailableAccountLiquidityNotional { get; }
}

/// <summary>One passive limit leg; quantity is never larger than the authorized target.</summary>
public sealed record ExecutionPlan(
    OrderId OrderId,
    ClientOrderId ClientOrderId,
    IdempotencyKey IdempotencyKey,
    Symbol Symbol,
    OrderSide Side,
    Quantity Quantity,
    Price LimitPrice,
    decimal AuthorizedNotional,
    DateTimeOffset ExpiresAtUtc,
    int Attempt);

public sealed record ExecutionPlanResult(ExecutionPlan? Plan, string? NoTradeReason)
{
    public bool IsNoTrade => Plan is null;
}

/// <summary>
/// Pure passive-limit planner. It never sends, cancels or retries an exchange order.
/// Any stale/incomplete market evidence returns an explicit NoTrade result.
/// </summary>
public static class ExecutionPlanner
{
    public const string MarketDataUnavailable = "EXECUTION_MARKET_DATA_UNAVAILABLE";
    public const string MarketDataStale = "EXECUTION_MARKET_DATA_STALE";
    public const string LiquidityUnavailable = "EXECUTION_LIQUIDITY_UNAVAILABLE";
    public const string TargetNotAuthorized = "EXECUTION_TARGET_NOT_AUTHORIZED";
    public const string AccountLiquidityUnavailable = "EXECUTION_ACCOUNT_LIQUIDITY_UNAVAILABLE";

    public static ExecutionPlanResult Plan(ExecutionPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var intent = request.Intent;
        var decision = request.Decision;
        var market = request.Market;

        if (!decision.AllowsNewExposure || decision.AuthorizedTargetNotional <= 0m)
        {
            return new(null, TargetNotAuthorized);
        }

        if (!market.IsComplete)
        {
            return new(null, MarketDataUnavailable);
        }

        if (market.ObservedAtUtc > request.EvaluatedAtUtc ||
            request.EvaluatedAtUtc - market.ObservedAtUtc > request.MaxMarketDataAge)
        {
            return new(null, MarketDataStale);
        }

        if (market.AvailableLiquidityNotional < decision.AuthorizedTargetNotional)
        {
            return new(null, LiquidityUnavailable);
        }

        // A buy consumes quote balance, while a sell reduces the already
        // reconciled base position. Never let a buy exceed free quote balance;
        // the caller may reserve fees by passing a smaller available amount.
        if (intent.Side == OrderSide.Buy &&
            request.AvailableAccountLiquidityNotional is { } availableQuote &&
            decision.AuthorizedTargetNotional >= availableQuote)
        {
            return new(null, AccountLiquidityUnavailable);
        }

        var limitPrice = intent.Side is OrderSide.Buy ? market.BestBid : market.BestAsk;
        var quantity = new Quantity(decision.AuthorizedTargetNotional / limitPrice.Value);
        if (quantity.Value <= 0m)
        {
            return new(null, TargetNotAuthorized);
        }

        var identity = BuildIdentity(intent, request.Attempt, limitPrice, quantity);
        return new(
            new ExecutionPlan(
                intent.OrderId,
                identity.ClientOrderId,
                identity.IdempotencyKey,
                intent.Symbol,
                intent.Side,
                quantity,
                limitPrice,
                decision.AuthorizedTargetNotional,
                request.EvaluatedAtUtc + request.TimeToLive,
                request.Attempt),
            null);
    }

    private static PlanIdentity BuildIdentity(
        OrderIntent intent,
        int attempt,
        Price price,
        Quantity quantity)
    {
        var canonical = string.Join(
            '|',
            intent.OrderId.ToString(),
            intent.Symbol.Value,
            intent.Side,
            intent.IdempotencyKey.Value,
            attempt.ToString(CultureInfo.InvariantCulture),
            price.Value.ToString("G29", CultureInfo.InvariantCulture),
            quantity.Value.ToString("G29", CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var token = Convert.ToHexString(hash[..16]);
        return new(new ClientOrderId($"ctp-{token}"), new IdempotencyKey($"execution:{token}"));
    }

    private sealed record PlanIdentity(ClientOrderId ClientOrderId, IdempotencyKey IdempotencyKey);
}
