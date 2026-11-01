namespace CryptoTrading.Exchange.Binance.Tests;

// Finite, real quota backend isolated to synthetic protocol tests.
internal sealed class BinanceTestBudget : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ct-rest-fixture-" + Guid.NewGuid().ToString("N"));
    public BinanceRequestBudget Instance { get; }
    public BinanceTestBudget()
    {
        BinanceRateLimitDefinition[] limits = [new("REQUEST_WEIGHT", "MINUTE", 1, 1_000_000),
            new("RAW_REQUESTS", "MINUTE", 1, 1_000_000), new("ORDERS", "MINUTE", 1, 1_000_000)];
        Instance = new BinanceRequestBudget(directory, TimeProvider.System, limits);
    }
    public void Dispose()
    { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
