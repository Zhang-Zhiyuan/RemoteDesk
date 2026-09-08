using Xunit;

namespace RemoteDesk.Tests;

public sealed class CaptureTargetAvailabilityStatusTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StatusRoundTripsWithReadableTextBeforeMachineTrailer(
        bool available)
    {
        var target = new CaptureTargetInfo(
            @"\\.\DISPLAY2|usb",
            "屏幕 2 | 2560x1440");
        CaptureTargetAvailabilityStatusData created =
            CaptureTargetAvailabilityStatusCodec.Create(
                available,
                target,
                targetGeneration: 37);

        string wire =
            CaptureTargetAvailabilityStatusCodec.Encode(
                created);

        Assert.StartsWith(
            created.DisplayMessage + "\n",
            wire,
            StringComparison.Ordinal);
        Assert.True(
            CaptureTargetAvailabilityStatusCodec.TryParse(
                wire,
                out CaptureTargetAvailabilityStatusData parsed));
        Assert.Equal(created, parsed);
    }

    [Theory]
    [InlineData("RemoteDesk.CaptureTargetStatus/v1|unavailable|QQ==|Qg==")]
    [InlineData("剪贴板里提到 RemoteDesk.CaptureTargetStatus/v1|unavailable|QQ==|Qg==")]
    [InlineData("普通文本\nRemoteDesk.CaptureTargetStatus/v10|unavailable|QQ==|Qg==")]
    [InlineData("普通文本\nRemoteDesk.CaptureTargetStatus/v1|unavailable-ish|QQ==|Qg==")]
    [InlineData("普通文本\nRemoteDesk.CaptureTargetStatus/v1|unavailable|***|Qg==")]
    [InlineData("普通文本\nRemoteDesk.CaptureTargetStatus/v1|unavailable|QQ==|Qg==|extra")]
    [InlineData("普通文本\nRemoteDesk.CaptureTargetStatus/v1|unavailable|QQ==|Qg==|-1")]
    [InlineData("普通文本\nRemoteDesk.CaptureTargetStatus/v1|unavailable|QQ==|Qg==|1 ")]
    [InlineData("普通文本\nRemoteDesk.CaptureTargetStatus/v1|unavailable|QQ== |Qg==|1")]
    [InlineData("普通文本\nRemoteDesk.CaptureTargetStatus/v1|unavailable|QQ==|Qg==\n尾随文本")]
    public void SimilarOrTamperedClipboardTextIsNotClassified(
        string message)
    {
        Assert.False(
            CaptureTargetAvailabilityStatusCodec.TryParse(
                message,
                out _));
    }

    [Fact]
    public void DuplicateMachineTrailerIsRejected()
    {
        CaptureTargetAvailabilityStatusData status =
            CaptureTargetAvailabilityStatusCodec.Create(
                false,
                new CaptureTargetInfo("display-2", "屏幕 2"));
        string wire =
            CaptureTargetAvailabilityStatusCodec.Encode(status);
        string duplicate = wire +
            "\n" +
            wire[wire.IndexOf(
                CaptureTargetAvailabilityStatusCodec.MachinePrefix,
                StringComparison.Ordinal)..];

        Assert.False(
            CaptureTargetAvailabilityStatusCodec.TryParse(
                duplicate,
                out _));
    }
}
