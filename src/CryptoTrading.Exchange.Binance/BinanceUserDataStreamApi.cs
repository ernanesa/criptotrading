using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CryptoTrading.Exchange.Binance;

/// <summary>Modern Spot WebSocket API options for HMAC signature subscriptions.</summary>
public sealed class BinanceUserDataStreamApiOptions
{
    public BinanceEnvironment Environment { get; set; } = BinanceEnvironment.Testnet;
    public Uri? WebSocketApiBaseAddress { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    public long RecvWindowMilliseconds { get; set; } = 5_000;
    public TimeSpan InitialReconnectDelay { get; set; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(10);
    public int MaxReconnectAttempts { get; set; } = 10;

    internal Uri GetWebSocketApiBaseAddress()
    {
        var expectedHost = Environment switch
        {
            BinanceEnvironment.Testnet => "ws-api.testnet.binance.vision",
            BinanceEnvironment.Demo => "demo-ws-api.binance.com",
            _ => throw new ArgumentOutOfRangeException(nameof(Environment), Environment, "Unsupported Binance environment.")
        };
        var defaultPath = "/ws-api/v3";
        var address = WebSocketApiBaseAddress ?? new Uri($"wss://{expectedHost}{defaultPath}");
        if (!address.IsAbsoluteUri || address.Scheme != "wss" ||
            !string.Equals(address.Host, expectedHost, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(address.AbsolutePath, defaultPath, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(address.Query) || !string.IsNullOrEmpty(address.Fragment))
        {
            throw new InvalidOperationException($"Binance WebSocket API address must be the allowlisted {Environment} endpoint.");
        }

        return address;
    }

    internal void Validate()
    {
        _ = GetWebSocketApiBaseAddress();
        if (string.IsNullOrWhiteSpace(ApiKey) || ApiKey.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Binance API key is required and cannot contain whitespace.", nameof(ApiKey));
        }

        if (string.IsNullOrWhiteSpace(ApiSecret) || ApiSecret.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Binance API secret is required and cannot contain whitespace.", nameof(ApiSecret));
        }

        if (RecvWindowMilliseconds is < 1 or > 60_000)
        {
            throw new ArgumentOutOfRangeException(nameof(RecvWindowMilliseconds));
        }

        if (InitialReconnectDelay <= TimeSpan.Zero || MaxReconnectDelay < InitialReconnectDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialReconnectDelay));
        }

        if (MaxReconnectAttempts is < 0 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxReconnectAttempts));
        }
    }
}

/// <summary>Control event emitted by the modern User Data Stream session.</summary>
public sealed record BinanceUserDataStreamControlEvent(
    DateTimeOffset ReceivedAtUtc,
    long EventTimeMilliseconds,
    string EventType,
    long? SubscriptionId) : BinanceUserDataEvent(ReceivedAtUtc, EventTimeMilliseconds);

/// <summary>
/// Modern WebSocket API client using HMAC `userDataStream.subscribe.signature`.
/// It deliberately has no trading methods and emits server/session lifecycle events.
/// </summary>
public sealed class BinanceUserDataStreamApiClient : IBinanceUserDataStreamClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly BinanceUserDataStreamApiOptions options;
    private readonly Func<IBinanceWebSocketTransport> transportFactory;
    private readonly TimeProvider timeProvider;

    public BinanceUserDataStreamApiClient(
        BinanceUserDataStreamApiOptions options,
        Func<IBinanceWebSocketTransport>? transportFactory = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options;
        transportFactory = transportFactory ?? (() => new ClientWebSocketTransport());
        this.transportFactory = transportFactory;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async IAsyncEnumerable<BinanceUserDataEvent> ReadAsync(
        string listenKey,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
#pragma warning disable CA2007
            await using var transport = transportFactory();
#pragma warning restore CA2007
            IAsyncEnumerator<ReadOnlyMemory<byte>>? enumerator = null;
            var subscriptionSucceeded = false;
            var receivedEvent = false;
            try
            {
                await transport.ConnectAsync(options.GetWebSocketApiBaseAddress(), cancellationToken).ConfigureAwait(false);
                enumerator = transport.ReadMessagesAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
                await SubscribeAsync(transport, enumerator, cancellationToken).ConfigureAwait(false);
                subscriptionSucceeded = true;
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
            catch (BinanceUserDataStreamProtocolException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            if (subscriptionSucceeded && enumerator is not null)
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    bool hasMessage;
                    try
                    {
                        hasMessage = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        yield break;
                    }
                    catch (WebSocketException) when (!cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (IOException) when (!cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (!hasMessage)
                    {
                        break;
                    }

                    var parsed = ParseMessage(enumerator.Current.Span, timeProvider.GetUtcNow());
                    if (parsed is null)
                    {
                        continue;
                    }

                    yield return parsed;
                    receivedEvent = true;
                    if (parsed is BinanceUserDataStreamControlEvent control &&
                        control.EventType is "eventStreamTerminated" or "serverShutdown")
                    {
                        break;
                    }
                }
            }

            if (enumerator is not null)
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            attempt = receivedEvent ? 0 : attempt + 1;
            if (options.MaxReconnectAttempts > 0 && attempt >= options.MaxReconnectAttempts)
            {
                yield break;
            }

            await Task.Delay(BinanceUserDataStreamClient.ComputeBackoff(
                options.InitialReconnectDelay, options.MaxReconnectDelay, attempt), timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal static string BuildSubscriptionRequest(
        string apiKey,
        string apiSecret,
        long timestampMilliseconds,
        long recvWindowMilliseconds,
        Guid requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiSecret);
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["apiKey"] = apiKey,
            ["recvWindow"] = recvWindowMilliseconds.ToString(CultureInfo.InvariantCulture),
            ["timestamp"] = timestampMilliseconds.ToString(CultureInfo.InvariantCulture)
        };
        var payload = string.Join('&', parameters.Select(pair => $"{pair.Key}={pair.Value}"));
        var signature = Sign(payload, apiSecret);
        return JsonSerializer.Serialize(new
        {
            id = requestId,
            method = "userDataStream.subscribe.signature",
            @params = new
            {
                apiKey,
                recvWindow = recvWindowMilliseconds,
                timestamp = timestampMilliseconds,
                signature
            }
        }, JsonOptions);
    }

    private async Task SubscribeAsync(
        IBinanceWebSocketTransport transport,
        IAsyncEnumerator<ReadOnlyMemory<byte>> enumerator,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid();
        var request = BuildSubscriptionRequest(
            options.ApiKey,
            options.ApiSecret,
            timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            options.RecvWindowMilliseconds,
            requestId);
        await transport.SendAsync(Encoding.UTF8.GetBytes(request), cancellationToken).ConfigureAwait(false);
        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            throw new BinanceUserDataStreamProtocolException("WebSocket API closed before subscription acknowledgement.");
        }

        using var document = JsonDocument.Parse(enumerator.Current.ToArray());
        var root = document.RootElement;
        if (!root.TryGetProperty("id", out var responseId) ||
            !string.Equals(responseId.GetString(), requestId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new BinanceUserDataStreamProtocolException("WebSocket API returned an unexpected subscription response.");
        }

        var status = root.GetProperty("status").GetInt32();
        if (status != 200)
        {
            throw new BinanceUserDataStreamProtocolException($"WebSocket API subscription failed with status {status}.");
        }
    }

    private static BinanceUserDataEvent? ParseMessage(ReadOnlySpan<byte> payload, DateTimeOffset receivedAtUtc)
    {
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray());
            var root = document.RootElement;
            if (!root.TryGetProperty("event", out var eventRoot))
            {
                return null;
            }

            var eventType = eventRoot.GetProperty("e").GetString();
            var eventTime = eventRoot.GetProperty("E").GetInt64();
            var subscriptionId = root.TryGetProperty("subscriptionId", out var subscription)
                ? subscription.GetInt64()
                : (long?)null;
            if (eventType is "eventStreamTerminated" or "serverShutdown")
            {
                return new BinanceUserDataStreamControlEvent(receivedAtUtc, eventTime, eventType, subscriptionId);
            }

            return BinanceUserDataParser.TryParse(payload, receivedAtUtc);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException)
        {
            return null;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308", Justification = "Binance accepts lowercase HMAC hex signatures.")]
    private static string Sign(string payload, string secret)
    {
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            return Convert.ToHexString(HMACSHA256.HashData(secretBytes, payloadBytes)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadBytes);
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }
}

public sealed class BinanceUserDataStreamProtocolException : InvalidOperationException
{
    public BinanceUserDataStreamProtocolException() { }

    public BinanceUserDataStreamProtocolException(string message)
        : base(message) { }

    public BinanceUserDataStreamProtocolException(string message, Exception innerException)
        : base(message, innerException) { }
}
