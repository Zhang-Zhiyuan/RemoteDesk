using System.Globalization;

namespace RemoteDesk;

internal sealed record RelayConnectionOptions(
    string ServerAddress,
    int Port,
    string AccessToken,
    string TlsCertificateSha256,
    string DeviceId)
{
    public override string ToString() => $"RelayConnectionOptions({ServerAddress}:{Port}, <redacted>)";

    public RelayConnectionOptions Validate()
    {
        string address = ServerAddress.Trim();
        string token = AccessToken.Trim();
        string certificateFingerprint = RelayTls.NormalizeFingerprint(
            TlsCertificateSha256);
        string deviceId = DeviceId.Trim();

        if (address.Length == 0)
        {
            throw new InvalidOperationException("请先配置公网中继服务器地址。");
        }

        if (Port is <= 0 or > 65535)
        {
            throw new InvalidOperationException("中继端口无效。");
        }

        if (token.Length is < 32 or > 4096)
        {
            throw new InvalidOperationException("服务器登录配置无效，请用 root 密码重新登录服务器。");
        }

        if (certificateFingerprint.Length != 64)
        {
            throw new InvalidOperationException("服务器身份配置无效，请用 root 密码重新登录服务器。");
        }

        if (!Guid.TryParse(deviceId, out Guid parsedDeviceId))
        {
            throw new InvalidOperationException("中继设备标识无效，请重新保存配置。");
        }

        return this with
        {
            ServerAddress = address,
            AccessToken = token,
            TlsCertificateSha256 = certificateFingerprint,
            DeviceId = parsedDeviceId.ToString("D")
        };
    }
}

internal sealed record RelayOnlineDevice(
    string DeviceId,
    string MachineName,
    string Platform,
    string? BuildStamp,
    bool Busy,
    int LastSeenSeconds)
{
    public IReadOnlyList<string> DirectAddresses { get; init; } = [];
    public int DirectPort { get; init; }
    public string AddressDisplay => DirectAddresses.Count > 0
        ? string.Join(" / ", DirectAddresses.Select(address => $"{address}:{DirectPort}"))
        : "未上报（仍可中继连接）";

    public string StatusText => Busy ? "使用中（可挤下线）" : "在线";

    public string BuildDisplay =>
        RemoteDeskBuildInfo.FormatShortBuildStamp(BuildStamp);

    public string LastSeenDisplay => LastSeenSeconds <= 1
        ? "刚刚"
        : string.Create(
            CultureInfo.CurrentCulture,
            $"{LastSeenSeconds} 秒前");
}

internal static class RelayDeviceSelectionPolicy
{
    public static bool IsLocalDevice(RelayOnlineDevice device, string? localDeviceId) =>
        Guid.TryParse(localDeviceId, out Guid localId) &&
        Guid.TryParse(device.DeviceId, out Guid targetId) &&
        localId == targetId;

    public static string? GetConnectionBlockReason(RelayOnlineDevice? device, string? localDeviceId)
    {
        if (device is null)
        {
            return "请先选择一台在线设备。";
        }
        if (!Guid.TryParse(localDeviceId, out _))
        {
            return "本机中继设备标识无效，请重新保存配置。";
        }
        if (!Guid.TryParse(device.DeviceId, out _))
        {
            return "所选中继设备标识无效，请刷新在线设备。";
        }
        return IsLocalDevice(device, localDeviceId)
            ? "这是本机，不能通过中继连接自己。请选择另一台在线设备。"
            : null;
    }
}

internal sealed record RelayProvisionRequest(
    string ServerAddress,
    int SshPort,
    string AdminUsername,
    string AdminPassword,
    int RelayPort,
    string? ExpectedSshHostKeySha256 = null)
{
    public override string ToString() => $"RelayProvisionRequest({ServerAddress}:{SshPort}, <redacted>)";
}

internal sealed record RelayProvisionResult(
    string ServerAddress,
    int RelayPort,
    string AccessToken,
    string TlsCertificateSha256,
    string SshHostKeySha256,
    bool Installed)
{
    public override string ToString() => $"RelayProvisionResult({ServerAddress}:{RelayPort}, <redacted>)";
}

internal sealed class RelayProtocolException : IOException
{
    public RelayProtocolException(string message)
        : base(message)
    {
    }
}

internal sealed class RelayAccessDeniedException : IOException
{
    public RelayAccessDeniedException(string message)
        : base(message)
    {
    }
}
