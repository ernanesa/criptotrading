namespace CryptoTrading.Domain.Risk;

/// <summary>The outcome of evaluating a portfolio target against a risk policy.</summary>
public enum RiskDecisionStatus
{
    Approved,
    Adjusted,
    Rejected,
    EmergencyBlocked,
}

/// <summary>Stable, machine-readable reasons included in every risk decision.</summary>
public static class RiskReasonCodes
{
    public const string TargetWithinLimits = "TARGET_WITHIN_LIMITS";
    public const string TargetCappedByNotional = "TARGET_CAPPED_BY_NOTIONAL";
    public const string TargetCappedByExposure = "TARGET_CAPPED_BY_EXPOSURE";
    public const string DailyLossLimit = "DAILY_LOSS_LIMIT";
    public const string DrawdownLimit = "DRAWDOWN_LIMIT";
    public const string MarketDataStale = "MARKET_DATA_STALE";
    public const string EmergencyStop = "EMERGENCY_STOP";
    public const string WithdrawalsDisabled = "WITHDRAWALS_DISABLED";
    public const string InvalidRequest = "INVALID_REQUEST";
}

/// <summary>Immutable, versioned hard limits. Values are decimal quote-currency notional.</summary>
public sealed record RiskPolicy
{
    public RiskPolicy(
        string version,
        decimal maxOrderNotional,
        decimal maxExposureNotional,
        decimal maxDailyLoss,
        decimal maxDrawdown,
        TimeSpan maxMarketDataAge,
        bool withdrawalsEnabled = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (version.Length > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        ValidatePositive(maxOrderNotional, nameof(maxOrderNotional));
        ValidatePositive(maxExposureNotional, nameof(maxExposureNotional));
        ValidatePositive(maxDailyLoss, nameof(maxDailyLoss));
        ValidatePositive(maxDrawdown, nameof(maxDrawdown));
        if (maxMarketDataAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMarketDataAge), "Market data age must be positive.");
        }

        Version = version.Trim();
        MaxOrderNotional = maxOrderNotional;
        MaxExposureNotional = maxExposureNotional;
        MaxDailyLoss = maxDailyLoss;
        MaxDrawdown = maxDrawdown;
        MaxMarketDataAge = maxMarketDataAge;
        WithdrawalsEnabled = withdrawalsEnabled;
    }

    public string Version { get; }

    public decimal MaxOrderNotional { get; }

    public decimal MaxExposureNotional { get; }

    public decimal MaxDailyLoss { get; }

    public decimal MaxDrawdown { get; }

    public TimeSpan MaxMarketDataAge { get; }

    /// <remarks>Production policies must keep this false. It is explicit to make unsafe configuration visible.</remarks>
    public bool WithdrawalsEnabled { get; }

    private static void ValidatePositive(decimal value, string parameterName)
    {
        if (value <= 0m)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Risk limits must be greater than zero.");
        }
    }
}

/// <summary>Snapshot consumed by the pure risk evaluator. Losses and exposures are non-negative magnitudes.</summary>
public sealed record RiskEvaluationRequest
{
    public RiskEvaluationRequest(
        decimal requestedTargetNotional,
        decimal currentExposureNotional,
        decimal dailyLoss,
        decimal drawdown,
        DateTimeOffset marketDataAt,
        DateTimeOffset evaluatedAt,
        bool emergencyStopActive = false,
        bool withdrawalRequested = false)
    {
        ValidateNonNegative(requestedTargetNotional, nameof(requestedTargetNotional));
        ValidateNonNegative(currentExposureNotional, nameof(currentExposureNotional));
        ValidateNonNegative(dailyLoss, nameof(dailyLoss));
        ValidateNonNegative(drawdown, nameof(drawdown));
        if (evaluatedAt.Offset != TimeSpan.Zero || marketDataAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Risk timestamps must be UTC.");
        }

        RequestedTargetNotional = requestedTargetNotional;
        CurrentExposureNotional = currentExposureNotional;
        DailyLoss = dailyLoss;
        Drawdown = drawdown;
        MarketDataAt = marketDataAt;
        EvaluatedAt = evaluatedAt;
        EmergencyStopActive = emergencyStopActive;
        WithdrawalRequested = withdrawalRequested;
    }

    public decimal RequestedTargetNotional { get; }
    public decimal CurrentExposureNotional { get; }
    public decimal DailyLoss { get; }
    public decimal Drawdown { get; }
    public DateTimeOffset MarketDataAt { get; }
    public DateTimeOffset EvaluatedAt { get; }
    public bool EmergencyStopActive { get; }
    public bool WithdrawalRequested { get; }

    private static void ValidateNonNegative(decimal value, string parameterName)
    {
        if (value < 0m)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Risk values cannot be negative.");
        }
    }
}

/// <summary>Reconstructible output of a risk evaluation, including policy version and reason codes.</summary>
public sealed record RiskDecision(
    RiskDecisionStatus Status,
    decimal RequestedTargetNotional,
    decimal AuthorizedTargetNotional,
    string PolicyVersion,
    IReadOnlyList<string> ReasonCodes)
{
    public bool AllowsNewExposure =>
        Status is (RiskDecisionStatus.Approved or RiskDecisionStatus.Adjusted) &&
        AuthorizedTargetNotional > 0m;
}

/// <summary>Pure, deterministic evaluator. It can reduce a target but never increase it.</summary>
public static class RiskEvaluator
{
    public static RiskDecision Evaluate(RiskPolicy policy, RiskEvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(request);

        var reasons = new List<string>();
        var requested = request.RequestedTargetNotional;

        if (request.WithdrawalRequested && !policy.WithdrawalsEnabled)
        {
            return Block(RiskDecisionStatus.EmergencyBlocked, requested, policy, RiskReasonCodes.WithdrawalsDisabled);
        }

        if (request.EmergencyStopActive)
        {
            return Block(RiskDecisionStatus.EmergencyBlocked, requested, policy, RiskReasonCodes.EmergencyStop);
        }

        if (request.DailyLoss >= policy.MaxDailyLoss)
        {
            return BlockNewExposure(request, policy, RiskReasonCodes.DailyLossLimit);
        }

        if (request.Drawdown >= policy.MaxDrawdown)
        {
            return BlockNewExposure(request, policy, RiskReasonCodes.DrawdownLimit);
        }

        if (request.EvaluatedAt - request.MarketDataAt > policy.MaxMarketDataAge)
        {
            return BlockNewExposure(request, policy, RiskReasonCodes.MarketDataStale);
        }

        var authorized = Math.Min(requested, policy.MaxOrderNotional);
        authorized = Math.Min(authorized, policy.MaxExposureNotional);
        if (authorized > requested)
        {
            throw new InvalidOperationException("Risk evaluator cannot increase a target.");
        }

        if (requested > policy.MaxOrderNotional)
        {
            reasons.Add(RiskReasonCodes.TargetCappedByNotional);
        }

        if (requested > policy.MaxExposureNotional)
        {
            reasons.Add(RiskReasonCodes.TargetCappedByExposure);
        }

        if (reasons.Count == 0)
        {
            reasons.Add(RiskReasonCodes.TargetWithinLimits);
            return new(RiskDecisionStatus.Approved, requested, authorized, policy.Version, reasons.AsReadOnly());
        }

        return new(RiskDecisionStatus.Adjusted, requested, authorized, policy.Version, reasons.AsReadOnly());
    }

    private static RiskDecision BlockNewExposure(RiskEvaluationRequest request, RiskPolicy policy, string reason)
    {
        // A risk breaker prevents new exposure, but allows a target that reduces an existing position.
        if (request.RequestedTargetNotional <= request.CurrentExposureNotional)
        {
            return new(
                RiskDecisionStatus.Adjusted,
                request.RequestedTargetNotional,
                request.RequestedTargetNotional,
                policy.Version,
                new[] { reason });
        }

        return Block(RiskDecisionStatus.Rejected, request.RequestedTargetNotional, policy, reason);
    }

    private static RiskDecision Block(
        RiskDecisionStatus status,
        decimal requested,
        RiskPolicy policy,
        string reason) => new(status, requested, 0m, policy.Version, new[] { reason });
}
