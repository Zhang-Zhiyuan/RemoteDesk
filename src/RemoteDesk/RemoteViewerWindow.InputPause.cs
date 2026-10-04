namespace RemoteDesk;

internal sealed partial class RemoteViewerWindow
{
    private readonly Button _inputPauseButton;
    private readonly Button _transferTasksButton;
    private bool _remoteInputCapability;

    internal bool LocalInputPausedForTests => _client.IsLocalInputPaused;
    internal Button InputPauseButtonForTests => _inputPauseButton;
    internal Button TransferTasksButtonForTests => _transferTasksButton;

    private void ToggleLocalInputPause()
    {
        if (_isClosing || IsDisposed) return;
        bool pause = !_client.IsLocalInputPaused;
        if (pause)
        {
            ReleaseAllRemoteInputs();
            CancelPendingClipboardPull();
            ResetAutomaticClipboard();
            CancelRemoteDragOut(status: null, cancelTransfer: false);
        }
        _client.SetLocalInputPaused(pause);
        ApplyEffectiveInputState();
        SetStatus(pause
            ? "仅观看：已暂停键鼠、粘贴和剪贴板写入；画面与已确认的文件传输继续。"
            : _remoteInputCapability ? "已恢复远程控制。" : "已关闭本地输入暂停；远端当前未允许输入。",
            MutedTextColor);
    }

    private void OnLocalInputPausedChanged(bool paused) => OnUi(() =>
    {
        if (_isClosing || IsDisposed) return;
        if (paused)
        {
            ReleaseAllRemoteInputs();
            CancelPendingClipboardPull();
            ResetAutomaticClipboard();
        }
        ApplyEffectiveInputState();
    });

    private void ApplyEffectiveInputState()
    {
        bool paused = _client.IsLocalInputPaused;
        bool enabled = _remoteInputCapability && !paused;
        if (_inputEnabled && !enabled) ReleaseAllRemoteInputs();
        _inputEnabled = enabled;
        // Stop the gesture, but a confirmed transfer belongs to the task panel.
        if (!enabled) CancelRemoteDragOut(status: null, cancelTransfer: !paused);
        if (!_pictureBox.IsDisposed)
        {
            _pictureBox.Cursor = enabled ? Cursors.Default : Cursors.No;
            _pictureBox.LocalImeEnabled = _isAndroidRemote && enabled;
        }
        _inputPauseButton.Text = paused ? "仅观看：开" : "仅观看：关";
        _inputPauseButton.AccessibleName = paused ? "仅观看已开启，点击恢复控制" : "点击暂停输入，仅观看远端画面";
        _toolTip.SetToolTip(_inputPauseButton, paused
            ? "点击恢复键鼠和剪贴板写入。重连不会自动恢复控制。"
            : "暂停键鼠、文字粘贴和向远端写入剪贴板；仍可缩放画面，已确认的文件传输不受影响。");
        UpdateRemoteInputMethodControls();
        ConfigureFilePullToolTips();
        QueueStatusFooterLayout();
    }

    private void ShowTransferTasks()
    {
        if (!_isClosing && !IsDisposed) FileTransferTasksWindow.ShowFor(_client);
    }
}
