using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using Xunit;

namespace CryptoTrading.Api.Tests;

public sealed class OperationalSecurityTests
{
    [Fact]
    public void ApiResetAuthorizerAlwaysDeniesAllCommands()
    {
        var authorizer = new DenyApiOperationalControlResetAuthorizer();
        var snapshot = new OperationalControlSnapshot(
            OperationalMode.SafeMode,
            1L,
            DateTimeOffset.UtcNow,
            "test",
            "test-safe-mode");

        var command = new OperationalControlCommand(
            "admin",
            "attempt-reset",
            new IdempotencyKey("idem-1"),
            DateTimeOffset.UtcNow);

        var auth = new OperationalControlResetAuthorization(
            "approver",
            "auth-1");

        var authorized = authorizer.IsAuthorized(snapshot, command, auth);

        Assert.False(authorized, "API operational control authorizer must strictly deny all reset mutations.");
    }
}
