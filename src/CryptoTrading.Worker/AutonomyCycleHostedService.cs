#pragma warning disable CA1515 // Hosted service is a host composition boundary.
#pragma warning disable CA1031 // Supervisor converts unexpected failures into SafeMode.

using System.Globalization;
using CryptoTrading.Backtesting.Autonomy;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Architecture;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Exchange.Binance;
using CryptoTrading.Persistence;
using Microsoft.Extensions.Options;

namespace CryptoTrading.Worker;

/// <summary>
/// Closed-bar A2 supervisor. It deliberately obtains a fresh account snapshot
/// before evaluating a decision and persists the resulting outbox record before
/// the dispatcher can perform exchange I/O. The default configuration keeps it
/// disabled.
/// </summary>
internal sealed class AutonomyCycleHostedService(
    IOptions<WorkerRuntimeOptions> options,
    IBinanceSpotMarketClient marketClient,
    IBinanceSpotAccountClient accountClient,
    IRiskPolicyStore riskPolicyStore,
    IOutboxStore outboxStore,
    IAutonomyCheckpointStore checkpointStore,
    IAsyncOperationalControl operationalControl,
    BaselineDecisionCycle cycle,
    ILogger<AutonomyCycleHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runtime = options.Value;
        runtime.Validate();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(runtime, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                AutonomyCycleLog.Failed(logger, exception.GetType().Name, exception.Message);
                try
                {
                    _ = await operationalControl.EnterSafeModeAsync(
                        new OperationalControlCommand(
                            "autonomy-cycle",
                            $"A2 cycle failed: {exception.GetType().Name}",
                            IdempotencyKey.New(),
                            DateTimeOffset.UtcNow),
                        stoppingToken).ConfigureAwait(false);
                }
                catch (Exception controlException) when (controlException is not OperationCanceledException)
                {
                    AutonomyCycleLog.ControlFailure(logger, controlException.GetType().Name);
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(runtime.PollIntervalMilliseconds), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunOnceAsync(WorkerRuntimeOptions runtime, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var checkpointScope = $"{runtime.AutonomySymbol}:{runtime.AutonomyInterval}:{cycle.StrategyVersion}";
        var lastProcessedCloseUtc = await checkpointStore
            .GetLastProcessedCloseAsync(checkpointScope, cancellationToken)
            .ConfigureAwait(false);
        var policyDocument = await riskPolicyStore.GetActiveRiskPolicyAsync(now, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No active RiskPolicy is available for the autonomy cycle.");
        if (policyDocument.Policy.WithdrawalsEnabled)
        {
            throw new InvalidOperationException("Autonomy cycle refuses a RiskPolicy with withdrawals enabled.");
        }

        var klines = await marketClient.GetKlinesAsync(
            runtime.AutonomySymbol,
            runtime.AutonomyInterval,
            limit: runtime.AutonomyKlineLimit,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var closed = klines
            .Where(kline => kline.CloseTimeUtc <= now)
            .Select(kline => new CloseBar(new Symbol(runtime.AutonomySymbol), kline.CloseTimeUtc, kline.Close))
            .OrderBy(bar => bar.CloseTimeUtc)
            .ToArray();
        if (closed.Length == 0 || lastProcessedCloseUtc is { } previous && closed[^1].CloseTimeUtc <= previous)
        {
            return;
        }

        var account = await accountClient.GetAccountAsync(cancellationToken).ConfigureAwait(false);
        if (!account.CanTrade)
        {
            throw new InvalidOperationException("Binance account does not currently allow trading.");
        }

        var btc = account.Balances.FirstOrDefault(balance => string.Equals(balance.Asset, "BTC", StringComparison.OrdinalIgnoreCase));
        var btcQuantity = ParseBalance(btc?.Free) + ParseBalance(btc?.Locked);
        var latest = closed[^1];
        var portfolio = new PortfolioSnapshot(
            new Dictionary<Asset, Money>(),
            btcQuantity * latest.ClosePrice.Value,
            0m,
            0m);
        var marketRisk = new MarketRiskSnapshot(
            expectedSlippageBps: 5m,
            volatilityBps: 0m,
            availableLiquidityNotional: runtime.AutonomyEntryTargetNotional * 10m,
            latest.CloseTimeUtc);
        var request = new BaselineDecisionCycleRequest(
            closed,
            portfolio,
            policyDocument.Policy,
            operationalControl.Snapshot,
            runtime.AutonomyEntryTargetNotional,
            now,
            marketRisk);
        var result = cycle.Evaluate(request);
        lastProcessedCloseUtc = latest.CloseTimeUtc;
        if (result.IsNoTrade)
        {
            await checkpointStore.SetLastProcessedCloseAsync(checkpointScope, latest.CloseTimeUtc, cancellationToken).ConfigureAwait(false);
            AutonomyCycleLog.NoTrade(logger, latest.CloseTimeUtc, result.NoTradeReason ?? "UNSPECIFIED");
            return;
        }

        if (result.Execution is not { IsDispatchable: true, OutboxRecord: { } outbox })
        {
            AutonomyCycleLog.Blocked(logger, latest.CloseTimeUtc, string.Join(',', result.Execution?.Decision.ReasonCodes ?? []));
            return;
        }

        _ = await outboxStore.EnqueueAsync(outbox, cancellationToken).ConfigureAwait(false);
        await checkpointStore.SetLastProcessedCloseAsync(checkpointScope, latest.CloseTimeUtc, cancellationToken).ConfigureAwait(false);
        AutonomyCycleLog.Enqueued(logger, latest.CloseTimeUtc, outbox.Command.ClientOrderId.Value, outbox.Command.Side.ToString());
    }

    private static decimal ParseBalance(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0m
            ? parsed
            : 0m;
}

internal static partial class AutonomyCycleLog
{
    [LoggerMessage(LogLevel.Information, "Autonomy cycle selected NO_TRADE. CloseTime={CloseTime} Reason={Reason}")]
    public static partial void NoTrade(ILogger logger, DateTimeOffset closeTime, string reason);

    [LoggerMessage(LogLevel.Information, "Autonomy cycle blocked by risk/control. CloseTime={CloseTime} Reasons={Reasons}")]
    public static partial void Blocked(ILogger logger, DateTimeOffset closeTime, string reasons);

    [LoggerMessage(LogLevel.Information, "Autonomy cycle enqueued order. CloseTime={CloseTime} ClientOrderId={ClientOrderId} Side={Side}")]
    public static partial void Enqueued(ILogger logger, DateTimeOffset closeTime, string clientOrderId, string side);

    [LoggerMessage(LogLevel.Error, "Autonomy cycle failed. ErrorType={ErrorType} Error={Error}")]
    public static partial void Failed(ILogger logger, string errorType, string error);

    [LoggerMessage(LogLevel.Error, "Autonomy cycle could not enter SafeMode. ErrorType={ErrorType}")]
    public static partial void ControlFailure(ILogger logger, string errorType);
}
