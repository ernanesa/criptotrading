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

    public PaperShadowSession(
        TrendFollowingStrategy strategy,
        IPaperExchange paper,
        PaperShadowOptions? options = null)
    {
        this.strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        this.paper = paper ?? throw new ArgumentNullException(nameof(paper));
        this.options = options ?? new PaperShadowOptions();
    }

    public ShadowRunResult Run(IReadOnlyList<CloseBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        var decisions = strategy.Evaluate(bars);
        var observations = new ShadowExecutionObservation[decisions.Count];
        var inPosition = false;

        for (var index = 0; index < decisions.Count; index++)
        {
            var decision = decisions[index];
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

        return ShadowRunRunner.Run(decisions, observations);
    }
}
