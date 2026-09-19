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
        using var dialog = CreateRelayAddressesDialog(device, _settings.Relay.DeviceId, Font, out ListBox list);
        if (dialog.ShowDialog(this) != DialogResult.OK || list.SelectedIndex < 0 ||
            dialog.AcceptButton is not Button { Enabled: true }) return;
        _selectedHistoryDevice = GetRecentDevices().FirstOrDefault(saved => RemoteDeviceIdentity.Same(saved.DeviceId, id));
        _viewerHostBox.Text = device.DirectAddresses[list.SelectedIndex];
        _viewerPortBox.Value = device.DirectPort;
        _viewerAutoPortBox.Checked = false;
        _viewerPasswordBox.Text = _relayViewerPasswordBox.Text;
        _tabs.SelectedIndex = 1;
        SetViewerStatus("已填入最新地址和端口。确认设备后点击连接；地址不可达时可回到中继连接。", MutedTextColor);
    }

    internal static Form CreateRelayAddressesDialog(RelayOnlineDevice device, string localDeviceId, Font font, out ListBox addresses)
    {
        var dialog = new Form { Text = $"{device.MachineName} · 最新地址", ClientSize = new Size(540, 300),
            Font = font, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var note = new Label { Dock = DockStyle.Top, AutoSize = true, UseMnemonic = false,
            Margin = new Padding(0, 0, 0, 10),
            Text = $"设备 ID：{device.DeviceId}\n同一局域网 / 可路由网络可使用下列地址；跨网仍用中继。" };
        var list = new ListBox { Dock = DockStyle.Fill, Height = 150, MinimumSize = new Size(0, 100),
            HorizontalScrollbar = true, IntegralHeight = false, AccessibleName = "设备最新地址" };
        list.Items.AddRange(device.DirectAddresses.Select(address => address.Contains(':')
            ? $"[{address}]:{device.DirectPort}" : $"{address}:{device.DirectPort}").ToArray());
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        addresses = list;
        var use = new Button { Text = "填入 IP 直连", AutoSize = true, DialogResult = DialogResult.OK,
            Enabled = list.Items.Count > 0 && !RelayDeviceSelectionPolicy.IsLocalDevice(device, localDeviceId) };
        var close = new Button { Text = "关闭", AutoSize = true, DialogResult = DialogResult.Cancel };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Margin = new Padding(0, 10, 0, 0) };
        actions.Controls.Add(use); actions.Controls.Add(close);
        ConfigureWrappingDialogActions(actions);
        layout.Controls.Add(note, 0, 0); layout.Controls.Add(list, 0, 1); layout.Controls.Add(actions, 0, 2);
        dialog.Controls.Add(layout); dialog.AcceptButton = use; dialog.CancelButton = close;
        ResponsiveWindowLayout.ConfigureDialog(dialog, new Size(580, 380), new Size(360, 230));
        return dialog;
    }
}
