#pragma warning disable CA1515 // Public contract enables host-level safety tests.

using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Domain.Runtime;
using CryptoTrading.Exchange.Binance;
using CryptoTrading.Persistence;
using Microsoft.Extensions.Options;

namespace CryptoTrading.Worker;

/// <summary>Checked immediately before every new external submit; recovery is read-only and separate.</summary>
public interface IExecutionSubmissionGuard
{
    Task ValidateAsync(SubmitOrderCommand command, LeasedOutboxMessage message, CancellationToken cancellationToken = default);
    Task ValidateFinalAsync(SubmitOrderCommand command, LeasedOutboxMessage message, CancellationToken cancellationToken = default);
}

public sealed class ExecutionDispatchBlockedException : InvalidOperationException
{
    public ExecutionDispatchBlockedException() { }
    public ExecutionDispatchBlockedException(string message) : base(message) { }
    public ExecutionDispatchBlockedException(string message, Exception innerException) : base(message, innerException) { }
}

internal sealed class ExecutionSubmissionGuard(
    IOperationalControl control,
    IRuntimeSupervisor supervisor,
    IRiskPolicyStore policies,
    IAutonomyRiskContextStore riskContexts,
    ILocalOpenOrderStore orders,
    IDurableSubmissionStore submissionStore,
    IBinanceSpotMarketClient market,
    IBinanceSpotAccountClient account,
    IOptions<WorkerRuntimeOptions> options,
    ILogger<ExecutionSubmissionGuard> logger,
    TimeProvider? timeProvider = null,
    ICapitalReservationStore? capitalReservations = null) : IExecutionSubmissionGuard
{
    private static readonly string[] SupportedQuoteAssets = ["USDT", "FDUSD", "USDC", "TUSD", "BUSD", "BTC", "ETH", "BNB", "BRL"];

    public async Task ValidateFinalAsync(SubmitOrderCommand command, LeasedOutboxMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            var commission = await ValidateCurrentCommissionAsync(command, UtcNow, cancellationToken).ConfigureAwait(false);
            var observation = await ValidateCoreAsync(command, message, commission.Rate, cancellationToken).ConfigureAwait(false);

            var now = UtcNow;
            var finalPolicy = await policies.GetActiveRiskPolicyAsync(now, cancellationToken).ConfigureAwait(false);
            now = UtcNow;
            if (finalPolicy is null || !IsCurrentPolicy(finalPolicy, command, now) ||
                !string.Equals(observation.PolicyDocumentHash, finalPolicy.DocumentHash, StringComparison.Ordinal))
            {
                throw new ExecutionDispatchBlockedException("FINAL_RISK_POLICY_NOT_CURRENT");
            }

            if (!await submissionStore.ValidateOutboxLeaseAsync(message, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false))
            {
                throw new ExecutionDispatchBlockedException("FINAL_EXECUTION_EXPIRED_OR_LEASE_LOST");
            }

            now = UtcNow;
            if (
                (command.ExecutionExpiresAtUtc ?? command.CreatedAtUtc.AddMinutes(1)) <= now ||
                command.CommissionValidUntilUtc is not { } plannedCommissionValidUntil ||
                plannedCommissionValidUntil <= now || commission.ObservedAtUtc > now ||
                now - commission.ObservedAtUtc > finalPolicy.Policy.MaxMarketDataAge ||
                observation.MarketObservedAtUtc > now ||
                now - observation.MarketObservedAtUtc > finalPolicy.Policy.MaxMarketDataAge)
            {
                throw new ExecutionDispatchBlockedException("FINAL_EXECUTION_EXPIRED_OR_LEASE_LOST");
            }

            ValidateOperationalState(command);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ExecutionDispatchBlockedException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ExecutionDispatchBlockedException("FINAL_REVALIDATION_UNAVAILABLE", exception);
        }
    }

    public async Task ValidateAsync(SubmitOrderCommand command, LeasedOutboxMessage message, CancellationToken cancellationToken = default)
    {
        _ = await ValidateCoreAsync(command, message, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ValidationObservation> ValidateCoreAsync(SubmitOrderCommand command, LeasedOutboxMessage message,
        decimal? freshCommissionRate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(message);
        ValidateOperationalState(command);
        var now = UtcNow;
        var expiry = command.ExecutionExpiresAtUtc ?? command.CreatedAtUtc.AddMinutes(1);
        if (command.CreatedAtUtc.Offset != TimeSpan.Zero || expiry.Offset != TimeSpan.Zero ||
            command.CreatedAtUtc > now || expiry <= now)
        {
            throw new ExecutionDispatchBlockedException("EXECUTION_EXPIRED_OR_INVALID_TIME");
        }

        if (!await submissionStore.ValidateOutboxLeaseAsync(message, cancellationToken).ConfigureAwait(false))
        {
            throw new ExecutionDispatchBlockedException("OUTBOX_LEASE_LOST");
        }

        var policy = await policies.GetActiveRiskPolicyAsync(now, cancellationToken).ConfigureAwait(false);
        if (policy is null || !IsCurrentPolicy(policy, command, now))
        {
            throw new ExecutionDispatchBlockedException("RISK_POLICY_NOT_CURRENT");
        }

        var openOrders = await orders.ReadOpenOrdersAsync(cancellationToken).ConfigureAwait(false);
        if (openOrders.Any(item => item.Symbol == command.Symbol.Value && item.ClientOrderId != command.ClientOrderId.Value))
        {
            throw new ExecutionDispatchBlockedException("OTHER_ORDER_UNRESOLVED");
        }

        var tickerTask = market.GetBookTickerAsync(command.Symbol.Value, cancellationToken);
        var klinesTask = market.GetKlinesAsync(command.Symbol.Value, options.Value.AutonomyInterval,
            limit: options.Value.AutonomyKlineLimit, cancellationToken: cancellationToken);
        var snapshotTask = account.GetAccountAsync(cancellationToken);
        await Task.WhenAll(tickerTask, klinesTask, snapshotTask).ConfigureAwait(false);
        var ticker = await tickerTask.ConfigureAwait(false);
        var klines = await klinesTask.ConfigureAwait(false);
        var closed = AutonomyCycleHostedService.ValidateAndMapKlines(klines, command.Symbol.Value,
            options.Value.AutonomyInterval, policy.Policy.MaxMarketDataAge, UtcNow, null);
        var measuredRisk = MarketRiskCalculator.Calculate(
            closed.Select(item => new MarketCandle(item.Bar.CloseTimeUtc, item.Bar.ClosePrice.Value)).ToArray(),
            [new MarketBookLevel(ticker.BidPrice, ticker.BidQuantity)],
            [new MarketBookLevel(ticker.AskPrice, ticker.AskQuantity)],
            command.Side == OrderSide.Buy ? MarketRiskSide.Buy : MarketRiskSide.Sell, ticker.ObservedAtUtc);
        var snapshot = await snapshotTask.ConfigureAwait(false);
        if (!TrySplitSymbol(command.Symbol.Value, out var baseAsset, out var quote))
        {
            throw new ExecutionDispatchBlockedException("QUOTE_ASSET_NOT_SUPPORTED");
        }
        var baseBalance = snapshot.Balances.FirstOrDefault(item => item.Asset == baseAsset);
        var quoteBalance = snapshot.Balances.FirstOrDefault(item => item.Asset == quote);
        var rawPositionQuantity = Parse(baseBalance?.Free) + Parse(baseBalance?.Locked);
        var isTestnetFaucetWallet = string.Equals(options.Value.ExecutionMode, "TESTNET", StringComparison.OrdinalIgnoreCase)
            && snapshot.Balances.Count > 20;
        var positionQuantity = isTestnetFaucetWallet && rawPositionQuantity >= 0.99m
            ? Math.Max(0m, rawPositionQuantity - 1.0m)
            : rawPositionQuantity;
        var quoteQuantity = Parse(quoteBalance?.Free) + Parse(quoteBalance?.Locked);
        var mid = (ticker.BidPrice + ticker.AskPrice) / 2m;
        var unsupportedAsset = !isTestnetFaucetWallet && snapshot.Balances.Any(item => item.Asset != baseAsset && item.Asset != quote &&
            Parse(item.Free) + Parse(item.Locked) > 0m);
        var evaluatedAt = UtcNow;
        var accounting = await riskContexts.RecordAutonomyRiskContextAsync(
            $"equity:{options.Value.ReconciliationAccount}:{command.Symbol.Value}:{quote}",
            command.Symbol, new Asset(quote), new DateTimeOffset(evaluatedAt.UtcDateTime.Date, TimeSpan.Zero),
            unsupportedAsset ? null : quoteQuantity + positionQuantity * mid, evaluatedAt, cancellationToken).ConfigureAwait(false);
        var executionPrice = command.Type == OrderType.Limit ? command.ReferencePrice.Value :
            command.Side == OrderSide.Buy ? ticker.AskPrice : ticker.BidPrice;
        var currentNotional = command.Quantity.Value * executionPrice;
        if (command.Side == OrderSide.Buy && (!accounting.RiskContextComplete ||
            accounting.RealizedDailyLossNotional >= policy.Policy.MaxDailyLoss ||
            accounting.DrawdownNotional is null || accounting.DrawdownNotional >= policy.Policy.MaxDrawdown ||
            Math.Abs(accounting.PositionQuantity - positionQuantity) > 0.00000001m))
        {
            throw new ExecutionDispatchBlockedException("PRE_SUBMIT_ACCOUNTING_OR_LOSS_BREAKER");
        }
        if (!measuredRisk.IsComplete || ticker.ObservedAtUtc > evaluatedAt ||
            evaluatedAt - ticker.ObservedAtUtc > policy.Policy.MaxMarketDataAge ||
            currentNotional > command.AuthorizedNotional || currentNotional > measuredRisk.AvailableLiquidityNotional ||
            currentNotional > policy.Policy.MaxOrderNotional ||
            (command.Side == OrderSide.Buy && positionQuantity * mid + currentNotional > policy.Policy.MaxExposureNotional))
        {
            throw new ExecutionDispatchBlockedException("PRE_SUBMIT_MARKET_OR_NOTIONAL_CHANGED");
        }
        var risk = RiskEvaluator.Evaluate(policy.Policy, new RiskEvaluationRequest(
            currentNotional, positionQuantity * mid, accounting.RealizedDailyLossNotional,
            accounting.DrawdownNotional ?? 0m, closed[^1].Bar.CloseTimeUtc, evaluatedAt,
            marketRisk: measuredRisk, riskContextComplete: accounting.RiskContextComplete), control.Snapshot);
        if (!risk.AllowsNewExposure || risk.AuthorizedTargetNotional < currentNotional ||
            policy.ExpiresAtUtc <= UtcNow || expiry <= UtcNow)
        {
            throw new ExecutionDispatchBlockedException("PRE_SUBMIT_RISK_REJECTED");
        }
        if (policy.Policy.BudgetPolicy is { } budgetPolicy && command.Side == OrderSide.Buy)
        {
            await ValidateAndReserveOperatorBudgetAsync(
                command,
                message,
                budgetPolicy,
                quote,
                positionQuantity * mid,
                accounting.RealizedDailyLossNotional,
                currentNotional,
                measuredRisk.ExpectedSlippageBps,
                freshCommissionRate is not null,
                cancellationToken).ConfigureAwait(false);
        }
        if (freshCommissionRate is { } rate)
        {
            ValidateFinalAvailableBalance(command, snapshot, rate, currentNotional);
        }

        ValidateOperationalState(command);
        ExecutionGuardLog.Approved(logger, message.MessageId, command.Symbol.Value, command.PolicyVersion,
            measuredRisk.ExpectedSlippageBps, measuredRisk.VolatilityBps, measuredRisk.AvailableLiquidityNotional);
        return new ValidationObservation(policy.DocumentHash, ticker.ObservedAtUtc, currentNotional);
    }

    private void ValidateOperationalState(SubmitOrderCommand command)
    {
        // SafeMode blocks new exposure only: a SELL (risk reduction) remains allowed while the runtime is
        // reconciled and connected. KillSwitch blocks every submission.
        var runtime = supervisor.Snapshot;
        if (command.Side == OrderSide.Sell &&
            control.Snapshot.Mode == OperationalMode.SafeMode &&
            runtime.ReconciliationHealthy &&
            runtime.AccountStreamConnected)
        {
            return;
        }

        if (control.Snapshot.BlocksNewEntries || !runtime.CanCreateNewExposure)
        {
            throw new ExecutionDispatchBlockedException("OPERATIONAL_OR_RUNTIME_BLOCKED");
        }
    }

    private DateTimeOffset UtcNow => (timeProvider ?? TimeProvider.System).GetUtcNow();

    private static decimal Parse(string? value) => value is null ? 0m :
        decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var result) && result >= 0m
            ? result : throw new ExecutionDispatchBlockedException("ACCOUNT_BALANCE_INVALID");

    private async Task<CommissionObservation> ValidateCurrentCommissionAsync(SubmitOrderCommand command, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (command.EstimatedCommissionRate is not (>= 0m and <= 1m) || string.IsNullOrWhiteSpace(command.CommissionSource) ||
            string.IsNullOrWhiteSpace(command.CommissionSnapshotHash) || command.CommissionObservedAtUtc is null ||
            command.CommissionValidUntilUtc is null || command.CommissionObservedAtUtc.Value.Offset != TimeSpan.Zero ||
            command.CommissionValidUntilUtc.Value.Offset != TimeSpan.Zero || command.CommissionObservedAtUtc > command.CommissionValidUntilUtc ||
            command.CommissionObservedAtUtc > now || command.CommissionValidUntilUtc <= now)
        {
            throw new ExecutionDispatchBlockedException("FINAL_COMMISSION_SNAPSHOT_INVALID_OR_EXPIRED");
        }

        var current = await account.GetCommissionAsync(command.Symbol.Value, cancellationToken).ConfigureAwait(false);
        var observedAt = UtcNow;
        if (current.Response is null || current.ObservedAtUtc.Offset != TimeSpan.Zero || current.ObservedAtUtc > observedAt ||
            !string.Equals(current.Response.Symbol, command.Symbol.Value, StringComparison.Ordinal))
        {
            throw new ExecutionDispatchBlockedException("FINAL_COMMISSION_REVALIDATION_INVALID");
        }

        // The commission response proves an offered discount, but not the fresh conversion
        // valuation needed to reserve that third-asset fee safely. Reserve the undiscounted rate.
        var standardRate = WorstCaseRateFor(current.Response.StandardCommission, command.Side, "STANDARD");

        var currentRate = standardRate +
            WorstCaseRateFor(current.Response.SpecialCommission, command.Side, "SPECIAL") +
            WorstCaseRateFor(current.Response.TaxCommission, command.Side, "TAX");
        if (currentRate > 1m || currentRate > command.EstimatedCommissionRate.Value)
        {
            throw new ExecutionDispatchBlockedException("FINAL_COMMISSION_RATE_ADVERSE_OR_ELIGIBILITY_UNPROVEN");
        }

        return new CommissionObservation(currentRate, current.ObservedAtUtc);
    }

    private static decimal WorstCaseRateFor(BinanceCommissionRateSet rates, OrderSide side, string component)
    {
        if (rates is null)
        {
            throw new ExecutionDispatchBlockedException("FINAL_COMMISSION_REVALIDATION_INVALID");
        }

        return Math.Max(ParseCommissionRate(rates.Maker, component), ParseCommissionRate(rates.Taker, component)) +
            ParseCommissionRate(side == OrderSide.Buy ? rates.Buyer : rates.Seller, component);
    }

    private static decimal ParseCommissionRate(string? value, string component) =>
        decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var rate) && rate is >= 0m and <= 1m
            ? rate
            : throw new ExecutionDispatchBlockedException($"FINAL_COMMISSION_{component}_INVALID");

    private static bool IsCurrentPolicy(RiskPolicyDocument? policy, SubmitOrderCommand command, DateTimeOffset now) =>
        policy is not null && policy.IsValidAt(now) && !policy.Policy.WithdrawalsEnabled &&
        policy.Policy.Version == command.PolicyVersion && !string.IsNullOrWhiteSpace(policy.DocumentHash) &&
        !string.IsNullOrWhiteSpace(policy.ApprovedBy) && !string.IsNullOrWhiteSpace(policy.ApprovalId) &&
        policy.ApprovedAtUtc is { } approvedAt && approvedAt.Offset == TimeSpan.Zero && approvedAt <= now;

    private static bool TrySplitSymbol(string symbol, out string baseAsset, out string quoteAsset)
    {
        var quote = SupportedQuoteAssets.FirstOrDefault(candidate =>
            symbol.EndsWith(candidate, StringComparison.Ordinal) && symbol.Length > candidate.Length);
        if (quote is null)
        {
            baseAsset = string.Empty;
            quoteAsset = string.Empty;
            return false;
        }

        baseAsset = symbol[..^quote.Length];
        quoteAsset = quote;
        return true;
    }

    private static void ValidateFinalAvailableBalance(SubmitOrderCommand command, BinanceAccountSnapshot accountSnapshot,
        decimal currentCommissionRate, decimal effectiveNotional)
    {
        if (!TrySplitSymbol(command.Symbol.Value, out var baseAsset, out var quoteAsset))
        {
            throw new ExecutionDispatchBlockedException("QUOTE_ASSET_NOT_SUPPORTED");
        }

        var requiredAsset = command.Side == OrderSide.Buy ? quoteAsset : baseAsset;
        var free = Parse(accountSnapshot.Balances.FirstOrDefault(item => item.Asset == requiredAsset)?.Free);
        if (command.Side == OrderSide.Sell)
        {
            if (free < command.Quantity.Value)
            {
                throw new ExecutionDispatchBlockedException("FINAL_INSUFFICIENT_FREE_BASE_BALANCE");
            }

            return;
        }

        decimal requiredQuote;
        try
        {
            requiredQuote = checked(effectiveNotional + effectiveNotional * currentCommissionRate);
        }
        catch (OverflowException)
        {
            throw new ExecutionDispatchBlockedException("FINAL_INSUFFICIENT_FREE_QUOTE_BALANCE");
        }

        if (free < requiredQuote)
        {
            throw new ExecutionDispatchBlockedException("FINAL_INSUFFICIENT_FREE_QUOTE_BALANCE");
        }
    }

    private async Task ValidateAndReserveOperatorBudgetAsync(
        SubmitOrderCommand command,
        LeasedOutboxMessage message,
        BudgetPolicy budgetPolicy,
        string quoteAsset,
        decimal openPositionsUsdt,
        decimal realizedLossUsdt,
        decimal candidateNotionalUsdt,
        decimal expectedSlippageBps,
        bool commitDurableReservation,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(command.Symbol.Value, budgetPolicy.AllowedSymbol, StringComparison.OrdinalIgnoreCase))
        {
            throw new ExecutionDispatchBlockedException(RiskReasonCodes.DisallowedSymbol);
        }

        BinanceBookTicker fxTicker;
        try
        {
            fxTicker = await market.GetBookTickerAsync("USDTBRL", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ExecutionDispatchBlockedException(RiskReasonCodes.FxQuoteUnavailable, exception);
        }

        var now = UtcNow;
        if (fxTicker is null || fxTicker.BidPrice <= 0m || fxTicker.AskPrice <= 0m)
        {
            throw new ExecutionDispatchBlockedException(RiskReasonCodes.FxQuoteUnavailable);
        }

        var fxQuote = new UsdtBrlFxQuote(
            fxTicker.AskPrice,
            fxTicker.ObservedAtUtc);

        var activeReservations = capitalReservations is not null
            ? await capitalReservations.ReadActiveReservationsAsync(options.Value.ReconciliationAccount, cancellationToken).ConfigureAwait(false)
            : [];

        var lockedUnknownUsdt = activeReservations
            .Where(static item => item.Status == CapitalReservationStatus.LockedUnknown)
            .Sum(static item => item.RemainingLockedAmount);
        if (lockedUnknownUsdt > 0m || activeReservations.Any(static item => item.Status == CapitalReservationStatus.LockedUnknown))
        {
            throw new ExecutionDispatchBlockedException(RiskReasonCodes.LockedUnknownCapitalActive);
        }

        var otherReservedUsdt = activeReservations
            .Where(item => !string.Equals(item.ClientOrderId, command.ClientOrderId.Value, StringComparison.Ordinal) &&
                           string.Equals(item.Asset, quoteAsset, StringComparison.Ordinal))
            .Sum(static item => item.RemainingLockedAmount);

        var estimatedStopRiskUsdt = Math.Round(
            candidateNotionalUsdt * (0.005m + budgetPolicy.RoundTripFeeAndReserveRate + expectedSlippageBps / 10_000m),
            8,
            MidpointRounding.ToPositiveInfinity);

        var isTestnet = string.Equals(options.Value.ExecutionMode, "TESTNET", StringComparison.OrdinalIgnoreCase);
        var effectiveOpenPositionsCount = openPositionsUsdt > (isTestnet ? 5m : 0m) ? 1 : 0;

        var commitment = budgetPolicy.EvaluateCommitment(
            new BudgetCommitmentRequest(
                requestedOrderNotionalUsdt: candidateNotionalUsdt,
                openPositionsNotionalUsdt: openPositionsUsdt,
                openPositionsCount: effectiveOpenPositionsCount,
                pendingOrdersReservedUsdt: otherReservedUsdt,
                unknownOrdersLockedUsdt: lockedUnknownUsdt,
                dailyRealizedLossUsdt: Math.Max(0m, realizedLossUsdt),
                cumulativeNetRealizedLossUsdt: Math.Max(0m, realizedLossUsdt),
                estimatedStopRiskUsdt: estimatedStopRiskUsdt,
                evaluatedAtUtc: now,
                fxQuote: fxQuote,
                isPositionReduction: command.Side == OrderSide.Sell));

        if (!commitment.IsApproved)
        {
            throw new ExecutionDispatchBlockedException(commitment.ReasonCode);
        }

        if (commitDurableReservation && capitalReservations is not null)
        {
            var fencingToken = Math.Max(1L, now.ToUnixTimeMilliseconds());
            var reservationResult = await capitalReservations.TryReserveCapitalAsync(
                new CapitalReservationRequest(
                    reservationId: $"res:{command.ClientOrderId.Value}",
                    accountId: options.Value.ReconciliationAccount,
                    symbol: command.Symbol.Value,
                    clientOrderId: command.ClientOrderId.Value,
                    asset: quoteAsset,
                    requestedAmount: commitment.RequiredOrderReservationUsdt,
                    effectiveBudgetCeilingAmount: commitment.EffectiveBudgetCeilingUsdt,
                    openPositionsCommittedAmount: openPositionsUsdt,
                    realizedLossesAmount: Math.Max(0m, realizedLossUsdt),
                    workerInstanceId: string.IsNullOrWhiteSpace(message.LeaseOwner) ? "worker" : message.LeaseOwner,
                    fencingToken: fencingToken,
                    occurredAtUtc: now,
                    openPositionsCount: effectiveOpenPositionsCount,
                    maxOpenPositions: budgetPolicy.MaxOpenPositions),
                cancellationToken).ConfigureAwait(false);

            if (!reservationResult.Succeeded)
            {
                throw new ExecutionDispatchBlockedException(reservationResult.ReasonCode);
            }
        }
    }

    private sealed record ValidationObservation(string PolicyDocumentHash, DateTimeOffset MarketObservedAtUtc, decimal EffectiveNotional);
    private sealed record CommissionObservation(decimal Rate, DateTimeOffset ObservedAtUtc);
}

internal static partial class ExecutionGuardLog
{
    [LoggerMessage(LogLevel.Information, "Pre-submit risk approved. MessageId={MessageId} Symbol={Symbol} PolicyVersion={PolicyVersion} SlippageBps={SlippageBps} VolatilityBps={VolatilityBps} MarketLiquidityNotional={MarketLiquidityNotional}")]
    public static partial void Approved(ILogger logger, Guid messageId, string symbol, string policyVersion,
        decimal slippageBps, decimal volatilityBps, decimal marketLiquidityNotional);
}
