using System.Globalization;
using CryptoTrading.Backtesting.Autonomy;
using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Architecture;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class BaselineFeatureSnapshotTests
{
    private static readonly Symbol BtcUsdt = new("BTCUSDT");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-20T12:00:00Z", CultureInfo.InvariantCulture);
    private static readonly TrendFollowingParameters Parameters = new("baseline", "1.0.0", BtcUsdt, 2, 3, 0m, 0m);

    [Fact]
    public void SameClosedBarEvidenceProducesStableFeatureFingerprintAndDecision()
    {
        var first = BaselineFeatureSnapshot.Create(Envelopes(), Parameters);
        var second = BaselineFeatureSnapshot.Create(Envelopes(), Parameters);

        Assert.Equal(BaselineFeatureSnapshot.CurrentSchemaVersion, first.SchemaVersion);
        Assert.Equal(first.FeatureFingerprint, second.FeatureFingerprint);
        Assert.Equal(first.Decision, second.Decision);
        Assert.Equal(first.CloseTimeUtc, first.Decision.CloseTimeUtc);
        Assert.Equal(Parameters.ConfigurationFingerprint, first.ConfigurationFingerprint);
    }

    [Fact]
    public void EvidencePriceAvailabilityOrConfigurationChangeChangesFeatureFingerprint()
    {
        var baseline = BaselineFeatureSnapshot.Create(Envelopes(), Parameters);
        var repriced = Envelopes();
        repriced[^1] = repriced[^1] with
        {
            Bar = new CloseBar(BtcUsdt, repriced[^1].Bar.CloseTimeUtc, 121m),
        };
        var delayed = Envelopes();
        delayed[^1] = delayed[^1] with { AvailableAtUtc = delayed[^1].AvailableAtUtc.AddSeconds(1) };
        var changedParameters = new TrendFollowingParameters("baseline", "1.0.1", BtcUsdt, 2, 3, 0m, 0m);

        Assert.NotEqual(baseline.FeatureFingerprint, BaselineFeatureSnapshot.Create(repriced, Parameters).FeatureFingerprint);
        Assert.NotEqual(baseline.FeatureFingerprint, BaselineFeatureSnapshot.Create(delayed, Parameters).FeatureFingerprint);
        Assert.NotEqual(baseline.FeatureFingerprint, BaselineFeatureSnapshot.Create(Envelopes(), changedParameters).FeatureFingerprint);
    }

    [Fact]
    public void InvalidSequenceOrLookAheadEvidenceFailsClosed()
    {
        var duplicateSequence = Envelopes();
        duplicateSequence[1] = duplicateSequence[1] with { Sequence = duplicateSequence[0].Sequence };
        var lookAhead = Envelopes();
        lookAhead[0] = lookAhead[0] with { AvailableAtUtc = lookAhead[0].Bar.CloseTimeUtc.AddMilliseconds(-1) };

        Assert.Throws<ArgumentException>(() => BaselineFeatureSnapshot.Create(duplicateSequence, Parameters));
        Assert.Throws<InvalidDataException>(() => BaselineFeatureSnapshot.Create(lookAhead, Parameters));
    }

    [Fact]
    public void CycleUsingSnapshotMatchesLegacyClosedBarEvaluation()
    {
        var envelopes = Envelopes();
        var request = NewRequest(envelopes.Select(static envelope => envelope.Bar).ToArray());
        var cycle = new BaselineDecisionCycle(Parameters);

        var legacy = cycle.Evaluate(request);
        var fromSnapshot = cycle.Evaluate(request with { FeatureSnapshot = BaselineFeatureSnapshot.Create(envelopes, Parameters) });

        Assert.Equal(legacy.Decision, fromSnapshot.Decision);
        Assert.Equal(legacy.IsNoTrade, fromSnapshot.IsNoTrade);
        Assert.Equal(legacy.Intent?.OrderId, fromSnapshot.Intent?.OrderId);
        Assert.Equal(legacy.Intent?.ClientOrderId, fromSnapshot.Intent?.ClientOrderId);
    }

    [Fact]
    public void CycleRejectsSnapshotEvaluatedBeforeLatestAvailability()
    {
        var envelopes = Envelopes();
        var request = NewRequest(envelopes.Select(static envelope => envelope.Bar).ToArray()) with
        {
            EvaluatedAtUtc = envelopes[^1].AvailableAtUtc.AddMilliseconds(-1),
            FeatureSnapshot = BaselineFeatureSnapshot.Create(envelopes, Parameters),
        };

        Assert.Throws<InvalidOperationException>(() => new BaselineDecisionCycle(Parameters).Evaluate(request));
    }

    private static ClosedBarEnvelope[] Envelopes() =>
    [
        Envelope(0, 100m),
        Envelope(1, 90m),
        Envelope(2, 80m),
        Envelope(3, 120m),
    ];

    private static ClosedBarEnvelope Envelope(long sequence, decimal price) => new(
        sequence,
        new CloseBar(BtcUsdt, Start.AddMinutes(sequence), price),
        Start.AddMinutes(sequence).AddSeconds(1));

    private static BaselineDecisionCycleRequest NewRequest(IReadOnlyList<CloseBar> bars) => new(
        bars,
        new PortfolioSnapshot(new Dictionary<Asset, Money>(), 0m, 0m, 0m),
        new RiskPolicy("policy-1", 1_000m, 2_000m, 500m, 500m, TimeSpan.FromMinutes(5)),
        OperationalControlSnapshot.Initial,
        100m,
        Start.AddMinutes(3).AddSeconds(1),
        new MarketRiskSnapshot(1m, 10m, 10_000m, Start.AddMinutes(3)));
}
