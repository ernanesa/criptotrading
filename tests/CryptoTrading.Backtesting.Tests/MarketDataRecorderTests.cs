using System.Globalization;
using Xunit;

namespace CryptoTrading.Backtesting.Tests;

public sealed class MarketDataRecorderTests
{
    [Fact]
    public async Task AppendAndReplayVerifyChecksumsAndUseDeterministicOrdering()
    {
        var directory = CreateDirectory();
        try
        {
            using var recorder = new MarketDataRecorder(directory);
            var received = DateTimeOffset.Parse("2026-09-19T12:00:00+00:00", CultureInfo.InvariantCulture);
            await recorder.AppendAsync(
                DateTimeOffset.Parse("2026-09-19T12:00:02+00:00", CultureInfo.InvariantCulture),
                received,
                "trade",
                "btcusdt@trade",
                "BTCUSDT",
                new { price = 102.5m, quantity = 0.01m },
                TestContext.Current.CancellationToken);
            await recorder.AppendAsync(
                DateTimeOffset.Parse("2026-09-19T12:00:01+00:00", CultureInfo.InvariantCulture),
                received.AddSeconds(1),
                "trade",
                "btcusdt@trade",
                "BTCUSDT",
                new { price = 101.5m, quantity = 0.02m },
                TestContext.Current.CancellationToken);

            var replay = await recorder.ReadReplayAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(2, replay.Records.Count);
            Assert.Equal(1, replay.Records[0].Sequence);
            Assert.Equal(0, replay.Records[1].Sequence);
            Assert.Equal("BTCUSDT", replay.Records[0].Symbol);
            Assert.Equal(64, replay.DatasetChecksum.Length);
            Assert.All(replay.Records, record => Assert.Equal(64, record.Checksum.Length));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SequenceContinuesAfterRecorderRestart()
    {
        var directory = CreateDirectory();
        try
        {
            var timestamp = DateTimeOffset.Parse("2026-09-19T12:00:00+00:00", CultureInfo.InvariantCulture);
            using var first = new MarketDataRecorder(directory);
            var firstRecord = await first.AppendAsync(timestamp, timestamp, "trade", "stream", "BTCUSDT", new { value = 1 }, TestContext.Current.CancellationToken);
            using var second = new MarketDataRecorder(directory);
            var secondRecord = await second.AppendAsync(timestamp, timestamp, "trade", "stream", "BTCUSDT", new { value = 2 }, TestContext.Current.CancellationToken);

            Assert.Equal(0, firstRecord.Sequence);
            Assert.Equal(1, secondRecord.Sequence);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReplayFingerprintRemainsStableAcrossRestart()
    {
        var directory = CreateDirectory();
        try
        {
            var timestamp = DateTimeOffset.Parse("2026-09-19T12:00:00+00:00", CultureInfo.InvariantCulture);
            using (var first = new MarketDataRecorder(directory))
            {
                await first.AppendAsync(timestamp, timestamp.AddMinutes(1), "kline-rest", "rest:klines:1d", "BTCUSDT",
                    new { closeTimeUtc = timestamp, close = 100m }, TestContext.Current.CancellationToken);
                var written = await first.ReadReplayAsync(cancellationToken: TestContext.Current.CancellationToken);
                using var second = new MarketDataRecorder(directory);
                var restarted = await second.ReadReplayAsync(cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(written.DatasetChecksum, restarted.DatasetChecksum);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TamperingWithPayloadFailsClosed()
    {
        var directory = CreateDirectory();
        try
        {
            using var recorder = new MarketDataRecorder(directory);
            var timestamp = DateTimeOffset.Parse("2026-09-19T12:00:00+00:00", CultureInfo.InvariantCulture);
            await recorder.AppendAsync(timestamp, timestamp, "trade", "stream", "BTCUSDT", new { price = 100m }, TestContext.Current.CancellationToken);
            var path = Assert.Single(Directory.EnumerateFiles(directory, "*.jsonl"));
            var line = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            var changed = line.Replace("100", "101", StringComparison.Ordinal);
            Assert.NotEqual(line, changed);
            await File.WriteAllTextAsync(path, changed, TestContext.Current.CancellationToken);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => recorder.ReadReplayAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("checksum mismatch", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RetentionDeletesExpiredFilesAndHonorsByteBudget()
    {
        var directory = CreateDirectory();
        try
        {
            using var recorder = new MarketDataRecorder(directory);
            var oldTime = DateTimeOffset.Parse("2026-09-17T12:00:00+00:00", CultureInfo.InvariantCulture);
            await recorder.AppendAsync(oldTime, oldTime, "trade", "stream", "BTCUSDT", new { value = 1 }, TestContext.Current.CancellationToken);
            var oldPath = Assert.Single(Directory.EnumerateFiles(directory, "*.jsonl"));
            File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.Subtract(TimeSpan.FromDays(3)));

            var result = await recorder.ApplyRetentionAsync(
                new MarketDataRetentionPolicy(TimeSpan.FromDays(1), maximumBytes: 1024 * 1024),
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, result.FilesDeleted);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.jsonl"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cryptotrading-market-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
