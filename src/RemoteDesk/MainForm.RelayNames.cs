namespace RemoteDesk;

public sealed partial class MainForm
{
    private Button _relayRenameButton = null!;

    private async Task RenameRelayDeviceAsync()
    {
        if (_relayOperationInProgress || _relayRefreshInProgress || _isClosing || IsDisposed) return;
        RelayOnlineDevice? device = GetSelectedRelayDevice();
        if (device is null || !IsRelayConfigured()) return;
        if (!device.CanRename)
        {
            MessageBox.Show(this, device.NamingUnavailableReason, "共享名称", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        long generation = _relayConfigurationGeneration;
        RelayConnectionOptions options = CreateRelayOptions(device.DeviceId);
        _relayOperationInProgress = true; // Pause directory refresh while editing; keep target/configuration stable.
        UpdateRelayActionState();
        try
        {
            using var dialog = CreateRelayNameDialog(device, Font, out TextBox name);
            if (dialog.ShowDialog(this) != DialogResult.OK || _isClosing || IsDisposed ||
                generation != _relayConfigurationGeneration) return;
            string sharedName = RelayDeviceName.Normalize(name.Text);
            SetRelayStatus("正在保存共享名称……", MutedTextColor);
            await RelayTunnelClient.RenameDeviceAsync(options, sharedName, _relayOperationCancellation.Token);
            if (_isClosing || IsDisposed || generation != _relayConfigurationGeneration) return;
            await RefreshRelayDevicesAsync(silent: true);
            if (!_isClosing && !IsDisposed && generation == _relayConfigurationGeneration)
                SetRelayStatus("共享名称已保存；同一中继的其他设备刷新列表后可见。", SuccessTextColor);
        }
        catch (OperationCanceledException) when (_relayOperationCancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or TimeoutException
            or System.Security.Authentication.AuthenticationException or ArgumentException)
        {
            if (_isClosing || IsDisposed || generation != _relayConfigurationGeneration) return;
            SetRelayStatus("共享名称未确认保存，请刷新列表核对。", DangerColor);
            MessageBox.Show(this, ex.Message, "共享名称保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _relayOperationInProgress = false;
            if (!_isClosing && !IsDisposed) UpdateRelayActionState();
        }
    }

    internal static Form CreateRelayNameDialog(RelayOnlineDevice device, Font font, out TextBox name)
    {
        var dialog = new Form { Text = "共享名称 · " + device.MachineName, StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
            ClientSize = new Size(480, 220), AutoScaleMode = AutoScaleMode.Dpi, Font = font };
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 3 };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = "保存在中继服务器，使用同一服务器的所有设备可见。\n不更改系统名称或设备密钥；清空可恢复系统原名。\n系统原名：" + device.OriginalMachineName,
            AutoSize = true, Dock = DockStyle.Top, MaximumSize = new Size(440, 0) });
        var input = new TextBox { Text = device.SharedName, Dock = DockStyle.Top, MaxLength = 80, PlaceholderText = "输入共享名称（可留空）" };
        name = input;
        panel.Controls.Add(input);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var save = new Button { Text = "保存并同步", AutoSize = true };
        save.Click += (_, _) =>
        {
            try { _ = RelayDeviceName.Normalize(input.Text); dialog.DialogResult = DialogResult.OK; }
            catch (ArgumentException ex) { MessageBox.Show(dialog, ex.Message, "名称无效", MessageBoxButtons.OK, MessageBoxIcon.Warning); input.Focus(); }
        };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(save); buttons.Controls.Add(cancel); panel.Controls.Add(buttons);
        dialog.Controls.Add(panel); dialog.AcceptButton = save; dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => { input.Focus(); input.SelectAll(); };
        ResponsiveWindowLayout.ConfigureDialog(dialog, new Size(520, 300), new Size(360, 220));
        return dialog;
    }
}
