namespace RemoteDesk;

/// <summary>Application identity, not a hardware fingerprint or authentication secret.</summary>
internal static class RemoteDeviceIdentity
{
    // MainForm supplies the persisted relay/device ID. Isolated host/test instances
    // may use the process-local default without touching the user's settings.
    public static string LocalId { get; set; } = Guid.NewGuid().ToString("D");
    public static string? Normalize(string? value) =>
        Guid.TryParse(value, out Guid id) && id != Guid.Empty ? id.ToString("D") : null;
    public static bool Same(string? first, string? second) =>
        Normalize(first) is string id && id == Normalize(second);
}
