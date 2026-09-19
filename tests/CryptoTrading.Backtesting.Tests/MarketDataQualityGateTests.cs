using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class MarketDataQualityGateTests
{
    [Fact]
    public void AcceptsClosedBarsOnlyAfterTheyBecomeAvailable()
    {
        var dataset = SyntheticMarketDataset.CreateBtcUsdtV1();
        var records = new[]
        {
            Envelope(0, 100m, DateTimeOffset.UnixEpoch.AddMinutes(1)),
            Envelope(1, 101m, DateTimeOffset.UnixEpoch.AddMinutes(2)),
        };

        var result = MarketDataQualityGate.ValidateClosedBars(dataset.Metadata, records, "btcusdt");

        Assert.Equal("synthetic-btcusdt-oos", result.DatasetId);
        Assert.Equal(2, result.BarCount);
        Assert.Equal(records[0].Bar.CloseTimeUtc, result.FirstBarUtc);
        Assert.Equal(records[1].Bar.CloseTimeUtc, result.LastBarUtc);
        Assert.Equal(64, result.ReplayFingerprint.Length);
    }

    [Fact]
    public void RejectsLookAheadWhenBarClosesAfterAvailability()
    {
        var dataset = SyntheticMarketDataset.CreateBtcUsdtV1();
        var records = new[]
        {
            Envelope(0, 100m, DateTimeOffset.UnixEpoch.AddSeconds(-1)),
        };

        var exception = Assert.Throws<InvalidDataException>(() =>
            MarketDataQualityGate.ValidateClosedBars(dataset.Metadata, records, "BTCUSDT"));

        Assert.Contains("used before availability", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsDuplicateSequenceAndNonMonotonicBarTime()
    {
        var dataset = SyntheticMarketDataset.CreateBtcUsdtV1();
        var duplicateSequence = new[]
        {
            Envelope(0, 100m, DateTimeOffset.UnixEpoch.AddMinutes(1)),
            Envelope(0, 101m, DateTimeOffset.UnixEpoch.AddMinutes(2)),
        };
        var nonMonotonicTime = new[]
        {
            Envelope(0, 100m, DateTimeOffset.UnixEpoch.AddMinutes(2)),
            Envelope(1, 101m, DateTimeOffset.UnixEpoch.AddMinutes(1)),
        };

        Assert.Throws<InvalidDataException>(() =>
            MarketDataQualityGate.ValidateClosedBars(dataset.Metadata, duplicateSequence, "BTCUSDT"));
        Assert.Throws<InvalidDataException>(() =>
            MarketDataQualityGate.ValidateClosedBars(dataset.Metadata, nonMonotonicTime, "BTCUSDT"));
    }

    [Fact]
    public async Task LoadsPublicKlineJournalIntoVersionedQualityGatedDataset()
    {
        using var directory = new TemporaryDirectory();
        using var recorder = new MarketDataRecorder(directory.Path);
        var first = DateTimeOffset.UnixEpoch.AddDays(1);
        await recorder.AppendAsync(first, first.AddMinutes(1), "kline-rest", "rest:klines:1d", "BTCUSDT",
            new { closeTimeUtc = first, close = 100m });
        var second = first.AddDays(1);
        await recorder.AppendAsync(second, second.AddMinutes(1), "kline-rest", "rest:klines:1d", "BTCUSDT",
            new { closeTimeUtc = second, close = 101m });

        var replay = await recorder.ReadReplayAsync();
        var loaded = PublicKlineDatasetLoader.Load(
            replay,
            "btc-testnet-1d",
            "2026-09-19",
            "binance-testnet-public",
            "btcusdt",
            DateTimeOffset.UtcNow);

        Assert.Equal(2, loaded.Records.Count);
        Assert.Equal(2, loaded.Quality.BarCount);
        Assert.Equal(replay.DatasetChecksum, loaded.Metadata.ContentHash);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cryptotrading-kline-loader-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private static ClosedBarEnvelope Envelope(long sequence, decimal price, DateTimeOffset availableAtUtc) =>
        new(
            sequence,
            new CloseBar(
                new Symbol("BTCUSDT"),
                DateTimeOffset.UnixEpoch.AddMinutes(sequence + 1),
                price),
            availableAtUtc);
}
