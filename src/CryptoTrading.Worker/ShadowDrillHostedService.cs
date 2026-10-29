using CryptoTrading.Backtesting.Strategies;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Exchange.Paper;
using CryptoTrading.Observability;
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
    ShadowReportWriter reportWriter,
    TradingAlertDispatcher alertDispatcher,
    ILogger<ShadowDrillHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var runtime = options.Value;
        if (!runtime.ShadowEnabled)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var symbol = new Symbol("BTCUSDT");
        var start = DateTimeOffset.UtcNow.AddSeconds(1);
        var bars = new[]
        {
            new CloseBar(symbol, start, 100m),
            new CloseBar(symbol, start.AddMinutes(1), 90m),
            new CloseBar(symbol, start.AddMinutes(2), 80m),
            new CloseBar(symbol, start.AddMinutes(3), 120m),
        };
        var strategy = new TrendFollowingStrategy(
            new TrendFollowingParameters("worker-shadow-drill", "1.0.0", symbol, 2, 3, 0m, 0m));
        // Synthetic prices must never affect the operational paper exchange.
        var result = new PaperShadowSession(strategy, new PaperExchange()).Run(bars);
        var completedAt = DateTimeOffset.UtcNow;
        var reportPath = await reportWriter.AppendAsync(new ShadowRunReport(
            Guid.NewGuid().ToString("N"),
            completedAt,
            result.ObservationCount,
            result.DivergenceCount,
            result.StaleCount,
            result.RecoveryCount), cancellationToken).ConfigureAwait(false);
        _ = await ShadowAlertPublisher.PublishAsync(result, alertDispatcher, completedAt, cancellationToken).ConfigureAwait(false);

        ShadowDrillLog.Completed(logger, result.ObservationCount, result.DivergenceCount, result.StaleCount, result.RecoveryCount, reportPath);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static partial class ShadowDrillLog
{
    [LoggerMessage(
        LogLevel.Information,
        "Local Shadow drill completed. Observations={ObservationCount} Divergences={DivergenceCount} Stale={StaleCount} Recoveries={RecoveryCount} ReportPath={ReportPath}")]
    public static partial void Completed(
        ILogger logger,
        int observationCount,
        int divergenceCount,
        int staleCount,
        int recoveryCount,
        string reportPath);
}
