namespace RemoteDesk;

internal sealed class RelaySetupDialog : Form
{
    private readonly TextBox _serverBox;
    private readonly NumericUpDown _sshPortBox;
    private readonly TextBox _usernameBox;
    private readonly TextBox _passwordBox;
    private readonly NumericUpDown _relayPortBox;

    public RelaySetupDialog(RelaySettings settings, bool loginOnly = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Text = loginOnly ? "登录公网服务器" : "部署 / 更新服务器";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(560, 360);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font(
            "Microsoft YaHei UI",
            9.25F,
            FontStyle.Regular,
            GraphicsUnit.Point);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 8,
            Padding = new Padding(20),
            BackColor = Color.White
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _serverBox = CreateTextBox(
            settings.ServerAddress ?? string.Empty,
            "relay.example.com 或公网 IP");
        _sshPortBox = CreatePortBox(settings.SshPort, 22);
        _usernameBox = CreateTextBox(
            settings.AdminUsername ?? "root",
            "root 或有 sudo 权限的账号");
        _passwordBox = CreateTextBox(string.Empty, "本次登录使用，不保存");
        _passwordBox.UseSystemPasswordChar = true;
        _relayPortBox = CreatePortBox(
            settings.RelayPort,
            RelaySettings.DefaultRelayPort);

        AddRow(root, 0, "公网服务器", _serverBox);
        AddRow(root, 1, "SSH 端口", _sshPortBox);
        AddRow(root, 2, "管理员账号", _usernameBox);
        AddRow(root, 3, "管理员密码", _passwordBox);
        if (!loginOnly) AddRow(root, 4, "中继端口", _relayPortBox);

        var note = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ForeColor = Color.FromArgb(71, 85, 105),
            Text = loginOnly
                ? "填写服务器 root / 管理员密码，不是设备密钥。登录后自动获取配置，不保存管理员密码，也不更新或重启服务器。\n" +
                  "之后自动连接；服务器身份变化时会停止登录。"
                : "通过 SSH 安装或更新中继服务，并放行服务器防火墙；云安全组仍需放行中继 TCP 端口。\n" +
                  "root / 管理员密码不保存。首次连接记录服务器身份，身份变化时停止部署。",
            Margin = new Padding(0, 8, 0, 10)
        };
        root.Controls.Add(note, 0, 5);
        root.SetColumnSpan(note, 2);

        var showPassword = new CheckBox
        {
            Text = "显示管理员密码",
            AutoSize = true,
            ForeColor = Color.FromArgb(71, 85, 105),
            Margin = new Padding(0)
        };
        showPassword.CheckedChanged += (_, _) =>
            _passwordBox.UseSystemPasswordChar = !showPassword.Checked;
        root.Controls.Add(showPassword, 1, 6);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 12, 0, 0)
        };
        var saveButton = new Button
        {
            Text = loginOnly ? "登录服务器" : "部署 / 更新并保存",
            AutoSize = true,
            MinimumSize = new Size(146, 36),
            DialogResult = DialogResult.None
        };
        var cancelButton = new Button
        {
            Text = "取消",
            AutoSize = true,
            MinimumSize = new Size(88, 36),
            DialogResult = DialogResult.Cancel
        };
        saveButton.Click += (_, _) => ValidateAndAccept();
        actions.Controls.Add(saveButton);
        actions.Controls.Add(cancelButton);
        root.Controls.Add(actions, 0, 7);
        root.SetColumnSpan(actions, 2);
        Controls.Add(root);
        AcceptButton = saveButton;
        CancelButton = cancelButton;
        ResponsiveWindowLayout.ConfigureDialog(this, new Size(600, 460), new Size(380, 260));
    }

    public RelayProvisionRequest CreateRequest(
        string? expectedSshHostKeySha256) =>
        new(
            _serverBox.Text.Trim(),
            (int)_sshPortBox.Value,
            _usernameBox.Text.Trim(),
            _passwordBox.Text,
            (int)_relayPortBox.Value,
            expectedSshHostKeySha256);

    private void ValidateAndAccept()
    {
        if (string.IsNullOrWhiteSpace(_serverBox.Text))
        {
            ShowWarning("请输入公网 Linux 服务器地址。", _serverBox);
            return;
        }

        if (string.IsNullOrWhiteSpace(_usernameBox.Text))
        {
            ShowWarning("请输入管理员账号。", _usernameBox);
            return;
        }

        if (string.IsNullOrEmpty(_passwordBox.Text))
        {
            ShowWarning("请输入服务器 root / 管理员密码，仅用于本次 SSH 登录；不是设备密钥。", _passwordBox);
            return;
        }

        if (_passwordBox.Text.IndexOfAny(['\r', '\n']) >= 0)
        {
            ShowWarning("管理员密码不能包含换行符。", _passwordBox);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private void ShowWarning(string message, Control focus)
    {
        MessageBox.Show(
            this,
            message,
            "配置中继",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
        focus.Focus();
    }

    private static TextBox CreateTextBox(
        string text,
        string placeholder) =>
        new()
        {
            Dock = DockStyle.Fill,
            Text = text,
            PlaceholderText = placeholder,
            Margin = new Padding(0, 4, 0, 4)
        };

    private static NumericUpDown CreatePortBox(
        int value,
        int fallback) =>
        new()
        {
            Minimum = 1,
            Maximum = 65535,
            Value = value is > 0 and <= 65535 ? value : fallback,
            Width = 120,
            Margin = new Padding(0, 4, 0, 4)
        };

    private static void AddRow(
        TableLayoutPanel root,
        int row,
        string label,
        Control control)
    {
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            ForeColor = Color.FromArgb(71, 85, 105),
            Margin = new Padding(0, 10, 8, 0)
        }, 0, row);
        root.Controls.Add(control, 1, row);
    }
}
