using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;

namespace CryptoTrading.Domain.Execution;

/// <summary>A strategy's proposed order before risk authorization.</summary>
public sealed record OrderIntent
{
    public OrderIntent(
        OrderId orderId,
        ClientOrderId clientOrderId,
        Symbol symbol,
        OrderSide side,
        OrderType type,
        Quantity quantity,
        Price referencePrice,
        CorrelationId correlationId,
        IdempotencyKey idempotencyKey,
        RiskEvaluationRequest riskEvaluation,
        DateTimeOffset createdAtUtc,
        decimal? estimatedCommissionRate = null,
        string? commissionSnapshotHash = null,
        string? commissionSource = null,
        DateTimeOffset? commissionObservedAtUtc = null,
        DateTimeOffset? commissionValidUntilUtc = null,
        int executionAttempt = 0,
        DateTimeOffset? executionExpiresAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(riskEvaluation);

        if (createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Order intent timestamp must be UTC.", nameof(createdAtUtc));
        }

        if (quantity.Value <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Order intent quantity must be greater than zero.");
        }

        var calculatedNotional = quantity.Value * referencePrice.Value;
        if (calculatedNotional != riskEvaluation.RequestedTargetNotional)
        {
            throw new ArgumentException(
                "Risk request notional must equal quantity multiplied by the reference price.",
                nameof(riskEvaluation));
        }

        OrderId = orderId;
        ClientOrderId = clientOrderId;
        Symbol = symbol;
        Side = side;
        Type = type;
        Quantity = quantity;
        ReferencePrice = referencePrice;
        CorrelationId = correlationId;
        IdempotencyKey = idempotencyKey;
        RiskEvaluation = riskEvaluation;
        CreatedAtUtc = createdAtUtc;
        if (executionAttempt is < 0 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(executionAttempt));
        }

        if (executionExpiresAtUtc is { } expiresAtUtc &&
            (expiresAtUtc.Offset != TimeSpan.Zero || expiresAtUtc < createdAtUtc))
        {
            throw new ArgumentException("Execution expiry must be UTC and cannot precede intent creation.", nameof(executionExpiresAtUtc));
        }
        if (estimatedCommissionRate is < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedCommissionRate));
        }

        if (estimatedCommissionRate is not null)
        {
            _ = new ExecutionFeeEstimate(
                estimatedCommissionRate.Value,
                commissionSnapshotHash ?? throw new ArgumentException("An estimated commission requires a snapshot hash.", nameof(commissionSnapshotHash)),
                commissionSource ?? throw new ArgumentException("An estimated commission requires a source.", nameof(commissionSource)),
                commissionObservedAtUtc ?? throw new ArgumentException("An estimated commission requires an observation timestamp.", nameof(commissionObservedAtUtc)),
                commissionValidUntilUtc ?? throw new ArgumentException("An estimated commission requires a validity timestamp.", nameof(commissionValidUntilUtc)));
        }

        EstimatedCommissionRate = estimatedCommissionRate;
        CommissionSnapshotHash = commissionSnapshotHash;
        CommissionSource = commissionSource;
        CommissionObservedAtUtc = commissionObservedAtUtc;
        CommissionValidUntilUtc = commissionValidUntilUtc;
        ExecutionAttempt = executionAttempt;
        ExecutionExpiresAtUtc = executionExpiresAtUtc;
    }

    public OrderId OrderId { get; }
    public ClientOrderId ClientOrderId { get; }
    public Symbol Symbol { get; }
    public OrderSide Side { get; }
    public OrderType Type { get; }
    public Quantity Quantity { get; }
    public Price ReferencePrice { get; }
    public CorrelationId CorrelationId { get; }
    public IdempotencyKey IdempotencyKey { get; }
    public RiskEvaluationRequest RiskEvaluation { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public decimal? EstimatedCommissionRate { get; }
    public string? CommissionSnapshotHash { get; }
    public string? CommissionSource { get; }
    public DateTimeOffset? CommissionObservedAtUtc { get; }
    public DateTimeOffset? CommissionValidUntilUtc { get; }
    public int ExecutionAttempt { get; }
    public DateTimeOffset? ExecutionExpiresAtUtc { get; }
}

/// <summary>Exchange-facing command; it is only created after risk authorization.</summary>
public sealed record SubmitOrderCommand(
    OrderId OrderId,
    ClientOrderId ClientOrderId,
    Symbol Symbol,
    OrderSide Side,
    OrderType Type,
    Quantity Quantity,
    Price ReferencePrice,
    decimal AuthorizedNotional,
    CorrelationId CorrelationId,
    IdempotencyKey IdempotencyKey,
    string PolicyVersion,
    DateTimeOffset CreatedAtUtc,
    decimal? EstimatedCommissionRate = null,
    string? CommissionSnapshotHash = null,
    string? CommissionSource = null,
    DateTimeOffset? CommissionObservedAtUtc = null,
    DateTimeOffset? CommissionValidUntilUtc = null,
    int ExecutionAttempt = 0,
    DateTimeOffset? ExecutionExpiresAtUtc = null);

public enum OutboxRecordStatus
{
    Pending,
}

/// <summary>
/// Durable-boundary contract for a submit command. Persistence is intentionally
/// outside the domain; adapters must persist this record before exchange I/O.
/// </summary>
public sealed record OutboxRecord(
    Guid MessageId,
    string MessageType,
    CorrelationId CorrelationId,
    IdempotencyKey IdempotencyKey,
    string PolicyVersion,
    SubmitOrderCommand Command,
    OutboxRecordStatus Status,
    DateTimeOffset CreatedAtUtc);

/// <summary>Result of evaluating an intent. Rejected results never contain a command or outbox record.</summary>
public sealed record OrderExecutionResult(
    RiskDecision Decision,
    SubmitOrderCommand? Command,
    OutboxRecord? OutboxRecord)
{
    public bool IsDispatchable => Command is not null && OutboxRecord is not null;
}

/// <summary>
/// Pure orchestration boundary for the first execution slice. It has no I/O,
/// never bypasses RiskEvaluator, and fails closed by construction.
/// </summary>
public static class OrderExecutionPipeline
{
    public const string SubmitOrderMessageType = "orders.submit.v1";

    public static OrderExecutionResult Evaluate(OrderIntent intent, RiskPolicy policy)
    {
        return EvaluateCore(intent, policy, operationalControl: null);
    }

    /// <summary>
    /// Evaluates an intent using a point-in-time operational control snapshot.
    /// The snapshot is captured once so a concurrent switch cannot produce a
    /// partially mixed decision.
    /// </summary>
    public static OrderExecutionResult Evaluate(
        OrderIntent intent,
        RiskPolicy policy,
        IOperationalControl operationalControl)
    {
        ArgumentNullException.ThrowIfNull(operationalControl);
        return Evaluate(intent, policy, operationalControl.Snapshot);
    }

    public static OrderExecutionResult Evaluate(
        OrderIntent intent,
        RiskPolicy policy,
        OperationalControlSnapshot operationalControl)
    {
        ArgumentNullException.ThrowIfNull(operationalControl);
        return EvaluateCore(intent, policy, operationalControl);
    }

    private static OrderExecutionResult EvaluateCore(
        OrderIntent intent,
        RiskPolicy policy,
        OperationalControlSnapshot? operationalControl)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(policy);

        var decision = operationalControl is null
            ? RiskEvaluator.Evaluate(policy, intent.RiskEvaluation)
            : RiskEvaluator.Evaluate(policy, intent.RiskEvaluation, operationalControl);
        if (!decision.AllowsNewExposure)
        {
            return new(decision, null, null);
        }

        var authorizedQuantity = new Quantity(decision.AuthorizedTargetNotional / intent.ReferencePrice.Value);
        var command = new SubmitOrderCommand(
            intent.OrderId,
            intent.ClientOrderId,
            intent.Symbol,
            intent.Side,
            intent.Type,
            authorizedQuantity,
            intent.ReferencePrice,
            decision.AuthorizedTargetNotional,
            intent.CorrelationId,
            intent.IdempotencyKey,
            decision.PolicyVersion,
            intent.CreatedAtUtc,
            intent.EstimatedCommissionRate,
            intent.CommissionSnapshotHash,
            intent.CommissionSource,
            intent.CommissionObservedAtUtc,
            intent.CommissionValidUntilUtc,
            intent.ExecutionAttempt,
            intent.ExecutionExpiresAtUtc);

        var outbox = new OutboxRecord(
            Guid.NewGuid(),
            SubmitOrderMessageType,
            intent.CorrelationId,
            intent.IdempotencyKey,
            decision.PolicyVersion,
            command,
            OutboxRecordStatus.Pending,
            intent.CreatedAtUtc);

        return new(decision, command, outbox);
    }
}
