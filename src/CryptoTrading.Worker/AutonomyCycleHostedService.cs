#pragma warning disable CA1515 // Hosted service is a host composition boundary.
#pragma warning disable CA1031 // Supervisor converts unexpected failures into SafeMode.

using System.Collections.Concurrent;
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
    IBinanceSpotMarketDataClient? marketDataStreamClient = null,
    CryptoTrading.Observability.TradingAlertDispatcher? alertDispatcher = null,
    IOutboxWakeupSignal? outboxWakeupSignal = null) : BackgroundService
{
    private static readonly string[] QuoteAssets = ["USDT", "USDC", "FDUSD", "BUSD", "TUSD", "BTC", "ETH", "BNB", "EUR", "TRY"];
    private readonly System.Threading.Channels.Channel<DateTimeOffset> barCloseSignalChannel =
        System.Threading.Channels.Channel.CreateBounded<DateTimeOffset>(
            new System.Threading.Channels.BoundedChannelOptions(1)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest
            });
    private int consecutiveTransientFailures;
    private const int MaxConsecutiveTransientFailures = 6;
    private readonly ConcurrentDictionary<string, BinanceBookTicker> streamBookTickers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BinanceBookTicker> previousBookTickers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, decimal> temporalOfiBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, decimal> tradeAggressionDeltaBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BinanceLocalOrderBook> localOrderBooks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<BinanceSpotKline>> klineCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastKlineFetchUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> positionOriginatingStrategy = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, decimal> positionLifetimePeakProfitBps = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (decimal CostNotional, decimal Quantity)> positionCostBases = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastIntrabarTriggerUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastClosedTradeAtBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? safeModeEnteredAtUtc;
    private DateTimeOffset? lastProlongedSafeModeAlertAtUtc;
    internal ConcurrentDictionary<string, BinanceBookTicker> StreamBookTickers => streamBookTickers;
    internal ConcurrentDictionary<string, List<BinanceSpotKline>> KlineCache => klineCache;

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
                    if (symbols.Count == 1)
                    {
                        await RunOnceAsync(runtime, symbols[0], stoppingToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await Task.WhenAll(symbols.Select(symbol => RunOnceAsync(runtime, symbol, stoppingToken))).ConfigureAwait(false);
                    }
                    consecutiveTransientFailures = 0;
                    var currentControl = operationalControl.Snapshot;
                    if (currentControl.Mode == OperationalMode.SafeMode)
                    {
                        safeModeEnteredAtUtc ??= DateTimeOffset.UtcNow;
                        var duration = DateTimeOffset.UtcNow - safeModeEnteredAtUtc.Value;
                        if (duration > TimeSpan.FromMinutes(15) &&
                            (lastProlongedSafeModeAlertAtUtc is null || DateTimeOffset.UtcNow - lastProlongedSafeModeAlertAtUtc.Value > TimeSpan.FromMinutes(15)))
                        {
                            lastProlongedSafeModeAlertAtUtc = DateTimeOffset.UtcNow;
                            if (alertDispatcher is not null)
                            {
                                _ = await alertDispatcher.PublishAsync(new CryptoTrading.Observability.TradingAlert(
                                    runtime.ExecutionMode,
                                    "AUTONOMY",
                                    "autonomy/prolonged_safemode",
                                    DateTimeOffset.UtcNow,
                                    Guid.NewGuid().ToString("N"),
                                    CryptoTrading.Observability.TradingAlertSeverity.Warning,
                                    "ops",
                                    $"SafeMode has been active for {duration.TotalMinutes:F0} minutes without recovery.",
                                    "docs/runbooks/operational-control.md",
                                    $"PROLONGED_SAFE_MODE_{safeModeEnteredAtUtc:yyyyMMddHHmmss}_{(int)(duration.TotalMinutes / 15)}"), stoppingToken).ConfigureAwait(false);
                            }
                        }
                    }
                    else
                    {
                        safeModeEnteredAtUtc = null;
                        lastProlongedSafeModeAlertAtUtc = null;
                    }
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

                            if (alertDispatcher is not null)
                            {
                                _ = await alertDispatcher.PublishAsync(new CryptoTrading.Observability.TradingAlert(
                                    runtime.ExecutionMode,
                                    "AUTONOMY",
                                    "autonomy/operational_control_escalation",
                                    DateTimeOffset.UtcNow,
                                    Guid.NewGuid().ToString("N"),
                                    CryptoTrading.Observability.TradingAlertSeverity.Critical,
                                    "ops",
                                    $"Autonomy cycle triggered KillSwitch due to {classification.ReasonCode}",
                                    "docs/runbooks/operational-control.md",
                                    classification.ReasonCode), stoppingToken).ConfigureAwait(false);
                            }
                        }
                        else if (classification.Severity == FailureSeverity.Transient && ++consecutiveTransientFailures < MaxConsecutiveTransientFailures)
                        {
                            var baseDelayMs = Math.Min(1500 * Math.Pow(1.4, consecutiveTransientFailures - 1), 6000);
                            var jitterMs = System.Security.Cryptography.RandomNumberGenerator.GetInt32(50, 250);
                            var calculatedBackoff = TimeSpan.FromMilliseconds(baseDelayMs + jitterMs);
                            var backoff = classification.ReasonCode == "RATE_LIMIT_EXCEEDED" && classification.SuggestedBackoff.HasValue
                                ? classification.SuggestedBackoff.Value
                                : calculatedBackoff;
                            await Task.Delay(backoff, stoppingToken).ConfigureAwait(false);
                            continue;
                        }
                        else
                        {
                            _ = await operationalControl.EnterSafeModeAsync(
                                new OperationalControlCommand(
                                    "autonomy-cycle",
                                    $"A2 failure: {classification.ReasonCode} (severity={classification.Severity}, consecutive={consecutiveTransientFailures})",
                                    IdempotencyKey.New(),
                                    DateTimeOffset.UtcNow),
                                stoppingToken).ConfigureAwait(false);

                            safeModeEnteredAtUtc ??= DateTimeOffset.UtcNow;
                            if (alertDispatcher is not null)
                            {
                                _ = await alertDispatcher.PublishAsync(new CryptoTrading.Observability.TradingAlert(
                                    runtime.ExecutionMode,
                                    "AUTONOMY",
                                    "autonomy/operational_control_escalation",
                                    DateTimeOffset.UtcNow,
                                    Guid.NewGuid().ToString("N"),
                                    CryptoTrading.Observability.TradingAlertSeverity.Critical,
                                    "ops",
                                    $"Autonomy cycle entered SafeMode due to {classification.ReasonCode}",
                                    "docs/runbooks/operational-control.md",
                                    classification.ReasonCode), stoppingToken).ConfigureAwait(false);
                            }
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
        var symbols = runtime.ResolveAutonomySymbols();
        var subscriptions = new List<BinanceMarketSubscription>(symbols.Count * 4);
        foreach (var s in symbols)
        {
            subscriptions.Add(new BinanceMarketSubscription(s, BinanceMarketStreamKind.Kline, runtime.AutonomyInterval));
            subscriptions.Add(new BinanceMarketSubscription(s, BinanceMarketStreamKind.BookTicker));
            subscriptions.Add(new BinanceMarketSubscription(s, BinanceMarketStreamKind.Trade));
            subscriptions.Add(new BinanceMarketSubscription(s, BinanceMarketStreamKind.Depth));
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var marketEvent in marketDataStreamClient!.ReadAsync(subscriptions, stoppingToken).ConfigureAwait(false))
                {
                    if (marketEvent is BinanceKlineEvent kline)
                    {
                        var openTimeUtc = DateTimeOffset.FromUnixTimeMilliseconds(kline.OpenTime);
                        var closeTimeUtc = DateTimeOffset.FromUnixTimeMilliseconds(kline.CloseTime);
                        if (klineCache.TryGetValue(kline.Symbol, out var list))
                        {
                            lock (list)
                            {
                                var klineSnapshot = new BinanceSpotKline(
                                    openTimeUtc,
                                    closeTimeUtc,
                                    kline.Open,
                                    kline.High,
                                    kline.Low,
                                    kline.Close,
                                    kline.Volume,
                                    kline.Interval,
                                    kline.QuoteVolume,
                                    kline.NumberOfTrades,
                                    kline.TakerBuyBaseVolume,
                                    kline.TakerBuyQuoteVolume);

                                if (list.Count > 0 && list[^1].OpenTimeUtc == openTimeUtc)
                                {
                                    list[^1] = klineSnapshot;
                                }
                                else if (kline.IsClosed)
                                {
                                    list.Add(klineSnapshot);
                                    if (list.Count > runtime.AutonomyKlineLimit * 2)
                                    {
                                        list.RemoveAt(0);
                                    }
                                }
                            }
                        }

                        if (kline.IsClosed)
                        {
                            barCloseSignalChannel.Writer.TryWrite(kline.ReceivedAt);
                        }
                    }
                    else if (marketEvent is BinanceBookTickerStreamEvent tickerEvent)
                    {
                        var nextTicker = new BinanceBookTicker(
                            tickerEvent.Symbol,
                            tickerEvent.BestBidPrice,
                            tickerEvent.BestBidQuantity,
                            tickerEvent.BestAskPrice,
                            tickerEvent.BestAskQuantity,
                            tickerEvent.ReceivedAt);
                        if (streamBookTickers.TryGetValue(tickerEvent.Symbol, out var prevTicker))
                        {
                            previousBookTickers[tickerEvent.Symbol] = prevTicker;
                            var stepOfi = BinanceLocalOrderBook.ComputeTemporalOfi(
                                prevTicker.BidPrice,
                                prevTicker.BidQuantity,
                                prevTicker.AskPrice,
                                prevTicker.AskQuantity,
                                nextTicker.BidPrice,
                                nextTicker.BidQuantity,
                                nextTicker.AskPrice,
                                nextTicker.AskQuantity);
                            temporalOfiBySymbol.AddOrUpdate(
                                tickerEvent.Symbol,
                                stepOfi,
                                (_, prevOfi) => Math.Round(Math.Clamp((prevOfi * 0.70m) + (stepOfi * 0.30m), -1m, 1m), 4));
                        }

                        streamBookTickers[tickerEvent.Symbol] = nextTicker;

                        if (positionCostBases.TryGetValue(tickerEvent.Symbol, out var basis) &&
                            basis.CostNotional > 0m && basis.Quantity > 0m &&
                            tickerEvent.BestBidPrice > 0m)
                        {
                            var curValueNotional = basis.Quantity * tickerEvent.BestBidPrice;
                            var curProfitBps = (curValueNotional - basis.CostNotional) / basis.CostNotional * 10_000m;
                            var peakBps = positionLifetimePeakProfitBps.AddOrUpdate(tickerEvent.Symbol, curProfitBps, (_, peak) => Math.Max(peak, curProfitBps));

                            var isTakeProfitTrigger = curProfitBps >= 45.0m;
                            var isChandelierTrigger = peakBps >= 25.0m && (peakBps - curProfitBps) >= 12.0m && curProfitBps >= 15.0m;
                            var isStopLossTrigger = curProfitBps <= -18.0m;

                            if (isTakeProfitTrigger || isChandelierTrigger || isStopLossTrigger)
                            {
                                var nowUtc = tickerEvent.ReceivedAt;
                                if (!lastIntrabarTriggerUtc.TryGetValue(tickerEvent.Symbol, out var lastTrigger) ||
                                    nowUtc - lastTrigger >= TimeSpan.FromSeconds(2))
                                {
                                    lastIntrabarTriggerUtc[tickerEvent.Symbol] = nowUtc;
                                    barCloseSignalChannel.Writer.TryWrite(nowUtc);
                                }
                            }
                        }
                    }
                    else if (marketEvent is BinanceTradeEvent tradeEvent)
                    {
                        var signedSign = tradeEvent.IsBuyerMaker ? -1m : 1m;
                        tradeAggressionDeltaBySymbol.AddOrUpdate(
                            tradeEvent.Symbol,
                            signedSign * 0.25m,
                            (_, prevDelta) => Math.Round(Math.Clamp((prevDelta * 0.85m) + (signedSign * 0.15m), -1m, 1m), 4));
                    }
                    else if (marketEvent is BinanceAggTradeStreamEvent aggTradeEvent)
                    {
                        var signedSign = aggTradeEvent.IsBuyerMaker ? -1m : 1m;
                        tradeAggressionDeltaBySymbol.AddOrUpdate(
                            aggTradeEvent.Symbol,
                            signedSign * 0.25m,
                            (_, prevDelta) => Math.Round(Math.Clamp((prevDelta * 0.85m) + (signedSign * 0.15m), -1m, 1m), 4));
                    }
                    else if (marketEvent is BinanceDepthUpdateEvent depthEvent)
                    {
                        var book = localOrderBooks.GetOrAdd(depthEvent.Symbol, s => new BinanceLocalOrderBook(s));
                        lock (book)
                        {
                            if (book.Status == BinanceOrderBookStatus.Healthy)
                            {
                                _ = book.ApplyDelta(depthEvent);
                            }
                        }
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

        IReadOnlyList<BinanceSpotKline> klines;
        var cacheEligible = klineCache.TryGetValue(symbol, out var cachedList) &&
            cachedList.Count >= runtime.AutonomyKlineLimit &&
            (now - lastKlineFetchUtc.GetValueOrDefault(symbol, DateTimeOffset.MinValue)) <= TimeSpan.FromMinutes(10);

        if (cacheEligible)
        {
            lock (cachedList!)
            {
                if (cachedList.Count >= runtime.AutonomyKlineLimit &&
                    cachedList[^1].CloseTimeUtc <= now &&
                    (now - cachedList[^1].CloseTimeUtc) <= policyDocument.Policy.MaxMarketDataAge)
                {
                    klines = cachedList.ToArray();
                }
                else
                {
                    cacheEligible = false;
                    klines = [];
                }
            }
        }
        else
        {
            klines = [];
        }

        if (!cacheEligible)
        {
            klines = await marketClient.GetKlinesAsync(
                symbol,
                runtime.AutonomyInterval,
                limit: runtime.AutonomyKlineLimit,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var newList = new List<BinanceSpotKline>(klines);
            klineCache[symbol] = newList;
            lastKlineFetchUtc[symbol] = now;
        }
        List<ClosedBarEnvelope> closed;
        try
        {
            closed = ValidateAndMapKlines(
                klines,
                symbol,
                runtime.AutonomyInterval,
                policyDocument.Policy.MaxMarketDataAge,
                now,
                lastProcessedCloseUtc);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Stale", StringComparison.OrdinalIgnoreCase) ||
                                                    ex.Message.Contains("GapDetected", StringComparison.OrdinalIgnoreCase) ||
                                                    ex.Message.Contains("gap", StringComparison.OrdinalIgnoreCase))
        {
            AutonomyCycleLog.MarketDataRejected(logger, symbol, ex.Message);
            return;
        }
        if (closed.Count == 0)
        {
            return;
        }

        if (lastProcessedCloseUtc is { } previous && closed[^1].Bar.CloseTimeUtc <= previous)
        {
            var isIntrabarProtectiveTrigger = false;
            if (positionCostBases.TryGetValue(symbol, out var intrabarBasis) &&
                intrabarBasis.CostNotional > 0m &&
                intrabarBasis.Quantity > 0m &&
                streamBookTickers.TryGetValue(symbol, out var intrabarTicker) &&
                intrabarTicker.BidPrice > 0m &&
                intrabarTicker.ObservedAtUtc > closed[^1].Bar.CloseTimeUtc &&
                intrabarTicker.ObservedAtUtc <= now &&
                now - intrabarTicker.ObservedAtUtc <= policyDocument.Policy.MaxMarketDataAge)
            {
                var intrabarValueNotional = intrabarBasis.Quantity * intrabarTicker.BidPrice;
                var intrabarProfitBps = (intrabarValueNotional - intrabarBasis.CostNotional) / intrabarBasis.CostNotional * 10_000m;
                var intrabarPeakBps = positionLifetimePeakProfitBps.AddOrUpdate(
                    symbol,
                    intrabarProfitBps,
                    (_, peak) => Math.Max(peak, intrabarProfitBps));
                var isIntrabarTakeProfit = intrabarProfitBps >= 45.0m;
                var isIntrabarChandelier = intrabarPeakBps >= 25.0m &&
                                           (intrabarPeakBps - intrabarProfitBps) >= 12.0m &&
                                           intrabarProfitBps >= 15.0m;
                var isIntrabarStopLoss = intrabarProfitBps <= -18.0m;
                if (isIntrabarTakeProfit || isIntrabarChandelier || isIntrabarStopLoss)
                {
                    var intrabarCloseUtc = DateTimeOffset.FromUnixTimeMilliseconds(intrabarTicker.ObservedAtUtc.ToUnixTimeMilliseconds());
                    var lastTrigger = lastIntrabarTriggerUtc.GetValueOrDefault(symbol, DateTimeOffset.MinValue);
                    if ((now - lastTrigger) >= TimeSpan.FromSeconds(15))
                    {
                        var intrabarScope = $"{checkpointScope}:intrabar";
                        var lastIntrabarCloseUtc = await decisionCommitStore
                            .GetLastProcessedCloseAsync(intrabarScope, cancellationToken)
                            .ConfigureAwait(false);
                        if (lastIntrabarCloseUtc is null || intrabarCloseUtc > lastIntrabarCloseUtc.Value.AddMilliseconds(1))
                        {
                            lastIntrabarTriggerUtc[symbol] = now;
                            checkpointScope = intrabarScope;
                            var intrabarAvailableAtUtc = now >= closed[^1].AvailableAtUtc ? now : closed[^1].AvailableAtUtc;
                            if (intrabarCloseUtc <= closed[^1].Bar.CloseTimeUtc)
                            {
                                intrabarCloseUtc = closed[^1].Bar.CloseTimeUtc.AddMilliseconds(1);
                            }
                            closed.Add(new ClosedBarEnvelope(
                                closed.Count,
                                new CloseBar(symbolObj, intrabarCloseUtc, intrabarTicker.BidPrice),
                                intrabarAvailableAtUtc));
                            isIntrabarProtectiveTrigger = true;
                        }
                    }
                }
            }

            if (!isIntrabarProtectiveTrigger)
            {
                return;
            }
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
        var faucetEndowment = isTestnetFaucetWallet ? baseAsset.ToUpperInvariant() switch
        {
            "BTC" => 1.0m,
            "ETH" => 1.0m,
            "BNB" => 1.0m,
            "SOL" => 4.0m,
            "XRP" => 341.0m,
            "ADA" => 1937.0m,
            "DOGE" => 5501.0m,
            _ => 0m
        } : 0m;
        var baseQuantity = Math.Max(0m, rawBaseQuantity - faucetEndowment);
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

        decimal totalOtherPositionsNotional = 0m;
        decimal totalOtherUnrealizedPnlQuote = 0m;
        var allOtherPositionsHaveBasis = true;
        var unvaluedOtherPosition = false;
        foreach (var otherSymbol in runtime.ResolveAutonomySymbols())
        {
            if (string.Equals(otherSymbol, symbol, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var otherBase = otherSymbol.EndsWith(quoteAsset.Value, StringComparison.OrdinalIgnoreCase) ? otherSymbol[..^quoteAsset.Value.Length] : otherSymbol;
            var otherBal = account.Balances.FirstOrDefault(b => string.Equals(b.Asset, otherBase, StringComparison.OrdinalIgnoreCase));
            if (otherBal is null)
            {
                continue;
            }

            var otherRawQty = ParseBalance(otherBal.Free) + ParseBalance(otherBal.Locked);
            var otherFaucet = isTestnetFaucetWallet ? otherBase.ToUpperInvariant() switch
            {
                "BTC" => 1.0m,
                "ETH" => 1.0m,
                "BNB" => 1.0m,
                "SOL" => 4.0m,
                "XRP" => 341.0m,
                "ADA" => 1937.0m,
                "DOGE" => 5501.0m,
                _ => 0m
            } : 0m;
            var otherNetQty = Math.Max(0m, otherRawQty - otherFaucet);
            if (otherNetQty > 0m)
            {
                decimal? otherMid = null;
                if (streamBookTickers.TryGetValue(otherSymbol, out var otherTicker) &&
                    DateTimeOffset.UtcNow - otherTicker.ObservedAtUtc <= policyDocument.Policy.MaxMarketDataAge &&
                    otherTicker.BidPrice > 0m &&
                    otherTicker.AskPrice >= otherTicker.BidPrice)
                {
                    otherMid = (otherTicker.BidPrice + otherTicker.AskPrice) / 2m;
                }
                else
                {
                    try
                    {
                        var restTicker = await marketClient.GetBookTickerAsync(otherSymbol, cancellationToken).ConfigureAwait(false);
                        if (restTicker.BidPrice > 0m && restTicker.AskPrice >= restTicker.BidPrice)
                        {
                            otherMid = (restTicker.BidPrice + restTicker.AskPrice) / 2m;
                        }
                    }
                    catch
                    {
                        // Handled below via unvaluedOtherPosition fail-closed guard
                    }
                }

                if (otherMid is { } validOtherMid)
                {
                    var otherMarketValue = otherNetQty * validOtherMid;
                    totalOtherPositionsNotional += otherMarketValue;
                    if (positionCostBases.TryGetValue(otherSymbol, out var otherBasis) &&
                        otherBasis.CostNotional > 0m &&
                        Math.Abs(otherBasis.Quantity - otherNetQty) <= 0.00000001m)
                    {
                        totalOtherUnrealizedPnlQuote += otherMarketValue - otherBasis.CostNotional;
                    }
                    else
                    {
                        allOtherPositionsHaveBasis = false;
                    }
                }
                else
                {
                    unvaluedOtherPosition = true;
                }
            }
        }

        decimal? currentEquityNotional = (unsupportedAssetBalance || unvaluedOtherPosition)
            ? null
            : totalQuoteBalance + (baseQuantity * latest.ClosePrice.Value) + totalOtherPositionsNotional;
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
        BinanceBookTicker bookTicker;
        if (streamBookTickers.TryGetValue(symbol, out var cachedTicker) &&
            cachedTicker.ObservedAtUtc <= DateTimeOffset.UtcNow &&
            DateTimeOffset.UtcNow - cachedTicker.ObservedAtUtc <= policyDocument.Policy.MaxMarketDataAge)
        {
            bookTicker = cachedTicker;
        }
        else
        {
            bookTicker = await marketClient.GetBookTickerAsync(
                symbol,
                cancellationToken).ConfigureAwait(false);
        }
        if (navSnapshots is not null && !unsupportedAssetBalance && !unvaluedOtherPosition &&
            bookTicker.ObservedAtUtc <= DateTimeOffset.UtcNow &&
            DateTimeOffset.UtcNow - bookTicker.ObservedAtUtc <= policyDocument.Policy.MaxMarketDataAge)
        {
            var valuationPrice = (bookTicker.BidPrice + bookTicker.AskPrice) / 2m;
            var consolidatedNavQuote = totalQuoteBalance + (baseQuantity * valuationPrice) + totalOtherPositionsNotional;
            decimal? symbolUnrealized = baseQuantity == 0m ? 0m :
                accounting.HasPositionBasis && Math.Abs(accounting.PositionQuantity - baseQuantity) <= 0.00000001m
                    ? baseQuantity * valuationPrice - accounting.PositionCostNotional : null;
            decimal? consolidatedUnrealized = symbolUnrealized.HasValue && allOtherPositionsHaveBasis
                ? symbolUnrealized.Value + totalOtherUnrealizedPnlQuote
                : null;
            try
            {
                await navSnapshots.RecordNavSnapshotAsync(new PortfolioNavSnapshot(
                    symbol, quoteAsset.Value, bookTicker.ObservedAtUtc,
                    consolidatedNavQuote, consolidatedUnrealized,
                    null, "account-snapshot+observed-book-mid"), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Valuation telemetry must never stop trading or push the system into SafeMode.
                AutonomyCycleLog.TelemetryWriteFailed(logger, "nav_snapshot", ex.GetType().Name);
            }
            AutonomyCycleLog.Valuation(logger, symbol, bookTicker.ObservedAtUtc,
                consolidatedNavQuote, consolidatedUnrealized);
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
        decimal? depthImbalanceTop5 = null;
        var localBook = localOrderBooks.GetOrAdd(symbol, s => new BinanceLocalOrderBook(s));
        bool needsBookSnapshot;
        lock (localBook)
        {
            needsBookSnapshot = localBook.Status != BinanceOrderBookStatus.Healthy;
        }

        if (needsBookSnapshot)
        {
            try
            {
                var snapshot = await marketClient.GetOrderBookSnapshotAsync(symbol, 20, cancellationToken).ConfigureAwait(false);
                if (snapshot.Bids.Count > 0 && snapshot.Asks.Count > 0)
                {
                    lock (localBook)
                    {
                        localBook.ApplySnapshot(snapshot);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort L2 depth snapshot; L1 bookTicker remains the fail-closed authority.
            }
        }

        lock (localBook)
        {
            if (localBook.Status == BinanceOrderBookStatus.Healthy)
            {
                depthImbalanceTop5 = localBook.CalculateTopDepthImbalance(5);
            }
        }
        var temporalOfi = temporalOfiBySymbol.TryGetValue(symbol, out var tOfi) ? (decimal?)tOfi : null;
        var tradeAggressionDelta = tradeAggressionDeltaBySymbol.TryGetValue(symbol, out var tAgg) ? (decimal?)tAgg : null;
        var executionMarket = new ExecutionMarketSnapshot(
            mappedExecutionMarket.BestBid,
            mappedExecutionMarket.BestAsk,
            mappedExecutionMarket.AvailableLiquidityNotional,
            mappedExecutionMarket.ObservedAtUtc,
            isComplete: mappedExecutionMarket.IsComplete && hasCompleteTradingRules,
            quantityStepSize: mappedExecutionMarket.QuantityStepSize,
            priceTickSize: mappedExecutionMarket.PriceTickSize,
            bidQuantity: mappedExecutionMarket.BidQuantity,
            askQuantity: mappedExecutionMarket.AskQuantity,
            temporalOrderFlowImbalance: temporalOfi,
            tradeAggressionDelta: tradeAggressionDelta,
            depthImbalanceTop5: depthImbalanceTop5);
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

        var hasOpenExposure = portfolio.GrossExposureNotional > 5m;
        var isUnauthorizedEntrySymbol = !hasOpenExposure &&
                                        policyDocument.Policy.BudgetPolicy is { } bp &&
                                        !bp.IsSymbolAllowed(symbol);

        decimal? lifetimePeakBps = null;
        string? originatingStrat = null;
        if (hasOpenExposure && accounting.HasPositionBasis && accounting.PositionCostNotional > 0m)
        {
            positionCostBases[symbol] = (accounting.PositionCostNotional, baseQuantity);
            var curProfitBps = (portfolio.GrossExposureNotional - accounting.PositionCostNotional) / accounting.PositionCostNotional * 10_000m;

            // Reconstruct historical lifetime peak from closed bars if unrecorded (e.g. after daemon restart)
            var avgEntryPrice = baseQuantity > 0m ? accounting.PositionCostNotional / baseQuantity : 0m;
            var highestRecentPrice = avgEntryPrice > 0m && closed.Count > 0 ? closed.Max(c => c.Bar.ClosePrice.Value) : 0m;
            var reconstructedPeakBps = avgEntryPrice > 0m && highestRecentPrice > avgEntryPrice
                ? (highestRecentPrice - avgEntryPrice) / avgEntryPrice * 10_000m
                : curProfitBps;

            lifetimePeakBps = positionLifetimePeakProfitBps.AddOrUpdate(
                symbol,
                Math.Max(curProfitBps, reconstructedPeakBps),
                (_, peak) => Math.Max(peak, Math.Max(curProfitBps, reconstructedPeakBps)));

            if (!positionOriginatingStrategy.TryGetValue(symbol, out var strat) || string.IsNullOrWhiteSpace(strat))
            {
#pragma warning disable CA1308 // Strategy identifiers use lowercase slugs by design
                strat = $"trend-following-{symbol.ToLowerInvariant()}";
#pragma warning restore CA1308
                positionOriginatingStrategy[symbol] = strat;
            }
            originatingStrat = strat;
        }
        else if (!hasOpenExposure)
        {
            if (positionCostBases.TryRemove(symbol, out _))
            {
                lastClosedTradeAtBySymbol[symbol] = DateTimeOffset.UtcNow;
            }
            positionLifetimePeakProfitBps.TryRemove(symbol, out _);
            positionOriginatingStrategy.TryRemove(symbol, out _);
        }

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
            decimal? leaderReturnBps = null;
            decimal? leaderOrderBookImbalance = null;
            if (!string.Equals(symbol, "BTCUSDT", StringComparison.OrdinalIgnoreCase))
            {
                if (klineCache.TryGetValue("BTCUSDT", out var btcKlines))
                {
                    lock (btcKlines)
                    {
                        if (btcKlines.Count >= 2 && btcKlines[^2].Close > 0m)
                        {
                            leaderReturnBps = Math.Round((btcKlines[^1].Close - btcKlines[^2].Close) / btcKlines[^2].Close * 10_000m, 4);
                        }
                    }
                }

                if (streamBookTickers.TryGetValue("BTCUSDT", out var btcTicker) &&
                    (btcTicker.BidQuantity + btcTicker.AskQuantity) > 0m)
                {
                    leaderOrderBookImbalance = Math.Round(
                        (btcTicker.BidQuantity - btcTicker.AskQuantity) / (btcTicker.BidQuantity + btcTicker.AskQuantity),
                        4);
                }
            }

            decimal? maxOpenPositionCorrelation = null;
            if (!hasOpenExposure)
            {
                foreach (var kvp in positionCostBases)
                {
                    if (string.Equals(kvp.Key, symbol, StringComparison.OrdinalIgnoreCase) || kvp.Value.Quantity <= 0m)
                    {
                        continue;
                    }

                    if (klineCache.TryGetValue(kvp.Key, out var otherKlinesList))
                    {
                        CloseBar[] otherBars;
                        lock (otherKlinesList)
                        {
                            otherBars = otherKlinesList
                                .Where(static k => k.Close > 0m)
                                .Select(k => new CloseBar(new Symbol(kvp.Key), k.CloseTimeUtc, k.Close, k.Open, k.High, k.Low, k.Volume, k.TakerBuyBaseVolume, k.QuoteVolume, k.NumberOfTrades))
                                .ToArray();
                        }

                        if (otherBars.Length >= 5 && bars.Length >= 5)
                        {
                            var corr = CrossSectionalPortfolioAllocator.ComputePairwiseReturnCorrelation(bars, otherBars, 20);
                            maxOpenPositionCorrelation = maxOpenPositionCorrelation.HasValue
                                ? Math.Max(maxOpenPositionCorrelation.Value, corr)
                                : corr;
                        }
                    }
                }
            }

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
                PendingExitReplanStrategyId: hasPendingExitReplan ? $"trend-following-{symbol.ToLowerInvariant()}" : null,
#pragma warning restore CA1308
                PositionOriginatingStrategyId: originatingStrat,
                LifetimePeakProfitBps: lifetimePeakBps,
                LeaderReturnBps: leaderReturnBps,
                LeaderOrderBookImbalance: leaderOrderBookImbalance,
                LastClosedTradeAtUtc: lastClosedTradeAtBySymbol.TryGetValue(symbol, out var closedAt) ? closedAt : null,
                MaxOpenPositionCorrelation: maxOpenPositionCorrelation);

            if (durablePerformances is not null)
            {
                adaptiveCycle.LoadStrategyRegimePerformances(await durablePerformances.GetPerformancesAsync(cancellationToken).ConfigureAwait(false));
            }
            var adaptiveResult = adaptiveCycle.Evaluate(adaptiveRequest);

            if (regimeStore is not null)
            {
                try
                {
                    await regimeStore.RecordMarketRegimeAsync(adaptiveResult.Regime, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AutonomyCycleLog.TelemetryWriteFailed(logger, "market_regime", ex.GetType().Name);
                }
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
                try
                {
                    await driftStore.RecordDriftObservationAsync(driftObs, driftEval, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AutonomyCycleLog.TelemetryWriteFailed(logger, "drift_observation", ex.GetType().Name);
                }
            }

            var adaptiveOutbox = hasCompleteTradingRules && !isUnauthorizedEntrySymbol && adaptiveResult.Execution is { IsDispatchable: true } exec
                ? exec.OutboxRecord
                : null;
            var adaptiveOutcome = !hasCompleteTradingRules || isUnauthorizedEntrySymbol || adaptiveResult.IsNoTrade
                ? "NO_TRADE"
                : adaptiveOutbox is not null ? "DISPATCHABLE" : "BLOCKED";
            var adaptiveReason = isUnauthorizedEntrySymbol
                ? $"SYMBOL_NOT_AUTHORIZED_IN_BUDGET_POLICY:{symbol}"
                : !hasCompleteTradingRules
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

            if (adaptiveOutbox.Command.Side == CryptoTrading.Domain.Orders.OrderSide.Buy)
            {
#pragma warning disable CA1308 // Strategy identifiers use lowercase slugs by design
                positionOriginatingStrategy[symbol] = adaptiveResult.SelectedStrategyId ?? $"trend-following-{symbol.ToLowerInvariant()}";
#pragma warning restore CA1308
                positionLifetimePeakProfitBps[symbol] = 0m;
            }

            AutonomyCycleLog.Enqueued(logger, latest.CloseTimeUtc, adaptiveOutbox.Command.ClientOrderId.Value, adaptiveOutbox.Command.Side.ToString());
            outboxWakeupSignal?.Notify();
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
        var outbox = hasCompleteTradingRules && !isUnauthorizedEntrySymbol ? GetDispatchableOutbox(result) : null;
        var outcome = !hasCompleteTradingRules || isUnauthorizedEntrySymbol || result.IsNoTrade
            ? "NO_TRADE"
            : outbox is not null ? "DISPATCHABLE" : "BLOCKED";
        var reason = isUnauthorizedEntrySymbol
            ? $"SYMBOL_NOT_AUTHORIZED_IN_BUDGET_POLICY:{symbol}"
            : !hasCompleteTradingRules
                ? "BINANCE_TRADING_RULES_UNAVAILABLE_OR_INCOMPLETE"
                : result.NoTradeReason ?? string.Join(',', result.Execution?.Decision.ReasonCodes ?? []);
        await decisionCommitStore.CommitAsync(
            CreateDecisionCommit(runtime, checkpointScope, featureSnapshot, result, outcome, reason, outbox, replanCandidate),
            cancellationToken).ConfigureAwait(false);
        lastProcessedCloseUtc = latest.CloseTimeUtc;
        if (!hasCompleteTradingRules || isUnauthorizedEntrySymbol || result.IsNoTrade)
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
        outboxWakeupSignal?.Notify();
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
            CodeVersion = CryptoTrading.Domain.Runtime.RuntimeCodeVersion.Current,
            StrategyVersion = decision.StrategyVersion,
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
                    decision.Kline.Close,
                    decision.Kline.Open,
                    decision.Kline.High,
                    decision.Kline.Low,
                    decision.Kline.Volume,
                    kline.TakerBuyBaseVolume,
                    kline.QuoteVolume,
                    kline.NumberOfTrades),
                nowUtc));
        }

        if (previousCloseUtc is { } previous && accepted.Count > 0)
        {
            var expectedNext = AddInterval(previous, interval);
            if (previous >= accepted[0].Bar.CloseTimeUtc || expectedNext >= accepted[0].Bar.CloseTimeUtc)
            {
                var firstAfterCheckpoint = accepted.FirstOrDefault(bar => bar.Bar.CloseTimeUtc > previous);
                if (firstAfterCheckpoint is not null && firstAfterCheckpoint.Bar.CloseTimeUtc > expectedNext)
                {
                    throw new InvalidOperationException("Binance REST kline snapshot has a gap after the durable autonomy checkpoint.");
                }
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

    [LoggerMessage(LogLevel.Warning, "Autonomy cycle market data for {Symbol} rejected by quality gate: {Reason}. Skipping cycle for symbol.")]
    public static partial void MarketDataRejected(ILogger logger, string symbol, string reason);

    [LoggerMessage(LogLevel.Warning, "Non-critical telemetry write failed and was isolated. Kind={Kind} ErrorType={ErrorType}")]
    public static partial void TelemetryWriteFailed(ILogger logger, string kind, string errorType);

    [LoggerMessage(LogLevel.Error, "Autonomy cycle could not enter SafeMode. ErrorType={ErrorType}")]
    public static partial void ControlFailure(ILogger logger, string errorType);
}
