using System.ComponentModel;
using System.Net.Sockets;

namespace RemoteDesk;

public sealed partial class MainForm
{
    private CheckBox _allowSshBox = null!;
    private NumericUpDown _sshHostPortBox = null!;
    private Button _sshTerminalButton = null!;
    private Button _sshLoginSettingsButton = null!;
    private Button _configureSshAliasButton = null!;
    private Button _manageSshAliasesButton = null!;
    private Button _closeSshTerminalsButton = null!;
    private RelaySshHostOptions _sshHostOptions = new(false, 22, "");
    private readonly HashSet<SshTerminalSession> _sshTerminals = [];
    private CancellationTokenSource? _sshOpening;

    private Control CreateSshHostSettings()
    {
        _allowSshBox = new CheckBox { Text = "允许通过中继连接本机 SSH（默认关闭）", AutoSize = true, Dock = DockStyle.Top };
        ResponsiveWindowLayout.ConfigureWrappedCheckBox(_allowSshBox);
        _sshHostPortBox = CreatePortInput();
        _sshHostPortBox.Dock = DockStyle.Top;
        _sshHostPortBox.Minimum = 1;
        _sshHostPortBox.Value = 22;
        _sshHostPortBox.AccessibleName = "本机 SSH 监听端口";
        var content = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.Controls.Add(_allowSshBox);
        content.Controls.Add(_sshHostPortBox);
        var hint = new Label { AutoSize = true, Dock = DockStyle.Top, UseMnemonic = false,
            Text = "转发至本机 127.0.0.1 的 SSH 服务；须自行安装并启动 OpenSSH Server。\n与桌面控制独立；还需设备密钥和 SSH 账户认证，不自动安装服务或开放端口。" };
        content.Controls.Add(hint);
        content.SizeChanged += (_, _) => hint.MaximumSize = new Size(Math.Max(1, content.ClientSize.Width), 0);
        _allowSshBox.CheckedChanged += async (_, _) => await SaveSshHostSettingsAsync();
        _sshHostPortBox.ValueChanged += async (_, _) => await SaveSshHostSettingsAsync();
        return content;
    }

    private void AddSshDeviceActions(FlowLayoutPanel actions)
    {
        _sshTerminalButton = CreateSecondaryButton("SSH 终端");
        _sshLoginSettingsButton = CreateSecondaryButton("SSH 登录设置…");
        _configureSshAliasButton = CreateSecondaryButton("配置终端 SSH");
        _manageSshAliasesButton = CreateSecondaryButton("管理终端别名");
        _closeSshTerminalsButton = CreateSecondaryButton("关闭 SSH 终端");
        _sshTerminalButton.Click += async (_, _) => await OpenSshTerminalAsync();
        _sshLoginSettingsButton.Click += async (_, _) => await OpenSshTerminalCoreAsync(configure: true, forceNew: true);
        var terminalMenu = new ContextMenuStrip();
        terminalMenu.Items.Add("新建终端（保留已有连接）", null, async (_, _) => await OpenSshTerminalCoreAsync(forceNew: true));
        _sshTerminalButton.ContextMenuStrip = terminalMenu;
        _sshTerminalButton.Disposed += (_, _) => terminalMenu.Dispose();
        _configureSshAliasButton.Click += async (_, _) => await ConfigureSshAliasAsync();
        _manageSshAliasesButton.Click += (_, _) => ManageSshAliases();
        _closeSshTerminalsButton.Click += (_, _) => CloseSshTerminals();
        actions.Controls.Add(_sshTerminalButton);
        actions.Controls.Add(_sshLoginSettingsButton);
        actions.Controls.Add(_configureSshAliasButton);
        actions.Controls.Add(_manageSshAliasesButton);
        actions.Controls.Add(_closeSshTerminalsButton);
    }

    private void ApplySshSettingsToUi()
    {
        _allowSshBox.Checked = _settings.Host.AllowSsh;
        _sshHostPortBox.Value = Math.Clamp(_settings.Host.SshPort, 1, 65535);
        PublishSshHostOptions();
    }

    private void PublishSshHostOptions()
    {
        Volatile.Write(ref _sshHostOptions, new RelaySshHostOptions(
            _settings.Host.AllowSsh, _settings.Host.SshPort, _hostPasswordBox.Text));
        _relayHostConnector.RequestAddressRefresh();
    }

    internal bool TryGetCurrentRelayHostRegistration(RelayConnectionOptions options, long generation, out int desktopPort)
    {
        desktopPort = 0;
        // Route optimization may yield for several seconds. Re-read intent and
        // service availability, never revive a withdrawn/changed registration.
        if (_isClosing || IsDisposed || generation != _relayConfigurationGeneration ||
            !_relayRegisterHostBox.Checked || !IsRelayConfigured() ||
            CreateRelayOptions(_settings.Relay.DeviceId) != options) return false;
        desktopPort = _hostServer.IsRunning ? _hostServer.ListeningPort : 0;
        return desktopPort > 0 || Volatile.Read(ref _sshHostOptions).IsAvailable;
    }

    private async Task SaveSshHostSettingsAsync()
    {
        if (_applyingSettings || _isClosing || _layoutPreview) return;
        bool oldEnabled = _settings.Host.AllowSsh;
        int oldPort = _settings.Host.SshPort;
        _settings.Host.AllowSsh = _allowSshBox.Checked;
        _settings.Host.SshPort = (int)_sshHostPortBox.Value;
        if (!TrySaveSettings())
        {
            _settings.Host.AllowSsh = oldEnabled;
            _settings.Host.SshPort = oldPort;
            _applyingSettings = true;
            try { ApplySshSettingsToUi(); } finally { _applyingSettings = false; }
            return;
        }
        PublishSshHostOptions();
        await ApplyRelayHostRegistrationAsync();
        UpdateSshActionState();
    }

    internal static string? SshConnectionBlockReason(RelayOnlineDevice? selected) => selected is null
        ? "请先选择在线设备。" : !selected.SupportsSsh
            ? "此设备未启用 SSH，或其中继 / 客户端版本尚不支持。" : null;

    private void UpdateSshActionState()
    {
        if (_sshTerminalButton is null || _sshTerminalButton.IsDisposed) return;
        string? reason = SshConnectionBlockReason(GetSelectedRelayDevice());
        var selected = GetSelectedRelayDevice();
        string? alias = null;
        if (selected is not null && IsRelayConfigured())
        {
            try { alias = SshTerminalLauncher.HostKeyAlias(_settings.Relay.TlsCertificateSha256!, selected.DeviceId); }
            catch (ArgumentException error) { reason = error.Message; }
        }
        bool existing = alias is not null && _sshTerminals.Any(terminal => terminal.IsActive && terminal.HostKeyAlias == alias);
        _sshTerminalButton.Text = _sshOpening is not null ? "取消 SSH 连接" : existing ? "唤回 SSH 终端" : "一键 SSH 终端";
        _sshTerminalButton.Enabled = _sshOpening is not null || (!_isClosing && IsRelayConfigured() && !_relayOperationInProgress && reason is null);
        SetToolTip(_sshTerminalButton, _sshOpening is not null
            ? "取消正在进行的 SSH 连接或别名验证，不影响远程桌面和已确认的文件传输。"
            : reason ?? "首次填写 SSH 登录设置，之后一键连接；已有会话优先唤回，最小化不断线。右键可新建另一终端。首次指纹与密码仍由系统 SSH 核验。");
        _sshLoginSettingsButton.Enabled = _sshOpening is null && !_isClosing && IsRelayConfigured() && !_relayOperationInProgress && reason is null;
        SetToolTip(_sshLoginSettingsButton, "修改此设备的 SSH 用户名、设备密钥或私钥路径后连接；不打断已有 SSH。SSH 密码不保存。");
        _configureSshAliasButton.Enabled = _sshOpening is null && !_isClosing && IsRelayConfigured() && !_relayOperationInProgress && reason is null;
        SetToolTip(_configureSshAliasButton, reason ?? "为此设备配置标准 ssh 别名；此后可在终端直接输入 ssh 别名，不必先打开远程桌面。");
        _manageSshAliasesButton.Enabled = _sshOpening is null && !_isClosing;
        SetToolTip(_manageSshAliasesButton, "查看 / 移除当前用户已配置的终端别名；离线也可使用，不删除私钥、主机指纹或设备密钥。");
        _closeSshTerminalsButton.Text = _sshTerminals.Count == 0 ? "关闭 SSH 终端" : $"关闭 SSH 终端（{_sshTerminals.Count}）";
        _closeSshTerminalsButton.Enabled = _sshTerminals.Count > 0;
        _allowSshBox.Enabled = !_relayOperationInProgress;
        _sshHostPortBox.Enabled = _allowSshBox.Checked && !_relayOperationInProgress;
        SetToolTip(_relayLogoutButton, _sshOpening is not null ? "请先取消 SSH 连接，再退出服务器。"
            : _sshTerminals.Count > 0 ? "请先关闭 SSH 终端，再退出服务器。" : "退出当前中继服务器，保留设备记录和设备密钥。");
        // Changing the relay identity must not leave hidden tunnels on an old login.
        // The adjacent close action releases only SSH sessions, never desktop input.
        if (_sshOpening is not null || _sshTerminals.Count > 0)
        {
            _relayConfigureButton.Enabled = _relayLoginButton.Enabled = _relayLogoutButton.Enabled = false;
        }
    }

    private string GetSshDeviceKey(RelayOnlineDevice selected, RelayConnectionOptions route)
    {
        string selection = RelayDeviceKeys.Scope(route) + ":" + selected.DeviceId;
        // A desktop session may freeze its key field while selection changes.
        // Never reuse that other device's draft for an independent SSH target.
        if (_relayKeySelection == selection && !string.IsNullOrEmpty(_relayViewerPasswordBox.Text)) return _relayViewerPasswordBox.Text;
        if (RelayDeviceSelectionPolicy.IsLocalDevice(selected, _settings.Relay.DeviceId)) return _hostPasswordBox.Text;
        return AppSettingsService.UnprotectSecret(RelayDeviceKeys.Find(_settings.Relay.DeviceKeys, route));
    }

    private Task OpenSshTerminalAsync() => OpenSshTerminalCoreAsync();

    private async Task OpenSshTerminalCoreAsync(bool configure = false, bool forceNew = false)
    {
        if (_sshOpening is not null) { _sshOpening.Cancel(); return; }
        RelayOnlineDevice? selected = GetSelectedRelayDevice();
        if (_isClosing || _relayOperationInProgress || !IsRelayConfigured() || SshConnectionBlockReason(selected) is not null) return;
        if (!File.Exists(SshTerminalLauncher.ExecutablePath))
        {
            MessageBox.Show(this, "未找到 Windows OpenSSH 客户端。请在 Windows 可选功能中安装“OpenSSH 客户端”，再重试。", "SSH 终端", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        RelayConnectionOptions route = CreateRelayOptions(selected!.DeviceId);
        string alias = SshTerminalLauncher.HostKeyAlias(route.TlsCertificateSha256, route.DeviceId);
        var existing = _sshTerminals.FirstOrDefault(terminal => terminal.HostKeyAlias == alias && terminal.IsActive);
        if (!forceNew && existing is not null)
        {
            bool recalled = await existing.RecallAsync();
            if (!_isClosing && !IsDisposed) SetRelayStatus(recalled ? "已唤回原 SSH 终端；连接和运行中的命令保持不变。" :
                "SSH 会话仍保留，当前系统未能唤回窗口，请从任务栏切回；右键“SSH 终端”可另开一个。", MutedTextColor);
            return;
        }
        if (_sshTerminals.Count(terminal => terminal.IsActive) >= 8)
        { SetRelayStatus("最多同时打开 8 个 SSH 终端，请先关闭不再使用的会话。", MutedTextColor); return; }
        long generation = _relayConfigurationGeneration;
        var profile = _settings.Viewer.SshProfiles.FirstOrDefault(item => item.HostKeyAlias == alias);
        string key = GetSshDeviceKey(selected, route), user = profile?.Username ?? "", identity = profile?.IdentityFile ?? "";
        if (configure || profile?.CanQuickConnect(alias, key) != true)
        {
            using var dialog = CreateSshConnectionDialog(selected.MachineName, Font, key, profile,
                out TextBox username, out TextBox deviceKey, out TextBox identityFile);
            if (dialog.ShowDialog(this) != DialogResult.OK || _isClosing) return;
            user = username.Text.Trim(); key = deviceKey.Text; identity = identityFile.Text.Trim();
        }
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(_relayOperationCancellation.Token);
        _sshOpening = pending;
        UpdateRelayActionState();
        SshRelayConnection? connection = null;
        SshLocalForwarder? forwarder = null;
        try
        {
            SetRelayStatus($"正在连接 {selected.MachineName} 的 SSH...", MutedTextColor);
            connection = await SshRelayClient.OpenAsync(route, key, pending.Token);
            if (_isClosing || IsDisposed || generation != _relayConfigurationGeneration) return;
            pending.Token.ThrowIfCancellationRequested();
            string? protectedKey = AppSettingsService.ProtectSecret(key);
            if (protectedKey is not null)
            {
                _settings.Relay.DeviceKeys = RelayDeviceKeys.Remember(_settings.Relay.DeviceKeys, route, protectedKey);
                TrySaveSettings();
            }
            string hosts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteDesk", "ssh", "known_hosts");
            Directory.CreateDirectory(Path.GetDirectoryName(hosts)!);
            forwarder = new SshLocalForwarder(connection.Stream, connection);
            connection = null;
            var start = SshTerminalLauncher.CreateStartInfo(SshTerminalLauncher.ExecutablePath, forwarder.Port, user,
                alias, hosts, identity);
            string? windowId = null;
            if (File.Exists(SshTerminalLauncher.WindowsTerminalPath))
            {
                string candidate = "remotedesk-ssh-" + Guid.NewGuid().ToString("N");
                var windowStart = SshTerminalLauncher.CreateWindowStartInfo(start, SshTerminalLauncher.WindowsTerminalPath, candidate);
                if (windowStart is not null) { start = windowStart; windowId = candidate; }
            }
            var terminal = new SshTerminalSession(forwarder, start, alias, windowId);
            forwarder = null;
            _sshTerminals.Add(terminal);
            _settings.Viewer.SshProfiles = SshTerminalProfile.Normalize(
                new[] { new SshTerminalProfile(alias, user, string.IsNullOrEmpty(identity) ? null : Path.GetFullPath(identity)) }
                    .Concat(_settings.Viewer.SshProfiles));
            TrySaveSettings();
            _ = ObserveSshTerminalAsync(terminal, selected.MachineName);
            SetRelayStatus($"已打开 {selected.MachineName} 的 SSH 终端；最小化保持连接，再点可唤回。首次指纹 / SSH 密码仍在终端确认。", SuccessTextColor);
        }
        catch (OperationCanceledException) { if (!_isClosing && !IsDisposed) SetRelayStatus("SSH 连接已取消。", MutedTextColor); }
        catch (Exception error) when (error is IOException or SocketException or InvalidOperationException or
            UnauthorizedAccessException or ArgumentException or Win32Exception or TimeoutException or System.Security.Authentication.AuthenticationException)
        { if (!_isClosing && !IsDisposed) SetRelayStatus("SSH 连接失败：" + error.Message, DangerColor); }
        finally
        {
            connection?.Dispose(); forwarder?.Dispose();
            if (ReferenceEquals(_sshOpening, pending)) _sshOpening = null;
            if (!_isClosing && !IsDisposed) UpdateRelayActionState();
        }
    }

    private async Task ObserveSshTerminalAsync(SshTerminalSession terminal, string name)
    {
        string? errorText = null;
        try { await terminal.Completion; }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or
            ObjectDisposedException or InvalidOperationException or TimeoutException or Win32Exception) { errorText = error.Message; }
        finally
        {
            terminal.Dispose(); _sshTerminals.Remove(terminal);
            if (!_isClosing && !IsDisposed)
            {
                SetRelayStatus(errorText is null ? $"{name} 的 SSH 终端已关闭。" : "SSH 终端结束：" + errorText,
                    errorText is null ? MutedTextColor : DangerColor);
                UpdateRelayActionState();
            }
        }
    }

    private async Task ConfigureSshAliasAsync()
    {
        RelayOnlineDevice? selected = GetSelectedRelayDevice();
        if (_sshOpening is not null || _isClosing || _relayOperationInProgress || !IsRelayConfigured() || SshConnectionBlockReason(selected) is not null) return;
        CancellationTokenSource? pending = null;
        try
        {
            if (!File.Exists(SshTerminalLauncher.ExecutablePath))
                throw new InvalidOperationException("请先在 Windows 可选功能中安装 OpenSSH 客户端。");
            RelayConnectionOptions route = CreateRelayOptions(selected!.DeviceId);
            long generation = _relayConfigurationGeneration;
            string scope = SshCommandProfiles.ComputeRelayScope(route);
            IReadOnlyList<SshCommandProfile> profiles = SshCommandProfiles.ReadAll();
            SshCommandProfile? previous = profiles.FirstOrDefault(item => item.DeviceId == route.DeviceId && item.RelayScope == scope);
            string hostAlias = SshTerminalLauncher.HostKeyAlias(route.TlsCertificateSha256, route.DeviceId);
            var preferences = previous is null ? _settings.Viewer.SshProfiles.FirstOrDefault(item => item.HostKeyAlias == hostAlias)
                : new SshTerminalProfile(hostAlias, previous.Username, previous.IdentityFile);
            string suggestedAlias = previous?.Alias ?? (SshAliasConfig.IsValidAlias(selected.MachineName) ? selected.MachineName.ToLowerInvariant() : "rd-" + route.DeviceId[..8]);
            using var dialog = CreateSshAliasDialog(selected.MachineName, Font, GetSshDeviceKey(selected, route), preferences, suggestedAlias,
                out TextBox username, out TextBox deviceKey, out TextBox identityFile, out TextBox commandAlias);
            if (dialog.ShowDialog(this) != DialogResult.OK || _isClosing) return;
            SshCommandProfile profile = new SshCommandProfile(previous?.Id ?? Guid.NewGuid(), commandAlias.Text.Trim(), route.DeviceId,
                scope, route.TlsCertificateSha256, username.Text.Trim(), string.IsNullOrWhiteSpace(identityFile.Text) ? null : Path.GetFullPath(identityFile.Text.Trim())).Validate();
            if (profiles.Any(item => item.Id != profile.Id && string.Equals(item.Alias, profile.Alias, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("此终端 SSH 别名已存在，请换一个别名；不会替换其他设备。");
            string helper = SshProxyDeployment.EnsureInstalled();
            pending = CancellationTokenSource.CreateLinkedTokenSource(_relayOperationCancellation.Token);
            _sshOpening = pending; UpdateRelayActionState();
            SetRelayStatus($"正在验证 {selected.MachineName} 的 SSH 设备密钥...", MutedTextColor);
            using (SshRelayConnection connection = await SshRelayClient.OpenAsync(route, deviceKey.Text, pending.Token))
            {
                if (_isClosing || IsDisposed || generation != _relayConfigurationGeneration) return;
                pending.Token.ThrowIfCancellationRequested();
                string protectedKey = AppSettingsService.ProtectSecret(deviceKey.Text) ?? throw new IOException("设备密钥无法安全保存，未配置 SSH 别名。");
                var oldKeys = _settings.Relay.DeviceKeys;
                _settings.Relay.DeviceKeys = RelayDeviceKeys.Remember(oldKeys, route, protectedKey);
                if (!TrySaveSettings())
                {
                    _settings.Relay.DeviceKeys = oldKeys;
                    throw new IOException("设备密钥未保存，未配置 SSH 别名。");
                }
            }
            string config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
            SshAliasConfigResult result;
            SshCommandProfiles.Save(profile);
            try { result = SshAliasConfig.Install(profile, helper, config, SshCommandProfiles.ManagedConfigPath, SshCommandProfiles.KnownHostsPath); }
            catch (Exception failure)
            {
                try
                {
                    if (previous is null) SshCommandProfiles.Remove(profile.Id);
                    else SshCommandProfiles.Save(previous);
                }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException or ArgumentException)
                { throw new IOException("SSH 配置未完成，别名元数据回滚也失败；请检查权限后重新配置。原错误：" + failure.Message, rollback); }
                throw;
            }
            SetRelayStatus($"终端 SSH 已配置：ssh {profile.Alias}", SuccessTextColor);
            MessageBox.Show(this, $"现在可在 PowerShell / Windows Terminal 中运行：\n\nssh {profile.Alias}\n\n" +
                "首次连接请核对 SSH 主机指纹。SSH 密码由系统终端提示，不保存在 RemoteDesk；设备密钥仍使用现有加密设置。\n\n" +
                "配置：" + config + (result.BackupPath is null ? "" : "\n原配置备份：" + result.BackupPath),
                "终端 SSH 已配置", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) { if (!_isClosing && !IsDisposed) SetRelayStatus("终端 SSH 配置已取消。", MutedTextColor); }
        catch (Exception error) when (error is IOException or SocketException or InvalidOperationException or UnauthorizedAccessException or
            ArgumentException or Win32Exception or TimeoutException or System.Security.Authentication.AuthenticationException)
        {
            if (!_isClosing && !IsDisposed)
            {
                SetRelayStatus("终端 SSH 配置失败：" + error.Message, DangerColor);
                MessageBox.Show(this, error.Message, "终端 SSH 未配置", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        finally
        {
            if (ReferenceEquals(_sshOpening, pending)) _sshOpening = null;
            pending?.Dispose();
            if (!_isClosing && !IsDisposed) UpdateRelayActionState();
        }
    }

    private void CloseSshTerminals()
    {
        _sshOpening?.Cancel();
        foreach (SshTerminalSession terminal in _sshTerminals.ToArray()) terminal.Dispose();
        _sshTerminals.Clear();
        if (!_isClosing && !IsDisposed) UpdateRelayActionState();
    }

    private void ManageSshAliases()
    {
        if (_isClosing || _sshOpening is not null) return;
        try
        {
            IReadOnlyList<SshCommandProfile> profiles = SshCommandProfiles.ReadAll();
            if (profiles.Count == 0)
            {
                MessageBox.Show(this, "当前用户还没有配置终端 SSH 别名。请选择支持 SSH 的在线设备，点击“配置终端 SSH”。", "终端 SSH 别名", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using var dialog = CreateSshAliasManagerDialog(profiles, Font, out ComboBox aliases);
            if (dialog.ShowDialog(this) != DialogResult.OK || aliases.SelectedItem is not SshAliasChoice selected) return;
            if (MessageBox.Show(this, $"移除终端别名“{selected.Profile.Alias}”？\n\n只移除 RemoteDesk 管理的 SSH 配置和路由元数据，不删除 SSH 私钥、主机指纹或设备密钥，也不关闭已连接的终端。",
                "移除终端 SSH 别名", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            string config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
            SshAliasConfig.Remove(selected.Profile, "", config, SshCommandProfiles.ManagedConfigPath, SshCommandProfiles.KnownHostsPath);
            try { SshCommandProfiles.Remove(selected.Profile.Id); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception)
            { throw new IOException("SSH 别名配置已移除，但路由元数据未能清理；请修复权限后在此处再次移除。", error); }
            SetRelayStatus($"已移除终端 SSH 别名 {selected.Profile.Alias}。", SuccessTextColor);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Win32Exception)
        {
            SetRelayStatus("终端别名管理失败：" + error.Message, DangerColor);
            MessageBox.Show(this, error.Message, "终端 SSH 别名", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private sealed record SshAliasChoice(SshCommandProfile Profile)
    { public override string ToString() => Profile.Alias + "  —  " + Profile.Username + "  ·  " + Profile.DeviceId[..8]; }

    internal static Form CreateSshAliasManagerDialog(IReadOnlyList<SshCommandProfile> profiles, Font font, out ComboBox aliases)
    {
        var dialog = new Form { Text = "管理终端 SSH 别名", Font = font, AutoScaleMode = AutoScaleMode.Dpi,
            StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16), ColumnCount = 1, RowCount = 4 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var hint = new Label { Text = "选择当前用户已保存的别名；无需目标在线或中继登录。\n移除仅影响以后使用该别名连接，不会关闭现有终端。", AutoSize = true, Dock = DockStyle.Top };
        panel.Controls.Add(hint); panel.SizeChanged += (_, _) => hint.MaximumSize = new Size(Math.Max(1, panel.ClientSize.Width - panel.Padding.Horizontal), 0);
        var choices = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "已保存的终端 SSH 别名" };
        var details = new TextBox { Name = "sshAliasDetails", AccessibleName = "所选终端别名详情", Dock = DockStyle.Top,
            Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical, Height = 120 };
        // The compact picker may truncate a long alias or username. Keep the
        // complete selected identity inspectable/copyable before removal.
        choices.SelectedIndexChanged += (_, _) => details.Text = choices.SelectedItem is SshAliasChoice selected
            ? "终端命令：ssh " + selected.Profile.Alias + Environment.NewLine + "SSH 用户名：" + selected.Profile.Username +
                Environment.NewLine + "设备 ID：" + selected.Profile.DeviceId : "";
        foreach (SshCommandProfile profile in profiles.OrderBy(item => item.Alias, StringComparer.OrdinalIgnoreCase)) choices.Items.Add(new SshAliasChoice(profile));
        if (choices.Items.Count > 0) choices.SelectedIndex = 0;
        panel.Controls.Add(choices); aliases = choices;
        panel.Controls.Add(details);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        var remove = new Button { Text = "移除所选别名…", AutoSize = true, DialogResult = DialogResult.OK, Enabled = choices.Items.Count > 0 };
        var cancel = new Button { Text = "关闭", AutoSize = true, DialogResult = DialogResult.Cancel };
        actions.Controls.Add(remove); actions.Controls.Add(cancel); ConfigureWrappingDialogActions(actions); panel.Controls.Add(actions);
        dialog.Controls.Add(panel); dialog.AcceptButton = remove; dialog.CancelButton = cancel;
        ResponsiveWindowLayout.ConfigureDialog(dialog, new Size(560, 380), new Size(360, 200));
        return dialog;
    }

    internal static Form CreateSshConnectionDialog(string name, Font font, string savedDeviceKey, out TextBox username, out TextBox deviceKey)
        => CreateSshConnectionDialog(name, font, savedDeviceKey, null, out username, out deviceKey, out _);

    internal static Form CreateSshConnectionDialog(string name, Font font, string savedDeviceKey, SshTerminalProfile? profile,
        out TextBox username, out TextBox deviceKey, out TextBox identityFile)
        => CreateSshDialog(name, font, savedDeviceKey, profile, null, out username, out deviceKey, out identityFile, out _);

    internal static Form CreateSshAliasDialog(string name, Font font, string savedDeviceKey, SshTerminalProfile? profile, string suggestedAlias,
        out TextBox username, out TextBox deviceKey, out TextBox identityFile, out TextBox alias)
        => CreateSshDialog(name, font, savedDeviceKey, profile, suggestedAlias, out username, out deviceKey, out identityFile, out alias);

    private static Form CreateSshDialog(string name, Font font, string savedDeviceKey, SshTerminalProfile? profile, string? suggestedAlias,
        out TextBox username, out TextBox deviceKey, out TextBox identityFile, out TextBox alias)
    {
        bool configureAlias = suggestedAlias is not null;
        var dialog = new Form { Text = (configureAlias ? "配置终端 SSH · " : "SSH · ") + name, Font = font, AutoScaleMode = AutoScaleMode.Dpi,
            StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16), ColumnCount = 1, RowCount = 8 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var explanation = new Label { AutoSize = true, Dock = DockStyle.Top, UseMnemonic = false,
            Text = configureAlias ? "配置后可在系统终端直接输入 ssh 别名；设备密钥验证成功后，会备份并修改当前用户的 .ssh/config。遇到已有别名冲突、Include 或 Match 时不会覆盖。\nSSH 密码不保存；私钥留空使用系统默认密钥 / agent。首次连接请核对主机指纹。" :
                "首次设置后即可一键打开；再次点击会唤回已有终端。最小化保持连接，退出 RemoteDesk 或断开 SSH 会结束连接。\n用户名与私钥路径会记住，SSH 密码不保存；私钥留空使用系统默认密钥 / agent。首次连接仍需核对主机指纹。" };
        panel.Controls.Add(explanation);
        panel.SizeChanged += (_, _) => explanation.MaximumSize = new Size(Math.Max(1, panel.ClientSize.Width - panel.Padding.Horizontal), 0);
        var aliasInput = new TextBox { Dock = DockStyle.Top, MaxLength = 64, Text = suggestedAlias ?? "", PlaceholderText = "例如 5090、lab-pc" };
        alias = aliasInput;
        if (configureAlias)
        {
            panel.RowCount += 2;
            panel.Controls.Add(new Label { Text = "终端别名（字母 / 数字 / - / _，以字母或数字开头）", AutoSize = true, Dock = DockStyle.Top });
            panel.Controls.Add(aliasInput);
        }
        else dialog.Disposed += (_, _) => aliasInput.Dispose();
        panel.Controls.Add(new Label { Text = "SSH 用户名", AutoSize = true, Dock = DockStyle.Top });
        var user = new TextBox { Dock = DockStyle.Top, MaxLength = 128, PlaceholderText = "例如 zzy 或 DOMAIN\\user", Text = profile?.Username ?? "" };
        panel.Controls.Add(user); username = user;
        panel.Controls.Add(new Label { Text = "这台设备的 RemoteDesk 密钥", AutoSize = true, Dock = DockStyle.Top });
        var key = new TextBox { Dock = DockStyle.Top, MaxLength = 4096, UseSystemPasswordChar = true, Text = savedDeviceKey };
        panel.Controls.Add(key); deviceKey = key;
        panel.Controls.Add(new Label { Text = "可选 SSH 私钥文件（OpenSSH 格式）", AutoSize = true, Dock = DockStyle.Top });
        var identityRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        identityRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        identityRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var identity = new TextBox { Dock = DockStyle.Top, Text = profile?.IdentityFile ?? "", PlaceholderText = "留空使用系统默认密钥 / agent", MaxLength = 32767 };
        var browse = new Button { Text = "选择…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Title = "选择 SSH 私钥（不会上传或保存内容）", CheckFileExists = true, Multiselect = false };
            if (picker.ShowDialog(dialog) == DialogResult.OK) identity.Text = picker.FileName;
        };
        identityRow.Controls.Add(identity, 0, 0); identityRow.Controls.Add(browse, 1, 0);
        panel.Controls.Add(identityRow); identityFile = identity;
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        var connect = new Button { Text = configureAlias ? "验证并配置别名" : "打开 SSH 终端", AutoSize = true };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        connect.Click += (_, _) =>
        {
            if (configureAlias && !SshAliasConfig.IsValidAlias(aliasInput.Text.Trim())) { aliasInput.Focus(); return; }
            if (!SshTerminalLauncher.IsValidUsername(user.Text.Trim())) { user.Focus(); return; }
            if (string.IsNullOrWhiteSpace(key.Text)) { key.Focus(); return; }
            if (!string.IsNullOrWhiteSpace(identity.Text) && !File.Exists(identity.Text.Trim()))
            {
                MessageBox.Show(dialog, "所选私钥文件不存在，请重新选择，或留空使用默认密钥 / agent。", "SSH 私钥", MessageBoxButtons.OK, MessageBoxIcon.Information);
                identity.Focus(); return;
            }
            dialog.DialogResult = DialogResult.OK;
        };
        actions.Controls.Add(connect); actions.Controls.Add(cancel); ConfigureWrappingDialogActions(actions); panel.Controls.Add(actions);
        dialog.Controls.Add(panel); dialog.AcceptButton = connect; dialog.CancelButton = cancel; dialog.Shown += (_, _) => { if (configureAlias) aliasInput.Focus(); else user.Focus(); };
        ResponsiveWindowLayout.ConfigureDialog(dialog, new Size(560, configureAlias ? 580 : 480), new Size(360, 300));
        return dialog;
    }
}
