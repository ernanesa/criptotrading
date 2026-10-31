using CryptoTrading.Backtesting.Autonomy;
using CryptoTrading.Domain.Orders;
using Microsoft.Extensions.Options;

namespace CryptoTrading.Worker;

internal sealed class MlArtifactState : IMlDecisionArtifactProvider
{
    private readonly Lock changeLock = new();
    private sealed record Snapshot(MlArtifactCatalog? Catalog, string Reason);
    private Snapshot snapshot = new(null, "ML_ARTIFACT_NOT_CONFIGURED");
    private bool stopped;

    public string AvailabilityReason => Volatile.Read(ref snapshot).Reason;
    public MlDecisionArtifact? Find(string symbol, string timeframe, OrderSide side, int horizonBars) =>
        Volatile.Read(ref snapshot).Catalog?.Find(symbol, timeframe, side, horizonBars);

    internal bool TryPublish(MlArtifactCatalog catalog) => TrySet(catalog, "ML_ARTIFACT_LOADED");

    internal bool TrySet(MlArtifactCatalog? catalog, string reason)
    {
        // Cold publication only; financial inference reads an immutable pointer.
        lock (changeLock)
        {
            if (stopped) return false;
            Volatile.Write(ref snapshot, new Snapshot(catalog, reason));
            return true;
        }
    }

    internal void Stop()
    {
        lock (changeLock)
        {
            stopped = true;
            Volatile.Write(ref snapshot, new Snapshot(null, "ML_ARTIFACT_STOPPED"));
        }
    }
}

/// <summary>One-shot local loading; no training, network requests or automatic promotion.</summary>
internal sealed class MlArtifactLoadingHostedService(
    IOptions<WorkerRuntimeOptions> options,
    MlArtifactState state,
    TimeProvider timeProvider,
    ILogger<MlArtifactLoadingHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!state.TrySet(null, "ML_ARTIFACT_LOADING")) return;
        var runtime = options.Value;
        runtime.ValidateMlArtifact();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(runtime.AutonomyMlArtifactPath))
            {
                state.TrySet(null, "ML_ARTIFACT_NOT_CONFIGURED");
                MlArtifactLog.Unavailable(logger, "ML_ARTIFACT_NOT_CONFIGURED");
                return;
            }
            var stream = new FileStream(runtime.AutonomyMlArtifactPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var streamLifetime = stream.ConfigureAwait(false);
            if (stream.Length is <= 0 or > MlArtifactCatalog.MaximumPayloadBytes)
                throw new InvalidDataException("ML_CATALOG_SIZE_INVALID");
            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
            var catalog = MlArtifactCatalog.LoadVerified(bytes, runtime.AutonomyMlArtifactSha256!);
            cancellationToken.ThrowIfCancellationRequested();
            var now = timeProvider.GetUtcNow();
            var timeframe = runtime.AutonomyInterval.Trim();
            var horizon = AutonomyCycleHostedService.ResolveMlHorizonBars(timeframe);
            var hasEligibleModel = now.Offset == TimeSpan.Zero && runtime.ResolveAutonomySymbols().Any(symbol =>
                catalog.Find(symbol, timeframe, OrderSide.Buy, horizon) is { } model &&
                model.Metadata.CreatedAtUtc <= now && now <= model.Metadata.ValidUntilUtc);
            if (!hasEligibleModel)
            {
                state.TrySet(null, "ML_ARTIFACT_CONTEXT_OR_VALIDITY_UNAVAILABLE");
                MlArtifactLog.Unavailable(logger, "ML_ARTIFACT_CONTEXT_OR_VALIDITY_UNAVAILABLE");
                return;
            }
            if (state.TryPublish(catalog)) MlArtifactLog.Loaded(logger, catalog.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.TrySet(null, "ML_ARTIFACT_LOAD_CANCELED");
            throw;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            state.TrySet(null, "ML_ARTIFACT_INVALID_OR_UNREADABLE");
            // Never log file contents, paths, or raw exception messages.
            MlArtifactLog.Unavailable(logger, "ML_ARTIFACT_INVALID_OR_UNREADABLE");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        state.Stop();
        return Task.CompletedTask;
    }
}

internal static partial class MlArtifactLog
{
    [LoggerMessage(4820, LogLevel.Information, "Loaded {ModelCount} integrity-checked ML artifacts; financial gates remain independent.")]
    public static partial void Loaded(ILogger logger, int modelCount);

    [LoggerMessage(4821, LogLevel.Warning, "ML artifact unavailable: {Code}. ML-dependent entries remain blocked.")]
    public static partial void Unavailable(ILogger logger, string code);
}
