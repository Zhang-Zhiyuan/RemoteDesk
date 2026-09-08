using System.Windows.Forms;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class RemoteFilePullUiTests
{
    [Theory]
    [InlineData(Keys.Control | Keys.Shift | Keys.R, true)]
    [InlineData(Keys.Control | Keys.R, false)]
    [InlineData(Keys.Control | Keys.Shift | Keys.Alt | Keys.R, false)]
    [InlineData(Keys.Control | Keys.Shift | Keys.V, false)]
    public void ShortcutRequiresExactControlShiftR(Keys keys, bool expected)
    {
        Assert.Equal(expected, RemoteFilePullUi.IsShortcut(new KeyEventArgs(keys)));
    }

    [Theory]
    [InlineData(true, "已请求远端回传剪贴板文件。", false, (int)RemoteFilePullStatusStage.Waiting)]
    [InlineData(true, "远端准备回传 2 项文件，等待确认。", false, (int)RemoteFilePullStatusStage.Waiting)]
    [InlineData(true, "开始接收文件：demo.zip (2 MB)", true, (int)RemoteFilePullStatusStage.Receiving)]
    [InlineData(true, "正在接收文件：demo.zip 45% (900 KB / 2 MB)", true, (int)RemoteFilePullStatusStage.Receiving)]
    [InlineData(true, "文件已保存到本机：C:\\Downloads\\demo.zip", true, (int)RemoteFilePullStatusStage.Receiving)]
    [InlineData(true, "远端文件回传完成：2 个", false, (int)RemoteFilePullStatusStage.Completed)]
    [InlineData(true, "已把 2 个回传文件放入本机剪贴板，可在资源管理器直接粘贴。", false, (int)RemoteFilePullStatusStage.Completed)]
    [InlineData(false, "远端剪贴板没有可回传的文件。", true, (int)RemoteFilePullStatusStage.Failed)]
    [InlineData(false, "接收远端文件失败：校验失败", true, (int)RemoteFilePullStatusStage.Failed)]
    [InlineData(true, "正在发送文件：local.txt 45%", true, (int)RemoteFilePullStatusStage.Other)]
    public void StatusClassifierTracksOnlyReturnedFileWorkflow(
        bool success,
        string message,
        bool requestInProgress,
        int expected)
    {
        Assert.Equal(
            (RemoteFilePullStatusStage)expected,
            RemoteFilePullUi.ClassifyStatus(success, message, requestInProgress));
    }

    [Theory]
    [InlineData((int)RemoteFilePullStatusStage.Waiting, "等待远端…")]
    [InlineData((int)RemoteFilePullStatusStage.Receiving, "正在接收…")]
    [InlineData((int)RemoteFilePullStatusStage.Completed, RemoteFilePullUi.DefaultButtonText)]
    [InlineData((int)RemoteFilePullStatusStage.Failed, RemoteFilePullUi.DefaultButtonText)]
    public void ButtonTextReflectsActiveStage(int stage, string expected)
    {
        Assert.Equal(expected, RemoteFilePullUi.GetButtonText((RemoteFilePullStatusStage)stage));
    }

    [Fact]
    public void CompletedStatusIncludesReceiveDirectoryAndClipboardGuidance()
    {
        const string receiveDirectory = @"C:\Users\demo user\Downloads\RemoteDeskReceived";

        string status = RemoteFilePullUi.FormatCompletedStatus(
            success: true,
            message: "远端文件回传完成：1 个",
            receiveDirectory);

        Assert.Contains(receiveDirectory, status, StringComparison.Ordinal);
        Assert.Contains("Ctrl+V", status, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedStatusDoesNotClaimSuccessWhenTransferFailed()
    {
        const string message = "远端文件回传失败：1 个";

        string status = RemoteFilePullUi.FormatCompletedStatus(
            success: false,
            message,
            @"C:\Downloads\RemoteDeskReceived");

        Assert.Equal(message, status);
    }

    [Fact]
    public void ClipboardFailureStillShowsWhereCompletedTransferWasSaved()
    {
        const string receiveDirectory = @"C:\Downloads\RemoteDeskReceived";

        string status = RemoteFilePullUi.FormatCompletedStatus(
            success: false,
            message: "远端文件回传完成：1 个；写入本机文件剪贴板失败：剪贴板忙。",
            receiveDirectory);

        Assert.Contains(receiveDirectory, status, StringComparison.Ordinal);
        Assert.Contains("重试写入剪贴板", status, StringComparison.Ordinal);
        Assert.DoesNotContain("Ctrl+V 即可粘贴", status, StringComparison.Ordinal);
    }

    [Fact]
    public void AvailableToolTipExplainsRemoteCopyShortcutAndLocalPaste()
    {
        string toolTip = RemoteFilePullUi.BuildAvailableToolTip(checksumEnabled: true);

        Assert.Contains("复制文件/文件夹后会自动发起取回", toolTip, StringComparison.Ordinal);
        Assert.Contains("需要重试", toolTip, StringComparison.Ordinal);
        Assert.Contains(RemoteFilePullUi.ShortcutText, toolTip, StringComparison.Ordinal);
        Assert.Contains("Ctrl+V", toolTip, StringComparison.Ordinal);
        Assert.Contains("SHA-256", toolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenReceiveDirectoryStartInfoUsesShellWithoutQuotingPathManually()
    {
        const string receiveDirectory = @"C:\Users\demo user\Downloads\RemoteDeskReceived";

        var startInfo = RemoteFilePullUi.CreateOpenReceiveDirectoryStartInfo(receiveDirectory);

        Assert.Equal(receiveDirectory, startInfo.FileName);
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal("open", startInfo.Verb);
        Assert.Empty(startInfo.Arguments);
    }
}
