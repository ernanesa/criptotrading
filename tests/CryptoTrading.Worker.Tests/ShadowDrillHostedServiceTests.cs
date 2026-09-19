using CryptoTrading.Exchange.Paper;
using CryptoTrading.Observability;
using CryptoTrading.Shadow;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class ShadowDrillHostedServiceTests
{
    [Fact]
    public async Task EnabledDrillWritesRedactedReportAndDoesNotRaiseCleanRunAlert()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cryptotrading-worker-shadow-{Guid.NewGuid():N}");
        using var writer = new ShadowReportWriter(directory);
        var sink = new InMemoryTradingAlertSink();
        using var dispatcher = new TradingAlertDispatcher(sink);
        var service = new ShadowDrillHostedService(
            Options.Create(new WorkerRuntimeOptions { ShadowEnabled = true, NoTrade = true, ShadowReportDirectory = directory }),
            new PaperExchange(),
            writer,
            dispatcher,
            NullLogger<ShadowDrillHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        var report = Assert.Single(Directory.GetFiles(directory, "*.jsonl"));
        Assert.Single(await File.ReadAllLinesAsync(report));
        Assert.Empty(sink.Alerts);
    }

    [Fact]
    public async Task DisabledDrillDoesNotCreateReport()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cryptotrading-worker-shadow-disabled-{Guid.NewGuid():N}");
        using var writer = new ShadowReportWriter(directory);
        var sink = new InMemoryTradingAlertSink();
        using var dispatcher = new TradingAlertDispatcher(sink);
        var service = new ShadowDrillHostedService(
            Options.Create(new WorkerRuntimeOptions { ShadowEnabled = false, ShadowReportDirectory = directory }),
            new PaperExchange(),
            writer,
            dispatcher,
            NullLogger<ShadowDrillHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.False(Directory.Exists(directory));
        Assert.Empty(sink.Alerts);
    }
}
