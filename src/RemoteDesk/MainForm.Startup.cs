namespace RemoteDesk;

public sealed partial class MainForm
{
    private async Task MigrateLegacyStartupAsync()
    {
        if (_isClosing || IsDisposed || !_startWithWindowsBox.Enabled ||
            !StartupService.ShouldMigrateLegacyRegistration(StartupService.GetStatus())) return;
        // Migrate only a previously opted-in legacy entry, never enable autostart
        // merely because the program now requires elevation. Existing managed
        // tasks and valid registrations for other portable copies are preserved.
        if (!WindowsProcessElevation.IsCurrentProcessElevated())
        {
            AppendHostLog("旧版开机启动项需要迁移，请在管理员权限下运行发布版 RemoteDesk 后重试。");
            return;
        }
        _startWithWindowsBox.Enabled = false;
        try
        {
            await StartupService.SetEnabledAsync(true);
            if (_isClosing || IsDisposed) return;
            _applyingSettings = true;
            try { ApplyStartupRegistrationStatus(StartupService.GetStatus()); }
            finally { _applyingSettings = false; }
            SaveSettingsFromUi();
            AppendHostLog("已将旧版开机启动项迁移为管理员自启动，登录后会启动受保护的副本到托盘。当前连接未中断。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            System.Security.SecurityException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            if (!_isClosing && !IsDisposed)
                AppendHostLog($"管理员自启动迁移未完成：{ex.Message}。请在发布版中使用“设置登录后自动可控”重试。");
        }
        finally
        {
            if (!_isClosing && !IsDisposed) _startWithWindowsBox.Enabled = true;
        }
    }
}
