namespace RemoteDesk;

// Shared by the GUI and the non-elevated, stdio-only SSH proxy.
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
        string certificateFingerprint = RelayTls.NormalizeFingerprint(TlsCertificateSha256);
        string deviceId = DeviceId.Trim();
        if (address.Length == 0)
            throw new InvalidOperationException("请先配置公网中继服务器地址。");
        if (Port is <= 0 or > 65535)
            throw new InvalidOperationException("中继端口无效。");
        if (token.Length is < 32 or > 4096)
            throw new InvalidOperationException("服务器登录配置无效，请用 root 密码重新登录服务器。");
        if (certificateFingerprint.Length != 64)
            throw new InvalidOperationException("服务器身份配置无效，请用 root 密码重新登录服务器。");
        if (!Guid.TryParse(deviceId, out Guid parsedDeviceId))
            throw new InvalidOperationException("中继设备标识无效，请重新保存配置。");
        return this with
        {
            ServerAddress = address, AccessToken = token,
            TlsCertificateSha256 = certificateFingerprint, DeviceId = parsedDeviceId.ToString("D")
        };
    }
}

internal sealed class RelayProtocolException(string message) : IOException(message);
internal sealed class RelayAccessDeniedException(string message) : IOException(message);
