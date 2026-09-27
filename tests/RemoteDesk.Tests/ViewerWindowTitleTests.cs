using Xunit;

namespace RemoteDesk.Tests;

public sealed class ViewerWindowTitleTests
{
    private static ViewerConnectionSnapshot Direct(string host = "10.0.0.2") =>
        new(host, 4567, "never-display-device-key", ViewerVideoMode.Automatic);

    [Theory]
    [InlineData("10.0.0.2", "10.0.0.2:4567")]
    [InlineData("2001:db8::1", "[2001:db8::1]:4567")]
    [InlineData("[2001:db8::1]", "[2001:db8::1]:4567")]
    public void DirectTitleUsesSessionEndpoint(string host, string endpoint) =>
        Assert.Equal($"RemoteDesk - PC ({endpoint}) · IP 直连", ViewerWindowTitle.Format(
            Direct(host), new("PC", "Windows", RemoteDeviceCapabilities.None)));

    [Theory]
    [InlineData(null, "Original PC")]
    [InlineData("工作站", "工作站")]
    [InlineData("\nA\u202eB\u2028", "AB")]
    public void RelayTitleUsesTargetNotServerOrCredentials(string? sharedName, string expected)
    {
        var route = new RelayConnectionOptions("relay.invalid", 56567, "private-token", "private-pin", "device-a");
        var snapshot = new ViewerConnectionSnapshot(route.ServerAddress, route.Port, "device-key", ViewerVideoMode.Automatic, route);
        Assert.Equal($"RemoteDesk - {expected} · 公网中继", ViewerWindowTitle.Format(
            snapshot, new("Original PC", "Linux", RemoteDeviceCapabilities.None), sharedName));
        Assert.Equal("RemoteDesk - device-a · 公网中继", ViewerWindowTitle.Format(snapshot, null));
    }

    [Fact]
    public void MissingSessionDoesNotPretendToBeConnected() =>
        Assert.Equal("RemoteDesk - 正在连接", ViewerWindowTitle.Format(null, null));

    [Fact]
    public void DeviceNameIsBoundedWithoutSplittingEmoji()
    {
        string name = string.Concat(Enumerable.Repeat("😀", 500));
        string title = ViewerWindowTitle.Format(Direct(), new(name, "Android", RemoteDeviceCapabilities.None));
        Assert.Equal(100, title.EnumerateRunes().Count(rune => rune.Value == 0x1f600));
        Assert.DoesNotContain('\ufffd', title);
    }
}
