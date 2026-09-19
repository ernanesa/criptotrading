using CryptoTrading.Backtesting;
using System.Globalization;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class ReplayContractsTests
{
    private static readonly DatasetMetadata Dataset = new(
        "BTCUSDT-trades",
        "2026-09-19-v1",
        "fixture",
        "trade.v1",
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        DateTimeOffset.Parse("2026-09-19T12:00:00+00:00", CultureInfo.InvariantCulture),
        "UTC");

    [Fact]
    public void SameInputAndConfigurationProduceSameFingerprint()
    {
        var request = CreateRequest(seed: 42);
        var runner = new DeterministicReplayRunner<TradeFixture>();

        var first = runner.Run(request);
        var second = runner.Run(request);

        Assert.Equal(first, second);
        Assert.Equal(2, first.EventCount);
    }

    [Fact]
    public void SeedIsPartOfTheFingerprint()
    {
        var runner = new DeterministicReplayRunner<TradeFixture>();

        var first = runner.Run(CreateRequest(seed: 42));
        var second = runner.Run(CreateRequest(seed: 43));

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void OutOfOrderEventsAreRejected()
    {
        var events = new[]
        {
            new ReplayEventEnvelope<TradeFixture>(1, DateTimeOffset.Parse("2026-01-01T00:00:01+00:00", CultureInfo.InvariantCulture), "trade", new(100m)),
            new ReplayEventEnvelope<TradeFixture>(2, DateTimeOffset.Parse("2026-01-01T00:00:00+00:00", CultureInfo.InvariantCulture), "trade", new(99m))
        };
        var request = new ReplayRunRequest<TradeFixture>(Dataset, new ReplayConfiguration(42, "UTC"), events);

        Assert.Throws<InvalidOperationException>(() => new DeterministicReplayRunner<TradeFixture>().Run(request));
    }

    private static ReplayRunRequest<TradeFixture> CreateRequest(long seed) =>
        new(Dataset, new ReplayConfiguration(seed, "UTC"),
        [
            new(1, DateTimeOffset.Parse("2026-01-01T00:00:00+00:00", CultureInfo.InvariantCulture), "trade", new TradeFixture(100m)),
            new(2, DateTimeOffset.Parse("2026-01-01T00:00:01+00:00", CultureInfo.InvariantCulture), "trade", new TradeFixture(101m))
        ]);

    private sealed record TradeFixture(decimal Price);
}
