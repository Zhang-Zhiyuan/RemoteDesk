using System.Diagnostics;

namespace RemoteDesk;

internal enum RemoteFilePullStatusStage
{
    Other,
    Waiting,
    Receiving,
    Completed,
    Failed
}

internal static class RemoteFilePullUi
{
    internal const string DefaultButtonText = "取回远端文件";
    internal const string ShortcutText = "Ctrl+Shift+R";

    internal static bool IsShortcut(KeyEventArgs args)
    {
        return args.Control &&
            args.Shift &&
            !args.Alt &&
            args.KeyCode == Keys.R;
    }

    internal static RemoteFilePullStatusStage ClassifyStatus(
        bool success,
        string? message,
        bool requestInProgress)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return RemoteFilePullStatusStage.Other;
        }

        if (RemoteViewerClient.IsReturnedClipboardFileBatchCompleteStatus(message) ||
            (message.StartsWith("已把 ", StringComparison.Ordinal) &&
                message.Contains("回传文件", StringComparison.Ordinal) &&
                message.Contains("本机剪贴板", StringComparison.Ordinal)))
        {
            return RemoteFilePullStatusStage.Completed;
        }

        if (RemoteViewerClient.IsReturnedClipboardFileBatchEmptyStatus(message) ||
            message.StartsWith("远端文件清单为空", StringComparison.Ordinal) ||
            message.StartsWith("已取消拉取远端文件", StringComparison.Ordinal) ||
            message.StartsWith("无法显示远端文件确认窗口", StringComparison.Ordinal) ||
            message.StartsWith("接收远端文件失败", StringComparison.Ordinal) ||
            message.StartsWith("连接已断开，无法请求远端文件", StringComparison.Ordinal) ||
            message.StartsWith("写入本机文件剪贴板失败", StringComparison.Ordinal) ||
            (requestInProgress && !success && message.StartsWith("文件传输已取消", StringComparison.Ordinal)))
        {
            return RemoteFilePullStatusStage.Failed;
        }

        if (message.StartsWith("已请求远端回传剪贴板文件", StringComparison.Ordinal) ||
            message.StartsWith("远端准备回传", StringComparison.Ordinal))
        {
            return RemoteFilePullStatusStage.Waiting;
        }

        if (message.StartsWith("已确认拉取远端文件", StringComparison.Ordinal) ||
            (requestInProgress &&
                (message.StartsWith("开始接收文件", StringComparison.Ordinal) ||
                    message.StartsWith("正在接收文件", StringComparison.Ordinal) ||
                    message.StartsWith("文件已保存到本机", StringComparison.Ordinal))))
        {
            return RemoteFilePullStatusStage.Receiving;
        }

        return RemoteFilePullStatusStage.Other;
    }

    internal static string GetButtonText(RemoteFilePullStatusStage stage)
    {
        return stage switch
        {
            RemoteFilePullStatusStage.Waiting => "等待远端…",
            RemoteFilePullStatusStage.Receiving => "正在接收…",
            _ => DefaultButtonText
        };
    }

    internal static string FormatCompletedStatus(bool success, string message, string receiveDirectory)
    {
        if (string.IsNullOrWhiteSpace(message) ||
            string.IsNullOrWhiteSpace(receiveDirectory) ||
            !message.StartsWith("远端文件回传完成", StringComparison.Ordinal))
        {
            return message;
        }

        string suffix = !success
            ? $"已接收文件目录：{receiveDirectory}（可点击“接收目录”打开；再次取回可重试写入剪贴板）"
            : message.Contains("本机剪贴板", StringComparison.Ordinal)
            ? $"接收目录：{receiveDirectory}（可点击“接收目录”打开；在资源管理器按 Ctrl+V 即可粘贴）"
            : $"接收目录：{receiveDirectory}；可点击“接收目录”打开，并在资源管理器按 Ctrl+V 粘贴";
        return $"{message}{Environment.NewLine}{suffix}";
    }

    internal static string BuildAvailableToolTip(bool checksumEnabled)
    {
        string checksumSuffix = checksumEnabled ? " 支持时会自动校验 SHA-256。" : string.Empty;
        return "在远程桌面中按 Ctrl+C 或 Ctrl+Insert 复制文件/文件夹后会自动发起取回；" +
            $"用菜单复制或需要重试时，可点击此按钮或按 {ShortcutText}。" +
            $"文件会保存到本机接收目录，并自动放入本机文件剪贴板，可在资源管理器 Ctrl+V。{checksumSuffix}";
    }

    internal static ProcessStartInfo CreateOpenReceiveDirectoryStartInfo(string receiveDirectory)
    {
        if (string.IsNullOrWhiteSpace(receiveDirectory))
        {
            throw new ArgumentException("接收目录不能为空。", nameof(receiveDirectory));
        }

        return new ProcessStartInfo
        {
            FileName = receiveDirectory,
            UseShellExecute = true,
            Verb = "open"
        };
    }
}
