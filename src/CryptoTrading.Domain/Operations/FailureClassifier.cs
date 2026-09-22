using System.Net;
using System.Net.Sockets;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Domain.Runtime;

namespace CryptoTrading.Domain.Operations;

/// <summary>Operational severity classification for runtime and adapter errors.</summary>
public enum FailureSeverity
{
    Transient,
    Unsafe,
    Unknown,
    Terminal,
}

/// <summary>Deterministic routing outcome derived by the failure classifier.</summary>
public sealed record FailureClassification(
    FailureSeverity Severity,
    RuntimeState TargetState,
    string Action,
    string ReasonCode,
    TimeSpan? SuggestedBackoff = null);

/// <summary>
/// Pure domain failure classifier. Maps exceptions into explicit operational
/// severities, target runtime states and safe actions without modifying state.
/// </summary>
public static class FailureClassifier
{
    public static FailureClassification Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is OperationCanceledException)
        {
            return new FailureClassification(
                FailureSeverity.Transient,
                RuntimeState.Degraded,
                "CancelOperation",
                "OPERATION_CANCELED");
        }

        if (exception is TimeoutException or SocketException)
        {
            return new FailureClassification(
                FailureSeverity.Transient,
                RuntimeState.Degraded,
                "RetryWithBackoff",
                "NETWORK_TIMEOUT_TRANSIENT",
                TimeSpan.FromSeconds(2));
        }

        if (exception is HttpRequestException httpEx)
        {
            if (httpEx.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new FailureClassification(
                    FailureSeverity.Terminal,
                    RuntimeState.KillSwitch,
                    "TriggerKillSwitch",
                    "AUTHENTICATION_SECURITY_VIOLATION");
            }

            if ((int?)httpEx.StatusCode is 429 or 418)
            {
                return new FailureClassification(
                    FailureSeverity.Transient,
                    RuntimeState.Degraded,
                    "BackoffRateLimit",
                    "RATE_LIMIT_EXCEEDED",
                    TimeSpan.FromSeconds(5));
            }

            return new FailureClassification(
                FailureSeverity.Transient,
                RuntimeState.Degraded,
                "RetryNetwork",
                "HTTP_TRANSIENT",
                TimeSpan.FromSeconds(1));
        }

        if (exception is InvalidOrderTransitionException)
        {
            return new FailureClassification(
                FailureSeverity.Terminal,
                RuntimeState.KillSwitch,
                "TriggerKillSwitch",
                "INVALID_ORDER_LIFECYCLE_TRANSITION");
        }

        if (exception is OperationalControlResetDeniedException)
        {
            return new FailureClassification(
                FailureSeverity.Terminal,
                RuntimeState.KillSwitch,
                "TriggerKillSwitch",
                "UNAUTHORIZED_RESET_ATTEMPT");
        }

        if (exception.Message.Contains("withdrawals enabled", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("KILL_SWITCH", StringComparison.OrdinalIgnoreCase))
        {
            return new FailureClassification(
                FailureSeverity.Terminal,
                RuntimeState.KillSwitch,
                "TriggerKillSwitch",
                "SECURITY_WITHDRAWALS_OR_KILL_SWITCH");
        }

        if (exception.Message.Contains("drift", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("UNKNOWN_ORDER", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("stale", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("SafeMode", StringComparison.OrdinalIgnoreCase))
        {
            return new FailureClassification(
                FailureSeverity.Unsafe,
                RuntimeState.SafeMode,
                "EnterSafeMode",
                "DATA_OR_STATE_DRIFT_UNSAFE");
        }

        // Default fail-safe: any unhandled exception routes to SafeMode to fail closed without risking exposure
        return new FailureClassification(
            FailureSeverity.Unknown,
            RuntimeState.SafeMode,
            "QuarantineAndEnterSafeMode",
            "UNKNOWN_SYSTEM_FAILURE");
    }
}
