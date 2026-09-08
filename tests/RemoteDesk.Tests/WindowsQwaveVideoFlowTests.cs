using System.Net;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class WindowsQwaveVideoFlowTests
{
    [Fact]
    public void AttachUsesAudioVideoNonAdaptiveFlowAndDisposesOnce()
    {
        var api = new RecordingQwaveApi();
        var destination =
            new IPEndPoint(IPAddress.Parse("192.0.2.25"), 56565);

        WindowsQwaveVideoFlow flow =
            Assert.IsType<WindowsQwaveVideoFlow>(
                WindowsQwaveVideoFlow.TryAttach(
                    new nint(123),
                    destination,
                    api));

        Assert.Equal(
            new[] { "create", "add" },
            api.Calls);
        Assert.Equal(new nint(41), api.AddQosHandle);
        Assert.Equal(new nint(123), api.AddSocketHandle);
        Assert.Equal(destination, api.AddDestination);
        Assert.Equal(
            WindowsQwaveTrafficType.AudioVideo,
            api.AddTrafficType);
        Assert.Equal(
            WindowsQwaveVideoFlow.NonAdaptiveFlowFlag,
            api.AddFlags);
        Assert.Equal(
            0x00000002u,
            WindowsQwaveVideoFlow.NonAdaptiveFlowFlag);

        flow.Dispose();
        flow.Dispose();

        Assert.Equal(
            new[] { "create", "add", "remove", "close" },
            api.Calls);
        Assert.Equal(new nint(41), api.RemoveQosHandle);
        Assert.Equal(new nint(123), api.RemoveSocketHandle);
        Assert.Equal((uint)73, api.RemoveFlowId);
        Assert.Equal((uint)0, api.RemoveFlags);
        Assert.Equal(new nint(41), api.CloseQosHandle);
    }

    [Fact]
    public void AttachCanUseControlForInteractiveInputFlow()
    {
        var api = new RecordingQwaveApi();
        var destination =
            new IPEndPoint(IPAddress.Parse("192.0.2.26"), 56565);

        using WindowsQwaveVideoFlow flow =
            Assert.IsType<WindowsQwaveVideoFlow>(
                WindowsQwaveVideoFlow.TryAttach(
                    new nint(124),
                    destination,
                    WindowsQwaveTrafficType.Control,
                    api));

        Assert.Equal(
            WindowsQwaveTrafficType.Control,
            api.AddTrafficType);
        Assert.Equal(
            5u,
            (uint)WindowsQwaveTrafficType.Control);
        Assert.Equal(
            WindowsQwaveVideoFlow.NonAdaptiveFlowFlag,
            api.AddFlags);
    }

    [Fact]
    public void UnavailableApiIsANoOp()
    {
        var api = new RecordingQwaveApi
        {
            IsAvailable = false
        };

        WindowsQwaveVideoFlow? flow =
            WindowsQwaveVideoFlow.TryAttach(
                new nint(123),
                new IPEndPoint(IPAddress.Loopback, 56565),
                api);

        Assert.Null(flow);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void AddFailureClosesHandleWithoutRemovingFlow()
    {
        var api = new RecordingQwaveApi
        {
            AddResult = false,
            AddedFlowId = 0
        };

        WindowsQwaveVideoFlow? flow =
            WindowsQwaveVideoFlow.TryAttach(
                new nint(123),
                new IPEndPoint(IPAddress.Loopback, 56565),
                api);

        Assert.Null(flow);
        Assert.Equal(
            new[] { "create", "add", "close" },
            api.Calls);
    }

    [Fact]
    public void NativeAddExceptionIsANoOpAndStillClosesHandle()
    {
        var api = new RecordingQwaveApi
        {
            ThrowOnAdd = true
        };

        WindowsQwaveVideoFlow? flow =
            WindowsQwaveVideoFlow.TryAttach(
                new nint(123),
                new IPEndPoint(IPAddress.Loopback, 56565),
                api);

        Assert.Null(flow);
        Assert.Equal(
            new[] { "create", "add", "close" },
            api.Calls);
    }

    [Fact]
    public void RemoveFailureCannotPreventHandleClose()
    {
        var api = new RecordingQwaveApi
        {
            ThrowOnRemove = true
        };
        WindowsQwaveVideoFlow flow =
            Assert.IsType<WindowsQwaveVideoFlow>(
                WindowsQwaveVideoFlow.TryAttach(
                    new nint(123),
                    new IPEndPoint(
                        IPAddress.IPv6Loopback,
                        56565),
                    api));

        flow.Dispose();

        Assert.Equal(
            new[] { "create", "add", "remove", "close" },
            api.Calls);
    }

    [Fact]
    public void InvalidSocketOrDestinationPortIsANoOp()
    {
        var api = new RecordingQwaveApi();

        Assert.Null(WindowsQwaveVideoFlow.TryAttach(
            new nint(-1),
            new IPEndPoint(IPAddress.Loopback, 56565),
            api));
        Assert.Null(WindowsQwaveVideoFlow.TryAttach(
            new nint(123),
            new IPEndPoint(IPAddress.Loopback, 0),
            api));

        Assert.Empty(api.Calls);
    }

    [Fact]
    public void NativeSocketAddressesUseWindowsSockaddrLayout()
    {
        byte[] ipv4 =
            WindowsQwaveVideoFlow.SerializeSocketAddress(
                new IPEndPoint(
                    IPAddress.Parse("192.0.2.25"),
                    56565));
        Assert.Equal(16, ipv4.Length);
        Assert.Equal(
            new byte[]
            {
                2, 0,
                0xdc, 0xf5,
                192, 0, 2, 25,
                0, 0, 0, 0, 0, 0, 0, 0
            },
            ipv4);

        IPAddress ipv6Address =
            IPAddress.Parse("2001:db8::5");
        byte[] ipv6 =
            WindowsQwaveVideoFlow.SerializeSocketAddress(
                new IPEndPoint(ipv6Address, 443));
        Assert.Equal(28, ipv6.Length);
        Assert.Equal(
            new byte[] { 23, 0, 0x01, 0xbb },
            ipv6.AsSpan(0, 4).ToArray());
        Assert.Equal(
            new byte[] { 0, 0, 0, 0 },
            ipv6.AsSpan(4, 4).ToArray());
        Assert.Equal(
            ipv6Address.GetAddressBytes(),
            ipv6.AsSpan(8, 16).ToArray());
        Assert.Equal(
            new byte[] { 0, 0, 0, 0 },
            ipv6.AsSpan(24, 4).ToArray());
    }

    private sealed class RecordingQwaveApi : IWindowsQwaveApi
    {
        public List<string> Calls { get; } = [];

        public bool IsAvailable { get; set; } = true;

        public bool CreateResult { get; set; } = true;

        public bool AddResult { get; set; } = true;

        public uint AddedFlowId { get; set; } = 73;

        public bool ThrowOnAdd { get; set; }

        public bool ThrowOnRemove { get; set; }

        public nint AddQosHandle { get; private set; }

        public nint AddSocketHandle { get; private set; }

        public IPEndPoint? AddDestination { get; private set; }

        public WindowsQwaveTrafficType AddTrafficType
        {
            get;
            private set;
        }

        public uint AddFlags { get; private set; }

        public nint RemoveQosHandle { get; private set; }

        public nint RemoveSocketHandle { get; private set; }

        public uint RemoveFlowId { get; private set; }

        public uint RemoveFlags { get; private set; }

        public nint CloseQosHandle { get; private set; }

        public bool TryCreateHandle(out nint qosHandle)
        {
            Calls.Add("create");
            qosHandle = new nint(41);
            return CreateResult;
        }

        public bool TryAddSocketToFlow(
            nint qosHandle,
            nint socketHandle,
            IPEndPoint destination,
            WindowsQwaveTrafficType trafficType,
            uint flags,
            out uint flowId)
        {
            Calls.Add("add");
            AddQosHandle = qosHandle;
            AddSocketHandle = socketHandle;
            AddDestination = destination;
            AddTrafficType = trafficType;
            AddFlags = flags;
            if (ThrowOnAdd)
            {
                throw new InvalidOperationException(
                    "Synthetic qWAVE add failure.");
            }

            flowId = AddedFlowId;
            return AddResult;
        }

        public bool TryRemoveSocketFromFlow(
            nint qosHandle,
            nint socketHandle,
            uint flowId,
            uint flags)
        {
            Calls.Add("remove");
            RemoveQosHandle = qosHandle;
            RemoveSocketHandle = socketHandle;
            RemoveFlowId = flowId;
            RemoveFlags = flags;
            if (ThrowOnRemove)
            {
                throw new InvalidOperationException(
                    "Synthetic qWAVE remove failure.");
            }

            return true;
        }

        public bool TryCloseHandle(nint qosHandle)
        {
            Calls.Add("close");
            CloseQosHandle = qosHandle;
            return true;
        }
    }
}
