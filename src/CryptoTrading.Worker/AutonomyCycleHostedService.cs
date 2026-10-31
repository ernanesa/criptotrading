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
using CryptoTrading.Observability;
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
    IOutboxWakeupSignal? outboxWakeupSignal = null,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider valuationTimeProvider = timeProvider ?? TimeProvider.System;
    private static readonly string[] QuoteAssets = ["USDT", "USDC", "FDUSD", "BUSD", "TUSD", "BTC", "ETH", "BNB", "EUR", "TRY"];
    private static readonly string[] CanaryBasketSymbols = ["SUIUSDT", "ARBUSDT", "TIAUSDT", "AVAXUSDT", "SOLUSDT"];
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
    private readonly ConcurrentDictionary<string, TradeAggressionState> tradeAggressionBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BinanceLocalOrderBook> localOrderBooks = new(StringComparer.OrdinalIgnoreCase);
    private long marketStreamEpoch;
    private readonly ConcurrentDictionary<string, List<BinanceSpotKline>> klineCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastKlineFetchUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> positionOriginatingStrategy = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, decimal> positionLifetimePeakProfitBps = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (decimal CostNotional, decimal Quantity)> positionCostBases = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastIntrabarWakeupUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastClosedTradeAtBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DynamicExitThresholds> dynamicExitThresholdsBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastProcessedCloseByScope = new(StringComparer.Ordinal);
    private sealed record CachedRiskPolicy(RiskPolicyDocument Document, DateTimeOffset CachedAtUtc);
    private CachedRiskPolicy? cachedActiveRiskPolicy;
    private DateTimeOffset? safeModeEnteredAtUtc;
    private DateTimeOffset? lastProlongedSafeModeAlertAtUtc;
    internal ConcurrentDictionary<string, BinanceBookTicker> StreamBookTickers => streamBookTickers;
    internal ConcurrentDictionary<string, BinanceLocalOrderBook> LocalOrderBooks => localOrderBooks;
    internal ConcurrentDictionary<string, List<BinanceSpotKline>> KlineCache => klineCache;
    internal ConcurrentDictionary<string, DynamicExitThresholds> DynamicExitThresholdsBySymbol => dynamicExitThresholdsBySymbol;

    internal decimal? ReadTradeAggression(string symbol, DateTimeOffset now, TimeSpan maxAge)
    {
        if (maxAge <= TimeSpan.Zero || !tradeAggressionBySymbol.TryGetValue(symbol, out var state) || state.TotalQuantity <= 0m ||
            now < state.SourceAtUtc || now < state.ReceivedAtUtc ||
            now - state.SourceAtUtc > maxAge || now - state.ReceivedAtUtc > maxAge)
            return null;
        return Math.Clamp(state.SignedQuantity / state.TotalQuantity, -1m, 1m);
    }

    private void ObserveTradeAggression(BinanceTradeEvent trade)
    {
        var now = valuationTimeProvider.GetUtcNow();
        if (trade.Quantity <= 0m || trade.Price <= 0m || trade.TradeId < 0 ||
            trade.EventTime <= 0 || trade.EventTime > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() ||
            trade.ReceivedAt.Offset != TimeSpan.Zero || trade.ReceivedAt > now)
            return;
        var sourceAt = DateTimeOffset.FromUnixTimeMilliseconds(trade.EventTime);
        if (sourceAt > now || sourceAt > trade.ReceivedAt) return;
        tradeAggressionBySymbol.AddOrUpdate(trade.Symbol,
            static (_, input) => TradeAggressionState.Initial(input.Trade, input.SourceAt),
            static (_, previous, input) => previous.Add(input.Trade, input.SourceAt),
            (Trade: trade, SourceAt: sourceAt));
    }

    private readonly record struct TradeAggressionState(
        decimal SignedQuantity, decimal TotalQuantity, DateTimeOffset SourceAtUtc, DateTimeOffset ReceivedAtUtc, long LastTradeId)
    {
        public static TradeAggressionState Initial(BinanceTradeEvent trade, DateTimeOffset sourceAt) =>
            new(trade.IsBuyerMaker ? -trade.Quantity : trade.Quantity, trade.Quantity, sourceAt, trade.ReceivedAt, trade.TradeId);

        public TradeAggressionState Add(BinanceTradeEvent trade, DateTimeOffset sourceAt)
        {
            if (trade.TradeId <= LastTradeId || sourceAt < SourceAtUtc || trade.ReceivedAt < ReceivedAtUtc) return this;
            if (TotalQuantity <= 0m) return Initial(trade, sourceAt);
            // Analytical five-second half-life, not a risk limit or calibrated alpha.
            // Only elapsed time is converted; price and quantity stay decimal.
            var decay = (decimal)Math.Pow(0.5d, (sourceAt - SourceAtUtc).Ticks / (5d * TimeSpan.TicksPerSecond));
            var decayedTotal = TotalQuantity * decay;
            if (decimal.MaxValue - decayedTotal < trade.Quantity)
                return new(0m, 0m, sourceAt, trade.ReceivedAt, trade.TradeId);
            return new(SignedQuantity * decay + (trade.IsBuyerMaker ? -trade.Quantity : trade.Quantity),
                decayedTotal + trade.Quantity, sourceAt, trade.ReceivedAt, trade.TradeId);
        }
    }

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
                        var batchNow = DateTimeOffset.UtcNow;
                        var batchPolicySnapshot = Volatile.Read(ref cachedActiveRiskPolicy);
                        if (batchPolicySnapshot is null || batchNow < batchPolicySnapshot.CachedAtUtc || (batchNow - batchPolicySnapshot.CachedAtUtc) >= TimeSpan.FromSeconds(5))
                        {
                            var activeBatchPolicy = await riskPolicyStore.GetActiveRiskPolicyAsync(batchNow, stoppingToken).ConfigureAwait(false);
                            if (activeBatchPolicy is not null)
                            {
                                Volatile.Write(ref cachedActiveRiskPolicy, new CachedRiskPolicy(activeBatchPolicy, batchNow));
                            }
                        }

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
                    AutonomyCycleLog.Failed(
                        logger,
                        exception.GetType().Name,
                        classification.ReasonCode,
                        ComputeExceptionFingerprint(exception),
                        "autonomy-cycle");
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

    internal async Task ConsumeMarketDataStreamAsync(WorkerRuntimeOptions runtime, CancellationToken stoppingToken)
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
                    if (marketEvent is BinanceMarketStreamDisconnectedEvent)
                    {
                        InvalidateMarketStreamState();
                    }
                    else if (marketEvent is BinanceKlineEvent kline)
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

                            var thresholds = dynamicExitThresholdsBySymbol.GetValueOrDefault(tickerEvent.Symbol, DynamicExitThresholds.Default);
                            var isTakeProfitTrigger = curProfitBps >= thresholds.TargetProfitBps;
                            var isChandelierTrigger = peakBps >= thresholds.ChandelierActivationBps &&
                                                      (peakBps - curProfitBps) >= thresholds.ChandelierPullbackBps &&
                                                      curProfitBps >= thresholds.MinProfitLockBps;
                            var isStopLossTrigger = curProfitBps <= -thresholds.StopLossBps;

                            if (isTakeProfitTrigger || isChandelierTrigger || isStopLossTrigger)
                            {
                                var nowUtc = tickerEvent.ReceivedAt;
                                if (!lastIntrabarWakeupUtc.TryGetValue(tickerEvent.Symbol, out var lastTrigger) ||
                                    nowUtc - lastTrigger >= TimeSpan.FromSeconds(2))
                                {
                                    lastIntrabarWakeupUtc[tickerEvent.Symbol] = nowUtc;
                                    barCloseSignalChannel.Writer.TryWrite(nowUtc);
                                }
                            }
                        }
                    }
                    else if (marketEvent is BinanceTradeEvent tradeEvent)
                    {
                        // This service subscribes to trade only. Mixing aggTrade
                        // IDs with trade IDs would count the same executions twice.
                        ObserveTradeAggression(tradeEvent);
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

                if (!stoppingToken.IsCancellationRequested)
                {
                    InvalidateMarketStreamState();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                InvalidateMarketStreamState();
                AutonomyCycleLog.StreamDisconnected(logger, ex);
                await Task.Delay(TimeSpan.FromSeconds(2), valuationTimeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private void InvalidateMarketStreamState()
    {
        Interlocked.Increment(ref marketStreamEpoch);
        streamBookTickers.Clear();
        previousBookTickers.Clear();
        temporalOfiBySymbol.Clear();
        tradeAggressionBySymbol.Clear();
        klineCache.Clear();
        foreach (var book in localOrderBooks.Values)
        {
            lock (book)
            {
                book.InvalidateForStreamDisconnect();
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
        var now = valuationTimeProvider.GetUtcNow();
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
        DateTimeOffset? cachedLatestBarCloseUtc = null;
        if (klineCache.TryGetValue(symbol, out var existingCacheList))
        {
            lock (existingCacheList)
            {
                for (var idx = existingCacheList.Count - 1; idx >= 0; idx--)
                {
                    if (existingCacheList[idx].CloseTimeUtc <= now)
                    {
                        cachedLatestBarCloseUtc = existingCacheList[idx].CloseTimeUtc;
                        break;
                    }
                }
            }
        }

        var canUseCachedCheckpoint = lastProcessedCloseByScope.TryGetValue(checkpointScope, out var cachedCheckpointCloseUtc) &&
            cachedLatestBarCloseUtc is { } latestBarInCache &&
            latestBarInCache <= cachedCheckpointCloseUtc;
        var checkpointTask = canUseCachedCheckpoint
            ? Task.FromResult<DateTimeOffset?>(cachedCheckpointCloseUtc)
            : decisionCommitStore.GetLastProcessedCloseAsync(checkpointScope, cancellationToken);

        var cachedPolicySnapshot = Volatile.Read(ref cachedActiveRiskPolicy);
        var canUseCachedPolicy = cachedPolicySnapshot is { } cp &&
            now >= cp.CachedAtUtc &&
            (now - cp.CachedAtUtc) < TimeSpan.FromSeconds(5);
        var policyTask = canUseCachedPolicy
            ? Task.FromResult<RiskPolicyDocument?>(cachedPolicySnapshot!.Document)
            : riskPolicyStore.GetActiveRiskPolicyAsync(now, cancellationToken);
        await Task.WhenAll(checkpointTask, policyTask).ConfigureAwait(false);

        var lastProcessedCloseUtc = await checkpointTask.ConfigureAwait(false);
        if (!canUseCachedCheckpoint && lastProcessedCloseUtc is { } dbCheckpointCloseUtc)
        {
            lastProcessedCloseByScope.AddOrUpdate(
                checkpointScope,
                dbCheckpointCloseUtc,
                (_, prev) => dbCheckpointCloseUtc > prev ? dbCheckpointCloseUtc : prev);
        }

        var policyDocument = await policyTask.ConfigureAwait(false)
            ?? throw new InvalidOperationException("No active RiskPolicy is available for the autonomy cycle.");
        if (!canUseCachedPolicy)
        {
            Volatile.Write(ref cachedActiveRiskPolicy, new CachedRiskPolicy(policyDocument, now));
        }
        if (policyDocument.Policy.WithdrawalsEnabled)
        {
            throw new InvalidOperationException("Autonomy cycle refuses a RiskPolicy with withdrawals enabled.");
        }

        IReadOnlyList<BinanceSpotKline> klines;
        var intervalDuration = ParseIntervalDuration(runtime.AutonomyInterval);
        var effectiveKlineAge = intervalDuration + policyDocument.Policy.MaxMarketDataAge;
        var cacheEligible = klineCache.TryGetValue(symbol, out var cachedList) &&
            cachedList.Count >= runtime.AutonomyKlineLimit &&
            (now - lastKlineFetchUtc.GetValueOrDefault(symbol, DateTimeOffset.MinValue)) <= effectiveKlineAge;

        if (cacheEligible)
        {
            lock (cachedList!)
            {
                if (cachedList.Count >= runtime.AutonomyKlineLimit &&
                    cachedList[^1].CloseTimeUtc <= now &&
                    (now - cachedList[^1].CloseTimeUtc) <= effectiveKlineAge)
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
        catch (InvalidOperationException ex)
        {
            AutonomyCycleLog.MarketDataRejected(logger, symbol, ex.Message);
            return;
        }
        if (closed.Count == 0)
        {
            return;
        }

        if (closed.Count >= 2 && !dynamicExitThresholdsBySymbol.ContainsKey(symbol))
        {
            var rawBars = closed.Select(static envelope => envelope.Bar).ToArray();
            var preliminaryIndicators = TechnicalIndicatorCalculator.Evaluate(rawBars);
            if (preliminaryIndicators.AtrBps > 0m)
            {
                dynamicExitThresholdsBySymbol.TryAdd(symbol, DynamicExitThresholds.Calculate(preliminaryIndicators.AtrBps));
            }
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
                var thresholds = dynamicExitThresholdsBySymbol.GetValueOrDefault(symbol, DynamicExitThresholds.Default);
                var isIntrabarTakeProfit = intrabarProfitBps >= thresholds.TargetProfitBps;
                var isIntrabarChandelier = intrabarPeakBps >= thresholds.ChandelierActivationBps &&
                                           (intrabarPeakBps - intrabarProfitBps) >= thresholds.ChandelierPullbackBps &&
                                           intrabarProfitBps >= thresholds.MinProfitLockBps;
                var isIntrabarStopLoss = intrabarProfitBps <= -thresholds.StopLossBps;
                if (isIntrabarTakeProfit || isIntrabarChandelier || isIntrabarStopLoss)
                {
                    var intrabarCloseUtc = DateTimeOffset.FromUnixTimeMilliseconds(intrabarTicker.ObservedAtUtc.ToUnixTimeMilliseconds());
                    // Wakeups are not completed decisions: frequent quotes must not
                    // postpone protection. Only a durable decision starts cooldown.
                    var intrabarScope = $"{checkpointScope}:intrabar";
                    var lastIntrabarCloseUtc = await decisionCommitStore
                        .GetLastProcessedCloseAsync(intrabarScope, cancellationToken)
                        .ConfigureAwait(false);
                    if (lastIntrabarCloseUtc is null ||
                        intrabarCloseUtc - lastIntrabarCloseUtc.Value >= TimeSpan.FromSeconds(15))
                    {
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
                TradingTelemetry.RecordAutonomyDecision("BLOCKED", blockReason);
                AutonomyCycleLog.Blocked(logger, "n/a", symbol, closed[^1].Bar.CloseTimeUtc, blockReason);
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
                lastProcessedCloseByScope[checkpointScope] = closed[^1].Bar.CloseTimeUtc;
                return;
            }
        }

        var replanTask = cancellationStore is not null
            ? cancellationStore.ReadReplanCandidatesAsync(100, cancellationToken)
            : Task.FromResult<IReadOnlyList<ExecutionReplanCandidate>>([]);
        var accountTask = accountClient.GetAccountAsync(cancellationToken);
        var commissionTask = accountClient.GetCommissionAsync(symbol, cancellationToken);

        await Task.WhenAll(replanTask, accountTask, commissionTask).ConfigureAwait(false);

        var candidates = await replanTask.ConfigureAwait(false);
        ExecutionReplanCandidate? replanCandidate = candidates.LastOrDefault(c =>
            string.Equals(c.Symbol.Value, symbol, StringComparison.OrdinalIgnoreCase));

        var account = await accountTask.ConfigureAwait(false);
        if (!account.CanTrade)
        {
            throw new InvalidOperationException("Binance account does not currently allow trading.");
        }

        var commission = await commissionTask.ConfigureAwait(false);
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
        var baseQuantity = ParseBalance(baseBalance?.Free) + ParseBalance(baseBalance?.Locked);
        var quoteAsset = new Asset("USDT");
        var quote = account.Balances.FirstOrDefault(balance => string.Equals(balance.Asset, quoteAsset.Value, StringComparison.OrdinalIgnoreCase));
        var availableQuoteLiquidity = ParseBalance(quote?.Free);
        var totalQuoteBalance = availableQuoteLiquidity + ParseBalance(quote?.Locked);
        var latest = closed[^1].Bar;
        var configuredUniverseAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            quoteAsset.Value
        };
        foreach (var s in runtime.ResolveAutonomySymbols())
        {
            var b = s.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s;
            configuredUniverseAssets.Add(b);
        }

        var unsupportedAssetBalance = account.Balances
            .Where(balance => !configuredUniverseAssets.Contains(balance.Asset))
            .Any(balance => ParseBalance(balance.Free) + ParseBalance(balance.Locked) > 0m);

        decimal totalOtherPositionsNotional = 0m;
        decimal totalOtherUnrealizedPnlQuote = 0m;
        var allOtherPositionsHaveBasis = true;
        var unvaluedOtherPosition = false;
        var usedOtherTickers = new List<(string Symbol, BinanceBookTicker Ticker)>();
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

            var otherNetQty = ParseBalance(otherBal.Free) + ParseBalance(otherBal.Locked);
            if (otherNetQty > 0m)
            {
                decimal? otherMid = null;
                if (streamBookTickers.TryGetValue(otherSymbol, out var otherTicker) &&
                    PortfolioTickerValidation.IsValid(otherTicker, otherSymbol, valuationTimeProvider.GetUtcNow(), policyDocument.Policy.MaxMarketDataAge))
                {
                    otherMid = (otherTicker.BidPrice + otherTicker.AskPrice) / 2m;
                    usedOtherTickers.Add((otherSymbol, otherTicker));
                }
                else
                {
                    try
                    {
                        var restTicker = await marketClient.GetBookTickerAsync(otherSymbol, cancellationToken).ConfigureAwait(false);
                        if (PortfolioTickerValidation.IsValid(restTicker, otherSymbol, valuationTimeProvider.GetUtcNow(), policyDocument.Policy.MaxMarketDataAge))
                        {
                            otherMid = (restTicker.BidPrice + restTicker.AskPrice) / 2m;
                            usedOtherTickers.Add((otherSymbol, restTicker));
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
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

        var riskValuationAt = valuationTimeProvider.GetUtcNow();
        unvaluedOtherPosition |= usedOtherTickers.Any(item =>
            !PortfolioTickerValidation.IsValid(item.Ticker, item.Symbol, riskValuationAt, policyDocument.Policy.MaxMarketDataAge));
        decimal? currentEquityNotional = (unsupportedAssetBalance || unvaluedOtherPosition)
            ? null
            : totalQuoteBalance + (baseQuantity * latest.ClosePrice.Value) + totalOtherPositionsNotional;
        var dayStartUtc = new DateTimeOffset(riskValuationAt.UtcDateTime.Date, TimeSpan.Zero);
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
            riskValuationAt,
            cancellationToken).ConfigureAwait(false);
        BinanceBookTicker bookTicker;
        if (streamBookTickers.TryGetValue(symbol, out var cachedTicker) &&
            PortfolioTickerValidation.IsValid(cachedTicker, symbol, valuationTimeProvider.GetUtcNow(), policyDocument.Policy.MaxMarketDataAge))
        {
            bookTicker = cachedTicker;
        }
        else
        {
            try
            {
                bookTicker = await marketClient.GetBookTickerAsync(
                    symbol,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                AutonomyCycleLog.MarketDataRejected(logger, symbol, ex.Message);
                return;
            }
        }
        var navValuationAt = valuationTimeProvider.GetUtcNow();
        if (!PortfolioTickerValidation.IsValid(bookTicker, symbol, navValuationAt, policyDocument.Policy.MaxMarketDataAge))
        {
            AutonomyCycleLog.MarketDataRejected(logger, symbol, "PRIMARY_BOOK_TICKER_INVALID_OR_STALE");
            return;
        }
        if (navSnapshots is not null && !unsupportedAssetBalance && !unvaluedOtherPosition &&
            usedOtherTickers.All(item => PortfolioTickerValidation.IsValid(item.Ticker, item.Symbol, navValuationAt, policyDocument.Policy.MaxMarketDataAge)))
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
                    symbol, quoteAsset.Value, navValuationAt,
                    consolidatedNavQuote, consolidatedUnrealized,
                    null, "account-snapshot+observed-book-mid"), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Valuation telemetry must never stop trading or push the system into SafeMode.
                AutonomyCycleLog.TelemetryWriteFailed(logger, "nav_snapshot", ex.GetType().Name);
            }
            AutonomyCycleLog.Valuation(logger, symbol, navValuationAt,
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
        decimal? depthLiquidityTop5 = null;
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
                var snapshotEpoch = Volatile.Read(ref marketStreamEpoch);
                var snapshot = await marketClient.GetOrderBookSnapshotAsync(symbol, 20, cancellationToken).ConfigureAwait(false);
                if (snapshot.Bids.Count > 0 && snapshot.Asks.Count > 0)
                {
                    lock (localBook)
                    {
                        if (snapshotEpoch == Volatile.Read(ref marketStreamEpoch))
                        {
                            localBook.ApplySnapshot(snapshot);
                        }
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
                var metrics = localBook.CalculateTopDepthMetrics(5);
                depthImbalanceTop5 = metrics.Imbalance;
                depthLiquidityTop5 = metrics.LiquidityNotional;
            }
        }
        var temporalOfi = temporalOfiBySymbol.TryGetValue(symbol, out var tOfi) ? (decimal?)tOfi : null;
        var tradeAggressionDelta = ReadTradeAggression(symbol, valuationTimeProvider.GetUtcNow(), policyDocument.Policy.MaxMarketDataAge);
        var effectiveAvailableLiquidity = Math.Max(mappedExecutionMarket.AvailableLiquidityNotional, depthLiquidityTop5 ?? 0m);
        var executionMarket = new ExecutionMarketSnapshot(
            mappedExecutionMarket.BestBid,
            mappedExecutionMarket.BestAsk,
            effectiveAvailableLiquidity,
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
        var decisionValuationAt = valuationTimeProvider.GetUtcNow();
        if (!PortfolioTickerValidation.IsValid(bookTicker, symbol, decisionValuationAt, policyDocument.Policy.MaxMarketDataAge))
        {
            AutonomyCycleLog.MarketDataRejected(logger, symbol, "PRIMARY_BOOK_TICKER_INVALID_OR_STALE");
            return;
        }
        var secondaryValuationsFresh = !unsupportedAssetBalance && !unvaluedOtherPosition &&
            usedOtherTickers.All(item => PortfolioTickerValidation.IsValid(item.Ticker, item.Symbol, decisionValuationAt, policyDocument.Policy.MaxMarketDataAge));
        var riskContextComplete = positionBasisConsistent && accounting.RiskContextComplete && secondaryValuationsFresh;
        var riskContextReason = !positionBasisConsistent
            ? "POSITION_BASIS_DRIFT"
            : !secondaryValuationsFresh ? "PORTFOLIO_VALUATION_STALE_OR_INCOMPLETE" : accounting.RiskContextUnavailableReason;
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

            lifetimePeakBps = positionLifetimePeakProfitBps.AddOrUpdate(
                symbol,
                Math.Max(0m, curProfitBps),
                (_, peak) => Math.Max(peak, curProfitBps));

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
                lastClosedTradeAtBySymbol[symbol] = valuationTimeProvider.GetUtcNow();
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
            Math.Max(Math.Min(buyRisk.AvailableLiquidityNotional, sellRisk.AvailableLiquidityNotional), depthLiquidityTop5 ?? 0m),
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
            MarketRegime? leaderRegime = null;
            decimal? leaderTrendSlope = null;
            decimal? ethBtcSpreadReturnBps = null;
            decimal? canaryBasketReturnBps = null;
            decimal? canaryBasketTrendSlope = null;
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

                        if (btcKlines.Count >= 20)
                        {
                            var btcCloseList = btcKlines
                                .Skip(btcKlines.Count - 20)
                                .Take(20)
                                .Where(static k => k.Close > 0m)
                                .Select(static k => k.Close)
                                .ToList();
                            if (btcCloseList.Count >= 20)
                            {
                                var btcSnapshot = MarketRegimeClassifier.Classify(
                                    new Symbol("BTCUSDT"),
                                    btcCloseList,
                                    now,
                                    trendSlopeThreshold: 0.00008m);
                                leaderRegime = btcSnapshot.Regime;
                                leaderTrendSlope = btcSnapshot.TrendSlopeRate;
                            }
                        }
                    }
                }

                if (leaderReturnBps is { } btcRetVal && klineCache.TryGetValue("ETHUSDT", out var ethKlines))
                {
                    lock (ethKlines)
                    {
                        if (ethKlines.Count >= 2 && ethKlines[^2].Close > 0m)
                        {
                            var ethRetBps = (ethKlines[^1].Close - ethKlines[^2].Close) / ethKlines[^2].Close * 10_000m;
                            ethBtcSpreadReturnBps = Math.Round(ethRetBps - btcRetVal, 4);
                        }
                    }
                }

                var canaryReturnsSum = 0m;
                var canaryReturnsCount = 0;
                var canarySlopeSum = 0m;
                var canarySlopeCount = 0;
                foreach (var canarySym in CanaryBasketSymbols)
                {
                    if (string.Equals(canarySym, symbol, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (klineCache.TryGetValue(canarySym, out var canaryKlines))
                    {
                        lock (canaryKlines)
                        {
                            if (canaryKlines.Count >= 2 && canaryKlines[^2].Close > 0m)
                            {
                                canaryReturnsSum += (canaryKlines[^1].Close - canaryKlines[^2].Close) / canaryKlines[^2].Close * 10_000m;
                                canaryReturnsCount++;
                            }

                            if (canaryKlines.Count >= 20)
                            {
                                var canaryCloseList = canaryKlines
                                    .Skip(canaryKlines.Count - 20)
                                    .Take(20)
                                    .Where(static k => k.Close > 0m)
                                    .Select(static k => k.Close)
                                    .ToList();
                                if (canaryCloseList.Count >= 20)
                                {
                                    var canarySnap = MarketRegimeClassifier.Classify(
                                        new Symbol(canarySym),
                                        canaryCloseList,
                                        now,
                                        trendSlopeThreshold: 0.00008m);
                                    canarySlopeSum += canarySnap.TrendSlopeRate;
                                    canarySlopeCount++;
                                }
                            }
                        }
                    }
                }

                if (canaryReturnsCount > 0)
                {
                    canaryBasketReturnBps = Math.Round(canaryReturnsSum / canaryReturnsCount, 4);
                }

                if (canarySlopeCount > 0)
                {
                    canaryBasketTrendSlope = Math.Round(canarySlopeSum / canarySlopeCount, 6);
                }

                if (streamBookTickers.TryGetValue("BTCUSDT", out var btcTicker) &&
                    (btcTicker.BidQuantity + btcTicker.AskQuantity) > 0m)
                {
                    leaderOrderBookImbalance = Math.Round(
                        (btcTicker.BidQuantity - btcTicker.AskQuantity) / (btcTicker.BidQuantity + btcTicker.AskQuantity),
                        4);
                }
            }

            decimal? marketBreadthRatio = null;
            var universeCount = 0;
            var bullishCount = 0;
            foreach (var cacheEntry in klineCache)
            {
                var cacheList = cacheEntry.Value;
                lock (cacheList)
                {
                    if (cacheList.Count >= 20)
                    {
                        universeCount++;
                        var close20 = cacheList[^20].Close;
                        var closeNow = cacheList[^1].Close;
                        if (closeNow > close20)
                        {
                            bullishCount++;
                        }
                    }
                }
            }

            if (universeCount >= 5)
            {
                marketBreadthRatio = Math.Round((decimal)bullishCount / universeCount, 4);
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
                valuationTimeProvider.GetUtcNow(),
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
                MaxOpenPositionCorrelation: maxOpenPositionCorrelation,
                MlHorizonBars: ResolveMlHorizonBars(runtime.AutonomyInterval),
                LeaderRegime: leaderRegime,
                LeaderTrendSlope: leaderTrendSlope,
                MarketBreadthRatio: marketBreadthRatio,
                EthBtcSpreadReturnBps: ethBtcSpreadReturnBps,
                CanaryBasketReturnBps: canaryBasketReturnBps,
                CanaryBasketTrendSlope: canaryBasketTrendSlope,
                MlTimeframe: runtime.AutonomyInterval.Trim());

            if (durablePerformances is not null)
            {
                adaptiveCycle.LoadStrategyRegimePerformances(await durablePerformances.GetPerformancesAsync(cancellationToken).ConfigureAwait(false));
            }
            var adaptiveResult = adaptiveCycle.Evaluate(adaptiveRequest);

            var volBps = bars.Length >= 20 ? adaptiveResult.Regime.VolatilityRate * 10_000m : 10m;
            var fullIndicators = TechnicalIndicatorCalculator.Evaluate(bars);
            var effectiveVol = Math.Max(volBps, fullIndicators.AtrBps);
            var commRateBps = commissionSnapshot is not null
                ? (commissionSnapshot.Standard.Maker + commissionSnapshot.Standard.Taker) * 10_000m
                : 15.0m;
            dynamicExitThresholdsBySymbol[symbol] = DynamicExitThresholds.Calculate(effectiveVol, commRateBps);

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

            var adaptiveCommit = CreateAdaptiveDecisionCommit(
                runtime,
                new Symbol(symbol),
                adaptiveCheckpointScope,
                featureSnapshot,
                adaptiveResult,
                adaptiveOutcome,
                adaptiveReason,
                adaptiveOutbox,
                replanCandidate);
            try
            {
                await decisionCommitStore.CommitAsync(adaptiveCommit, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already associated with different evidence", StringComparison.OrdinalIgnoreCase))
            {
                AutonomyCycleLog.MarketDataRejected(logger, symbol, $"Commit skipped: {ex.Message}");
                lastProcessedCloseUtc = latest.CloseTimeUtc;
                lastProcessedCloseByScope[checkpointScope] = latest.CloseTimeUtc;
                return;
            }

            lastProcessedCloseUtc = latest.CloseTimeUtc;
            lastProcessedCloseByScope[checkpointScope] = latest.CloseTimeUtc;
            if (!hasCompleteTradingRules || adaptiveResult.IsNoTrade)
            {
                TradingTelemetry.RecordAutonomyDecision("NO_TRADE", adaptiveReason);
                AutonomyCycleLog.NoTrade(logger, adaptiveCommit.DecisionId, symbol, latest.CloseTimeUtc, adaptiveReason);
                return;
            }

            if (adaptiveOutbox is null)
            {
                TradingTelemetry.RecordAutonomyDecision("BLOCKED", adaptiveReason);
                AutonomyCycleLog.Blocked(
                    logger,
                    adaptiveCommit.DecisionId,
                    symbol,
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

            TradingTelemetry.RecordAutonomyDecision("DISPATCHABLE", "ORDER_ENQUEUED");
            AutonomyCycleLog.Enqueued(logger, adaptiveCommit.DecisionId, symbol, latest.CloseTimeUtc, adaptiveOutbox.Command.ClientOrderId.Value, adaptiveOutbox.Command.Side.ToString());
            outboxWakeupSignal?.Notify();
            return;
        }

        var request = new BaselineDecisionCycleRequest(
            bars,
            portfolio,
            policyDocument.Policy,
            effectiveControl,
            runtime.AutonomyEntryTargetNotional,
            valuationTimeProvider.GetUtcNow(),
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
        var baselineCommit = CreateDecisionCommit(
            runtime,
            checkpointScope,
            featureSnapshot,
            result,
            outcome,
            reason,
            outbox,
            replanCandidate);
        try
        {
            await decisionCommitStore.CommitAsync(baselineCommit, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already associated with different evidence", StringComparison.OrdinalIgnoreCase))
        {
            AutonomyCycleLog.MarketDataRejected(logger, symbol, $"Commit skipped: {ex.Message}");
            lastProcessedCloseUtc = latest.CloseTimeUtc;
            lastProcessedCloseByScope[checkpointScope] = latest.CloseTimeUtc;
            return;
        }
        lastProcessedCloseUtc = latest.CloseTimeUtc;
        lastProcessedCloseByScope[checkpointScope] = latest.CloseTimeUtc;
        if (!hasCompleteTradingRules || isUnauthorizedEntrySymbol || result.IsNoTrade)
        {
            TradingTelemetry.RecordAutonomyDecision("NO_TRADE", reason);
            AutonomyCycleLog.NoTrade(logger, baselineCommit.DecisionId, symbol, latest.CloseTimeUtc, reason);
            return;
        }

        if (outbox is null)
        {
            TradingTelemetry.RecordAutonomyDecision("BLOCKED", reason);
            AutonomyCycleLog.Blocked(
                logger,
                baselineCommit.DecisionId,
                symbol,
                latest.CloseTimeUtc,
                reason);
            return;
        }

        TradingTelemetry.RecordAutonomyDecision("DISPATCHABLE", "ORDER_ENQUEUED");
        AutonomyCycleLog.Enqueued(logger, baselineCommit.DecisionId, symbol, latest.CloseTimeUtc, outbox.Command.ClientOrderId.Value, outbox.Command.Side.ToString());
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
            PriceTickSize: >= 0m, // 0 disables the tick rule per Binance filters.md
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
        var intervalDuration = ParseIntervalDuration(interval);
        var effectiveStaleness = intervalDuration + maximumStaleness;
        var guard = new BinanceClosedKlineGuard(symbol, interval, effectiveStaleness);
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

    internal static TimeSpan ParseIntervalDuration(string interval) => interval switch
    {
        "1s" => TimeSpan.FromSeconds(1),
        "1m" => TimeSpan.FromMinutes(1),
        "3m" => TimeSpan.FromMinutes(3),
        "5m" => TimeSpan.FromMinutes(5),
        "15m" => TimeSpan.FromMinutes(15),
        "30m" => TimeSpan.FromMinutes(30),
        "1h" => TimeSpan.FromHours(1),
        "2h" => TimeSpan.FromHours(2),
        "4h" => TimeSpan.FromHours(4),
        "6h" => TimeSpan.FromHours(6),
        "8h" => TimeSpan.FromHours(8),
        "12h" => TimeSpan.FromHours(12),
        "1d" => TimeSpan.FromDays(1),
        "3d" => TimeSpan.FromDays(3),
        "1w" => TimeSpan.FromDays(7),
        _ => TimeSpan.FromMinutes(1)
    };

    internal static int ResolveMlHorizonBars(string interval) => interval.Trim() switch
    {
        "1m" => 4,
        "5m" => 8,
        "15m" => 16,
        "1h" => 24,
        _ => 4
    };

    private static string ComputeExceptionFingerprint(Exception exception)
    {
        var payload = string.Join('|',
            exception.GetType().FullName,
            exception.StackTrace,
            exception.InnerException?.GetType().FullName);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..16];
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
    [LoggerMessage(LogLevel.Information, "Autonomy cycle selected NO_TRADE. DecisionId={DecisionId} Symbol={Symbol} CloseTime={CloseTime} Reason={Reason}")]
    public static partial void NoTrade(ILogger logger, string decisionId, string symbol, DateTimeOffset closeTime, string reason);

    [LoggerMessage(LogLevel.Information, "Autonomy cycle blocked by risk/control. DecisionId={DecisionId} Symbol={Symbol} CloseTime={CloseTime} Reasons={Reasons}")]
    public static partial void Blocked(ILogger logger, string decisionId, string symbol, DateTimeOffset closeTime, string reasons);

    [LoggerMessage(LogLevel.Information, "Autonomy cycle enqueued order. DecisionId={DecisionId} Symbol={Symbol} CloseTime={CloseTime} ClientOrderId={ClientOrderId} Side={Side}")]
    public static partial void Enqueued(ILogger logger, string decisionId, string symbol, DateTimeOffset closeTime, string clientOrderId, string side);

    [LoggerMessage(LogLevel.Error, "Autonomy cycle failed. ErrorType={ErrorType} ReasonCode={ReasonCode} ExceptionFingerprint={ExceptionFingerprint} Operation={Operation}")]
    public static partial void Failed(
        ILogger logger,
        string errorType,
        string reasonCode,
        string exceptionFingerprint,
        string operation);

    [LoggerMessage(LogLevel.Warning, "Binance market data WebSocket stream disconnected. Reconnecting in 2 seconds...")]
    public static partial void StreamDisconnected(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Warning, "Autonomy cycle market data for {Symbol} rejected by quality gate: {Reason}. Skipping cycle for symbol.")]
    public static partial void MarketDataRejected(ILogger logger, string symbol, string reason);

    [LoggerMessage(LogLevel.Warning, "Non-critical telemetry write failed and was isolated. Kind={Kind} ErrorType={ErrorType}")]
    public static partial void TelemetryWriteFailed(ILogger logger, string kind, string errorType);

    [LoggerMessage(LogLevel.Error, "Autonomy cycle could not enter SafeMode. ErrorType={ErrorType}")]
    public static partial void ControlFailure(ILogger logger, string errorType);
}

/// <summary>
/// Dynamic take-profit, chandelier pullback, and protective stop-loss thresholds calibrated
/// per-coin from point-in-time ATR volatility and market regime noise bands.
/// </summary>
internal readonly record struct DynamicExitThresholds(
    decimal TargetProfitBps,
    decimal ChandelierActivationBps,
    decimal ChandelierPullbackBps,
    decimal MinProfitLockBps,
    decimal StopLossBps)
{
    public static readonly DynamicExitThresholds Default = new(
        TargetProfitBps: 180.0m,
        ChandelierActivationBps: 120.0m,
        ChandelierPullbackBps: 50.0m,
        MinProfitLockBps: 40.0m,
        StopLossBps: 120.0m);

    public static DynamicExitThresholds Calculate(decimal effectiveVolBps, decimal roundTripFeeBps = 15.0m)
    {
        var safeFeeBps = Math.Max(roundTripFeeBps, 15.0m);
        var safeVolBps = Math.Max(effectiveVolBps, 10.0m);

        var totalRealisticCostBps = safeFeeBps + 3.0m;
        var minTargetProfitBps = Math.Clamp(Math.Max(180.0m, totalRealisticCostBps * 4.0m), 180.0m, 450.0m);
        var targetProfitBps = Math.Clamp(safeVolBps * 3.5m, minTargetProfitBps, 450.0m);

        var stopLossBps = Math.Clamp(safeVolBps * 1.8m, 100.0m, 250.0m);

        var rawChandelierActivationBps = Math.Max(safeFeeBps + 60.0m, safeVolBps * 2.0m);
        var chandelierActivationBps = Math.Min(rawChandelierActivationBps, Math.Max(safeFeeBps + 30.0m, targetProfitBps - 20.0m));
        var chandelierPullbackBps = Math.Clamp(safeVolBps * 1.5m, 35.0m, 100.0m);
        var minProfitLockBps = Math.Max(40.0m, safeFeeBps + 15.0m);

        return new DynamicExitThresholds(
            TargetProfitBps: Math.Round(targetProfitBps, 2),
            ChandelierActivationBps: Math.Round(chandelierActivationBps, 2),
            ChandelierPullbackBps: Math.Round(chandelierPullbackBps, 2),
            MinProfitLockBps: Math.Round(minProfitLockBps, 2),
            StopLossBps: Math.Round(stopLossBps, 2));
    }
}
