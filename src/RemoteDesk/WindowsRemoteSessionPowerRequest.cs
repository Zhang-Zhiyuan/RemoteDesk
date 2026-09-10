using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RemoteDesk;

// Scoped to an authenticated viewer, never to the listening service's lifetime.
// A handle-based request survives async thread changes and is released on
// disconnect/process exit. No power-plan, lock, UAC or display-driver changes.
internal sealed class WindowsRemoteSessionPowerRequest : IDisposable
{
    private SafeFileHandle? _handle;
    private readonly bool _system, _display;

    private WindowsRemoteSessionPowerRequest(SafeFileHandle? handle, bool system = false, bool display = false)
    { _handle = handle; _system = system; _display = display; }

    internal static WindowsRemoteSessionPowerRequest TryAcquire(Action<string>? log = null)
    {
        nint reason = Marshal.StringToHGlobalUni("RemoteDesk 正在进行远程桌面会话");
        SafeFileHandle? handle = null;
        try
        {
            var context = new ReasonContext { Flags = 1, SimpleReason = reason };
            handle = PowerCreateRequest(ref context);
            if (handle.IsInvalid)
            {
                log?.Invoke("Windows 未接受会话显示保持请求；未修改电源设置。");
                handle.Dispose();
                return new(null);
            }
            bool system = PowerSetRequest(handle, 1); // PowerRequestSystemRequired.
            bool display = PowerSetRequest(handle, 0); // PowerRequestDisplayRequired.
            // Wake an already-idle display as well; this pulse leaves no
            // continuous request tied to a thread-pool thread.
            if (system && display) SetThreadExecutionState(0x3);
            else log?.Invoke("Windows 未完全接受会话显示保持请求；系统电源策略继续生效。");
            return new(handle, system, display);
        }
        catch
        {
            handle?.Dispose();
            throw;
        }
        finally { Marshal.FreeHGlobal(reason); }
    }

    public void Dispose()
    {
        SafeFileHandle? handle = Interlocked.Exchange(ref _handle, null);
        if (handle is null) return;
        try
        {
            if (_display) PowerClearRequest(handle, 0);
            if (_system) PowerClearRequest(handle, 1);
        }
        finally { handle.Dispose(); }
    }

    // Full REASON_CONTEXT union, with only SIMPLE_STRING selected. The two
    // unused fields reserve the detailed-resource branch on 32/64-bit Windows.
    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version, Flags;
        public nint SimpleReason;
        public uint ResourceId, StringCount;
        public nint Strings;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerSetRequest(SafeFileHandle handle, int type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerClearRequest(SafeFileHandle handle, int type);
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
}
