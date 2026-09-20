using System.Net.WebSockets;
using Xunit;

namespace CryptoTrading.Exchange.Binance.Tests;

public sealed class BinanceEndpointPolicyTests
{
    [Fact]
    public void RestEndpointsAcceptOnlyAllowlistedHttpsHostPathAndDefaultPort()
    {
        var valid = new BinanceClientOptions
        {
            Environment = BinanceEnvironment.Testnet,
            RestBaseAddress = new Uri("https://testnet.binance.vision:443/api/")
        };

        valid.Validate();

        foreach (var value in new[]
        {
            "http://testnet.binance.vision/api/",
            "https://api.binance.com/api/",
            "https://testnet.binance.vision:444/api/",
            "https://user@testnet.binance.vision/api/",
            "https://testnet.binance.vision/api/?redirect=evil",
            "https://testnet.binance.vision/api/#fragment",
            "https://testnet.binance.vision/private/"
        })
        {
            var options = new BinanceClientOptions { RestBaseAddress = new Uri(value) };
            Assert.Throws<InvalidOperationException>(() => options.Validate());
        }
    }

    [Fact]
    public void MarketWebSocketRejectsWsProductionHostPortUserInfoQueryAndWrongPath()
    {
        foreach (var value in new[]
        {
            "ws://stream.testnet.binance.vision/stream",
            "wss://stream.binance.com/stream",
            "wss://stream.testnet.binance.vision:444/stream",
            "wss://user@stream.testnet.binance.vision/stream",
            "wss://stream.testnet.binance.vision/stream?streams=btcusdt@trade",
            "wss://stream.testnet.binance.vision/stream#fragment",
            "wss://stream.testnet.binance.vision/ws"
        })
        {
            var options = new BinanceMarketDataOptions { WebSocketBaseAddress = new Uri(value) };
            Assert.Throws<InvalidOperationException>(() => options.Validate());
        }

        var valid = new BinanceMarketDataOptions
        {
            WebSocketBaseAddress = new Uri("wss://stream.testnet.binance.vision:443/stream/")
        };
        valid.Validate();
    }

    [Fact]
    public void AuthenticatedWebSocketRejectsUnsafeUriComponents()
    {
        foreach (var value in new[]
        {
            "ws://ws-api.testnet.binance.vision/ws-api/v3",
            "wss://ws-api.binance.com/ws-api/v3",
            "wss://ws-api.testnet.binance.vision:444/ws-api/v3",
            "wss://user@ws-api.testnet.binance.vision/ws-api/v3",
            "wss://ws-api.testnet.binance.vision/ws-api/v3?x=1",
            "wss://ws-api.testnet.binance.vision/ws-api/v2"
        })
        {
            var options = new BinanceUserDataStreamApiOptions
            {
                ApiKey = "test-key",
                ApiSecret = "test-secret",
                WebSocketApiBaseAddress = new Uri(value)
            };
            Assert.Throws<InvalidOperationException>(() => options.Validate());
        }

        var valid = new BinanceUserDataStreamApiOptions
        {
            ApiKey = "test-key",
            ApiSecret = "test-secret",
            WebSocketApiBaseAddress = new Uri("wss://ws-api.testnet.binance.vision:443/ws-api/v3")
        };
        valid.Validate();
    }

    [Fact]
    public void FragmentedWebSocketMessageCannotExceedOneMegabyte()
    {
        long accumulated = 0;
        const int fragmentSize = 64 * 1024;
        while (accumulated < BinanceWebSocketLimits.MaxMessageBytes)
        {
            var next = (int)Math.Min(fragmentSize, BinanceWebSocketLimits.MaxMessageBytes - accumulated);
            BinanceWebSocketLimits.EnsureWithinLimit(accumulated, next);
            accumulated += next;
        }

        Assert.Throws<WebSocketException>(() =>
            BinanceWebSocketLimits.EnsureWithinLimit(accumulated, 1));
    }
}
