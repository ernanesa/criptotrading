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
        bool isComplete = true,
        decimal? quantityStepSize = null,
        decimal? priceTickSize = null,
        decimal? bidQuantity = null,
        decimal? askQuantity = null,
        decimal? temporalOrderFlowImbalance = null,
        decimal? tradeAggressionDelta = null,
        decimal? depthImbalanceTop5 = null)
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
        QuantityStepSize = quantityStepSize;
        PriceTickSize = priceTickSize;
        BidQuantity = bidQuantity;
        AskQuantity = askQuantity;
        TemporalOrderFlowImbalance = temporalOrderFlowImbalance is { } ofi ? Math.Clamp(Math.Round(ofi, 4), -1m, 1m) : null;
        TradeAggressionDelta = tradeAggressionDelta is { } aggr ? Math.Clamp(Math.Round(aggr, 4), -1m, 1m) : null;
        DepthImbalanceTop5 = depthImbalanceTop5 is { } l2 ? Math.Clamp(Math.Round(l2, 4), -1m, 1m) : null;
    }

    public Price BestBid { get; }
    public Price BestAsk { get; }
    public decimal AvailableLiquidityNotional { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public bool IsComplete { get; }
    public decimal? QuantityStepSize { get; }
    public decimal? PriceTickSize { get; }
    public decimal? BidQuantity { get; }
    public decimal? AskQuantity { get; }
    public decimal? TemporalOrderFlowImbalance { get; }
    public decimal? TradeAggressionDelta { get; }
    public decimal? DepthImbalanceTop5 { get; }
    public decimal OrderBookImbalance =>
        BidQuantity is { } bidQty && AskQuantity is { } askQty && (bidQty + askQty) > 0m
            ? Math.Round((bidQty - askQty) / (bidQty + askQty), 4)
            : 0m;

    /// <summary>
    /// Composite order-flow imbalance blending top-of-book L1 imbalance, Cont-Kukanov-Stoikov (2014)
    /// temporal OFI across quote updates, L2 top-5 depth imbalance, and signed trade aggression delta.
    /// Defaults to <see cref="OrderBookImbalance"/> when L2/OFI telemetry is not supplied.
    /// </summary>
    public decimal EffectiveOrderFlowImbalance
    {
        get
        {
            var l1 = OrderBookImbalance;
            if (TemporalOrderFlowImbalance is null && DepthImbalanceTop5 is null && TradeAggressionDelta is null)
            {
                return l1;
            }

            var ofi = TemporalOrderFlowImbalance ?? l1;
            var l2 = DepthImbalanceTop5 ?? l1;
            var aggr = TradeAggressionDelta ?? ofi;
            return Math.Clamp(Math.Round((l1 * 0.35m) + (ofi * 0.30m) + (l2 * 0.20m) + (aggr * 0.15m), 4), -1m, 1m);
        }
    }

    /// <summary>Arithmetic mid-price between best bid and best ask.</summary>
    public decimal MidPrice => (BestBid.Value + BestAsk.Value) / 2m;

    /// <summary>
    /// Stoikov (2018) volume-weighted Micro-Price:
    /// P_micro = (Q_bid * P_ask + Q_ask * P_bid) / (Q_bid + Q_ask).
    /// Leads arithmetic mid-price by 100-500ms in limit order books.
    /// </summary>
    public decimal MicroPrice =>
        BidQuantity is { } bidQty && AskQuantity is { } askQty && (bidQty + askQty) > 0m
            ? (bidQty * BestAsk.Value + askQty * BestBid.Value) / (bidQty + askQty)
            : MidPrice;

    /// <summary>
    /// Signed divergence between Stoikov Micro-Price and arithmetic Mid-Price in basis points:
    /// (MicroPrice - MidPrice) / MidPrice * 10,000.
    /// </summary>
    public decimal MicroPriceDivergenceBps =>
        MidPrice > 0m
            ? Math.Round((MicroPrice - MidPrice) / MidPrice * 10_000m, 4)
            : 0m;
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
        decimal? availableAccountLiquidityNotional = null,
        FeeReservePolicy? feeReservePolicy = null,
        EffectiveCommissionSnapshot? effectiveCommission = null,
        TimeSpan? commissionMaximumAge = null,
        bool makerFeeEligible = false,
        bool useConservativeCommissionReserve = false,
        ExecutionTacticDecision? tactic = null)
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
        FeeReserve = feeReservePolicy ?? new FeeReservePolicy();
        EffectiveCommission = effectiveCommission;
        CommissionMaximumAge = commissionMaximumAge ?? TimeSpan.FromMinutes(15);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(CommissionMaximumAge, TimeSpan.Zero);
        MakerFeeEligible = makerFeeEligible;
        UseConservativeCommissionReserve = useConservativeCommissionReserve;
        Tactic = tactic;
    }

    public OrderIntent Intent { get; }
    public RiskDecision Decision { get; }
    public ExecutionMarketSnapshot Market { get; }
    public DateTimeOffset EvaluatedAtUtc { get; }
    public TimeSpan MaxMarketDataAge { get; }
    public TimeSpan TimeToLive { get; }
    public int Attempt { get; }
    public decimal? AvailableAccountLiquidityNotional { get; }
    public FeeReservePolicy FeeReserve { get; }
    public EffectiveCommissionSnapshot? EffectiveCommission { get; }
    public TimeSpan CommissionMaximumAge { get; }
    /// <summary>True only when policy and venue semantics prove a maker fee is applicable.</summary>
    public bool MakerFeeEligible { get; }
    public bool UseConservativeCommissionReserve { get; }
    public ExecutionTacticDecision? Tactic { get; }
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
    int Attempt,
    decimal? EstimatedCommissionRate = null,
    string? CommissionSnapshotHash = null,
    string? CommissionSource = null,
    DateTimeOffset? CommissionObservedAtUtc = null,
    DateTimeOffset? CommissionValidUntilUtc = null);

public sealed record ExecutionPlanResult(ExecutionPlan? Plan, string? NoTradeReason)
{
    public bool IsNoTrade => Plan is null;
}

/// <summary>
/// Deterministic native exchange protective OCO exit plan (above <c>LIMIT_MAKER</c> take-profit +
/// below <c>STOP_LOSS_LIMIT</c> stop-loss) aligned to symbol tick and step rules.
/// </summary>
public sealed record ProtectiveOcoPlan(
    Symbol Symbol,
    OrderSide Side,
    Quantity Quantity,
    Price AboveTakeProfitPrice,
    Price BelowStopLossTriggerPrice,
    Price BelowStopLossLimitPrice,
    ClientOrderId ListClientOrderId,
    ClientOrderId AboveClientOrderId,
    ClientOrderId BelowClientOrderId);

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
    public const string CommissionUnavailable = "EXECUTION_COMMISSION_UNAVAILABLE";

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

        // A buy consumes quote balance and must leave a strict positive
        // headroom after the configured fee reserve. A sell reduces the
        // already reconciled base position and never consumes quote reserve.
        decimal? estimatedCommissionRate = null;
        decimal? effectiveCommissionRate = null;
        if (request.EffectiveCommission is { } effectiveCommission)
        {
            var rateAvailable = request.UseConservativeCommissionReserve
                ? effectiveCommission.TryGetConservativeRate(
                    intent.Side,
                    request.EvaluatedAtUtc,
                    request.CommissionMaximumAge,
                    out var rate,
                    out _)
                : effectiveCommission.TryGetEffectiveRate(
                    intent.Side,
                    makerEligible: request.MakerFeeEligible,
                    request.EvaluatedAtUtc,
                    request.CommissionMaximumAge,
                    out rate,
                    out _);
            if (!rateAvailable)
            {
                return new(null, CommissionUnavailable);
            }

            effectiveCommissionRate = rate;
            estimatedCommissionRate = rate;
        }

        if (intent.Side == OrderSide.Buy && request.AvailableAccountLiquidityNotional is { } availableQuote)
        {
            decimal feeReserve;
            if (effectiveCommissionRate is { } effectiveRate)
            {
                if (!request.FeeReserve.TryCalculate(decision.AuthorizedTargetNotional, effectiveRate, out feeReserve))
                {
                    return new(null, CommissionUnavailable);
                }
            }
            else if (!request.FeeReserve.TryCalculate(decision.AuthorizedTargetNotional, out feeReserve))
            {
                return new(null, AccountLiquidityUnavailable);
            }

            try
            {
                if (decision.AuthorizedTargetNotional + feeReserve >= availableQuote)
                {
                    return new(null, AccountLiquidityUnavailable);
                }
            }
            catch (OverflowException)
            {
                return new(null, AccountLiquidityUnavailable);
            }
        }

        var limitPrice = intent.Side is OrderSide.Buy ? market.BestBid : market.BestAsk;
        if (market.PriceTickSize is { } tickSize && tickSize > 0m)
        {
            decimal tickSource;
            if (request.Tactic is { } tacticDecision)
            {
                if (tacticDecision.SelectedTactic == ExecutionTactic.AggressiveTakerImmediate || (intent.Type is OrderType.Market && tacticDecision.RequiresImmediateFill))
                {
                    tickSource = intent.Side is OrderSide.Buy
                        ? market.BestAsk.Value * 1.00015m
                        : market.BestBid.Value * 0.99985m;
                }
                else if (tacticDecision.SelectedTactic == ExecutionTactic.PassiveMakerDeep)
                {
                    tickSource = intent.Side is OrderSide.Buy
                        ? Math.Max(tickSize, market.BestBid.Value - tickSize)
                        : market.BestAsk.Value + tickSize;
                }
                else // PassiveMakerPegged or IcebergChunked (maker)
                {
                    var offset = Math.Max(0, tacticDecision.PegOffsetTicks);
                    var spreadTicks = (market.BestAsk.Value - market.BestBid.Value) / tickSize;
                    if (offset == 0 && spreadTicks >= 2m)
                    {
                        var favorableMicroPrice = intent.Side is OrderSide.Buy
                            ? market.MicroPriceDivergenceBps >= 0.25m
                            : market.MicroPriceDivergenceBps <= -0.25m;
                        if (favorableMicroPrice)
                        {
                            offset = 1;
                        }
                    }

                    if (intent.Side is OrderSide.Buy)
                    {
                        var maxBuy = market.BestAsk.Value - tickSize;
                        tickSource = Math.Min(maxBuy, market.BestBid.Value + (offset * tickSize));
                    }
                    else
                    {
                        var minSell = market.BestBid.Value + tickSize;
                        tickSource = Math.Max(minSell, market.BestAsk.Value - (offset * tickSize));
                    }
                }
            }
            else
            {
                tickSource = intent.Side is OrderSide.Buy && intent.Type is OrderType.Market
                    ? market.BestAsk.Value * 1.00015m
                    : intent.Side is OrderSide.Sell
                        ? market.BestBid.Value * 0.99985m
                        : market.BestBid.Value;
            }

            var ticks = Math.Floor(tickSource / tickSize);
            limitPrice = new Price(ticks * tickSize);
        }

        var rawQuantity = intent.Side is OrderSide.Sell && market.QuantityStepSize is not null && intent.RiskEvaluation.RequestedTargetNotional > 0m
            ? decimal.Round(intent.Quantity.Value * Math.Min(1m, decision.AuthorizedTargetNotional / intent.RiskEvaluation.RequestedTargetNotional), 8)
            : decision.AuthorizedTargetNotional / limitPrice.Value;
        if (market.QuantityStepSize is { } stepSize && stepSize > 0m)
        {
            var steps = Math.Floor(rawQuantity / stepSize);
            rawQuantity = steps * stepSize;
        }

        var quantity = new Quantity(rawQuantity);
        if (quantity.Value <= 0m)
        {
            return new(null, TargetNotAuthorized);
        }

        var authorizedNotional = quantity.Value * limitPrice.Value;

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
                authorizedNotional,
                request.EvaluatedAtUtc + request.TimeToLive,
                request.Attempt,
                estimatedCommissionRate,
                request.EffectiveCommission?.CanonicalPayloadHash,
                request.EffectiveCommission?.Source,
                request.EffectiveCommission?.ObservedAtUtc,
                request.EffectiveCommission is { } snapshot
                    ? snapshot.ObservedAtUtc + request.CommissionMaximumAge
                    : null),
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

    /// <summary>
    /// Computes a deterministic native protective OCO exit plan (Spot long exit: <see cref="OrderSide.Sell"/>)
    /// with tick-aligned <c>LIMIT_MAKER</c> take-profit and <c>STOP_LOSS_LIMIT</c> stop-loss prices.
    /// </summary>
    public static ProtectiveOcoPlan PlanProtectiveOco(
        Symbol symbol,
        Quantity quantity,
        Price entryPrice,
        decimal takeProfitBps = 45m,
        decimal stopLossBps = 18m,
        decimal? priceTickSize = null,
        decimal? quantityStepSize = null,
        string? listSeed = null)
    {
        if (entryPrice.Value <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(entryPrice), "Entry price must be positive.");
        }

        if (takeProfitBps <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(takeProfitBps), "Take-profit basis points must be positive.");
        }

        if (stopLossBps <= 0m || stopLossBps >= 5_000m)
        {
            throw new ArgumentOutOfRangeException(nameof(stopLossBps), "Stop-loss basis points must be between 0 and 5000.");
        }

        var rawQty = quantity.Value;
        if (quantityStepSize is { } step && step > 0m)
        {
            rawQty = Math.Floor(rawQty / step) * step;
        }

        var alignedQty = new Quantity(rawQty);
        var aboveRaw = entryPrice.Value * (1m + (takeProfitBps / 10_000m));
        var belowTriggerRaw = entryPrice.Value * (1m - (stopLossBps / 10_000m));
        var belowLimitRaw = belowTriggerRaw * (1m - 0.0005m);

        decimal aboveAligned;
        decimal belowTriggerAligned;
        decimal belowLimitAligned;
        if (priceTickSize is { } tick && tick > 0m)
        {
            aboveAligned = Math.Max(entryPrice.Value + tick, Math.Ceiling(aboveRaw / tick) * tick);
            belowTriggerAligned = Math.Min(entryPrice.Value - tick, Math.Floor(belowTriggerRaw / tick) * tick);
            belowLimitAligned = Math.Min(belowTriggerAligned - tick, Math.Floor(belowLimitRaw / tick) * tick);
        }
        else
        {
            aboveAligned = decimal.Round(aboveRaw, 8);
            belowTriggerAligned = decimal.Round(belowTriggerRaw, 8);
            belowLimitAligned = decimal.Round(belowLimitRaw, 8);
        }

        var canonical = string.Join(
            '|',
            symbol.Value,
            alignedQty.Value.ToString("G29", CultureInfo.InvariantCulture),
            aboveAligned.ToString("G29", CultureInfo.InvariantCulture),
            belowTriggerAligned.ToString("G29", CultureInfo.InvariantCulture),
            belowLimitAligned.ToString("G29", CultureInfo.InvariantCulture),
            listSeed ?? "default");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var token = Convert.ToHexString(hash[..12]);

        return new ProtectiveOcoPlan(
            symbol,
            OrderSide.Sell,
            alignedQty,
            new Price(aboveAligned),
            new Price(belowTriggerAligned),
            new Price(belowLimitAligned),
            new ClientOrderId($"ocol-{token}"),
            new ClientOrderId($"ocotp-{token}"),
            new ClientOrderId($"ocosl-{token}"));
    }

    private sealed record PlanIdentity(ClientOrderId ClientOrderId, IdempotencyKey IdempotencyKey);
}
