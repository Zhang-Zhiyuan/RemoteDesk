using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using static RemoteDesk.WindowsServiceNative;

namespace RemoteDesk;

internal static class WindowsSshListeners
{
    // Read-only SCM access. The existing provisioning path handles stopped or disabled services.
    private const uint ManagerConnect = 1, ServiceQueryConfig = 1, ServiceQueryStatus = 4;
    private const uint ServiceRunning = 4, InsufficientBuffer = 122;
    private const int TcpTableOwnerPidListener = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        internal uint Type, State, Controls, ExitCode, ServiceExitCode, CheckPoint, WaitHint, ProcessId, Flags;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(nint service, int level, out ServiceStatusProcess status, int size, out uint required);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable([Out] byte[]? table, ref int size, bool sort, int family, int tableClass, uint reserved);

    internal static IPEndPoint[]? TryRead()
    {
        nint manager = OpenSCManager(null, null, ManagerConnect);
        if (manager == 0) return null;
        nint service = 0;
        try
        {
            service = OpenService(manager, "sshd", ServiceQueryConfig | ServiceQueryStatus);
            if (service == 0 || !IsEnabled(service)) return null;
            uint pid = RunningProcessId(service);
            if (pid == 0) return null;
            var endpoints = new List<IPEndPoint>();
            if (Socket.OSSupportsIPv4 && !ReadTable(AddressFamily.InterNetwork, pid, endpoints) ||
                Socket.OSSupportsIPv6 && !ReadTable(AddressFamily.InterNetworkV6, pid, endpoints)) return null;
            return endpoints.Count > 0 && RunningProcessId(service) == pid ? endpoints.Distinct().ToArray() : null;
        }
        finally
        {
            if (service != 0) CloseServiceHandle(service);
            CloseServiceHandle(manager);
        }
    }

    private static bool IsEnabled(nint service)
    {
        const int maxConfigBytes = 8192; // QUERY_SERVICE_CONFIGW's documented maximum.
        nint buffer = Marshal.AllocHGlobal(maxConfigBytes);
        try
        {
            return QueryServiceConfig(service, buffer, maxConfigBytes, out _) &&
                   Marshal.PtrToStructure<ServiceConfiguration>(buffer).Start is 2 or 3;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static uint RunningProcessId(nint service) =>
        QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _) &&
        status.State == ServiceRunning ? status.ProcessId : 0;

    private static bool ReadTable(AddressFamily family, uint pid, List<IPEndPoint> endpoints)
    {
        int size = 0;
        byte[]? table = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint result = GetExtendedTcpTable(table, ref size, false, (int)family, TcpTableOwnerPidListener, 0);
            if (result == 0) return table != null && ParseTable(table, family, pid, endpoints);
            if (result != InsufficientBuffer || size is < 4 or > 16 * 1024 * 1024) return false;
            table = new byte[size];
        }
        return false;
    }

    internal static bool ParseTable(ReadOnlySpan<byte> table, AddressFamily family, uint pid, List<IPEndPoint> endpoints)
    {
        // MIB_TCPROW_OWNER_PID and MIB_TCP6ROW_OWNER_PID have different field orders.
        // Address, scope and port are in network byte order. DWORD count and PID are native.
        bool v6 = family == AddressFamily.InterNetworkV6;
        int rowSize = v6 ? 56 : 24;
        if (pid == 0 || (!v6 && family != AddressFamily.InterNetwork) || table.Length < 4) return false;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        var rows = table[4..];
        if (count > rows.Length / rowSize) return false;
        for (int i = 0; i < count; i++)
        {
            var row = rows.Slice(i * rowSize, rowSize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(row[^4..]) != pid) continue;
            int port = BinaryPrimitives.ReadUInt16BigEndian(row[(v6 ? 20 : 8)..]);
            if (port == 0) continue;
            var address = v6 ? new IPAddress(row[..16], BinaryPrimitives.ReadUInt32BigEndian(row[16..20]))
                             : new IPAddress(row[4..8]);
            endpoints.Add(WindowsSshService.ListenerEndpoint(address, port));
        }
        return true;
    }
}
