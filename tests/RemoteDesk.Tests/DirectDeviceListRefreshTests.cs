using Xunit;

namespace RemoteDesk.Tests;

public sealed class DirectDeviceListRefreshTests
{
    [Theory]
    [InlineData("name")]
    [InlineData("remark")]
    [InlineData("identity")]
    [InlineData("address")]
    [InlineData("port")]
    [InlineData("target")]
    [InlineData("platform")]
    [InlineData("capabilities")]
    [InlineData("version")]
    [InlineData("last-connected")]
    public void SnapshotComparisonIncludesEverySavedRowField(string field)
    {
        var saved = new SavedRemoteDevice { Address = "192.0.2.1", MachineName = "Computer",
            DeviceId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa" };
        var before = MainForm.RemoteDeviceListItem.FromSaved(saved);
        switch (field)
        {
            case "name": saved.MachineName = "New name"; break;
            case "remark": saved.Remark = "New remark"; break;
            case "identity": saved.DeviceId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"; break;
            case "address": saved.Address = "2001:db8::1"; break;
            case "port": saved.Port = 40565; break;
            case "target": saved.CaptureTarget = "Second monitor"; break;
            case "platform": saved.Platform = RemoteDevicePlatforms.Android; break;
            case "capabilities": saved.Capabilities = RemoteDeviceCapabilities.RemoteDesktop; break;
            case "version": saved.BuildStamp = "20261004123456"; break;
            case "last-connected": saved.LastConnectedAt = DateTimeOffset.UnixEpoch; break;
        }
        Assert.False(before.HasSameSnapshot(MainForm.RemoteDeviceListItem.FromSaved(saved)));
    }

    [Fact]
    public void SnapshotComparisonIncludesDiscoveredActionAvailability()
    {
        var host = new DiscoveredHost("Phone", "192.0.2.1", 56565, "Screen", false, false,
            RemoteDevicePlatforms.Android, RemoteDeviceCapabilities.RemoteDesktop);
        var before = MainForm.RemoteDeviceListItem.FromDiscovered(host, null);
        Assert.True(before.HasSameSnapshot(MainForm.RemoteDeviceListItem.FromDiscovered(host, null)));
        Assert.False(before.HasSameSnapshot(MainForm.RemoteDeviceListItem.FromDiscovered(host with { IsHostRunning = true }, null)));
        Assert.False(before.HasSameSnapshot(MainForm.RemoteDeviceListItem.FromDiscovered(host with { CanRemoteStart = true }, null)));
        Assert.False(before.HasSameSnapshot(MainForm.RemoteDeviceListItem.FromDiscovered(host, new SavedRemoteDevice())));
    }

    [Fact]
    public void IdenticalRefreshRetainsNativeRowsAndScrollWithoutSelectionEvents()
    {
        using var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        using var list = CreateList();
        var devices = Devices();
        MainForm.UpdateDirectDeviceLists(combo, list, devices, devices[19]);
        list.TopIndex = 14;
        int top = list.TopIndex;
        int changes = 0;
        combo.SelectedIndexChanged += (_, _) => changes++;
        list.SelectedIndexChanged += (_, _) => changes++;
        for (int i = 0; i < 25; i++)
        {
            var refreshed = Devices();
            MainForm.UpdateDirectDeviceLists(combo, list, refreshed, refreshed[19]);
        }
        Assert.Equal(0, changes);
        Assert.Equal(top, list.TopIndex);
        Assert.Same(devices[19], list.SelectedItem);
        Assert.Same(list.SelectedItem, combo.SelectedItem);
        for (int i = 0; i < devices.Length; i++) Assert.Same(devices[i], list.Items[i]);
    }

    [Fact]
    public void UpdatingMetadataReplacesOnlyChangedRowsAndKeepsScroll()
    {
        using var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        using var list = CreateList();
        var devices = Devices();
        MainForm.UpdateDirectDeviceLists(combo, list, devices, devices[19]);
        list.TopIndex = 14;
        int top = list.TopIndex;
        var refreshed = Devices();
        refreshed[19] = MainForm.RemoteDeviceListItem.FromDiscovered(new DiscoveredHost(
            "Renamed Android", "2001:db8::20", 40565, "Waiting for permission", false, false,
            RemoteDevicePlatforms.Android, RemoteDeviceCapabilities.None,
            DeviceId: devices[19].DeviceId), null);
        MainForm.UpdateDirectDeviceLists(combo, list, refreshed, refreshed[19]);
        Assert.Equal(top, list.TopIndex);
        Assert.Same(devices[0], list.Items[0]);
        Assert.Same(refreshed[19], list.SelectedItem);
        Assert.Same(list.SelectedItem, combo.SelectedItem);
        Assert.False(((MainForm.RemoteDeviceListItem)list.SelectedItem!).CanOpenViewerNow);
    }

    [Fact]
    public void InsertedRowsKeepThePreviouslyVisibleDeviceAtTheTop()
    {
        using var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        using var list = CreateList();
        var devices = Devices();
        MainForm.UpdateDirectDeviceLists(combo, list, devices, devices[19]);
        list.TopIndex = 14;
        var previousTop = (MainForm.RemoteDeviceListItem)list.Items[list.TopIndex];
        var added = MainForm.RemoteDeviceListItem.FromSaved(new SavedRemoteDevice { Address = "192.0.2.150" });
        MainForm.UpdateDirectDeviceLists(combo, list, [added, .. devices], devices[19]);
        Assert.Equal(previousTop.DeviceId, ((MainForm.RemoteDeviceListItem)list.Items[list.TopIndex]).DeviceId);
        Assert.Same(devices[19], list.SelectedItem);
    }

    [Fact]
    public void EmptyListClearsBothSelectors()
    {
        using var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        using var list = CreateList();
        var devices = Devices();
        MainForm.UpdateDirectDeviceLists(combo, list, devices, devices[0]);
        MainForm.UpdateDirectDeviceLists(combo, list, [], null);
        Assert.Empty(list.Items);
        Assert.Empty(combo.Items);
        Assert.Null(list.SelectedItem);
        Assert.Null(combo.SelectedItem);
    }

    private static MainForm.RemoteDeviceListItem[] Devices() => Enumerable.Range(1, 40)
        .Select(i => MainForm.RemoteDeviceListItem.FromSaved(new SavedRemoteDevice {
            Address = $"192.0.2.{i}", MachineName = "Device " + i, Port = 56565,
            DeviceId = $"00000000-0000-0000-0000-{i:000000000000}" })).ToArray();

    private static ListBox CreateList()
    {
        var list = new ListBox { Size = new Size(600, 160), IntegralHeight = false };
        _ = list.Handle; // Unshown real Win32 list; no discovery or saved configuration.
        return list;
    }
}
