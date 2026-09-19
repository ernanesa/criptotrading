using CryptoTrading.Domain.Risk;
using CryptoTrading.Exchange.Binance;
using CryptoTrading.Persistence;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class AccountStreamHostedServiceTests
{
    [Fact]
    public void ExecutionEventsProduceStableDeduplicationIdentity()
    {
        var receivedAt = DateTimeOffset.UtcNow;
        var first = AccountStreamHostedService.ToRecord(new BinanceExecutionReportEvent(
            receivedAt,
            1_700_000_000_123,
            "BTCUSDT",
            42,
            7,
            "client-42",
            "BUY",
            "LIMIT",
            "TRADE",
            "FILLED",
            0.01m,
            0.01m,
            100.50m,
            0.001m,
            "USDT"));
        var second = AccountStreamHostedService.ToRecord(new BinanceExecutionReportEvent(
            receivedAt.AddSeconds(5),
            1_700_000_000_123,
            "BTCUSDT",
            42,
            7,
            "client-42",
            "BUY",
            "LIMIT",
            "TRADE",
            "FILLED",
            0.01m,
            0.01m,
            100.50m,
            0.001m,
            "USDT"));

        Assert.Equal(first.EventId, second.EventId);
        Assert.Equal(first.DedupeKey, second.DedupeKey);
        Assert.Equal("executionReport", first.EventType);
    }

    [Fact]
    public void ServerShutdownIsPersistedAsControlEvidence()
    {
        var record = AccountStreamHostedService.ToRecord(new BinanceUserDataStreamControlEvent(
            DateTimeOffset.UtcNow,
            1_700_000_000_124,
            "serverShutdown",
            7));

        Assert.Equal("serverShutdown", record.EventType);
        Assert.Equal("serverShutdown", record.PayloadJson.Contains("serverShutdown", StringComparison.Ordinal) ? "serverShutdown" : null);
        Assert.Equal(64, record.PayloadSha256.Length);
    }

    [Fact]
    public void AccountStreamGateRequiresPersistenceMigrationAndNoTrade()
    {
        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => new WorkerRuntimeOptions
        {
            AccountStreamEnabled = true,
            PersistenceEnabled = true,
            ApplyMigrations = true,
            NoTrade = false,
            PostgresConnectionString = "Host=localhost"
        }.Validate());

        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => new WorkerRuntimeOptions
        {
            AccountStreamEnabled = true,
            PersistenceEnabled = true,
            ApplyMigrations = false,
            PostgresConnectionString = "Host=localhost"
        }.Validate());
    }
}
