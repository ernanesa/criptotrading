#pragma warning disable CA1515 // Hosted service is a host composition boundary.
#pragma warning disable CA1031 // Supervisor converts unexpected failures into SafeMode.

using System.Globalization;
using CryptoTrading.Backtesting.Autonomy;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Architecture;
using CryptoTrading.Domain.Execution;
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
    IAutonomyRiskContextStore riskContextStore,
    IOutboxStore outboxStore,
    IAutonomyCheckpointStore checkpointStore,
    IAsyncOperationalControl operationalControl,
    CryptoTrading.Domain.Runtime.IRuntimeSupervisor runtimeSupervisor,
    BaselineDecisionCycle cycle,
    ILogger<AutonomyCycleHostedService> logger) : BackgroundService
{
    private static readonly string[] QuoteAssets = ["USDT", "USDC", "FDUSD", "BUSD", "TUSD", "BTC", "ETH", "BNB", "EUR", "TRY"];
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
        var closed = ValidateAndMapKlines(
            klines,
            runtime.AutonomySymbol,
            runtime.AutonomyInterval,
            policyDocument.Policy.MaxMarketDataAge,
            now,
            lastProcessedCloseUtc);
        if (closed.Count == 0 || lastProcessedCloseUtc is { } previous && closed[^1].CloseTimeUtc <= previous)
        {
            return;
        }

        var account = await accountClient.GetAccountAsync(cancellationToken).ConfigureAwait(false);
        if (!account.CanTrade)
        {
            throw new InvalidOperationException("Binance account does not currently allow trading.");
        }

        var commission = await accountClient.GetCommissionAsync(runtime.AutonomySymbol, cancellationToken).ConfigureAwait(false);
        var discountValuation = await ResolveDiscountValuationAsync(
            commission,
            runtime.AutonomySymbol,
            account,
            marketClient,
            cancellationToken).ConfigureAwait(false);
        var commissionSnapshot = MapCommission(
            commission,
            runtime.AutonomySymbol,
            runtime.ReconciliationAccount,
            account,
            now,
            discountValuation);

        var btc = account.Balances.FirstOrDefault(balance => string.Equals(balance.Asset, "BTC", StringComparison.OrdinalIgnoreCase));
        var btcQuantity = ParseBalance(btc?.Free) + ParseBalance(btc?.Locked);
        var quoteAsset = new Asset("USDT");
        var quote = account.Balances.FirstOrDefault(balance => string.Equals(balance.Asset, quoteAsset.Value, StringComparison.OrdinalIgnoreCase));
        var availableQuoteLiquidity = ParseBalance(quote?.Free);
        var totalQuoteBalance = availableQuoteLiquidity + ParseBalance(quote?.Locked);
        var latest = closed[^1];
        var unsupportedAssetBalance = account.Balances
            .Where(balance => !string.Equals(balance.Asset, "BTC", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(balance.Asset, quoteAsset.Value, StringComparison.OrdinalIgnoreCase))
            .Any(balance => ParseBalance(balance.Free) + ParseBalance(balance.Locked) > 0m);
        decimal? currentEquityNotional = unsupportedAssetBalance
            ? null
            : totalQuoteBalance + (btcQuantity * latest.ClosePrice.Value);
        var dayStartUtc = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        // Unlike the decision checkpoint, the equity scope must survive
        // strategy-version changes; otherwise a new strategy could reset the
        // historical drawdown protection after only one baseline observation.
        var riskContextScope = $"equity:{runtime.ReconciliationAccount}:{runtime.AutonomySymbol}:{quoteAsset.Value}";
        var accounting = await riskContextStore.RecordAutonomyRiskContextAsync(
            riskContextScope,
            new Symbol(runtime.AutonomySymbol),
            quoteAsset,
            dayStartUtc,
            currentEquityNotional,
            now,
            cancellationToken).ConfigureAwait(false);
        var bookTicker = await marketClient.GetBookTickerAsync(
            runtime.AutonomySymbol,
            cancellationToken).ConfigureAwait(false);
        var executionMarket = BinanceExecutionMarketSnapshotMapper.Map(bookTicker);
        var positionBasisConsistent = btcQuantity <= 0m
            ? !accounting.HasPositionBasis || accounting.PositionQuantity <= 0m
            : accounting.HasPositionBasis && Math.Abs(accounting.PositionQuantity - btcQuantity) <= 0.00000001m;
        var riskContextComplete = positionBasisConsistent && accounting.RiskContextComplete;
        var riskContextReason = !positionBasisConsistent
            ? "POSITION_BASIS_DRIFT"
            : accounting.RiskContextUnavailableReason;
        var portfolio = new PortfolioSnapshot(
            new Dictionary<Asset, Money>
            {
                [quoteAsset] = new Money(availableQuoteLiquidity, quoteAsset),
                [new Asset("BTC")] = new Money(btcQuantity, new Asset("BTC")),
            },
            btcQuantity * latest.ClosePrice.Value,
            accounting.RealizedDailyLossNotional,
            accounting.DrawdownNotional ?? 0m,
            accounting.PositionCostNotional,
            riskContextComplete,
            riskContextReason);
        var marketRisk = new MarketRiskSnapshot(
            expectedSlippageBps: 5m,
            volatilityBps: 0m,
            availableLiquidityNotional: availableQuoteLiquidity,
            latest.CloseTimeUtc);
        var runtimeSnapshot = runtimeSupervisor.Snapshot;
        var effectiveControl = runtimeSnapshot.CanCreateNewExposure
            ? operationalControl.Snapshot
            : new OperationalControlSnapshot(
                OperationalMode.SafeMode,
                runtimeSnapshot.Revision,
                now,
                "runtime-supervisor",
                $"RUNTIME_NOT_READY:{runtimeSnapshot.State}");
        var request = new BaselineDecisionCycleRequest(
            closed,
            portfolio,
            policyDocument.Policy,
            effectiveControl,
            runtime.AutonomyEntryTargetNotional,
            DateTimeOffset.UtcNow,
            marketRisk,
            executionMarket,
            policyDocument.Policy.MaxMarketDataAge,
            TimeSpan.FromMinutes(1),
            availableQuoteLiquidity,
            new FeeReservePolicy(runtime.AutonomyFeeReserveBps),
            commissionSnapshot,
            policyDocument.Policy.MaxMarketDataAge);
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
            AutonomyCycleLog.Blocked(
                logger,
                latest.CloseTimeUtc,
                result.NoTradeReason ?? string.Join(',', result.Execution?.Decision.ReasonCodes ?? []));
            return;
        }

        _ = await outboxStore.EnqueueAsync(outbox, cancellationToken).ConfigureAwait(false);
        await checkpointStore.SetLastProcessedCloseAsync(checkpointScope, latest.CloseTimeUtc, cancellationToken).ConfigureAwait(false);
        AutonomyCycleLog.Enqueued(logger, latest.CloseTimeUtc, outbox.Command.ClientOrderId.Value, outbox.Command.Side.ToString());
    }

    private static decimal ParseBalance(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0m;
        }

        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) || parsed < 0m)
        {
            throw new InvalidOperationException("Binance returned an invalid account balance.");
        }

        return parsed;
    }

    private static EffectiveCommissionSnapshot MapCommission(
        BinanceCommissionSnapshot snapshot,
        string symbol,
        string accountScope,
        BinanceAccountSnapshot account,
        DateTimeOffset observedAtUtc,
        (bool Available, decimal? Price, DateTimeOffset? ObservedAtUtc, string? Source) discountValuation)
    {
        var response = snapshot.Response;
        var discountEnabled = response.Discount.EnabledForAccount && response.Discount.EnabledForSymbol;
        return new EffectiveCommissionSnapshot(
            new Symbol(symbol),
            "testnet",
            accountScope,
            MapRates(response.StandardCommission),
            MapRates(response.SpecialCommission),
            MapRates(response.TaxCommission),
            response.Discount.EnabledForAccount,
            response.Discount.EnabledForSymbol,
            response.Discount.DiscountAsset,
            ParseOptionalRate(response.Discount.Discount),
            !discountEnabled || discountValuation.Available,
            snapshot.ObservedAtUtc <= observedAtUtc ? snapshot.ObservedAtUtc : observedAtUtc,
            discountAssetQuotePrice: discountValuation.Price,
            discountAssetValuationAtUtc: discountValuation.ObservedAtUtc,
            discountAssetValuationSource: discountValuation.Source);
    }

    private static async Task<(bool Available, decimal? Price, DateTimeOffset? ObservedAtUtc, string? Source)> ResolveDiscountValuationAsync(
        BinanceCommissionSnapshot commission,
        string symbol,
        BinanceAccountSnapshot account,
        IBinanceSpotMarketClient marketClient,
        CancellationToken cancellationToken)
    {
        var discount = commission.Response.Discount;
        if (!discount.EnabledForAccount || !discount.EnabledForSymbol)
        {
            return (false, null, null, null);
        }

        if (!string.Equals(discount.DiscountAsset, "BNB", StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, null, null);
        }

        var bnb = account.Balances.FirstOrDefault(balance => string.Equals(balance.Asset, "BNB", StringComparison.OrdinalIgnoreCase));
        if (ParseBalance(bnb?.Free) + ParseBalance(bnb?.Locked) <= 0m)
        {
            return (false, null, null, null);
        }

        var normalized = new Symbol(symbol).Value;
        var quote = QuoteAssets.FirstOrDefault(asset => normalized.EndsWith(asset, StringComparison.Ordinal));
        if (quote is null || normalized.Length <= quote.Length || string.Equals(quote, "BNB", StringComparison.Ordinal))
        {
            return (false, null, null, null);
        }

        try
        {
            var ticker = await marketClient.GetBookTickerAsync("BNB" + quote, cancellationToken).ConfigureAwait(false);
            return ticker.BidPrice > 0m && ticker.ObservedAtUtc.Offset == TimeSpan.Zero
                ? (true, ticker.BidPrice, ticker.ObservedAtUtc, "binance:GET:/api/v3/ticker/bookTicker")
                : (false, null, null, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or BinanceApiException or FormatException)
        {
            return (false, null, null, null);
        }
    }

    private static CommissionRateSet MapRates(BinanceCommissionRateSet rates) => new(
        ParseBalance(rates.Maker), ParseBalance(rates.Taker), ParseBalance(rates.Buyer), ParseBalance(rates.Seller));

    private static decimal? ParseOptionalRate(string? value) => string.IsNullOrWhiteSpace(value) ? null : ParseBalance(value);

    private static List<CloseBar> ValidateAndMapKlines(
        IReadOnlyList<BinanceSpotKline> klines,
        string symbol,
        string interval,
        TimeSpan maximumStaleness,
        DateTimeOffset nowUtc,
        DateTimeOffset? previousCloseUtc)
    {
        ArgumentNullException.ThrowIfNull(klines);
        var guard = new BinanceClosedKlineGuard(symbol, interval, maximumStaleness);
        var accepted = new List<CloseBar>(klines.Count);
        foreach (var kline in klines.OrderBy(static item => item.OpenTimeUtc))
        {
            // REST returns the currently open candle as the last row. It is not
            // an error; it is simply not eligible for a closed-bar decision.
            if (kline.CloseTimeUtc.AddMilliseconds(1) > nowUtc)
            {
                continue;
            }

            var decision = guard.Evaluate(kline, nowUtc);
            if (!decision.IsAccepted || decision.Kline is null)
            {
                throw new InvalidOperationException(
                    $"Binance kline quality gate rejected the REST snapshot: {decision.RejectionReason}.");
            }

            accepted.Add(new CloseBar(
                new Symbol(decision.Kline.Symbol),
                decision.Kline.CloseTimeUtc,
                decision.Kline.Close));
        }

        if (previousCloseUtc is { } previous && accepted.Count > 0)
        {
            var expectedNext = AddInterval(previous, interval);
            var firstAfterCheckpoint = accepted.FirstOrDefault(bar => bar.CloseTimeUtc > previous);
            if (firstAfterCheckpoint is not null && firstAfterCheckpoint.CloseTimeUtc > expectedNext)
            {
                throw new InvalidOperationException("Binance REST kline snapshot has a gap after the durable autonomy checkpoint.");
            }
        }

        return accepted;
    }

    private static DateTimeOffset AddInterval(DateTimeOffset value, string interval) => interval switch
    {
        "1m" => value.AddMinutes(1),
        "5m" => value.AddMinutes(5),
        "15m" => value.AddMinutes(15),
        "1h" => value.AddHours(1),
        _ => throw new InvalidOperationException($"Unsupported autonomy interval '{interval}'.")
    };
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
