using System.Net;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class MainFormDiscoveryTests
{
    [Fact]
    public void MergeDiscoveredHostsReplacesMatchingEndpoint()
    {
        var existing = new[]
        {
            new DiscoveredHost(
                "Phone",
                "192.0.2.88",
                56565,
                "Android App 常驻，等待录屏授权",
                IsHostRunning: false,
                CanRemoteStart: false,
                RemoteDevicePlatforms.Android,
                RemoteDeviceCapabilities.None),
            new DiscoveredHost(
                "Desktop",
                "192.0.2.90",
                56565,
                "所有屏幕",
                IsHostRunning: true,
                CanRemoteStart: false,
                RemoteDevicePlatforms.Windows,
                RemoteDeviceCapabilities.RemoteDesktop)
        };
        var updated = new[]
        {
            new DiscoveredHost(
                "Phone",
                "192.0.2.88",
                56565,
                "Android Screen",
                IsHostRunning: true,
                CanRemoteStart: false,
                RemoteDevicePlatforms.Android,
                RemoteDeviceCapabilities.RemoteDesktop)
        };

        IReadOnlyList<DiscoveredHost> merged = MainForm.MergeDiscoveredHosts(existing, updated);

        Assert.Equal(2, merged.Count);
        DiscoveredHost phone = Assert.Single(merged, host => host.Address == "192.0.2.88");
        Assert.True(phone.IsHostRunning);
        Assert.Equal("Android Screen", phone.CaptureTarget);
        Assert.Contains(merged, host => host.Address == "192.0.2.90");
    }

    [Fact]
    public void BuildDiscoveryProbeTargetsIncludesManualAndRecentDevices()
    {
        var localAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "192.0.2.249"
        };
        var recentDevices = new[]
        {
            new SavedRemoteDevice
            {
                MachineName = "OlderDesktop",
                Address = "192.0.2.251",
                Port = 56566,
                LastConnectedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
            },
            new SavedRemoteDevice
            {
                MachineName = "NewestDesktop",
                Address = "192.0.2.252",
                Port = 56567,
                LastConnectedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            }
        };

        IReadOnlyList<DiscoveryProbeTarget> targets = MainForm.BuildDiscoveryProbeTargets(
            " 192.0.2.250 ",
            56565,
            recentDevices,
            localAddresses);

        Assert.Equal(
            [
                new DiscoveryProbeTarget("192.0.2.250", 56565),
                new DiscoveryProbeTarget("192.0.2.252", 56567),
                new DiscoveryProbeTarget("192.0.2.251", 56566)
            ],
            targets);
    }

    [Fact]
    public void BuildDiscoveryProbeTargetsSkipsDuplicatesInvalidEntriesAndLocalDevices()
    {
        var localAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "192.0.2.249"
        };
        var recentDevices = new[]
        {
            new SavedRemoteDevice
            {
                MachineName = "ManualDuplicate",
                Address = "192.0.2.250",
                Port = 56565,
                LastConnectedAt = DateTimeOffset.UtcNow
            },
            new SavedRemoteDevice
            {
                MachineName = "LocalByAddress",
                Address = "192.0.2.249",
                Port = 56565,
                LastConnectedAt = DateTimeOffset.UtcNow
            },
            new SavedRemoteDevice
            {
                MachineName = Environment.MachineName,
                Address = "192.0.2.253",
                Port = 56565,
                LastConnectedAt = DateTimeOffset.UtcNow
            },
            new SavedRemoteDevice
            {
                MachineName = "InvalidPort",
                Address = "192.0.2.254",
                Port = IPEndPoint.MaxPort + 1,
                LastConnectedAt = DateTimeOffset.UtcNow
            },
            new SavedRemoteDevice
            {
                MachineName = "Remote",
                Address = "192.0.2.255",
                Port = 56568,
                LastConnectedAt = DateTimeOffset.UtcNow
            }
        };

        IReadOnlyList<DiscoveryProbeTarget> targets = MainForm.BuildDiscoveryProbeTargets(
            "192.0.2.250",
            56565,
            recentDevices,
            localAddresses);

        Assert.Equal(
            [
                new DiscoveryProbeTarget("192.0.2.250", 56565),
                new DiscoveryProbeTarget("192.0.2.255", 56568)
            ],
            targets);
    }

    [Fact]
    public void BuildDiscoveryProbeTargetsLimitsRecentDevices()
    {
        SavedRemoteDevice[] recentDevices = Enumerable.Range(0, 30)
            .Select(index => new SavedRemoteDevice
            {
                MachineName = $"Desktop-{index}",
                Address = $"192.0.2.{index + 20}",
                Port = 56565,
                LastConnectedAt = DateTimeOffset.UtcNow.AddMinutes(-index)
            })
            .ToArray();

        IReadOnlyList<DiscoveryProbeTarget> targets = MainForm.BuildDiscoveryProbeTargets(
            "192.0.2.10",
            56565,
            recentDevices,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(21, targets.Count);
        Assert.Equal(new DiscoveryProbeTarget("192.0.2.10", 56565), targets[0]);
        Assert.Equal(new DiscoveryProbeTarget("192.0.2.20", 56565), targets[1]);
        Assert.Equal(new DiscoveryProbeTarget("192.0.2.39", 56565), targets[^1]);
    }

    [Fact]
    public void CompatibleDiscoveredPortReplacesSavedLegacyPort()
    {
        var recentDevices =
            new List<SavedRemoteDevice>
            {
                new()
                {
                    MachineName = "Entity",
                    Address = "192.0.2.249",
                    Port = Protocol.DefaultPort,
                    LastConnectedAt =
                        DateTimeOffset.UtcNow
                },
                new()
                {
                    MachineName = "Duplicate",
                    Address = "192.0.2.249",
                    Port = RemotePortPolicy
                        .PreferredFallbackHostPort,
                    Remark = "办公室主机",
                    LastConnectedAt =
                        DateTimeOffset.UtcNow
                            .AddMinutes(-1)
                }
            };
        DiscoveredHost[] discoveredHosts =
        [
            new(
                "Entity",
                "192.0.2.249",
                RemotePortPolicy
                    .PreferredFallbackHostPort,
                "Primary",
                IsHostRunning: true,
                CanRemoteStart: false,
                RemoteDevicePlatforms.Windows,
                RemoteDeviceCapabilities
                    .RemoteDesktop)
        ];

        bool changed =
            MainForm
                .MigrateCompatibleRecentDevicePorts(
                    recentDevices,
                    discoveredHosts);

        Assert.True(changed);
        SavedRemoteDevice migrated =
            Assert.Single(recentDevices);
        Assert.Equal(
            RemotePortPolicy
                .PreferredFallbackHostPort,
            migrated.Port);
        Assert.Equal(
            "办公室主机",
            migrated.Remark);
    }

    [Theory]
    [InlineData(false, 56565, 40565, true)]
    [InlineData(false, 40565, 56565, true)]
    [InlineData(false, 56565, 41234, false)]
    [InlineData(true, 56565, 40565, false)]
    public void ViewerAdoptsOnlyLiveCompatibleDiscoveredPort(
        bool isSavedOnly,
        int currentPort,
        int discoveredPort,
        bool expected)
    {
        bool actual =
            MainForm
                .ShouldAdoptCompatibleDiscoveredPort(
                    "192.0.2.249",
                    currentPort,
                    "192.0.2.249",
                    discoveredPort,
                    isSavedOnly);

        Assert.Equal(
            expected,
            actual);
    }

    [Fact]
    public void SavedDeviceRemarkCanBeChangedClearedAndDeleted()
    {
        var recentDevices =
            new List<SavedRemoteDevice>
            {
                new()
                {
                    MachineName = "Office-PC",
                    Address = "192.0.2.249",
                    Port = 56565,
                    Remark = "旧备注",
                    LastConnectedAt =
                        DateTimeOffset.UtcNow
                }
            };

        Assert.True(
            MainForm.UpdateSavedDeviceRemark(
                recentDevices,
                " 192.0.2.249 ",
                56565,
                "  办公室主机  "));
        Assert.Equal(
            "办公室主机",
            recentDevices[0].Remark);

        Assert.True(
            MainForm.UpdateSavedDeviceRemark(
                recentDevices,
                "192.0.2.249",
                56565,
                "   "));
        Assert.Null(recentDevices[0].Remark);
        Assert.False(
            MainForm.UpdateSavedDeviceRemark(
                recentDevices,
                "192.0.2.250",
                56565,
                "不存在"));

        Assert.True(
            MainForm.RemoveSavedDevice(
                recentDevices,
                "192.0.2.249",
                56565));
        Assert.Empty(recentDevices);
        Assert.False(
            MainForm.RemoveSavedDevice(
                recentDevices,
                "192.0.2.249",
                56565));
    }

    [Fact]
    public void ReconnectingPreservesRemarkAcrossCompatiblePortMigration()
    {
        var recentDevices =
            new List<SavedRemoteDevice>
            {
                new()
                {
                    MachineName = "Old-Name",
                    Address = "192.0.2.249",
                    Port = Protocol.DefaultPort,
                    Remark = "我的工作站",
                    LastConnectedAt =
                        DateTimeOffset.UtcNow
                            .AddDays(-1)
                }
            };
        var reconnected = new SavedRemoteDevice
        {
            MachineName = "New-Name",
            Address = "192.0.2.249",
            Port = RemotePortPolicy
                .PreferredFallbackHostPort,
            LastConnectedAt = DateTimeOffset.UtcNow
        };

        MainForm.UpsertRecentDevice(
            recentDevices,
            reconnected);

        SavedRemoteDevice saved =
            Assert.Single(recentDevices);
        Assert.Same(reconnected, saved);
        Assert.Equal("New-Name", saved.MachineName);
        Assert.Equal("我的工作站", saved.Remark);
        Assert.Equal(
            RemotePortPolicy.PreferredFallbackHostPort,
            saved.Port);
    }
}
