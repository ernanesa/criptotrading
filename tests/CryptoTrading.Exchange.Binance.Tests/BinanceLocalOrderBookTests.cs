using System.Text;
using Xunit;

namespace CryptoTrading.Exchange.Binance.Tests;

public sealed class BinanceLocalOrderBookTests
{
    [Fact]
    public void DeltaBeforeSnapshotRequestsResync()
    {
        var book = new BinanceLocalOrderBook("BTCUSDT");

        var result = book.ApplyDelta(Delta(1, 1));

        Assert.Equal(BinanceOrderBookApplyDisposition.ResyncRequired, result.Disposition);
        Assert.True(result.ResyncRequired);
        Assert.Equal(BinanceOrderBookInvalidReason.NoSnapshot, result.InvalidReason);
        Assert.Single(book.Quarantined);
    }

    [Fact]
    public void FirstDeltaMayBridgeSnapshotBoundaryAndSubsequentDeltaMustBeContiguous()
    {
        var book = new BinanceLocalOrderBook("BTCUSDT");
        Assert.Equal(BinanceOrderBookApplyDisposition.SnapshotApplied, book.ApplySnapshot(Snapshot(100)).Disposition);

        var first = book.ApplyDelta(Delta(99, 101));
        var second = book.ApplyDelta(Delta(102, 102, price: 100.5m));

        Assert.Equal(BinanceOrderBookApplyDisposition.DeltaApplied, first.Disposition);
        Assert.Equal(BinanceOrderBookApplyDisposition.DeltaApplied, second.Disposition);
        Assert.Equal(102, book.LastUpdateId);
        Assert.Equal([100.5m, 100m], book.Bids.Select(level => level.Price).ToArray());
    }

    [Fact]
    public void SnapshotDoesNotProvideDepthSignalsUntilFirstDeltaBridgesIt()
    {
        var book = NewBookAt(100);

        Assert.Empty(book.Bids);
        Assert.Empty(book.Asks);
        Assert.Null(book.CalculateTopDepthImbalance());
        Assert.Null(book.CalculateTopDepthLiquidityNotional());

        Assert.Equal(BinanceOrderBookApplyDisposition.DeltaApplied, book.ApplyDelta(Delta(100, 101)).Disposition);
        Assert.NotNull(book.CalculateTopDepthImbalance());
        Assert.NotNull(book.CalculateTopDepthLiquidityNotional());
    }

    [Fact]
    public void DisconnectInvalidatesBridgedBookUntilNewSnapshotAndBridge()
    {
        var book = NewBookAt(100);
        Assert.Equal(BinanceOrderBookApplyDisposition.DeltaApplied, book.ApplyDelta(Delta(100, 101)).Disposition);
        Assert.NotNull(book.CalculateTopDepthLiquidityNotional());

        book.InvalidateForStreamDisconnect();

        Assert.Equal(BinanceOrderBookStatus.Invalid, book.Status);
        Assert.Null(book.CalculateTopDepthLiquidityNotional());
        Assert.Equal(BinanceOrderBookApplyDisposition.ResyncRequired, book.ApplyDelta(Delta(102, 102)).Disposition);
        Assert.Equal(BinanceOrderBookApplyDisposition.SnapshotApplied, book.ApplySnapshot(Snapshot(200)).Disposition);
        Assert.Null(book.CalculateTopDepthLiquidityNotional());
        Assert.Equal(BinanceOrderBookApplyDisposition.DeltaApplied, book.ApplyDelta(Delta(200, 201)).Disposition);
        Assert.NotNull(book.CalculateTopDepthLiquidityNotional());
    }

    [Theory]
    [InlineData("empty-bids")]
    [InlineData("empty-asks")]
    [InlineData("zero-bid-quantity")]
    [InlineData("zero-ask-quantity")]
    [InlineData("duplicate-bids")]
    [InlineData("duplicate-asks")]
    [InlineData("locked")]
    [InlineData("crossed")]
    public void InvalidSnapshotCannotReplaceOrExposePreviouslyBridgedBook(string scenario)
    {
        var book = NewBookAt(100);
        Assert.Equal(BinanceOrderBookApplyDisposition.DeltaApplied, book.ApplyDelta(Delta(100, 101)).Disposition);
        BinanceOrderBookLevel[] bids = scenario switch
        {
            "empty-bids" => [],
            "zero-bid-quantity" => [Level(100m, 0m)],
            "duplicate-bids" => [Level(100m, 1m), Level(100m, 2m)],
            "locked" => [Level(101m, 1m)],
            "crossed" => [Level(102m, 1m)],
            _ => [Level(100m, 1m)]
        };
        BinanceOrderBookLevel[] asks = scenario switch
        {
            "empty-asks" => [],
            "zero-ask-quantity" => [Level(101m, 0m)],
            "duplicate-asks" => [Level(101m, 1m), Level(101m, 2m)],
            _ => [Level(101m, 1m)]
        };

        var result = book.ApplySnapshot(new BinanceOrderBookSnapshot("BTCUSDT", 200, bids, asks));

        Assert.Equal(BinanceOrderBookApplyDisposition.InvalidQuarantined, result.Disposition);
        Assert.Equal(BinanceOrderBookStatus.Invalid, book.Status);
        Assert.True(book.ResyncRequired);
        Assert.Equal(101, book.LastUpdateId);
        Assert.Empty(book.Bids);
        Assert.Empty(book.Asks);
        Assert.Null(book.CalculateTopDepthImbalance());
        Assert.Null(book.CalculateTopDepthLiquidityNotional());
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("crossed")]
    [InlineData("empty-bids")]
    [InlineData("empty-asks")]
    public void DeltaCannotExposeEmptyOrCrossedBook(string scenario)
    {
        var book = NewBookAt(100);
        var bids = scenario switch
        {
            "locked" => new[] { new BinanceOrderBookLevel(101m, 1m) },
            "crossed" => [new BinanceOrderBookLevel(102m, 1m)],
            "empty-bids" => [new BinanceOrderBookLevel(100m, 0m)],
            _ => Array.Empty<BinanceOrderBookLevel>()
        };
        var asks = scenario == "empty-asks" ? new[] { new BinanceOrderBookLevel(101m, 0m) } : [];
        var delta = new BinanceDepthUpdateEvent("btcusdt@depth", "BTCUSDT", 1, DateTimeOffset.UnixEpoch, 101, 101, null, bids, asks);

        var result = book.ApplyDelta(delta);

        Assert.Equal(BinanceOrderBookApplyDisposition.InvalidQuarantined, result.Disposition);
        Assert.Equal(BinanceOrderBookInvalidReason.InvalidBookState, result.InvalidReason);
        Assert.Equal(BinanceOrderBookStatus.Invalid, book.Status);
        Assert.Equal(100, book.LastUpdateId);
        Assert.Empty(book.Bids);
        Assert.Empty(book.Asks);
        Assert.Null(book.CalculateTopDepthImbalance());
        Assert.Null(book.CalculateTopDepthLiquidityNotional());
    }

    [Fact]
    public void DuplicateIsQuarantinedWithoutInvalidatingHealthyBook()
    {
        var book = NewBookAt(100);
        book.ApplyDelta(Delta(101, 101));

        var result = book.ApplyDelta(Delta(101, 101, price: 999));

        Assert.Equal(BinanceOrderBookApplyDisposition.DuplicateQuarantined, result.Disposition);
        Assert.Equal(BinanceOrderBookStatus.Healthy, book.Status);
        Assert.False(book.ResyncRequired);
        Assert.DoesNotContain(book.Bids, level => level.Price == 999);
    }

    [Fact]
    public void GapInvalidatesBookAndRequiresFreshSnapshot()
    {
        var book = NewBookAt(100);
        var result = book.ApplyDelta(Delta(102, 102));

        Assert.Equal(BinanceOrderBookApplyDisposition.GapDetected, result.Disposition);
        Assert.Equal(BinanceOrderBookInvalidReason.SequenceGap, result.InvalidReason);
        Assert.True(book.ResyncRequired);

        Assert.Equal(BinanceOrderBookApplyDisposition.SnapshotApplied, book.ApplySnapshot(Snapshot(102)).Disposition);
        Assert.Equal(BinanceOrderBookApplyDisposition.DeltaApplied, book.ApplyDelta(Delta(103, 103)).Disposition);
        Assert.Equal(BinanceOrderBookStatus.Healthy, book.Status);
    }

    [Fact]
    public void OutOfOrderDeltaIsQuarantinedAndBookIsInvalid()
    {
        var book = NewBookAt(100);
        book.ApplyDelta(Delta(101, 102));

        var result = book.ApplyDelta(Delta(102, 103));

        Assert.Equal(BinanceOrderBookApplyDisposition.OutOfOrderQuarantined, result.Disposition);
        Assert.Equal(BinanceOrderBookInvalidReason.OutOfOrder, result.InvalidReason);
        Assert.True(book.ResyncRequired);
    }

    [Fact]
    public void BookKeepsOnlyBestConfiguredLevelsOnEachSide()
    {
        var book = new BinanceLocalOrderBook("BTCUSDT", maxLevels: 2);
        book.ApplySnapshot(new BinanceOrderBookSnapshot(
            "BTCUSDT",
            10,
            [Level(99), Level(100), Level(101)],
            [Level(102), Level(103), Level(104)]));
        book.ApplyDelta(new BinanceDepthUpdateEvent("btcusdt@depth", "BTCUSDT", 1, DateTimeOffset.UnixEpoch, 11, 11, null, [], []));

        Assert.Equal([101m, 100m], book.Bids.Select(level => level.Price).ToArray());
        Assert.Equal([102m, 103m], book.Asks.Select(level => level.Price).ToArray());
    }

    [Fact]
    public void QuarantineIsBounded()
    {
        var book = new BinanceLocalOrderBook("BTCUSDT", quarantineCapacity: 2);
        book.ApplyDelta(Delta(1, 1));
        book.ApplyDelta(Delta(2, 2));
        book.ApplyDelta(Delta(3, 3));

        Assert.Equal(2, book.Quarantined.Count);
        Assert.Equal(2, book.Quarantined.First().FirstUpdateId);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The market-data client owns the injected transport for the duration of this test.")]
    public async Task ParserMapsDepthUpdateAndInvalidPayloadIsQuarantined()
    {
        const string payload = "{\"stream\":\"btcusdt@depth@100ms\",\"data\":{\"e\":\"depthUpdate\",\"E\":1720000000123,\"s\":\"BTCUSDT\",\"U\":7,\"u\":8,\"pu\":6,\"b\":[[\"100.00\",\"1.25\"]],\"a\":[[\"101.00\",\"0.75\"]]}}";
        var transport = new DepthTransport(payload);
        var client = new BinanceSpotMarketDataClient(
            new BinanceMarketDataOptions
            {
                WebSocketBaseAddress = new Uri("wss://stream.testnet.binance.vision/stream"),
                InitialReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1)
            },
            () => transport);

        await using var enumerator = client.ReadAsync([new BinanceMarketSubscription("BTCUSDT", BinanceMarketStreamKind.Trade)], cancellationToken: TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        var update = Assert.IsType<BinanceDepthUpdateEvent>(enumerator.Current);
        Assert.Equal(7, update.FirstUpdateId);
        Assert.Equal(8, update.FinalUpdateId);
        Assert.Equal(6, update.PreviousFinalUpdateId);
        Assert.Equal(1.25m, Assert.Single(update.Bids).Quantity);
    }

    [Fact]
    public void CalculatesTopDepthImbalanceAndTemporalOfi()
    {
        var book = new BinanceLocalOrderBook("BTCUSDT");
        book.ApplySnapshot(new BinanceOrderBookSnapshot(
            "BTCUSDT",
            10,
            [new BinanceOrderBookLevel(100m, 3m), new BinanceOrderBookLevel(99m, 1m)],
            [new BinanceOrderBookLevel(101m, 1m), new BinanceOrderBookLevel(102m, 1m)]));
        book.ApplyDelta(new BinanceDepthUpdateEvent("btcusdt@depth@100ms", "BTCUSDT", 1, DateTimeOffset.UnixEpoch, 11, 11, null, [], []));

        // Top 5 depth: Bids = 4, Asks = 2 => (4 - 2) / 6 = 0.3333
        Assert.Equal(0.3333m, book.CalculateTopDepthImbalance(5));

        // Upward bid tick from 100 to 100.50 with ask unchanged at 101 (ask qty drops from 2 to 1)
        var ofi = BinanceLocalOrderBook.ComputeTemporalOfi(
            prevBidPrice: 100m, prevBidQty: 2m, prevAskPrice: 101m, prevAskQty: 2m,
            currBidPrice: 100.5m, currBidQty: 3m, currAskPrice: 101m, currAskQty: 1m);
        Assert.Equal(0.50m, ofi);
    }

    [Fact]
    public void ValidatesMarketLotSizeConstraintsWhenPresent()
    {
        var symbolInfo = new BinanceSymbolInfo(
            "BTCUSDT",
            "TRADING",
            "BTC",
            "USDT",
            8,
            8,
            [
                new BinanceSymbolFilter("PRICE_FILTER", MinPrice: "0.01", MaxPrice: "1000000.00", TickSize: "0.01"),
                new BinanceSymbolFilter("LOT_SIZE", MinQty: "0.00010", MaxQty: "100.00000", StepSize: "0.00010"),
                new BinanceSymbolFilter("MARKET_LOT_SIZE", MinQty: "0.00100", MaxQty: "10.00000", StepSize: "0.00100"),
                new BinanceSymbolFilter("MIN_NOTIONAL", MinNotional: "5.00"),
            ],
            OrderTypes: ["LIMIT", "MARKET"]);

        var rules = BinanceSymbolTradingRulesParser.Parse(symbolInfo);
        Assert.Equal(0.00100m, rules.MarketMinimumQuantity);
        Assert.Equal(10.00000m, rules.MarketMaximumQuantity);
        Assert.Equal(0.00100m, rules.MarketQuantityStepSize);

        var belowMarketMin = rules.ValidateMarketOrder(0.00050m, referencePrice: 80_000m);
        Assert.False(belowMarketMin.IsValid);
        Assert.Contains(belowMarketMin.Violations, v => v.Code == "QUANTITY_BELOW_MINIMUM");

        var validMarketOrder = rules.ValidateMarketOrder(0.00200m, referencePrice: 80_000m);
        Assert.True(validMarketOrder.IsValid);
    }

    [Fact]
    public void BuildsWebSocketOrderPayloadsAndValidatesOcoAndAmendContracts()
    {
        var reqId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var orderReq = new BinanceSpotOrderRequest(
            "BTCUSDT",
            BinanceSpotOrderSide.Buy,
            BinanceSpotOrderType.Limit,
            0.01m,
            "ctp-entry-001",
            price: 84_000m);
        var wsPlaceJson = BinanceSpotWebSocketOrderPayloadBuilder.BuildOrderPlacePayload(
            reqId, orderReq, "api-key-1", "secret-1", 1_700_000_000_000L);
        Assert.Contains("\"method\":\"order.place\"", wsPlaceJson, StringComparison.Ordinal);
        Assert.Contains("\"newClientOrderId\":\"ctp-entry-001\"", wsPlaceJson, StringComparison.Ordinal);
        Assert.Contains("\"signature\":", wsPlaceJson, StringComparison.Ordinal);

        var amendReq = new BinanceSpotAmendKeepPriorityRequest("BTCUSDT", "ctp-entry-001", 0.005m, "ctp-entry-001a");
        var wsAmendJson = BinanceSpotWebSocketOrderPayloadBuilder.BuildAmendKeepPriorityPayload(
            reqId, amendReq, "api-key-1", "secret-1", 1_700_000_000_000L);
        Assert.Contains("\"method\":\"order.amend.keepPriority\"", wsAmendJson, StringComparison.Ordinal);
        Assert.Contains("\"newQty\":\"0.005\"", wsAmendJson, StringComparison.Ordinal);

        var cancelReplaceReq = new BinanceSpotCancelReplaceRequest(
            "BTCUSDT",
            BinanceSpotOrderSide.Buy,
            BinanceSpotOrderType.Limit,
            "ctp-entry-001",
            "ctp-entry-002",
            0.01m,
            price: 84_010m);
        var wsCancelReplaceJson = BinanceSpotWebSocketOrderPayloadBuilder.BuildCancelReplacePayload(
            reqId, cancelReplaceReq, "api-key-1", "secret-1", 1_700_000_000_000L);
        Assert.Contains("\"method\":\"order.cancelReplace\"", wsCancelReplaceJson, StringComparison.Ordinal);

        var ocoReq = new BinanceSpotOcoOrderRequest(
            "BTCUSDT",
            BinanceSpotOrderSide.Sell,
            0.01m,
            "ocol-001",
            "ocotp-001",
            abovePrice: 84_500m,
            "ocosl-001",
            belowStopPrice: 83_850m,
            belowLimitPrice: 83_800m);
        var wsOcoJson = BinanceSpotWebSocketOrderPayloadBuilder.BuildOcoOrderListPayload(
            reqId, ocoReq, "api-key-1", "secret-1", 1_700_000_000_000L);
        Assert.Contains("\"method\":\"orderList.place.oco\"", wsOcoJson, StringComparison.Ordinal);
        Assert.Contains("\"aboveType\":\"LIMIT_MAKER\"", wsOcoJson, StringComparison.Ordinal);
        Assert.Contains("\"belowType\":\"STOP_LOSS_LIMIT\"", wsOcoJson, StringComparison.Ordinal);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The market-data client owns the injected transport for the duration of this test.")]
    public async Task ParsesAggTradeStreamEventFromMarketStream()
    {
        const string payload = """
            {
              "stream": "btcusdt@aggTrade",
              "data": {
                "e": "aggTrade",
                "E": 1700000000100,
                "s": "BTCUSDT",
                "a": 998877,
                "p": "84210.50",
                "q": "0.12500000",
                "f": 100,
                "l": 104,
                "T": 1700000000095,
                "m": false
              }
            }
            """;
        var transport = new DepthTransport(payload);
        var client = new BinanceSpotMarketDataClient(
            new BinanceMarketDataOptions
            {
                WebSocketBaseAddress = new Uri("wss://stream.testnet.binance.vision/stream"),
                InitialReconnectDelay = TimeSpan.FromMilliseconds(1),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1)
            },
            () => transport);

        await using var enumerator = client
            .ReadAsync([new BinanceMarketSubscription("BTCUSDT", BinanceMarketStreamKind.AggTrade)], cancellationToken: TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await enumerator.MoveNextAsync());
        var aggTrade = Assert.IsType<BinanceAggTradeStreamEvent>(enumerator.Current);
        Assert.Equal("BTCUSDT", aggTrade.Symbol);
        Assert.Equal(998877L, aggTrade.AggregateTradeId);
        Assert.Equal(84_210.50m, aggTrade.Price);
        Assert.Equal(0.125m, aggTrade.Quantity);
        Assert.False(aggTrade.IsBuyerMaker);
    }

    private static BinanceLocalOrderBook NewBookAt(long updateId)
    {
        var book = new BinanceLocalOrderBook("BTCUSDT");
        book.ApplySnapshot(Snapshot(updateId));
        return book;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(60)]
    [InlineData(1000)]
    public void TopDepthMetricsMatchSortedReferenceAcrossSmallAndLargeSelections(int levels)
    {
        var book = DeepBook();
        var bid = book.Bids.Take(levels).ToArray();
        var ask = book.Asks.Take(levels).ToArray();
        var bidQty = bid.Sum(level => level.Quantity);
        var askQty = ask.Sum(level => level.Quantity);
        Assert.Equal(Math.Round((bidQty - askQty) / (bidQty + askQty), 4), book.CalculateTopDepthImbalance(levels));
        Assert.Equal(Math.Round(Math.Min(bid.Sum(level => level.Price * level.Quantity), ask.Sum(level => level.Price * level.Quantity)), 2),
            book.CalculateTopDepthLiquidityNotional(levels));
    }

    [Fact]
    public void RepeatedHotDepthQueriesDoNotAllocateAfterWarmup()
    {
        var book = DeepBook();
        for (var i = 0; i < 32; i++)
        {
            _ = book.CalculateTopDepthImbalance();
            _ = book.CalculateTopDepthLiquidityNotional();
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        decimal checksum = 0m;
        for (var i = 0; i < 1000; i++)
            checksum += (book.CalculateTopDepthImbalance() ?? 0m) + (book.CalculateTopDepthLiquidityNotional() ?? 0m);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(checksum);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ChangingSmallDepthSelectionsAlsoAvoidsHeapAllocations()
    {
        var book = DeepBook();
        for (var i = 0; i < 32; i++) _ = book.CalculateTopDepthMetrics(i % 2 + 5);
        var before = GC.GetAllocatedBytesForCurrentThread();
        decimal checksum = 0m;
        for (var i = 0; i < 1000; i++)
        {
            var metrics = book.CalculateTopDepthMetrics(i % 2 + 5);
            checksum += (metrics.Imbalance ?? 0m) + (metrics.LiquidityNotional ?? 0m);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(checksum);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void LargeSnapshotPruningRetainsBestLevelsOnBothSides()
    {
        var book = DeepBook(maxLevels: 25);
        Assert.Equal(Enumerable.Range(0, 25).Select(i => 1000m - i), book.Bids.Select(level => level.Price));
        Assert.Equal(Enumerable.Range(0, 25).Select(i => 1001m + i), book.Asks.Select(level => level.Price));
    }

    [Fact]
    public void CachedSnapshotsCannotBeMutatedAndRemainStableAfterBookUpdates()
    {
        var book = DeepBook();
        var oldBids = book.Bids;
        var oldQuantity = oldBids[0].Quantity;
        var previousImbalance = book.CalculateTopDepthImbalance();
        Assert.Same(oldBids, book.Bids);
        Assert.Throws<NotSupportedException>(() => ((IList<BinanceOrderBookLevel>)oldBids)[0] = Level(9999m));
        book.ApplyDelta(new("btcusdt@depth@100ms", "BTCUSDT", 2, DateTimeOffset.UnixEpoch, 102, 102, null,
            [Level(1000m, oldQuantity + 5m)], []));
        Assert.NotSame(oldBids, book.Bids);
        Assert.Equal(oldQuantity, oldBids[0].Quantity);
        Assert.Equal(oldQuantity + 5m, book.Bids[0].Quantity);
        Assert.NotEqual(previousImbalance, book.CalculateTopDepthImbalance());
        book.InvalidateForStreamDisconnect();
        Assert.Empty(book.Bids);
        Assert.Null(book.CalculateTopDepthImbalance());
        Assert.Equal(oldQuantity, oldBids[0].Quantity);
    }

    [Fact]
    public void OverflowingDepthNotionalInvalidatesBookRatherThanPublishingLiquidity()
    {
        var book = new BinanceLocalOrderBook("BTCUSDT");
        book.ApplySnapshot(new("BTCUSDT", 100, [Level(1e20m, 1e20m)], [Level(1e20m + 1m, 1e20m)]));
        book.ApplyDelta(new("btcusdt@depth@100ms", "BTCUSDT", 1, DateTimeOffset.UnixEpoch, 101, 101, null, [], []));
        Assert.Null(book.CalculateTopDepthLiquidityNotional());
        Assert.True(book.ResyncRequired);
        Assert.Null(book.CalculateTopDepthImbalance());
        Assert.Empty(book.Bids);
    }

    private static BinanceLocalOrderBook DeepBook(int maxLevels = 80)
    {
        var book = new BinanceLocalOrderBook("BTCUSDT", maxLevels: maxLevels);
        var bids = Enumerable.Range(0, 80).Reverse().Select(i => Level(1000m - i, i % 7 + 1)).ToArray();
        var asks = Enumerable.Range(0, 80).Reverse().Select(i => Level(1001m + i, i % 5 + 2)).ToArray();
        book.ApplySnapshot(new("BTCUSDT", 100, bids, asks));
        book.ApplyDelta(new("btcusdt@depth@100ms", "BTCUSDT", 1, DateTimeOffset.UnixEpoch, 101, 101, null, [], []));
        Assert.True(book.IsSynchronized);
        return book;
    }

    private static BinanceOrderBookSnapshot Snapshot(long updateId) =>
        new("BTCUSDT", updateId, [Level(100)], [Level(101)]);

    private static BinanceDepthUpdateEvent Delta(long first, long final, decimal price = 100) =>
        new("btcusdt@depth@100ms", "BTCUSDT", 1, DateTimeOffset.UnixEpoch, first, final, null, [Level(price)], []);

    private static BinanceOrderBookLevel Level(decimal price, decimal quantity = 1m) => new(price, quantity);

    private sealed class DepthTransport(string payload) : IBinanceWebSocketTransport
    {
        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => Task.CompletedTask;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadMessagesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Encoding.UTF8.GetBytes(payload);
            await Task.Yield();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
