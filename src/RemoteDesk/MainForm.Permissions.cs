namespace RemoteDesk;

public sealed partial class MainForm
{
    private Func<Task>? _refreshPermissionStatus;

    internal readonly record struct HostPermissionStatus(bool StartupEnabled, bool SecureDesktopInstalled);

    internal static Task<HostPermissionStatus> QueryHostPermissionStatusAsync(
        Func<HostPermissionStatus>? query = null) => Task.Run(query ?? (() => new HostPermissionStatus(
            WindowsPersistentStartup.GetStatus().IsEnabled,
            WindowsSecureDesktopInstallation.InstalledExecutable(WindowsPersistentStartup.UserSid) is not null)));

    private Control BuildHostPermissionsPanel()
    {
        var panel = CreateSection("被控权限设置", "按需要开启；普通桌面控制无需全部安装。", sizeToContent: true);
        Label admin = CreateMutedLabel("");
        Label startup = CreateMutedLabel("正在检查…");
        Label secure = CreateMutedLabel("正在检查…");
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
        bool refreshing = false;
        bool refreshRequested = false;
        _refreshPermissionStatus = async () =>
        {
            if (_isClosing || panel.IsDisposed || !panel.IsHandleCreated || !panel.Visible) return;
            refreshRequested = true;
            if (refreshing) return;
            refreshing = true;
            try
            {
                do
                {
                    refreshRequested = false;
                    // Task Scheduler COM and service/ACL queries can block for
                    // seconds. Neither opening this panel nor constructing the
                    // main window should wait for them on the UI thread.
                    HostPermissionStatus state = await QueryHostPermissionStatusAsync();
                    if (_isClosing || panel.IsDisposed) return;
                    if (refreshRequested) continue; // A permission action changed the state while querying.
                    bool elevated = WindowsProcessElevation.IsCurrentProcessElevated();
                    panel.SuspendLayout();
                    try
                    {
                        admin.Text = elevated ? "当前已允许" : "当前仅普通桌面";
                        _restartAsAdministratorButton.Text = elevated ? "已允许" : "授权并重启应用";
                        _restartAsAdministratorButton.Enabled = !elevated;
                        startup.Text = state.StartupEnabled ? "已配置，登录后自动以管理员运行" : "未启用管理员自启";
                        secure.Text = state.SecureDesktopInstalled
                            ? (elevated ? "已配置" : "已配置，需授权重启应用生效") : "未配置";
                    }
                    finally { panel.ResumeLayout(); }
                } while (refreshRequested);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                or System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                if (_isClosing || panel.IsDisposed) return;
                startup.Text = "暂时无法读取状态";
                secure.Text = "暂时无法读取状态";
                _diagnosticLog.Append("PERMISSIONS", $"读取被控权限状态失败：{ex.Message}");
            }
            finally { refreshing = false; }
        };
        return panel.Parent!;
    }
}
