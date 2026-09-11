namespace RemoteDesk;

public sealed partial class MainForm
{
    private Action? _refreshPermissionStatus;

    private Control BuildHostPermissionsPanel()
    {
        var panel = CreateSection("被控权限设置", "按需要开启；普通桌面控制无需全部安装。", sizeToContent: true);
        Label admin = CreateMutedLabel("");
        Label startup = CreateMutedLabel("");
        Label secure = CreateMutedLabel("");
        static FlowLayoutPanel Row(Label state, Button action)
        {
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            state.AutoSize = true; state.Margin = new Padding(0, 12, 12, 4);
            row.Controls.Add(state); row.Controls.Add(action); return row;
        }
        AddSettingRow(panel, 0, "控制管理员窗口", Row(admin, _restartAsAdministratorButton));
        AddSettingRow(panel, 1, "登录后自动可控", Row(startup, _persistentStartupButton));
        AddSettingRow(panel, 2, "控制锁屏 / UAC", Row(secure, _secureDesktopButton));
        var note = CreateMutedLabel("第一项会重启本机应用；后两项需要一次系统管理员授权。\n锁屏控制适用于当前用户已登录后的锁屏，不支持开机首次登录前接管或 Ctrl+Alt+Del。");
        note.AutoSize = true; note.MaximumSize = new Size(700, 0);
        AddSettingRow(panel, 3, "使用范围", note);
        _refreshPermissionStatus = () =>
        {
            if (panel.IsDisposed) return;
            bool elevated = WindowsProcessElevation.IsCurrentProcessElevated();
            admin.Text = elevated ? "当前已允许" : "当前仅普通桌面";
            _restartAsAdministratorButton.Text = elevated ? "已允许" : "授权并重启应用";
            _restartAsAdministratorButton.Enabled = !elevated;
            try
            {
                PersistentStartupStatus state = WindowsPersistentStartup.GetStatus();
                startup.Text = state.IsEnabled ? "已配置，登录后自动以管理员运行" : "未启用管理员自启";
                secure.Text = WindowsSecureDesktopInstallation.InstalledExecutable(WindowsPersistentStartup.UserSid) is not null
                    ? (elevated ? "已配置" : "已配置，需授权重启应用生效") : "未配置";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { startup.Text = "暂时无法读取状态"; secure.Text = "暂时无法读取状态"; }
        };
        _refreshPermissionStatus();
        return panel.Parent!;
    }
}
