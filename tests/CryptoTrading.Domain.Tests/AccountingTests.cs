using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class AccountingTests
{
    private static readonly Asset Usdt = new("USDT");
    private static readonly AccountId Wallet = new("wallet");
    private static readonly AccountId External = new("external");

    [Fact]
    public void EntryRequiresBalancedPostingsPerAsset()
    {
        Assert.Throws<ArgumentException>(() => CreateEntry(
            new LedgerPosting(Wallet, Usdt, 10m),
            new LedgerPosting(External, Usdt, -9m)));
    }

    [Fact]
    public void EntryMayContainMultipleIndependentCommodities()
    {
        var entry = CreateEntry(
            new LedgerPosting(Wallet, Usdt, 100m),
            new LedgerPosting(External, Usdt, -100m),
            new LedgerPosting(Wallet, new Asset("BTC"), 1m),
            new LedgerPosting(External, new Asset("BTC"), -1m));

        Assert.Equal(4, entry.Postings.Count);
    }

    [Fact]
    public void JournalIsAppendOnlyAndIdempotent()
    {
        var journal = new InMemoryJournal();
        var entry = CreateEntry(idempotencyKey: new IdempotencyKey("deposit-1"),
            new LedgerPosting(Wallet, Usdt, 10m),
            new LedgerPosting(External, Usdt, -10m));

        Assert.Same(entry, journal.Append(entry));
        Assert.Same(entry, journal.Append(entry));
        Assert.Single(journal.Entries);
    }

    [Fact]
    public void JournalRejectsDifferentEntryWithReusedIdempotencyKey()
    {
        var journal = new InMemoryJournal();
        var key = new IdempotencyKey("deposit-1");
        journal.Append(CreateEntry(idempotencyKey: key,
            new LedgerPosting(Wallet, Usdt, 10m),
            new LedgerPosting(External, Usdt, -10m)));

        Assert.Throws<InvalidOperationException>(() => journal.Append(CreateEntry(idempotencyKey: key,
            new LedgerPosting(Wallet, Usdt, 11m),
            new LedgerPosting(External, Usdt, -11m))));
    }

    [Fact]
    public void LedgerRejectsNegativeAvailableBalanceWithoutPartialMutation()
    {
        var ledger = new InMemoryLedger([External]);
        var entry = CreateEntry(
            new LedgerPosting(Wallet, Usdt, -10m),
            new LedgerPosting(External, Usdt, 10m));

        Assert.Throws<InvalidOperationException>(() => ledger.Apply(entry));
        Assert.Equal(0m, ledger.GetBalance(Wallet, Usdt).Amount);
        Assert.Equal(0m, ledger.GetBalance(External, Usdt).Amount);
    }

    [Fact]
    public void LedgerAppliesMultiCommodityEntryAtomically()
    {
        var ledger = new InMemoryLedger([External]);
        ledger.Apply(CreateEntry(
            new LedgerPosting(Wallet, Usdt, 100m),
            new LedgerPosting(External, Usdt, -100m),
            new LedgerPosting(Wallet, new Asset("BTC"), 2m),
            new LedgerPosting(External, new Asset("BTC"), -2m)));

        Assert.Equal(100m, ledger.GetBalance(Wallet, Usdt).Amount);
        Assert.Equal(2m, ledger.GetBalance(Wallet, new Asset("BTC")).Amount);
    }

    private static LedgerEntry CreateEntry(
        params LedgerPosting[] postings) => CreateEntry(null, postings);

    private static LedgerEntry CreateEntry(
        IdempotencyKey? idempotencyKey,
        params LedgerPosting[] postings) => new(
            LedgerEntryId.New(),
            CorrelationId.New(),
            DateTimeOffset.UtcNow,
            "test",
            postings,
            idempotencyKey);
}
