using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Primitives;
using Xunit;

namespace CryptoTrading.Domain.Tests;

/// <summary>
/// Deterministic property-style coverage for the accounting invariants.
/// The fixed seed makes every generated case reproducible when a failure occurs.
/// </summary>
public sealed class AccountingPropertyTests
{
    private static readonly AccountId Wallet = new("wallet");
    private static readonly AccountId External = new("external");
    private static readonly DateTimeOffset OccurredAt = new(2026, 9, 19, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public void GeneratedBalancedEntriesAreBalancedForEveryAsset()
    {
        var random = new DeterministicSequence(0x51A7_2026);

        for (var iteration = 0; iteration < 256; iteration++)
        {
            var usdt = NextAmount(random);
            var btc = NextAmount(random);
            var entry = CreateEntry(
                iteration,
                new LedgerPosting(Wallet, new Asset("USDT"), usdt),
                new LedgerPosting(External, new Asset("USDT"), -usdt),
                new LedgerPosting(Wallet, new Asset("BTC"), btc),
                new LedgerPosting(External, new Asset("BTC"), -btc));

            Assert.All(entry.Postings.GroupBy(static posting => posting.Amount.Asset), group =>
                Assert.Equal(0m, group.Sum(static posting => posting.Amount.Amount)));
        }
    }

    [Fact]
    public void GeneratedUnbalancedEntriesAreRejectedWithoutJournalMutation()
    {
        var random = new DeterministicSequence(0xBA1A_CED);
        var journal = new InMemoryJournal();

        for (var iteration = 0; iteration < 128; iteration++)
        {
            var amount = NextAmount(random);
            var postings = new[]
            {
                new LedgerPosting(Wallet, new Asset("USDT"), amount),
                new LedgerPosting(External, new Asset("USDT"), -(amount - 0.01m)),
            };

            Assert.Throws<ArgumentException>(() => CreateEntry(iteration, postings));
            Assert.Empty(journal.Entries);
        }
    }

    [Fact]
    public void GeneratedJournalEntriesAreIdempotentAcrossRetries()
    {
        var random = new DeterministicSequence(0x1D3A_507);
        var journal = new InMemoryJournal();

        for (var iteration = 0; iteration < 256; iteration++)
        {
            var amount = NextAmount(random);
            var entry = CreateEntry(
                iteration,
                new IdempotencyKey($"deposit-{iteration}"),
                new LedgerPosting(Wallet, new Asset("USDT"), amount),
                new LedgerPosting(External, new Asset("USDT"), -amount));

            var first = journal.Append(entry);
            var replay = journal.Append(entry);

            Assert.Same(first, replay);
            Assert.Equal(iteration + 1, journal.Entries.Count);
        }
    }

    [Fact]
    public void GeneratedMultiAssetEntriesAreAtomicWhenOneAssetWouldGoNegative()
    {
        var random = new DeterministicSequence(0xA701_1C);

        for (var iteration = 0; iteration < 256; iteration++)
        {
            var ledger = new InMemoryLedger([External]);
            var usdt = NextAmount(random);
            var btc = NextAmount(random);
            ledger.Apply(CreateEntry(
                iteration,
                new LedgerPosting(Wallet, new Asset("USDT"), usdt),
                new LedgerPosting(External, new Asset("USDT"), -usdt),
                new LedgerPosting(Wallet, new Asset("BTC"), btc),
                new LedgerPosting(External, new Asset("BTC"), -btc)));

            var beforeUsdt = ledger.GetBalance(Wallet, new Asset("USDT"));
            var beforeBtc = ledger.GetBalance(Wallet, new Asset("BTC"));
            var eth = NextAmount(random);
            var invalidTransfer = CreateEntry(
                10_000 + iteration,
                new LedgerPosting(Wallet, new Asset("USDT"), 1m),
                new LedgerPosting(External, new Asset("USDT"), -1m),
                new LedgerPosting(Wallet, new Asset("ETH"), -eth),
                new LedgerPosting(External, new Asset("ETH"), eth));

            Assert.Throws<InvalidOperationException>(() => ledger.Apply(invalidTransfer));
            Assert.Equal(beforeUsdt, ledger.GetBalance(Wallet, new Asset("USDT")));
            Assert.Equal(beforeBtc, ledger.GetBalance(Wallet, new Asset("BTC")));
            Assert.Equal(0m, ledger.GetBalance(Wallet, new Asset("ETH")).Amount);
        }
    }

    [Fact]
    public void GeneratedValidTransfersNeverMakeConstrainedBalancesNegative()
    {
        var random = new DeterministicSequence(0xBEEF_009);

        for (var iteration = 0; iteration < 256; iteration++)
        {
            var ledger = new InMemoryLedger([External]);
            var asset = new Asset("USDT");
            var initialDeposit = NextAmount(random) + 100m;
            ledger.Apply(CreateEntry(
                iteration,
                new LedgerPosting(Wallet, asset, initialDeposit),
                new LedgerPosting(External, asset, -initialDeposit)));

            var remaining = initialDeposit;
            for (var transfer = 0; transfer < 16; transfer++)
            {
                var spend = decimal.Round((decimal)random.NextFraction() * remaining, 2, MidpointRounding.ToZero);
                if (spend == 0m)
                {
                    continue;
                }

                ledger.Apply(CreateEntry(
                    1_000_000 + iteration * 100 + transfer,
                    new LedgerPosting(Wallet, asset, -spend),
                    new LedgerPosting(External, asset, spend)));
                remaining -= spend;

                Assert.True(ledger.GetBalance(Wallet, asset).Amount >= 0m);
            }

            Assert.Equal(remaining, ledger.GetBalance(Wallet, asset).Amount);
        }
    }

    private static decimal NextAmount(DeterministicSequence random) => random.Next(1, 10_000) / 100m;

    private static LedgerEntry CreateEntry(int sequence, params LedgerPosting[] postings) =>
        CreateEntry(sequence, null, postings);

    private static LedgerEntry CreateEntry(
        int sequence,
        IdempotencyKey? idempotencyKey,
        params LedgerPosting[] postings) => new(
            new LedgerEntryId(Guid.Parse($"00000000-0000-0000-0000-{sequence + 1:D12}")),
            new CorrelationId(Guid.Parse($"00000000-0000-0000-0001-{sequence + 1:D12}")),
            OccurredAt.AddSeconds(sequence),
            "property-test",
            postings,
            idempotencyKey);

    private sealed class DeterministicSequence(uint state)
    {
        private uint state = state == 0 ? 0x6D2B_79F5u : state;

        public int Next(int minimumInclusive, int maximumExclusive)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(minimumInclusive, maximumExclusive);
            return minimumInclusive + (int)(NextUInt() % (uint)(maximumExclusive - minimumInclusive));
        }

        public double NextFraction() => NextUInt() / (double)uint.MaxValue;

        private uint NextUInt()
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }
    }
}
