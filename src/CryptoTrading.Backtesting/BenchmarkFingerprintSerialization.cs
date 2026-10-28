using System.Text.Json.Serialization;

namespace CryptoTrading.Backtesting;

internal sealed record BenchmarkFingerprintPayload(
    [property: JsonPropertyName("strategy")] BenchmarkResult Strategy,
    [property: JsonPropertyName("no_trade")] BenchmarkResult NoTrade,
    [property: JsonPropertyName("buy_hold")] BenchmarkResult BuyHold);

internal sealed record BenchmarkWindowFingerprintPayload(
    [property: JsonPropertyName("window")] WalkForwardWindow Window,
    [property: JsonPropertyName("benchmark")] string Benchmark);

internal sealed record BenchmarkAggregateFingerprintPayload(
    [property: JsonPropertyName("windows")] BenchmarkWindowFingerprintPayload[] Windows,
    [property: JsonPropertyName("strategy_return")] decimal StrategyReturn,
    [property: JsonPropertyName("no_trade_return")] decimal NoTradeReturn,
    [property: JsonPropertyName("buy_hold_return")] decimal BuyHoldReturn);

[JsonSerializable(typeof(BenchmarkFingerprintPayload))]
[JsonSerializable(typeof(BenchmarkAggregateFingerprintPayload))]
internal sealed partial class BenchmarkFingerprintJsonContext : JsonSerializerContext;
