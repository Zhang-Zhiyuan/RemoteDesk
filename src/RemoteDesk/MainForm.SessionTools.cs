namespace RemoteDesk;

public sealed partial class MainForm
{
    private Button _transferTasksButton = null!;
    private Button _viewerInputPauseButton = null!;

    private void OnViewerLocalInputPausedChanged(bool paused) => OnUi(() =>
    {
        ApplyViewerCapabilityState(_viewerClient.IsConnected);
        SetViewerStatus(paused
            ? "仅观看：键鼠、粘贴和向远端写入剪贴板已暂停，画面与已确认的文件传输继续。"
            : "本地输入暂停已关闭；是否可控制取决于远端权限。", MutedTextColor);
    });

    private void ShowTransferTasks()
    {
        if (!_isClosing && !IsDisposed) FileTransferTasksWindow.ShowFor(_viewerClient);
    }
}
