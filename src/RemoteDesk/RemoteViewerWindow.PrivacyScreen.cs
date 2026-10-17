namespace RemoteDesk;

internal sealed partial class RemoteViewerWindow
{
    private Button _privacyScreenButton = null!;
    private readonly System.Windows.Forms.Timer _privacyScreenTimer = new() { Interval = 3000 };
    private PrivacyScreenStatus? _privacyStatus;
    private bool _privacyBusy;
    private long _privacyGeneration = -1;
    private void InitializePrivacyScreen()
    {
        _privacyScreenButton = CreateStatusActionButton("隐私屏：待确认");
        _privacyScreenButton.Click += async (_, _) => await TogglePrivacyScreenAsync();
        _privacyScreenTimer.Tick += async (_, _) => await RefreshPrivacyScreenAsync();
        Shown += async (_, _) => { _privacyScreenTimer.Start(); await RefreshPrivacyScreenAsync(); };
    }
    private void UpdatePrivacyScreenButton()
    {
        bool supported = _client.SupportsPrivacyScreen;
        if (_privacyGeneration != _client.InputConnectionGeneration) _privacyStatus = null;
        _privacyScreenButton.Enabled = supported && !_privacyBusy;
        string label = !supported ? "隐私屏：不支持" : _privacyBusy ? "隐私屏：确认中" :
            _privacyStatus is null ? "隐私屏：状态未知" : _privacyStatus.Enabled ? "隐私屏：开" : !_privacyStatus.Allowed ? "隐私屏：未允许" : "隐私屏：关";
        if (_privacyScreenButton.Text != label) { _privacyScreenButton.Text = label; QueueStatusFooterLayout(); }
        _toolTip.SetToolTip(_privacyScreenButton, !supported ? "当前被控系统或版本不支持隐私屏。" :
            _privacyStatus?.Message ?? "仅在收到被控端确认后显示开启；超时不可假定已遮挡。点击刷新并选择操作。");
    }
    private async Task RefreshPrivacyScreenAsync()
    {
        if (_isClosing || IsDisposed || _privacyBusy) return;
        if (!_client.SupportsPrivacyScreen) { _privacyStatus = null; UpdatePrivacyScreenButton(); return; }
        _privacyBusy = true;
        long generation = _client.InputConnectionGeneration;
        try { _privacyStatus = await _client.RequestPrivacyScreenAsync(PrivacyScreenAction.Query, generation); _privacyGeneration = generation; }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException or NotSupportedException) { _privacyStatus = null; }
        finally { _privacyBusy = false; if (!_isClosing && !IsDisposed) UpdatePrivacyScreenButton(); }
    }
    private async Task TogglePrivacyScreenAsync()
    {
        if (_privacyBusy || _isClosing || !_client.SupportsPrivacyScreen) return;
        await RefreshPrivacyScreenAsync();
        if (_isClosing || IsDisposed) return;
        if (_privacyStatus is null) { MessageBox.Show(this, "无法确认隐私屏状态，请重试。不要假定本地画面已遮挡。", "隐私屏"); return; }
        bool enable = !_privacyStatus.Enabled;
        if (enable && !_privacyStatus.Allowed) { MessageBox.Show(this, _privacyStatus.Message, "隐私屏"); return; }
        long generation = _client.InputConnectionGeneration;
        ReleaseAllRemoteInputs();
        _privacyBusy = true;
        if (enable && MessageBox.Show(this,
            "将遮挡被控电脑的全部本地屏幕，远端继续显示。\n本地 Ctrl+Alt+Shift+P、断开此连接可恢复。\n不阻止本地键鼠，不覆盖锁屏或 UAC，不是安全锁定。\n\n确认开启？",
            "开启隐私屏", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
        { _privacyBusy = false; UpdatePrivacyScreenButton(); return; }
        UpdatePrivacyScreenButton();
        try
        {
            _privacyStatus = await _client.RequestPrivacyScreenAsync(enable ? PrivacyScreenAction.Enable : PrivacyScreenAction.Disable, generation);
            _privacyGeneration = generation;
            if (_isClosing || IsDisposed) return;
            if (!_privacyStatus.Success) MessageBox.Show(this, _privacyStatus.Message, "隐私屏", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException or NotSupportedException)
        { _privacyStatus = null; if (!_isClosing && !IsDisposed) MessageBox.Show(this, ex.Message, "隐私屏状态未确认"); }
        finally { _privacyBusy = false; if (!_isClosing && !IsDisposed) UpdatePrivacyScreenButton(); }
    }
}
