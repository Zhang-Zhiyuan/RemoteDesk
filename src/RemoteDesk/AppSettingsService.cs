using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteDesk;

internal sealed class RemoteDeskSettings
{
    public int SchemaVersion { get; set; }

    public AppBehaviorSettings App { get; set; } = new();

    public HostSettings Host { get; set; } = new();

    public ViewerSettings Viewer { get; set; } = new();

    public RelaySettings Relay { get; set; } = new();
}

internal sealed class AppBehaviorSettings
{
    public bool StartWithWindows { get; set; }

    public bool MinimizeToTray { get; set; } = true;
}

internal sealed class HostSettings
{
    public int Port { get; set; } = Protocol.DefaultPort;

    public int Fps { get; set; } = 30;

    public int JpegQuality { get; set; } = 85;

    public int ScalePercent { get; set; } = 100;

    public bool AdaptiveQuality { get; set; } = true;

    public bool AutoStart { get; set; } = true;

    public bool AllowRemoteStart { get; set; }

    public string? CaptureTargetId { get; set; }

    public string? ProtectedPassword { get; set; }
}

internal sealed class ViewerSettings
{
    public bool AutoDetectPort { get; set; } = true;
    public string? Host { get; set; }

    public int Port { get; set; } = Protocol.DefaultPort;

    public bool PreferH264 { get; set; } = true;

    public ViewerVideoMode VideoMode { get; set; } = ViewerVideoMode.Automatic;

    public string? CaptureTargetId { get; set; }

    public string? ProtectedPassword { get; set; }

    public List<SavedRemoteDevice> RecentDevices { get; set; } = [];
}

internal sealed class SavedRemoteDevice
{
    public string? DeviceId { get; set; }
    public string? ProtectedPassword { get; set; }
    public bool AutoDetectPort { get; set; } = true;
    public string? MachineName { get; set; }

    public string? Remark { get; set; }

    public string? Address { get; set; }

    public int Port { get; set; } = Protocol.DefaultPort;

    public string? CaptureTarget { get; set; }

    public string? Platform { get; set; }

    public RemoteDeviceCapabilities Capabilities { get; set; }

    public string? BuildStamp { get; set; }

    public DateTimeOffset LastConnectedAt { get; set; }
}

internal sealed class RelaySettings
{
    public const int DefaultRelayPort = 56567;

    public string? ServerAddress { get; set; }

    public int RelayPort { get; set; } = DefaultRelayPort;

    public int SshPort { get; set; } = 22;

    public string? AdminUsername { get; set; }

    public bool RegisterThisDevice { get; set; } = true;

    public bool OptimizeNetworkRoute { get; set; } = true;

    public ViewerVideoMode VideoMode { get; set; } =
        ViewerVideoMode.Automatic;

    public string? ProtectedViewerPassword { get; set; }

    public List<RelayDeviceKey> DeviceKeys { get; set; } = [];

    public string DeviceId { get; set; } = Guid.NewGuid().ToString("D");

    public string? ProtectedAccessToken { get; set; }

    public string? TlsCertificateSha256 { get; set; }

    public string? SshHostKeySha256 { get; set; }
}

internal readonly record struct SettingsSaveResult(bool Success, string? ErrorMessage = null);

internal sealed class AppSettingsService
{
    internal const int CurrentSettingsSchemaVersion = 4;
    internal const int MaxSavedDeviceRemarkLength = 80;
    private const int MinUserPort = 1024;
    private const int MaxRecentRemoteDevices = 20;
    private const int MaxSettingsTextLength = 256;
    private static readonly byte[] SecretEntropy = Encoding.UTF8.GetBytes("RemoteDesk.HostPassword.v1");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public AppSettingsService() : this(GetDefaultSettingsPath())
    {
    }

    internal AppSettingsService(string settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath))
        {
            throw new ArgumentException("配置路径不能为空。", nameof(settingsPath));
        }

        _settingsPath = settingsPath;
    }

    private static string GetDefaultSettingsPath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = AppContext.BaseDirectory;
        }

        return Path.Combine(appData, "RemoteDesk", "settings.json");
    }

    public RemoteDeskSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return CreateDefaultSettings();
            }

            string json = File.ReadAllText(_settingsPath);
            RemoteDeskSettings? settings =
                JsonSerializer.Deserialize<RemoteDeskSettings>(json, JsonOptions);
            MigrateSettings(settings);
            return NormalizeSettings(settings);
        }
        catch (JsonException)
        {
            TryMoveCorruptSettings();
            return CreateDefaultSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return CreateDefaultSettings();
        }
    }

    private static RemoteDeskSettings CreateDefaultSettings()
    {
        var settings = new RemoteDeskSettings();
        settings.SchemaVersion = CurrentSettingsSchemaVersion;
        return NormalizeSettings(settings);
    }

    private static void MigrateSettings(
        RemoteDeskSettings? settings)
    {
        if (settings is null)
        {
            return;
        }

        if (settings.SchemaVersion < 2 &&
            settings.Host is not null)
        {
            // AutoStart was previously introduced with an opt-in false
            // default. Migrate that release once so existing installations
            // resume their host after login and updater restarts. A user who
            // disables it after this migration keeps that explicit choice.
            settings.Host.AutoStart = true;
        }

        if (settings.SchemaVersion < 3 &&
            settings.Viewer is not null &&
            settings.Viewer.VideoMode ==
                ViewerVideoMode.ForceH264)
        {
            // Older builds exposed ForceH264 as if it were a quality preset.
            // On a host without a usable hardware encoder that selection has
            // no compatible frame path and deliberately closes the session.
            // Migrate it once to the resilient automatic mode. A user can
            // still explicitly select H.264-only after schema v3.
            settings.Viewer.VideoMode =
                ViewerVideoMode.Automatic;
            settings.Viewer.PreferH264 = true;
        }

        settings.SchemaVersion = Math.Max(
            settings.SchemaVersion,
            CurrentSettingsSchemaVersion);
    }

    public SettingsSaveResult Save(RemoteDeskSettings settings)
    {
        try
        {
            settings = NormalizeSettings(settings);
            string? settingsDirectory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(settingsDirectory))
            {
                Directory.CreateDirectory(settingsDirectory);
            }

            string json = JsonSerializer.Serialize(settings, JsonOptions);
            string temporaryPath = Path.Combine(
                string.IsNullOrWhiteSpace(settingsDirectory) ? "." : settingsDirectory,
                $"{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                File.WriteAllText(temporaryPath, json);
                File.Move(temporaryPath, _settingsPath, overwrite: true);
            }
            finally
            {
                TryDeleteFile(temporaryPath);
            }
            return new SettingsSaveResult(Success: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new SettingsSaveResult(Success: false, ex.Message);
        }
    }

    public static string? ProtectSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return null;
        }

        try
        {
            byte[] protectedBytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(secret),
                SecretEntropy,
                DataProtectionScope.CurrentUser);

            return Convert.ToBase64String(protectedBytes);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    public static string UnprotectSecret(string? protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(protectedSecret))
        {
            return string.Empty;
        }

        try
        {
            byte[] secretBytes = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedSecret),
                SecretEntropy,
                DataProtectionScope.CurrentUser);

            return Encoding.UTF8.GetString(secretBytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return string.Empty;
        }
    }

    internal static RemoteDeskSettings NormalizeSettings(RemoteDeskSettings? settings)
    {
        settings ??= new RemoteDeskSettings();
        settings.App ??= new AppBehaviorSettings();
        settings.Host ??= new HostSettings();
        settings.Viewer ??= new ViewerSettings();
        settings.Relay ??= new RelaySettings();
        settings.SchemaVersion = Math.Max(
            settings.SchemaVersion,
            CurrentSettingsSchemaVersion);

        settings.Host.Port = NormalizePort(settings.Host.Port);
        settings.Host.Fps = Math.Clamp(settings.Host.Fps, 1, 60);
        settings.Host.JpegQuality = Math.Clamp(settings.Host.JpegQuality, 30, 90);
        settings.Host.ScalePercent = NormalizeScalePercent(settings.Host.ScalePercent);
        settings.Host.CaptureTargetId = NormalizeOptionalText(settings.Host.CaptureTargetId);

        settings.Viewer.Host = NormalizeOptionalText(settings.Viewer.Host);
        settings.Viewer.Port = NormalizePort(settings.Viewer.Port);
        settings.Viewer.CaptureTargetId = NormalizeOptionalText(settings.Viewer.CaptureTargetId);
        settings.Viewer.RecentDevices ??= [];
        settings.Viewer.RecentDevices = NormalizeRecentDevices(settings.Viewer.RecentDevices);
        settings.Viewer.VideoMode = NormalizeViewerVideoMode(settings.Viewer.VideoMode, settings.Viewer.PreferH264);

        settings.Relay.ServerAddress =
            NormalizeOptionalText(settings.Relay.ServerAddress);
        settings.Relay.RelayPort = NormalizeRelayPort(
            settings.Relay.RelayPort);
        settings.Relay.SshPort = settings.Relay.SshPort is > 0 and <= IPEndPoint.MaxPort
            ? settings.Relay.SshPort
            : 22;
        settings.Relay.AdminUsername =
            NormalizeOptionalText(settings.Relay.AdminUsername);
        settings.Relay.DeviceId = Guid.TryParse(
                settings.Relay.DeviceId,
                out Guid deviceId)
            ? deviceId.ToString("D")
            : Guid.NewGuid().ToString("D");
        settings.Relay.VideoMode = NormalizeViewerVideoMode(
            settings.Relay.VideoMode,
            legacyPreferH264: true);
        settings.Relay.TlsCertificateSha256 =
            NormalizeOptionalText(settings.Relay.TlsCertificateSha256);
        settings.Relay.SshHostKeySha256 =
            NormalizeOptionalText(settings.Relay.SshHostKeySha256);
        settings.Relay.DeviceKeys = RelayDeviceKeys.Normalize(settings.Relay.DeviceKeys);
        return settings;
    }

    private static int NormalizePort(int port)
    {
        return port is >= MinUserPort and <= IPEndPoint.MaxPort ? port : Protocol.DefaultPort;
    }

    private static int NormalizeRelayPort(int port)
    {
        return port is > 0 and <= IPEndPoint.MaxPort
            ? port
            : RelaySettings.DefaultRelayPort;
    }

    private static int NormalizeScalePercent(int scalePercent)
    {
        return scalePercent switch
        {
            50 or 75 or 100 or
                ScreenCaptureService.QhdMaximumScaleMode =>
                scalePercent,
            _ => 100
        };
    }

    private static string? NormalizeOptionalText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length <= MaxSettingsTextLength ? trimmed : trimmed[..MaxSettingsTextLength];
    }

    private static List<SavedRemoteDevice> NormalizeRecentDevices(IEnumerable<SavedRemoteDevice?> devices)
    {
        var normalized = new List<SavedRemoteDevice>();
        foreach (SavedRemoteDevice? device in devices
            .Where(device => device is not null)
            .OrderByDescending(device => device!.LastConnectedAt))
        {
            string? address = NormalizeOptionalText(device!.Address);
            if (address is null || device.Port <= 0 || device.Port > IPEndPoint.MaxPort)
            {
                continue;
            }

            SavedRemoteDevice? retainedDevice = normalized.FirstOrDefault(item => RemoteDeviceIdentity.Same(item.DeviceId, device.DeviceId)) ??
                normalized.FirstOrDefault(item => item.Port == device.Port && string.Equals(item.Address, address, StringComparison.OrdinalIgnoreCase) &&
                    !RemoteDeviceIdentity.Conflicts(item.DeviceId, device.DeviceId));
            if (retainedDevice is not null)
            {
                if (string.IsNullOrWhiteSpace(
                        retainedDevice.Remark) &&
                    !string.IsNullOrWhiteSpace(
                        device.Remark))
                {
                    retainedDevice.Remark =
                        NormalizeSavedDeviceRemark(
                            device.Remark);
                }

                retainedDevice.ProtectedPassword ??= device.ProtectedPassword;
                retainedDevice.DeviceId ??= RemoteDeviceIdentity.Normalize(device.DeviceId);
                continue;
            }

            if (normalized.Count >= MaxRecentRemoteDevices)
            {
                continue;
            }

            var normalizedDevice = new SavedRemoteDevice
            {
                DeviceId = RemoteDeviceIdentity.Normalize(device.DeviceId),
                ProtectedPassword = device.ProtectedPassword,
                AutoDetectPort = device.AutoDetectPort,
                MachineName = NormalizeOptionalText(device.MachineName),
                Remark = NormalizeSavedDeviceRemark(device.Remark),
                Address = address,
                Port = device.Port,
                CaptureTarget = NormalizeOptionalText(device.CaptureTarget),
                Platform = RemoteDevicePlatforms.Normalize(device.Platform),
                Capabilities = device.Capabilities,
                BuildStamp = RemoteDeskBuildInfo.NormalizeBuildStamp(device.BuildStamp),
                LastConnectedAt = device.LastConnectedAt
            };
            normalized.Add(normalizedDevice);
        }

        return normalized;
    }

    internal static string? NormalizeSavedDeviceRemark(string? value)
    {
        string? normalized = NormalizeOptionalText(value);
        return normalized is null ||
            normalized.Length <= MaxSavedDeviceRemarkLength
                ? normalized
                : normalized[..MaxSavedDeviceRemarkLength];
    }

    private static ViewerVideoMode NormalizeViewerVideoMode(ViewerVideoMode mode, bool legacyPreferH264)
    {
        if (!Enum.IsDefined(mode))
        {
            return legacyPreferH264 ? ViewerVideoMode.Automatic : ViewerVideoMode.StableJpeg;
        }

        if (mode == ViewerVideoMode.Automatic && !legacyPreferH264)
        {
            return ViewerVideoMode.StableJpeg;
        }

        return mode;
    }

    private void TryMoveCorruptSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return;
            }

            string backupPath = $"{_settingsPath}.{DateTime.Now:yyyyMMddHHmmss}.bad";
            File.Move(_settingsPath, backupPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
        }
    }
}
