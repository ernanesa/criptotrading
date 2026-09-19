using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Persistence;
using Xunit;

namespace CryptoTrading.Persistence.Tests;

public sealed class FullStateReconstructionTests
{
    private static readonly AccountId Wallet = new("wallet");
    private static readonly AccountId External = new("external");
    private static readonly Asset Usdt = new("USDT");

    [Fact]
    public void ManifestIsOrderIndependentAndRebuildsLedger()
    {
        var first = Entry(1, 10m);
        var second = Entry(2, 5m);
        var eventRecord = new AccountStreamEventRecord(
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            "executionReport", "execution:1", 1_700_000_000_001,
            new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero),
            "{\"e\":\"executionReport\",\"s\":\"BTCUSDT\"}", "BTCUSDT",
            Guid.Parse("20000000-0000-0000-0000-000000000001"), 1, 4);
        var order = new LocalOpenOrderRecord("BTCUSDT", 42, "client-42", "NEW", DateTimeOffset.UtcNow);
        var outbox = new OutboxRecoveryMetadata(
            Guid.Parse("30000000-0000-0000-0000-000000000001"), "Pending", 1, DateTimeOffset.UtcNow);
        var captured = new FixedTimeProvider(new DateTimeOffset(2026, 9, 19, 12, 1, 0, TimeSpan.Zero));

        var firstManifest = FullStateReconstructor.Build(
            FullStateReconstructionInput.Create([first, second], [order], [eventRecord], [outbox]),
            [External],
            captured);
        var secondManifest = FullStateReconstructor.Build(
            FullStateReconstructionInput.Create([second, first], [order], [eventRecord], [outbox]),
            [External],
            captured);

        Assert.Equal(firstManifest.ManifestSha256, secondManifest.ManifestSha256);
        Assert.Equal(2, firstManifest.Ledger.EntriesApplied);
        Assert.Equal(15m, firstManifest.Ledger.Ledger.GetBalance(Wallet, Usdt).Amount);
        Assert.True(firstManifest.HasAllLocalEvidence);
        Assert.Equal(1, firstManifest.OpenOrderCount);
        Assert.Equal(1, firstManifest.AccountStreamEventCount);
        Assert.Equal(1, firstManifest.OutboxRecoveryRecordCount);
    }

    [Fact]
    public void RejectsDuplicateProjectionIdentity()
    {
        var order = new LocalOpenOrderRecord("BTCUSDT", 42, "client-42", "NEW", DateTimeOffset.UtcNow);
        var input = FullStateReconstructionInput.Create([], [order, order], [], []);

        Assert.Throws<InvalidOperationException>(() => FullStateReconstructor.Build(input));
    }

    [Fact]
    public void RejectsDuplicateAccountEventIdentity()
    {
        var eventRecord = EventRecord("execution:1");
        var input = FullStateReconstructionInput.Create([], [], [eventRecord, eventRecord], []);

        Assert.Throws<InvalidOperationException>(() => FullStateReconstructor.Build(input));
    }

    [Fact]
    public void RejectsNonUtcOutboxMetadata()
    {
        var metadata = new OutboxRecoveryMetadata(
            Guid.NewGuid(), "Pending", 0,
            new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.FromHours(-3)));

        Assert.Throws<ArgumentException>(() => FullStateReconstructor.Build(
            FullStateReconstructionInput.Create([], [], [], [metadata])));
    }

    [Fact]
    public void RejectsNegativeLedgerProjectionInsteadOfProducingOptimisticState()
    {
        var entry = new LedgerEntry(
            LedgerEntryId.New(), CorrelationId.New(), DateTimeOffset.UtcNow,
            "invalid-recovery", [
                new LedgerPosting(Wallet, Usdt, -1m),
                new LedgerPosting(External, Usdt, 1m)]);

        Assert.Throws<InvalidOperationException>(() => FullStateReconstructor.Build(
            FullStateReconstructionInput.Create([entry], [], [], [])));
    }

    private static LedgerEntry Entry(int sequence, decimal amount) => new(
        new LedgerEntryId(Guid.Parse($"00000000-0000-0000-0000-{sequence + 1:D12}")),
        CorrelationId.New(), DateTimeOffset.UtcNow.AddMinutes(sequence), "recovery-test", [
            new LedgerPosting(Wallet, Usdt, amount),
            new LedgerPosting(External, Usdt, -amount)]);

    private static AccountStreamEventRecord EventRecord(string dedupeKey) => new(
        Guid.NewGuid(), "executionReport", dedupeKey, 1_700_000_000_001,
        DateTimeOffset.UtcNow,
        "{\"e\":\"executionReport\",\"s\":\"BTCUSDT\"}", "BTCUSDT");

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
