using Hindsight.Core.Context;

namespace Hindsight.Core.Tests;

public class OverloadGuardTests
{
    [Fact]
    public void Trips_AfterTenConsecutiveHighReads_Once()
    {
        var g = new OverloadGuard();
        for (int i = 0; i < 9; i++) Assert.False(g.Observe(5000));
        Assert.False(g.Observe(10));            // resets the streak
        for (int i = 0; i < 9; i++) Assert.False(g.Observe(1001));
        Assert.True(g.Observe(1001));
        Assert.True(g.Tripped);
        Assert.False(g.Observe(9999));          // only reported once
        Assert.True(g.Tripped);
    }
}
