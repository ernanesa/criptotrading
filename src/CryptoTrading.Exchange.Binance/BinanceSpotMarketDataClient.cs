using System.Net.WebSockets;
using CryptoTrading.Observability;

namespace CryptoTrading.Exchange.Binance;

public interface IBinanceSpotMarketDataClient
{
    IAsyncEnumerable<BinanceMarketDataEvent> ReadAsync(
        IEnumerable<BinanceMarketSubscription> subscriptions,
        CancellationToken cancellationToken = default);
}

public sealed class BinanceSpotMarketDataClient : IBinanceSpotMarketDataClient
{
    private readonly BinanceMarketDataOptions options;
    private readonly Func<IBinanceWebSocketTransport> transportFactory;
    private readonly TimeProvider timeProvider;

    public BinanceSpotMarketDataClient(
        BinanceMarketDataOptions options,
        Func<IBinanceWebSocketTransport>? transportFactory = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options;
        this.transportFactory = transportFactory ?? (() => new ClientWebSocketTransport());
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async IAsyncEnumerable<BinanceMarketDataEvent> ReadAsync(
        IEnumerable<BinanceMarketSubscription> subscriptions,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        var streamNames = subscriptions.Select(subscription => subscription.ToStreamName()).Distinct(StringComparer.Ordinal).ToArray();
        if (streamNames.Length is 0)
        {
            throw new ArgumentException("At least one market data subscription is required.", nameof(subscriptions));
        }

        var uri = BuildStreamUri(options.GetWebSocketBaseAddress(), streamNames);
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var transport = transportFactory();
            var connected = false;
            var receivedValidEvent = false;
            var disconnectReason = BinanceMarketDisconnectReason.TransportClosed;
            try
            {
                try
                {
                    await transport.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
                    connected = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    disconnectReason = BinanceMarketDisconnectReason.ReadFailure;
                }
                catch (WebSocketException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Reconnect below with bounded exponential backoff.
                    disconnectReason = BinanceMarketDisconnectReason.ReadFailure;
                }
                catch (IOException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Reconnect below with bounded exponential backoff.
                    disconnectReason = BinanceMarketDisconnectReason.ReadFailure;
                }

                if (connected)
                {
                    using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var enumerator = transport.ReadMessagesAsync(sessionCts.Token)
                        .GetAsyncEnumerator(sessionCts.Token);
                    try
                    {
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            ReadOnlyMemory<byte> item;
                            Task<bool>? moveNextTask = null;
                            try
                            {
                                moveNextTask = enumerator.MoveNextAsync().AsTask();
                                var hasNext = options.StreamReadTimeout > TimeSpan.Zero
                                    ? await moveNextTask.WaitAsync(options.StreamReadTimeout, timeProvider, cancellationToken).ConfigureAwait(false)
                                    : await moveNextTask.ConfigureAwait(false);
                                if (!hasNext)
                                {
                                    break;
                                }

                                item = enumerator.Current;
                            }
                            catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
                            {
                                // Zombie stream detected: socket remained open but no message arrived within StreamReadTimeout.
                                disconnectReason = BinanceMarketDisconnectReason.StreamTimeout;
                                await sessionCts.CancelAsync().ConfigureAwait(false);
                                if (moveNextTask is not null)
                                {
                                    try
                                    {
                                        await moveNextTask.WaitAsync(TimeSpan.FromSeconds(3), timeProvider, CancellationToken.None).ConfigureAwait(false);
                                    }
                                    catch (OperationCanceledException)
                                    {
                                    }
                                    catch (TimeoutException)
                                    {
                                    }
#pragma warning disable CA1031 // Aborting a timed out stream read can throw any transport-specific exception; we must not leak it during cleanup.
                                    catch (Exception)
                                    {
                                    }
#pragma warning restore CA1031
                                }
                                break;
                            }
                            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                            {
                                // A transport-side cancellation (for example a session watchdog)
                                // is a reconnectable failure, not a caller shutdown.
                                disconnectReason = BinanceMarketDisconnectReason.ReadFailure;
                                break;
                            }
                            catch (WebSocketException) when (!cancellationToken.IsCancellationRequested)
                            {
                                // Reconnect after an established socket fails while reading.
                                disconnectReason = BinanceMarketDisconnectReason.ReadFailure;
                                break;
                            }
                            catch (IOException) when (!cancellationToken.IsCancellationRequested)
                            {
                                // Reconnect after a receive/read I/O failure.
                                disconnectReason = BinanceMarketDisconnectReason.ReadFailure;
                                break;
                            }

                            var parsed = BinanceMarketDataParser.TryParse(item, timeProvider.GetUtcNow());
                            if (parsed is BinanceMarketServerShutdownEvent)
                            {
                                // Close this session and reconnect using the existing bounded backoff.
                                disconnectReason = BinanceMarketDisconnectReason.ServerShutdown;
                                break;
                            }
                            if (parsed is null)
                            {
                                // One bad event must not tear down a healthy feed, but it must remain observable.
                                TradingTelemetry.RecordInvalidMarketDataPayload();
                                continue;
                            }

                            // A valid payload proves that this session is healthy. The
                            // next reconnect may therefore start at the initial delay.
                            receivedValidEvent = true;
                            yield return parsed;
                        }

                        if (!cancellationToken.IsCancellationRequested)
                        {
                            yield return new BinanceMarketStreamDisconnectedEvent(timeProvider.GetUtcNow(), disconnectReason);
                        }
                    }
                    finally
                    {
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    }
                }
                else if (!cancellationToken.IsCancellationRequested)
                {
                    yield return new BinanceMarketStreamDisconnectedEvent(timeProvider.GetUtcNow(), disconnectReason);
                }
            }
            finally
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }

            if (receivedValidEvent)
            {
                attempt = 0;
            }

            if (options.MaxReconnectAttempts > 0 && attempt >= options.MaxReconnectAttempts)
            {
                yield break;
            }

            var delay = ComputeBackoff(options.InitialReconnectDelay, options.MaxReconnectDelay, attempt);
            attempt++;
            await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static Uri BuildStreamUri(Uri baseAddress, IReadOnlyCollection<string> streamNames)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        if (streamNames.Count is 0)
        {
            throw new ArgumentException("At least one stream name is required.", nameof(streamNames));
        }
        var separator = baseAddress.Query.Length is 0 ? "?" : "&";
        var streams = string.Join('/', streamNames.Select(Uri.EscapeDataString));
        return new Uri($"{baseAddress}{separator}streams={streams}", UriKind.Absolute);
    }

    internal static TimeSpan ComputeBackoff(TimeSpan initial, TimeSpan maximum, int attempt)
    {
        var multiplier = Math.Min(attempt, 30);
        var ticks = initial.Ticks > maximum.Ticks / (1L << Math.Min(multiplier, 30))
            ? maximum.Ticks
            : initial.Ticks * (1L << multiplier);
        return TimeSpan.FromTicks(Math.Min(ticks, maximum.Ticks));
    }
}

public interface IBinanceWebSocketTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This transport is receive-only.");

    /// <summary>Current payload is borrowed until the next advance or reader/transport disposal; parse or copy it before then.</summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadMessagesAsync(CancellationToken cancellationToken);
}

internal sealed class ClientWebSocketTransport : IBinanceWebSocketTransport
{
    private readonly PooledWebSocketTransport transport = new(PooledWebSocketTransport.ConnectClientAsync);

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => transport.ConnectAsync(uri, cancellationToken);

    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        transport.SendAsync(payload, cancellationToken);

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadMessagesAsync(CancellationToken cancellationToken) => transport.ReadMessagesAsync(cancellationToken);

    public ValueTask DisposeAsync() => transport.DisposeAsync();
}
