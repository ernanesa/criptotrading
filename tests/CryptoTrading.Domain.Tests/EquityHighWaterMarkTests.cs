using CryptoTrading.Domain.Risk;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class EquityHighWaterMarkTests
{
    private static readonly DateTimeOffset First = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstObservationIsPersistedButDoesNotAuthorizeRiskContext()
    {
        var result = EquityHighWaterMarkCalculator.Apply(1_000m, First, null);

        Assert.False(result.IsComplete);
        Assert.Equal(EquityHighWaterMarkCalculator.InitialObservationRequired, result.UnavailableReason);
        Assert.Equal(1_000m, result.State.HighWaterMarkNotional);
        Assert.Equal(0m, result.State.DrawdownNotional);
    }

    [Fact]
    public void HighWaterMarkIsMonotonicAndDrawdownIsNotional()
    {
        var initial = EquityHighWaterMarkCalculator.Apply(1_000m, First, null).State;
        var increased = EquityHighWaterMarkCalculator.Apply(1_200m, First.AddMinutes(1), initial);
        var decreased = EquityHighWaterMarkCalculator.Apply(900m, First.AddMinutes(2), increased.State);

        Assert.True(increased.IsComplete);
        Assert.Equal(1_200m, increased.State.HighWaterMarkNotional);
        Assert.Equal(1_200m, decreased.State.HighWaterMarkNotional);
        Assert.Equal(300m, decreased.State.DrawdownNotional);
    }

    [Fact]
    public void OlderOrConflictingReplayFailsClosed()
    {
        var initial = EquityHighWaterMarkCalculator.Apply(1_000m, First, null).State;
        var current = EquityHighWaterMarkCalculator.Apply(1_100m, First.AddMinutes(1), initial).State;

        var older = EquityHighWaterMarkCalculator.Apply(950m, First, current);
        var conflict = EquityHighWaterMarkCalculator.Apply(950m, First.AddMinutes(1), current);

        Assert.False(older.IsComplete);
        Assert.Equal(EquityHighWaterMarkCalculator.OutOfOrderValuation, older.UnavailableReason);
        Assert.False(conflict.IsComplete);
        Assert.Equal(EquityHighWaterMarkCalculator.ValuationConflict, conflict.UnavailableReason);
        Assert.Equal(current, older.State);
    }

    [Fact]
    public void IdempotentFirstObservationDoesNotCountAsSecondValuation()
    {
        var initial = EquityHighWaterMarkCalculator.Apply(1_000m, First, null).State;

        var replay = EquityHighWaterMarkCalculator.Apply(1_000m, First, initial);

        Assert.False(replay.IsComplete);
        Assert.Equal(EquityHighWaterMarkCalculator.InitialObservationRequired, replay.UnavailableReason);
    }
}
