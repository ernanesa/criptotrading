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
    public const string SafeMode = "SAFE_MODE_NO_NEW_ENTRIES";
    public const string KillSwitchActive = "KILL_SWITCH_NO_NEW_ENTRIES";
    public const string WithdrawalsDisabled = "WITHDRAWALS_DISABLED";
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string MarketRiskDataUnavailable = "MARKET_RISK_DATA_UNAVAILABLE";
    public const string SlippageLimit = "SLIPPAGE_LIMIT";
    public const string VolatilityLimit = "VOLATILITY_LIMIT";
    public const string LiquidityLimit = "LIQUIDITY_LIMIT";
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
        bool withdrawalsEnabled = false,
        decimal? maxSlippageBps = null,
        decimal? maxVolatilityBps = null,
        decimal? minLiquidityNotional = null)
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

        ValidateOptionalNonNegative(maxSlippageBps, nameof(maxSlippageBps));
        ValidateOptionalNonNegative(maxVolatilityBps, nameof(maxVolatilityBps));
        ValidateOptionalNonNegative(minLiquidityNotional, nameof(minLiquidityNotional));

        Version = version.Trim();
        MaxOrderNotional = maxOrderNotional;
        MaxExposureNotional = maxExposureNotional;
        MaxDailyLoss = maxDailyLoss;
        MaxDrawdown = maxDrawdown;
        MaxMarketDataAge = maxMarketDataAge;
        WithdrawalsEnabled = withdrawalsEnabled;
        MaxSlippageBps = maxSlippageBps;
        MaxVolatilityBps = maxVolatilityBps;
        MinLiquidityNotional = minLiquidityNotional;
    }

    public string Version { get; }

    public decimal MaxOrderNotional { get; }

    public decimal MaxExposureNotional { get; }

    public decimal MaxDailyLoss { get; }

    public decimal MaxDrawdown { get; }

    public TimeSpan MaxMarketDataAge { get; }

    /// <remarks>Production policies must keep this false. It is explicit to make unsafe configuration visible.</remarks>
    public bool WithdrawalsEnabled { get; }

    /// <summary>Maximum expected execution slippage in basis points, when configured.</summary>
    public decimal? MaxSlippageBps { get; }

    /// <summary>Maximum observed volatility in basis points, when configured.</summary>
    public decimal? MaxVolatilityBps { get; }

    /// <summary>Minimum available quote-currency liquidity, when configured.</summary>
    public decimal? MinLiquidityNotional { get; }

    private static void ValidatePositive(decimal value, string parameterName)
    {
        if (value <= 0m)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Risk limits must be greater than zero.");
        }
    }

    private static void ValidateOptionalNonNegative(decimal? value, string parameterName)
    {
        if (value is < 0m)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Optional market-risk limits cannot be negative.");
        }
    }
}

/// <summary>Point-in-time, locally derived market-risk inputs consumed by the pure evaluator.</summary>
public sealed record MarketRiskSnapshot
{
    public MarketRiskSnapshot(
        decimal expectedSlippageBps,
        decimal volatilityBps,
        decimal availableLiquidityNotional,
        DateTimeOffset observedAt,
        bool isComplete = true)
    {
        ValidateNonNegative(expectedSlippageBps, nameof(expectedSlippageBps));
        ValidateNonNegative(volatilityBps, nameof(volatilityBps));
        ValidateNonNegative(availableLiquidityNotional, nameof(availableLiquidityNotional));
        if (observedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Market-risk timestamps must be UTC.", nameof(observedAt));
        }

        ExpectedSlippageBps = expectedSlippageBps;
        VolatilityBps = volatilityBps;
        AvailableLiquidityNotional = availableLiquidityNotional;
        ObservedAt = observedAt;
        IsComplete = isComplete;
    }

    public decimal ExpectedSlippageBps { get; }
    public decimal VolatilityBps { get; }
    public decimal AvailableLiquidityNotional { get; }
    public DateTimeOffset ObservedAt { get; }
    public bool IsComplete { get; }

    private static void ValidateNonNegative(decimal value, string parameterName)
    {
        if (value < 0m)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Market-risk values cannot be negative.");
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
        bool withdrawalRequested = false,
        MarketRiskSnapshot? marketRisk = null)
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
        MarketRisk = marketRisk;
    }

    public decimal RequestedTargetNotional { get; }
    public decimal CurrentExposureNotional { get; }
    public decimal DailyLoss { get; }
    public decimal Drawdown { get; }
    public DateTimeOffset MarketDataAt { get; }
    public DateTimeOffset EvaluatedAt { get; }
    public bool EmergencyStopActive { get; }
    public bool WithdrawalRequested { get; }
    public MarketRiskSnapshot? MarketRisk { get; }

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

        var marketRiskConfigured = policy.MaxSlippageBps.HasValue ||
            policy.MaxVolatilityBps.HasValue ||
            policy.MinLiquidityNotional.HasValue;
        if (marketRiskConfigured && (request.MarketRisk is null || !request.MarketRisk.IsComplete))
        {
            return BlockNewExposure(request, policy, RiskReasonCodes.MarketRiskDataUnavailable);
        }

        var marketRisk = request.MarketRisk;
        if (marketRisk is not null)
        {
            if (policy.MaxSlippageBps is { } maxSlippage && marketRisk.ExpectedSlippageBps > maxSlippage)
            {
                return BlockNewExposure(request, policy, RiskReasonCodes.SlippageLimit);
            }

            if (policy.MaxVolatilityBps is { } maxVolatility && marketRisk.VolatilityBps > maxVolatility)
            {
                return BlockNewExposure(request, policy, RiskReasonCodes.VolatilityLimit);
            }

            if (policy.MinLiquidityNotional is { } minimumLiquidity &&
                marketRisk.AvailableLiquidityNotional < minimumLiquidity)
            {
                return BlockNewExposure(request, policy, RiskReasonCodes.LiquidityLimit);
            }
        }

        var authorized = Math.Min(requested, policy.MaxOrderNotional);
        authorized = Math.Min(authorized, policy.MaxExposureNotional);
        if (marketRisk is not null && policy.MinLiquidityNotional is not null)
        {
            authorized = Math.Min(authorized, marketRisk.AvailableLiquidityNotional);
            if (requested > marketRisk.AvailableLiquidityNotional)
            {
                reasons.Add(RiskReasonCodes.LiquidityLimit);
            }
        }
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

    /// <summary>
    /// Evaluates an intent against the independent operational control plane. SafeMode
    /// and kill switch block new exposure but retain the normal reduction path.
    /// </summary>
    public static RiskDecision Evaluate(
        RiskPolicy policy,
        RiskEvaluationRequest request,
        OperationalControlSnapshot operationalControl)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operationalControl);

        if (operationalControl.BlocksNewEntries)
        {
            var reason = operationalControl.Mode is OperationalMode.KillSwitch
                ? RiskReasonCodes.KillSwitchActive
                : RiskReasonCodes.SafeMode;
            return BlockNewExposure(request, policy, reason);
        }

        return Evaluate(policy, request);
    }

    private static RiskDecision Block(
        RiskDecisionStatus status,
        decimal requested,
        RiskPolicy policy,
        string reason) => new(status, requested, 0m, policy.Version, new[] { reason });
}
