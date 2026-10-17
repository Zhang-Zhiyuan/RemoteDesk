namespace RemoteDesk;

public sealed partial class MainForm
{
    private CheckBox _allowPrivacyScreenBox = null!;
    private Control CreatePrivacyScreenSettings()
    {
        _allowPrivacyScreenBox = new CheckBox { AutoSize = true, Dock = DockStyle.Top,
            Text = "允许远端开关隐私屏（勾选不会立即遮屏）" };
        ResponsiveWindowLayout.ConfigureWrappedCheckBox(_allowPrivacyScreenBox);
        SetToolTip(_allowPrivacyScreenBox, "遮挡全部本地显示器，远端仍可操作。Ctrl+Alt+Shift+P 可恢复；断开开启它的连接后恢复。\n不阻止本地键鼠，不覆盖锁屏或 UAC；不是安全锁定。Windows 10 2004 起支持。" );
        _allowPrivacyScreenBox.CheckedChanged += async (_, _) =>
        {
            if (_applyingSettings || _isClosing || _layoutPreview) return;
            bool old = _settings.Host.AllowPrivacyScreen;
            bool desired = _allowPrivacyScreenBox.Checked;
            _settings.Host.AllowPrivacyScreen = desired;
            // A failed settings write must not undo an explicit local revocation.
            Task? restore = desired ? null : _hostServer.SetPrivacyScreenAllowedAsync(false);
            if (!TrySaveSettings())
            {
                if (desired)
                {
                    _settings.Host.AllowPrivacyScreen = old;
                    _applyingSettings = true;
                    try { _allowPrivacyScreenBox.Checked = old; } finally { _applyingSettings = false; }
                }
                if (restore is not null) await restore;
                return;
            }
            await (restore ?? _hostServer.SetPrivacyScreenAllowedAsync(true));
        };
        return _allowPrivacyScreenBox;
    }
}
