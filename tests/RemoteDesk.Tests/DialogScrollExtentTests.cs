using Xunit;

namespace RemoteDesk.Tests;

public sealed class DialogScrollExtentTests
{
    [Theory]
    [InlineData(674, 430, 480, 630, 674, 594, false)]
    [InlineData(674, 430, 480, 630, 674, 630, true)]
    [InlineData(320, 230, 480, 665, 320, 665, false)]
    [InlineData(320, 230, 480, 665, 480, 665, true)]
    [InlineData(1000, 700, 480, 559, 1000, 700, true)]
    [InlineData(1000, 700, 480, 559, 1000, 1000, false)]
    public void DetectsStaleRangeWithoutReschedulingSettledLayout(
        int viewportWidth, int viewportHeight, int requiredWidth, int requiredHeight,
        int displayWidth, int displayHeight, bool expected)
    {
        Assert.Equal(expected, ResponsiveWindowLayout.IsScrollExtentCurrent(
            new Size(viewportWidth, viewportHeight), new Size(requiredWidth, requiredHeight),
            new Size(displayWidth, displayHeight)));
    }
}
