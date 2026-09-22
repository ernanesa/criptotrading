using System.Net;
using System.Net.Sockets;
using CryptoTrading.Domain.Operations;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Domain.Runtime;
using Xunit;

namespace CryptoTrading.Domain.Tests.Operations;

public sealed class FailureClassifierTests
{
    [Fact]
    public void ClassifyOperationCanceledReturnsTransient()
    {
        var result = FailureClassifier.Classify(new OperationCanceledException());

        Assert.Equal(FailureSeverity.Transient, result.Severity);
        Assert.Equal(RuntimeState.Degraded, result.TargetState);
        Assert.Equal("OPERATION_CANCELED", result.ReasonCode);
    }

    [Fact]
    public void ClassifyTimeoutReturnsTransientWithBackoff()
    {
        var result = FailureClassifier.Classify(new TimeoutException("Timed out"));

        Assert.Equal(FailureSeverity.Transient, result.Severity);
        Assert.Equal(RuntimeState.Degraded, result.TargetState);
        Assert.Equal("NETWORK_TIMEOUT_TRANSIENT", result.ReasonCode);
        Assert.NotNull(result.SuggestedBackoff);
    }

    [Fact]
    public void ClassifySocketExceptionReturnsTransient()
    {
        var result = FailureClassifier.Classify(new SocketException((int)SocketError.ConnectionReset));

        Assert.Equal(FailureSeverity.Transient, result.Severity);
        Assert.Equal(RuntimeState.Degraded, result.TargetState);
    }

    [Fact]
    public void ClassifyRateLimitReturnsTransientWithBackoff()
    {
        var result = FailureClassifier.Classify(new HttpRequestException("Too Many Requests", null, (HttpStatusCode)429));

        Assert.Equal(FailureSeverity.Transient, result.Severity);
        Assert.Equal(RuntimeState.Degraded, result.TargetState);
        Assert.Equal("RATE_LIMIT_EXCEEDED", result.ReasonCode);
        Assert.Equal(TimeSpan.FromSeconds(5), result.SuggestedBackoff);
    }

    [Fact]
    public void ClassifyUnauthorizedHttpReturnsTerminalKillSwitch()
    {
        var result = FailureClassifier.Classify(new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden));

        Assert.Equal(FailureSeverity.Terminal, result.Severity);
        Assert.Equal(RuntimeState.KillSwitch, result.TargetState);
        Assert.Equal("AUTHENTICATION_SECURITY_VIOLATION", result.ReasonCode);
    }

    [Fact]
    public void ClassifyInvalidOrderTransitionReturnsTerminalKillSwitch()
    {
        var result = FailureClassifier.Classify(new InvalidOrderTransitionException("Transition impossible"));

        Assert.Equal(FailureSeverity.Terminal, result.Severity);
        Assert.Equal(RuntimeState.KillSwitch, result.TargetState);
        Assert.Equal("INVALID_ORDER_LIFECYCLE_TRANSITION", result.ReasonCode);
    }

    [Fact]
    public void ClassifyUnauthorizedResetReturnsTerminalKillSwitch()
    {
        var result = FailureClassifier.Classify(new OperationalControlResetDeniedException("Denied"));

        Assert.Equal(FailureSeverity.Terminal, result.Severity);
        Assert.Equal(RuntimeState.KillSwitch, result.TargetState);
        Assert.Equal("UNAUTHORIZED_RESET_ATTEMPT", result.ReasonCode);
    }

    [Fact]
    public void ClassifyWithdrawalsEnabledReturnsTerminalKillSwitch()
    {
        var result = FailureClassifier.Classify(new InvalidOperationException("Active RiskPolicy has withdrawals enabled"));

        Assert.Equal(FailureSeverity.Terminal, result.Severity);
        Assert.Equal(RuntimeState.KillSwitch, result.TargetState);
        Assert.Equal("SECURITY_WITHDRAWALS_OR_KILL_SWITCH", result.ReasonCode);
    }

    [Fact]
    public void ClassifyDriftReturnsUnsafeSafeMode()
    {
        var result = FailureClassifier.Classify(new InvalidOperationException("POSITION_BASIS_DRIFT detected"));

        Assert.Equal(FailureSeverity.Unsafe, result.Severity);
        Assert.Equal(RuntimeState.SafeMode, result.TargetState);
        Assert.Equal("DATA_OR_STATE_DRIFT_UNSAFE", result.ReasonCode);
    }

    [Fact]
    public void ClassifyUnhandledExceptionDefaultsToUnknownSafeMode()
    {
        var result = FailureClassifier.Classify(new ArgumentOutOfRangeException("unknown", "Unexpected value"));

        Assert.Equal(FailureSeverity.Unknown, result.Severity);
        Assert.Equal(RuntimeState.SafeMode, result.TargetState);
        Assert.Equal("UNKNOWN_SYSTEM_FAILURE", result.ReasonCode);
    }
}
