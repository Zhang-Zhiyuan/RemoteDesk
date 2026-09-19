using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RemoteDesk;

internal enum SecureDesktopIdentityQuery
{
    OpenProcess,
    OpenProcessToken,
    QueryFullProcessImageName,
    ProcessIdToSessionId,
    GetTokenElevation,
    GetNamedPipeServerProcessId
}

internal sealed class SecureDesktopIdentityQueryException : Win32Exception
{
    internal SecureDesktopIdentityQuery Query { get; }

    internal SecureDesktopIdentityQueryException(SecureDesktopIdentityQuery query, int error)
        : base(error, "Desktop helper identity query failed.") => Query = query;
}

// The service duplicates only its OWN SYSTEM token into the authorized console
// session. It never opens winlogon/lsass tokens or changes desktop/UAC ACLs.
internal static class WindowsSecureDesktopNative
{
    internal static bool IsSystem
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return identity.IsSystem; }
    }

    internal static uint? OwnedConsoleSession(string ownerSid)
    {
        uint session = WTSGetActiveConsoleSessionId();
        if (session == uint.MaxValue || !WTSQueryUserToken(session, out nint token)) return null;
        try
        {
            using var identity = new WindowsIdentity(token);
            return identity.User?.Value == ownerSid ? session : null;
        }
        finally { CloseHandle(token); }
    }

    internal static void EnablePrivilege(string name)
    {
        Check(OpenProcessToken(GetCurrentProcess(), 0x28, out nint token));
        try
        {
            Check(LookupPrivilegeValue(null, name, out long luid));
            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
            Check(AdjustTokenPrivileges(token, false, ref privileges, 0, 0, 0));
            int error = Marshal.GetLastWin32Error();
            if (error != 0) throw new Win32Exception(error);
        }
        finally { CloseHandle(token); }
    }

    internal static Process StartAgent(string executable, string ownerSid, uint sessionId, string stopEvent)
    {
        if (!IsSystem) throw new UnauthorizedAccessException("桌面辅助进程只能由系统服务启动。");
        Check(OpenProcessToken(GetCurrentProcess(), 0x000B, out nint token));
        nint copy = 0;
        try
        {
            Check(DuplicateTokenEx(token, 0xF01FF, 0, 2, 1, out copy));
            Check(SetTokenInformation(copy, 12, ref sessionId, sizeof(uint)));
            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(), Desktop = "winsta0\\default",
                Flags = 1, ShowWindow = 0
            };
            var command = new StringBuilder($"\"{executable}\" {WindowsSecureDesktopService.AgentArgument} {ownerSid} {sessionId} {stopEvent} {Environment.ProcessId}");
            Check(CreateProcessAsUser(copy, executable, command, 0, 0, false, 0x08000000,
                0, Path.GetDirectoryName(executable)!, ref startup, out ProcessInfo child));
            try { return Process.GetProcessById((int)child.ProcessId); }
            finally { CloseHandle(child.Process); CloseHandle(child.Thread); }
        }
        finally { if (copy != 0) CloseHandle(copy); CloseHandle(token); }
    }

    internal static (string? Sid, bool Elevated, string Image, int Session) ProcessIdentity(uint processId)
    {
        nint process = OpenProcess(0x1000, false, processId);
        CheckIdentityQuery(process != 0, SecureDesktopIdentityQuery.OpenProcess);
        nint token = 0;
        try
        {
            CheckIdentityQuery(OpenProcessToken(process, 8, out token), SecureDesktopIdentityQuery.OpenProcessToken);
            using var identity = new WindowsIdentity(token);
            var path = new StringBuilder(32768);
            int length = path.Capacity;
            CheckIdentityQuery(QueryFullProcessImageName(process, 0, path, ref length), SecureDesktopIdentityQuery.QueryFullProcessImageName);
            CheckIdentityQuery(ProcessIdToSessionId(processId, out uint session), SecureDesktopIdentityQuery.ProcessIdToSessionId);
            // Querying elevation does not require TOKEN_DUPLICATE. IsInRole
            // on a foreign primary token attempts impersonation and can throw.
            CheckIdentityQuery(GetTokenInformation(token, 20, out int elevated, sizeof(int), out _), SecureDesktopIdentityQuery.GetTokenElevation);
            return (identity.User?.Value, elevated != 0,
                path.ToString(), checked((int)session));
        }
        finally { if (token != 0) CloseHandle(token); CloseHandle(process); }
    }

    internal static uint PipeClientProcessId(SafePipeHandle pipe)
    { Check(GetNamedPipeClientProcessId(pipe, out uint pid)); return pid; }
    internal static uint PipeServerProcessId(SafePipeHandle pipe)
    { CheckIdentityQuery(GetNamedPipeServerProcessId(pipe, out uint pid), SecureDesktopIdentityQuery.GetNamedPipeServerProcessId); return pid; }

    private static void CheckIdentityQuery(bool okay, SecureDesktopIdentityQuery query)
    {
        if (!okay) throw new SecureDesktopIdentityQueryException(query, Marshal.GetLastWin32Error());
    }

    internal static void Check(bool okay)
    { if (!okay) throw new Win32Exception(Marshal.GetLastWin32Error()); }

    internal static void ReportFailure(string stage, Exception error)
    {
        // Metadata only: never include exception messages, input, pixels or settings.
        nint log = RegisterEventSource(null, "RemoteDesk");
        if (log == 0) return;
        try
        {
            string text = "Desktop helper " + stage + ": " + error.GetType().Name + " HResult=" + error.HResult +
                (error is Win32Exception native ? " Win32=" + native.NativeErrorCode : "") +
                (error is NativeInputInjectionException input ? " Native=" + input.NativeError + " Api=" + input.ApiName : "") +
                " Method=" + new StackTrace(error, false).GetFrame(0)?.GetMethod()?.Name;
            ReportEvent(log, 1, 0, 1001, 0, 1, 0, [text], 0);
        }
        finally { DeregisterEventSource(log); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct TokenPrivileges { public uint Count; public long Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public short ShowWindow, ReservedBytes; public nint ReservedPointer, StdIn, StdOut, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint session, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(nint token, int type, out int value, int size, out int returned);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(nint token, bool disableAll, ref TokenPrivileges privileges, uint length, nint previous, nint returned);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(nint token, uint access, nint attributes, int level, int type, out nint copy);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(nint token, int type, ref uint info, int length);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUser(nint token, string application, StringBuilder command, nint processAttributes,
        nint threadAttributes, bool inherit, uint flags, nint environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern nint RegisterEventSource(string? server, string source);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern bool ReportEvent(nint log, ushort type, ushort category,
        uint eventId, nint sid, ushort count, uint dataSize, string[] strings, nint data);
    [DllImport("advapi32.dll")] private static extern bool DeregisterEventSource(nint log);
}
