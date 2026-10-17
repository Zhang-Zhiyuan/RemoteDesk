namespace RemoteDesk;

internal sealed partial class RemoteViewerWindow
{
    private Button _privacyScreenButton = null!;
    private readonly System.Windows.Forms.Timer _privacyScreenTimer = new() { Interval = 3000 };
    private PrivacyScreenStatus? _privacyStatus;
    private bool _privacyQueryBusy;
    private long _privacyQueryGeneration = -1;
    private PrivacyScreenAction? _privacyCommand;
    private long _privacyGeneration = -1, _privacyRevision;
    private ContextMenuStrip _privacyMenu = null!;

    private void InitializePrivacyScreen()
    {
        _privacyScreenButton = CreateStatusActionButton("隐私屏：待确认");
        _privacyScreenButton.Click += async (_, _) => await TogglePrivacyScreenAsync();
        _privacyMenu = new ContextMenuStrip();
        var enable = new ToolStripMenuItem("开启隐私屏");
        var disable = new ToolStripMenuItem("关闭隐私屏 / 恢复本地显示");
        var refresh = new ToolStripMenuItem("刷新状态");
        enable.Click += async (_, _) => await SetPrivacyScreenAsync(PrivacyScreenAction.Enable);
        disable.Click += async (_, _) => await SetPrivacyScreenAsync(PrivacyScreenAction.Disable);
        refresh.Click += async (_, _) => await RefreshPrivacyScreenAsync();
        _privacyMenu.Items.AddRange([enable, disable, refresh]);
        _privacyMenu.Opening += (_, _) =>
        {
            _privacyMenu.Font = Font;
            enable.Enabled = _client.SupportsPrivacyScreen && _privacyCommand is null && _privacyStatus?.Allowed != false;
            // Recovery remains available even when an enable/query reply is delayed.
            disable.Enabled = _client.SupportsPrivacyScreen && _privacyCommand != PrivacyScreenAction.Disable;
            refresh.Enabled = !_privacyQueryBusy && _privacyCommand is null;
        };
        _privacyScreenButton.ContextMenuStrip = _privacyMenu;
        _privacyScreenButton.Disposed += (_, _) => _privacyMenu.Dispose();
        _privacyScreenTimer.Tick += async (_, _) => await RefreshPrivacyScreenAsync();
        Shown += async (_, _) => { _privacyScreenTimer.Start(); await RefreshPrivacyScreenAsync(); };
    }

    private void UpdatePrivacyScreenButton()
    {
        bool supported = _client.SupportsPrivacyScreen;
        if (_privacyGeneration != _client.InputConnectionGeneration)
        {
            _privacyGeneration = _client.InputConnectionGeneration;
            _privacyStatus = null;
            _privacyCommand = null;
            _privacyQueryBusy = false;
            _privacyRevision++;
        }
        _privacyScreenButton.Enabled = supported && _privacyCommand != PrivacyScreenAction.Disable;
        string label = !supported ? "隐私屏：不支持" :
            _privacyCommand == PrivacyScreenAction.Enable ? "取消遮屏 / 恢复显示" :
            _privacyCommand == PrivacyScreenAction.Disable ? "正在恢复显示…" :
            _privacyStatus is null ? "隐私屏：状态未知" : _privacyStatus.Enabled ? "关闭隐私屏" :
            !_privacyStatus.Allowed ? "隐私屏：未允许" : "开启隐私屏";
        if (_privacyScreenButton.Text != label) { _privacyScreenButton.Text = label; QueueStatusFooterLayout(); }
        _privacyScreenButton.AccessibleName = label;
        _toolTip.SetToolTip(_privacyScreenButton, !supported ? "当前被控系统或版本不支持隐私屏。" :
            _privacyCommand == PrivacyScreenAction.Enable ? "正在请求遮挡全部本地屏幕。再次点击可请求恢复；未确认前不能假定已遮挡。" :
            (_privacyStatus?.Message ?? "尚未确认状态。点击可直接选择开启或恢复显示。") +
            "\n连接期间可随时切换，无需重连。右键可明确开启 / 关闭；本地 Ctrl+Alt+Shift+P 也可恢复。");
    }

    internal static bool AcceptPrivacyReply(long requestedGeneration, long currentGeneration, long requestedRevision, long currentRevision)
        => requestedGeneration == currentGeneration && requestedRevision == currentRevision;

    private async Task RefreshPrivacyScreenAsync()
    {
        if (!_isClosing && !IsDisposed) UpdatePrivacyScreenButton();
        if (_isClosing || IsDisposed || _privacyQueryBusy || _privacyCommand is not null) return;
        if (!_client.SupportsPrivacyScreen) { _privacyStatus = null; UpdatePrivacyScreenButton(); return; }
        _privacyQueryBusy = true;
        long generation = _client.InputConnectionGeneration, revision = _privacyRevision;
        _privacyQueryGeneration = generation;
        try
        {
            var status = await _client.RequestPrivacyScreenAsync(PrivacyScreenAction.Query, generation);
            if (AcceptPrivacyReply(generation, _client.InputConnectionGeneration, revision, _privacyRevision))
            { _privacyStatus = status; _privacyGeneration = generation; }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException or NotSupportedException)
        { if (AcceptPrivacyReply(generation, _client.InputConnectionGeneration, revision, _privacyRevision)) _privacyStatus = null; }
        finally
        {
            if (_privacyQueryGeneration == generation) _privacyQueryBusy = false;
            if (!_isClosing && !IsDisposed) UpdatePrivacyScreenButton();
        }
    }

    private Task TogglePrivacyScreenAsync()
    {
        if (_isClosing || IsDisposed || !_client.SupportsPrivacyScreen) return Task.CompletedTask;
        UpdatePrivacyScreenButton();
        if (_privacyCommand == PrivacyScreenAction.Enable) return SetPrivacyScreenAsync(PrivacyScreenAction.Disable);
        if (_privacyCommand is not null) return Task.CompletedTask;
        if (_privacyGeneration != _client.InputConnectionGeneration) _privacyStatus = null;
        if (_privacyStatus is null || !_privacyStatus.Allowed)
        {
            if (_privacyScreenButton.Visible && ClientRectangle.IntersectsWith(RectangleToClient(_privacyScreenButton.RectangleToScreen(_privacyScreenButton.ClientRectangle))))
                _privacyMenu.Show(_privacyScreenButton, new Point(0, 0), ToolStripDropDownDirection.AboveRight);
            else _privacyMenu.Show(Cursor.Position);
            return Task.CompletedTask;
        }
        return SetPrivacyScreenAsync(_privacyStatus.Enabled ? PrivacyScreenAction.Disable : PrivacyScreenAction.Enable);
    }

    private async Task SetPrivacyScreenAsync(PrivacyScreenAction action)
    {
        if (!_isClosing && !IsDisposed) UpdatePrivacyScreenButton();
        if (_isClosing || IsDisposed || !_client.SupportsPrivacyScreen ||
            _privacyCommand == PrivacyScreenAction.Disable || action == PrivacyScreenAction.Enable && _privacyCommand is not null) return;
        long generation = _client.InputConnectionGeneration, revision = ++_privacyRevision;
        ReleaseAllRemoteInputs();
        _privacyCommand = action;
        UpdatePrivacyScreenButton();
        try
        {
            var status = await _client.RequestPrivacyScreenAsync(action, generation);
            if (_isClosing || IsDisposed || !AcceptPrivacyReply(generation, _client.InputConnectionGeneration, revision, _privacyRevision)) return;
            _privacyStatus = status; _privacyGeneration = generation;
            if (!status.Success) MessageBox.Show(this, status.Message, "隐私屏", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException or NotSupportedException)
        {
            if (!_isClosing && !IsDisposed && AcceptPrivacyReply(generation, _client.InputConnectionGeneration, revision, _privacyRevision))
            { _privacyStatus = null; MessageBox.Show(this, ex.Message, "隐私屏状态未确认"); }
        }
        finally
        {
            if (revision == _privacyRevision) _privacyCommand = null;
            if (!_isClosing && !IsDisposed) UpdatePrivacyScreenButton();
        }
    }
}
