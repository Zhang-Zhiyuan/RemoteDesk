namespace RemoteDesk;

internal sealed record RelayDirectEndpoint(string DeviceId, string MachineName, string Platform,
    string Address, int Port, bool SupportsDesktop)
{
    internal static RelayDirectEndpoint[] FromDirectory(IReadOnlyList<RelayOnlineDevice> devices, string? localId)
    {
        var endpoints = new List<RelayDirectEndpoint>();
        foreach (var device in devices.OrderBy(d => d.DeviceId, StringComparer.Ordinal))
        {
            if (RemoteDeviceIdentity.Normalize(device.DeviceId) is not string id || RemoteDeviceIdentity.Same(id, localId) ||
                device.DirectPort is < 1 or > 65535 || (!device.SupportsDesktop && !device.SupportsSsh)) continue;
            string name = string.IsNullOrWhiteSpace(device.OriginalMachineName) ? device.MachineName : device.OriginalMachineName;
            foreach (string address in RelayAddressReport.Normalize(device.DirectAddresses))
                endpoints.Add(new(id, name, device.Platform, address, device.DirectPort, device.SupportsDesktop));
        }
        // Container bridges and private addresses can be identical on unrelated computers.
        // A LAN discovery reply can identify the actual device there independently.
        var conflicts = endpoints.GroupBy(e => (e.Address, e.Port))
            .Where(group => group.Select(e => e.DeviceId).Distinct().Skip(1).Any()).Select(group => group.Key).ToHashSet();
        return endpoints.Where(e => !conflicts.Contains((e.Address, e.Port))).ToArray();
    }
}
