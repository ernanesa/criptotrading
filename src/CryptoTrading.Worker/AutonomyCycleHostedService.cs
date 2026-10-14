#pragma warning disable CA1515 // Hosted service is a host composition boundary.
#pragma warning disable CA1031 // Supervisor converts unexpected failures into SafeMode.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CryptoTrading.Backtesting;
using CryptoTrading.Backtesting.Autonomy;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Architecture;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Operations;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Regimes;
using CryptoTrading.Domain.Research;
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
    IAutonomyDecisionCommitStore decisionCommitStore,
    IAsyncOperationalControl operationalControl,
    CryptoTrading.Domain.Runtime.IRuntimeSupervisor runtimeSupervisor,
    BaselineDecisionCycle cycle,
    ILogger<AutonomyCycleHostedService> logger,
    ILocalOpenOrderStore? openOrderStore = null,
    IOrderCancellationStore? cancellationStore = null,
    AdaptiveDecisionCycle? adaptiveCycle = null,
    IMarketRegimeStore? regimeStore = null,
    IDriftObservationStore? driftStore = null,
    IBinanceSymbolTradingRulesProvider? rulesProvider = null,
    IPortfolioNavSnapshotStore? navSnapshots = null,
    IStrategyRegimePerformanceStore? durablePerformances = null,
    IBinanceSpotMarketDataClient? marketDataStreamClient = null) : BackgroundService
{
    private static readonly string[] QuoteAssets = ["USDT", "USDC", "FDUSD", "BUSD", "TUSD", "BTC", "ETH", "BNB", "EUR", "TRY"];
    private readonly System.Threading.Channels.Channel<DateTimeOffset> barCloseSignalChannel =
        System.Threading.Channels.Channel.CreateBounded<DateTimeOffset>(
            new System.Threading.Channels.BoundedChannelOptions(1)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest
            });
    private int consecutiveTransientFailures;
    private const int MaxConsecutiveTransientFailures = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runtime = options.Value;
        runtime.Validate();
        var streamConsumerTask = marketDataStreamClient is not null
            ? ConsumeMarketDataStreamAsync(runtime, stoppingToken)
            : Task.CompletedTask;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var symbols = runtime.ResolveAutonomySymbols();
                    foreach (var symbol in symbols)
                    {
                        await RunOnceAsync(runtime, symbol, stoppingToken).ConfigureAwait(false);
                    }
                    consecutiveTransientFailures = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    using var errorScope = logger.BeginScope(new Dictionary<string, object?> { ["error_stack"] = exception.StackTrace });
                    var classification = FailureClassifier.Classify(exception);
                    AutonomyCycleLog.Failed(logger, exception.GetType().Name, classification.ReasonCode);
                    try
                    {
                        if (classification.TargetState == CryptoTrading.Domain.Runtime.RuntimeState.KillSwitch)
                        {
                            _ = await operationalControl.ActivateKillSwitchAsync(
                                new OperationalControlCommand(
                                    "autonomy-cycle",
                                    $"A2 terminal failure: {classification.ReasonCode}",
                                    IdempotencyKey.New(),
                                    DateTimeOffset.UtcNow),
                                stoppingToken).ConfigureAwait(false);
                        }
                        else if (classification.Severity == FailureSeverity.Transient && ++consecutiveTransientFailures < MaxConsecutiveTransientFailures)
                        {
                            var backoff = classification.SuggestedBackoff ?? TimeSpan.FromSeconds(2);
                            await Task.Delay(backoff, stoppingToken).ConfigureAwait(false);
                            continue;
                        }
                        else
                        {
                            _ = await operationalControl.EnterSafeModeAsync(
                                new OperationalControlCommand(
                                    "autonomy-cycle",
                                    $"A2 failure: {classification.ReasonCode} (consecutive={consecutiveTransientFailures})",
                                    IdempotencyKey.New(),
                                    DateTimeOffset.UtcNow),
                                stoppingToken).ConfigureAwait(false);
                        }
                    }
                    catch (Exception controlException) when (controlException is not OperationCanceledException)
                    {
                        AutonomyCycleLog.ControlFailure(logger, controlException.GetType().Name);
                    }
                }

                if (marketDataStreamClient is null)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(runtime.PollIntervalMilliseconds), stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    delayCts.CancelAfter(runtime.PollIntervalMilliseconds);
                    try
                    {
                        if (await barCloseSignalChannel.Reader.WaitToReadAsync(delayCts.Token).ConfigureAwait(false))
                        {
                            while (barCloseSignalChannel.Reader.TryRead(out _)) { }
                        }
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Polling interval elapsed with no bar close signal; continue to next scheduled cycle.
                    }
                }
            }
        }
        finally
        {
            try
            {
                await streamConsumerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }
    }

    private async Task ConsumeMarketDataStreamAsync(WorkerRuntimeOptions runtime, CancellationToken stoppingToken)
    {
        var subscriptions = runtime.ResolveAutonomySymbols()
            .Select(s => new BinanceMarketSubscription(s, BinanceMarketStreamKind.Kline, runtime.AutonomyInterval))
            .ToList();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var marketEvent in marketDataStreamClient!.ReadAsync(subscriptions, stoppingToken).ConfigureAwait(false))
                {
                    if (marketEvent is BinanceKlineEvent kline && kline.IsClosed)
                    {
                        barCloseSignalChannel.Writer.TryWrite(kline.ReceivedAt);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AutonomyCycleLog.StreamDisconnected(logger, ex);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    internal Task RunOnceAsync(WorkerRuntimeOptions runtime, CancellationToken cancellationToken)
    {
        return RunOnceAsync(runtime, runtime.AutonomySymbol, cancellationToken);
    }

    internal async Task RunOnceAsync(WorkerRuntimeOptions runtime, string symbol, CancellationToken cancellationToken)
    {
        using var cycleActivity = CryptoTrading.Observability.TradingTelemetry.ActivitySource.StartActivity("autonomy.cycle");
        using var cycleScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["event_name"] = "autonomy.cycle",
            ["symbol"] = symbol,
            ["engine_mode"] = runtime.AutonomyEngineMode,
            ["trace_id"] = System.Diagnostics.Activity.Current?.TraceId.ToString()
        });
        var now = DateTimeOffset.UtcNow;
        var symbolObj = new Symbol(symbol);
        var activeCycle = cycle.Parameters.Symbol == symbolObj
            ? cycle
            : new BaselineDecisionCycle(new TrendFollowingParameters(
                cycle.Parameters.StrategyId,
                cycle.Parameters.Version,
                symbolObj,
                cycle.Parameters.FastPeriod,
                cycle.Parameters.SlowPeriod,
                cycle.Parameters.FeeRate,
                cycle.Parameters.SlippageRate));
        var isAdaptive = adaptiveCycle is not null && !string.Equals(runtime.AutonomyEngineMode, "Baseline", StringComparison.OrdinalIgnoreCase);
        var activeStrategyVersion = isAdaptive ? AdaptiveDecisionCycle.StrategyVersion : activeCycle.StrategyVersion;
        var checkpointScope = $"{symbol}:{runtime.AutonomyInterval}:{activeStrategyVersion}";
        var lastProcessedCloseUtc = await decisionCommitStore
            .GetLastProcessedCloseAsync(checkpointScope, cancellationToken)
            .ConfigureAwait(false);
        var policyDocument = await riskPolicyStore.GetActiveRiskPolicyAsync(now, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No active RiskPolicy is available for the autonomy cycle.");
        if (policyDocument.Policy.WithdrawalsEnabled)
        {
            throw new InvalidOperationException("Autonomy cycle refuses a RiskPolicy with withdrawals enabled.");
        }

        var klines = await marketClient.GetKlinesAsync(
            symbol,
            runtime.AutonomyInterval,
            limit: runtime.AutonomyKlineLimit,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var closed = ValidateAndMapKlines(
            klines,
            symbol,
            runtime.AutonomyInterval,
            policyDocument.Policy.MaxMarketDataAge,
            now,
            lastProcessedCloseUtc);
        if (closed.Count == 0 || lastProcessedCloseUtc is { } previous && closed[^1].Bar.CloseTimeUtc <= previous)
        {
            return;
        }

        var featureSnapshot = BaselineFeatureSnapshot.Create(
            closed,
            activeCycle.Parameters);

        if (openOrderStore is not null)
        {
            var openOrders = await openOrderStore.ReadOpenOrdersAsync(cancellationToken).ConfigureAwait(false);
            var unknownOrder = openOrders.FirstOrDefault(o =>
                string.Equals(o.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(o.Status, "UNKNOWN", StringComparison.OrdinalIgnoreCase));

            if (unknownOrder is not null)
            {
                var blockReason = $"UNKNOWN_ORDER_EXISTS:{unknownOrder.ClientOrderId}";
                AutonomyCycleLog.Blocked(logger, closed[^1].Bar.CloseTimeUtc, blockReason);
                var blockedTrace = JsonSerializer.Serialize(new
                {
                    Reason = blockReason,
                    BlockedClientOrderId = unknownOrder.ClientOrderId,
                    Symbol = symbol,
                    CloseTimeUtc = closed[^1].Bar.CloseTimeUtc,
                });
                var environment = runtime.ExecutionMode.Trim().ToUpperInvariant();
                var accountScope = runtime.ReconciliationAccount.Trim();
                var canonicalIdentity = string.Join('|', environment, accountScope, symbol,
                    activeCycle.StrategyId, activeCycle.StrategyVersion, closed[^1].Bar.CloseTimeUtc.ToString("O", CultureInfo.InvariantCulture));
                var decisionId = "ad-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalIdentity)));
                await decisionCommitStore.CommitAsync(
                    new AutonomyDecisionCommit(
                        decisionId,
                        environment,
                        accountScope,
                        checkpointScope,
                        new Symbol(symbol),
                        activeCycle.StrategyId,
                        activeCycle.StrategyVersion,
                        closed[^1].Bar.CloseTimeUtc,
                        featureSnapshot.AvailableAtUtc,
                        featureSnapshot.SchemaVersion,
                        featureSnapshot.ConfigurationFingerprint,
                        featureSnapshot.FeatureFingerprint,
                        TrendFollowingSignal.NoTrade.ToString(),
                        "UNKNOWN_ORDER_BLOCKED",
                        null,
                        "BLOCKED",
                        blockReason,
                        blockedTrace,
                        null),
                    cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        ExecutionReplanCandidate? replanCandidate = null;
        if (cancellationStore is not null)
        {
            var candidates = await cancellationStore.ReadReplanCandidatesAsync(100, cancellationToken).ConfigureAwait(false);
            replanCandidate = candidates.LastOrDefault(c =>
                string.Equals(c.Symbol.Value, symbol, StringComparison.OrdinalIgnoreCase));
        }

        var account = await accountClient.GetAccountAsync(cancellationToken).ConfigureAwait(false);
        if (!account.CanTrade)
        {
            throw new InvalidOperationException("Binance account does not currently allow trading.");
        }

        var commission = await accountClient.GetCommissionAsync(symbol, cancellationToken).ConfigureAwait(false);
        var discountValuation = await ResolveDiscountValuationAsync(
            commission,
            symbol,
            account,
            marketClient,
            cancellationToken).ConfigureAwait(false);
        var commissionSnapshot = MapCommission(
            commission,
            symbol,
            runtime.ReconciliationAccount,
            account,
            now,
            discountValuation);

        var baseAsset = symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
            ? symbol[..^4]
            : symbol;
        var baseBalance = account.Balances.FirstOrDefault(balance => string.Equals(balance.Asset, baseAsset, StringComparison.OrdinalIgnoreCase));
        var rawBaseQuantity = ParseBalance(baseBalance?.Free) + ParseBalance(baseBalance?.Locked);
        var isTestnetFaucetWallet = string.Equals(runtime.ExecutionMode, "TESTNET", StringComparison.OrdinalIgnoreCase)
            && account.Balances.Count > 20;
        var baseQuantity = isTestnetFaucetWallet && string.Equals(baseAsset, "BTC", StringComparison.OrdinalIgnoreCase) && rawBaseQuantity >= 0.99m
            ? Math.Max(0m, rawBaseQuantity - 1.0m)
            : rawBaseQuantity;
        var quoteAsset = new Asset("USDT");
        var quote = account.Balances.FirstOrDefault(balance => string.Equals(balance.Asset, quoteAsset.Value, StringComparison.OrdinalIgnoreCase));
        var availableQuoteLiquidity = ParseBalance(quote?.Free);
        var totalQuoteBalance = availableQuoteLiquidity + ParseBalance(quote?.Locked);
        var latest = closed[^1].Bar;
        var configuredUniverseAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            quoteAsset.Value,
            "BNB"
        };
        foreach (var s in runtime.ResolveAutonomySymbols())
        {
            var b = s.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s;
            configuredUniverseAssets.Add(b);
        }

        var unsupportedAssetBalance = !isTestnetFaucetWallet && account.Balances
            .Where(balance => !configuredUniverseAssets.Contains(balance.Asset))
            .Any(balance => ParseBalance(balance.Free) + ParseBalance(balance.Locked) > 0m);
        decimal? currentEquityNotional = unsupportedAssetBalance
            ? null
            : totalQuoteBalance + (baseQuantity * latest.ClosePrice.Value);
        var dayStartUtc = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        // Unlike the decision checkpoint, the equity scope must survive
        // strategy-version changes; otherwise a new strategy could reset the
        // historical drawdown protection after only one baseline observation.
        var riskContextScope = $"equity:{runtime.ReconciliationAccount}:{symbol}:{quoteAsset.Value}";
        var accounting = await riskContextStore.RecordAutonomyRiskContextAsync(
            riskContextScope,
            new Symbol(symbol),
            quoteAsset,
            dayStartUtc,
            currentEquityNotional,
            now,
            cancellationToken).ConfigureAwait(false);
        var bookTicker = await marketClient.GetBookTickerAsync(
            symbol,
            cancellationToken).ConfigureAwait(false);
        if (navSnapshots is not null && !unsupportedAssetBalance &&
            bookTicker.ObservedAtUtc <= DateTimeOffset.UtcNow &&
            DateTimeOffset.UtcNow - bookTicker.ObservedAtUtc <= policyDocument.Policy.MaxMarketDataAge)
        {
            var valuationPrice = (bookTicker.BidPrice + bookTicker.AskPrice) / 2m;
            decimal? unrealized = baseQuantity == 0m ? 0m :
                accounting.HasPositionBasis && Math.Abs(accounting.PositionQuantity - baseQuantity) <= 0.00000001m
                    ? baseQuantity * valuationPrice - accounting.PositionCostNotional : null;
            await navSnapshots.RecordNavSnapshotAsync(new PortfolioNavSnapshot(
                symbol, quoteAsset.Value, bookTicker.ObservedAtUtc,
                totalQuoteBalance + baseQuantity * valuationPrice, unrealized,
                null, "account-snapshot+observed-book-mid"), cancellationToken).ConfigureAwait(false);
            AutonomyCycleLog.Valuation(logger, symbol, bookTicker.ObservedAtUtc,
                totalQuoteBalance + baseQuantity * valuationPrice, unrealized);
        }
        BinanceSymbolTradingRules? rules = null;
        if (rulesProvider is not null)
        {
            try
            {
                rules = await rulesProvider.GetAsync(symbol, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The execution market remains incomplete below, so a new exposure fails closed.
            }
        }
        var mappedExecutionMarket = BinanceExecutionMarketSnapshotMapper.Map(bookTicker, rules);
        var hasCompleteTradingRules = HasCompleteTradingRules(rules, symbol);
        var executionMarket = new ExecutionMarketSnapshot(
            mappedExecutionMarket.BestBid,
            mappedExecutionMarket.BestAsk,
            mappedExecutionMarket.AvailableLiquidityNotional,
            mappedExecutionMarket.ObservedAtUtc,
            isComplete: mappedExecutionMarket.IsComplete && hasCompleteTradingRules,
            quantityStepSize: mappedExecutionMarket.QuantityStepSize,
            priceTickSize: mappedExecutionMarket.PriceTickSize,
            bidQuantity: mappedExecutionMarket.BidQuantity,
            askQuantity: mappedExecutionMarket.AskQuantity);
        var baseAssetObj = new Asset(baseAsset);
        var positionBasisConsistent = baseQuantity <= 0m
            ? !accounting.HasPositionBasis || accounting.PositionQuantity <= 0m
            : accounting.HasPositionBasis && Math.Abs(accounting.PositionQuantity - baseQuantity) <= 0.00000001m;
        var riskContextComplete = positionBasisConsistent && accounting.RiskContextComplete;
        var riskContextReason = !positionBasisConsistent
            ? "POSITION_BASIS_DRIFT"
            : accounting.RiskContextUnavailableReason;
        var portfolio = new PortfolioSnapshot(
            new Dictionary<Asset, Money>
            {
                [quoteAsset] = new Money(availableQuoteLiquidity, quoteAsset),
                [baseAssetObj] = new Money(baseQuantity, baseAssetObj),
            },
            baseQuantity * latest.ClosePrice.Value,
            accounting.RealizedDailyLossNotional,
            accounting.DrawdownNotional ?? 0m,
            accounting.PositionCostNotional,
            riskContextComplete,
            riskContextReason);
        var candles = closed.Select(item => new MarketCandle(item.Bar.CloseTimeUtc, item.Bar.ClosePrice.Value)).ToArray();
        var bids = new[] { new MarketBookLevel(bookTicker.BidPrice, bookTicker.BidQuantity) };
        var asks = new[] { new MarketBookLevel(bookTicker.AskPrice, bookTicker.AskQuantity) };
        var buyRisk = MarketRiskCalculator.Calculate(candles, bids, asks, MarketRiskSide.Buy, bookTicker.ObservedAtUtc);
        var sellRisk = MarketRiskCalculator.Calculate(candles, bids, asks, MarketRiskSide.Sell, bookTicker.ObservedAtUtc);
        var marketRisk = new MarketRiskSnapshot(
            Math.Max(buyRisk.ExpectedSlippageBps, sellRisk.ExpectedSlippageBps),
            buyRisk.VolatilityBps,
            Math.Min(buyRisk.AvailableLiquidityNotional, sellRisk.AvailableLiquidityNotional),
            bookTicker.ObservedAtUtc, buyRisk.IsComplete && sellRisk.IsComplete);
        var runtimeSnapshot = runtimeSupervisor.Snapshot;
        var effectiveControl = runtimeSnapshot.CanCreateNewExposure
            ? operationalControl.Snapshot
            : new OperationalControlSnapshot(
                OperationalMode.SafeMode,
                runtimeSnapshot.Revision,
                now,
                "runtime-supervisor",
                $"RUNTIME_NOT_READY:{runtimeSnapshot.State}");
        var bars = closed.Select(static envelope => envelope.Bar).ToArray();

        // When adaptive engine mode is enabled (A3 - A5), evaluate candidate strategies and regimes
        if (isAdaptive && adaptiveCycle is not null)
        {
            var adaptiveCheckpointScope = checkpointScope;
            var hasPendingExitReplan = replanCandidate is { Side: CryptoTrading.Domain.Orders.OrderSide.Sell } rc &&
                                       portfolio.GrossExposureNotional > 5m &&
                                       rc.CreatedAtUtc >= now.AddMinutes(-15);
            var hasPendingEntryReplan = replanCandidate is { Side: CryptoTrading.Domain.Orders.OrderSide.Buy } entryRc &&
                                        portfolio.GrossExposureNotional <= 5m &&
                                        entryRc.CreatedAtUtc >= now.AddMinutes(-2);
            var adaptiveRequest = new AdaptiveDecisionCycleRequest(
                bars,
                portfolio,
                policyDocument.Policy,
                effectiveControl,
                runtime.AutonomyEntryTargetNotional,
                DateTimeOffset.UtcNow,
                marketRisk,
                executionMarket,
                policyDocument.Policy.MaxMarketDataAge,
                TimeSpan.FromSeconds(42),
                availableQuoteLiquidity,
                new FeeReservePolicy(runtime.AutonomyFeeReserveBps),
                commissionSnapshot,
                policyDocument.Policy.MaxMarketDataAge,
                featureSnapshot,
                UseConservativeCommissionReserve: true,
                ExecutionAttempt: hasPendingExitReplan || hasPendingEntryReplan ? replanCandidate!.NextAttempt : 0,
                HasPendingExitReplan: hasPendingExitReplan,
#pragma warning disable CA1308 // Strategy identifiers use lowercase slugs by design
                PendingExitReplanStrategyId: hasPendingExitReplan ? $"trend-following-{symbol.ToLowerInvariant()}" : null);
#pragma warning restore CA1308

            if (durablePerformances is not null)
            {
                adaptiveCycle.LoadStrategyRegimePerformances(await durablePerformances.GetPerformancesAsync(cancellationToken).ConfigureAwait(false));
            }
            var adaptiveResult = adaptiveCycle.Evaluate(adaptiveRequest);

            if (regimeStore is not null)
            {
                await regimeStore.RecordMarketRegimeAsync(adaptiveResult.Regime, cancellationToken).ConfigureAwait(false);
            }

            if (driftStore is not null && adaptiveResult.Regime.VolatilityRate > MarketRegimeClassifier.DefaultMaxVolatilityThreshold)
            {
                var driftObs = new DriftObservation(
                    adaptiveResult.SelectedStrategyId ?? AdaptiveDecisionCycle.StrategyId,
                    DriftType.VolatilityRegimeDrift,
                    ExpectedValue: 0.02m,
                    ObservedValue: adaptiveResult.Regime.VolatilityRate,
                    AllowedDeviationThreshold: MarketRegimeClassifier.DefaultMaxVolatilityThreshold,
                    adaptiveResult.Regime.ObservedAtUtc);
                var driftEval = DriftMonitoringEngine.Evaluate(driftObs);
                await driftStore.RecordDriftObservationAsync(driftObs, driftEval, cancellationToken).ConfigureAwait(false);
            }

            var adaptiveOutbox = hasCompleteTradingRules && adaptiveResult.Execution is { IsDispatchable: true } exec
                ? exec.OutboxRecord
                : null;
            var adaptiveOutcome = !hasCompleteTradingRules || adaptiveResult.IsNoTrade
                ? "NO_TRADE"
                : adaptiveOutbox is not null ? "DISPATCHABLE" : "BLOCKED";
            var adaptiveReason = !hasCompleteTradingRules
                ? "BINANCE_TRADING_RULES_UNAVAILABLE_OR_INCOMPLETE"
                : adaptiveResult.NoTradeReason ?? string.Join(',', adaptiveResult.Execution?.Decision.ReasonCodes ?? []);

            await decisionCommitStore.CommitAsync(
                CreateAdaptiveDecisionCommit(runtime, new Symbol(symbol), adaptiveCheckpointScope, featureSnapshot, adaptiveResult, adaptiveOutcome, adaptiveReason, adaptiveOutbox, replanCandidate),
                cancellationToken).ConfigureAwait(false);

            lastProcessedCloseUtc = latest.CloseTimeUtc;
            if (!hasCompleteTradingRules || adaptiveResult.IsNoTrade)
            {
                AutonomyCycleLog.NoTrade(logger, latest.CloseTimeUtc, adaptiveReason);
                return;
            }

            if (adaptiveOutbox is null)
            {
                AutonomyCycleLog.Blocked(
                    logger,
                    latest.CloseTimeUtc,
                    adaptiveReason);
                return;
            }

            AutonomyCycleLog.Enqueued(logger, latest.CloseTimeUtc, adaptiveOutbox.Command.ClientOrderId.Value, adaptiveOutbox.Command.Side.ToString());
            return;
        }

        var request = new BaselineDecisionCycleRequest(
            bars,
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
            policyDocument.Policy.MaxMarketDataAge,
            featureSnapshot,
            UseConservativeCommissionReserve: true);
        var result = activeCycle.Evaluate(request);
        var outbox = hasCompleteTradingRules ? GetDispatchableOutbox(result) : null;
        var outcome = !hasCompleteTradingRules || result.IsNoTrade
            ? "NO_TRADE"
            : outbox is not null ? "DISPATCHABLE" : "BLOCKED";
        var reason = !hasCompleteTradingRules
            ? "BINANCE_TRADING_RULES_UNAVAILABLE_OR_INCOMPLETE"
            : result.NoTradeReason ?? string.Join(',', result.Execution?.Decision.ReasonCodes ?? []);
        await decisionCommitStore.CommitAsync(
            CreateDecisionCommit(runtime, checkpointScope, featureSnapshot, result, outcome, reason, outbox, replanCandidate),
            cancellationToken).ConfigureAwait(false);
        lastProcessedCloseUtc = latest.CloseTimeUtc;
        if (!hasCompleteTradingRules || result.IsNoTrade)
        {
            AutonomyCycleLog.NoTrade(logger, latest.CloseTimeUtc, reason);
            return;
        }

        if (outbox is null)
        {
            AutonomyCycleLog.Blocked(
                logger,
                latest.CloseTimeUtc,
                reason);
            return;
        }

        AutonomyCycleLog.Enqueued(logger, latest.CloseTimeUtc, outbox.Command.ClientOrderId.Value, outbox.Command.Side.ToString());
    }

    private static AutonomyDecisionCommit CreateAdaptiveDecisionCommit(
        WorkerRuntimeOptions runtime,
        Symbol symbol,
        string checkpointScope,
        BaselineFeatureSnapshot featureSnapshot,
        AdaptiveDecisionCycleResult result,
        string outcome,
        string reason,
        OutboxRecord? outbox,
        ExecutionReplanCandidate? replanCandidate = null)
    {
        var environment = runtime.ExecutionMode.Trim().ToUpperInvariant();
        var accountScope = runtime.ReconciliationAccount.Trim();
        var strategyId = result.SelectedStrategyId ?? AdaptiveDecisionCycle.StrategyId;
        var strategyVersion = result.SelectedStrategyVersion;
        var canonicalIdentity = string.Join('|', environment, accountScope, symbol.Value,
            strategyId, strategyVersion, featureSnapshot.Decision.CloseTimeUtc.ToString("O", CultureInfo.InvariantCulture));
        var decisionId = "ad-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalIdentity)));
        var risk = result.Execution?.Decision;

        return new(
            decisionId, environment, accountScope, checkpointScope, symbol, strategyId, strategyVersion,
            featureSnapshot.Decision.CloseTimeUtc, featureSnapshot.AvailableAtUtc, featureSnapshot.SchemaVersion,
            featureSnapshot.ConfigurationFingerprint, featureSnapshot.FeatureFingerprint, result.RecommendedSide?.ToString() ?? "NoTrade",
            result.Regime.Regime.ToString(),
            risk, outcome, string.IsNullOrWhiteSpace(reason) ? null : reason, result.DecisionTraceJson, outbox);
    }

    private static AutonomyDecisionCommit CreateDecisionCommit(
        WorkerRuntimeOptions runtime,
        string checkpointScope,
        BaselineFeatureSnapshot featureSnapshot,
        BaselineDecisionCycleResult result,
        string outcome,
        string reason,
        OutboxRecord? outbox,
        ExecutionReplanCandidate? replanCandidate = null)
    {
        var decision = result.Decision;
        var environment = runtime.ExecutionMode.Trim().ToUpperInvariant();
        var accountScope = runtime.ReconciliationAccount.Trim();
        var canonicalIdentity = string.Join('|', environment, accountScope, decision.Symbol.Value,
            decision.StrategyId, decision.StrategyVersion, decision.CloseTimeUtc.ToString("O", CultureInfo.InvariantCulture));
        var decisionId = "ad-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalIdentity)));
        var risk = result.Execution?.Decision;
        var trace = JsonSerializer.Serialize(new
        {
            featureSnapshot.Decision.BarIndex,
            featureSnapshot.Decision.FastEma,
            featureSnapshot.Decision.SlowEma,
            featureSnapshot.Decision.FeeRate,
            featureSnapshot.Decision.SlippageRate,
            featureSnapshot.Decision.EstimatedRoundTripCostRate,
            Result = outcome,
            Reason = reason,
            IntentClientOrderId = result.Intent?.ClientOrderId.Value,
            OutboxMessageId = outbox?.MessageId,
            ReplanCandidateId = replanCandidate?.CandidateId,
            ReplanAttempt = replanCandidate?.NextAttempt,
        });
        return new(
            decisionId, environment, accountScope, checkpointScope, decision.Symbol, decision.StrategyId, decision.StrategyVersion,
            decision.CloseTimeUtc, featureSnapshot.AvailableAtUtc, featureSnapshot.SchemaVersion,
            featureSnapshot.ConfigurationFingerprint, featureSnapshot.FeatureFingerprint, decision.Signal.ToString(), decision.Reason.ToString(),
            risk, outcome, string.IsNullOrWhiteSpace(reason) ? null : reason, trace, outbox);
    }

    private static OutboxRecord? GetDispatchableOutbox(BaselineDecisionCycleResult result) =>
        result.Execution is { IsDispatchable: true } execution ? execution.OutboxRecord : null;

    private static bool HasCompleteTradingRules(BinanceSymbolTradingRules? rules, string symbol) =>
        rules is
        {
            Status: "TRADING",
            MinimumPrice: >= 0m,
            MaximumPrice: >= 0m,
            PriceTickSize: > 0m,
            MinimumQuantity: > 0m,
            MaximumQuantity: >= 0m,
            QuantityStepSize: > 0m,
            MinimumNotional: > 0m,
            MaximumNotional: >= 0m
        } && string.Equals(rules.Symbol, symbol, StringComparison.OrdinalIgnoreCase);

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
        var discountActive = response.Discount.EnabledForAccount &&
                             response.Discount.EnabledForSymbol &&
                             ParseOptionalRate(response.Discount.Discount) is > 0m;
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
            discountActive && discountValuation.Available,
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
        if (!discount.EnabledForAccount || !discount.EnabledForSymbol || ParseOptionalRate(discount.Discount) is not > 0m)
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

    internal static List<ClosedBarEnvelope> ValidateAndMapKlines(
        IReadOnlyList<BinanceSpotKline> klines,
        string symbol,
        string interval,
        TimeSpan maximumStaleness,
        DateTimeOffset nowUtc,
        DateTimeOffset? previousCloseUtc)
    {
        ArgumentNullException.ThrowIfNull(klines);
        var guard = new BinanceClosedKlineGuard(symbol, interval, maximumStaleness);
        var accepted = new List<ClosedBarEnvelope>(klines.Count);
        var eligibleKlines = klines
            .OrderBy(static item => item.OpenTimeUtc)
            .Where(kline => kline.CloseTimeUtc.AddMilliseconds(1) <= nowUtc)
            .ToList();

        for (var i = 0; i < eligibleKlines.Count; i++)
        {
            var kline = eligibleKlines[i];
            var isLatest = i == eligibleKlines.Count - 1;
            // The staleness quality gate should only evaluate whether the snapshot itself (the latest closed bar)
            // is fresh relative to nowUtc. Historical bars in the backfill window closed in the past, so evaluating
            // them against nowUtc would falsely reject them as Stale.
            var evaluationTime = isLatest ? nowUtc : kline.CloseTimeUtc.AddMilliseconds(1);

            var decision = guard.Evaluate(kline, evaluationTime);
            if (!decision.IsAccepted || decision.Kline is null)
            {
                throw new InvalidOperationException(
                    $"Binance kline quality gate rejected the REST snapshot: {decision.RejectionReason}.");
            }

            accepted.Add(new ClosedBarEnvelope(
                accepted.Count,
                new CloseBar(
                    new Symbol(decision.Kline.Symbol),
                    decision.Kline.CloseTimeUtc,
                    decision.Kline.Close),
                nowUtc));
        }

        if (previousCloseUtc is { } previous && accepted.Count > 0)
        {
            var expectedNext = AddInterval(previous, interval);
            var firstAfterCheckpoint = accepted.FirstOrDefault(bar => bar.Bar.CloseTimeUtc > previous);
            if (firstAfterCheckpoint is not null && firstAfterCheckpoint.Bar.CloseTimeUtc > expectedNext)
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
    [LoggerMessage(LogLevel.Information, "Portfolio valuation recorded. Symbol={Symbol} ObservedAt={ObservedAt} NavQuote={NavQuote} UnrealizedPnlQuote={UnrealizedPnlQuote} ExternalCashFlowKnown=false")]
    public static partial void Valuation(ILogger logger, string symbol, DateTimeOffset observedAt, decimal navQuote, decimal? unrealizedPnlQuote);
    [LoggerMessage(LogLevel.Information, "Autonomy cycle selected NO_TRADE. CloseTime={CloseTime} Reason={Reason}")]
    public static partial void NoTrade(ILogger logger, DateTimeOffset closeTime, string reason);

    [LoggerMessage(LogLevel.Information, "Autonomy cycle blocked by risk/control. CloseTime={CloseTime} Reasons={Reasons}")]
    public static partial void Blocked(ILogger logger, DateTimeOffset closeTime, string reasons);

    [LoggerMessage(LogLevel.Information, "Autonomy cycle enqueued order. CloseTime={CloseTime} ClientOrderId={ClientOrderId} Side={Side}")]
    public static partial void Enqueued(ILogger logger, DateTimeOffset closeTime, string clientOrderId, string side);

    [LoggerMessage(LogLevel.Error, "Autonomy cycle failed. ErrorType={ErrorType} Error={Error}")]
    public static partial void Failed(ILogger logger, string errorType, string error);

    [LoggerMessage(LogLevel.Warning, "Binance market data WebSocket stream disconnected. Reconnecting in 2 seconds...")]
    public static partial void StreamDisconnected(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Error, "Autonomy cycle could not enter SafeMode. ErrorType={ErrorType}")]
    public static partial void ControlFailure(ILogger logger, string errorType);
}
