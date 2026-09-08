using Xunit;

namespace RemoteDesk.Tests;

public sealed class AppSettingsServiceTests
{
    [Fact]
    public void SaveReportsDirectoryConflictAndCanRetryAfterItIsRemoved()
    {
        using var temp = TemporaryDirectory.Create();
        string path = Path.Combine(temp.Path, "settings.json");
        Directory.CreateDirectory(path);
        var service = new AppSettingsService(path);
        var settings = new RemoteDeskSettings { Host = new HostSettings { Fps = 45 } };

        SettingsSaveResult failed = service.Save(settings);

        Assert.False(failed.Success);
        Assert.False(string.IsNullOrWhiteSpace(failed.ErrorMessage));
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
        Assert.Equal(45, settings.Host.Fps);
        Directory.Delete(path);

        SettingsSaveResult retried = service.Save(settings);

        Assert.True(retried.Success);
        Assert.Null(retried.ErrorMessage);
        Assert.Equal(45, service.Load().Host.Fps);
    }

    [Fact]
    public void SaveReportsParentPathConflictWithoutOverwritingTheFile()
    {
        using var temp = TemporaryDirectory.Create();
        string parent = Path.Combine(temp.Path, "blocked-parent");
        File.WriteAllText(parent, "preserve this file");
        var service = new AppSettingsService(Path.Combine(parent, "settings.json"));

        SettingsSaveResult result = service.Save(new RemoteDeskSettings());

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.Equal("preserve this file", File.ReadAllText(parent));
    }

    [Fact]
    public void LockedSettingsKeepPreviousFileAndPendingDeviceEditsUntilRetry()
    {
        using var temp = TemporaryDirectory.Create();
        string path = Path.Combine(temp.Path, "settings.json");
        var service = new AppSettingsService(path);
        var settings = new RemoteDeskSettings
        {
            Viewer = new ViewerSettings
            {
                RecentDevices =
                [
                    new SavedRemoteDevice { Address = "10.0.0.2", Remark = "旧备注" },
                    new SavedRemoteDevice { Address = "10.0.0.3" }
                ]
            }
        };
        Assert.True(service.Save(settings).Success);
        string original = File.ReadAllText(path);
        Assert.True(MainForm.UpdateSavedDeviceRemark(settings.Viewer.RecentDevices, "10.0.0.2", 56565, "新备注"));
        Assert.True(MainForm.RemoveSavedDevice(settings.Viewer.RecentDevices, "10.0.0.3", 56565));
        settings.Relay.ServerAddress = "pending-relay.example.com";

        using (FileStream locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                SettingsSaveResult failed = service.Save(settings);
                Assert.False(failed.Success);
                Assert.False(string.IsNullOrWhiteSpace(failed.ErrorMessage));
                Assert.Equal(original, File.ReadAllText(path));
                Assert.Equal("新备注", Assert.Single(settings.Viewer.RecentDevices).Remark);
                Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
            }
        }

        Assert.True(service.Save(settings).Success);
        RemoteDeskSettings reloaded = service.Load();
        Assert.Equal("新备注", Assert.Single(reloaded.Viewer.RecentDevices).Remark);
        Assert.Equal("pending-relay.example.com", reloaded.Relay.ServerAddress);
    }

    [Fact]
    public void SaveAndLoadRoundTripsSettings()
    {
        using var temp = TemporaryDirectory.Create();
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        var service = new AppSettingsService(settingsPath);
        var connectedAt = DateTimeOffset.Now;

        service.Save(new RemoteDeskSettings
        {
            App = new AppBehaviorSettings
            {
                StartWithWindows = true,
                MinimizeToTray = false
            },
            Host = new HostSettings
            {
                Port = 45678,
                Fps = 24,
                JpegQuality = 67,
                ScalePercent =
                    ScreenCaptureService.QhdMaximumScaleMode,
                AdaptiveQuality = false,
                AllowRemoteStart = false,
                CaptureTargetId = "display-1"
            },
            Viewer = new ViewerSettings
            {
                Host = "192.0.2.249",
                Port = 45679,
                VideoMode = ViewerVideoMode.ForceH264,
                CaptureTargetId = "android-screen",
                RecentDevices =
                [
                    new SavedRemoteDevice
                    {
                        MachineName = "RemoteDesk-Phone",
                        Remark = "客厅平板",
                        Address = "192.0.2.88",
                        Port = 56565,
                        CaptureTarget = "Android Screen",
                        Platform = RemoteDevicePlatforms.Android,
                        Capabilities = RemoteDeviceCapabilities.RemoteDesktop,
                        BuildStamp = "20260623010203",
                        LastConnectedAt = connectedAt
                    }
                ]
            },
            Relay = new RelaySettings
            {
                ServerAddress = "relay.example.com",
                RelayPort = 45680,
                SshPort = 2222,
                AdminUsername = "relay-admin",
                RegisterThisDevice = false,
                VideoMode = ViewerVideoMode.StableJpeg,
                DeviceId = "cb798c06-a035-459d-9693-a48f0efbf9db",
                ProtectedAccessToken = "protected-token",
                ProtectedViewerPassword = "protected-password",
                TlsCertificateSha256 = new string('A', 64),
                SshHostKeySha256 = "SHA256:test"
            }
        });

        RemoteDeskSettings loaded = service.Load();

        Assert.True(loaded.App.StartWithWindows);
        Assert.False(loaded.App.MinimizeToTray);
        Assert.Equal(45678, loaded.Host.Port);
        Assert.Equal(24, loaded.Host.Fps);
        Assert.True(loaded.Host.AutoStart);
        Assert.Equal(
            ScreenCaptureService.QhdMaximumScaleMode,
            loaded.Host.ScalePercent);
        Assert.False(loaded.Host.AdaptiveQuality);
        Assert.Equal("192.0.2.249", loaded.Viewer.Host);
        Assert.Equal(ViewerVideoMode.ForceH264, loaded.Viewer.VideoMode);
        SavedRemoteDevice device = Assert.Single(loaded.Viewer.RecentDevices);
        Assert.Equal("RemoteDesk-Phone", device.MachineName);
        Assert.Equal("客厅平板", device.Remark);
        Assert.Equal(RemoteDevicePlatforms.Android, device.Platform);
        Assert.Equal("20260623010203", device.BuildStamp);
        Assert.Equal(connectedAt, device.LastConnectedAt);
        Assert.Equal("relay.example.com", loaded.Relay.ServerAddress);
        Assert.Equal(45680, loaded.Relay.RelayPort);
        Assert.Equal(2222, loaded.Relay.SshPort);
        Assert.Equal("relay-admin", loaded.Relay.AdminUsername);
        Assert.False(loaded.Relay.RegisterThisDevice);
        Assert.Equal(ViewerVideoMode.StableJpeg, loaded.Relay.VideoMode);
        Assert.Equal(
            "cb798c06-a035-459d-9693-a48f0efbf9db",
            loaded.Relay.DeviceId);
        Assert.Equal("protected-token", loaded.Relay.ProtectedAccessToken);
        Assert.Equal("protected-password", loaded.Relay.ProtectedViewerPassword);
        Assert.Equal(new string('A', 64), loaded.Relay.TlsCertificateSha256);
        Assert.Equal("SHA256:test", loaded.Relay.SshHostKeySha256);
    }

    [Fact]
    public void SaveSupportsRelativeFileNameInCurrentDirectory()
    {
        using var temp = TemporaryDirectory.Create();
        string originalDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = temp.Path;
            var service = new AppSettingsService("settings.json");

            service.Save(new RemoteDeskSettings
            {
                Viewer = new ViewerSettings
                {
                    Host = "192.0.2.250"
                }
            });

            Assert.True(File.Exists(Path.Combine(temp.Path, "settings.json")));
            Assert.Equal("192.0.2.250", service.Load().Viewer.Host);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    [Fact]
    public void SaveAndLoadPreservesAutomaticFallbackHostPort()
    {
        using var temp = TemporaryDirectory.Create();
        string settingsPath =
            Path.Combine(
                temp.Path,
                "settings.json");
        var service =
            new AppSettingsService(
                settingsPath);

        service.Save(
            new RemoteDeskSettings
            {
                Host = new HostSettings
                {
                    Port = RemotePortPolicy
                        .PreferredFallbackHostPort
                }
            });

        Assert.Equal(
            RemotePortPolicy
                .PreferredFallbackHostPort,
            service.Load().Host.Port);
    }

    [Fact]
    public void LoadMigratesLegacyPreferH264FalseToStableJpegMode()
    {
        using var temp = TemporaryDirectory.Create();
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(
            settingsPath,
            """
            {
              "Viewer": {
                "PreferH264": false
              }
            }
            """);
        var service = new AppSettingsService(settingsPath);

        RemoteDeskSettings settings = service.Load();

        Assert.Equal(ViewerVideoMode.StableJpeg, settings.Viewer.VideoMode);
    }

    [Fact]
    public void LoadMigratesSchemaTwoForcedH264ToResilientAutomaticMode()
    {
        using var temp = TemporaryDirectory.Create();
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(
            settingsPath,
            """
            {
              "SchemaVersion": 2,
              "Viewer": {
                "PreferH264": true,
                "VideoMode": 2
              }
            }
            """);
        var service = new AppSettingsService(settingsPath);

        RemoteDeskSettings settings = service.Load();

        Assert.Equal(
            ViewerVideoMode.Automatic,
            settings.Viewer.VideoMode);
        Assert.True(settings.Viewer.PreferH264);
        Assert.Equal(
            AppSettingsService.CurrentSettingsSchemaVersion,
            settings.SchemaVersion);
    }

    [Fact]
    public void LoadPreservesForcedH264ChosenInCurrentSchema()
    {
        using var temp = TemporaryDirectory.Create();
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(
            settingsPath,
            $$"""
            {
              "SchemaVersion": {{AppSettingsService.CurrentSettingsSchemaVersion}},
              "Viewer": {
                "PreferH264": true,
                "VideoMode": 2
              }
            }
            """);
        var service = new AppSettingsService(settingsPath);

        RemoteDeskSettings settings = service.Load();

        Assert.Equal(
            ViewerVideoMode.ForceH264,
            settings.Viewer.VideoMode);
    }

    [Fact]
    public void SchemaTwoDoesNotRepeatLegacyAutoStartMigration()
    {
        using var temp = TemporaryDirectory.Create();
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(
            settingsPath,
            """
            {
              "SchemaVersion": 2,
              "Host": {
                "AutoStart": false
              }
            }
            """);
        var service = new AppSettingsService(settingsPath);

        RemoteDeskSettings settings = service.Load();

        Assert.False(settings.Host.AutoStart);
    }

    [Fact]
    public void LoadMigratesLegacyHostWithoutAutoStartToRunningBehavior()
    {
        using var temp = TemporaryDirectory.Create();
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(
            settingsPath,
            """
            {
              "Host": {
                "Port": 40565
              }
            }
            """);
        var service = new AppSettingsService(settingsPath);

        RemoteDeskSettings settings = service.Load();

        Assert.True(settings.Host.AutoStart);
        Assert.Equal(
            AppSettingsService.CurrentSettingsSchemaVersion,
            settings.SchemaVersion);
    }

    [Fact]
    public void LoadMigratesPreviousOptInHostAutoStartToEnabled()
    {
        using var temp = TemporaryDirectory.Create();
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(
            settingsPath,
            """
            {
              "Host": {
                "AutoStart": false
              }
            }
            """);
        var service = new AppSettingsService(settingsPath);

        RemoteDeskSettings settings = service.Load();

        Assert.True(settings.Host.AutoStart);
        Assert.Equal(85, settings.Host.JpegQuality);
    }

    [Fact]
    public void LoadPreservesExplicitlyDisabledHostAutoStartAfterMigration()
    {
        using var temp = TemporaryDirectory.Create();
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(
            settingsPath,
            $$"""
            {
              "SchemaVersion": {{AppSettingsService.CurrentSettingsSchemaVersion}},
              "Host": {
                "AutoStart": false
              }
            }
            """);
        var service = new AppSettingsService(settingsPath);

        RemoteDeskSettings settings = service.Load();

        Assert.False(settings.Host.AutoStart);
        Assert.Equal(85, settings.Host.JpegQuality);
    }

    [Fact]
    public void LoadWithoutExistingSettingsDefaultsHostToAutoStart()
    {
        using var temp = TemporaryDirectory.Create();
        var service = new AppSettingsService(
            Path.Combine(temp.Path, "settings.json"));

        RemoteDeskSettings settings = service.Load();

        Assert.True(settings.Host.AutoStart);
        Assert.Equal(85, settings.Host.JpegQuality);
        Assert.Equal(
            AppSettingsService.CurrentSettingsSchemaVersion,
            settings.SchemaVersion);
    }

    [Fact]
    public void LoadMovesCorruptJsonAsideAndReturnsDefaults()
    {
        using var temp = TemporaryDirectory.Create();
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(settingsPath, "{ invalid json");
        var service = new AppSettingsService(settingsPath);

        RemoteDeskSettings settings = service.Load();

        Assert.Equal(Protocol.DefaultPort, settings.Host.Port);
        Assert.True(settings.Host.AutoStart);
        Assert.False(File.Exists(settingsPath));
        Assert.Single(Directory.GetFiles(temp.Path, "settings.json.*.bad"));
    }

    [Fact]
    public void LoadNormalizesNullSections()
    {
        using var temp = TemporaryDirectory.Create();
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(
            settingsPath,
            """
            {
              "App": null,
              "Host": null,
              "Viewer": {
                "RecentDevices": null
              }
            }
            """);
        var service = new AppSettingsService(settingsPath);

        RemoteDeskSettings settings = service.Load();

        Assert.NotNull(settings.App);
        Assert.NotNull(settings.Host);
        Assert.NotNull(settings.Viewer);
        Assert.NotNull(settings.Viewer.RecentDevices);
        Assert.Empty(settings.Viewer.RecentDevices);
        Assert.Equal(Protocol.DefaultPort, settings.Host.Port);
    }

    [Fact]
    public void LoadNormalizesOutOfRangeValuesAndRecentDevices()
    {
        using var temp = TemporaryDirectory.Create();
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        string longName = new('x', 300);
        string longRemark = new('r', 120);
        File.WriteAllText(
            settingsPath,
            $$"""
            {
              "Host": {
                "Port": 80,
                "Fps": 999,
                "JpegQuality": 5,
                "ScalePercent": 25,
                "CaptureTargetId": "  display-1  "
              },
              "Viewer": {
                "Host": "  192.0.2.250  ",
                "Port": 70000,
                "VideoMode": 999,
                "PreferH264": true,
                "CaptureTargetId": "  android-screen  ",
                "RecentDevices": [
                  null,
                  {
                    "MachineName": "old duplicate",
                    "Address": " 192.0.2.10 ",
                    "Port": 56565,
                    "Platform": "Android",
                    "LastConnectedAt": "2026-06-01T00:00:00+00:00"
                  },
                  {
                    "MachineName": "{{longName}}",
                    "Remark": "  {{longRemark}}  ",
                    "Address": "192.0.2.10",
                    "Port": 56565,
                    "CaptureTarget": "  main  ",
                    "Platform": "Linux",
                    "LastConnectedAt": "2026-06-02T00:00:00+00:00"
                  },
                  {
                    "MachineName": "invalid port",
                    "Address": "192.0.2.11",
                    "Port": 70000,
                    "LastConnectedAt": "2026-06-03T00:00:00+00:00"
                  },
                  {
                    "MachineName": "blank address",
                    "Address": "   ",
                    "Port": 56565,
                    "LastConnectedAt": "2026-06-04T00:00:00+00:00"
                  }
                ]
              }
            }
            """);
        var service = new AppSettingsService(settingsPath);

        RemoteDeskSettings settings = service.Load();

        Assert.Equal(Protocol.DefaultPort, settings.Host.Port);
        Assert.Equal(60, settings.Host.Fps);
        Assert.Equal(30, settings.Host.JpegQuality);
        Assert.Equal(100, settings.Host.ScalePercent);
        Assert.Equal("display-1", settings.Host.CaptureTargetId);
        Assert.Equal("192.0.2.250", settings.Viewer.Host);
        Assert.Equal(Protocol.DefaultPort, settings.Viewer.Port);
        Assert.Equal(ViewerVideoMode.Automatic, settings.Viewer.VideoMode);
        Assert.Equal("android-screen", settings.Viewer.CaptureTargetId);

        SavedRemoteDevice device = Assert.Single(settings.Viewer.RecentDevices);
        Assert.Equal("192.0.2.10", device.Address);
        Assert.Equal(56565, device.Port);
        Assert.Equal(RemoteDevicePlatforms.Linux, device.Platform);
        Assert.Equal("main", device.CaptureTarget);
        Assert.Equal(256, device.MachineName!.Length);
        Assert.Equal(
            AppSettingsService.MaxSavedDeviceRemarkLength,
            device.Remark!.Length);
    }

    [Fact]
    public void NormalizeSettingsPreservesRemarkFromOlderDuplicate()
    {
        var settings = new RemoteDeskSettings
        {
            Viewer = new ViewerSettings
            {
                RecentDevices =
                [
                    new SavedRemoteDevice
                    {
                        MachineName = "Current name",
                        Address = "192.0.2.249",
                        Port = 56565,
                        LastConnectedAt =
                            DateTimeOffset.UtcNow
                    },
                    new SavedRemoteDevice
                    {
                        MachineName = "Old name",
                        Remark = "办公室主机",
                        Address = "192.0.2.249",
                        Port = 56565,
                        LastConnectedAt =
                            DateTimeOffset.UtcNow
                                .AddDays(-1)
                    }
                ]
            }
        };

        RemoteDeskSettings normalized =
            AppSettingsService.NormalizeSettings(settings);

        SavedRemoteDevice device =
            Assert.Single(
                normalized.Viewer.RecentDevices);
        Assert.Equal("Current name", device.MachineName);
        Assert.Equal("办公室主机", device.Remark);
    }

    [Fact]
    public void ProtectSecretRoundTripsWithoutPlainText()
    {
        const string password = "local-password-123";

        string? protectedSecret = AppSettingsService.ProtectSecret(password);

        Assert.False(string.IsNullOrWhiteSpace(protectedSecret));
        Assert.NotEqual(password, protectedSecret);
        Assert.Equal(password, AppSettingsService.UnprotectSecret(protectedSecret));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"RemoteDesk.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
