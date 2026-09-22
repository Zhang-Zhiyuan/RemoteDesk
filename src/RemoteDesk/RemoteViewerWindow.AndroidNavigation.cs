namespace RemoteDesk;

internal sealed partial class RemoteViewerWindow
{
    private bool _androidNavigationInProgress;

    internal static bool IsAndroidNavigationKey(Keys key) =>
        key is Keys.Escape or Keys.Home or Keys.F12;

    internal static RemoteInputCommand[] CreateAndroidNavigationCommands(Keys key)
    {
        if (!IsAndroidNavigationKey(key)) throw new ArgumentOutOfRangeException(nameof(key));
        return [RemoteInputCommand.KeyDown((int)key), RemoteInputCommand.KeyUp((int)key)];
    }

    private Button CreateAndroidNavigationButton(string label, Keys key)
    {
        var button = CreateStatusActionButton(label);
        button.AccessibleName = "远端手机" + label;
        button.Visible = _isAndroidRemote;
        button.Enabled = false;
        button.Click += async (_, _) => await SendAndroidNavigationAsync(key);
        return button;
    }

    private void UpdateAndroidNavigationControls()
    {
        foreach (var button in new[] { _androidBackButton, _androidHomeButton, _androidRecentsButton })
        {
            if (button.IsDisposed) continue;
            button.Visible = _isAndroidRemote;
            button.Enabled = _isAndroidRemote && _inputEnabled && _client.IsConnected && !_androidNavigationInProgress;
        }
    }

    private async Task SendAndroidNavigationAsync(Keys key)
    {
        if (_androidNavigationInProgress || !IsAndroidNavigationKey(key)) return;
        if (!_isAndroidRemote || !_inputEnabled || !_client.IsConnected)
        {
            SetStatus("手机导航仅在安卓远端已连接、允许控制时可用。", DangerTextColor);
            return;
        }
        _androidNavigationInProgress = true;
        UpdateAndroidNavigationControls();
        try
        {
            long generation = _client.InputConnectionGeneration;
            ReleaseAllRemoteInputs();
            if (_remoteInputOwnership.HasPendingReleases(generation) ||
                !_client.TryQueueKeyboardChord(CreateAndroidNavigationCommands(key), generation))
                throw new IOException("输入队列繁忙或连接已变化，请稍后重试。");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _client.FlushInputAsync(timeout.Token);
            if (!_isClosing && !IsDisposed)
                SetStatus("已向远端手机发送" + (key == Keys.Escape ? "返回" : key == Keys.Home ? "主页" : "最近任务") + "。", SuccessTextColor);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or System.Net.Sockets.SocketException)
        {
            if (!_isClosing && !IsDisposed)
                SetStatus("手机导航发送失败：" + (ex is OperationCanceledException ? "等待发送超时。" : ex.Message), DangerTextColor);
        }
        finally
        {
            _androidNavigationInProgress = false;
            if (!_isClosing && !IsDisposed)
            {
                UpdateAndroidNavigationControls();
                if (!_pictureBox.IsDisposed && _pictureBox.CanFocus) _pictureBox.Focus();
            }
        }
    }
}
