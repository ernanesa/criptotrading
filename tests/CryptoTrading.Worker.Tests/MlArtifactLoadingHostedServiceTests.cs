using CryptoTrading.Backtesting.Autonomy;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class MlArtifactLoadingHostedServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LoadsRealVerifiedLocalCatalogAndStopPermanentlyInvalidatesState()
    {
        var export = Export();
        var directory = Path.Combine(Path.GetTempPath(), "ct-ml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "candidate.mlmodel.json");
        try
        {
            await File.WriteAllBytesAsync(path, export.Payload.ToArray(), TestContext.Current.CancellationToken);
            var options = new WorkerRuntimeOptions { AutonomyMlArtifactPath = path, AutonomyMlArtifactSha256 = export.Sha256 };
            var state = new MlArtifactState();
            var loader = Loader(options, state);
            await loader.StartAsync(TestContext.Current.CancellationToken);
            var model = state.Find("BTCUSDT", "1m", OrderSide.Buy, 4);
            Assert.NotNull(model);
            Assert.True(model.TryPredict(new("BTCUSDT", "1m", OrderSide.Buy, 4, Now, Now, new(-2m, -4m, -6m, 4m)), out var prediction));
            Assert.NotNull(prediction.Probability);
            Assert.True(options.NoTrade);
            Assert.Equal("ML_ARTIFACT_LOADED", state.AvailabilityReason);
            await loader.StopAsync(TestContext.Current.CancellationToken);
            Assert.Null(state.Find("BTCUSDT", "1m", OrderSide.Buy, 4));
            Assert.False(state.TryPublish(MlArtifactCatalog.LoadVerified(export.Payload.Span, export.Sha256)));
            Assert.Equal("ML_ARTIFACT_STOPPED", state.AvailabilityReason);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task TamperedFileClearsAPreviouslyLoadedCatalog()
    {
        var export = Export();
        var directory = Path.Combine(Path.GetTempPath(), "ct-ml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "candidate.mlmodel.json");
        try
        {
            await File.WriteAllBytesAsync(path, export.Payload.ToArray(), TestContext.Current.CancellationToken);
            var state = new MlArtifactState();
            var loader = Loader(new WorkerRuntimeOptions { AutonomyMlArtifactPath = path, AutonomyMlArtifactSha256 = export.Sha256 }, state);
            await loader.StartAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(state.Find("BTCUSDT", "1m", OrderSide.Buy, 4));
            await File.WriteAllTextAsync(path, "synthetic invalid model payload", TestContext.Current.CancellationToken);
            await loader.StartAsync(TestContext.Current.CancellationToken);
            Assert.Null(state.Find("BTCUSDT", "1m", OrderSide.Buy, 4));
            Assert.Equal("ML_ARTIFACT_INVALID_OR_UNREADABLE", state.AvailabilityReason);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task UnconfiguredOrCanceledLoaderCannotPublishAClassifier()
    {
        var state = new MlArtifactState();
        await Loader(new WorkerRuntimeOptions(), state).StartAsync(TestContext.Current.CancellationToken);
        Assert.Null(state.Find("BTCUSDT", "1m", OrderSide.Buy, 4));
        Assert.Equal("ML_ARTIFACT_NOT_CONFIGURED", state.AvailabilityReason);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Loader(new WorkerRuntimeOptions(), state).StartAsync(cancellation.Token));
        Assert.Null(state.Find("BTCUSDT", "1m", OrderSide.Buy, 4));
    }

    [Theory]
    [InlineData("candidate.mlmodel.json", null)]
    [InlineData(null, "ABC")]
    [InlineData("candidate.pem", "0000000000000000000000000000000000000000000000000000000000000000")]
    public void PartialOrWrongKindOfModelConfigurationFailsBeforeFileAccess(string? path, string? hash)
    {
        var options = new WorkerRuntimeOptions { AutonomyMlArtifactPath = path, AutonomyMlArtifactSha256 = hash };
        Assert.Throws<OptionsValidationException>(options.ValidateMlArtifact);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("oversized")]
    [InlineData("expired")]
    [InlineData("future")]
    public async Task UnreadableOversizedOrTemporallyIneligibleCatalogCannotBecomeActive(string scenario)
    {
        var export = Export(createdAt: scenario == "future" ? Now.AddHours(1) : null,
            validUntil: scenario == "expired" ? Now.AddSeconds(-1) : null);
        var directory = Path.Combine(Path.GetTempPath(), "ct-ml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "candidate.mlmodel.json");
        try
        {
            if (scenario != "missing") await File.WriteAllBytesAsync(path, export.Payload.ToArray(), TestContext.Current.CancellationToken);
            if (scenario == "oversized")
            {
                using var file = File.OpenWrite(path);
                file.SetLength(MlArtifactCatalog.MaximumPayloadBytes + 1L);
            }
            var state = new MlArtifactState();
            await Loader(new WorkerRuntimeOptions { AutonomyMlArtifactPath = path, AutonomyMlArtifactSha256 = export.Sha256 }, state)
                .StartAsync(TestContext.Current.CancellationToken);
            Assert.Null(state.Find("BTCUSDT", "1m", OrderSide.Buy, 4));
            Assert.NotEqual("ML_ARTIFACT_LOADED", state.AvailabilityReason);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task WhitespaceIntervalUsesSameTimeframeAndHorizonInLoaderAndDecision()
    {
        var export = Export(timeframe: "5m", horizonBars: 8);
        var directory = Path.Combine(Path.GetTempPath(), "ct-ml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "candidate.mlmodel.json");
        try
        {
            await File.WriteAllBytesAsync(path, export.Payload.ToArray(), TestContext.Current.CancellationToken);
            var options = new WorkerRuntimeOptions { AutonomyMlArtifactPath = path, AutonomyMlArtifactSha256 = export.Sha256, AutonomyInterval = " 5m " };
            var state = new MlArtifactState();
            await Loader(options, state).StartAsync(TestContext.Current.CancellationToken);
            Assert.Equal("ML_ARTIFACT_LOADED", state.AvailabilityReason);
            var horizon = AutonomyCycleHostedService.ResolveMlHorizonBars(options.AutonomyInterval);
            Assert.Equal(8, horizon);
            Assert.NotNull(state.Find("BTCUSDT", options.AutonomyInterval.Trim(), OrderSide.Buy, horizon));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static MlArtifactLoadingHostedService Loader(WorkerRuntimeOptions options, MlArtifactState state) =>
        new(Options.Create(options), state, new FixedClock(), NullLogger<MlArtifactLoadingHostedService>.Instance);

    private static MlArtifactExport Export(DateTimeOffset? createdAt = null, DateTimeOffset? validUntil = null,
        string timeframe = "1m", int horizonBars = 4)
    {
        var start = Now.AddDays(-2);
        var period = int.Parse(timeframe.AsSpan(0, timeframe.Length - 1), System.Globalization.CultureInfo.InvariantCulture);
        MlTrainingObservation[] Rows(int offset, int count) => Enumerable.Range(0, count).Select(i =>
        {
            var x = (i % 16 - 8) / 2m;
            var at = start.AddMinutes((offset + i) * period);
            return new MlTrainingObservation(at, at.AddMinutes(horizonBars * period), new(x, x * 2m, x * 3m, 2m + Math.Abs(x)), x < 0m);
        }).ToArray();
        var model = MlDecisionTrainer.Train(Rows(0, 64), Rows(80, 32), Rows(130, 32),
            new MlArtifactPolicy("BTCUSDT", timeframe, OrderSide.Buy, horizonBars), createdAt ?? start.AddMinutes(180 * period), validUntil ?? Now.AddDays(1), TestContext.Current.CancellationToken);
        return MlArtifactCatalog.CreateBundle([model]);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
