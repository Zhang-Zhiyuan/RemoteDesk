namespace RemoteDesk;

internal sealed partial class RemoteViewerWindow
{
    private readonly Button _additionalScreenButton;
    private readonly ContextMenuStrip _additionalScreenMenu = new();
    private Func<CaptureTargetInfo, Task>? _openAdditionalScreen;
    private Func<CaptureTargetInfo, bool>? _activateAdditionalScreen;
    private bool _openingAdditionalScreen;
    private string? _screenWindowTitle;
    private CaptureTargetInfo? _lastKnownScreenTarget;

    internal CaptureTargetInfo? SelectedCaptureTarget => _captureTargets.FirstOrDefault(target =>
        string.Equals(target.Id, _selectedCaptureTargetId, StringComparison.OrdinalIgnoreCase)) ??
        (string.Equals(_lastKnownScreenTarget?.Id, _selectedCaptureTargetId, StringComparison.OrdinalIgnoreCase)
            ? _lastKnownScreenTarget : null);

    internal void ConfigureScreenWindows(string title, Func<CaptureTargetInfo, Task>? openAdditionalScreen,
        Func<CaptureTargetInfo, bool>? activateAdditionalScreen = null)
    {
        _screenWindowTitle = title;
        _openAdditionalScreen = openAdditionalScreen;
        _activateAdditionalScreen = activateAdditionalScreen;
        UpdateAdditionalScreenControls();
    }

    private void OnScreenAttachmentAvailable() => OnUi(UpdateAdditionalScreenControls);

    private void UpdateAdditionalScreenControls()
    {
        if (_additionalScreenButton.IsDisposed) return;
        CaptureTargetInfo[] screens = _captureTargets.Where(target => !string.Equals(target.Id,
            ScreenCaptureTarget.AllScreensId, StringComparison.OrdinalIgnoreCase)).ToArray();
        _additionalScreenButton.Visible = _openAdditionalScreen is not null && screens.Length > 1;
        _additionalScreenButton.Enabled = !_openingAdditionalScreen && _client.CanOpenAdditionalScreen;
        _additionalScreenButton.Text = _openingAdditionalScreen ? "打开中…" : "多屏分窗";
        _toolTip.SetToolTip(_additionalScreenButton, _client.CanOpenAdditionalScreen
            ? "将另一块远程屏幕打开为独立窗口，可拖到本地另一台显示器；关闭其中一个不影响另一个。"
            : "多屏分窗目前需要两端均为支持此功能的 Windows 版本；当前连接仍可使用“切换屏幕”。");
        CaptureTargetInfo? selected = SelectedCaptureTarget;
        if (selected is not null) _lastKnownScreenTarget = selected;
        if (_screenWindowTitle is not null)
        {
            // Keep the screen identity visible in a narrow title/taskbar label.
            // Temporary topology loss must not erase which window belongs to it.
            Text = selected is null ? _screenWindowTitle : $"{selected.DisplayName} — {_screenWindowTitle}";
        }
        QueueStatusFooterLayout();
    }

    private async Task OpenAdditionalScreenAsync()
    {
        if (_openAdditionalScreen is null || !_additionalScreenButton.Enabled) return;
        ReleaseAllRemoteInputs();
        CaptureTargetInfo[] targets = _captureTargets.Where(target =>
            !string.Equals(target.Id, ScreenCaptureTarget.AllScreensId, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(target.Id, _selectedCaptureTargetId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (targets.Length == 1) await OpenAdditionalScreenAsync(targets[0]);
        else if (targets.Length > 1)
        {
            foreach (ToolStripItem item in _additionalScreenMenu.Items.Cast<ToolStripItem>().ToArray()) item.Dispose();
            _additionalScreenMenu.Items.Clear();
            foreach (CaptureTargetInfo target in targets)
                _additionalScreenMenu.Items.Add(target.DisplayName, null, async (_, _) => await OpenAdditionalScreenAsync(target));
            // This action may live in the narrow-window overflow, so anchor its
            // menu to the viewer, not to a hidden/reparented toolbar button.
            _additionalScreenMenu.Show(this, PointToClient(Cursor.Position));
        }
    }

    private async Task OpenAdditionalScreenAsync(CaptureTargetInfo target)
    {
        if (_openingAdditionalScreen || _openAdditionalScreen is null) return;
        _openingAdditionalScreen = true;
        UpdateAdditionalScreenControls();
        try { await _openAdditionalScreen(target); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_isClosing && !IsDisposed) SetStatus($"打开独立屏幕失败：{error.Message}", DangerTextColor); }
        finally
        {
            _openingAdditionalScreen = false;
            if (!_isClosing && !IsDisposed) UpdateAdditionalScreenControls();
        }
    }
}
