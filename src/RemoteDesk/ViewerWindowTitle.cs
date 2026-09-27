using System.Globalization;

namespace RemoteDesk;

internal static class ViewerWindowTitle
{
    internal static string Format(ViewerConnectionSnapshot? connection, RemoteDeviceDescriptor? device, string? sharedName = null)
    {
        if (connection is null) return "RemoteDesk - 正在连接";
        string name = Label(sharedName);
        if (name.Length == 0) name = Label(device?.MachineName);
        if (connection.RelayRoute is { } relay)
        {
            if (name.Length == 0) name = Label(relay.DeviceId);
            return $"RemoteDesk - {name} · 公网中继";
        }
        string host = Label(connection.Host);
        if (host.Contains(':') && !host.StartsWith('[')) host = $"[{host}]";
        string endpoint = $"{host}:{connection.Port}";
        return $"RemoteDesk - {(name.Length == 0 ? endpoint : $"{name} ({endpoint})")} · IP 直连";
    }

    private static string Label(string? value) => string.Concat((value ?? "").EnumerateRunes()
        .Where(rune => System.Text.Rune.GetUnicodeCategory(rune) is not
            (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
        .Take(100).Select(rune => rune.ToString())).Trim();
}
