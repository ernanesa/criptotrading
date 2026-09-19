using System.Net.WebSockets;

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
            try
            {
                try
                {
                    await transport.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
                    connected = true;
                    attempt = 0;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }
                catch (WebSocketException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Reconnect below with bounded exponential backoff.
                }
                catch (IOException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Reconnect below with bounded exponential backoff.
                }

                if (connected)
                {
                    await foreach (var item in transport.ReadMessagesAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var parsed = BinanceMarketDataParser.TryParse(item.Span, timeProvider.GetUtcNow());
                        if (parsed is null)
                        {
                            // Invalid payloads are quarantined by omission; one bad event must not tear down a healthy feed.
                            continue;
                        }

                        yield return parsed;
                    }
                }
            }
            finally
            {
                await transport.DisposeAsync().ConfigureAwait(false);
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

    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadMessagesAsync(CancellationToken cancellationToken);
}

internal sealed class ClientWebSocketTransport : IBinanceWebSocketTransport
{
    private readonly ClientWebSocket client = new();

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => client.ConnectAsync(uri, cancellationToken);

    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        client.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadMessagesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (client.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await client.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                yield break;
            }

            await message.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
            if (!result.EndOfMessage)
            {
                continue;
            }

            yield return message.ToArray();
            message.SetLength(0);
        }
    }

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }
}
