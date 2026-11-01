using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Xunit;

namespace CryptoTrading.Exchange.Binance.Tests;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025:Do not pass IDisposable instances into unawaited tasks", Justification = "Connection delegates transfer socket ownership to the transport; every test awaits reader and transport disposal before leaving socket scope.")]
public sealed class PooledWebSocketTransportTests
{
    [Fact]
    public async Task FragmentedMessagesKeepBorrowedCurrentStableUntilAdvanceAndReturnEveryBuffer()
    {
        using var socket = new Socket();
        var pool = new TrackedPool();
        var transport = new PooledWebSocketTransport((_, _) => Task.FromResult<WebSocket>(socket), 2, pool);
        await transport.ConnectAsync(new Uri("wss://stream.binance.com/stream"), TestContext.Current.CancellationToken);
        var reader = transport.ReadMessagesAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        try
        {
            await socket.DeliverAsync("hel", false);
            await socket.DeliverAsync("lo", true);
            Assert.True(await reader.MoveNextAsync());
            var borrowed = reader.Current;
            Assert.Equal("hello", Encoding.UTF8.GetString(borrowed.Span));
            await socket.DeliverAsync("next", true);
            Assert.Equal("hello", Encoding.UTF8.GetString(borrowed.Span));
            Assert.True(await reader.MoveNextAsync());
            Assert.Equal("next", Encoding.UTF8.GetString(reader.Current.Span));
        }
        finally
        {
            await reader.DisposeAsync().ConfigureAwait(true);
            await transport.DisposeAsync().ConfigureAwait(true);
        }
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task OverflowIsTerminalAndDoesNotReuseTheCurrentPayloadPrematurely()
    {
        using var socket = new Socket();
        var pool = new TrackedPool();
        var transport = new PooledWebSocketTransport((_, _) => Task.FromResult<WebSocket>(socket), 1, pool);
        await transport.ConnectAsync(new Uri("wss://stream.binance.com/stream"), TestContext.Current.CancellationToken);
        var reader = transport.ReadMessagesAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        try
        {
            await socket.DeliverAsync("current", true);
            Assert.True(await reader.MoveNextAsync());
            var borrowed = reader.Current;
            await socket.DeliverAsync("queued", true);
            await socket.DeliverAsync("overflow", true);
            await socket.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal("current", Encoding.UTF8.GetString(borrowed.Span));
            await Assert.ThrowsAsync<IOException>(() => reader.MoveNextAsync().AsTask());
        }
        finally
        {
            await reader.DisposeAsync().ConfigureAwait(true);
            await transport.DisposeAsync().ConfigureAwait(true);
        }
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task CancellationUnblocksReceiveAndRepeatedDisposeIsSafe()
    {
        using var socket = new Socket();
        var pool = new TrackedPool();
        var transport = new PooledWebSocketTransport((_, _) => Task.FromResult<WebSocket>(socket), 1, pool);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await transport.ConnectAsync(new Uri("wss://stream.binance.com/stream"), cancel.Token);
        var reader = transport.ReadMessagesAsync(cancel.Token).GetAsyncEnumerator(cancel.Token);
        var pending = reader.MoveNextAsync().AsTask();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await reader.DisposeAsync().ConfigureAwait(true);
        await Task.WhenAll(transport.DisposeAsync().AsTask(), transport.DisposeAsync().AsTask());
        Assert.Equal(0, pool.Outstanding);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogicalOneMiBLimitIsEnforcedAcrossFragments(bool overflow)
    {
        using var socket = new Socket();
        var pool = new TrackedPool();
        var transport = new PooledWebSocketTransport((_, _) => Task.FromResult<WebSocket>(socket), 2, pool);
        await transport.ConnectAsync(new Uri("wss://stream.binance.com/stream"), TestContext.Current.CancellationToken);
        var reader = transport.ReadMessagesAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        try
        {
            for (var i = 0; i < 64; i++) await socket.DeliverAsync(new byte[16 * 1024], !overflow && i == 63);
            if (overflow)
            {
                await socket.DeliverAsync(new byte[1], true);
                await Assert.ThrowsAsync<WebSocketException>(() => reader.MoveNextAsync().AsTask());
            }
            else
            {
                Assert.True(await reader.MoveNextAsync());
                Assert.Equal(1_048_576, reader.Current.Length);
            }
        }
        finally
        {
            await reader.DisposeAsync().ConfigureAwait(true);
            await transport.DisposeAsync().ConfigureAwait(true);
        }
        Assert.Equal(0, pool.Outstanding);
    }

    [Theory]
    [InlineData(WebSocketMessageType.Binary, false)]
    [InlineData(WebSocketMessageType.Close, false)]
    [InlineData(WebSocketMessageType.Close, true)]
    public async Task UnexpectedTypeOrCloseInsideMessageFailsAndReleasesThePartialBuffer(WebSocketMessageType type, bool emptyFragment)
    {
        using var socket = new Socket();
        var pool = new TrackedPool();
        var transport = new PooledWebSocketTransport((_, _) => Task.FromResult<WebSocket>(socket), 2, pool);
        await transport.ConnectAsync(new Uri("wss://stream.binance.com/stream"), TestContext.Current.CancellationToken);
        var reader = transport.ReadMessagesAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        try
        {
            await socket.DeliverAsync(emptyFragment ? [] : Encoding.UTF8.GetBytes("partial"), false);
            await socket.DeliverAsync([], true, type);
            await Assert.ThrowsAsync<IOException>(() => reader.MoveNextAsync().AsTask());
        }
        finally
        {
            await reader.DisposeAsync().ConfigureAwait(true);
            await transport.DisposeAsync().ConfigureAwait(true);
        }
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task DisposeCancelsAndAwaitsAnInFlightConnection()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var transport = new PooledWebSocketTransport(async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false); }
            finally { ended.TrySetResult(); }
            throw new InvalidOperationException("Unreachable connection completion.");
        });
        var connecting = transport.ConnectAsync(new Uri("wss://stream.binance.com/stream"), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        try
        {
            await transport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(ended.Task.IsCompleted);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await connecting.ConfigureAwait(true); } catch (OperationCanceledException) { }
            await transport.DisposeAsync().ConfigureAwait(true);
        }
    }

    private sealed class TrackedPool : ArrayPool<byte>
    {
        private readonly ConcurrentDictionary<byte[], byte> rented = new(ReferenceEqualityComparer.Instance);
        public int Outstanding => rented.Count;
        public override byte[] Rent(int minimumLength)
        {
            var value = new byte[minimumLength];
            Assert.True(rented.TryAdd(value, 0));
            return value;
        }
        public override void Return(byte[] array, bool clearArray = false)
        {
            Assert.True(rented.TryRemove(array, out _), "Buffer was returned twice or was not owned.");
            if (clearArray) Array.Clear(array);
        }
    }

    private sealed class Socket : WebSocket
    {
        private readonly Channel<(byte[] Data, bool End, WebSocketMessageType Type, TaskCompletionSource Copied)> frames = Channel.CreateUnbounded<(byte[], bool, WebSocketMessageType, TaskCompletionSource)>();
        private readonly CancellationTokenSource aborted = new();
        public TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => aborted.IsCancellationRequested ? WebSocketState.Aborted : WebSocketState.Open;
        public override string? SubProtocol => null;
        public Task DeliverAsync(string text, bool end) => DeliverAsync(Encoding.UTF8.GetBytes(text), end);
        public async Task DeliverAsync(byte[] data, bool end, WebSocketMessageType type = WebSocketMessageType.Text)
        {
            var copied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await frames.Writer.WriteAsync((data, end, type, copied), TestContext.Current.CancellationToken).ConfigureAwait(false);
            await copied.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, aborted.Token);
            var frame = await frames.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
            frame.Data.CopyTo(buffer);
            frame.Copied.TrySetResult();
            return new(frame.Data.Length, frame.Type, frame.End);
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var result = await ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            return new(result.Count, result.MessageType, result.EndOfMessage);
        }
        public override void Abort() { aborted.Cancel(); Aborted.TrySetResult(); }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() => aborted.Dispose();
    }
}
