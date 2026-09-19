namespace RemoteDesk;

internal sealed record RemoteDeviceDescriptor(
    string MachineName,
    string Platform,
    RemoteDeviceCapabilities Capabilities,
    string? BuildStamp = null,
    string? DeviceId = null);

[Flags]
internal enum RemoteDeviceCapabilities
{
    None = 0,
    RemoteDesktop = 1 << 0,
    InputControl = 1 << 1,
    ClipboardText = 1 << 2,
    FileReceive = 1 << 3,
    CaptureTargetSelection = 1 << 4,
    RemoteStart = 1 << 5,
    FileDropPaste = 1 << 6,
    FileSend = 1 << 7,
    FileChecksum = 1 << 8,
    FileTransferCancel = 1 << 9,
    FileTransferPreview = 1 << 10,
    RemoteUpdate = 1 << 11,
    ClipboardSequenceTracking = 1 << 12,
    LowLatencyUdpVideo = 1 << 13,
    UdpVideoCongestionFeedback = 1 << 14,
    LowLatencyUdpVideoXorFec = 1 << 15,
    LowLatencyUdpMouseInput = 1 << 16,
    LowLatencyUdpMouseInputAppliedAck = 1 << 17,
    ShortGopH264 = 1 << 18,
    HighFrameRateH264 = 1 << 19,
    AuthenticatedUdpHeartbeat = 1 << 20,
    HighQualityJpeg = 1 << 21,
    DeviceIdentity = 1 << 22,
    ClipboardPasteShortcut = 1 << 23,
    FileTransferReceipt = 1 << 24,
    HostVideoDiagnostics = 1 << 25,
    // Reserved negotiated tier. Do not advertise until the endpoint's full
    // native capture/request/render path is enabled and qualified.
    NativeDetailV1 = 1 << 26,
    FileReceiveLocation = 1 << 27,
    ClipboardSnapshotV1 = 1 << 28
}

internal static class RemoteDevicePlatforms
{
    public const string Unknown = "Unknown";
    public const string Windows = "Windows";
    public const string MacOS = "macOS";
    public const string Linux = "Linux";
    public const string Android = "Android";
    public const string IOS = "iOS";

    public static string Current
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return Windows;
            }

            if (OperatingSystem.IsMacOS())
            {
                return MacOS;
            }

            if (OperatingSystem.IsLinux())
            {
                return Linux;
            }

            if (OperatingSystem.IsAndroid())
            {
                return Android;
            }

            if (OperatingSystem.IsIOS())
            {
                return IOS;
            }

            return Unknown;
        }
    }

    public static string Normalize(string? platform, string fallback = Unknown)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            return fallback;
        }

        string trimmed = platform.Trim();
        return trimmed.Length <= 32 ? trimmed : trimmed[..32];
    }
}

internal static class RemoteDeviceCapabilityInfo
{
    public static RemoteDeviceCapabilities LocalWindows(bool canRemoteStart)
    {
        RemoteDeviceCapabilities capabilities =
            RemoteDeviceCapabilities.DeviceIdentity |
            RemoteDeviceCapabilities.HostVideoDiagnostics |
            RemoteDeviceCapabilities.RemoteDesktop |
            RemoteDeviceCapabilities.InputControl |
            RemoteDeviceCapabilities.ClipboardText |
            RemoteDeviceCapabilities.ClipboardSnapshotV1 |
            RemoteDeviceCapabilities.FileReceive |
            RemoteDeviceCapabilities.CaptureTargetSelection |
            RemoteDeviceCapabilities.FileDropPaste |
            RemoteDeviceCapabilities.FileSend |
            RemoteDeviceCapabilities.FileChecksum |
            RemoteDeviceCapabilities.FileTransferReceipt |
            RemoteDeviceCapabilities.FileReceiveLocation |
            RemoteDeviceCapabilities.FileTransferCancel |
            RemoteDeviceCapabilities.FileTransferPreview |
            RemoteDeviceCapabilities.ClipboardSequenceTracking |
            RemoteDeviceCapabilities.LowLatencyUdpVideo |
            RemoteDeviceCapabilities.UdpVideoCongestionFeedback |
            RemoteDeviceCapabilities.LowLatencyUdpVideoXorFec |
            RemoteDeviceCapabilities.LowLatencyUdpMouseInput |
            RemoteDeviceCapabilities.LowLatencyUdpMouseInputAppliedAck |
            RemoteDeviceCapabilities.ShortGopH264 |
            RemoteDeviceCapabilities.HighFrameRateH264 |
            RemoteDeviceCapabilities.AuthenticatedUdpHeartbeat |
            RemoteDeviceCapabilities.NativeDetailV1 |
            RemoteDeviceCapabilities.HighQualityJpeg;

        if (RemoteUpdater.CanApplyRemoteUpdate)
        {
            capabilities |= RemoteDeviceCapabilities.RemoteUpdate;
        }

        if (canRemoteStart)
        {
            capabilities |= RemoteDeviceCapabilities.RemoteStart;
        }

        return capabilities;
    }

    public static RemoteDeviceCapabilities LegacyWindows(bool canRemoteStart)
    {
        RemoteDeviceCapabilities capabilities =
            RemoteDeviceCapabilities.RemoteDesktop |
            RemoteDeviceCapabilities.InputControl |
            RemoteDeviceCapabilities.ClipboardText |
            RemoteDeviceCapabilities.FileReceive |
            RemoteDeviceCapabilities.CaptureTargetSelection;

        if (canRemoteStart)
        {
            capabilities |= RemoteDeviceCapabilities.RemoteStart;
        }

        return capabilities;
    }

    public static string Format(RemoteDeviceCapabilities capabilities)
    {
        List<string> names = [];
        if (capabilities.HasFlag(RemoteDeviceCapabilities.RemoteDesktop))
        {
            names.Add("桌面");
        }

        if (capabilities.HasFlag(RemoteDeviceCapabilities.InputControl))
        {
            names.Add("输入");
        }

        if (capabilities.HasFlag(RemoteDeviceCapabilities.ClipboardText))
        {
            names.Add("剪贴板");
        }

        if (capabilities.HasFlag(RemoteDeviceCapabilities.FileReceive))
        {
            names.Add("文件");
        }

        if (capabilities.HasFlag(RemoteDeviceCapabilities.FileDropPaste))
        {
            names.Add("定位拖放");
        }

        if (capabilities.HasFlag(RemoteDeviceCapabilities.FileSend))
        {
            names.Add("文件回传");
        }

        if (capabilities.HasFlag(RemoteDeviceCapabilities.FileChecksum))
        {
            names.Add("文件校验");
        }

        if (capabilities.HasFlag(RemoteDeviceCapabilities.FileTransferCancel))
        {
            names.Add("传输取消");
        }

        if (capabilities.HasFlag(RemoteDeviceCapabilities.FileTransferPreview))
        {
            names.Add("传前确认");
        }

        if (capabilities.HasFlag(RemoteDeviceCapabilities.RemoteUpdate))
        {
            names.Add("远程更新");
        }

        if (capabilities.HasFlag(RemoteDeviceCapabilities.ClipboardSequenceTracking))
        {
            names.Add("剪贴板就绪");
        }

        if (capabilities.HasFlag(RemoteDeviceCapabilities.LowLatencyUdpVideo))
        {
            names.Add("低延迟画面");
        }

        if (capabilities.HasFlag(RemoteDeviceCapabilities.UdpVideoCongestionFeedback))
        {
            names.Add("画面拥塞反馈");
        }

        if (capabilities.HasFlag(RemoteDeviceCapabilities.LowLatencyUdpVideoXorFec))
        {
            names.Add("画面 XOR FEC");
        }

        if (capabilities.HasFlag(RemoteDeviceCapabilities.LowLatencyUdpMouseInput))
        {
            names.Add("低延迟鼠标");
        }

        if (capabilities.HasFlag(
                RemoteDeviceCapabilities.LowLatencyUdpMouseInputAppliedAck))
        {
            names.Add("鼠标应用回执");
        }

        if (capabilities.HasFlag(
                RemoteDeviceCapabilities.ShortGopH264))
        {
            names.Add("H.264 短 GOP");
        }

        if (capabilities.HasFlag(
                RemoteDeviceCapabilities.HighFrameRateH264))
        {
            names.Add("H.264 60 FPS");
        }

        if (capabilities.HasFlag(
                RemoteDeviceCapabilities.AuthenticatedUdpHeartbeat))
        {
            names.Add("UDP 活性心跳");
        }

        if (capabilities.HasFlag(
                RemoteDeviceCapabilities.HighQualityJpeg))
        {
            names.Add("JPEG 清晰优先");
        }

        if (capabilities.HasFlag(RemoteDeviceCapabilities.RemoteStart))
        {
            names.Add("远程启动");
        }

        if (capabilities.HasFlag(RemoteDeviceCapabilities.NativeDetailV1)) names.Add("原生文字补清");
        if (capabilities.HasFlag(RemoteDeviceCapabilities.FileReceiveLocation)) names.Add("接收目录确认");

        return names.Count == 0 ? "能力未知" : string.Join("/", names);
    }
}
