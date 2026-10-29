using CryptoTrading.Backtesting;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Exchange.Paper;

namespace CryptoTrading.Shadow;

/// <summary>Local-only options for connecting a strategy replay to Paper.</summary>
public sealed record PaperShadowOptions
{
    public Quantity OrderQuantity { get; init; } = new(0.001m);

    public IReadOnlySet<int> StaleBarIndexes { get; init; } = new HashSet<int>();

    public IReadOnlySet<int> RecoveredBarIndexes { get; init; } = new HashSet<int>();
}

/// <summary>
/// Runs a strategy against the deterministic Paper exchange and converts each
/// bar/fill outcome into a redacted Shadow observation. It has no network or
/// authenticated exchange capability.
/// </summary>
public sealed class PaperShadowSession
{
    private readonly TrendFollowingStrategy strategy;
    private readonly IPaperExchange paper;
    private readonly PaperShadowOptions options;
    private readonly TimeProvider clock;
    private readonly AdvancingTimeProvider? replayClock;

    /// <param name="timeProvider">
    /// Wall clock used to reject historical bars when no replay clock is given.
    /// </param>
    /// <param name="replayClock">
    /// Deterministic clock shared with the Paper exchange. When supplied, the
    /// session advances it to each bar so historical replay produces fills.
    /// </param>
    public PaperShadowSession(
        TrendFollowingStrategy strategy,
        IPaperExchange paper,
        PaperShadowOptions? options = null,
        TimeProvider? timeProvider = null,
        AdvancingTimeProvider? replayClock = null)
    {
        this.strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        this.paper = paper ?? throw new ArgumentNullException(nameof(paper));
        this.options = options ?? new PaperShadowOptions();
        this.clock = timeProvider ?? TimeProvider.System;
        this.replayClock = replayClock;
    }

    public ShadowRunResult Run(IReadOnlyList<CloseBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);

        // Paper orders only execute on ticks at or after their submission time,
        // which is the wall clock. Historical bars would therefore produce zero
        // fills silently, which is indistinguishable from "no signal".
        if (replayClock is null && bars.Count > 0 && bars[^1].CloseTimeUtc < clock.GetUtcNow())
        {
            throw new InvalidOperationException(
                "Paper shadow is forward-only: the last bar predates the current clock, so every tick would be " +
                "skipped and the run would report no fills. Use a backtest for historical data.");
        }

        var decisions = strategy.Evaluate(bars);
        var observations = new ShadowExecutionObservation[decisions.Count];
        var inPosition = false;

        for (var index = 0; index < decisions.Count; index++)
        {
            var decision = decisions[index];
            replayClock?.AdvanceTo(decision.CloseTimeUtc);
            var closePrice = bars[index].ClosePrice;
            var tick = new PaperMarketTick(
                decision.Symbol,
                closePrice,
                closePrice,
                closePrice,
                decision.CloseTimeUtc);
            var fills = Array.Empty<PaperFill>();

            if (decision.Signal == TrendFollowingSignal.EnterLong && !inPosition)
            {
                _ = paper.Submit(
                    new PaperOrderRequest(
                        new ClientOrderId($"shadow-{decision.BarIndex:D6}"),
                        decision.Symbol,
                        OrderSide.Buy,
                        OrderType.Market,
                        options.OrderQuantity),
                    new IdempotencyKey($"shadow-submit-{decision.BarIndex:D6}"));
                fills = paper.Apply(tick).ToArray();
                inPosition = fills.Length > 0;
            }
            else if (decision.Signal == TrendFollowingSignal.ExitLong && inPosition)
            {
                _ = paper.Submit(
                    new PaperOrderRequest(
                        new ClientOrderId($"shadow-{decision.BarIndex:D6}"),
                        decision.Symbol,
                        OrderSide.Sell,
                        OrderType.Market,
                        options.OrderQuantity),
                    new IdempotencyKey($"shadow-submit-{decision.BarIndex:D6}"));
                fills = paper.Apply(tick).ToArray();
                inPosition = fills.Length == 0;
            }

            var expectedFingerprint = ShadowRunRunner.Fingerprint(decision);
            var observedFingerprint = decision.Signal == TrendFollowingSignal.NoTrade || fills.Length > 0
                ? expectedFingerprint
                : "paper-no-fill";
            observations[index] = new(
                decision.BarIndex,
                decision.CloseTimeUtc,
                observedFingerprint,
                options.StaleBarIndexes.Contains(decision.BarIndex),
                options.RecoveredBarIndexes.Contains(decision.BarIndex));
        }

        var result = ShadowRunRunner.Run(decisions, observations);

        // Fills are computed by the deterministic Paper exchange above; summarizing
        // them gives the run a realized result instead of only drift metrics.
        return paper.Fills.Count == 0
            ? result
            : result with { PaperResult = PaperPnlCalculator.Calculate(paper.Fills) };
    }
}
