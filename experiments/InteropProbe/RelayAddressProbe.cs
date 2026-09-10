using System.Net;
using System.Net.Sockets;
using RemoteDesk;

// Own temporary echo host, not an installed host; never inject OS input or alter NICs.
internal static class RelayAddressProbe
{
    internal static async Task<int> RunAsync(RelayConnectionOptions configuration, string output)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var token = deadline.Token;
        var options = configuration with { DeviceId = Guid.NewGuid().ToString() };
        var local = new TcpListener(IPAddress.Loopback, 0);
        local.Start(1);
        int port = ((IPEndPoint)local.LocalEndpoint).Port;
        IReadOnlyList<string> actual = RelayAddressReport.LocalAddresses();
        if (actual.Count == 0) throw new InvalidOperationException("No usable local IPv4 address");
        IReadOnlyList<string> current = actual;
        using var connector = new RelayHostConnector(addressProvider: () => current);
        Task echo = Task.Run(async () => {
            using var socket = await local.AcceptTcpClientAsync(token);
            var stream = socket.GetStream();
            byte[] data = new byte[4096];
            for (int count; (count = await stream.ReadAsync(data, token)) > 0;) await stream.WriteAsync(data.AsMemory(0, count), token);
        }, token);
        try
        {
            await connector.StartAsync(options, port, token);
            async Task<RelayOnlineDevice> WaitFor(IReadOnlyList<string> expected)
            {
                for (int count = 0; count < 30; count++)
                {
                    var devices = await RelayTunnelClient.ListDevicesAsync(options, token);
                    var own = devices.Where(device => device.DeviceId == options.DeviceId).ToArray();
                    if (own.Length == 1 && own[0].DirectAddresses.SequenceEqual(expected) &&
                        own[0].DirectPort == (expected.Count > 0 ? port : 0)) return own[0];
                    await Task.Delay(200, token);
                }
                throw new InvalidOperationException("Server did not update the owned device address record");
            }
            await WaitFor(actual);
            using var viewer = new TcpClient();
            await RelayTunnelClient.ConnectViewerIntoAsync(viewer, options, token);
            var stages = new List<object>();
            foreach (var addresses in new IReadOnlyList<string>[] { [], actual })
            {
                current = addresses;
                if (!connector.RequestAddressRefresh()) throw new InvalidOperationException("Registration is not active");
                await WaitFor(addresses);
                byte[] message = System.Text.Encoding.UTF8.GetBytes("address-change-中文");
                await viewer.GetStream().WriteAsync(message, token);
                byte[] echoed = new byte[message.Length];
                await viewer.GetStream().ReadExactlyAsync(echoed, token);
                if (!message.SequenceEqual(echoed)) throw new IOException("Active relay bytes changed");
                stages.Add(new { advertisedCount = addresses.Count, sameSessionEcho = true });
            }
            Program.Save(Path.Combine(output, "address-report.json"), new { complete = true, platform = "Windows",
                localAddresses = actual, customPort = port, stages,
                scope = "Public relay; product registration/directory; simulated address removal/restoration without changing NICs. One owned echo session, no screen or OS input." });
            return 0;
        }
        finally
        {
            await connector.StopAsync();
            deadline.Cancel(); local.Stop();
            try { await echo; } catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException) { }
        }
    }
}
