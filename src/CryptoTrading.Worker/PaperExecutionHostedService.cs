using CryptoTrading.Exchange.Binance;
using CryptoTrading.Exchange.Paper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoTrading.Worker;

/// <summary>
/// Feeds market ticks to the Paper exchange so submitted paper orders can fill.
/// Without this pump the exchange never executes and orders stay open forever.
/// It performs read-only market-data requests and never reaches a real venue.
/// </summary>
internal sealed class PaperExecutionHostedService(
    IPaperExchange paper,
    IBinanceSpotMarketClient marketClient,
    IOptions<WorkerRuntimeOptions> options,
    TimeProvider timeProvider,
    ILogger<PaperExecutionHostedService> logger) : BackgroundService
{
    private readonly PaperTickPump pump = new(paper);

    /// <summary>Fills observed during this process, exposed for diagnostics.</summary>
    public IReadOnlyList<PaperFill> Fills => pump.Fills;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runtime = options.Value;
        if (runtime.NoTrade || !string.Equals(runtime.ExecutionMode, "PAPER", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var symbols = runtime.ResolveAutonomySymbols();
        if (symbols.Count == 0)
        {
            PaperExecutionLog.NoSymbols(logger);
            return;
        }

        var interval = TimeSpan.FromMilliseconds(runtime.PollIntervalMilliseconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var symbol in symbols)
            {
                await PumpOnceAsync(symbol, stoppingToken).ConfigureAwait(false);
            }

            await Task.Delay(interval, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Fetches one ticker and applies it. Returns the number of fills produced.</summary>
    internal async Task<int> PumpOnceAsync(string symbol, CancellationToken cancellationToken)
    {
        if (options.Value.NoTrade || !string.Equals(options.Value.ExecutionMode, "PAPER", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        BinanceBookTicker ticker;
        try
        {
            ticker = await marketClient.GetBookTickerAsync(symbol, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            // A missing tick is not an execution; retry on the next poll.
            PaperExecutionLog.TickUnavailable(logger, symbol, exception.GetType().Name);
            return 0;
        }
        catch (System.Text.Json.JsonException exception)
        {
            PaperExecutionLog.TickUnavailable(logger, symbol, exception.GetType().Name);
            return 0;
        }

        var tick = ToTick(ticker, symbol, timeProvider.GetUtcNow());
        if (tick is null)
        {
            PaperExecutionLog.TickRejected(logger, symbol);
            return 0;
        }

        var produced = pump.Apply(tick);
        if (produced > 0)
        {
            PaperExecutionLog.Filled(logger, symbol, produced);
        }

        return produced;
    }

    /// <summary>
    /// Builds a tick only from a usable book. A crossed or non-positive quote must
    /// never become an execution price.
    /// </summary>
    internal static PaperMarketTick? ToTick(BinanceBookTicker ticker, string expectedSymbol, DateTimeOffset now)
    {
        if (ticker is null || !string.Equals(ticker.Symbol, expectedSymbol, StringComparison.Ordinal) ||
            ticker.BidPrice <= 0m || ticker.AskPrice < ticker.BidPrice)
        {
            return null;
        }

        if (ticker.ObservedAtUtc > now)
        {
            return null;
        }

        return new PaperMarketTick(
            new Domain.Primitives.Symbol(ticker.Symbol),
            ticker.BidPrice,
            ticker.AskPrice,
            (ticker.BidPrice + ticker.AskPrice) / 2m,
            ticker.ObservedAtUtc);
    }
}

internal static partial class PaperExecutionLog
{
    [LoggerMessage(LogLevel.Warning, "Paper execution is enabled but no autonomy symbol is configured; no ticks will be applied.")]
    public static partial void NoSymbols(ILogger logger);

    [LoggerMessage(LogLevel.Warning, "Paper tick unavailable for {Symbol}. ErrorType={ErrorType}")]
    public static partial void TickUnavailable(ILogger logger, string symbol, string errorType);

    [LoggerMessage(LogLevel.Warning, "Paper tick rejected for {Symbol} because the book is not usable.")]
    public static partial void TickRejected(ILogger logger, string symbol);

    [LoggerMessage(LogLevel.Information, "Paper fill produced. Symbol={Symbol} Fills={Fills}")]
    public static partial void Filled(ILogger logger, string symbol, int fills);
}
