using System.Runtime.InteropServices;

namespace RemoteDesk;

internal static class WindowsInputIntegrityGuard
{
    internal const string ElevationRequiredMessage =
        "目标窗口的管理员权限高于 RemoteDesk 被控端，" +
        "Windows 已阻止点击或键盘。请在被控端 RemoteDesk 中点击" +
        "“管理员重启”并确认 UAC；UAC、锁屏等安全桌面仍需在" +
        "被控端本机确认或解锁。";

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const uint GaRoot = 2;
    private static readonly Lazy<uint?> CurrentIntegrityLevel =
        new(ReadCurrentIntegrityLevel);

    public static void ThrowIfPointerTargetRequiresElevation(
        int screenX,
        int screenY)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ThrowIfWindowRequiresElevation(
            WindowFromPoint(
                new NativePoint(screenX, screenY)));
    }

    public static void ThrowIfForegroundTargetRequiresElevation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ThrowIfWindowRequiresElevation(
            GetForegroundWindow());
    }

    internal static bool IsTargetIntegrityHigher(
        uint? currentIntegrityLevel,
        uint? targetIntegrityLevel) =>
        currentIntegrityLevel.HasValue &&
        targetIntegrityLevel.HasValue &&
        targetIntegrityLevel.Value >
            currentIntegrityLevel.Value;

    private static void ThrowIfWindowRequiresElevation(
        nint window)
    {
        if (window == 0)
        {
            return;
        }

        nint rootWindow = GetAncestor(window, GaRoot);
        if (rootWindow != 0)
        {
            window = rootWindow;
        }

        _ = GetWindowThreadProcessId(
            window,
            out uint targetProcessId);
        if (targetProcessId == 0 ||
            targetProcessId == (uint)Environment.ProcessId)
        {
            return;
        }

        uint? currentIntegrity =
            CurrentIntegrityLevel.Value;
        uint? targetIntegrity =
            ReadProcessIntegrityLevel(targetProcessId);
        if (!IsTargetIntegrityHigher(
                currentIntegrity,
                targetIntegrity))
        {
            return;
        }

        throw new InvalidOperationException(
            ElevationRequiredMessage +
            $"（被控端完整性 0x{currentIntegrity:X}，" +
            $"目标窗口 0x{targetIntegrity:X}）");
    }

    private static uint? ReadCurrentIntegrityLevel()
    {
        nint token = 0;
        try
        {
            if (!OpenProcessToken(
                    GetCurrentProcess(),
                    TokenQuery,
                    out token))
            {
                return null;
            }

            return ReadTokenIntegrityLevel(token);
        }
        catch (Exception ex) when (
            ex is DllNotFoundException or
                EntryPointNotFoundException or
                BadImageFormatException)
        {
            return null;
        }
        finally
        {
            if (token != 0)
            {
                CloseHandle(token);
            }
        }
    }

    private static uint? ReadProcessIntegrityLevel(
        uint processId)
    {
        nint process = 0;
        nint token = 0;
        try
        {
            process = OpenProcess(
                ProcessQueryLimitedInformation,
                inheritHandle: false,
                processId);
            if (process == 0 ||
                !OpenProcessToken(
                    process,
                    TokenQuery,
                    out token))
            {
                return null;
            }

            return ReadTokenIntegrityLevel(token);
        }
        catch (Exception ex) when (
            ex is DllNotFoundException or
                EntryPointNotFoundException or
                BadImageFormatException)
        {
            return null;
        }
        finally
        {
            if (token != 0)
            {
                CloseHandle(token);
            }

            if (process != 0)
            {
                CloseHandle(process);
            }
        }
    }

    private static uint? ReadTokenIntegrityLevel(
        nint token)
    {
        _ = GetTokenInformation(
            token,
            TokenIntegrityLevel,
            0,
            0,
            out int requiredLength);
        if (requiredLength <= IntPtr.Size)
        {
            return null;
        }

        nint buffer = Marshal.AllocHGlobal(requiredLength);
        try
        {
            if (!GetTokenInformation(
                    token,
                    TokenIntegrityLevel,
                    buffer,
                    requiredLength,
                    out _))
            {
                return null;
            }

            nint sid = Marshal.ReadIntPtr(buffer);
            if (sid == 0 || !IsValidSid(sid))
            {
                return null;
            }

            nint countPointer =
                GetSidSubAuthorityCount(sid);
            if (countPointer == 0)
            {
                return null;
            }

            byte count = Marshal.ReadByte(countPointer);
            if (count == 0)
            {
                return null;
            }

            nint authorityPointer =
                GetSidSubAuthority(sid, (uint)(count - 1));
            return authorityPointer == 0
                ? null
                : unchecked((uint)Marshal.ReadInt32(
                    authorityPointer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public readonly int X;
        public readonly int Y;
    }

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(
        NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(
        nint window,
        uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint window,
        out uint processId);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        nint process,
        uint desiredAccess,
        out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        nint token,
        int tokenInformationClass,
        nint tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsValidSid(nint sid);

    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthorityCount(
        nint sid);

    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthority(
        nint sid,
        uint subAuthority);
}
