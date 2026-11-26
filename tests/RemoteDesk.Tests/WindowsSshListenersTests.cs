using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class WindowsSshListenersTests
{
    [Theory]
    [InlineData(false, "0.0.0.0", 0, "127.0.0.1")]
    [InlineData(false, "192.0.2.12", 0, "192.0.2.12")]
    [InlineData(true, "::", 0, "::1")]
    [InlineData(true, "fe80::4", 513, "fe80::4%513")]
    public void ReadsOnlyServiceOwnedListeners(bool v6, string address, int scope, string expected)
    {
        var family = v6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
        int stride = v6 ? 56 : 24;
        byte[] table = new byte[4 + 2 * stride];
        BinaryPrimitives.WriteUInt32LittleEndian(table, 2);
        for (int i = 0; i < 2; i++)
        {
            var row = table.AsSpan(4 + i * stride, stride);
            IPAddress.Parse(address).GetAddressBytes().CopyTo(row[(v6 ? 0 : 4)..]);
            if (v6) BinaryPrimitives.WriteUInt32BigEndian(row[16..], (uint)scope);
            BinaryPrimitives.WriteUInt16BigEndian(row[(v6 ? 20 : 8)..], i == 0 ? (ushort)22 : (ushort)63379);
            BinaryPrimitives.WriteUInt32LittleEndian(row[^4..], i == 0 ? 44u : 55u);
        }
        List<IPEndPoint> endpoints = [];
        Assert.True(WindowsSshListeners.ParseTable(table, family, 55, endpoints));
        Assert.Equal(new IPEndPoint(IPAddress.Parse(expected), 63379), Assert.Single(endpoints));
        endpoints.Clear();
        Assert.False(WindowsSshListeners.ParseTable(table.AsSpan(0, table.Length - 1), family, 55, endpoints));
        Assert.Empty(endpoints);
    }
}
