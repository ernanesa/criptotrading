namespace CryptoTrading.Persistence;

/// <summary>Result of comparing two local full-state recovery manifests.</summary>
public sealed record FullStateReconciliationResult(
    bool IsEquivalent,
    IReadOnlyList<string> Mismatches,
    string ExpectedManifestSha256,
    string ActualManifestSha256);

/// <summary>
/// Compares restored local state with a pre-restore manifest without mutating
/// the database. Any mismatch is explicit and fail-closed for promotion use.
/// </summary>
public static class FullStateReconciler
{
    public static FullStateReconciliationResult Compare(
        FullStateReconstructionManifest expected,
        FullStateReconstructionManifest actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        var mismatches = new List<string>();
        CompareValue("manifest_sha256", expected.ManifestSha256, actual.ManifestSha256, mismatches);
        CompareValue("ledger_entries_applied", expected.Ledger.EntriesApplied, actual.Ledger.EntriesApplied, mismatches);
        CompareValue("ledger_last_occurred_at_utc", expected.Ledger.LastOccurredAtUtc, actual.Ledger.LastOccurredAtUtc, mismatches);
        CompareValue("open_order_count", expected.OpenOrderCount, actual.OpenOrderCount, mismatches);
        CompareValue("account_stream_event_count", expected.AccountStreamEventCount, actual.AccountStreamEventCount, mismatches);
        CompareValue("outbox_recovery_record_count", expected.OutboxRecoveryRecordCount, actual.OutboxRecoveryRecordCount, mismatches);
        CompareValue("has_all_local_evidence", expected.HasAllLocalEvidence, actual.HasAllLocalEvidence, mismatches);

        return new(
            mismatches.Count == 0,
            mismatches.AsReadOnly(),
            expected.ManifestSha256,
            actual.ManifestSha256);
    }

    private static void CompareValue<T>(string name, T expected, T actual, List<string> mismatches)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            mismatches.Add(name);
        }
    }
}
