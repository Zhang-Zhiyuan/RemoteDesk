namespace RemoteDesk;

public sealed partial class MainForm
{
    private Button _directSshButton = null!;

    private RemoteDeviceListItem? DirectSshDevice()
    {
        var row = GetSelectedRemoteDevice();
        return row is not null && DirectSshTarget.SameHost(row.Address, _viewerHostBox.Text) &&
            row.Port == (int)_viewerPortBox.Value ? row : null;
    }

    private IEnumerable<DirectSshPreference> DirectSshHistory(string address, RemoteDeviceListItem? row)
    {
        bool reused = _discoveredHostsBox.Items.OfType<RemoteDeviceListItem>()
            .Any(item => item.ReplacedAtAddress && DirectSshTarget.SameHost(item.Address, address));
        return DirectSshPreference.ForDevice(_settings.Viewer.DirectSshTargets, address, row?.DirectoryDeviceId, reused);
    }

    private static string DirectHostSystem(RemoteDeviceListItem? row) => row?.KnownPlatform switch
    {
        RemoteDevicePlatforms.Windows => "Windows 原生",
        null or "" or RemoteDevicePlatforms.Unknown => "按地址连接（系统待确认）",
        string platform => platform
    };

    private static bool MayChooseWsl(RemoteDeviceListItem? row) =>
        row?.KnownPlatform is null or "" or RemoteDevicePlatforms.Unknown or RemoteDevicePlatforms.Windows;

    private void UpdateDirectSshButton()
    {
        if (_directSshButton is null) return;
        var row = DirectSshDevice();
        bool wsl = MayChooseWsl(row) && DirectSshHistory(_viewerHostBox.Text.Trim(), row).FirstOrDefault()?.Wsl == true;
        string label = wsl ? "WSL" : row?.KnownPlatform is null or "" or RemoteDevicePlatforms.Unknown ? "选择系统" : DirectHostSystem(row);
        _directSshButton.Text = $"SSH · {label}…";
        _directSshButton.Enabled = row?.ReplacedAtAddress != true;
        string hint = row?.ReplacedAtAddress == true ? "此地址已由另一设备使用。请选择正在监听的设备。" :
            "选择 SSH 目标系统，核对直连地址和端口，然后打开终端或 VS Code。";
        if (_toolTip.GetToolTip(_directSshButton) != hint) SetToolTip(_directSshButton, hint);
    }

    private void OpenDirectSsh()
    {
        using var dialog = CreateDirectSshDialog();
        dialog.ShowDialog(this);
    }

    private Form CreateDirectSshDialog()
    {
        string device = _viewerHostBox.Text.Trim();
        var row = DirectSshDevice();
        var history = DirectSshHistory(device, row).ToList();
        string native = DirectHostSystem(row);
        var peer = row?.DirectoryDeviceId is { } knownId ? RelayRow(knownId)?.Tag as RelayOnlineDevice : null;
        bool hasWsl = peer?.WslDestinations.Count > 0;
        string name = row?.DisplayName ?? device;
        var dialog = new Form { Text = name.Length == 0 ? "SSH 直连" : $"SSH 直连 · {name}", Font = Font, AutoScaleMode = AutoScaleMode.Dpi,
            StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(14),
            ColumnCount = 1, RowCount = 0, GrowStyle = TableLayoutPanelGrowStyle.AddRows };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Field(string label, Control input)
        {
            panel.Controls.Add(new Label { Text = label, AutoSize = true, Dock = DockStyle.Top });
            input.Dock = DockStyle.Top; input.AccessibleName = label; panel.Controls.Add(input);
        }
        var system = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        system.Items.Add(native);
        if (MayChooseWsl(row)) system.Items.Add("WSL（Linux 子系统）");
        var host = new TextBox { MaxLength = 253 };
        var port = new TextBox { MaxLength = 5, PlaceholderText = "留空使用 SSH 配置，通常为 22" };
        var user = new TextBox { MaxLength = 128, PlaceholderText = "留空使用 SSH 配置" };
        Field("目标系统", system); Field("SSH 主机 / IP", host); Field("SSH 端口", port); Field("系统账号", user);
        var endpoint = new Label { Name = "sshEndpointPreview", AutoSize = true, Dock = DockStyle.Top, UseMnemonic = false, Margin = new Padding(3, 8, 3, 4) };
        var hint = new Label { Name = "sshEndpointHint", AutoSize = true, Dock = DockStyle.Top, UseMnemonic = false };
        panel.Controls.Add(endpoint); panel.Controls.Add(hint);
        panel.ClientSizeChanged += (_, _) => endpoint.MaximumSize = hint.MaximumSize = new Size(
            Math.Max(1, panel.ClientSize.Width - panel.Padding.Horizontal - hint.Margin.Horizontal), 0);
        string reportedPort = row?.WindowsSshPort is > 0 ? row.WindowsSshPort.ToString()! : "";
        var drafts = new Dictionary<bool, (string Host, string Port, string User)>();
        foreach (var preference in history)
            drafts.TryAdd(preference.Wsl, (DirectSshTarget.SameHost(preference.Target.Host, preference.Device) ? device : preference.Target.Host,
                preference.UseReportedPort && row?.WindowsSshPort is not null ? reportedPort : preference.Target.Port?.ToString() ?? "",
                preference.Target.Username));
        var terminal = new Button { Text = "打开终端", AutoSize = true };
        var editor = new Button { Text = "用 VS Code 打开", AutoSize = true };
        bool? displayed = null;
        bool IsWsl() => system.SelectedIndex == 1;
        string TargetLabel() => IsWsl() ? "WSL" : native;
        string EndpointText() => string.IsNullOrWhiteSpace(port.Text)
            ? $"{host.Text.Trim()}（端口由 SSH 配置决定，默认 22）"
            : int.TryParse(port.Text, out int number) && number is > 0 and <= 65535
                ? NetworkUtils.FormatEndpoint(host.Text.Trim(), number) : $"{host.Text.Trim()}（端口无效）";
        void RefreshEndpoint()
        {
            bool sameHost = DirectSshTarget.SameHost(host.Text, device);
            bool needsPort = sameHost && string.IsNullOrWhiteSpace(port.Text);
            string connection = needsPort && (IsWsl() || row?.WindowsSshPort == 0) ? $"{host.Text.Trim()}（请填写映射端口）" :
                needsPort && hasWsl && row?.WindowsSshPort is null ? $"{host.Text.Trim()}（Windows SSH 端口待确认）" : EndpointText();
            endpoint.Text = string.IsNullOrWhiteSpace(host.Text) ? "请填写 SSH 地址" : $"目标 {TargetLabel()} · {connection}";
            hint.Text = IsWsl()
                ? "使用 WSL 可达的地址，或 Windows 地址和已映射到 WSL 的 SSH 端口。具体发行版由该入口决定。"
                : !sameHost ? "使用手动填写的 SSH 入口，实际系统取决于它指向的服务。"
                : row?.WindowsSshPort switch
                {
                    0 => "Windows sshd 只监听本机。可通过中继进入 Windows，或填写已有的外部转发端口。",
                    > 0 when port.Text == reportedPort => "该端口由目标电脑上的 Windows sshd 监听。",
                    > 0 => $"目标电脑上报的 Windows sshd 端口为 {reportedPort}。当前填写的入口需自行确认。",
                    _ when row?.KnownPlatform == RemoteDevicePlatforms.Windows => "设备未上报 Windows sshd 端口。若 22 已转发到 WSL，请填写 Windows 的独立端口。",
                    _ => "使用该地址的 SSH 服务。系统账号和主机指纹由 SSH 客户端验证。"
                };
            try
            {
                ValidateDirectSshSystem(IsWsl(), DirectSshTarget.Create(host.Text, port.Text, user.Text), device, row?.WindowsSshPort, hasWsl);
                terminal.Enabled = editor.Enabled = true;
            }
            catch (ArgumentException error)
            {
                terminal.Enabled = editor.Enabled = false;
                if (!string.IsNullOrWhiteSpace(host.Text)) hint.Text = error.Message;
            }
        }
        host.TextChanged += (_, _) => RefreshEndpoint();
        port.TextChanged += (_, _) => RefreshEndpoint();
        user.TextChanged += (_, _) => RefreshEndpoint();
        system.SelectedIndexChanged += (_, _) =>
        {
            bool wsl = IsWsl();
            if (displayed is bool previous) drafts[previous] = (host.Text, port.Text, user.Text);
            var draft = drafts.GetValueOrDefault(wsl, (Host: device, Port: wsl ? "" : reportedPort, User: ""));
            (host.Text, port.Text, user.Text) = draft;
            displayed = wsl;
            RefreshEndpoint();
        };
        system.SelectedIndex = system.Items.Count > 1 && history.FirstOrDefault()?.Wsl == true ? 1 : 0;
        if (peer is { SupportsSsh: true } online)
        {
            string id = online.DeviceId;
            long generation = _relayConfigurationGeneration;
            var link = new LinkLabel { Name = "sshRelayDestination", AutoSize = true, Dock = DockStyle.Top, UseMnemonic = false };
            var systems = new ContextMenuStrip();
            dialog.Disposed += (_, _) => systems.Dispose();
            void SelectRelayTarget(string systemId)
            {
                if (generation != _relayConfigurationGeneration || !SelectRelaySshTarget(id, systemId))
                { hint.Text = "中继上的目标已变化，请刷新设备后再选择。"; return; }
                dialog.DialogResult = DialogResult.Cancel;
            }
            void UpdateLink()
            {
                link.Visible = !IsWsl() || online.WslDestinations.Count > 0;
                link.Text = IsWsl() && online.WslDestinations.Count == 1
                    ? $"转到公网中继 · {online.WslDestinations[0].Label}"
                    : IsWsl() ? "选择公网中继上的 WSL 发行版…" : $"转到公网中继 · {native}";
            }
            foreach (var destination in online.WslDestinations.Where(s => s.Available))
                systems.Items.Add(destination.Label, null, (_, _) => SelectRelayTarget(destination.Id));
            link.LinkClicked += (_, _) =>
            {
                if (!IsWsl()) SelectRelayTarget(id);
                else if (online.WslDestinations.Count == 1) SelectRelayTarget(online.WslDestinations[0].Id);
                else systems.Show(link, new Point(0, link.Height));
            };
            system.SelectedIndexChanged += (_, _) => UpdateLink();
            UpdateLink();
            panel.Controls.Add(link);
        }
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, WrapContents = true, Padding = new Padding(8) };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        void Launch(bool useEditor)
        {
            try
            {
                var target = DirectSshTarget.Create(host.Text, port.Text, user.Text);
                bool wsl = IsWsl();
                ValidateDirectSshSystem(wsl, target, device, row?.WindowsSshPort, hasWsl);
                DesktopUserLaunch.OpenDirectSsh(target, useEditor);
                var entry = new DirectSshPreference(device.Length == 0 ? target.Host : device, wsl, target)
                { DeviceId = row?.DirectoryDeviceId, UseReportedPort = !wsl && target.Port is > 0 && target.Port == row?.WindowsSshPort && DirectSshTarget.SameHost(target.Host, device) };
                _settings.Viewer.DirectSshTargets = _settings.Viewer.DirectSshTargets.Prepend(entry)
                    .DistinctBy(p => p.Key).Take(50).ToList();
                if (device.Length == 0) _viewerHostBox.Text = target.Host;
                TrySaveSettings();
                UpdateDirectSshButton();
                SetViewerStatus($"已请求{(useEditor ? " VS Code " : "终端")}直连 {TargetLabel()} · {EndpointText()}。", SuccessTextColor);
                dialog.DialogResult = DialogResult.OK;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { hint.Text = ex.Message; }
        }
        terminal.Click += (_, _) => Launch(false); editor.Click += (_, _) => Launch(true);
        actions.Controls.AddRange([terminal, editor, cancel]); ConfigureWrappingDialogActions(actions);
        dialog.Controls.Add(panel); dialog.Controls.Add(actions); dialog.AcceptButton = terminal; dialog.CancelButton = cancel;
        ResponsiveWindowLayout.ConfigureDialog(dialog, new Size(560, 460), new Size(340, 310));
        return dialog;
    }

    private ListViewItem? RelayRow(string deviceId) => _relayDevicesList.Items.Cast<ListViewItem>()
        .FirstOrDefault(item => item.Tag is RelayOnlineDevice device && RemoteDeviceIdentity.Same(device.DeviceId, deviceId));

    private bool SelectRelaySshTarget(string deviceId, string systemId)
    {
        var item = RelayRow(deviceId);
        if (item?.Tag is not RelayOnlineDevice { SupportsSsh: true } device ||
            (systemId != device.DeviceId && !device.WslDestinations.Any(s => s.Id == systemId && s.Available))) return false;
        _tabs.SelectedTab = _relayPage;
        _relayDevicesList.SelectedItems.Clear();
        item.Selected = item.Focused = true;
        item.EnsureVisible();
        SelectedSshSystem(device);
        _sshSystem.SelectedItem = _sshSystem.Items.Cast<SshDestination>().Single(s => s.Id == systemId);
        UpdateRelayActionState();
        _sshTerminalButton.Focus();
        return true;
    }

    internal static void ValidateDirectSshSystem(bool wsl, DirectSshTarget target, string address, int? windowsPort, bool hasWsl = false)
    {
        if (!DirectSshTarget.SameHost(target.Host, address)) return;
        if (wsl && target.Port is null)
            throw new ArgumentException("使用 Windows 地址连接 WSL 时，请填写映射到 WSL 的 SSH 端口。");
        if (wsl && windowsPort is > 0 && target.Port == windowsPort)
            throw new ArgumentException("此端口由 Windows sshd 监听。请选择 Windows 原生，或填写 WSL 的映射端口。");
        if (!wsl && windowsPort == 0 && target.Port is null)
            throw new ArgumentException("Windows sshd 只监听本机。请选择中继中的 Windows 原生，或填写已配置的外部转发端口。");
        if (!wsl && hasWsl && windowsPort is null && target.Port is null)
            throw new ArgumentException("此电脑同时提供 Windows 和 WSL，尚未上报 Windows 直连端口。请填写已知的 Windows SSH 端口，或选择中继中的 Windows 原生。");
    }
}
