using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Exchange.Paper;
using CryptoTrading.Shadow;
using Microsoft.Extensions.Options;

namespace CryptoTrading.Worker;

/// <summary>
/// Optional one-shot local Shadow drill. It is disabled by default and cannot
/// run alongside a non-NO_TRADE worker configuration.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "The host creates this hosted service through dependency injection.")]
internal sealed class ShadowDrillHostedService(
    IOptions<WorkerRuntimeOptions> options,
    IPaperExchange paper,
    ILogger<ShadowDrillHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var runtime = options.Value;
        if (!runtime.ShadowEnabled)
        {
            return Task.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var symbol = new Symbol("BTCUSDT");
        var bars = new[]
        {
            new CloseBar(symbol, DateTimeOffset.UnixEpoch, 100m),
            new CloseBar(symbol, DateTimeOffset.UnixEpoch.AddMinutes(1), 90m),
            new CloseBar(symbol, DateTimeOffset.UnixEpoch.AddMinutes(2), 80m),
            new CloseBar(symbol, DateTimeOffset.UnixEpoch.AddMinutes(3), 120m),
        };
        var strategy = new TrendFollowingStrategy(
            new TrendFollowingParameters("worker-shadow-drill", "1.0.0", symbol, 2, 3, 0m, 0m));
        var result = new PaperShadowSession(strategy, paper).Run(bars);
        ShadowDrillLog.Completed(logger, result.ObservationCount, result.DivergenceCount, result.StaleCount, result.RecoveryCount);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static partial class ShadowDrillLog
{
    [LoggerMessage(
        LogLevel.Information,
        "Local Shadow drill completed. Observations={ObservationCount} Divergences={DivergenceCount} Stale={StaleCount} Recoveries={RecoveryCount}")]
    public static partial void Completed(
        ILogger logger,
        int observationCount,
        int divergenceCount,
        int staleCount,
        int recoveryCount);
}
