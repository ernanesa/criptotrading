namespace CryptoTrading.Exchange.Binance;

/// <summary>Provides current symbol rules to a pre-trade validation boundary.</summary>
public interface IBinanceSymbolTradingRulesProvider
{
    Task<BinanceSymbolTradingRules> GetAsync(
        string symbol,
        CancellationToken cancellationToken = default);

    Task<BinanceSymbolTradingRules> RefreshAsync(
        string symbol,
        CancellationToken cancellationToken = default);
}

/// <summary>One immutable, time-bounded snapshot of exchange symbol rules.</summary>
public sealed record BinanceSymbolTradingRulesSnapshot(
    BinanceSymbolTradingRules Rules,
    DateTimeOffset FetchedAtUtc,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Local cache for public Binance exchangeInfo rules. Expired rules are never
/// returned: refresh failures are surfaced so an order boundary can fail closed.
/// </summary>
public sealed class BinanceSymbolTradingRulesCache : IBinanceSymbolTradingRulesProvider, IDisposable
{
    private readonly IBinanceSpotMarketClient marketClient;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan freshness;
    private readonly Dictionary<string, BinanceSymbolTradingRulesSnapshot> snapshots = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private bool disposed;

    public BinanceSymbolTradingRulesCache(
        IBinanceSpotMarketClient marketClient,
        TimeSpan freshness,
        TimeProvider? timeProvider = null)
    {
        this.marketClient = marketClient ?? throw new ArgumentNullException(nameof(marketClient));
        if (freshness <= TimeSpan.Zero || freshness > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(freshness), "Rules freshness must be greater than zero and at most one day.");
        }

        this.freshness = freshness;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<BinanceSymbolTradingRules> GetAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var normalized = BinanceSpotOrderRequest.NormalizeSymbol(symbol);
        var now = timeProvider.GetUtcNow();
        lock (snapshots)
        {
            if (snapshots.TryGetValue(normalized, out var snapshot) && snapshot.ExpiresAtUtc > now)
            {
                return snapshot.Rules;
            }
        }

        return await RefreshAsync(normalized, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BinanceSymbolTradingRules> RefreshAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var normalized = BinanceSpotOrderRequest.NormalizeSymbol(symbol);
        ThrowIfDisposed();
        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = timeProvider.GetUtcNow();
            BinanceSymbolTradingRules rules;
            try
            {
                rules = await marketClient.GetTradingRulesAsync(normalized, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new BinanceTradingRulesUnavailableException(normalized, exception);
            }

            var snapshot = new BinanceSymbolTradingRulesSnapshot(rules, now, now.Add(freshness));
            lock (snapshots)
            {
                snapshots[normalized] = snapshot;
            }

            return rules;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        refreshGate.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}

public sealed class BinanceTradingRulesUnavailableException : IOException
{
    public BinanceTradingRulesUnavailableException()
        : this(string.Empty, new IOException("Fresh Binance trading rules are unavailable."))
    {
    }

    public BinanceTradingRulesUnavailableException(string message)
        : base(message)
    {
        Symbol = string.Empty;
    }

    public BinanceTradingRulesUnavailableException(string symbol, Exception innerException)
        : base($"Fresh Binance trading rules are unavailable for symbol '{symbol}'; pre-trade validation must fail closed.", innerException)
    {
        Symbol = symbol;
    }

    public string Symbol { get; }
}
