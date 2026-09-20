using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;

namespace CryptoTrading.Domain.Architecture;

/// <summary>Pure input exposed to a strategy; it contains no exchange or persistence adapter.</summary>
public sealed record StrategyContext(
    DateTimeOffset AsOfUtc,
    IReadOnlyList<MarketObservation> Market,
    PortfolioSnapshot Portfolio,
    StrategyHealthSnapshot Health);

public sealed record MarketObservation(
    Symbol Symbol,
    DateTimeOffset ObservedAtUtc,
    Price LastPrice,
    Quantity AvailableLiquidity);

public sealed record PortfolioSnapshot(
    IReadOnlyDictionary<Asset, Money> Balances,
    decimal GrossExposureNotional,
    decimal DailyLossNotional,
    decimal Drawdown,
    decimal PositionCostNotional = 0m,
    bool RiskContextComplete = true,
    string? RiskContextUnavailableReason = null);

/// <summary>Strategy produces intents only; it cannot submit or persist an order.</summary>
public interface IStrategy
{
    string StrategyId { get; }

    IReadOnlyList<OrderIntent> Generate(StrategyContext context);
}

/// <summary>Portfolio is a read model consumed by strategy and risk layers.</summary>
public interface IPortfolio
{
    PortfolioSnapshot Snapshot();
}

/// <summary>Risk is the only layer allowed to authorize an intent for execution.</summary>
public interface IRiskGate
{
    OrderExecutionResult Evaluate(
        OrderIntent intent,
        RiskPolicy policy,
        OperationalControlSnapshot operationalControl);
}

/// <summary>Execution persists an already-authorized command before exchange I/O.</summary>
public interface IExecutionPort
{
    ValueTask<ExecutionPortResult> ExecuteAsync(
        SubmitOrderCommand command,
        CancellationToken cancellationToken = default);
}

public sealed record ExecutionPortResult(
    OrderId OrderId,
    ClientOrderId ClientOrderId,
    OrderStatus Status,
    string? ExchangeOrderId,
    bool AlreadyKnown);

/// <summary>OMS owns lifecycle transitions and reconciliation-facing order state.</summary>
public interface IOrderManagement
{
    Order Find(OrderId orderId);

    OrderTransition Transition(
        OrderId orderId,
        OrderStatus target,
        IdempotencyKey idempotencyKey,
        DateTimeOffset occurredAtUtc,
        string? exchangeOrderId = null);
}

/// <summary>Exchange adapter boundary; concrete adapters live outside the domain.</summary>
public interface IExchangePort
{
    ValueTask<ExecutionPortResult> SubmitAsync(
        SubmitOrderCommand command,
        CancellationToken cancellationToken = default);
}
