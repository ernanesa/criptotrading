using CryptoTrading.Domain.Accounting;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using System.Globalization;
using Xunit;

namespace CryptoTrading.Persistence.Tests;

public sealed class ExecutionBasisProjectionTests
{
    [Theory]
    [InlineData("0", "0", "0")]
    [InlineData("0.5", "50", "0")]
    [InlineData("1", "100", "0.01")]
    public void SellWithoutVerifiedInventoryFailsClosed(string quantity, string cost, string baseFee)
    {
        var command = CreateSell(decimal.Parse(baseFee, CultureInfo.InvariantCulture));

        Assert.Throws<InvalidOperationException>(() => PostgresTradingStore.ApplyPersistedAverageCost(
            command, decimal.Parse(quantity, CultureInfo.InvariantCulture), decimal.Parse(cost, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void SellWithVerifiedInventoryCalculatesOnlyItsKnownCost()
    {
        var result = PostgresTradingStore.ApplyPersistedAverageCost(CreateSell(0m), 1m, 100m);

        Assert.Equal(0m, result.Quantity);
        Assert.Equal(0m, result.CostQuote);
        Assert.Equal(20m, result.RealizedPnlQuote);
    }

    [Fact]
    public void ReplayRejectsSameJournalIdentityWithDifferentPostings()
    {
        var command = CreateSell(0m);
        var original = ExecutionAccounting.CreateLedgerEntry(command);
        var otherPostings = original.Postings.Select(posting => new LedgerPosting(
            posting.Account, posting.Amount.Asset, posting.Amount.Amount * 2m)).ToArray();
        var different = new LedgerEntry(original.Id, original.CorrelationId, original.OccurredAt,
            original.Reason, otherPostings, original.IdempotencyKey);

        Assert.Throws<InvalidOperationException>(() => PostgresTradingStore.ValidateExecutionReplayJournal(original, different));
        PostgresTradingStore.ValidateExecutionReplayJournal(original, original);
    }

    [Fact]
    public void ReplayAcceptsJournalAmountsRoundedToPostgresNumericScale()
    {
        var template = CreateSell(0m);
        var command = new ExecutionAccountingCommand(template.ExecutionId, template.TradeId, template.OrderId,
            template.Symbol, template.BaseAsset, template.QuoteAsset, template.Side,
            new Quantity(0.123456789012345678m), new Price(1.234567890123456789m), template.Commission,
            template.WalletAccount, template.FeesAccount, template.LedgerEntryId, template.CorrelationId,
            template.IdempotencyKey, template.OccurredAtUtc);
        var candidate = ExecutionAccounting.CreateLedgerEntry(command);
        var persisted = new LedgerEntry(candidate.Id, candidate.CorrelationId, candidate.OccurredAt, candidate.Reason,
            candidate.Postings.Select(posting => new LedgerPosting(posting.Account, posting.Amount.Asset,
                decimal.Round(posting.Amount.Amount, 18, MidpointRounding.AwayFromZero))), candidate.IdempotencyKey);

        PostgresTradingStore.ValidateExecutionReplayJournal(persisted, candidate);
    }

    [Fact]
    public void ReplayAcceptsExecutionAmountsRoundedToPostgresNumericScale()
    {
        var template = CreateSell(0m);
        var command = new ExecutionAccountingCommand(template.ExecutionId, template.TradeId, template.OrderId,
            template.Symbol, template.BaseAsset, template.QuoteAsset, template.Side,
            new Quantity(1.0000000000000000001m), template.Price, template.Commission,
            template.WalletAccount, template.FeesAccount, template.LedgerEntryId, template.CorrelationId,
            template.IdempotencyKey, template.OccurredAtUtc);
        var facts = new ExecutionReplayFacts(command.TradeId, command.OrderId, command.Symbol.Value,
            command.BaseAsset.Value, command.QuoteAsset.Value, "SELL", 1m, command.Price.Value,
            command.Commission.Amount, command.Commission.Asset.Value, command.OccurredAtUtc, command.LedgerEntryId.Value);

        PostgresTradingStore.ValidateExecutionReplayFacts(facts, command);
    }

    [Fact]
    public void ReplayRejectsConflictingPersistedExecutionFacts()
    {
        var command = CreateSell(0m);
        var facts = new ExecutionReplayFacts(command.TradeId, command.OrderId, command.Symbol.Value,
            command.BaseAsset.Value, command.QuoteAsset.Value, "SELL", command.Quantity.Value,
            command.Price.Value, command.Commission.Amount, command.Commission.Asset.Value,
            command.OccurredAtUtc, command.LedgerEntryId.Value);
        PostgresTradingStore.ValidateExecutionReplayFacts(facts, command);
        PostgresTradingStore.ValidateExecutionReplayFacts(facts with { OccurredAtUtc = facts.OccurredAtUtc.AddTicks(9) }, command);

        var conflicts = new[]
        {
            facts with { TradeId = null }, facts with { OrderId = 2 }, facts with { Symbol = "ETHUSDT" },
            facts with { BaseAsset = "ETH" }, facts with { QuoteAsset = "BTC" }, facts with { Side = "BUY" },
            facts with { Quantity = 2m }, facts with { Price = 121m }, facts with { Commission = 1m },
            facts with { CommissionAsset = "USDT" }, facts with { OccurredAtUtc = facts.OccurredAtUtc.AddMicroseconds(1) },
            facts with { LedgerEntryId = Guid.NewGuid() }
        };
        foreach (var conflict in conflicts)
        {
            Assert.Throws<InvalidOperationException>(() => PostgresTradingStore.ValidateExecutionReplayFacts(conflict, command));
        }
    }

    private static ExecutionAccountingCommand CreateSell(decimal baseFee)
    {
        var baseAsset = new Asset("BTC");
        var quoteAsset = new Asset("USDT");
        return new ExecutionAccountingCommand(
            "unit-execution", 1, 1, new Symbol("BTCUSDT"), baseAsset, quoteAsset,
            OrderSide.Sell, new Quantity(1m), new Price(120m), new Money(baseFee, baseAsset),
            new AccountId("wallet"), new AccountId("fees"), LedgerEntryId.New(), CorrelationId.New(),
            IdempotencyKey.New(), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }
}
