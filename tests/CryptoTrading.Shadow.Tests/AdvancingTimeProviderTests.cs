using Xunit;

namespace CryptoTrading.Shadow.Tests;

public sealed class AdvancingTimeProviderTests
{
    [Fact]
    public void RejectsBackwardMovementInsteadOfSilentlyKeepingLaterTime()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new AdvancingTimeProvider(start);
        clock.AdvanceTo(start.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(() => clock.AdvanceTo(start));
        Assert.Equal(start.AddMinutes(1), clock.GetUtcNow());
    }
}
