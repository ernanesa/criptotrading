using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace CryptoTrading.Exchange.Binance;

/// <summary>One connection/reader; payload ownership expires at the next advance or disposal.</summary>
internal sealed class PooledWebSocketTransport : IBinanceWebSocketTransport
{
    private const int ChunkSize = 16 * 1024;
    private readonly Func<Uri, CancellationToken, Task<WebSocket>> connect;
    private readonly Channel<Lease> channel;
    private readonly ArrayPool<byte> pool;
    private readonly CancellationTokenSource stop = new();
    private readonly Lock gate = new();
    private WebSocket? socket;
    private Task? pump;
    private Task? disposal;
    private Lease? current;
    private ExceptionDispatchInfo? failure;
    private bool stopping;
    private int enumerated;

    internal PooledWebSocketTransport(Func<Uri, CancellationToken, Task<WebSocket>> connect,
        int capacity = 32, ArrayPool<byte>? pool = null)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.connect = connect;
        this.pool = pool ?? ArrayPool<byte>.Shared;
        channel = Channel.CreateBounded<Lease>(new BoundedChannelOptions(capacity)
        {
            SingleWriter = true, SingleReader = false, FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    internal static async Task<WebSocket> ConnectClientAsync(Uri uri, CancellationToken token)
    {
        var client = new ClientWebSocket();
        client.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        client.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
        try { await client.ConnectAsync(uri, token).ConfigureAwait(false); return client; }
        catch { client.Dispose(); throw; }
    }

    [SuppressMessage("Reliability", "CA2025:Do not pass IDisposable instances into unawaited tasks", Justification = "The receive task owns the socket until DisposeCoreAsync awaits it before disposing the socket.")]
    public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        var connected = await connect(uri, cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            if (stopping || socket is not null)
            {
                connected.Dispose();
                throw new InvalidOperationException("Transport is stopped or already connected.");
            }
            socket = connected;
            pump = PumpAsync(connected);
        }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        (socket ?? throw new InvalidOperationException("Transport is not connected."))
            .SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref enumerated, 1) != 0) throw new InvalidOperationException("Only one message enumerator is supported.");
        if (socket is null) throw new InvalidOperationException("Transport is not connected.");
        using var registration = cancellationToken.Register(() => Fail(new OperationCanceledException(cancellationToken)));
        try
        {
            while (true)
            {
                Lease? next;
                lock (gate)
                {
                    current?.Dispose();
                    current = null;
                    failure?.Throw();
                    if (channel.Reader.TryRead(out next)) current = next;
                }
                if (next is not null)
                {
                    yield return next.Memory;
                    continue;
                }
                bool available;
                try { available = await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false); }
                catch
                {
                    lock (gate) failure?.Throw();
                    throw;
                }
                lock (gate) failure?.Throw();
                if (!available) yield break;
            }
        }
        finally { await DisposeAsync().ConfigureAwait(false); }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The dedicated producer publishes every terminal failure to the reader, which rethrows it; otherwise a waiting reader could hang.")]
    private async Task PumpAsync(WebSocket connected)
    {
        byte[]? scratch = null, message = null;
        var length = 0;
        try
        {
            scratch = pool.Rent(ChunkSize);
            while (true)
            {
                var result = await connected.ReceiveAsync(scratch.AsMemory(0, ChunkSize), stop.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (length != 0) throw new IOException("WebSocket closed during a message.");
                    channel.Writer.TryComplete();
                    return;
                }
                if (result.MessageType != WebSocketMessageType.Text) throw new IOException("Unexpected WebSocket message type.");
                BinanceWebSocketLimits.EnsureWithinLimit(length, result.Count);
                var required = length + result.Count;
                if (message is null || required > message.Length)
                {
                    var size = Math.Max(1, Math.Max(required, Math.Min(BinanceWebSocketLimits.MaxMessageBytes,
                        message is null ? 1024 : message.Length * 2)));
                    var replacement = pool.Rent(size);
                    if (message is not null)
                    {
                        message.AsSpan(0, length).CopyTo(replacement);
                        pool.Return(message, clearArray: true);
                    }
                    message = replacement;
                }
                scratch.AsSpan(0, result.Count).CopyTo(message.AsSpan(length));
                length = required;
                if (!result.EndOfMessage) continue;
                Lease? lease = new(pool, message, length);
                message = null;
                length = 0;
                try
                {
                    lock (gate)
                    {
                        if (failure is not null || stopping) return;
                        if (channel.Writer.TryWrite(lease)) { lease = null; continue; }
                    }
                }
                finally { lease?.Dispose(); }
                Fail(new IOException("WebSocket message queue capacity exceeded."));
                return;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) { Fail(error is IOException or WebSocketException ? error : new IOException("WebSocket receive failed.", error)); }
        finally
        {
            if (message is not null) pool.Return(message, clearArray: true);
            if (scratch is not null) pool.Return(scratch, clearArray: true);
        }
    }

    private void Fail(Exception error)
    {
        lock (gate)
        {
            if (failure is not null || stopping) return;
            failure = ExceptionDispatchInfo.Capture(error);
            channel.Writer.TryComplete(error);
            while (channel.Reader.TryRead(out var queued)) queued.Dispose();
        }
        try { socket?.Abort(); }
        catch (ObjectDisposedException) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate) return new(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Task? pending;
        lock (gate)
        {
            stopping = true;
            channel.Writer.TryComplete();
            current?.Dispose();
            current = null;
            while (channel.Reader.TryRead(out var queued)) queued.Dispose();
            pending = pump;
        }
        await stop.CancelAsync().ConfigureAwait(false);
        try { socket?.Abort(); }
        catch (ObjectDisposedException) { }
        if (pending is not null) await pending.ConfigureAwait(false);
        socket?.Dispose();
        stop.Dispose();
    }

    private sealed class Lease(ArrayPool<byte> pool, byte[] buffer, int length) : IDisposable
    {
        private byte[]? buffer = buffer;
        public ReadOnlyMemory<byte> Memory => (buffer ?? throw new ObjectDisposedException(nameof(Lease))).AsMemory(0, length);
        public void Dispose()
        {
            var returned = Interlocked.Exchange(ref buffer, null);
            if (returned is not null) pool.Return(returned, clearArray: true);
        }
    }
}
