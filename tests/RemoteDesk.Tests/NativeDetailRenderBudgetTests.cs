using System.Diagnostics;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class NativeDetailRenderBudgetTests
{
    [Fact]
    public void OnlyExplicitLocalHeadroomAllowsDetail()
    {
        long now = Stopwatch.Frequency * 10;
        Assert.False(default(NativeDetailRenderBudget).Allows(now));
        Assert.False(new NativeDetailRenderBudget(0).Allows(0));
        Assert.False(new NativeDetailRenderBudget(now).Allows(now));
        Assert.False(new NativeDetailRenderBudget(now - 1).Allows(now));
        Assert.False(new NativeDetailRenderBudget(now + Stopwatch.Frequency / 1000).Allows(now));
        Assert.True(new NativeDetailRenderBudget(now + Stopwatch.Frequency / 100).Allows(now));
        Assert.False(new NativeDetailRenderBudget(long.MaxValue).Allows(-1));
        Assert.False(new NativeDetailRenderBudget(-1).Allows(now));
        Assert.False(new NativeDetailRenderBudget(long.MaxValue).Allows(long.MaxValue - 1));
        Assert.True(new NativeDetailRenderBudget(long.MaxValue).Allows(0));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void InputBaseBacklogAndCongestionAlwaysWin(bool input, bool backlog, bool congestion) =>
        Assert.False(new NativeDetailRenderBudget(long.MaxValue, input, backlog, congestion).Allows(1));
}
