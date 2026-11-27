using System.Security.Cryptography;
using System.Text;

namespace RemoteDesk;

internal sealed class RelayDeviceKey
{
    public string Scope { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";
    public override string ToString() => "RelayDeviceKey(<redacted>)";
}

internal static class RelayDeviceKeys
{
    internal static string Scope(RelayConnectionOptions options) =>
        Scope(options.ServerAddress, options.Port, options.TlsCertificateSha256);

    internal static string Scope(string server, int port, string fingerprint) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(server.Trim().TrimEnd('.').ToLowerInvariant() + "\n" + port +
            "\n" + RelayTls.NormalizeFingerprint(fingerprint))));

    internal static string? Find(IEnumerable<RelayDeviceKey> keys, RelayConnectionOptions target) =>
        keys.FirstOrDefault(key => key.Scope == Scope(target) &&
            string.Equals(key.DeviceId, target.DeviceId, StringComparison.OrdinalIgnoreCase))?.ProtectedPassword;

    internal static List<RelayDeviceKey> Remember(IEnumerable<RelayDeviceKey> keys, RelayConnectionOptions target, string protectedPassword)
    {
        string scope = Scope(target);
        var entry = new RelayDeviceKey { Scope = scope, DeviceId = Guid.Parse(target.DeviceId).ToString("D"), ProtectedPassword = protectedPassword };
        return Normalize(new[] { entry }.Concat(keys));
    }

    internal static List<RelayDeviceKey> Normalize(IEnumerable<RelayDeviceKey?>? keys) => (keys ?? [])
        .Where(key => key is not null && key.Scope is { Length: 64 } && key.Scope.All(Uri.IsHexDigit) &&
            Guid.TryParse(key.DeviceId, out Guid id) && id != Guid.Empty && key.ProtectedPassword is { Length: > 0 and <= 16384 })
        .Select(key => new RelayDeviceKey { Scope = key!.Scope.ToUpperInvariant(), DeviceId = Guid.Parse(key.DeviceId).ToString("D"), ProtectedPassword = key.ProtectedPassword })
        .DistinctBy(key => key.Scope + ":" + key.DeviceId).Take(50).ToList();

    internal static bool SetPublication(RelaySettings settings, bool enabled, Func<bool> save)
    {
        bool previous = settings.RegisterThisDevice;
        bool committed = false;
        settings.RegisterThisDevice = enabled;
        try { return committed = save(); }
        finally { if (!committed) settings.RegisterThisDevice = previous; }
    }

    internal static RelaySettings WithoutServerLogin(RelaySettings previous) => new()
    {
        DeviceId = previous.DeviceId,
        DeviceKeys = Normalize(previous.DeviceKeys),
        RegisterThisDevice = previous.RegisterThisDevice,
        OptimizeNetworkRoute = previous.OptimizeNetworkRoute,
        VideoMode = previous.VideoMode
    };
}
