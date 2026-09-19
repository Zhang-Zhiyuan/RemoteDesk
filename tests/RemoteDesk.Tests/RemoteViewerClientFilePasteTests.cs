using Xunit;

namespace RemoteDesk.Tests;

public sealed class RemoteViewerClientFilePasteTests
{
    [Fact]
    public void NormalizeFileDropPathsTrimsAndSkipsBlankItems()
    {
        string[] paths = ClipboardTextService.NormalizeFileDropPaths(
        [
            null,
            "",
            "   ",
            @"  C:\share\a.txt  ",
            @"D:\share\b.txt"
        ]);

        Assert.Equal(new[] { @"C:\share\a.txt", @"D:\share\b.txt" }, paths);
    }

    [Fact]
    public void CreateFilePastePlanSkipsDirectoriesMissingAndBlankPaths()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\share\a.txt",
            @"C:\share\b.txt"
        };
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\share\folder"
        };

        RemoteFilePastePlan plan = RemoteViewerClient.CreateFilePastePlan(
            [
                @"C:\share\a.txt",
                @"C:\share\folder",
                @"C:\share\missing.txt",
                "",
                "   ",
                @"C:\share\b.txt"
            ],
            maxFiles: 8,
            files.Contains,
            directories.Contains);

        Assert.Equal(new[] { @"C:\share\a.txt", @"C:\share\b.txt" }, plan.Files);
        Assert.Equal(1, plan.SkippedDirectories);
        Assert.Equal(1, plan.SkippedMissing);
        Assert.False(plan.Truncated);
    }

    [Fact]
    public void CreateFilePastePlanIncludesDirectoriesWhenRequested()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\share\a.txt"
        };
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\share\folder"
        };

        RemoteFilePastePlan plan = RemoteViewerClient.CreateFilePastePlan(
            [
                @"C:\share\a.txt",
                @"C:\share\folder",
                @"C:\share\missing.txt"
            ],
            maxFiles: 8,
            files.Contains,
            directories.Contains,
            includeDirectories: true);

        Assert.Equal(new[] { @"C:\share\a.txt", @"C:\share\folder" }, plan.Files);
        Assert.Collection(
            plan.TransferItems,
            item => Assert.Equal(RemoteFilePasteItemKind.File, item.Kind),
            item => Assert.Equal(RemoteFilePasteItemKind.Directory, item.Kind));
        Assert.Equal(0, plan.SkippedDirectories);
        Assert.Equal(1, plan.SkippedMissing);
        Assert.False(plan.Truncated);
    }

    [Fact]
    public void CreateFilePastePlanLimitsFilesAndRemovesDuplicates()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\share\a.txt",
            @"C:\share\b.txt",
            @"C:\share\c.txt"
        };

        RemoteFilePastePlan plan = RemoteViewerClient.CreateFilePastePlan(
            [
                @"C:\share\a.txt",
                @"c:\share\a.txt",
                @"C:\share\b.txt",
                @"C:\share\c.txt"
            ],
            maxFiles: 2,
            files.Contains,
            _ => false);

        Assert.Equal(new[] { @"C:\share\a.txt", @"C:\share\b.txt" }, plan.Files);
        Assert.Equal(0, plan.SkippedDirectories);
        Assert.Equal(0, plan.SkippedMissing);
        Assert.True(plan.Truncated);
    }

    [Fact]
    public void FormatClipboardFilePasteStatusReportsSkippedItems()
    {
        string status = RemoteViewerWindow.FormatClipboardFilePasteStatus(
            new RemoteFilePasteResult(
                SentFiles: 2,
                FailedFiles: 1,
                SkippedDirectories: 1,
                SkippedMissing: 1,
                Truncated: true,
                ArchivedDirectories: 1),
            maxFiles: 32);

        Assert.Contains("已从剪贴板发送 2 个文件", status, StringComparison.Ordinal);
        Assert.Contains("1 个文件失败", status, StringComparison.Ordinal);
        Assert.Contains("已打包 1 个文件夹为 zip", status, StringComparison.Ordinal);
        Assert.Contains("跳过 1 个文件夹", status, StringComparison.Ordinal);
        Assert.Contains("跳过 1 个不可访问项", status, StringComparison.Ordinal);
        Assert.Contains("一次最多粘贴 32 个文件", status, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatClipboardFilePasteStatusReportsFailedOnlyBatch()
    {
        string status = RemoteViewerWindow.FormatClipboardFilePasteStatus(
            new RemoteFilePasteResult(
                SentFiles: 0,
                FailedFiles: 1,
                SkippedDirectories: 0,
                SkippedMissing: 0,
                Truncated: false),
            maxFiles: 32);

        Assert.Contains("剪贴板文件发送失败", status, StringComparison.Ordinal);
        Assert.DoesNotContain("剪贴板没有可发送的文件", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestRemoteClipboardFilesCanSuppressRequestFailureStatus()
    {
        using var client = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        var statuses = new List<string>();
        client.FileTransferStatusReceived += (_success, message) => statuses.Add(message);

        Assert.False(await client.RequestRemoteClipboardFilesAsync(notifyRequest: false));

        Assert.Empty(statuses);
    }

    [Fact]
    public async Task RequestRemoteClipboardFilesReportsManualRequestFailureStatus()
    {
        using var client = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        var statuses = new List<string>();
        var pendingTransitions = new List<bool>();
        client.FileTransferStatusReceived += (_success, message) => statuses.Add(message);
        client.RemoteClipboardFileRequestPendingChanged += pending => pendingTransitions.Add(pending);

        Assert.False(await client.RequestRemoteClipboardFilesAsync());

        string status = Assert.Single(statuses);
        Assert.Contains("连接已断开", status, StringComparison.Ordinal);
        Assert.False(client.IsRemoteClipboardFileRequestPending);
        Assert.Empty(pendingTransitions);
    }

    [Theory]
    [InlineData("读取远端文件剪贴板失败：clipboard busy")]
    [InlineData("等待远程复制更新剪贴板超时，本次文件回传已终止。")]
    [InlineData("远端文件回传已拒绝：远程更新传输尚未结束。")]
    [InlineData("远端文件回传已取消。")]
    [InlineData("远端文件回传进行中，请等待完成或先取消当前请求。")]
    [InlineData("已有远端文件回传清单等待确认，请先确认或取消当前请求。")]
    [InlineData("没有等待确认的远端文件回传。")]
    [InlineData("Linux has no return files. Use --return-file.")]
    [InlineData("Linux could not prepare return files: inaccessible")]
    [InlineData("Linux could not return files: source changed")]
    [InlineData("Linux return file transfer cancelled by viewer.")]
    [InlineData("Linux return file transfer failed: access denied")]
    [InlineData("Linux return file list contains no readable files.")]
    [InlineData("Linux has no pending return files to send.")]
    public void RemoteClipboardFileRequestTerminalFailureStatusRecognizesPreparationFailures(string message)
    {
        Assert.True(RemoteViewerClient.IsReturnedClipboardFileRequestTerminalFailureStatus(false, message));
        Assert.False(RemoteViewerClient.IsReturnedClipboardFileRequestTerminalFailureStatus(true, message));
    }

    [Fact]
    public void RemoteClipboardFileRequestTerminalFailureStatusKeepsPerFileFailuresPending()
    {
        Assert.False(RemoteViewerClient.IsReturnedClipboardFileRequestTerminalFailureStatus(
            false,
            "回传远端文件失败：report.txt - access denied"));
        Assert.False(RemoteViewerClient.IsReturnedClipboardFileRequestTerminalFailureStatus(
            false,
            "Linux failed to return report.txt: access denied"));
    }

    [Fact]
    public void InvalidRemoteUpdateStartConsumesOutstandingPackageRequest()
    {
        using var client = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        Assert.True(client.TryReserveSelfUpdatePackageRequest());
        RemoteControlMessage invalidStart = RemoteMessageCodec.DecodeControl(
            RemoteMessageCodec.EncodeRemoteUpdateStart(
                "invalid-update",
                "unexpected.exe",
                fileLength: 1));

        Assert.Throws<InvalidDataException>(() =>
            client.BeginSelfUpdateTransferCore(invalidStart));

        RemoteControlMessage laterValidStart = RemoteMessageCodec.DecodeControl(
            RemoteMessageCodec.EncodeRemoteUpdateStart(
                "later-update",
                "RemoteDesk.exe",
                fileLength: 1));
        Assert.Throws<InvalidOperationException>(() =>
            client.BeginSelfUpdateTransferCore(laterValidStart));
    }
}
