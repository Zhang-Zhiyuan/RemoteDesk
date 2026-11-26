using System.Runtime.InteropServices;

namespace RemoteDesk;

internal static class WindowsServiceNative
{
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint OpenService(nint manager, string name, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool QueryServiceConfig(nint service, nint config, uint bytes, out uint required);

    [DllImport("advapi32.dll")]
    internal static extern bool CloseServiceHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceConfiguration
    {
        internal uint Type, Start, Error;
        internal nint Binary, Group;
        internal uint Tag;
        internal nint Dependencies, Account, DisplayName;
    }
}
