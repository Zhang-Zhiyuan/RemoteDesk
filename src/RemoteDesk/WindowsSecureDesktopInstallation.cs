using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace RemoteDesk;

internal static partial class WindowsSecureDesktopInstallation
{
    internal const string InstallArgument = "--install-secure-desktop";
    internal const string DisableArgument = "--disable-secure-desktop";
    internal const string ServiceArgument = "--secure-desktop-service";
    internal const string RecoverAfterUpdateArgument = "--recover-secure-desktop-after-update";
    private const uint ServiceAllAccess = 0xF01FF;

    internal static string ValidateSid(string value)
    {
        var sid = new SecurityIdentifier(value);
        if (sid.Value != value || !(value.StartsWith("S-1-5-21-", StringComparison.Ordinal) ||
            value.StartsWith("S-1-12-1-", StringComparison.Ordinal)))
            throw new ArgumentException("锁屏控制需要有效的 Windows 用户 SID。");
        return value;
    }

    internal static string ServiceName(string sid) => "RemoteDesk-SecureDesktop-" + ValidateSid(sid);
    internal static string RootForSid(string sid) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RemoteDesk", "Managed", ValidateSid(sid));
    internal static string ServiceCommand(string executable, string sid) => $"\"{executable}\" {ServiceArgument} {ValidateSid(sid)}";

    internal static string? ReadOwnedExecutable(string command, string sid)
    {
        string suffix = $"\" {ServiceArgument} {ValidateSid(sid)}";
        if (!command.StartsWith('"') || !command.EndsWith(suffix, StringComparison.Ordinal)) return null;
        string path = command[1..^suffix.Length];
        return WindowsPersistentStartup.IsManagedExecutable(path, RootForSid(sid)) ? path : null;
    }

    internal static string? InstalledExecutable(string sid, bool requireEnabled = true)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName(sid));
        if (key is null || (requireEnabled && key.GetValue("Start") is not (2 or 3))) return null;
        if (key.GetValue("Type") is not 0x10 ||
            !string.Equals(key.GetValue("ObjectName") as string, "LocalSystem", StringComparison.OrdinalIgnoreCase)) return null;
        return key.GetValue("ImagePath") is string command ? ReadOwnedExecutable(command, sid) : null;
    }

    internal static bool RecoverSharedExecutableAfterUpdate()
    {
        string sid = WindowsPersistentStartup.UserSid;
        bool elevated = WindowsProcessElevation.IsCurrentProcessElevated();
        string currentExecutable = Path.GetFullPath(Application.ExecutablePath);
        if (elevated && UpgradeExistingHelperAfterUpdate(sid, currentExecutable)) return true;
        return RecoverSharedExecutableAfterUpdate(
            elevated, currentExecutable,
            () => InstalledExecutable(sid), WindowsPersistentStartup.ValidateProtectedFile,
            QueryAuthenticatedStatus, guard => RestartExistingService(sid, guard), WaitForAuthenticatedStatus);
    }

    internal static async Task<bool> RecoverSharedExecutableAfterUpdateAsync()
    {
        if (!WindowsProcessElevation.IsCurrentProcessElevated()) return false;
        // Recovery must outlive a normal parent/UI exit once STOP has been sent.
        // This fixed-purpose child is not a shell and cannot run arbitrary commands.
        // Disposing its Process handle does not terminate the child, and no UI
        // lifetime cancellation token is attached to its bounded SCM/IPC work.
        using Process child = Process.Start(CreateRecoveryStartInfo(
            Path.GetFullPath(Application.ExecutablePath), WindowsPersistentStartup.UserSid))
            ?? throw new InvalidOperationException("无法启动更新后的锁屏辅助恢复程序。");
        await child.WaitForExitAsync().ConfigureAwait(false);
        return InterpretRecoveryExitCode(child.ExitCode);
    }

    internal static ProcessStartInfo CreateRecoveryStartInfo(string executable, string sid)
    {
        if (!Path.IsPathFullyQualified(executable))
            throw new ArgumentException("锁屏辅助恢复需要完整的当前程序路径。", nameof(executable));
        ValidateSid(sid);
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add(RecoverAfterUpdateArgument);
        start.ArgumentList.Add(sid);
        return start;
    }

    internal static bool InterpretRecoveryExitCode(int exitCode) => exitCode switch
    {
        0 => false,
        2 => true,
        _ => throw new InvalidOperationException("更新后的锁屏辅助恢复未完成；被控端仍保持运行，请检查锁屏控制服务。")
    };

    internal static int ExecuteRecoveryCommand(string[] args, string currentSid, bool elevated, Func<bool> recover)
    {
        try
        {
            if (!elevated || args.Length != 2 || args[0] != RecoverAfterUpdateArgument ||
                args[1] != currentSid || ValidateSid(args[1]) != currentSid)
                return 1;
            return recover() ? 2 : 0;
        }
        catch
        {
            // The parent sees only this fixed result, never native exception
            // messages, process paths or identity details from this CLI boundary.
            return 1;
        }
    }

    // The startup caller restricts this to --resume-host-after-update. The
    // separate policy boundary also fails closed if installation ownership,
    // enablement or the protected executable changes during recovery.
    internal static bool RecoverSharedExecutableAfterUpdate(bool isElevated, string currentExecutable,
        Func<string?> readEnabledExecutable, Action<string> validateProtectedFile, Action queryStatus,
        Action<Action> restartService, Action<Action> waitForAuthenticatedStatus)
    {
        if (!isElevated) return false;
        string? installed = readEnabledExecutable();
        if (installed is null || !string.Equals(installed, currentExecutable, StringComparison.OrdinalIgnoreCase))
            return false;
        validateProtectedFile(installed);
        try
        {
            queryStatus();
            return false; // Already authenticated; never restart a healthy helper.
        }
        catch (InvalidOperationException ex) when (ex.InnerException is SecureDesktopServerIdentityException
            { Mismatch: SecureDesktopServerIdentityMismatch.Executable })
        {
            // Updating the shared EXE can rename the still-running helper's
            // process image to the old backup. Keep the exact identity check:
            // reload only this existing service, never accept the backup path.
        }

        void EnsureUnchangedOwnership()
        {
            if (!string.Equals(readEnabledExecutable(), installed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("锁屏控制安装在恢复期间已更改，已停止恢复；请检查当前安装状态。");
            validateProtectedFile(installed);
            if (!string.Equals(readEnabledExecutable(), installed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("锁屏控制安装在恢复期间已更改，已停止恢复；请检查当前安装状态。");
        }

        EnsureUnchangedOwnership();
        restartService(EnsureUnchangedOwnership);
        EnsureUnchangedOwnership();
        waitForAuthenticatedStatus(EnsureUnchangedOwnership);
        EnsureUnchangedOwnership();
        return true;
    }

    private static void QueryAuthenticatedStatus()
    {
        using var client = new WindowsSecureDesktopClient();
        client.QueryStatus();
    }

    private static void WaitForAuthenticatedStatus(Action ensureUnchangedOwnership)
    {
        var ready = Stopwatch.StartNew();
        Exception? lastFailure = null;
        while (ready.Elapsed < TimeSpan.FromSeconds(10))
        {
            ensureUnchangedOwnership();
            try
            {
                QueryAuthenticatedStatus();
            }
            catch (InvalidOperationException ex)
            {
                lastFailure = ex;
                Thread.Sleep(250);
                continue;
            }
            ensureUnchangedOwnership();
            return;
        }
        throw new InvalidOperationException("更新后的锁屏辅助进程尚未通过身份验证；请检查锁屏控制服务。", lastFailure);
    }

    private static void RestartExistingService(string sid, Action ensureUnchangedOwnership)
    {
        ensureUnchangedOwnership();
        nint manager = OpenSCManager(null, null, 1); // SC_MANAGER_CONNECT only.
        WindowsSecureDesktopNative.Check(manager != 0);
        nint service = 0;
        try
        {
            // QUERY_STATUS | START | STOP. No configuration/write/install rights.
            service = OpenService(manager, ServiceName(sid), 0x04 | 0x10 | 0x20);
            WindowsSecureDesktopNative.Check(service != 0);
            ensureUnchangedOwnership();
            StopAndWait(service);
            ensureUnchangedOwnership();
            WindowsSecureDesktopNative.Check(StartService(service, 0, 0));
            WaitForState(service, 4, TimeSpan.FromSeconds(15));
            ensureUnchangedOwnership();
        }
        finally { if (service != 0) CloseServiceHandle(service); CloseServiceHandle(manager); }
    }

    internal static async Task ChangeAsync(bool enable)
    {
        var info = new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, Verb = "runas" };
        info.ArgumentList.Add(enable ? InstallArgument : DisableArgument);
        info.ArgumentList.Add(WindowsPersistentStartup.UserSid);
        using Process helper = Process.Start(info) ?? throw new InvalidOperationException("无法启动锁屏控制安装程序。");
        await helper.WaitForExitAsync();
        if (helper.ExitCode != 0) throw new InvalidOperationException("锁屏控制配置未完成，现有连接保持不变。");
    }

    internal static bool TryHandleCommand(string[] args)
    {
        if (args.Length > 0 && args[0] == RecoverAfterUpdateArgument)
        {
            try
            {
                Environment.ExitCode = ExecuteRecoveryCommand(args, WindowsPersistentStartup.UserSid,
                    WindowsProcessElevation.IsCurrentProcessElevated(), RecoverSharedExecutableAfterUpdate);
            }
            catch { Environment.ExitCode = 1; }
            if (Environment.ExitCode == 1) Console.Error.WriteLine("RemoteDesk secure desktop recovery failed.");
            return true;
        }
        if (args.Length == 0 || args[0] is not (InstallArgument or DisableArgument)) return false;
        bool quiet = args.Length == 3 && args[2] == "--quiet";
        try
        {
            if (args.Length is < 2 or > 3 || (args.Length == 3 && !quiet) ||
                ValidateSid(args[1]) != WindowsPersistentStartup.UserSid || !WindowsProcessElevation.IsCurrentProcessElevated())
                throw new UnauthorizedAccessException("请使用当前 Windows 用户的管理员权限安装锁屏控制。");
            if (args[0] == InstallArgument) Install(Application.ExecutablePath, args[1]);
            else Disable(args[1]);
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            if (quiet) Console.Error.WriteLine(ex.Message);
            else MessageBox.Show(ex.Message, "RemoteDesk 锁屏控制", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        return true;
    }

    internal static void Install(string source, string sid)
    {
        ValidateSid(sid);
        if (!WindowsProcessElevation.IsCurrentProcessElevated() || sid != WindowsPersistentStartup.UserSid)
            throw new UnauthorizedAccessException();
        string? previousExecutable = InstalledExecutable(sid, requireEnabled: false);
        bool previouslyEnabled = InstalledExecutable(sid) is not null;
        using (RegistryKey? existing = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName(sid)))
        {
            if (existing is not null && previousExecutable is null)
                throw new IOException("同名系统服务不是此 RemoteDesk 的安装，未覆盖。");
        }
        if (previousExecutable is not null) WindowsPersistentStartup.ValidateProtectedFile(previousExecutable);
        // Reuse the existing protected, content-addressed package and elevated
        // user logon task. No user passwords/configuration are copied to SYSTEM.
        WindowsPersistentStartup.Install(source);
        string executable = WindowsPersistentStartup.GetStatus().ExecutablePath
            ?? throw new IOException("受保护的 RemoteDesk 副本安装失败。");
        WindowsPersistentStartup.ValidateProtectedFile(executable);
        nint manager = OpenSCManager(null, null, 3);
        WindowsSecureDesktopNative.Check(manager != 0);
        nint service = 0;
        bool changed = false;
        try
        {
            service = OpenService(manager, ServiceName(sid), ServiceAllAccess);
            if (service != 0)
            {
                if (InstalledExecutable(sid, requireEnabled: false) is null)
                    throw new IOException("同名系统服务不是此 RemoteDesk 的安装，未覆盖。");
                changed = true;
                StopAndWait(service);
                WindowsSecureDesktopNative.Check(ChangeServiceConfig(service, uint.MaxValue, 2, 1,
                    ServiceCommand(executable, sid), null, 0, null, "LocalSystem", null, "RemoteDesk 锁屏控制"));
                changed = true;
            }
            else
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 1060) throw new Win32Exception(error);
                service = CreateService(manager, ServiceName(sid), "RemoteDesk 锁屏控制", ServiceAllAccess,
                    0x10, 2, 1, ServiceCommand(executable, sid), null, 0, null, "LocalSystem", null);
                WindowsSecureDesktopNative.Check(service != 0);
                changed = true;
            }
            WindowsSecureDesktopNative.Check(StartService(service, 0, 0));
            WaitForState(service, 4, TimeSpan.FromSeconds(15));
            // Running in SCM alone is insufficient: the session worker may
            // still have failed. Verify the same mutually authenticated IPC
            // path used by the host before declaring the install successful.
            var ready = Stopwatch.StartNew();
            Exception? lastFailure = null;
            while (ready.Elapsed < TimeSpan.FromSeconds(10))
            {
                try
                {
                    using var client = new WindowsSecureDesktopClient();
                    client.QueryStatus();
                    return;
                }
                catch (InvalidOperationException ex) { lastFailure = ex; }
                Thread.Sleep(250);
            }
            throw new InvalidOperationException("锁屏辅助进程尚未就绪；请查看 Windows 应用日志中的 RemoteDesk 事件。", lastFailure);
        }
        catch
        {
            if (changed && service != 0)
            {
                try
                {
                    StopAndWait(service);
                    WindowsSecureDesktopNative.Check(ChangeServiceConfig(service, uint.MaxValue,
                        previousExecutable is not null && previouslyEnabled ? 2u : 4u, 1,
                        ServiceCommand(previousExecutable ?? executable, sid), null, 0, null, "LocalSystem", null, null));
                    if (previousExecutable is not null && previouslyEnabled)
                        WindowsSecureDesktopNative.Check(StartService(service, 0, 0));
                }
                catch (Exception rollback) { WindowsSecureDesktopNative.ReportFailure("install-rollback", rollback); }
            }
            throw;
        }
        finally { if (service != 0) CloseServiceHandle(service); CloseServiceHandle(manager); }
    }

    internal static void Disable(string sid)
    {
        if (!WindowsProcessElevation.IsCurrentProcessElevated() || sid != WindowsPersistentStartup.UserSid)
            throw new UnauthorizedAccessException();
        if (InstalledExecutable(sid, false) is null) throw new IOException("未找到属于此用户的锁屏控制服务。");
        nint manager = OpenSCManager(null, null, 1);
        WindowsSecureDesktopNative.Check(manager != 0);
        nint service = 0;
        try
        {
            service = OpenService(manager, ServiceName(sid), ServiceAllAccess);
            WindowsSecureDesktopNative.Check(service != 0);
            StopAndWait(service);
            WindowsSecureDesktopNative.Check(ChangeServiceConfig(service, uint.MaxValue, 4, uint.MaxValue,
                null, null, 0, null, null, null, null));
        }
        finally { if (service != 0) CloseServiceHandle(service); CloseServiceHandle(manager); }
    }

    private static void StopAndWait(nint service)
    {
        if (!ControlService(service, 1, out _))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 1062) throw new Win32Exception(error);
            return;
        }
        WaitForState(service, 1, TimeSpan.FromSeconds(15));
    }

    private static void WaitForState(nint service, uint desired, TimeSpan deadline)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < deadline)
        {
            WindowsSecureDesktopNative.Check(QueryServiceStatus(service, out WindowsSecureDesktopService.ServiceStatus status));
            if (status.State == desired) return;
            if (desired == 4 && status.State == 1) throw new IOException("锁屏控制服务启动失败，请检查系统服务日志。");
            Thread.Sleep(100);
        }
        throw new TimeoutException("锁屏控制服务状态切换超时。");
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateService(nint manager, string name, string display, uint access, uint type, uint start, uint error, string binary, string? group, nint tag, string? dependencies, string account, string? password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ChangeServiceConfig(nint service, uint type, uint start, uint error, string? binary, string? group, nint tag, string? dependencies, string? account, string? password, string? display);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool StartService(nint service, int count, nint arguments);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool ControlService(nint service, uint control, out WindowsSecureDesktopService.ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(nint service, out WindowsSecureDesktopService.ServiceStatus status);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(nint handle);
}
