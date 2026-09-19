using System.Diagnostics;

namespace CryptoTrading.Domain.Accounting;

/// <summary>Result of rebuilding the balance projection from the append-only journal.</summary>
public sealed record LedgerReconstructionResult(
    InMemoryLedger Ledger,
    int EntriesApplied,
    DateTimeOffset? LastOccurredAtUtc,
    TimeSpan Duration);

/// <summary>
/// Rebuilds a ledger projection without mutating the source journal. Ordering is
/// deterministic so a recovery drill can be reproduced from the same journal.
/// </summary>
public static class LedgerReconstructor
{
    public static LedgerReconstructionResult Rebuild(
        IEnumerable<LedgerEntry> journal,
        IEnumerable<AccountId>? unconstrainedAccounts = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(journal);

        var clock = timeProvider ?? TimeProvider.System;
        var started = clock.GetTimestamp();
        var ledger = new InMemoryLedger(unconstrainedAccounts);
        var ordered = journal
            .Select(static entry => entry ?? throw new ArgumentException("The journal cannot contain null entries.", nameof(journal)))
            .OrderBy(static entry => entry.OccurredAt)
            .ThenBy(static entry => entry.Id.Value)
            .ToArray();

        foreach (var entry in ordered)
        {
            ledger.Apply(entry);
        }

        return new(
            ledger,
            ordered.Length,
            ordered.Length == 0 ? null : ordered[^1].OccurredAt.ToUniversalTime(),
            Stopwatch.GetElapsedTime(started, clock.GetTimestamp()));
    }
}
