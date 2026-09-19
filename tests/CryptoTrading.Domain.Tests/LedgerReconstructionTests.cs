using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Domain.Tests;

[Trait("Category", "RecoveryDrill")]
public sealed class LedgerReconstructionTests
{
    private static readonly AccountId Wallet = new("wallet");
    private static readonly AccountId External = new("external");
    private static readonly Asset Usdt = new("USDT");

    [Fact]
    public void RebuildsProjectionInDeterministicJournalOrder()
    {
        var first = Entry(2, new DateTimeOffset(2026, 9, 19, 12, 2, 0, TimeSpan.Zero), 30m);
        var second = Entry(1, new DateTimeOffset(2026, 9, 19, 12, 1, 0, TimeSpan.Zero), 50m);

        var result = LedgerReconstructor.Rebuild([first, second], [External]);

        Assert.Equal(2, result.EntriesApplied);
        Assert.Equal(new DateTimeOffset(2026, 9, 19, 12, 2, 0, TimeSpan.Zero), result.LastOccurredAtUtc);
        Assert.Equal(80m, result.Ledger.GetBalance(Wallet, Usdt).Amount);
        Assert.True(result.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public void RebuildIsIdempotentForDuplicateJournalRows()
    {
        var entry = Entry(3, DateTimeOffset.UtcNow, 25m);

        var result = LedgerReconstructor.Rebuild([entry, entry], [External]);

        Assert.Equal(2, result.EntriesApplied);
        Assert.Equal(25m, result.Ledger.GetBalance(Wallet, Usdt).Amount);
    }

    [Fact]
    public void EmptyJournalProducesEmptySnapshot()
    {
        var result = LedgerReconstructor.Rebuild([]);

        Assert.Equal(0, result.EntriesApplied);
        Assert.Null(result.LastOccurredAtUtc);
        Assert.Equal(0m, result.Ledger.GetBalance(Wallet, Usdt).Amount);
    }

    private static LedgerEntry Entry(int sequence, DateTimeOffset occurredAt, decimal amount) => new(
        new LedgerEntryId(Guid.Parse($"00000000-0000-0000-0000-{sequence + 1:D12}")),
        CorrelationId.New(),
        occurredAt,
        "recovery-test",
        [
            new LedgerPosting(Wallet, Usdt, amount),
            new LedgerPosting(External, Usdt, -amount),
        ]);
}
