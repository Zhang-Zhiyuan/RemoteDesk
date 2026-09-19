using System.Net;

namespace RemoteDesk;

public sealed partial class MainForm
{
    private CheckBox _viewerAutoPortBox = null!;
    private Button _addDeviceButton = null!;
    private SavedRemoteDevice? _selectedHistoryDevice;
    private SavedRemoteDevice? _confirmedHistorySource;
    private long _rememberedViewerGeneration;

    private void InitializeDeviceRefresh()
    {
        var timer = new System.Windows.Forms.Timer { Interval = 30_000 };
        timer.Tick += async (_, _) =>
        {
            if (Visible && WindowState != FormWindowState.Minimized && !_viewerClient.IsConnected && !_viewerActionInProgress &&
                _discoveryScanCancellation is null) await DiscoverHostsAsync(silent: true);
        };
        timer.Start();
        Disposed += (_, _) => timer.Dispose();
    }

    internal static (string Host, int Port, bool ExplicitPort) ParseDeviceEndpoint(string address, string port)
    {
        string host = address.Trim();
        bool explicitPort = !string.IsNullOrWhiteSpace(port);
        if (host.StartsWith('['))
        {
            int end = host.IndexOf(']');
            if (end < 0) throw new ArgumentException("IPv6 地址格式无效。");
            string suffix = host[(end + 1)..];
            if (suffix.Length > 0)
            {
                if (!suffix.StartsWith(':') || suffix.Length < 2) throw new ArgumentException("端口格式无效。");
                port = suffix[1..]; explicitPort = true;
            }
            host = host[1..end];
        }
        else if (host.Count(c => c == ':') == 1)
        {
            int split = host.IndexOf(':'); port = host[(split + 1)..]; host = host[..split]; explicitPort = true;
        }
        if (host.Length is 0 or > 253 || host.Any(c => char.IsWhiteSpace(c) || "/\\@[]".Contains(c)))
            throw new ArgumentException("请填写有效 IP 或主机名，不要包含协议或路径。");
        int number = Protocol.DefaultPort;
        if (explicitPort && (!int.TryParse(port, out number) || number is < 1 or > 65535))
            throw new ArgumentException("端口应为 1–65535；留空可自动探测。");
        host = host.TrimEnd('.');
        if (host.Length == 0 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new ArgumentException("请填写有效 IP 或主机名。");
        return (host, number, explicitPort);
    }

    internal static bool SameSavedMachine(SavedRemoteDevice a, SavedRemoteDevice b)
    {
        if (RemoteDeviceIdentity.Same(a.DeviceId, b.DeviceId)) return true;
        if (RemoteDeviceIdentity.Conflicts(a.DeviceId, b.DeviceId)) return false;
        if (!string.Equals(a.Address?.Trim(), b.Address?.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        if (a.Port == b.Port) return true;
        // Keep the legacy automatic-port migration, but never merge two known
        // different application identities just because they share an IP/name.
        return (RemoteDeviceIdentity.Normalize(a.DeviceId) is null || RemoteDeviceIdentity.Normalize(b.DeviceId) is null) &&
            RemotePortPolicy.AreCompatibleHostPorts(a.Port, b.Port);
    }

    internal static IReadOnlyList<DiscoveredHost> CollapseDiscoveryAliases(IEnumerable<DiscoveredHost> hosts) =>
        hosts.GroupBy(h => RemoteDeviceIdentity.Normalize(h.DeviceId) is string id ? "id:" + id : $"ep:{h.Address}:{h.Port}",
                StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(h => h.IsHostRunning).ThenBy(h => h.Address, StringComparer.OrdinalIgnoreCase).First()).ToArray();

    private SavedRemoteDevice? FindSavedForDiscovery(string host, int port, string? deviceId) =>
        FindSavedForDiscovery(GetRecentDevices(), host, port, deviceId);

    internal static SavedRemoteDevice? FindSavedForDiscovery(IEnumerable<SavedRemoteDevice> saved, string host, int port, string? deviceId) =>
        saved.FirstOrDefault(d => RemoteDeviceIdentity.Same(d.DeviceId, deviceId)) ??
        saved.FirstOrDefault(d => !RemoteDeviceIdentity.Conflicts(d.DeviceId, deviceId) && d.Port == port &&
            string.Equals(d.Address?.Trim(), host.Trim(), StringComparison.OrdinalIgnoreCase));

    internal static IReadOnlyList<DiscoveredHost> FindDeviceCandidates(string address, int port, string? deviceId,
        string? name, IReadOnlyList<DiscoveredHost> hosts)
    {
        var ready = hosts.Where(h => h.IsHostRunning || h.CanRemoteStart).ToArray();
        var identity = ready.Where(h => RemoteDeviceIdentity.Same(h.DeviceId, deviceId)).ToArray();
        var sameAddress = ready.Where(h => string.Equals(h.Address, address, StringComparison.OrdinalIgnoreCase)).ToArray();
        var exact = sameAddress.Where(h => h.Port == port).ToArray();
        if (identity.Length > 0) return CollapseDiscoveryAliases(identity);
        if (exact.Length > 0) return exact;
        if (sameAddress.Length > 0) return sameAddress;
        return ready.Where(h => !string.IsNullOrWhiteSpace(name) &&
            string.Equals(h.MachineName, name, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private async Task<bool> ResolveViewerEndpointAsync()
    {
        _confirmedHistorySource = null;
        var endpoint = ParseDeviceEndpoint(_viewerHostBox.Text, _viewerAutoPortBox.Checked ? "" : _viewerPortBox.Value.ToString());
        using (var selfCheckTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            await SelfConnectionGuard.ValidateDirectHostAsync(endpoint.Host, selfCheckTimeout.Token);
        if (endpoint.ExplicitPort) _viewerAutoPortBox.Checked = false;
        _viewerHostBox.Text = endpoint.Host;
        if (endpoint.ExplicitPort) _viewerPortBox.Value = endpoint.Port;
        int requestedPort = (int)_viewerPortBox.Value;
        SavedRemoteDevice? saved = _selectedHistoryDevice;
        if (saved is not null && !string.Equals(saved.Address, endpoint.Host, StringComparison.OrdinalIgnoreCase) &&
            !_lastDiscoveredHosts.Any(h => h.Address == endpoint.Host && RemoteDeviceIdentity.Same(h.DeviceId, saved.DeviceId))) saved = null;
        SelfConnectionGuard.RejectLocalDevice(saved?.DeviceId);
        saved ??= FindSavedForDiscovery(endpoint.Host, requestedPort, null);
        // An old self record's IP may now belong to a different DHCP client.
        // Do not turn that stale address match into a permanent address ban.
        if (RemoteDeviceIdentity.Same(saved?.DeviceId, RemoteDeviceIdentity.LocalId)) saved = null;
        if (!_viewerAutoPortBox.Checked) return ConfirmMovedSavedDevice(saved, endpoint.Host, requestedPort);
        SetViewerStatus("正在自动查找设备和监听端口…", MutedTextColor);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        IReadOnlyList<DiscoveredHost> found;
        try
        {
            var targets = GetRecentDevices().Where(d => string.Equals(d.Address, endpoint.Host, StringComparison.OrdinalIgnoreCase))
                .Select(d => new DiscoveryProbeTarget(endpoint.Host, d.Port)).Append(new(endpoint.Host, requestedPort)).ToArray();
            found = await NetworkDiscoveryService.DiscoverAsync(TimeSpan.FromMilliseconds(1100), timeout.Token,
                directTargets: targets, hostProbePort: Protocol.DefaultPort, includeBroadcast: saved is not null);
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Net.Sockets.SocketException or IOException)
        { found = []; }
        if (_isClosing || IsDisposed) return false;
        found = RemoveLocalDiscoveredHosts(found, out _);
        // Resolve DNS aliases to the actual source IP before selecting the response.
        IReadOnlyList<IPAddress> ips = [];
        try { ips = await NetworkDiscoveryService.ResolveDirectProbeAddressesAsync([endpoint.Host], timeout.Token); }
        catch (Exception ex) when (ex is OperationCanceledException or System.Net.Sockets.SocketException) { }
        var sameTarget = found.Where(h => ips.Any(ip => ip.ToString() == h.Address)).ToArray();
        IReadOnlyList<DiscoveredHost> options = saved is null ? sameTarget.Where(h => h.IsHostRunning || h.CanRemoteStart).ToArray() :
            FindDeviceCandidates(endpoint.Host, requestedPort, saved.DeviceId, saved.MachineName, found);
        if (options.Count == 0) options = sameTarget.Where(h => h.IsHostRunning || h.CanRemoteStart).ToArray();
        if (options.Count == 0) return ConfirmMovedSavedDevice(saved, endpoint.Host, requestedPort);
        DiscoveredHost? chosen = options.Count == 1 ? options[0] : ChooseDeviceEndpoint(options);
        if (chosen is null || !ConfirmMovedSavedDevice(saved, chosen.Address, chosen.Port)) return false;
        _viewerHostBox.Text = chosen.Address; _viewerPortBox.Value = ClampToRange(chosen.Port, _viewerPortBox);
        UpdateDiscoveredHosts(MergeDiscoveredHosts(_lastDiscoveredHosts, found), silent: true);
        return true;
    }

    private bool ConfirmMovedSavedDevice(SavedRemoteDevice? saved, string host, int port)
    {
        SelfConnectionGuard.RejectLocalDevice(saved?.DeviceId);
        bool confirmed = saved is null || (string.Equals(saved.Address, host, StringComparison.OrdinalIgnoreCase) && saved.Port == port) ||
            MessageBox.Show(this, $"{saved.Remark ?? saved.MachineName}\n原地址：{saved.Address}:{saved.Port}\n新地址：{host}:{port}\n\n请确认这是你的设备。连接成功后自动合并同一设备的记录。",
                "设备地址已变化", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK;
        if (confirmed) _confirmedHistorySource = saved;
        return confirmed;
    }

    private DiscoveredHost? ChooseDeviceEndpoint(IReadOnlyList<DiscoveredHost> options)
    {
        using var dialog = new Form { Text = "选择设备 / 端口", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(540, 270), Font = Font, MinimizeBox = false, MaximizeBox = false };
        var list = new ListBox { Dock = DockStyle.Fill }; list.Items.AddRange(options.Cast<object>().ToArray()); list.SelectedIndex = 0;
        var ok = new Button { Text = "选择", Dock = DockStyle.Bottom, Height = 40, DialogResult = DialogResult.OK };
        dialog.Controls.Add(list); dialog.Controls.Add(ok); dialog.AcceptButton = ok;
        ResponsiveWindowLayout.ConfigureDialog(dialog, new Size(560, 340), new Size(360, 220));
        return dialog.ShowDialog(this) == DialogResult.OK ? list.SelectedItem as DiscoveredHost : null;
    }

    private void AddDevice()
    {
        if (_viewerActionInProgress || _viewerClient.IsConnected) return;
        using var dialog = CreateAddDeviceDialog(Font, out TextBox[] fields, out Label error, out Button save);
        TextBox address = fields[0], port = fields[1], password = fields[2], remark = fields[3];
        save.Click += (_, _) =>
        {
            try
            {
                var value = ParseDeviceEndpoint(address.Text, port.Text);
                if (string.IsNullOrWhiteSpace(password.Text)) throw new ArgumentException("请输入对方设备的设备密钥。");
                var node = new SavedRemoteDevice { Address = value.Host, Port = value.Port, AutoDetectPort = !value.ExplicitPort,
                    MachineName = value.Host, Remark = string.IsNullOrWhiteSpace(remark.Text) ? null : remark.Text,
                    ProtectedPassword = AppSettingsService.ProtectSecret(password.Text), LastConnectedAt = DateTimeOffset.Now };
                UpsertRecentDevice(GetRecentDevices(), node);
                if (!TrySaveSettings()) { error.Text = "保存失败，请检查配置目录权限。"; return; }
                UpdateDiscoveredHosts(_lastDiscoveredHosts, true); ApplyRemoteDevice(RemoteDeviceListItem.FromSaved(node));
                dialog.DialogResult = DialogResult.OK;
            }
            catch (ArgumentException ex) { error.Text = ex.Message; }
        };
        dialog.ShowDialog(this);
    }

    internal static Form CreateAddDeviceDialog(Font font, out TextBox[] fields, out Label error, out Button save)
    {
        var dialog = new Form { Text = "新增设备", ClientSize = new Size(460, 350), Font = font,
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 10 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var address = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "IP / 主机名" };
        var port = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "留空自动探测" };
        var password = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        var remark = new TextBox { Dock = DockStyle.Fill, MaxLength = AppSettingsService.MaxSavedDeviceRemarkLength };
        fields = [address, port, password, remark]; string[] labels = ["IP / 主机名", "端口（可选）", "设备密钥", "备注（可选）"];
        for (int i = 0; i < fields.Length; i++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = labels[i], AutoSize = true, Dock = DockStyle.Top,
                UseMnemonic = false, Margin = new Padding(0, i == 0 ? 0 : 10, 0, 3) }, 0, i * 2);
            fields[i].Dock = DockStyle.Top;
            fields[i].Margin = new Padding(0);
            fields[i].AccessibleName = labels[i];
            layout.Controls.Add(fields[i], 0, i * 2 + 1);
        }
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        error = new Label { AutoSize = true, Dock = DockStyle.Top, ForeColor = DangerColor,
            UseMnemonic = false, Margin = new Padding(0, 8, 0, 0) };
        layout.Controls.Add(error, 0, 8);
        save = new Button { Text = "保存设备", AutoSize = true }; var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Margin = new Padding(0, 8, 0, 0) };
        actions.Controls.Add(save); actions.Controls.Add(cancel);
        ConfigureWrappingDialogActions(actions);
        layout.Controls.Add(actions, 0, 9);
        dialog.Controls.Add(layout); dialog.AcceptButton = save; dialog.CancelButton = cancel;
        ResponsiveWindowLayout.ConfigureDialog(dialog, new Size(500, 400), new Size(360, 240));
        return dialog;
    }
}
