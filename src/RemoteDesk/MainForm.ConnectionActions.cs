namespace RemoteDesk;

public sealed partial class MainForm
{
    // A manual desktop attempt has its own lifetime. Cancelling it must not
    // cancel relay publication, SSH terminals or a later desktop connection.
    private CancellationTokenSource? _viewerConnectionCancellation;

    private CancellationTokenSource? BeginViewerConnectionAction(bool disconnecting)
    {
        var attempt = disconnecting ? null : CancellationTokenSource.CreateLinkedTokenSource(
            _relayOperationCancellation.Token);
        _viewerConnectionCancellation = attempt;
        _viewerActionInProgress = true;
        _viewerActionDisconnecting = disconnecting;
        SetViewerConnectionInputsEnabled(false);
        UpdateViewerActionState();
        return attempt;
    }

    private bool TryCancelViewerConnectionAction()
    {
        if (!_viewerActionInProgress) return false;
        if (_viewerConnectionCancellation is { IsCancellationRequested: false } attempt)
        {
            // Revoke reconnect eligibility before cancellation can deliver a
            // late DeviceInfo/disconnect notification from the network thread.
            CancelViewerReconnectIntent();
            attempt.Cancel();
            SetViewerStatus("正在取消连接…", MutedTextColor);
            SetRelayStatus("正在取消远程桌面连接…", MutedTextColor);
            UpdateViewerActionState();
        }
        // Leave the attempt busy until its finally has drained the old client.
        return true;
    }

    private async Task CompleteCancelledViewerConnectionAsync()
    {
        CancelViewerReconnectIntent();
        try { await _viewerClient.DisconnectAsync(); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or
            ObjectDisposedException or InvalidOperationException) { }
        if (_isClosing || IsDisposed) return;
        SetViewerStatus("已取消连接，可以重新选择设备。", MutedTextColor);
        SetRelayStatus("已取消远程桌面连接；服务器登录和本机上线状态不变。", MutedTextColor);
    }

    private void EndViewerConnectionAction(CancellationTokenSource? attempt)
    {
        bool isCurrent = ReferenceEquals(_viewerConnectionCancellation, attempt);
        attempt?.Dispose();
        if (!isCurrent) return;
        _viewerConnectionCancellation = null;
        if (_isClosing || IsDisposed) return;
        _viewerActionInProgress = false;
        _viewerActionDisconnecting = false;
        if (!_viewerClient.IsConnected) _pendingViewerConnection = null;
        SetViewerConnectionInputsEnabled(!_viewerClient.IsConnected);
        UpdateViewerActionState();
    }

    private void ApplyPendingViewerAction(Button button)
    {
        bool cancelling = _viewerConnectionCancellation?.IsCancellationRequested == true;
        bool canCancel = !_viewerActionDisconnecting && _viewerConnectionCancellation is not null && !cancelling;
        button.Text = _viewerActionDisconnecting ? "断开中…" : cancelling ? "取消中…" : "取消连接";
        button.Enabled = canCancel;
        ApplyButtonStyle(button, canCancel ? DangerColor : NeutralBadgeBackColor,
            canCancel ? Color.White : NeutralBadgeTextColor, canCancel ? DangerColor : PanelBorderColor);
        SetToolTip(button, _viewerActionDisconnecting ? "正在安全关闭当前远程会话。" :
            cancelling ? "正在停止本次连接，请稍候。" : "正在连接远程桌面；点击可取消，不影响本机被控和 SSH 会话。");
    }
}
