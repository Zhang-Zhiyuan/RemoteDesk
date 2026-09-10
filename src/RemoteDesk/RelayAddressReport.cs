using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace RemoteDesk;

// These are user-selected direct-connection hints, not verified reachability.
// Public NAT source ports are deliberately never advertised as host ports.
internal static class RelayAddressReport
{
    internal const int MaxAddresses = 8;

    internal static IReadOnlyList<string> Normalize(IEnumerable<string?> values) => values.Take(32)
        .Where(IsUsableIPv4).Select(value => value!).Distinct(StringComparer.Ordinal).Take(MaxAddresses).ToArray();

    internal static bool IsUsableIPv4(string? value)
    {
        if (value is null || value.Length is < 7 or > 15 ||
            !IPAddress.TryParse(value, out IPAddress? ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
            ip.ToString() != value) return false; // Reject short, octal, hexadecimal and DNS input.
        byte[] bytes = ip.GetAddressBytes();
        return bytes[0] is > 0 and < 224 && bytes[0] != 127 && !(bytes[0] == 169 && bytes[1] == 254);
    }

    internal static IReadOnlyList<string> LocalAddresses() => Normalize(NetworkUtils.GetLocalIPv4Addresses());

    internal static (IReadOnlyList<string> Addresses, int Port) Parse(JsonElement value)
    {
        if (!value.TryGetProperty("directPort", out var port) || !port.TryGetInt32Safe(out int number) ||
            number is < 1 or > 65535 || !value.TryGetProperty("directAddresses", out var addresses) ||
            addresses.ValueKind != JsonValueKind.Array) return ([], 0);
        var normalized = Normalize(addresses.EnumerateArray().Take(32)
            .Select(address => address.ValueKind == JsonValueKind.String ? address.GetString() : null));
        return (normalized, normalized.Count > 0 ? number : 0);
    }

    private static bool TryGetInt32Safe(this JsonElement value, out int number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number);
    }
}
