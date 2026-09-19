using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RemoteDesk;

internal static partial class WindowsSecureDesktopInstallation
{
    internal sealed record OwnedServiceConfiguration(string Executable, string Command, uint StartType, uint ErrorControl);

    private static bool UpgradeExistingHelperAfterUpdate(string sid, string currentExecutable)
    {
        string? installed = InstalledExecutable(sid);
        if (installed is null || string.Equals(installed, currentExecutable, StringComparison.OrdinalIgnoreCase)) return false;
        string? currentBuild = RemoteDeskBuildInfo.ReadExecutableBuildStamp(currentExecutable);
        string? oldBuild = RemoteDeskBuildInfo.ReadExecutableBuildStamp(installed);
        if (!ShouldUpgradeExistingHelper(true, installed, currentExecutable, oldBuild, currentBuild)) return false;
        if (!WindowsPersistentStartup.IsManagedExecutable(currentExecutable, RootForSid(sid)))
            throw new InvalidOperationException("新版被控端不在受保护安装目录，未修改锁屏服务；请先安装常驻启动，再更新锁屏控制。");
        WindowsPersistentStartup.ValidateProtectedFile(installed);
        WindowsPersistentStartup.ValidateProtectedFile(currentExecutable);
        // Deny rename/write while checking and registering these exact files.
        // No files or logon tasks are installed/copied by this maintenance path.
        using var oldFile = new FileStream(installed, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var newFile = new FileStream(currentExecutable, FileMode.Open, FileAccess.Read, FileShare.Read);
        nint manager = OpenSCManager(null, null, 1);
        WindowsSecureDesktopNative.Check(manager != 0);
        nint service = 0;
        try
        {
            // Existing handle only: QUERY_CONFIG | CHANGE_CONFIG |
            // QUERY_STATUS | START | STOP. Never CREATE_SERVICE or delete.
            service = OpenService(manager, ServiceName(sid), 0x37);
            WindowsSecureDesktopNative.Check(service != 0);
            OwnedServiceConfiguration? original = ReadEnabledOwnedService(service, sid);
            if (original is null || !string.Equals(original.Executable, installed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("锁屏服务安装已变化，未继续更新。");
            var replacement = original with { Executable = currentExecutable, Command = ServiceCommand(currentExecutable, sid) };
            return UpgradeExistingHelperAfterUpdate(original, replacement,
                () => ReadEnabledOwnedService(service, sid),
                () =>
                {
                    WindowsPersistentStartup.ValidateProtectedFile(installed);
                    WindowsPersistentStartup.ValidateProtectedFile(currentExecutable);
                    _ = RemoteUpdateTrust.ValidateUpgrade(installed, currentExecutable);
                },
                () =>
                {
                    WindowsSecureDesktopNative.Check(QueryServiceStatus(service, out var status));
                    return status.State == 4;
                },
                () => StopAndWait(service),
                command => WindowsSecureDesktopNative.Check(ChangeServiceConfig(service, uint.MaxValue,
                    uint.MaxValue, uint.MaxValue, command, null, 0, null, null, null, null)),
                () =>
                {
                    WindowsSecureDesktopNative.Check(QueryServiceStatus(service, out var status));
                    if (status.State != 4) WindowsSecureDesktopNative.Check(StartService(service, 0, 0));
                    WaitForState(service, 4, TimeSpan.FromSeconds(15));
                },
                WaitForAuthenticatedStatus,
                error => WindowsSecureDesktopNative.ReportFailure("update-helper-rollback", error));
        }
        finally { if (service != 0) CloseServiceHandle(service); CloseServiceHandle(manager); }
    }

    internal static bool ShouldUpgradeExistingHelper(bool elevated, string? installedExecutable, string currentExecutable,
        string? installedBuild, string? currentBuild) => elevated && installedExecutable is not null &&
        !string.Equals(installedExecutable, currentExecutable, StringComparison.OrdinalIgnoreCase) &&
        RemoteDeskBuildInfo.CompareBuildStamps(currentBuild, installedBuild) is > 0;

    internal static bool UpgradeExistingHelperAfterUpdate(OwnedServiceConfiguration original, OwnedServiceConfiguration replacement,
        Func<OwnedServiceConfiguration?> readCurrent, Action validateUpgrade, Func<bool> isRunning,
        Action stop, Action<string> changeCommand, Action start, Action<Action> waitForAuthenticatedStatus,
        Action<Exception> reportRollbackFailure)
    {
        if (original.StartType is not (2 or 3) || replacement.StartType != original.StartType ||
            replacement.ErrorControl != original.ErrorControl || original.Executable == replacement.Executable)
            return false;
        void Guard(OwnedServiceConfiguration expected)
        {
            if (readCurrent() != expected)
                throw new InvalidOperationException("锁屏服务配置在更新期间已变化，已停止操作；请检查现有安装。");
        }
        Guard(original);
        validateUpgrade();
        Guard(original);
        bool wasRunning = isRunning();
        bool stopAttempted = false, changeAttempted = false;
        try
        {
            Guard(original);
            stopAttempted = true;
            stop();
            Guard(original);
            changeAttempted = true;
            changeCommand(replacement.Command);
            Guard(replacement);
            start();
            waitForAuthenticatedStatus(() => Guard(replacement));
            Guard(replacement);
            return true;
        }
        catch
        {
            // Roll back only a configuration still matching this transaction.
            // A concurrent disable/reconfigure/delete belongs to its caller.
            try
            {
                OwnedServiceConfiguration? current = readCurrent();
                if (changeAttempted && current == replacement)
                {
                    stop();
                    Guard(replacement);
                    changeCommand(original.Command);
                    Guard(original);
                    if (wasRunning)
                    {
                        start();
                        waitForAuthenticatedStatus(() => Guard(original));
                    }
                }
                else if (stopAttempted && current == original && wasRunning)
                {
                    Guard(original);
                    start();
                    waitForAuthenticatedStatus(() => Guard(original));
                }
            }
            catch (Exception rollback) { try { reportRollbackFailure(rollback); } catch { } }
            throw;
        }
    }

    private static OwnedServiceConfiguration? ReadEnabledOwnedService(nint service, string sid)
    {
        QueryServiceConfig(service, 0, 0, out uint required);
        int error = Marshal.GetLastWin32Error();
        if (error != 122 || required is < 1 or > 65536) throw new Win32Exception(error);
        nint buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            WindowsSecureDesktopNative.Check(QueryServiceConfig(service, buffer, required, out _));
            var config = Marshal.PtrToStructure<ServiceConfiguration>(buffer);
            if (config.Type != 0x10 || config.Start is not (2 or 3) ||
                !string.Equals(Marshal.PtrToStringUni(config.Account), "LocalSystem", StringComparison.OrdinalIgnoreCase)) return null;
            string? command = Marshal.PtrToStringUni(config.Binary);
            string? executable = command is null ? null : ReadOwnedExecutable(command, sid);
            return executable is null ? null : new(executable, command!, config.Start, config.Error);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfiguration
    {
        internal uint Type, Start, Error;
        internal nint Binary, Group;
        internal uint Tag;
        internal nint Dependencies, Account, DisplayName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryServiceConfig(nint service, nint config, uint bytes, out uint required);
}
