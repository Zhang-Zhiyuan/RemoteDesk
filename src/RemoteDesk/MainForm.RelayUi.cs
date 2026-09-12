namespace RemoteDesk;

public sealed partial class MainForm
{
    private Button _relayLogoutButton = null!;
    private string? _relayKeySelection;

    private void SelectRelayDeviceKey()
    {
        if (_relayRefreshInProgress || _viewerClient.IsConnected || _viewerActionInProgress || _isClosing) return;
        RelayOnlineDevice? selected = GetSelectedRelayDevice();
        string? selection = selected is null || !IsRelayConfigured() ? null :
            RelayDeviceKeys.Scope(CreateRelayOptions(selected.DeviceId)) + ":" + selected.DeviceId;
        _relayViewerPasswordBox.PlaceholderText = selected is null ? "先选择在线设备" : $"{selected.MachineName} 的设备密钥";
        if (selection == _relayKeySelection) return; // Keep edits during periodic directory refresh.
        _relayKeySelection = selection;
        _relayViewerPasswordBox.Text = selection is null ? "" : AppSettingsService.UnprotectSecret(
            RelayDeviceKeys.Find(_settings.Relay.DeviceKeys, CreateRelayOptions(selected!.DeviceId))) ?? "";
    }

    private string? RequestRelayDeviceKey(RelayOnlineDevice selected)
    {
        if (!string.IsNullOrEmpty(_relayViewerPasswordBox.Text)) return _relayViewerPasswordBox.Text;
        using var dialog = CreateRelayDeviceKeyDialog(selected.MachineName, Font, out TextBox key);
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        _relayViewerPasswordBox.Text = key.Text;
        return key.Text;
    }

    internal static Form CreateRelayDeviceKeyDialog(string deviceName, Font font, out TextBox password)
    {
        var dialog = new Form { Text = "连接 " + deviceName, StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
            ClientSize = new Size(440, 180), AutoScaleMode = AutoScaleMode.Dpi, Font = font };
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 3 };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = "请输入这台设备自己的密钥，不是服务器 root 密码。\n连接成功后自动记住；可在设备列表上方更改。", AutoSize = true, Dock = DockStyle.Top, MaximumSize = new Size(402, 0) });
        var key = new TextBox { Dock = DockStyle.Top, UseSystemPasswordChar = true, MaxLength = 4096 };
        password = key;
        panel.Controls.Add(key);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var connect = new Button { Text = "连接", AutoSize = true };
        connect.Click += (_, _) => { if (key.Text.Length > 0) dialog.DialogResult = DialogResult.OK; else key.Focus(); };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(connect); buttons.Controls.Add(cancel); panel.Controls.Add(buttons);
        dialog.Controls.Add(panel); dialog.AcceptButton = connect; dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => key.Focus();
        ResponsiveWindowLayout.ConfigureDialog(dialog, new Size(480, 260), new Size(360, 200));
        return dialog;
    }

    private async Task LogoutRelayServerAsync()
    {
        if (_relayOperationInProgress || _viewerActionInProgress || _viewerClient.IsConnected || IsViewerReconnecting()) return;
        if (MessageBox.Show(this, "退出后本机不再通过此服务器上线，设备记录和设备密钥保留。\n再次接入需要服务器管理员密码。\n\n如果服务器重装或身份变化，请先核实，再退出并重新登录。",
            "退出服务器？", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        RelaySettings previous = _settings.Relay;
        _settings.Relay = RelayDeviceKeys.WithoutServerLogin(previous);
        if (!TrySaveSettings())
        {
            _settings.Relay = previous;
            SetRelayStatus("退出未保存，原连接配置保持不变，请重试。", DangerColor);
            return;
        }
        _relayConfigurationGeneration++;
        _relayOperationInProgress = true;
        _relayDevicesList.Items.Clear(); _relayKeySelection = null; _relayViewerPasswordBox.Clear();
        UpdateRelayServerSummary();
        UpdateRelayActionState();
        try
        {
            await _relayHostConnector.StopAsync();
            if (!_isClosing && !IsDisposed) SetRelayStatus("已退出服务器，设备记录和设备密钥保留。", MutedTextColor);
        }
        catch (Exception ex)
        {
            if (!_isClosing && !IsDisposed)
                SetRelayStatus($"服务器登录已清除，但停止旧连接失败：{ex.Message}。请重启本机应用。", DangerColor);
        }
        finally
        {
            _relayOperationInProgress = false;
            if (!_isClosing && !IsDisposed) UpdateRelayActionState();
        }
    }
}
