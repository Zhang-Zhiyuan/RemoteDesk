using Xunit;

namespace RemoteDesk.Tests;

public sealed class ConnectionDiagnosticsTests
{
    [Fact]
    public void BuildReportIncludesRenderTelemetryAndLegacyModeRecovery()
    {
        var telemetry = new RemoteViewerRenderTelemetrySnapshot(
            DirectHardwarePresentedFrames: 3,
            DirectHardwareOccludedFrames: 1,
            DirectHardwareSkippedFrames: 2,
            DirectHardwarePresentationFailures: 1,
            DirectPresentationQualificationAttempts: 4,
            DirectPresentationQualificationSuccesses: 3,
            DirectPresentationQualificationFailures: 1,
            JpegFallbackRequested: false,
            TotalDirectHardwareDecodeMilliseconds: 12.5,
            TotalDirectHardwareDecodeToPresentMilliseconds: 4.25,
            TotalDirectHardwareReceiveToPresentMilliseconds: 20.75,
            MaximumDirectHardwareReceiveToPresentMilliseconds: 9.5,
            DirectHardwareDecodeLatency: default,
            DirectHardwareDecodeToPresentLatency: default,
            DirectHardwareReceiveToPresentLatency: default,
            DirectHardwarePathDisabled: true,
            LastDirectHardwareFailureDetail: "first line\nsecond line",
            LastRenderedDecoderBackend: "Software");

        string report = ConnectionDiagnostics.BuildReport(
            "10.0.0.5",
            Protocol.DefaultPort,
            ViewerVideoMode.ForceH264,
            "ffmpeg.exe",
            ["10.0.0.2"],
            [],
            renderTelemetry: new ViewerRenderTelemetryReport(
                "10.0.0.5",
                Protocol.DefaultPort,
                ViewerVideoMode.ForceH264,
                IntentGeneration: 7,
                ConnectionGeneration: 11,
                DateTimeOffset.Parse("2026-08-25T12:34:56Z"),
                telemetry,
                IsWindowLifetimeCumulative: true));

        Assert.Contains("直显 Present=3", report);
        Assert.Contains("目标=10.0.0.5:56565", report);
        Assert.Contains("模式=自动低延迟（兼容旧 H.264 选项）", report);
        Assert.Contains("范围=窗口生命周期累计", report);
        Assert.Contains("资格窗口 尝试=4，成功=3，失败=1", report);
        Assert.Contains("JPEG 回退=未请求（旧 H.264 选项：允许锁屏/解码故障时自动兼容）", report);
        Assert.Contains("直显路径=已禁用", report);
        Assert.Contains("实际 decoder=Software", report);
        Assert.Contains("12.5 ms", report);
        Assert.Contains("最近硬件失败：first line second line", report);
        Assert.DoesNotContain("first line\nsecond line", report);
    }

    [Fact]
    public void BuildReportUsesTelemetrySessionModeForFallbackInvariant()
    {
        var telemetry = new RemoteViewerRenderTelemetrySnapshot(
            DirectHardwarePresentedFrames: 0,
            DirectHardwareOccludedFrames: 0,
            DirectHardwareSkippedFrames: 0,
            DirectHardwarePresentationFailures: 1,
            DirectPresentationQualificationAttempts: 1,
            DirectPresentationQualificationSuccesses: 0,
            DirectPresentationQualificationFailures: 1,
            JpegFallbackRequested: true,
            TotalDirectHardwareDecodeMilliseconds: 0,
            TotalDirectHardwareDecodeToPresentMilliseconds: 0,
            TotalDirectHardwareReceiveToPresentMilliseconds: 0,
            MaximumDirectHardwareReceiveToPresentMilliseconds: 0,
            DirectHardwareDecodeLatency: default,
            DirectHardwareDecodeToPresentLatency: default,
            DirectHardwareReceiveToPresentLatency: default,
            DirectHardwarePathDisabled: true,
            LastDirectHardwareFailureDetail: "hardware path failed",
            LastRenderedDecoderBackend: "JPEG");

        string report = ConnectionDiagnostics.BuildReport(
            "10.0.0.9",
            Protocol.DefaultPort,
            ViewerVideoMode.ForceH264,
            "ffmpeg.exe",
            [],
            [],
            renderTelemetry: new ViewerRenderTelemetryReport(
                "10.0.0.8",
                Protocol.DefaultPort,
                ViewerVideoMode.Automatic,
                IntentGeneration: 3,
                ConnectionGeneration: 5,
                DateTimeOffset.Parse("2026-08-25T13:00:00Z"),
                telemetry,
                IsWindowLifetimeCumulative: true));

        Assert.Contains("视频模式：自动低延迟（兼容旧 H.264 选项）", report);
        Assert.Contains("模式=自动低延迟", report);
        Assert.Contains("JPEG 回退=已请求", report);
        Assert.DoesNotContain(
            "异常：强制 H.264 不应请求 JPEG",
            report);
    }

    [Fact]
    public void BuildReportExplainsMissingRenderTelemetry()
    {
        string report = ConnectionDiagnostics.BuildReport(
            "10.0.0.5",
            Protocol.DefaultPort,
            ViewerVideoMode.Automatic,
            null,
            [],
            []);

        Assert.Contains(
            "渲染遥测：暂无最近完成查看会话的窗口遥测。",
            report);
    }

    [Fact]
    public void ReportRecognizesWindowsCompatibleFallbackPort()
    {
        string report =
            ConnectionDiagnostics.BuildReport(
                "192.0.2.249",
                Protocol.DefaultPort,
                ViewerVideoMode.Automatic,
                ffmpegPath: null,
                ["198.51.100.79"],
                [
                    new DiscoveredHost(
                        "Entity",
                        "192.0.2.249",
                        RemotePortPolicy
                            .PreferredFallbackHostPort,
                        "Primary",
                        IsHostRunning: true,
                        CanRemoteStart: false,
                        RemoteDevicePlatforms
                            .Windows,
                        RemoteDeviceCapabilities
                            .RemoteDesktop)
                ],
                hasNativeHardwareDecoder:
                    true);

        Assert.Contains(
            "被控端正在监听",
            report);
        Assert.Contains(
            $"Windows 兼容端口已迁移到 " +
            $"{RemotePortPolicy.PreferredFallbackHostPort}",
            report);
        Assert.DoesNotContain(
            "未发现该地址",
            report);
    }

    [Fact]
    public void BuildReportWarnsWhenForceH264HasNoFfmpeg()
    {
        string report = ConnectionDiagnostics.BuildReport(
            "192.0.2.249",
            Protocol.DefaultPort,
            ViewerVideoMode.ForceH264,
            null,
            ["192.0.2.10"],
            []);

        Assert.Contains("视频模式：自动低延迟（兼容旧 H.264 选项）", report);
        Assert.Contains("未找到 ffmpeg 回退", report);
        Assert.Contains("当前可使用 JPEG 正常连接", report);
        Assert.Contains("更新显卡驱动、安装 ffmpeg", report);
    }

    [Fact]
    public void BuildReportAcceptsNativeHardwareDecoderWithoutFfmpeg()
    {
        string report = ConnectionDiagnostics.BuildReport(
            "192.0.2.249",
            Protocol.DefaultPort,
            ViewerVideoMode.ForceH264,
            null,
            ["192.0.2.10"],
            [],
            hasNativeHardwareDecoder: true);

        Assert.Contains(
            "MF/D3D11 硬解可运行时探测",
            report);
        Assert.Contains(
            "未找到 ffmpeg 回退",
            report);
        Assert.DoesNotContain(
            "更新显卡驱动、安装 ffmpeg",
            report);
    }

    [Fact]
    public void BuildReportSuggestsAndroidRecordingAuthorizationWhenAppIsOnlineButHostIsNotRunning()
    {
        var android = new DiscoveredHost(
            "Pixel",
            "192.0.2.88",
            Protocol.DefaultPort,
            "Android Screen",
            IsHostRunning: false,
            CanRemoteStart: false,
            RemoteDevicePlatforms.Android,
            RemoteDeviceCapabilities.RemoteDesktop |
            RemoteDeviceCapabilities.InputControl);

        string report = ConnectionDiagnostics.BuildReport(
            "192.0.2.88",
            Protocol.DefaultPort,
            ViewerVideoMode.Automatic,
            "ffmpeg.exe",
            ["192.0.2.10"],
            [android]);

        Assert.Contains("软件在线，但被控端未启动", report);
        Assert.Contains("手机端点击“启动被控端”", report);
    }

    [Fact]
    public void BuildReportMarksTcpProbeOnlyHostAsConnectable()
    {
        var host = new DiscoveredHost(
            "192.0.2.249",
            "192.0.2.249",
            Protocol.DefaultPort,
            "TCP 被控端",
            IsHostRunning: true,
            CanRemoteStart: false,
            RemoteDevicePlatforms.Unknown,
            RemoteDeviceCapabilities.RemoteDesktop);

        string report = ConnectionDiagnostics.BuildReport(
            "192.0.2.249",
            Protocol.DefaultPort,
            ViewerVideoMode.StableJpeg,
            null,
            ["192.0.2.10"],
            [host]);

        Assert.Contains("被控端正在监听", report);
        Assert.Contains("当前状态可连接", report);
        Assert.Contains("稳定 JPEG 模式不会启用", report);
    }

    [Fact]
    public void BuildReportNamesAllNegotiatedLowLatencyExtensions()
    {
        var host = new DiscoveredHost(
            "LatencyHost",
            "192.0.2.249",
            Protocol.DefaultPort,
            "Primary",
            IsHostRunning: true,
            CanRemoteStart: false,
            RemoteDevicePlatforms.Windows,
            RemoteDeviceCapabilities.LowLatencyUdpVideo |
                RemoteDeviceCapabilities
                    .UdpVideoCongestionFeedback |
                RemoteDeviceCapabilities
                    .LowLatencyUdpVideoXorFec |
                RemoteDeviceCapabilities
                    .LowLatencyUdpMouseInput |
                RemoteDeviceCapabilities
                    .LowLatencyUdpMouseInputAppliedAck |
                RemoteDeviceCapabilities
                    .ShortGopH264);

        string report = ConnectionDiagnostics.BuildReport(
            "192.0.2.249",
            Protocol.DefaultPort,
            ViewerVideoMode.Automatic,
            "ffmpeg.exe",
            ["192.0.2.10"],
            [host]);

        Assert.Contains("低延迟画面", report);
        Assert.Contains("画面拥塞反馈", report);
        Assert.Contains("画面 XOR FEC", report);
        Assert.Contains("低延迟鼠标", report);
        Assert.Contains("鼠标应用回执", report);
        Assert.Contains("H.264 短 GOP", report);
    }

    [Fact]
    public void BuildReportShowsTcpProbeReasonWhenDiscoveryMissesTarget()
    {
        string report = ConnectionDiagnostics.BuildReport(
            "192.0.2.249",
            Protocol.DefaultPort,
            ViewerVideoMode.Automatic,
            "ffmpeg.exe",
            ["192.0.2.10"],
            [],
            [
                new RemoteDeskTcpProbeResult(
                    "192.0.2.249",
                    Protocol.DefaultPort,
                    RemoteDeskTcpProbeStatus.NotRemoteDesk,
                    "端口已打开，但不是 RemoteDesk 握手")
            ]);

        Assert.Contains("TCP：192.0.2.249:56565 端口打开但不是 RemoteDesk", report);
        Assert.Contains("该端口是否被其他服务占用", report);
    }

    [Fact]
    public void
        BuildReportTreatsDirectTcpSuccessAsConnectableWhenDiscoveryMisses()
    {
        string report = ConnectionDiagnostics.BuildReport(
            "127.0.0.1",
            Protocol.DefaultPort,
            ViewerVideoMode.Automatic,
            "ffmpeg.exe",
            ["192.0.2.10"],
            [],
            [
                new RemoteDeskTcpProbeResult(
                    "127.0.0.1",
                    Protocol.DefaultPort,
                    RemoteDeskTcpProbeStatus.RemoteDesk,
                    "RemoteDesk 握手正常")
            ]);

        Assert.Contains(
            "局域网发现未命中该目标",
            report);
        Assert.Contains(
            "直连 TCP 已确认 RemoteDesk",
            report);
        Assert.Contains(
            "直连握手正常，可直接点击连接",
            report);
        Assert.DoesNotContain(
            "确认对方软件已打开",
            report);
        Assert.DoesNotContain(
            "未发现该地址上的 RemoteDesk 握手",
            report);
    }

    [Fact]
    public void BuildReportShowsUnresolvedTargetWhenNoTcpProbeWasPossible()
    {
        string report = ConnectionDiagnostics.BuildReport(
            "unknown-host",
            Protocol.DefaultPort,
            ViewerVideoMode.Automatic,
            "ffmpeg.exe",
            ["192.0.2.10"],
            []);

        Assert.Contains("TCP：目标未解析到 IPv4", report);
        Assert.Contains("直接填写对方内网 IP", report);
    }
}
