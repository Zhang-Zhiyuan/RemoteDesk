using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace RemoteDesk;

internal enum WindowsQwaveTrafficType : uint
{
    AudioVideo = 3,
    Control = 5
}

internal interface IWindowsQwaveApi
{
    bool IsAvailable { get; }

    bool TryCreateHandle(out nint qosHandle);

    bool TryAddSocketToFlow(
        nint qosHandle,
        nint socketHandle,
        IPEndPoint destination,
        WindowsQwaveTrafficType trafficType,
        uint flags,
        out uint flowId);

    bool TryRemoveSocketFromFlow(
        nint qosHandle,
        nint socketHandle,
        uint flowId,
        uint flags);

    bool TryCloseHandle(nint qosHandle);
}

/// <summary>
/// Best-effort qWAVE priority marking for the active Windows video socket.
/// qWAVE availability and policy are machine dependent, so failure deliberately
/// leaves the existing UDP transport untouched.
/// </summary>
internal sealed class WindowsQwaveVideoFlow : IDisposable
{
    internal const uint NonAdaptiveFlowFlag = 0x00000002;

    private static readonly IWindowsQwaveApi NativeApi =
        new WindowsQwaveApi();

    private readonly IWindowsQwaveApi _api;
    private readonly nint _qosHandle;
    private readonly nint _socketHandle;
    private readonly uint _flowId;
    private int _disposed;

    private WindowsQwaveVideoFlow(
        IWindowsQwaveApi api,
        nint qosHandle,
        nint socketHandle,
        uint flowId)
    {
        _api = api;
        _qosHandle = qosHandle;
        _socketHandle = socketHandle;
        _flowId = flowId;
    }

    public static WindowsQwaveVideoFlow? TryAttach(
        Socket socket,
        IPEndPoint destination)
    {
        return TryAttach(
            socket,
            destination,
            WindowsQwaveTrafficType.AudioVideo);
    }

    internal static WindowsQwaveVideoFlow? TryAttach(
        Socket socket,
        IPEndPoint destination,
        WindowsQwaveTrafficType trafficType)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(destination);

        try
        {
            if (socket.SafeHandle.IsClosed ||
                socket.SafeHandle.IsInvalid)
            {
                return null;
            }

            return TryAttach(
                socket.SafeHandle.DangerousGetHandle(),
                destination,
                trafficType,
                NativeApi);
        }
        catch (Exception ex) when (IsBestEffortFailure(ex))
        {
            return null;
        }
    }

    internal static WindowsQwaveVideoFlow? TryAttach(
        nint socketHandle,
        IPEndPoint destination,
        IWindowsQwaveApi api)
    {
        return TryAttach(
            socketHandle,
            destination,
            WindowsQwaveTrafficType.AudioVideo,
            api);
    }

    internal static WindowsQwaveVideoFlow? TryAttach(
        nint socketHandle,
        IPEndPoint destination,
        WindowsQwaveTrafficType trafficType,
        IWindowsQwaveApi api)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(api);

        if (!api.IsAvailable ||
            socketHandle == new nint(-1) ||
            destination.Port == 0)
        {
            return null;
        }

        nint qosHandle = 0;
        bool attached = false;
        try
        {
            if (!api.TryCreateHandle(out qosHandle) ||
                qosHandle == 0)
            {
                return null;
            }

            if (!api.TryAddSocketToFlow(
                    qosHandle,
                    socketHandle,
                    destination,
                    trafficType,
                    NonAdaptiveFlowFlag,
                    out uint flowId) ||
                flowId == 0)
            {
                return null;
            }

            attached = true;
            return new WindowsQwaveVideoFlow(
                api,
                qosHandle,
                socketHandle,
                flowId);
        }
        catch (Exception ex) when (IsBestEffortFailure(ex))
        {
            return null;
        }
        finally
        {
            if (!attached && qosHandle != 0)
            {
                TryCloseHandle(api, qosHandle);
            }
        }
    }

    internal static byte[] SerializeSocketAddress(
        IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        IPAddress address = endpoint.Address.IsIPv4MappedToIPv6
            ? endpoint.Address.MapToIPv4()
            : endpoint.Address;
        byte[] addressBytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] socketAddress = new byte[16];
            BinaryPrimitives.WriteUInt16LittleEndian(
                socketAddress.AsSpan(0, 2),
                (ushort)AddressFamily.InterNetwork);
            BinaryPrimitives.WriteUInt16BigEndian(
                socketAddress.AsSpan(2, 2),
                checked((ushort)endpoint.Port));
            addressBytes.CopyTo(
                socketAddress.AsSpan(4, 4));
            return socketAddress;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] socketAddress = new byte[28];
            BinaryPrimitives.WriteUInt16LittleEndian(
                socketAddress.AsSpan(0, 2),
                (ushort)AddressFamily.InterNetworkV6);
            BinaryPrimitives.WriteUInt16BigEndian(
                socketAddress.AsSpan(2, 2),
                checked((ushort)endpoint.Port));
            addressBytes.CopyTo(
                socketAddress.AsSpan(8, 16));
            BinaryPrimitives.WriteUInt32LittleEndian(
                socketAddress.AsSpan(24, 4),
                checked((uint)address.ScopeId));
            return socketAddress;
        }

        throw new ArgumentException(
            "qWAVE only supports IPv4 and IPv6 destinations.",
            nameof(endpoint));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _api.TryRemoveSocketFromFlow(
                _qosHandle,
                _socketHandle,
                _flowId,
                flags: 0);
        }
        catch (Exception ex) when (IsBestEffortFailure(ex))
        {
        }
        finally
        {
            TryCloseHandle(_api, _qosHandle);
        }
    }

    private static void TryCloseHandle(
        IWindowsQwaveApi api,
        nint qosHandle)
    {
        try
        {
            api.TryCloseHandle(qosHandle);
        }
        catch (Exception ex) when (IsBestEffortFailure(ex))
        {
        }
    }

    private static bool IsBestEffortFailure(Exception exception) =>
        exception is not OutOfMemoryException;

    private sealed class WindowsQwaveApi : IWindowsQwaveApi
    {
        private const string QwaveLibrary = "qwave.dll";

        public bool IsAvailable => OperatingSystem.IsWindows();

        public bool TryCreateHandle(out nint qosHandle)
        {
            var version = new QosVersion
            {
                MajorVersion = 1,
                MinorVersion = 0
            };
            return QOSCreateHandle(ref version, out qosHandle);
        }

        public bool TryAddSocketToFlow(
            nint qosHandle,
            nint socketHandle,
            IPEndPoint destination,
            WindowsQwaveTrafficType trafficType,
            uint flags,
            out uint flowId)
        {
            byte[] socketAddress =
                WindowsQwaveVideoFlow.SerializeSocketAddress(
                    destination);
            GCHandle pinnedAddress = GCHandle.Alloc(
                socketAddress,
                GCHandleType.Pinned);
            try
            {
                flowId = 0;
                return QOSAddSocketToFlow(
                    qosHandle,
                    socketHandle,
                    pinnedAddress.AddrOfPinnedObject(),
                    trafficType,
                    flags,
                    ref flowId);
            }
            finally
            {
                pinnedAddress.Free();
            }
        }

        public bool TryRemoveSocketFromFlow(
            nint qosHandle,
            nint socketHandle,
            uint flowId,
            uint flags) =>
            QOSRemoveSocketFromFlow(
                qosHandle,
                socketHandle,
                flowId,
                flags);

        public bool TryCloseHandle(nint qosHandle) =>
            QOSCloseHandle(qosHandle);

        [StructLayout(LayoutKind.Sequential)]
        private struct QosVersion
        {
            public ushort MajorVersion;
            public ushort MinorVersion;
        }

        [DllImport(
            QwaveLibrary,
            EntryPoint = "QOSCreateHandle",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QOSCreateHandle(
            ref QosVersion version,
            out nint qosHandle);

        [DllImport(
            QwaveLibrary,
            EntryPoint = "QOSAddSocketToFlow",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QOSAddSocketToFlow(
            nint qosHandle,
            nint socketHandle,
            nint destinationAddress,
            WindowsQwaveTrafficType trafficType,
            uint flags,
            ref uint flowId);

        [DllImport(
            QwaveLibrary,
            EntryPoint = "QOSRemoveSocketFromFlow",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QOSRemoveSocketFromFlow(
            nint qosHandle,
            nint socketHandle,
            uint flowId,
            uint flags);

        [DllImport(
            QwaveLibrary,
            EntryPoint = "QOSCloseHandle",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QOSCloseHandle(nint qosHandle);
    }
}
