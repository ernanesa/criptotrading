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
    ILogger<ExecutionSubmissionGuard> logger) : IExecutionSubmissionGuard
{
    public async Task ValidateFinalAsync(SubmitOrderCommand command, LeasedOutboxMessage message, CancellationToken cancellationToken = default)
    {
        ValidateOperationalState();
        var now = DateTimeOffset.UtcNow;
        if ((command.ExecutionExpiresAtUtc ?? command.CreatedAtUtc.AddMinutes(1)) <= now ||
            !await submissionStore.ValidateOutboxLeaseAsync(message, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false))
        {
            throw new ExecutionDispatchBlockedException("FINAL_EXECUTION_EXPIRED_OR_LEASE_LOST");
        }
        var policy = await policies.GetActiveRiskPolicyAsync(now, cancellationToken).ConfigureAwait(false);
        if (policy is null || policy.Lifecycle != RiskPolicyLifecycle.Active || policy.Policy.WithdrawalsEnabled ||
            policy.Policy.Version != command.PolicyVersion || policy.EffectiveFromUtc > now || policy.ExpiresAtUtc <= now)
        {
            throw new ExecutionDispatchBlockedException("FINAL_RISK_POLICY_NOT_CURRENT");
        }
        ValidateOperationalState();
    }

    public async Task ValidateAsync(SubmitOrderCommand command, LeasedOutboxMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(message);
        ValidateOperationalState();
        var now = DateTimeOffset.UtcNow;
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
        if (policy is null || policy.Lifecycle != RiskPolicyLifecycle.Active || policy.Policy.WithdrawalsEnabled ||
            policy.EffectiveFromUtc > now || policy.ExpiresAtUtc <= now || policy.Policy.Version != command.PolicyVersion)
        {
            throw new ExecutionDispatchBlockedException("RISK_POLICY_NOT_CURRENT");
        }

        var openOrders = await orders.ReadOpenOrdersAsync(cancellationToken).ConfigureAwait(false);
        if (openOrders.Any(item => item.Symbol == command.Symbol.Value && item.ClientOrderId != command.ClientOrderId.Value))
        {
            throw new ExecutionDispatchBlockedException("OTHER_ORDER_UNRESOLVED");
        }

        var ticker = await market.GetBookTickerAsync(command.Symbol.Value, cancellationToken).ConfigureAwait(false);
        var klines = await market.GetKlinesAsync(command.Symbol.Value, options.Value.AutonomyInterval,
            limit: options.Value.AutonomyKlineLimit, cancellationToken: cancellationToken).ConfigureAwait(false);
        var closed = AutonomyCycleHostedService.ValidateAndMapKlines(klines, command.Symbol.Value,
            options.Value.AutonomyInterval, policy.Policy.MaxMarketDataAge, DateTimeOffset.UtcNow, null);
        var measuredRisk = MarketRiskCalculator.Calculate(
            closed.Select(item => new MarketCandle(item.Bar.CloseTimeUtc, item.Bar.ClosePrice.Value)).ToArray(),
            [new MarketBookLevel(ticker.BidPrice, ticker.BidQuantity)],
            [new MarketBookLevel(ticker.AskPrice, ticker.AskQuantity)],
            command.Side == OrderSide.Buy ? MarketRiskSide.Buy : MarketRiskSide.Sell, ticker.ObservedAtUtc);
        var snapshot = await account.GetAccountAsync(cancellationToken).ConfigureAwait(false);
        const string quote = "USDT";
        if (!command.Symbol.Value.EndsWith(quote, StringComparison.Ordinal))
        {
            throw new ExecutionDispatchBlockedException("QUOTE_ASSET_NOT_SUPPORTED");
        }
        var baseAsset = command.Symbol.Value[..^quote.Length];
        var baseBalance = snapshot.Balances.FirstOrDefault(item => item.Asset == baseAsset);
        var quoteBalance = snapshot.Balances.FirstOrDefault(item => item.Asset == quote);
        var positionQuantity = Parse(baseBalance?.Free) + Parse(baseBalance?.Locked);
        var quoteQuantity = Parse(quoteBalance?.Free) + Parse(quoteBalance?.Locked);
        var mid = (ticker.BidPrice + ticker.AskPrice) / 2m;
        var unsupportedAsset = snapshot.Balances.Any(item => item.Asset != baseAsset && item.Asset != quote &&
            Parse(item.Free) + Parse(item.Locked) > 0m);
        var evaluatedAt = DateTimeOffset.UtcNow;
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
            policy.ExpiresAtUtc <= DateTimeOffset.UtcNow || expiry <= DateTimeOffset.UtcNow)
        {
            throw new ExecutionDispatchBlockedException("PRE_SUBMIT_RISK_REJECTED");
        }
        ValidateOperationalState();
        ExecutionGuardLog.Approved(logger, message.MessageId, command.Symbol.Value, command.PolicyVersion,
            measuredRisk.ExpectedSlippageBps, measuredRisk.VolatilityBps, measuredRisk.AvailableLiquidityNotional);
    }

    private void ValidateOperationalState()
    {
        if (control.Snapshot.BlocksNewEntries || !supervisor.Snapshot.CanCreateNewExposure)
        {
            throw new ExecutionDispatchBlockedException("OPERATIONAL_OR_RUNTIME_BLOCKED");
        }
    }

    private static decimal Parse(string? value) => value is null ? 0m :
        decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var result) && result >= 0m
            ? result : throw new ExecutionDispatchBlockedException("ACCOUNT_BALANCE_INVALID");
}

internal static partial class ExecutionGuardLog
{
    [LoggerMessage(LogLevel.Information, "Pre-submit risk approved. MessageId={MessageId} Symbol={Symbol} PolicyVersion={PolicyVersion} SlippageBps={SlippageBps} VolatilityBps={VolatilityBps} MarketLiquidityNotional={MarketLiquidityNotional}")]
    public static partial void Approved(ILogger logger, Guid messageId, string symbol, string policyVersion,
        decimal slippageBps, decimal volatilityBps, decimal marketLiquidityNotional);
}
