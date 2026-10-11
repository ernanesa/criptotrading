using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CryptoTrading.Domain.Risk;

/// <summary>Lifecycle of a risk-policy document. Transitions are controlled by the persistence boundary.</summary>
public enum RiskPolicyLifecycle
{
    Draft,
    Approved,
    Active,
    Superseded,
    Revoked,
}

/// <summary>
/// Immutable, auditable risk-policy artifact. The hash covers only the policy
/// values and validity window, never mutable approval metadata.
/// </summary>
public sealed record RiskPolicyDocument
{
    public RiskPolicyDocument(
        RiskPolicy policy,
        DateTimeOffset effectiveFromUtc,
        DateTimeOffset expiresAtUtc,
        string createdBy,
        string documentHash,
        RiskPolicyLifecycle lifecycle = RiskPolicyLifecycle.Draft,
        string? approvedBy = null,
        string? approvalId = null,
        DateTimeOffset? approvedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ValidateUtc(effectiveFromUtc, nameof(effectiveFromUtc));
        ValidateUtc(expiresAtUtc, nameof(expiresAtUtc));
        if (expiresAtUtc <= effectiveFromUtc)
        {
            throw new ArgumentException("Risk-policy expiry must be after its effective time.", nameof(expiresAtUtc));
        }

        var normalizedCreatedBy = ValidateActor(createdBy, nameof(createdBy));
        var normalizedDocumentHash = ValidateHash(documentHash);
        if (approvedAtUtc is { } approvedAt)
        {
            ValidateUtc(approvedAt, nameof(approvedAtUtc));
        }

        var normalizedApprovedBy = approvedBy is null ? null : ValidateActor(approvedBy, nameof(approvedBy));
        var normalizedApprovalId = approvalId is null ? null : ValidateActor(approvalId, nameof(approvalId));
        if (normalizedApprovedBy is not null && string.Equals(normalizedCreatedBy, normalizedApprovedBy, StringComparison.Ordinal))
        {
            throw new ArgumentException("Risk-policy approval must be performed by an actor different from the creator.", nameof(approvedBy));
        }

        if (lifecycle is RiskPolicyLifecycle.Approved or RiskPolicyLifecycle.Active &&
            (normalizedApprovedBy is null || normalizedApprovalId is null || approvedAtUtc is null))
        {
            throw new ArgumentException("Approved and active policies require approval metadata.", nameof(lifecycle));
        }

        if (lifecycle == RiskPolicyLifecycle.Draft &&
            (normalizedApprovedBy is not null || normalizedApprovalId is not null || approvedAtUtc is not null))
        {
            throw new ArgumentException("Draft policies cannot contain approval metadata.", nameof(lifecycle));
        }

        Policy = policy;
        EffectiveFromUtc = effectiveFromUtc;
        ExpiresAtUtc = expiresAtUtc;
        CreatedBy = normalizedCreatedBy;
        DocumentHash = normalizedDocumentHash;
        Lifecycle = lifecycle;
        ApprovedBy = normalizedApprovedBy;
        ApprovalId = normalizedApprovalId;
        ApprovedAtUtc = approvedAtUtc;

        var expectedHash = ComputeHash(policy, effectiveFromUtc, expiresAtUtc);
        if (!string.Equals(normalizedDocumentHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Risk-policy document hash does not match its canonical content.", nameof(documentHash));
        }
    }

    public RiskPolicy Policy { get; }
    public DateTimeOffset EffectiveFromUtc { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public string CreatedBy { get; }
    public string DocumentHash { get; }
    public RiskPolicyLifecycle Lifecycle { get; }
    public string? ApprovedBy { get; }
    public string? ApprovalId { get; }
    public DateTimeOffset? ApprovedAtUtc { get; }

    public static RiskPolicyDocument CreateDraft(
        RiskPolicy policy,
        DateTimeOffset effectiveFromUtc,
        DateTimeOffset expiresAtUtc,
        string createdBy) =>
        new(policy, effectiveFromUtc, expiresAtUtc, createdBy,
            ComputeHash(policy, effectiveFromUtc, expiresAtUtc));

    public RiskPolicyDocument Approve(string approver, string approvalId, DateTimeOffset approvedAtUtc)
    {
        ValidateUtc(approvedAtUtc, nameof(approvedAtUtc));
        if (approvedAtUtc < EffectiveFromUtc || approvedAtUtc >= ExpiresAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(approvedAtUtc), "Approval must occur within the policy validity window.");
        }

        if (Lifecycle is not RiskPolicyLifecycle.Draft)
        {
            throw new InvalidOperationException("Only draft risk policies can be approved.");
        }

        return new RiskPolicyDocument(
            Policy, EffectiveFromUtc, ExpiresAtUtc, CreatedBy, DocumentHash,
            RiskPolicyLifecycle.Approved, approver, approvalId, approvedAtUtc);
    }

    public RiskPolicyDocument Activate(DateTimeOffset activatedAtUtc)
    {
        ValidateUtc(activatedAtUtc, nameof(activatedAtUtc));
        if (Lifecycle != RiskPolicyLifecycle.Approved)
        {
            throw new InvalidOperationException("Only approved risk policies can be activated.");
        }

        if (activatedAtUtc < EffectiveFromUtc || activatedAtUtc >= ExpiresAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(activatedAtUtc), "A policy cannot be activated outside its validity window.");
        }

        return new RiskPolicyDocument(
            Policy, EffectiveFromUtc, ExpiresAtUtc, CreatedBy, DocumentHash,
            RiskPolicyLifecycle.Active, ApprovedBy, ApprovalId, ApprovedAtUtc);
    }

    public bool IsValidAt(DateTimeOffset atUtc) =>
        atUtc.Offset == TimeSpan.Zero &&
        atUtc >= EffectiveFromUtc &&
        atUtc < ExpiresAtUtc &&
        Lifecycle == RiskPolicyLifecycle.Active;

    public static string ComputeHash(RiskPolicy policy, DateTimeOffset effectiveFromUtc, DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ValidateUtc(effectiveFromUtc, nameof(effectiveFromUtc));
        ValidateUtc(expiresAtUtc, nameof(expiresAtUtc));
        if (expiresAtUtc <= effectiveFromUtc)
        {
            throw new ArgumentException("Risk-policy expiry must be after its effective time.", nameof(expiresAtUtc));
        }

        var canonical = string.Join('|',
            policy.Version,
            Decimal(policy.MaxOrderNotional),
            Decimal(policy.MaxExposureNotional),
            Decimal(policy.MaxDailyLoss),
            Decimal(policy.MaxDrawdown),
            policy.MaxMarketDataAge.Ticks.ToString(CultureInfo.InvariantCulture),
            policy.WithdrawalsEnabled ? "true" : "false",
            NullableDecimal(policy.MaxSlippageBps),
            NullableDecimal(policy.MaxVolatilityBps),
            NullableDecimal(policy.MinLiquidityNotional),
            CanonicalUtcTicks(effectiveFromUtc).ToString(CultureInfo.InvariantCulture),
            CanonicalUtcTicks(expiresAtUtc).ToString(CultureInfo.InvariantCulture));
        if (policy.BudgetPolicy is not null)
        {
            canonical = string.Concat(canonical, "|", policy.BudgetPolicy.ToCanonicalComponent());
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string Decimal(decimal value) => value.ToString("0.##############################", CultureInfo.InvariantCulture);
    private static string NullableDecimal(decimal? value) => value is { } number ? Decimal(number) : "null";

    // PostgreSQL timestamptz stores microseconds. Canonicalizing at that
    // boundary keeps a persisted document hash stable across restart/replay.
    private static long CanonicalUtcTicks(DateTimeOffset value) =>
        value.ToUniversalTime().Ticks / 10 * 10;

    private static string ValidateActor(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.Length > 200)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Actor values cannot exceed 200 characters.");
        }

        return normalized;
    }

    private static string ValidateHash(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length != 64 || normalized.Any(static character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException("Risk-policy document hash must be a SHA-256 hexadecimal value.", nameof(value));
        }

        return normalized;
    }

    private static void ValidateUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Risk-policy timestamps must be UTC.", parameterName);
        }
    }
}
