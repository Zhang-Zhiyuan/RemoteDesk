namespace RemoteDesk;

internal sealed record RelaySshHostOptions(bool Enabled, int Port, string DeviceKey)
{
    internal static RelaySshHostOptions Disabled { get; } = new(false, 22, "");
    internal bool IsAvailable => Enabled && Port is > 0 and <= 65535 && !string.IsNullOrWhiteSpace(DeviceKey);
    public override string ToString() => $"RelaySshHostOptions(Enabled={Enabled}, Port={Port}, <redacted>)";
}
