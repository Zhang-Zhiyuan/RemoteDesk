using System.Globalization;
using System.Text;

namespace RemoteDesk;

internal readonly record struct ViewerRenderTelemetryReport(
    string Host,
    int Port,
    ViewerVideoMode VideoMode,
    long IntentGeneration,
    long ConnectionGeneration,
    DateTimeOffset CapturedAtUtc,
    RemoteViewerRenderTelemetrySnapshot Snapshot,
    bool IsWindowLifetimeCumulative);

internal static class ConnectionDiagnostics
{
    public static string BuildReport(
        string? host,
        int port,
        ViewerVideoMode videoMode,
        string? ffmpegPath,
        IReadOnlyList<string> localAddresses,
        IReadOnlyList<DiscoveredHost> discoveredHosts,
        IReadOnlyList<RemoteDeskTcpProbeResult>? tcpProbeResults = null,
        bool hasNativeHardwareDecoder = false,
        ViewerRenderTelemetryReport? renderTelemetry = null)
    {
        string normalizedHost = host?.Trim() ?? string.Empty;
        DiscoveredHost? target = FindTargetHost(normalizedHost, port, discoveredHosts);
        tcpProbeResults ??= Array.Empty<RemoteDeskTcpProbeResult>();
        var builder = new StringBuilder();

        builder.AppendLine("连接诊断");
        builder.AppendLine($"本机 IP：{FormatLocalAddresses(localAddresses)}");
        builder.AppendLine(string.IsNullOrWhiteSpace(normalizedHost)
            ? "目标：未填写"
            : $"目标：{normalizedHost}:{port}");
        builder.AppendLine($"视频模式：{FormatVideoMode(videoMode)}");
        builder.AppendLine(
            FormatH264DecoderLine(
                videoMode,
                ffmpegPath,
                hasNativeHardwareDecoder));
        builder.Append(FormatRenderTelemetry(renderTelemetry));
        builder.AppendLine(FormatDiscoveryLine(
            normalizedHost,
            port,
            target,
            discoveredHosts.Count,
            tcpProbeResults));
        builder.AppendLine(FormatTcpProbeLine(normalizedHost, tcpProbeResults));
        builder.AppendLine(
            FormatNextStep(
                normalizedHost,
                videoMode,
                ffmpegPath,
                hasNativeHardwareDecoder,
                target,
                tcpProbeResults));

        return builder.ToString().TrimEnd();
    }

    private static string FormatRenderTelemetry(
        ViewerRenderTelemetryReport? telemetryReport)
    {
        if (telemetryReport is null)
        {
            return "渲染遥测：暂无最近完成查看会话的窗口遥测。\n";
        }

        ViewerRenderTelemetryReport report = telemetryReport.Value;
        RemoteViewerRenderTelemetrySnapshot value = report.Snapshot;
        string backend = string.IsNullOrWhiteSpace(value.LastRenderedDecoderBackend)
            ? "未记录"
            : LimitDiagnosticDetail(value.LastRenderedDecoderBackend);
        string failure = string.IsNullOrWhiteSpace(value.LastDirectHardwareFailureDetail)
            ? "无"
            : LimitDiagnosticDetail(value.LastDirectHardwareFailureDetail);
        string fallback = value.JpegFallbackRequested ? "已请求" : "未请求";
        string directState = value.DirectHardwarePathDisabled ? "已禁用" : "仍可用/未禁用";
        string forceH264Note = report.VideoMode == ViewerVideoMode.ForceH264
            ? "（旧 H.264 选项：允许锁屏/解码故障时自动兼容）"
            : string.Empty;
        string captureTime = report.CapturedAtUtc == default
            ? "未记录"
            : report.CapturedAtUtc.ToUniversalTime().ToString(
                "yyyy-MM-dd HH:mm:ss 'UTC'",
                CultureInfo.InvariantCulture);
        string scope = report.IsWindowLifetimeCumulative
            ? "窗口生命周期累计"
            : "连接段";
        return
            "渲染遥测（最近完成查看会话；" +
            $"目标={LimitDiagnosticDetail(report.Host)}:{report.Port}，" +
            $"模式={FormatVideoMode(report.VideoMode)}，" +
            $"intent={report.IntentGeneration}，" +
            $"连接代际={report.ConnectionGeneration}，" +
            $"采集={captureTime}，范围={scope}）：\n" +
            $"直显 Present={value.DirectHardwarePresentedFrames}，" +
            $"遮挡={value.DirectHardwareOccludedFrames}，" +
            $"跳过={value.DirectHardwareSkippedFrames}，" +
            $"失败={value.DirectHardwarePresentationFailures}；\n" +
            $"资格窗口 尝试={value.DirectPresentationQualificationAttempts}，" +
            $"成功={value.DirectPresentationQualificationSuccesses}，" +
            $"失败={value.DirectPresentationQualificationFailures}；\n" +
            $"JPEG 回退={fallback}{forceH264Note}，直显路径={directState}，" +
            $"实际 decoder={backend}；\n" +
            $"硬解累计 decode={FormatMilliseconds(value.TotalDirectHardwareDecodeMilliseconds)}，" +
            $"decode→Present={FormatMilliseconds(value.TotalDirectHardwareDecodeToPresentMilliseconds)}，" +
            $"receive→Present 最大={FormatMilliseconds(value.MaximumDirectHardwareReceiveToPresentMilliseconds)}；\n" +
            $"最近硬件失败：{failure}\n";
    }

    private static string FormatMilliseconds(double milliseconds)
    {
        return double.IsFinite(milliseconds) && milliseconds >= 0
            ? milliseconds.ToString("0.##", CultureInfo.InvariantCulture) + " ms"
            : "未记录";
    }

    private static string LimitDiagnosticDetail(string value)
    {
        string normalized = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        const int maxLength = 240;
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength] + "…";
    }

    internal static string FormatVideoMode(ViewerVideoMode videoMode)
    {
        return videoMode switch
        {
            ViewerVideoMode.StableJpeg => "稳定 JPEG",
            ViewerVideoMode.ForceH264 => "自动低延迟（兼容旧 H.264 选项）",
            _ => "自动低延迟"
        };
    }

    private static string FormatLocalAddresses(IReadOnlyList<string> localAddresses)
    {
        string[] addresses = localAddresses
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => address.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return addresses.Length == 0 ? "未读取到可用 IPv4" : string.Join(", ", addresses);
    }

    private static string FormatH264DecoderLine(
        ViewerVideoMode videoMode,
        string? ffmpegPath,
        bool hasNativeHardwareDecoder)
    {
        if (videoMode == ViewerVideoMode.StableJpeg)
        {
            return "H.264 解码：稳定 JPEG 模式不会启用";
        }

        string native = hasNativeHardwareDecoder
            ? "MF/D3D11 硬解可运行时探测"
            : "MF/D3D11 硬解不可用";
        string ffmpeg = string.IsNullOrWhiteSpace(
            ffmpegPath)
                ? "未找到 ffmpeg 回退"
                : $"ffmpeg 回退可用，{ffmpegPath}";
        return $"H.264 解码：{native}；{ffmpeg}";
    }

    private static string FormatDiscoveryLine(
        string host,
        int port,
        DiscoveredHost? target,
        int discoveredCount,
        IReadOnlyList<RemoteDeskTcpProbeResult> tcpProbeResults)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return discoveredCount == 0
                ? "探测：未填写目标 IP，仅完成本机环境检查"
                : $"探测：发现 {discoveredCount} 台远端设备";
        }

        if (target is null)
        {
            if (tcpProbeResults.Any(result =>
                    result.Status ==
                        RemoteDeskTcpProbeStatus.RemoteDesk))
            {
                return "探测：局域网发现未命中该目标；" +
                    "直连 TCP 已确认 RemoteDesk";
            }

            return "探测：未发现该地址上的 RemoteDesk 握手";
        }

        string state = target.IsHostRunning
            ? "被控端正在监听"
            : target.CanRemoteStart ? "软件在线，可远程启动被控端" : "软件在线，但被控端未启动";
        string portMigration =
            target.Port != port &&
            RemotePortPolicy.AreCompatibleHostPorts(
                port,
                target.Port)
                ? $"；Windows 兼容端口已迁移到 {target.Port}"
                : string.Empty;
        return $"探测：{state}{portMigration}；平台 {target.Platform}；屏幕 {FormatCaptureTarget(target.CaptureTarget)}；能力 {FormatCapabilities(target.Capabilities)}";
    }

    private static string FormatNextStep(
        string host,
        ViewerVideoMode videoMode,
        string? ffmpegPath,
        bool hasNativeHardwareDecoder,
        DiscoveredHost? target,
        IReadOnlyList<RemoteDeskTcpProbeResult> tcpProbeResults)
    {
        if (videoMode == ViewerVideoMode.ForceH264 &&
            !hasNativeHardwareDecoder &&
            string.IsNullOrWhiteSpace(ffmpegPath))
        {
            return "建议：当前可使用 JPEG 正常连接；更新显卡驱动、安装 ffmpeg 后可恢复 H.264。";
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            return "建议：填写对方内网 IP，或先点击“扫描/探测”。";
        }

        if (target is null)
        {
            if (tcpProbeResults.Any(result =>
                    result.Status ==
                        RemoteDeskTcpProbeStatus.RemoteDesk))
            {
                return "建议：直连握手正常，可直接点击连接；" +
                    "局域网发现未命中不影响手动直连。";
            }

            if (tcpProbeResults.Count == 0)
            {
                return "建议：确认目标名称能解析到内网 IPv4，或直接填写对方内网 IP。";
            }

            if (tcpProbeResults.Any(result => result.Status == RemoteDeskTcpProbeStatus.NotRemoteDesk))
            {
                return "建议：目标端口已打开但不是 RemoteDesk，请确认端口填写正确，或检查该端口是否被其他服务占用。";
            }

            if (tcpProbeResults.Any(result => result.Status == RemoteDeskTcpProbeStatus.ConnectionFailed))
            {
                return "建议：确认对方软件已打开、被控端已启动、防火墙允许 TCP 端口访问。";
            }

            return "建议：确认对方软件已打开、端口一致、防火墙允许局域网访问；Android 还需启动被控端并授权录屏。";
        }

        if (!target.IsHostRunning && !target.CanRemoteStart &&
            string.Equals(target.Platform, RemoteDevicePlatforms.Android, StringComparison.OrdinalIgnoreCase))
        {
            return "建议：手机端点击“启动被控端”并完成屏幕录制授权。";
        }

        if (!target.IsHostRunning && target.CanRemoteStart)
        {
            return "建议：可直接点击连接，程序会先尝试远程启动被控端。";
        }

        return "建议：当前状态可连接；内网优先用“自动低延迟”，画面不稳时切到“稳定 JPEG”。";
    }

    private static string FormatTcpProbeLine(
        string host,
        IReadOnlyList<RemoteDeskTcpProbeResult> tcpProbeResults)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return "TCP：未填写目标，未执行端口握手检查";
        }

        if (tcpProbeResults.Count == 0)
        {
            return "TCP：目标未解析到 IPv4，未执行端口握手检查";
        }

        return "TCP：" + string.Join("；", tcpProbeResults.Select(FormatTcpProbeResult));
    }

    private static string FormatTcpProbeResult(RemoteDeskTcpProbeResult result)
    {
        string status = result.Status switch
        {
            RemoteDeskTcpProbeStatus.RemoteDesk => "RemoteDesk 握手正常",
            RemoteDeskTcpProbeStatus.NotRemoteDesk => "端口打开但不是 RemoteDesk",
            RemoteDeskTcpProbeStatus.Timeout => "超时",
            _ => "连接失败"
        };

        return $"{result.Address}:{result.Port} {status}（{result.Detail}）";
    }

    private static string FormatCaptureTarget(string captureTarget)
    {
        return string.IsNullOrWhiteSpace(captureTarget) ? "未声明" : captureTarget.Trim();
    }

    private static string FormatCapabilities(RemoteDeviceCapabilities capabilities)
    {
        if (capabilities == RemoteDeviceCapabilities.None)
        {
            return "未声明";
        }

        var names = new List<string>();
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

        if (capabilities.HasFlag(
                RemoteDeviceCapabilities.UdpVideoCongestionFeedback))
        {
            names.Add("画面拥塞反馈");
        }

        if (capabilities.HasFlag(
                RemoteDeviceCapabilities.LowLatencyUdpVideoXorFec))
        {
            names.Add("画面 XOR FEC");
        }

        if (capabilities.HasFlag(
                RemoteDeviceCapabilities.LowLatencyUdpMouseInput))
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
                RemoteDeviceCapabilities.HighQualityJpeg))
        {
            names.Add("JPEG 清晰优先");
        }

        if (capabilities.HasFlag(RemoteDeviceCapabilities.RemoteStart))
        {
            names.Add("远程启动");
        }

        return names.Count == 0 ? "未声明" : string.Join("/", names);
    }

    internal static DiscoveredHost? FindTargetHost(
        string host,
        int port,
        IReadOnlyList<DiscoveredHost> discoveredHosts)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return discoveredHosts.FirstOrDefault();
        }

        DiscoveredHost? exact =
            discoveredHosts.FirstOrDefault(item =>
            item.Port == port &&
            (string.Equals(item.Address, host, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.MachineName, host, StringComparison.OrdinalIgnoreCase)));
        return exact ??
            discoveredHosts.FirstOrDefault(item =>
                RemotePortPolicy.AreCompatibleHostPorts(
                    port,
                    item.Port) &&
                (string.Equals(
                        item.Address,
                        host,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        item.MachineName,
                        host,
                        StringComparison.OrdinalIgnoreCase)));
    }
}
