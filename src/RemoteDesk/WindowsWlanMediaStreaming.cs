using System.Runtime.InteropServices;

namespace RemoteDesk;

internal interface IWlanMediaStreamingApi
{
    uint OpenHandle(out nint clientHandle);

    uint EnumerateConnectedInterfaces(
        nint clientHandle,
        out IReadOnlyList<Guid> interfaceIds);

    uint SetMediaStreamingMode(
        nint clientHandle,
        Guid interfaceId,
        bool enabled);

    uint CloseHandle(nint clientHandle);
}

/// <summary>
/// Holds Windows WLAN media-streaming mode only while an interactive remote
/// session is active. All native calls are best-effort: this optimization must
/// never prevent a wired host, an unsupported WLAN driver, or a stopped WLAN
/// service from hosting a session.
/// </summary>
internal static class WindowsWlanMediaStreaming
{
    private const uint ErrorSuccess = 0;
    private static readonly IDisposable InactiveLease =
        new InactiveMediaStreamingLease();

    internal static IDisposable TryAcquireInteractive(
        Action<string>? diagnostic = null)
    {
        return TryAcquire(
            OperatingSystem.IsWindows(),
            NativeWlanMediaStreamingApi.Instance,
            diagnostic);
    }

    internal static IDisposable TryAcquire(
        bool isWindows,
        IWlanMediaStreamingApi api,
        Action<string>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (!isWindows)
        {
            return InactiveLease;
        }

        nint clientHandle = 0;
        bool handleOpened = false;
        try
        {
            uint openResult = api.OpenHandle(out clientHandle);
            if (openResult != ErrorSuccess || clientHandle == 0)
            {
                string detail = openResult != ErrorSuccess
                    ? $"WlanOpenHandle 返回 {openResult}"
                    : "WlanOpenHandle 返回空句柄";
                Report(
                    diagnostic,
                    $"WLAN 媒体流优化未启用：{detail}。");

                return InactiveLease;
            }

            handleOpened = true;
            uint enumerateResult =
                api.EnumerateConnectedInterfaces(
                    clientHandle,
                    out IReadOnlyList<Guid> interfaceIds);
            if (enumerateResult != ErrorSuccess)
            {
                Report(
                    diagnostic,
                    $"WLAN 媒体流优化未启用：WlanEnumInterfaces 返回 {enumerateResult}。");
                CloseBestEffort(api, clientHandle, diagnostic);
                return InactiveLease;
            }

            if (interfaceIds.Count == 0)
            {
                CloseBestEffort(api, clientHandle, diagnostic);
                return InactiveLease;
            }

            var enabledInterfaces =
                new List<Guid>(interfaceIds.Count);
            uint firstEnableError = ErrorSuccess;
            foreach (Guid interfaceId in interfaceIds)
            {
                uint setResult;
                try
                {
                    setResult = api.SetMediaStreamingMode(
                        clientHandle,
                        interfaceId,
                        enabled: true);
                }
                catch (Exception ex)
                {
                    Report(
                        diagnostic,
                        $"WLAN 媒体流优化接口启用失败：{ex.Message}");
                    continue;
                }

                if (setResult == ErrorSuccess)
                {
                    enabledInterfaces.Add(interfaceId);
                }
                else if (firstEnableError == ErrorSuccess)
                {
                    firstEnableError = setResult;
                }
            }

            if (enabledInterfaces.Count == 0)
            {
                if (firstEnableError != ErrorSuccess)
                {
                    Report(
                        diagnostic,
                        $"WLAN 媒体流优化未启用：接口设置返回 {firstEnableError}。");
                }

                CloseBestEffort(api, clientHandle, diagnostic);
                return InactiveLease;
            }

            if (enabledInterfaces.Count != interfaceIds.Count)
            {
                Report(
                    diagnostic,
                    $"WLAN 媒体流优化已部分启用（{enabledInterfaces.Count}/{interfaceIds.Count} 个已连接接口）。");
            }
            else
            {
                Report(
                    diagnostic,
                    $"WLAN 媒体流优化已启用（{enabledInterfaces.Count} 个已连接接口）。");
            }

            handleOpened = false;
            return new ActiveMediaStreamingLease(
                api,
                clientHandle,
                enabledInterfaces.ToArray(),
                diagnostic);
        }
        catch (Exception ex)
        {
            Report(
                diagnostic,
                $"WLAN 媒体流优化不可用：{ex.Message}");
            if (handleOpened)
            {
                CloseBestEffort(api, clientHandle, diagnostic);
            }

            return InactiveLease;
        }
    }

    private static void CloseBestEffort(
        IWlanMediaStreamingApi api,
        nint clientHandle,
        Action<string>? diagnostic)
    {
        try
        {
            uint closeResult = api.CloseHandle(clientHandle);
            if (closeResult != ErrorSuccess)
            {
                Report(
                    diagnostic,
                    $"WLAN 媒体流优化句柄关闭返回 {closeResult}。");
            }
        }
        catch (Exception ex)
        {
            Report(
                diagnostic,
                $"WLAN 媒体流优化句柄关闭失败：{ex.Message}");
        }
    }

    private static void Report(
        Action<string>? diagnostic,
        string message)
    {
        if (diagnostic is null)
        {
            return;
        }

        try
        {
            diagnostic(message);
        }
        catch
        {
            // A diagnostic callback must not turn optional network tuning into
            // a session failure.
        }
    }

    private sealed class ActiveMediaStreamingLease : IDisposable
    {
        private readonly nint _clientHandle;
        private readonly Guid[] _enabledInterfaces;
        private readonly Action<string>? _diagnostic;
        private IWlanMediaStreamingApi? _api;

        internal ActiveMediaStreamingLease(
            IWlanMediaStreamingApi api,
            nint clientHandle,
            Guid[] enabledInterfaces,
            Action<string>? diagnostic)
        {
            _api = api;
            _clientHandle = clientHandle;
            _enabledInterfaces = enabledInterfaces;
            _diagnostic = diagnostic;
        }

        public void Dispose()
        {
            IWlanMediaStreamingApi? api =
                Interlocked.Exchange(ref _api, null);
            if (api is null)
            {
                return;
            }

            uint firstDisableError = ErrorSuccess;
            foreach (Guid interfaceId in _enabledInterfaces)
            {
                try
                {
                    uint setResult = api.SetMediaStreamingMode(
                        _clientHandle,
                        interfaceId,
                        enabled: false);
                    if (setResult != ErrorSuccess &&
                        firstDisableError == ErrorSuccess)
                    {
                        firstDisableError = setResult;
                    }
                }
                catch (Exception ex)
                {
                    Report(
                        _diagnostic,
                        $"WLAN 媒体流优化接口恢复失败：{ex.Message}");
                }
            }

            if (firstDisableError != ErrorSuccess)
            {
                Report(
                    _diagnostic,
                    $"WLAN 媒体流优化接口恢复返回 {firstDisableError}。");
            }

            CloseBestEffort(api, _clientHandle, _diagnostic);
        }
    }

    private sealed class InactiveMediaStreamingLease : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

internal sealed class NativeWlanMediaStreamingApi :
    IWlanMediaStreamingApi
{
    private const uint WlanClientVersion = 2;
    private const uint ErrorSuccess = 0;
    private const uint ErrorInvalidData = 13;
    private const int InterfaceListHeaderBytes =
        sizeof(uint) + sizeof(uint);
    private const int MaximumInterfaceCount = 4_096;

    internal static NativeWlanMediaStreamingApi Instance { get; } =
        new();

    private NativeWlanMediaStreamingApi()
    {
    }

    public uint OpenHandle(out nint clientHandle)
    {
        return WlanOpenHandle(
            WlanClientVersion,
            0,
            out _,
            out clientHandle);
    }

    public uint EnumerateConnectedInterfaces(
        nint clientHandle,
        out IReadOnlyList<Guid> interfaceIds)
    {
        interfaceIds = Array.Empty<Guid>();
        uint result = WlanEnumInterfaces(
            clientHandle,
            0,
            out nint interfaceList);
        if (result != ErrorSuccess)
        {
            return result;
        }

        if (interfaceList == 0)
        {
            return ErrorInvalidData;
        }

        try
        {
            int interfaceCount = Marshal.ReadInt32(interfaceList);
            if (interfaceCount < 0 ||
                interfaceCount > MaximumInterfaceCount)
            {
                return ErrorInvalidData;
            }

            int itemSize = Marshal.SizeOf<WlanInterfaceInfo>();
            var connectedInterfaces =
                new List<Guid>(interfaceCount);
            for (int index = 0; index < interfaceCount; index++)
            {
                int itemOffset = checked(
                    InterfaceListHeaderBytes +
                    (index * itemSize));
                nint itemPointer =
                    nint.Add(interfaceList, itemOffset);
                WlanInterfaceInfo item =
                    Marshal.PtrToStructure<WlanInterfaceInfo>(
                        itemPointer);
                if (item.State == WlanInterfaceState.Connected)
                {
                    connectedInterfaces.Add(item.InterfaceGuid);
                }
            }

            interfaceIds = connectedInterfaces;
            return ErrorSuccess;
        }
        finally
        {
            WlanFreeMemory(interfaceList);
        }
    }

    public uint SetMediaStreamingMode(
        nint clientHandle,
        Guid interfaceId,
        bool enabled)
    {
        int value = enabled ? 1 : 0;
        return WlanSetInterface(
            clientHandle,
            ref interfaceId,
            WlanInterfaceOpcode.MediaStreamingMode,
            sizeof(int),
            ref value,
            0);
    }

    public uint CloseHandle(nint clientHandle)
    {
        return WlanCloseHandle(clientHandle, 0);
    }

    private enum WlanInterfaceOpcode
    {
        MediaStreamingMode = 3
    }

    private enum WlanInterfaceState
    {
        NotReady = 0,
        Connected = 1,
        AdHocNetworkFormed = 2,
        Disconnecting = 3,
        Disconnected = 4,
        Associating = 5,
        Discovering = 6,
        Authenticating = 7
    }

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        internal Guid InterfaceGuid;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        internal string Description;

        internal WlanInterfaceState State;
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(
        uint clientVersion,
        nint reserved,
        out uint negotiatedVersion,
        out nint clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(
        nint clientHandle,
        nint reserved,
        out nint interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanSetInterface(
        nint clientHandle,
        ref Guid interfaceId,
        WlanInterfaceOpcode opcode,
        int dataSize,
        ref int data,
        nint reserved);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(nint memory);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(
        nint clientHandle,
        nint reserved);
}
