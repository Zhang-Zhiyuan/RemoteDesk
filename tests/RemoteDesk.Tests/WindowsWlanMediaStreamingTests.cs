using System.Net;
using System.Net.NetworkInformation;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class WindowsWlanMediaStreamingTests
{
    [Theory]
    [InlineData("192.0.2.1", "192.0.2.1", true, true)]
    [InlineData("192.0.2.1", "192.0.2.2", true, false)]
    [InlineData("192.0.2.1", "192.0.2.1", false, false)]
    [InlineData("127.0.0.1", "127.0.0.1", true, false)]
    [InlineData("::ffff:192.0.2.1", "192.0.2.1", true, true)]
    [InlineData("::ffff:127.0.0.1", "127.0.0.1", true, false)]
    public void OnlyTheActualWirelessSocketInterfaceIsEligible(string local, string address, bool wireless, bool expected) =>
        Assert.Equal(expected, WindowsWlanMediaStreaming.UsesWirelessInterface(IPAddress.Parse(local),
            wireless ? NetworkInterfaceType.Wireless80211 : NetworkInterfaceType.Ethernet, [IPAddress.Parse(address)]));

    [Fact]
    public void UnusedWirelessInterfacesAreNotAdjusted()
    {
        Guid selected = Guid.NewGuid(), unused = Guid.NewGuid();
        var api = new RecordingWlanApi(selected, unused);
        using (WindowsWlanMediaStreaming.TryAcquire(true, api, selectedInterfaces: new HashSet<Guid> { selected }))
            Assert.Equal([(selected, true)], api.SetCalls);
        Assert.Equal([(selected, true), (selected, false)], api.SetCalls);
        Assert.Equal(1, api.CloseCalls);
        var wiredApi = new RecordingWlanApi(unused);
        using (WindowsWlanMediaStreaming.TryAcquire(true, wiredApi, selectedInterfaces: new HashSet<Guid>())) { }
        Assert.Equal(0, wiredApi.OpenCalls);
        Assert.Empty(wiredApi.SetCalls);
    }

    [Fact]
    public void SuccessfulLeaseBalancesEveryInterfaceAndClosesOnce()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        var api = new RecordingWlanApi(first, second);

        IDisposable lease = WindowsWlanMediaStreaming.TryAcquire(
            isWindows: true,
            api);

        Assert.Equal(
            [(first, true), (second, true)],
            api.SetCalls);
        Assert.Equal(0, api.CloseCalls);

        lease.Dispose();
        lease.Dispose();

        Assert.Equal(
            [
                (first, true),
                (second, true),
                (first, false),
                (second, false)
            ],
            api.SetCalls);
        Assert.Equal(1, api.CloseCalls);
    }

    [Fact]
    public void PartialEnableOnlyRestoresInterfacesThatWereChanged()
    {
        Guid enabled = Guid.NewGuid();
        Guid rejected = Guid.NewGuid();
        var api = new RecordingWlanApi(enabled, rejected);
        api.SetResults[(rejected, true)] = 50;
        var diagnostics = new List<string>();

        using (WindowsWlanMediaStreaming.TryAcquire(
                   isWindows: true,
                   api,
                   diagnostics.Add))
        {
        }

        Assert.Contains((enabled, false), api.SetCalls);
        Assert.DoesNotContain((rejected, false), api.SetCalls);
        Assert.Contains(
            diagnostics,
            message => message.Contains(
                "部分启用",
                StringComparison.Ordinal));
        Assert.Equal(1, api.CloseCalls);
    }

    [Fact]
    public void EnumerationFailureClosesHandleWithoutChangingInterfaces()
    {
        var api = new RecordingWlanApi(Guid.NewGuid())
        {
            EnumerationResult = 1062
        };

        using IDisposable lease =
            WindowsWlanMediaStreaming.TryAcquire(
                isWindows: true,
                api);

        Assert.Empty(api.SetCalls);
        Assert.Equal(1, api.CloseCalls);
    }

    [Fact]
    public void OpenFailureAndNonWindowsPathAreInert()
    {
        var openFailure = new RecordingWlanApi(Guid.NewGuid())
        {
            OpenResult = 5
        };

        using (WindowsWlanMediaStreaming.TryAcquire(
                   isWindows: true,
                   openFailure))
        {
        }

        Assert.Equal(1, openFailure.OpenCalls);
        Assert.Equal(0, openFailure.CloseCalls);
        Assert.Empty(openFailure.SetCalls);

        var nonWindows = new RecordingWlanApi(Guid.NewGuid());
        using (WindowsWlanMediaStreaming.TryAcquire(
                   isWindows: false,
                   nonWindows))
        {
        }

        Assert.Equal(0, nonWindows.OpenCalls);
        Assert.Equal(0, nonWindows.CloseCalls);
        Assert.Empty(nonWindows.SetCalls);
    }

    [Fact]
    public void NativeFailuresAndDiagnosticFailuresNeverEscape()
    {
        Guid interfaceId = Guid.NewGuid();
        var api = new RecordingWlanApi(interfaceId)
        {
            ThrowOnEnable = true
        };

        IDisposable lease = WindowsWlanMediaStreaming.TryAcquire(
            isWindows: true,
            api,
            _ => throw new InvalidOperationException(
                "diagnostic sink failed"));

        lease.Dispose();

        Assert.Equal(1, api.CloseCalls);
    }

    [Fact]
    public void DisposeFailureStillClosesHandleAndIsIdempotent()
    {
        Guid interfaceId = Guid.NewGuid();
        var api = new RecordingWlanApi(interfaceId);
        IDisposable lease = WindowsWlanMediaStreaming.TryAcquire(
            isWindows: true,
            api,
            _ => throw new InvalidOperationException(
                "diagnostic sink failed"));
        api.ThrowOnDisable = true;

        lease.Dispose();
        lease.Dispose();

        Assert.Equal(1, api.CloseCalls);
        Assert.Equal(
            [(interfaceId, true), (interfaceId, false)],
            api.SetCalls);
    }

    private sealed class RecordingWlanApi :
        IWlanMediaStreamingApi
    {
        private readonly Guid[] _interfaceIds;

        internal RecordingWlanApi(params Guid[] interfaceIds)
        {
            _interfaceIds = interfaceIds;
        }

        internal uint OpenResult { get; set; }

        internal uint EnumerationResult { get; set; }

        internal bool ThrowOnEnable { get; set; }

        internal bool ThrowOnDisable { get; set; }

        internal int OpenCalls { get; private set; }

        internal int CloseCalls { get; private set; }

        internal List<(Guid InterfaceId, bool Enabled)> SetCalls
        {
            get;
        } = [];

        internal Dictionary<(Guid InterfaceId, bool Enabled), uint>
            SetResults
        { get; } = [];

        public uint OpenHandle(out nint clientHandle)
        {
            OpenCalls++;
            clientHandle = OpenResult == 0 ? 123 : 0;
            return OpenResult;
        }

        public uint EnumerateConnectedInterfaces(
            nint clientHandle,
            out IReadOnlyList<Guid> interfaceIds)
        {
            interfaceIds = _interfaceIds;
            return EnumerationResult;
        }

        public uint SetMediaStreamingMode(
            nint clientHandle,
            Guid interfaceId,
            bool enabled)
        {
            SetCalls.Add((interfaceId, enabled));
            if (enabled && ThrowOnEnable)
            {
                throw new DllNotFoundException("wlanapi.dll");
            }

            if (!enabled && ThrowOnDisable)
            {
                throw new InvalidOperationException(
                    "driver stopped");
            }

            return SetResults.GetValueOrDefault(
                (interfaceId, enabled));
        }

        public uint CloseHandle(nint clientHandle)
        {
            CloseCalls++;
            return 0;
        }
    }
}
