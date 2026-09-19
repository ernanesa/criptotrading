using CryptoTrading.Domain.Primitives;

namespace CryptoTrading.Domain.Accounting;

/// <summary>Identifies one balance bucket in the journal.</summary>
public readonly record struct AccountId
{
    public AccountId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.Length > 100 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Account identifiers must be 1-100 characters and contain no control characters.", nameof(value));
        }

        Value = normalized;
    }

    public string Value { get; }

    public static AccountId FromString(string value) => new(value);

    public static implicit operator AccountId(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>A signed posting. Positive values increase an account balance.</summary>
public readonly record struct LedgerPosting(AccountId Account, Money Amount)
{
    public LedgerPosting(AccountId account, Asset asset, decimal amount)
        : this(account, new Money(amount, asset))
    {
    }

    public void Validate()
    {
        if (Amount.Amount == 0m)
        {
            throw new ArgumentException("A ledger posting cannot have a zero amount.", nameof(Amount));
        }
    }
}

/// <summary>Immutable, balanced journal event. Entries are append-only and never mutated.</summary>
public sealed record LedgerEntry
{
    public LedgerEntry(
        LedgerEntryId id,
        CorrelationId correlationId,
        DateTimeOffset occurredAt,
        string reason,
        IEnumerable<LedgerPosting> postings,
        IdempotencyKey? idempotencyKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(reason), "Ledger entry reasons cannot exceed 200 characters.");
        }

        ArgumentNullException.ThrowIfNull(postings);
        var materialized = postings.ToArray();
        if (materialized.Length < 2)
        {
            throw new ArgumentException("A journal entry requires at least two postings.", nameof(postings));
        }

        foreach (var posting in materialized)
        {
            posting.Validate();
        }

        foreach (var group in materialized.GroupBy(static posting => posting.Amount.Asset))
        {
            if (group.Sum(static posting => posting.Amount.Amount) != 0m)
            {
                throw new ArgumentException($"Journal entry is not balanced for asset {group.Key}.", nameof(postings));
            }
        }

        Id = id;
        CorrelationId = correlationId;
        OccurredAt = occurredAt;
        Reason = reason.Trim();
        Postings = Array.AsReadOnly(materialized);
        IdempotencyKey = idempotencyKey;
    }

    public LedgerEntryId Id { get; }

    public CorrelationId CorrelationId { get; }

    public DateTimeOffset OccurredAt { get; }

    public string Reason { get; }

    public IReadOnlyList<LedgerPosting> Postings { get; }

    public IdempotencyKey? IdempotencyKey { get; }
}

public interface IJournal
{
    IReadOnlyList<LedgerEntry> Entries { get; }

    LedgerEntry Append(LedgerEntry entry);
}

/// <summary>Deterministic append-only journal. Re-appending an identical id is idempotent.</summary>
public sealed class InMemoryJournal : IJournal
{
    private readonly List<LedgerEntry> entries = [];
    private readonly Dictionary<LedgerEntryId, LedgerEntry> byId = [];
    private readonly Dictionary<IdempotencyKey, LedgerEntry> byIdempotencyKey = [];

    public IReadOnlyList<LedgerEntry> Entries => entries.AsReadOnly();

    public LedgerEntry Append(LedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (byId.TryGetValue(entry.Id, out var existingById))
        {
            EnsureSameEntry(existingById, entry);
            return existingById;
        }

        if (entry.IdempotencyKey is { } key && byIdempotencyKey.TryGetValue(key, out var existingByKey))
        {
            EnsureSameEntry(existingByKey, entry);
            return existingByKey;
        }

        entries.Add(entry);
        byId.Add(entry.Id, entry);
        if (entry.IdempotencyKey is { } idempotencyKey)
        {
            byIdempotencyKey.Add(idempotencyKey, entry);
        }

        return entry;
    }

    private static void EnsureSameEntry(LedgerEntry existing, LedgerEntry candidate)
    {
        if (existing != candidate && !existing.Equals(candidate))
        {
            throw new InvalidOperationException("The journal identity is already associated with different data.");
        }
    }
}

public interface ILedger
{
    Money GetBalance(AccountId account, Asset asset);

    IReadOnlySet<string> GetAssets(AccountId account);

    void Apply(LedgerEntry entry);
}

/// <summary>In-memory projection of the journal with non-negative account invariants.</summary>
public sealed class InMemoryLedger : ILedger
{
    private readonly Dictionary<(AccountId Account, Asset Asset), decimal> balances = [];
    private readonly HashSet<LedgerEntryId> appliedEntries = [];
    private readonly HashSet<AccountId> unconstrainedAccounts;

    public InMemoryLedger(IEnumerable<AccountId>? unconstrainedAccounts = null)
    {
        this.unconstrainedAccounts = unconstrainedAccounts is null
            ? []
            : unconstrainedAccounts.ToHashSet();
    }

    public Money GetBalance(AccountId account, Asset asset) =>
        new(balances.GetValueOrDefault((account, asset)), asset);

    public IReadOnlySet<string> GetAssets(AccountId account) =>
        balances.Keys
            .Where(key => key.Account == account)
            .Select(static key => key.Asset.Value)
            .ToHashSet(StringComparer.Ordinal);

    public void Apply(LedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!appliedEntries.Add(entry.Id))
        {
            return;
        }

        var projected = new Dictionary<(AccountId Account, Asset Asset), decimal>();
        foreach (var posting in entry.Postings)
        {
            var key = (posting.Account, posting.Amount.Asset);
            var current = projected.GetValueOrDefault(key, balances.GetValueOrDefault(key));
            var next = current + posting.Amount.Amount;
            if (next < 0m && !unconstrainedAccounts.Contains(posting.Account))
            {
                appliedEntries.Remove(entry.Id);
                throw new InvalidOperationException($"Insufficient {posting.Amount.Asset} balance in account {posting.Account}.");
            }

            projected[key] = next;
        }

        foreach (var (key, amount) in projected)
        {
            balances[key] = amount;
        }
    }
}
