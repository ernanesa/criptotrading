using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;

namespace CryptoTrading.Exchange.Binance;

public sealed class BinanceUserDataStreamOptions
{
    public BinanceEnvironment Environment { get; set; } = BinanceEnvironment.Testnet;

    public Uri? WebSocketBaseAddress { get; set; }

    public TimeSpan InitialReconnectDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(10);

    public int MaxReconnectAttempts { get; set; } = 10;

    internal Uri GetWebSocketBaseAddress()
    {
        var expectedHost = Environment switch
        {
            BinanceEnvironment.Testnet => "stream.testnet.binance.vision",
            BinanceEnvironment.Demo => "stream.binance.com",
            _ => throw new ArgumentOutOfRangeException(nameof(Environment), Environment, "Unsupported Binance environment.")
        };
        var address = WebSocketBaseAddress ?? new Uri($"wss://{expectedHost}/ws/");
        if (!address.IsAbsoluteUri || address.Scheme != "wss" ||
            !string.Equals(address.Host, expectedHost, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(address.Query) || !string.IsNullOrEmpty(address.Fragment))
        {
            throw new InvalidOperationException($"Binance user stream address must be the allowlisted wss {Environment} endpoint.");
        }

        return address.AbsoluteUri.EndsWith('/', StringComparison.Ordinal)
            ? address
            : new Uri(address.AbsoluteUri + "/");
    }

    internal void Validate()
    {
        _ = GetWebSocketBaseAddress();
        if (InitialReconnectDelay <= TimeSpan.Zero || MaxReconnectDelay < InitialReconnectDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialReconnectDelay), "Reconnect delays must be positive and ordered.");
        }

        if (MaxReconnectAttempts is < 0 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxReconnectAttempts), "Maximum reconnect attempts must be between zero and 1,000.");
        }
    }
}

public abstract record BinanceUserDataEvent(DateTimeOffset ReceivedAtUtc, long EventTimeMilliseconds);

public sealed record BinanceExecutionReportEvent(
    DateTimeOffset ReceivedAtUtc,
    long EventTimeMilliseconds,
    string Symbol,
    long OrderId,
    long? TradeId,
    string ClientOrderId,
    string Side,
    string OrderType,
    string ExecutionType,
    string OrderStatus,
    decimal LastExecutedQuantity,
    decimal CumulativeExecutedQuantity,
    decimal LastExecutedPrice,
    decimal Commission,
    string? CommissionAsset) : BinanceUserDataEvent(ReceivedAtUtc, EventTimeMilliseconds);

public sealed record BinanceAccountPositionBalance(string Asset, decimal Free, decimal Locked);

public sealed record BinanceOutboundAccountPositionEvent(
    DateTimeOffset ReceivedAtUtc,
    long EventTimeMilliseconds,
    long LastUpdateTimeMilliseconds,
    IReadOnlyList<BinanceAccountPositionBalance> Balances) : BinanceUserDataEvent(ReceivedAtUtc, EventTimeMilliseconds);

public interface IBinanceUserDataStreamClient
{
    IAsyncEnumerable<BinanceUserDataEvent> ReadAsync(
        string listenKey,
        CancellationToken cancellationToken = default);
}

public sealed class BinanceUserDataStreamClient : IBinanceUserDataStreamClient
{
    private readonly BinanceUserDataStreamOptions options;
    private readonly Func<IBinanceWebSocketTransport> transportFactory;
    private readonly TimeProvider timeProvider;

    public BinanceUserDataStreamClient(
        BinanceUserDataStreamOptions options,
        Func<IBinanceWebSocketTransport>? transportFactory = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options;
        this.transportFactory = transportFactory ?? (() => new UserDataClientWebSocketTransport());
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async IAsyncEnumerable<BinanceUserDataEvent> ReadAsync(
        string listenKey,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateListenKey(listenKey);
        var uri = BuildStreamUri(options.GetWebSocketBaseAddress(), listenKey);
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
                }
                catch (IOException) when (!cancellationToken.IsCancellationRequested)
                {
                }

                if (connected)
                {
                    await foreach (var payload in transport.ReadMessagesAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var parsed = BinanceUserDataParser.TryParse(payload.Span, timeProvider.GetUtcNow());
                        if (parsed is not null)
                        {
                            yield return parsed;
                        }
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

            await Task.Delay(ComputeBackoff(options.InitialReconnectDelay, options.MaxReconnectDelay, attempt), timeProvider, cancellationToken)
                .ConfigureAwait(false);
            attempt++;
        }
    }

    internal static Uri BuildStreamUri(Uri baseAddress, string listenKey)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ValidateListenKey(listenKey);
        return new Uri($"{baseAddress.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(listenKey)}", UriKind.Absolute);
    }

    internal static TimeSpan ComputeBackoff(TimeSpan initial, TimeSpan maximum, int attempt)
    {
        var multiplier = Math.Min(attempt, 30);
        var factor = 1L << multiplier;
        var ticks = initial.Ticks > maximum.Ticks / factor ? maximum.Ticks : initial.Ticks * factor;
        return TimeSpan.FromTicks(Math.Min(ticks, maximum.Ticks));
    }

    private static void ValidateListenKey(string listenKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listenKey);
        if (listenKey.Length > 512 || listenKey.Any(static character => char.IsControl(character) || character is '/' or '\\' or '?' or '#'))
        {
            throw new ArgumentException("listenKey contains unsupported characters or is too long.", nameof(listenKey));
        }
    }
}

internal static class BinanceUserDataParser
{
    public static BinanceUserDataEvent? TryParse(ReadOnlySpan<byte> payload, DateTimeOffset receivedAtUtc)
    {
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray());
            var root = document.RootElement;
            var eventRoot = root.TryGetProperty("event", out var nested) ? nested : root;
            var eventType = eventRoot.GetProperty("e").GetString();
            return eventType switch
            {
                "executionReport" => ParseExecution(eventRoot, receivedAtUtc),
                "outboundAccountPosition" => ParsePosition(eventRoot, receivedAtUtc),
                _ => null
            };
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static BinanceExecutionReportEvent ParseExecution(JsonElement root, DateTimeOffset receivedAtUtc) =>
        new(
            receivedAtUtc,
            root.GetProperty("E").GetInt64(),
            Required(root, "s"),
            root.GetProperty("i").GetInt64(),
            OptionalNonNegativeOrNull(root, "t"),
            Required(root, "c"),
            Required(root, "S"),
            Required(root, "o"),
            Required(root, "x"),
            Required(root, "X"),
            Decimal(root, "l"),
            Decimal(root, "z"),
            Decimal(root, "L"),
            Decimal(root, "n"),
            Optional(root, "N"));

    private static BinanceOutboundAccountPositionEvent ParsePosition(JsonElement root, DateTimeOffset receivedAtUtc)
    {
        var balances = root.GetProperty("B").EnumerateArray().Select(balance =>
            new BinanceAccountPositionBalance(
                Required(balance, "a"),
                Decimal(balance, "f"),
                Decimal(balance, "l"))).ToArray();
        return new(receivedAtUtc, root.GetProperty("E").GetInt64(), root.GetProperty("u").GetInt64(), balances);
    }

    private static string Required(JsonElement root, string name) =>
        root.GetProperty(name).GetString() is { Length: > 0 } value ? value : throw new JsonException($"Missing '{name}'.");

    private static string? Optional(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    private static long? OptionalNonNegativeOrNull(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var id = value.GetInt64();
        return id < 0 ? null : id;
    }

    private static decimal Decimal(JsonElement root, string name) =>
        decimal.Parse(Required(root, name), NumberStyles.Number, CultureInfo.InvariantCulture);
}

internal sealed class UserDataClientWebSocketTransport : IBinanceWebSocketTransport
{
    private readonly ClientWebSocket client = new();

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => client.ConnectAsync(uri, cancellationToken);

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
            if (result.EndOfMessage)
            {
                yield return message.ToArray();
                message.SetLength(0);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }
}
