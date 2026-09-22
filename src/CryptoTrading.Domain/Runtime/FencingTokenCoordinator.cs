namespace CryptoTrading.Domain.Runtime;

/// <summary>Distributed monotonic fencing token preventing split-brain execution and double-trading (US-183).</summary>
public sealed record FencingToken(
    string LeaderNodeId,
    long Epoch,
    DateTimeOffset AcquiredAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    public bool IsExpired(DateTimeOffset nowUtc) => nowUtc >= ExpiresAtUtc;
}

/// <summary>Result of fencing token validation.</summary>
public sealed record FencingValidationResult(
    bool IsValid,
    string Reason);

/// <summary>
/// Distributed fencing coordinator enforcing monotonic epochs across runtime instances (US-183).
/// Protects critical trading and order submission pathways against stale or zombie leaders.
/// </summary>
public sealed class FencingTokenCoordinator
{
    private readonly object _lock = new();
    private FencingToken? _activeToken;
    private long _lastKnownEpoch;

    public FencingToken? ActiveToken
    {
        get
        {
            lock (_lock)
            {
                return _activeToken;
            }
        }
    }

    public long LastKnownEpoch
    {
        get
        {
            lock (_lock)
            {
                return _lastKnownEpoch;
            }
        }
    }

    public FencingToken Acquire(string nodeId, TimeSpan leaseDuration, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "Lease duration must be positive.");
        }

        if (nowUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC.", nameof(nowUtc));
        }

        lock (_lock)
        {
            var nextEpoch = _lastKnownEpoch + 1;
            var token = new FencingToken(nodeId, nextEpoch, nowUtc, nowUtc.Add(leaseDuration));
            _lastKnownEpoch = nextEpoch;
            _activeToken = token;
            return token;
        }
    }

    public FencingValidationResult ValidateExecution(FencingToken token, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(token);

        lock (_lock)
        {
            if (token.Epoch < _lastKnownEpoch)
            {
                return new FencingValidationResult(
                    IsValid: false,
                    Reason: $"STALE_EPOCH: Token epoch {token.Epoch} is superseded by active epoch {_lastKnownEpoch}. Execution rejected to prevent double-trading.");
            }

            if (token.IsExpired(nowUtc))
            {
                return new FencingValidationResult(
                    IsValid: false,
                    Reason: $"TOKEN_EXPIRED: Token expired at {token.ExpiresAtUtc:O} (current: {nowUtc:O}). Execution rejected.");
            }

            if (_activeToken != null && _activeToken.LeaderNodeId != token.LeaderNodeId)
            {
                return new FencingValidationResult(
                    IsValid: false,
                    Reason: $"LEADER_MISMATCH: Current active leader is {_activeToken.LeaderNodeId}, incoming token belongs to {token.LeaderNodeId}.");
            }

            return new FencingValidationResult(
                IsValid: true,
                Reason: "VALID: Token is active, monotonically current, and held by authorized leader.");
        }
    }
}
