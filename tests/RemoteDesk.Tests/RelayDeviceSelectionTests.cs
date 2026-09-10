using Xunit;

namespace RemoteDesk.Tests;

public sealed class RelayDeviceSelectionTests
{
    private const string LocalId = "ab93e876-9cb8-448d-b8e4-7ff3c5bd0100";
    private const string RemoteId = "be6f3bcf-a8c6-47d2-bcb1-7340f11a98ce";

    [Theory]
    [InlineData(true, false, true, true, false, true)]
    [InlineData(false, false, true, true, false, false)]
    [InlineData(true, true, true, true, false, false)]
    [InlineData(true, false, false, true, false, false)]
    [InlineData(true, false, true, false, false, false)]
    [InlineData(true, false, true, true, true, false)]
    public void DirectoryPollingRunsOnlyForAnIdleVisibleConfiguredPage(
        bool visible, bool minimized, bool pageSelected,
        bool configured, bool busy, bool expected)
    {
        int configurationReads = 0;
        Assert.Equal(expected, MainForm.ShouldPollRelayDirectory(
            visible, minimized, pageSelected, busy, () =>
            {
                configurationReads++;
                return configured;
            }));
        Assert.Equal(visible && !minimized && pageSelected && !busy ? 1 : 0,
            configurationReads);
    }

    [Theory]
    [InlineData("D")]
    [InlineData("N")]
    [InlineData("B")]
    [InlineData("P")]
    public void SelfConnectionIsBlockedRegardlessOfGuidFormatting(string format)
    {
        string equivalent = " " + Guid.Parse(LocalId).ToString(format).ToUpperInvariant() + " ";
        var device = Device(equivalent);

        Assert.True(RelayDeviceSelectionPolicy.IsLocalDevice(device, LocalId));
        Assert.Contains("不能通过中继连接自己", RelayDeviceSelectionPolicy.GetConnectionBlockReason(device, LocalId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemoteDeviceWithSameMachineNameIsAllowedEvenWhenBusy(bool busy)
    {
        var device = Device(RemoteId) with { Busy = busy };
        Assert.False(RelayDeviceSelectionPolicy.IsLocalDevice(device, LocalId));
        Assert.Null(RelayDeviceSelectionPolicy.GetConnectionBlockReason(device, LocalId));
    }

    [Fact]
    public void MissingSelectionIsBlockedWithActionableMessage()
    {
        Assert.Contains("请先选择", RelayDeviceSelectionPolicy.GetConnectionBlockReason(null, LocalId));
    }

    [Theory]
    [InlineData(null, RemoteId)]
    [InlineData("bad-local", RemoteId)]
    [InlineData(LocalId, "bad-target")]
    public void InvalidIdentityCannotBecomeConnectable(string? localId, string targetId)
    {
        Assert.NotNull(RelayDeviceSelectionPolicy.GetConnectionBlockReason(Device(targetId), localId));
    }

    [Fact]
    public void DirectoryRowLabelsLocalDeviceWithoutChangingItsIdentity()
    {
        var device = Device(LocalId);
        var item = MainForm.CreateRelayDeviceListItem(device, LocalId);
        Assert.EndsWith("（本机）", item.Text);
        Assert.Equal("本机（不可自连）", item.SubItems[3].Text);
        Assert.Same(device, item.Tag);
        Assert.Equal(6, item.SubItems.Count);
        Assert.Contains("未上报", item.SubItems[5].Text);
    }

    [Fact]
    public void DirectoryRowPreservesRemoteBusyStatus()
    {
        var device = Device(RemoteId) with { Busy = true };
        var item = MainForm.CreateRelayDeviceListItem(device, LocalId);
        Assert.Equal(device.MachineName, item.Text);
        Assert.Equal("使用中（可挤下线）", item.SubItems[3].Text);
        Assert.Same(device, item.Tag);
    }

    private static RelayOnlineDevice Device(string id) =>
        new(id, "Same machine name", "Windows", null, false, 0);
}
