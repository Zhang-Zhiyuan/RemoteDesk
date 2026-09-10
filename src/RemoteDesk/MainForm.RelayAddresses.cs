namespace RemoteDesk;

public sealed partial class MainForm
{
    private Button _relayReportAddressButton = null!;
    private Button _relayAddressButton = null!;

    private async Task ShowRelayAddressesAsync()
    {
        string? id = GetSelectedRelayDevice()?.DeviceId;
        if (id is null || _relayRefreshInProgress) return;
        long generation = _relayConfigurationGeneration;
        // Do not fill a direct endpoint from an old row after a DHCP change,
        // directory failure, host departure or a switch to another relay.
        await RefreshRelayDevicesAsync(silent: false);
        if (_isClosing || IsDisposed || generation != _relayConfigurationGeneration) return;
        RelayOnlineDevice? device = _relayDevicesList.Items.Cast<ListViewItem>()
            .Select(item => item.Tag as RelayOnlineDevice).FirstOrDefault(item => item?.DeviceId == id);
        if (device is null) { SetRelayStatus("设备已离线或目录未能刷新，请稍后重试。", MutedTextColor); return; }
        if (device.DirectAddresses.Count == 0)
        {
            MessageBox.Show(this, "设备尚未上报 IP。请更新中继服务器和被控端，或点击对方的“立即上报本机 IP”。\n\n仍可使用中继连接，不需要知道对方 IP。",
                "设备地址", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dialog = new Form { Text = $"{device.MachineName} · 最新地址", ClientSize = new Size(540, 270),
            Font = Font, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
        var note = new Label { Dock = DockStyle.Top, Height = 80, Padding = new Padding(12),
            Text = $"设备 ID：{device.DeviceId}\n同一局域网 / 可路由网络可使用下列地址；跨网仍用中继。" };
        var list = new ListBox { Dock = DockStyle.Fill };
        list.Items.AddRange(device.DirectAddresses.Select(address => $"{address}:{device.DirectPort}").ToArray());
        list.SelectedIndex = 0;
        var use = new Button { Text = "填入 IP 直连", Dock = DockStyle.Bottom, Height = 42,
            DialogResult = DialogResult.OK, Enabled = !RelayDeviceSelectionPolicy.IsLocalDevice(device, _settings.Relay.DeviceId) };
        dialog.Controls.Add(list); dialog.Controls.Add(note); dialog.Controls.Add(use); dialog.AcceptButton = use;
        if (dialog.ShowDialog(this) != DialogResult.OK || list.SelectedIndex < 0 || !use.Enabled) return;
        _selectedHistoryDevice = GetRecentDevices().FirstOrDefault(saved => RemoteDeviceIdentity.Same(saved.DeviceId, id));
        _viewerHostBox.Text = device.DirectAddresses[list.SelectedIndex];
        _viewerPortBox.Value = device.DirectPort;
        _viewerAutoPortBox.Checked = false;
        _viewerPasswordBox.Text = _relayViewerPasswordBox.Text;
        _tabs.SelectedIndex = 1;
        SetViewerStatus("已填入最新地址和端口。确认设备后点击连接；地址不可达时可回到中继连接。", MutedTextColor);
    }
}
