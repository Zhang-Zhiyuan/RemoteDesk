using Xunit;

namespace RemoteDesk.Tests;

public sealed class RemoteDeviceCapabilityInfoTests
{
    [Fact]
    public void LocalWindowsAdvertisesFileDropPaste()
    {
        RemoteDeviceCapabilities capabilities = RemoteDeviceCapabilityInfo.LocalWindows(canRemoteStart: false);

        Assert.True(capabilities.HasFlag(RemoteDeviceCapabilities.FileDropPaste));
        Assert.True(capabilities.HasFlag(RemoteDeviceCapabilities.FileSend));
        Assert.True(capabilities.HasFlag(RemoteDeviceCapabilities.FileChecksum));
        Assert.True(capabilities.HasFlag(RemoteDeviceCapabilities.FileTransferCancel));
        Assert.True(capabilities.HasFlag(RemoteDeviceCapabilities.FileTransferPreview));
        Assert.Equal(
            RemoteUpdater.CanApplyRemoteUpdate,
            capabilities.HasFlag(RemoteDeviceCapabilities.RemoteUpdate));
        Assert.True(capabilities.HasFlag(RemoteDeviceCapabilities.ClipboardSequenceTracking));
        Assert.True(capabilities.HasFlag(RemoteDeviceCapabilities.LowLatencyUdpVideo));
        Assert.True(capabilities.HasFlag(RemoteDeviceCapabilities.UdpVideoCongestionFeedback));
        Assert.True(capabilities.HasFlag(RemoteDeviceCapabilities.LowLatencyUdpVideoXorFec));
        Assert.True(capabilities.HasFlag(RemoteDeviceCapabilities.LowLatencyUdpMouseInput));
        Assert.True(capabilities.HasFlag(RemoteDeviceCapabilities.LowLatencyUdpMouseInputAppliedAck));
        // LocalWindows describes host/send capabilities, not the separate
        // Windows viewer capability handshake.
        Assert.True(capabilities.HasFlag(RemoteDeviceCapabilities.ShortGopH264));
        Assert.True(capabilities.HasFlag(
            RemoteDeviceCapabilities.HighFrameRateH264));
        Assert.True(capabilities.HasFlag(
            RemoteDeviceCapabilities.AuthenticatedUdpHeartbeat));
        Assert.True(capabilities.HasFlag(
            RemoteDeviceCapabilities.HighQualityJpeg));
        Assert.Contains("定位拖放", RemoteDeviceCapabilityInfo.Format(capabilities), StringComparison.Ordinal);
        Assert.Contains("文件回传", RemoteDeviceCapabilityInfo.Format(capabilities), StringComparison.Ordinal);
        Assert.Contains("文件校验", RemoteDeviceCapabilityInfo.Format(capabilities), StringComparison.Ordinal);
        Assert.Contains("传输取消", RemoteDeviceCapabilityInfo.Format(capabilities), StringComparison.Ordinal);
        Assert.Contains("传前确认", RemoteDeviceCapabilityInfo.Format(capabilities), StringComparison.Ordinal);
        Assert.Equal(
            RemoteUpdater.CanApplyRemoteUpdate,
            RemoteDeviceCapabilityInfo.Format(capabilities).Contains(
                "远程更新",
                StringComparison.Ordinal));
        Assert.Contains("剪贴板就绪", RemoteDeviceCapabilityInfo.Format(capabilities), StringComparison.Ordinal);
        Assert.Contains("低延迟画面", RemoteDeviceCapabilityInfo.Format(capabilities), StringComparison.Ordinal);
        Assert.Contains("画面拥塞反馈", RemoteDeviceCapabilityInfo.Format(capabilities), StringComparison.Ordinal);
        Assert.Contains("画面 XOR FEC", RemoteDeviceCapabilityInfo.Format(capabilities), StringComparison.Ordinal);
        Assert.Contains("低延迟鼠标", RemoteDeviceCapabilityInfo.Format(capabilities), StringComparison.Ordinal);
        Assert.Contains("鼠标应用回执", RemoteDeviceCapabilityInfo.Format(capabilities), StringComparison.Ordinal);
        Assert.Contains("H.264 短 GOP", RemoteDeviceCapabilityInfo.Format(capabilities), StringComparison.Ordinal);
        Assert.Contains("H.264 60 FPS", RemoteDeviceCapabilityInfo.Format(capabilities), StringComparison.Ordinal);
        Assert.Contains("UDP 活性心跳", RemoteDeviceCapabilityInfo.Format(capabilities), StringComparison.Ordinal);
        Assert.Contains("JPEG 清晰优先", RemoteDeviceCapabilityInfo.Format(capabilities), StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyWindowsDoesNotAssumeFileDropPaste()
    {
        RemoteDeviceCapabilities capabilities = RemoteDeviceCapabilityInfo.LegacyWindows(canRemoteStart: false);

        Assert.False(capabilities.HasFlag(RemoteDeviceCapabilities.FileDropPaste));
        Assert.False(capabilities.HasFlag(RemoteDeviceCapabilities.FileSend));
        Assert.False(capabilities.HasFlag(RemoteDeviceCapabilities.FileChecksum));
        Assert.False(capabilities.HasFlag(RemoteDeviceCapabilities.FileTransferCancel));
        Assert.False(capabilities.HasFlag(RemoteDeviceCapabilities.FileTransferPreview));
        Assert.False(capabilities.HasFlag(RemoteDeviceCapabilities.RemoteUpdate));
        Assert.False(capabilities.HasFlag(RemoteDeviceCapabilities.ClipboardSequenceTracking));
        Assert.False(capabilities.HasFlag(RemoteDeviceCapabilities.LowLatencyUdpVideo));
        Assert.False(capabilities.HasFlag(RemoteDeviceCapabilities.UdpVideoCongestionFeedback));
        Assert.False(capabilities.HasFlag(RemoteDeviceCapabilities.LowLatencyUdpVideoXorFec));
        Assert.False(capabilities.HasFlag(RemoteDeviceCapabilities.LowLatencyUdpMouseInput));
        Assert.False(capabilities.HasFlag(RemoteDeviceCapabilities.LowLatencyUdpMouseInputAppliedAck));
        Assert.False(capabilities.HasFlag(RemoteDeviceCapabilities.ShortGopH264));
        Assert.False(capabilities.HasFlag(
            RemoteDeviceCapabilities.HighFrameRateH264));
        Assert.False(capabilities.HasFlag(
            RemoteDeviceCapabilities.AuthenticatedUdpHeartbeat));
        Assert.False(capabilities.HasFlag(
            RemoteDeviceCapabilities.HighQualityJpeg));
    }

    [Fact]
    public void LowLatencyUdpCapabilityBitsRemainProtocolStable()
    {
        Assert.Equal(1 << 13, (int)RemoteDeviceCapabilities.LowLatencyUdpVideo);
        Assert.Equal(1 << 14, (int)RemoteDeviceCapabilities.UdpVideoCongestionFeedback);
        Assert.Equal(1 << 15, (int)RemoteDeviceCapabilities.LowLatencyUdpVideoXorFec);
        Assert.Equal(1 << 16, (int)RemoteDeviceCapabilities.LowLatencyUdpMouseInput);
        Assert.Equal(1 << 17, (int)RemoteDeviceCapabilities.LowLatencyUdpMouseInputAppliedAck);
        Assert.Equal(1 << 18, (int)RemoteDeviceCapabilities.ShortGopH264);
        Assert.Equal(
            1 << 19,
            (int)RemoteDeviceCapabilities.HighFrameRateH264);
        Assert.Equal(
            1 << 20,
            (int)RemoteDeviceCapabilities.AuthenticatedUdpHeartbeat);
        Assert.Equal(
            1 << 21,
            (int)RemoteDeviceCapabilities.HighQualityJpeg);
    }
}
