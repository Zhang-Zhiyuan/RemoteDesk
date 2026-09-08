using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteDesk;
using Renci.SshNet;

// Explicit, interactive public-server acceptance tool. Never run as part of
// automated unit tests. No credential argument, environment variable or file.
if (args.Length != 4 || !int.TryParse(args[1], out int relayPort) ||
    relayPort is < 1 or > 65535 || !args[3].StartsWith("SHA256:", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Usage: RelayPublicProbe <server> <relay-port> <ssh-user> <verified-ssh-sha256>");
    return 2;
}
if (Console.IsInputRedirected)
{
    Console.Error.WriteLine("Use an interactive terminal; the SSH password is entered without echo.");
    return 2;
}

Console.WriteLine("This explicitly installs/updates the RemoteDesk relay on the specified server.");
Console.WriteLine("Synthetic loopback endpoints only: no desktop, keyboard, clipboard or user settings access.");
Console.Write("SSH password (hidden): ");
string password = ReadSecret();
string? accessToken = null;
var checks = new List<object>();
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
CancellationToken stop = deadline.Token;
try
{
    var request = new RelayProvisionRequest(args[0], 22, args[2], password, relayPort, args[3]);
    var provisioner = new RelayProvisioner();
    Console.WriteLine("PROVISION starting the application's SSH/SFTP installer");
    RelayProvisionResult installed = await provisioner.ProvisionAsync(request, stop);
    accessToken = installed.AccessToken;
    Pass("provision", new { installed.Installed, installed.RelayPort, installed.TlsCertificateSha256 });

    var options = new RelayConnectionOptions(args[0], installed.RelayPort,
        installed.AccessToken, installed.TlsCertificateSha256, Guid.NewGuid().ToString("D"));
    var watch = Stopwatch.StartNew();
    IReadOnlyList<RelayOnlineDevice> initial = await RelayTunnelClient.ListDevicesAsync(options, stop);
    Pass("public-directory", new { elapsedMs = watch.Elapsed.TotalMilliseconds, onlineDevices = initial.Count });
    await ExpectAsync<RelayAccessDeniedException>("bad-access-token-rejected", () =>
        RelayTunnelClient.ListDevicesAsync(options with { AccessToken = new string('0', 64) }, stop));
    await ExpectAsync<AuthenticationException>("wrong-tls-pin-rejected", () =>
        RelayTunnelClient.ListDevicesAsync(options with { TlsCertificateSha256 = new string('0', 64) }, stop));

    using var ssh = new SshClient(args[0], 22, args[2], password);
    ssh.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
    ssh.HostKeyReceived += (_, key) => key.CanTrust = string.Equals(
        RelayProvisioner.NormalizeSshFingerprint(key.FingerPrintSHA256),
        RelayProvisioner.NormalizeSshFingerprint(args[3]), StringComparison.Ordinal);
    await ssh.ConnectAsync(stop);
    string originalPid = await ReadPidAsync(ssh, stop);

    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    using var hostStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
    using var connector = new RelayHostConnector();
    connector.StatusChanged += message => Console.WriteLine("HOST " + message);
    Task echo = EchoHostAsync(listener, hostStop.Token);
    try
    {
        int localPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        await connector.StartAsync(options, localPort, stop);
        await WaitForDeviceAsync(options, present: true, stop);
        Pass("host-online", new { syntheticDeviceId = options.DeviceId });

        using (var viewer = new TcpClient())
        {
            viewer.NoDelay = true;
            await RelayTunnelClient.ConnectViewerIntoAsync(viewer, options, stop);
            var rtts = new List<double>();
            for (int sample = 0; sample < 5; sample++)
                rtts.Add(await EchoVerifiedAsync(viewer.GetStream(), 256, stop));
            rtts.Sort();
            Pass("public-bidirectional-echo", new { samples = 5, medianRttMs = rtts[2], maxRttMs = rtts[^1] });

            // Read and write concurrently: serializing a large echo can deadlock
            // against bounded buffers on the real Internet.
            double elapsedMs = await EchoVerifiedAsync(viewer.GetStream(), 1024 * 1024, stop);
            Pass("public-payload-integrity", new { bytesEachDirection = 1024 * 1024,
                elapsedMs, aggregateMbps = (2.0 * 1024 * 1024 * 8) / (elapsedMs * 1000) });

            RelayProvisionResult repeated = await provisioner.ProvisionAsync(request, stop);
            if (repeated.Installed || repeated.AccessToken != installed.AccessToken ||
                repeated.TlsCertificateSha256 != installed.TlsCertificateSha256 ||
                await ReadPidAsync(ssh, stop) != originalPid)
                throw new InvalidOperationException("Repeat installation changed server identity or restarted the service.");
            await EchoVerifiedAsync(viewer.GetStream(), 256, stop);
            Pass("repeat-install-preserves-live-tunnel", new { serverPid = originalPid });

            await Task.Delay(TimeSpan.FromSeconds(11), stop);
            IReadOnlyList<RelayOnlineDevice> active = await RelayTunnelClient.ListDevicesAsync(options, stop);
            RelayOnlineDevice own = active.Single(device => device.DeviceId == options.DeviceId);
            if (!own.Busy || own.LastSeenSeconds > 15)
                throw new InvalidOperationException("Heartbeat or directory busy state did not update.");
            Pass("heartbeat-and-busy-state", new { own.Busy, own.LastSeenSeconds });
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var retry = new TcpClient { NoDelay = true };
            await RelayTunnelClient.ConnectViewerIntoAsync(retry, options, stop);
            await EchoVerifiedAsync(retry.GetStream(), 1024, stop);
        }
        Pass("viewer-reconnect", new { successfulAttempts = 3 });

        await connector.StopAsync();
        await WaitForDeviceAsync(options, present: false, stop);
        await connector.StartAsync(options, localPort, stop);
        await WaitForDeviceAsync(options, present: true, stop);
        using (var recovered = new TcpClient { NoDelay = true })
        {
            await RelayTunnelClient.ConnectViewerIntoAsync(recovered, options, stop);
            await EchoVerifiedAsync(recovered.GetStream(), 1024, stop);
        }
        Pass("host-reconnect", new { recovered = true });
    }
    finally
    {
        await connector.StopAsync();
        hostStop.Cancel();
        listener.Stop();
        try { await echo; }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException) { }
    }
    Pass("encrypted-remotedesk-protocol", await RelayProtocolProbe.RunAsync(options, stop));
    await WaitForDeviceAsync(options, present: false, stop);
    Pass("synthetic-device-cleanup", new { removedFromDirectory = true });
    Console.WriteLine("RESULT " + JsonSerializer.Serialize(new { success = true, server = args[0], port = relayPort, checks }));
    return 0;
}
catch (Exception ex)
{
    string error = ex.GetType().Name + ": " + ex.Message;
    if (password.Length != 0) error = error.Replace(password, "[redacted]", StringComparison.Ordinal);
    if (!string.IsNullOrEmpty(accessToken)) error = error.Replace(accessToken, "[redacted]", StringComparison.Ordinal);
    Console.Error.WriteLine("RESULT " + JsonSerializer.Serialize(new { success = false, error, checks }));
    return 1;
}
finally
{
    password = string.Empty;
    accessToken = null;
}

void Pass(string name, object detail)
{
    checks.Add(new { name, detail });
    Console.WriteLine("PASS " + name + " " + JsonSerializer.Serialize(detail));
}

async Task ExpectAsync<T>(string name, Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { Pass(name, new { rejected = true }); return; }
    throw new InvalidOperationException(name + " unexpectedly succeeded.");
}

static string ReadSecret()
{
    var value = new StringBuilder();
    while (true)
    {
        ConsoleKeyInfo key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString(); }
        if (key.Key == ConsoleKey.Backspace) { if (value.Length != 0) value.Length--; continue; }
        if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
    }
}

static async Task<string> ReadPidAsync(SshClient ssh, CancellationToken stop)
{
    using var command = ssh.CreateCommand("systemctl show remotedesk-relay.service --property=MainPID --value");
    await command.ExecuteAsync(stop);
    string result = command.Result.Trim();
    if (command.ExitStatus != 0 || !int.TryParse(result, out int pid) || pid <= 0)
        throw new IOException("The relay service has no running MainPID.");
    return result;
}

static async Task WaitForDeviceAsync(RelayConnectionOptions options, bool present, CancellationToken stop)
{
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
    deadline.CancelAfter(TimeSpan.FromSeconds(20));
    do
    {
        IReadOnlyList<RelayOnlineDevice> devices = await RelayTunnelClient.ListDevicesAsync(options, deadline.Token);
        if (devices.Any(device => device.DeviceId == options.DeviceId) == present) return;
        await Task.Delay(300, deadline.Token);
    } while (true);
}

static async Task<double> EchoVerifiedAsync(NetworkStream stream, int size, CancellationToken stop)
{
    byte[] sent = RandomNumberGenerator.GetBytes(size);
    byte[] received = new byte[size];
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
    deadline.CancelAfter(TimeSpan.FromSeconds(90));
    var watch = Stopwatch.StartNew();
    await Task.WhenAll(stream.WriteAsync(sent, deadline.Token).AsTask(),
        stream.ReadExactlyAsync(received, deadline.Token).AsTask());
    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(sent), SHA256.HashData(received)))
        throw new IOException("Public relay payload hash mismatch.");
    return watch.Elapsed.TotalMilliseconds;
}

static async Task EchoHostAsync(TcpListener listener, CancellationToken stop)
{
    var clients = new List<Task>();
    try
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client = await listener.AcceptTcpClientAsync(stop);
            client.NoDelay = true;
            clients.Add(EchoClientAsync(client, stop));
        }
    }
    catch (Exception ex) when (ex is OperationCanceledException or SocketException) { }
    finally { await Task.WhenAll(clients); }
}

static async Task EchoClientAsync(TcpClient client, CancellationToken stop)
{
    using (client)
    {
        try
        {
            NetworkStream stream = client.GetStream();
            byte[] buffer = new byte[32 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, stop)) != 0)
                await stream.WriteAsync(buffer.AsMemory(0, read), stop);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
    }
}
