using Xunit;

namespace RemoteDesk.Tests;

public sealed class RemoteViewerClientVideoModeTests
{
    [Fact]
    public void RelayTransportKeepsTcpFeaturesAndDoesNotAdvertiseUdpRoutes()
    {
        RemoteDeviceCapabilities capabilities =
            RemoteViewerClient.RelayTransportViewerCapabilities;

        Assert.True(capabilities.HasFlag(
            RemoteDeviceCapabilities.FileChecksum));
        Assert.True(capabilities.HasFlag(
            RemoteDeviceCapabilities.HighQualityJpeg));
        Assert.False(capabilities.HasFlag(
            RemoteDeviceCapabilities.LowLatencyUdpVideo));
        Assert.False(capabilities.HasFlag(
            RemoteDeviceCapabilities.UdpVideoCongestionFeedback));
        Assert.False(capabilities.HasFlag(
            RemoteDeviceCapabilities.LowLatencyUdpMouseInput));
        Assert.False(capabilities.HasFlag(
            RemoteDeviceCapabilities.AuthenticatedUdpHeartbeat));
        Assert.False(capabilities.HasFlag(
            RemoteDeviceCapabilities.HighFrameRateH264));
        Assert.False(capabilities.HasFlag(
            RemoteDeviceCapabilities.ShortGopH264));
    }

    [Fact]
    public void ResolveSupportedVideoCodecsUsesH264WithJpegFallbackForAutomaticModeWhenFfmpegExists()
    {
        RemoteVideoCodecs codecs = RemoteViewerClient.ResolveSupportedVideoCodecs(
            ViewerVideoMode.Automatic,
            "ffmpeg.exe",
            hasNativeHardwareDecoder: false);

        Assert.True(codecs.HasFlag(RemoteVideoCodecs.Jpeg));
        Assert.True(codecs.HasFlag(RemoteVideoCodecs.H264AnnexB));
    }

    [Fact]
    public void ResolveSupportedVideoCodecsFallsBackToJpegForAutomaticModeWithoutFfmpeg()
    {
        RemoteVideoCodecs codecs = RemoteViewerClient.ResolveSupportedVideoCodecs(
            ViewerVideoMode.Automatic,
            null,
            hasNativeHardwareDecoder: false);

        Assert.Equal(RemoteVideoCodecs.Jpeg, codecs);
    }

    [Fact]
    public void ResolveSupportedVideoCodecsUsesOnlyJpegForStableMode()
    {
        RemoteVideoCodecs codecs = RemoteViewerClient.ResolveSupportedVideoCodecs(
            ViewerVideoMode.StableJpeg,
            "ffmpeg.exe",
            hasNativeHardwareDecoder: true);

        Assert.Equal(RemoteVideoCodecs.Jpeg, codecs);
    }

    [Fact]
    public void ResolveSupportedVideoCodecsUsesOnlyH264ForForceMode()
    {
        RemoteVideoCodecs codecs = RemoteViewerClient.ResolveSupportedVideoCodecs(
            ViewerVideoMode.ForceH264,
            "ffmpeg.exe",
            hasNativeHardwareDecoder: false);

        Assert.Equal(RemoteVideoCodecs.H264AnnexB, codecs);
    }

    [Fact]
    public void ResolveSupportedVideoCodecsRejectsForceModeWithoutFfmpeg()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RemoteViewerClient.ResolveSupportedVideoCodecs(
                ViewerVideoMode.ForceH264,
                ffmpegPath: null,
                hasNativeHardwareDecoder: false));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, false)]
    public void ResolveSupportedVideoCodecsUsesNativeHardwareWithoutFfmpeg(
        int videoModeValue,
        bool expectsJpegFallback)
    {
        RemoteVideoCodecs codecs =
            RemoteViewerClient.ResolveSupportedVideoCodecs(
                (ViewerVideoMode)videoModeValue,
                ffmpegPath: null,
                hasNativeHardwareDecoder: true);

        Assert.True(
            codecs.HasFlag(
                RemoteVideoCodecs.H264AnnexB));
        Assert.Equal(
            expectsJpegFallback,
            codecs.HasFlag(
                RemoteVideoCodecs.Jpeg));
    }

    [Fact]
    public void ShortGopCapabilityRequiresProtectedUdpTier()
    {
        const RemoteDeviceCapabilities prerequisites =
            RemoteDeviceCapabilities.LowLatencyUdpVideo |
            RemoteDeviceCapabilities.UdpVideoCongestionFeedback |
            RemoteDeviceCapabilities.LowLatencyUdpVideoXorFec;

        RemoteDeviceCapabilities enabled =
            RemoteViewerClient.ResolveLocalViewerCapabilities(
                prerequisites,
                enableShortGopH264: true);
        Assert.True(enabled.HasFlag(
            RemoteDeviceCapabilities.ShortGopH264));

        foreach (RemoteDeviceCapabilities missing in
            new[]
            {
                RemoteDeviceCapabilities.LowLatencyUdpVideo,
                RemoteDeviceCapabilities.UdpVideoCongestionFeedback,
                RemoteDeviceCapabilities.LowLatencyUdpVideoXorFec
            })
        {
            RemoteDeviceCapabilities incomplete =
                RemoteViewerClient.ResolveLocalViewerCapabilities(
                    (prerequisites & ~missing) |
                        RemoteDeviceCapabilities.ShortGopH264,
                    enableShortGopH264: true);
            Assert.False(incomplete.HasFlag(
                RemoteDeviceCapabilities.ShortGopH264));
        }
    }

    [Fact]
    public void ShortGopCapabilityCanBeRolledBackWithoutRemovingUdpTier()
    {
        const RemoteDeviceCapabilities baseline =
            RemoteDeviceCapabilities.LowLatencyUdpVideo |
            RemoteDeviceCapabilities.UdpVideoCongestionFeedback |
            RemoteDeviceCapabilities.LowLatencyUdpVideoXorFec;

        RemoteDeviceCapabilities capabilities =
            RemoteViewerClient.ResolveLocalViewerCapabilities(
                baseline |
                    RemoteDeviceCapabilities.ShortGopH264,
                enableShortGopH264: false);

        Assert.True(capabilities.HasFlag(
            RemoteDeviceCapabilities.LowLatencyUdpVideo));
        Assert.True(capabilities.HasFlag(
            RemoteDeviceCapabilities.UdpVideoCongestionFeedback));
        Assert.True(capabilities.HasFlag(
            RemoteDeviceCapabilities.LowLatencyUdpVideoXorFec));
        Assert.False(capabilities.HasFlag(
            RemoteDeviceCapabilities.ShortGopH264));
    }

    [Fact]
    public void ShortGopGatePreservesHighFrameRateCapability()
    {
        const RemoteDeviceCapabilities baseline =
            RemoteDeviceCapabilities.HighFrameRateH264;

        RemoteDeviceCapabilities capabilities =
            RemoteViewerClient.ResolveLocalViewerCapabilities(
                baseline,
                enableShortGopH264: false);

        Assert.True(capabilities.HasFlag(
            RemoteDeviceCapabilities.HighFrameRateH264));
    }

    [Fact]
    public void ProductionViewerDoesNotNegotiateRemoteShortGopCapability()
    {
        Assert.True(
            RemoteViewerClient
                .LocalH264SamplesAreAllIndependent);

        const RemoteDeviceCapabilities remoteCapabilities =
            RemoteDeviceCapabilities.LowLatencyUdpVideo |
            RemoteDeviceCapabilities.UdpVideoCongestionFeedback |
            RemoteDeviceCapabilities.LowLatencyUdpVideoXorFec |
            RemoteDeviceCapabilities.ShortGopH264 |
            RemoteDeviceCapabilities.AuthenticatedUdpHeartbeat;

        LowLatencyVideoFeatures features =
            RemoteViewerClient
                .ResolveNegotiatedLowLatencyVideoFeatures(
                    remoteCapabilities);

        Assert.True(features.HasFlag(
            LowLatencyVideoFeatures.CongestionFeedback));
        Assert.True(features.HasFlag(
            LowLatencyVideoFeatures.XorFec));
        Assert.False(features.HasFlag(
            LowLatencyVideoFeatures.ShortGopH264));
        Assert.True(features.HasFlag(
            LowLatencyVideoFeatures.AuthenticatedHeartbeat));
    }
}
