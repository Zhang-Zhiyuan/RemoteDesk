using Xunit;

namespace RemoteDesk.Tests;

public sealed class WindowsH264VideoSignalTests
{
    // SPS samples from the old and current hardware encoder and from a
    // tagged studio-range stream replayed inside a native-detail envelope.
    private const string OldNative = "6764001FAC2B402802DD808800001F400007530078E155";
    private const string Full709 = "6764001FAC2B402802DD80B7010001400000FA00003A9803C70AA8";
    private const string Studio709 = "674D402895900780227E5C05A808680A000007D00001D4C1C000001312D000051615779447C70A92";

    [Fact]
    public void ReusingAConnectionDistinguishesUntaggedNativeFromBothTaggedRanges()
    {
        var signal = new WindowsH264VideoSignal();
        foreach (var sps in new[] { OldNative, OldNative, Studio709, OldNative, Full709, Studio709 })
        {
            // Mixed three- and four-byte start codes with a leading AUD.
            byte[] packet = Convert.FromHexString("00000109F000000001" + sps + "0000000168EE03C8");
            Assert.Equal(sps == OldNative, signal.UsesDriverDefaults(packet));
            Assert.Equal(sps == OldNative, signal.UsesDriverDefaults(Convert.FromHexString("00000141BC")));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("00000165BC")]
    [InlineData("0000016764001FAC00000168")]
    [InlineData("0000016764001FAC2B402802DD808800001F400007530078E155")]
    public void IncompleteOrMissingHeaderKeepsDecoderColorInformation(string hex)
    {
        var signal = new WindowsH264VideoSignal();
        Assert.False(signal.UsesDriverDefaults(Convert.FromHexString(hex)));
    }

    [Theory]
    [InlineData(54, 99, 255, true, true)]
    [InlineData(63, 102, 240, false, true)]
    [InlineData(76, 85, 255, true, false)]
    [InlineData(81, 90, 240, false, false)]
    public void SyntheticRedIdentifiesTheActualGpuConversion(byte y, byte u, byte v, bool full, bool bt709)
    {
        var actual = FfmpegDesktopH264Capture.IdentifySyntheticRed([y, y, y, y, u, v]);
        Assert.Equal(new SdrVideoColorSpace(full, bt709 ? Vortice.MediaFoundation.VideoTransferMatrix.Bt709 :
            Vortice.MediaFoundation.VideoTransferMatrix.Bt601), actual);
        Assert.Null(FfmpegDesktopH264Capture.IdentifySyntheticRed([0, 0, 0, 0, 128, 128]));
    }
}
