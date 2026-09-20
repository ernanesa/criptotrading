using Xunit;

namespace CryptoTrading.Observability.Tests;

public sealed class LocalDiagnosticsAccessPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.42.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void LoopbackAddressesAreAllowed(string address) =>
        Assert.True(LocalDiagnosticsAccessPolicy.IsLoopback(IPAddress.Parse(address)));

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("::ffff:192.168.1.1")]
    public void NonLoopbackAddressesAreDenied(string address) =>
        Assert.False(LocalDiagnosticsAccessPolicy.IsLoopback(IPAddress.Parse(address)));

    [Fact]
    public void MissingRemoteAddressIsDenied() =>
        Assert.False(LocalDiagnosticsAccessPolicy.IsLoopback(null));
}
