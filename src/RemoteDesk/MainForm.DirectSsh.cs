namespace RemoteDesk;

public sealed partial class MainForm
{
    private void OpenDirectSsh()
    {
        string device = _viewerHostBox.Text.Trim();
        var history = _settings.Viewer.DirectSshTargets.Where(p =>
            string.Equals(p.Device, device, StringComparison.OrdinalIgnoreCase)).ToList();
        using var dialog = new Form { Text = "SSH 直连", Font = Font, AutoScaleMode = AutoScaleMode.Dpi,
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
        system.Items.AddRange(["主机系统", "WSL"]);
        var host = new TextBox { MaxLength = 253 };
        var port = new TextBox { MaxLength = 5, PlaceholderText = "留空使用 SSH 配置，通常为 22" };
        var user = new TextBox { MaxLength = 128, PlaceholderText = "留空使用 SSH 配置" };
        Field("目标系统", system); Field("SSH 主机 / IP", host); Field("SSH 端口", port); Field("系统账号", user);
        var hint = new Label { AutoSize = true, Dock = DockStyle.Top, UseMnemonic = false };
        panel.Controls.Add(hint);
        panel.SizeChanged += (_, _) => hint.MaximumSize = new Size(Math.Max(1, panel.ClientSize.Width - panel.Padding.Horizontal), 0);
        system.SelectedIndexChanged += (_, _) =>
        {
            bool wsl = system.SelectedIndex == 1;
            var previous = history.FirstOrDefault(p => p.Wsl == wsl)?.Target;
            host.Text = previous?.Host ?? device; port.Text = previous?.Port?.ToString() ?? ""; user.Text = previous?.Username ?? "";
            hint.Text = wsl ? "填写发行版可达的 IP，或 Windows 地址和已有的 WSL 映射端口。" : "直接连接目标的 SSH 服务。首次连接请核对主机指纹。";
        };
        system.SelectedIndex = history.FirstOrDefault()?.Wsl == true ? 1 : 0;
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, WrapContents = true, Padding = new Padding(8) };
        var terminal = new Button { Text = "打开终端", AutoSize = true };
        var editor = new Button { Text = "用 VS Code 打开", AutoSize = true };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        void Launch(bool useEditor)
        {
            try
            {
                var target = DirectSshTarget.Create(host.Text, port.Text, user.Text);
                bool wsl = system.SelectedIndex == 1;
                if (wsl && target.Port is null && string.Equals(target.Host, device.Trim('[', ']'), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("使用 Windows 地址连接 WSL 时，请填写 WSL 对外可达的 SSH 端口。");
                DesktopUserLaunch.OpenDirectSsh(target, useEditor);
                var entry = new DirectSshPreference(device.Length == 0 ? target.Host : device, wsl, target);
                _settings.Viewer.DirectSshTargets = _settings.Viewer.DirectSshTargets.Prepend(entry)
                    .DistinctBy(p => (p.Device.ToLowerInvariant(), p.Wsl)).Take(50).ToList();
                if (device.Length == 0) _viewerHostBox.Text = target.Host;
                TrySaveSettings();
                SetViewerStatus($"已请求{(useEditor ? " VS Code " : "终端")}直连 {target.Host}。", SuccessTextColor);
                dialog.DialogResult = DialogResult.OK;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { hint.Text = ex.Message; }
        }
        terminal.Click += (_, _) => Launch(false); editor.Click += (_, _) => Launch(true);
        actions.Controls.AddRange([terminal, editor, cancel]); ConfigureWrappingDialogActions(actions);
        dialog.Controls.Add(panel); dialog.Controls.Add(actions); dialog.AcceptButton = terminal; dialog.CancelButton = cancel;
        ResponsiveWindowLayout.ConfigureDialog(dialog, new Size(530, 400), new Size(340, 270));
        dialog.ShowDialog(this);
    }
}
