using Xunit;

namespace RemoteDesk.Tests;

public sealed class RemoteDeskBuildInfoTests
{
    [Fact]
    public void AssemblyBuildStampIsAvailable()
    {
        Assert.NotNull(RemoteDeskBuildInfo.NormalizeBuildStamp(RemoteDeskBuildInfo.BuildStamp));
    }

    [Fact]
    public void NormalizeBuildStampAcceptsFourteenDigits()
    {
        Assert.Equal("20260623010203", RemoteDeskBuildInfo.NormalizeBuildStamp(" 20260623010203 "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("dev")]
    [InlineData("2026062301020")]
    [InlineData("2026062301020x")]
    public void NormalizeBuildStampRejectsInvalidValues(string stamp)
    {
        Assert.Null(RemoteDeskBuildInfo.NormalizeBuildStamp(stamp));
    }

    [Fact]
    public void CompareBuildStampsOrdersByTimestamp()
    {
        Assert.True(RemoteDeskBuildInfo.CompareBuildStamps("20260623010204", "20260623010203") > 0);
        Assert.True(RemoteDeskBuildInfo.CompareBuildStamps("20260623010202", "20260623010203") < 0);
        Assert.Equal(0, RemoteDeskBuildInfo.CompareBuildStamps("20260623010203", "20260623010203"));
    }

    [Fact]
    public void CompareBuildStampsReturnsNullForUnknownValues()
    {
        Assert.Null(RemoteDeskBuildInfo.CompareBuildStamps("dev", "20260623010203"));
    }
}
