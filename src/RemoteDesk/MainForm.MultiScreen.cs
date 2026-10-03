namespace RemoteDesk;

public sealed partial class MainForm
{
    private readonly Dictionary<string, AdditionalScreenSession> _additionalScreens = new(StringComparer.OrdinalIgnoreCase);
    private bool _closingAdditionalScreens;

    private sealed class AdditionalScreenSession(RemoteViewerClient client, long controllerGeneration,
        ViewerConnectionSnapshot controllerConnection) : IDisposable
    {
        internal RemoteViewerClient Client { get; } = client;
        internal long ControllerGeneration { get; } = controllerGeneration;
        internal ViewerConnectionSnapshot ControllerConnection { get; } = controllerConnection;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal RemoteViewerWindow? Window { get; set; }
        internal bool Closing { get; set; }
        internal Task Cleanup { get; private set; } = Task.CompletedTask;
        public void Dispose()
        {
            if (Closing) return;
            Closing = true;
            Cancellation.Cancel();
            Window?.CloseAfterDisconnect();
            // Socket/capture drains must not block the WinForms message loop.
            Cleanup = Task.Run(() =>
            {
                try { Client.Dispose(); }
                catch (Exception error) { System.Diagnostics.Trace.TraceError("独立屏幕连接清理失败：{0}", error.Message); }
                finally { Cancellation.Dispose(); }
            });
        }
    }

    private async Task OpenAdditionalScreenAsync(CaptureTargetInfo target)
    {
        if (_isClosing || IsDisposed || !_viewerClient.IsConnected) return;
        CaptureTargetInfo? controllerTarget = _viewerWindow?.SelectedCaptureTarget ??
            _viewerCaptureTargetBox.SelectedItem as CaptureTargetInfo;
        if (string.Equals(controllerTarget?.Id, target.Id, StringComparison.OrdinalIgnoreCase))
        {
            // Every independent window can reopen the original screen. Reuse
            // its paused transport instead of consuming another capture slot.
            ShowViewerWindow();
            return;
        }
        if (_additionalScreens.TryGetValue(target.Id, out var existing))
        {
            if (TryActivateAdditionalScreen(target)) return;
            _additionalScreens.Remove(target.Id);
            existing.Dispose();
        }
        if (_additionalScreens.Count >= RemoteHostServer.ActiveClientGate<object>.MaximumScreens - 1)
            throw new InvalidOperationException("最多同时打开 4 个远程屏幕窗口。");
        ViewerConnectionSnapshot? connection;
        lock (_viewerReconnectLock) connection = _pendingViewerConnection?.Connection ?? _viewerReconnectIntent?.Connection;
        if (connection is null) throw new InvalidOperationException("连接信息已失效，请重新连接。");
        long generation = _viewerClient.InputConnectionGeneration;
        RemoteViewerClient client = _viewerClient.CreateAdditionalScreenClient(target.Id);
        var screen = new AdditionalScreenSession(client, generation, connection);
        CancellationToken cancellation = screen.Cancellation.Token;
        _additionalScreens.Add(target.Id, screen);
        bool Current() => !_isClosing && !IsDisposed && !screen.Closing && _viewerClient.IsConnected &&
            IsScreenControllerCurrent(screen) && _additionalScreens.TryGetValue(target.Id, out var value) && ReferenceEquals(value, screen);
        client.CaptureTargetsUpdated += update => OnUi(() =>
        {
            if (Current() && client.IsCurrentCaptureTargetsUpdate(update)) screen.Window?.SetCaptureTargets(update.Targets, target.Id);
        });
        client.ConnectedChanged += connected => OnUi(() =>
        {
            if (!connected && Current() && screen.Window is not null)
            {
                SetViewerStatus("独立屏幕连接已断开，可通过“多屏分窗”重新打开；其他窗口不受影响。", DangerColor);
                screen.Window.CloseAfterDisconnect();
            }
        });
        client.ConfirmRemoteClipboardFileTransfer = (items, note) => ConfirmRemoteClipboardFileTransfer(items, note, screen.Window);
        try
        {
            if (connection.RelayRoute is { } relay)
                await client.ConnectViaRelayAsync(relay, connection.Password, connection.VideoMode, cancellation);
            else
                await client.ConnectAsync(connection.Host, connection.Port, connection.Password, connection.VideoMode, cancellation);
            if (!await client.WaitForCurrentDeviceInfoAsync(RemoteViewerClient.DeviceInfoHandshakeTimeout, cancellation))
                throw new TimeoutException("独立屏幕连接未完成握手。");
            if (!Current()) throw new OperationCanceledException("主连接已改变。");
            if (!client.IsConnected) throw new IOException("独立屏幕连接在打开窗口前已断开，请重新打开。");
            bool Can(RemoteDeviceCapabilities capability) => CanConnectedViewer(capability);
            var window = new RemoteViewerWindow(client, GetViewerWindowTitle(),
                Can(RemoteDeviceCapabilities.InputControl), Can(RemoteDeviceCapabilities.ClipboardText),
                Can(RemoteDeviceCapabilities.FileReceive), Can(RemoteDeviceCapabilities.FileDropPaste),
                Can(RemoteDeviceCapabilities.FileSend), isAndroidRemote: false);
            screen.Window = window;
            window.SetRemotePlatform(GetCurrentRemotePlatform());
            window.ConfigureScreenWindows(GetViewerWindowTitle(), OpenAdditionalScreenAsync, TryActivateAdditionalScreen);
            window.SetCaptureTargets(_viewerCaptureTargetBox.Items.OfType<CaptureTargetInfo>().ToArray(), target.Id);
            window.SetCaptureTargetSelectionEnabled(false);
            window.FormClosed += async (_, _) => await AdditionalScreenClosedAsync(target.Id, screen);
            window.ShowIndependentWindow();
            SetViewerStatus($"已打开独立窗口：{target.DisplayName}。各窗口可分别移动、全屏和关闭。", SuccessTextColor);
        }
        catch
        {
            if (_additionalScreens.TryGetValue(target.Id, out var current) && ReferenceEquals(current, screen))
                _additionalScreens.Remove(target.Id);
            screen.Dispose();
            await DisconnectUnusedScreenControllerAsync(screen);
            throw;
        }
    }

    private bool TryActivateAdditionalScreen(CaptureTargetInfo target)
    {
        if (_isClosing || IsDisposed || !_additionalScreens.TryGetValue(target.Id, out var screen) ||
            screen.Closing || !IsScreenControllerCurrent(screen)) return false;
        if (screen.Window is { IsDisposed: false } window && screen.Client.IsConnected)
        {
            window.ShowIndependentWindow();
            SetViewerStatus($"已显示独立窗口：{target.DisplayName}；原窗口继续显示原来的屏幕。", SuccessTextColor);
            return true;
        }
        if (screen.Window is null)
        {
            SetViewerStatus($"正在打开 {target.DisplayName}，请稍候。", MutedTextColor);
            return true;
        }
        return false;
    }

    private async Task AdditionalScreenClosedAsync(string targetId, AdditionalScreenSession screen)
    {
        if (screen.Closing || !_additionalScreens.TryGetValue(targetId, out var current) || !ReferenceEquals(current, screen)) return;
        _additionalScreens.Remove(targetId);
        screen.Dispose();
        await screen.Cleanup;
        await DisconnectUnusedScreenControllerAsync(screen);
    }

    private bool IsScreenControllerCurrent(AdditionalScreenSession screen)
    {
        if (_viewerClient.InputConnectionGeneration != screen.ControllerGeneration) return false;
        lock (_viewerReconnectLock)
            return ReferenceEquals(_pendingViewerConnection?.Connection ?? _viewerReconnectIntent?.Connection,
                screen.ControllerConnection);
    }

    private async Task DisconnectUnusedScreenControllerAsync(AdditionalScreenSession screen)
    {
        // Disposal can finish after a different manual connection has started.
        // Match both the published transport and its pending/qualified intent;
        // never cancel the newer handshake or its automatic-reconnect owner.
        if (!_closingAdditionalScreens && !_isClosing && !IsDisposed && _additionalScreens.Count == 0 &&
            (_viewerWindow is null || _viewerWindow.IsDisposed) && IsScreenControllerCurrent(screen))
        {
            CancelViewerReconnectIntent();
            try { await _viewerClient.DisconnectIfCurrentGenerationAsync(screen.ControllerGeneration); }
            catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or ObjectDisposedException or InvalidOperationException)
            {
                if (!_isClosing && !IsDisposed && _viewerClient.InputConnectionGeneration == screen.ControllerGeneration)
                    SetViewerStatus("关闭最后一个远程窗口后断开失败：" + error.Message, DangerColor);
            }
        }
    }

    private void CloseAdditionalScreens()
    {
        _closingAdditionalScreens = true;
        try
        {
            AdditionalScreenSession[] screens = _additionalScreens.Values.ToArray();
            _additionalScreens.Clear();
            foreach (AdditionalScreenSession screen in screens) screen.Dispose();
        }
        finally { _closingAdditionalScreens = false; }
    }
}
