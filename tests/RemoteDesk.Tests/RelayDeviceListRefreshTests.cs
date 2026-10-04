using Xunit;

namespace RemoteDesk.Tests;

public sealed class RelayDeviceListRefreshTests
{
    private const string LocalId = "ab93e876-9cb8-448d-b8e4-7ff3c5bd0100";

    [Fact]
    public void RepeatedDirectoryRefreshKeepsNativeRowsSelectionAndFocus()
    {
        using var list = CreateList();
        var devices = Enumerable.Range(1, 40).Select(Device).ToArray();
        MainForm.UpdateRelayDeviceList(list, devices, LocalId);
        ListViewItem[] rows = list.Items.Cast<ListViewItem>().ToArray();
        rows[19].Selected = true;
        rows[19].Focused = true;
        list.TopItem = rows[15];
        var top = list.TopItem;
        int selectionChanges = 0;
        list.SelectedIndexChanged += (_, _) => selectionChanges++;
        for (int i = 0; i < 20; i++)
            MainForm.UpdateRelayDeviceList(list, devices.Select(d => d with { LastSeenSeconds = i }).ToArray(), LocalId);

        for (int i = 0; i < rows.Length; i++) Assert.Same(rows[i], list.Items[i]);
        Assert.Same(rows[19], Assert.Single(list.SelectedItems.Cast<ListViewItem>()));
        Assert.Same(rows[19], list.FocusedItem);
        Assert.Same(top, list.TopItem);
        Assert.Equal(0, selectionChanges);
        Assert.Equal("19 秒前", rows[0].SubItems[4].Text);
    }

    [Fact]
    public void RefreshKeepsSurvivingRowsWhenDevicesAreAddedRemovedOrReordered()
    {
        using var list = CreateList();
        var first = Device(1);
        var second = Device(2);
        var third = Device(3);
        MainForm.UpdateRelayDeviceList(list, [first, second, third], LocalId);
        var kept = list.Items[1];
        var reordered = list.Items[2];
        kept.Selected = true;
        kept.Focused = true;
        MainForm.UpdateRelayDeviceList(list, [third, Device(4), second], LocalId);
        Assert.Equal(3, list.Items.Count);
        Assert.Same(reordered, list.Items[0]);
        Assert.Same(kept, list.Items[2]);
        Assert.Same(kept, Assert.Single(list.SelectedItems.Cast<ListViewItem>()));
        Assert.Same(kept, list.FocusedItem);
        Assert.DoesNotContain(list.Items.Cast<ListViewItem>(), item => ReferenceEquals(item.Tag, first));
    }

    [Fact]
    public void RefreshedRowUsesLatestMetadataAndLocalIdentity()
    {
        using var list = CreateList();
        var first = Device(1);
        MainForm.UpdateRelayDeviceList(list, [first], LocalId);
        var item = list.Items[0];
        var changed = first with
        {
            MachineName = "新共享名称", Platform = "Linux", BuildStamp = "20261004120000", Busy = true,
            LastSeenSeconds = 8, DirectAddresses = ["2001:db8::2"], DirectPort = 40565,
            CanRename = true, OriginalMachineName = "original", SharedName = "新共享名称"
        };
        MainForm.UpdateRelayDeviceList(list, [changed], LocalId);
        Assert.Same(item, list.Items[0]);
        Assert.Same(changed, item.Tag);
        Assert.Equal("新共享名称", item.Text);
        Assert.Equal("Linux", item.SubItems[1].Text);
        Assert.Equal(changed.BuildDisplay, item.SubItems[2].Text);
        Assert.Equal(changed.StatusText, item.SubItems[3].Text);
        Assert.Equal(changed.AddressDisplay, item.SubItems[5].Text);
        Assert.Contains("[2001:db8::2]:40565", item.ToolTipText);

        MainForm.UpdateRelayDeviceList(list, [changed], first.DeviceId);
        Assert.Same(item, list.Items[0]);
        Assert.EndsWith("（本机）", item.Text);
        Assert.Equal("本机（不可远控桌面）", item.SubItems[3].Text);
    }

    [Fact]
    public void EquivalentGuidFormattingRetainsTheRowAndUpdatesItsMetadata()
    {
        using var list = CreateList();
        var device = Device(1);
        MainForm.UpdateRelayDeviceList(list, [device], LocalId);
        var row = list.Items[0];
        var changed = device with { DeviceId = Guid.Parse(device.DeviceId).ToString("B").ToUpperInvariant() };
        MainForm.UpdateRelayDeviceList(list, [changed], LocalId);
        Assert.Same(row, list.Items[0]);
        Assert.Same(changed, row.Tag);
    }

    [Fact]
    public void DuplicateIncomingIdentitiesNeverReuseOneNativeRowTwice()
    {
        using var list = CreateList();
        var device = Device(1);
        MainForm.UpdateRelayDeviceList(list, [device], LocalId);
        MainForm.UpdateRelayDeviceList(list, [device, device], LocalId);
        Assert.Equal(2, list.Items.Count);
        Assert.NotSame(list.Items[0], list.Items[1]);
    }

    [Fact]
    public void DeviceGoingOfflineClearsSelectionWithoutSelectingAnotherDevice()
    {
        using var list = CreateList();
        var first = Device(1);
        var second = Device(2);
        MainForm.UpdateRelayDeviceList(list, [first, second], LocalId);
        list.Items[0].Selected = true;
        list.Items[0].Focused = true;
        MainForm.UpdateRelayDeviceList(list, [second], LocalId);
        Assert.Empty(list.SelectedItems);
        MainForm.UpdateRelayDeviceList(list, [], LocalId);
        Assert.Empty(list.Items);
    }

    private static ListView CreateList()
    {
        var list = new ListView { View = View.Details, MultiSelect = false, HideSelection = false, Size = new Size(600, 180) };
        for (int i = 0; i < 6; i++) list.Columns.Add("Column " + i, 140);
        _ = list.Handle; // Native list state; no visible window, network or user settings.
        return list;
    }

    private static RelayOnlineDevice Device(int number) =>
        new($"00000000-0000-0000-0000-{number:000000000000}", "Device " + number, "Windows", "20261003120615", false, 0);
}
