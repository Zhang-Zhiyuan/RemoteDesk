using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteDesk;
using Renci.SshNet;

internal static class SshRelayFixtureProbe
{
    // Only explicitly owned loopback fixtures. No production password, remote
    // input, terminal capture, or arbitrary shell command is accepted as input.
    internal static async Task<int> RunAsync(JsonElement config, string output)
    {
        string address = config.GetProperty("serverAddress").GetString()!;
        if (!config.GetProperty("isFixture").GetBoolean() || !IPAddress.TryParse(address, out var ip) || !IPAddress.IsLoopback(ip))
            throw new InvalidOperationException("SSH integration probe is loopback fixture only.");
        var options = new RelayConnectionOptions(address, config.GetProperty("port").GetInt32(),
            config.GetProperty("token").GetString()!, config.GetProperty("tlsCertificateSha256").GetString()!,
            config.GetProperty("deviceId").GetString()!);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var windowsHost = new RelayHostConnector(sshOptionsProvider: () => new RelaySshHostOptions(true,
            config.GetProperty("sshPort").GetInt32(), config.GetProperty("deviceKey").GetString()!));
        bool testWindowsHost = config.TryGetProperty("windowsHostBridge", out var useWindows) && useWindows.GetBoolean();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            if (testWindowsHost)
            {
                // Tests the Windows host bridge against the same isolated sshd
                // reached by WSL loopback forwarding, not a production SSH service.
                options = options with { DeviceId = Guid.NewGuid().ToString("D") };
                await windowsHost.StartAsync(options, 0, timeout.Token);
                while (!(await RelayTunnelClient.ListDevicesAsync(options, timeout.Token)).Any(d => d.DeviceId == options.DeviceId && d.SupportsSsh))
                    await Task.Delay(40, timeout.Token);
            }
            var devices = await RelayTunnelClient.ListDevicesAsync(options, timeout.Token);
            if (!devices.Any(d => d.DeviceId == options.DeviceId && d.SupportsSsh)) throw new Exception("Fixture is not SSH-capable.");
            using SshRelayConnection tunnel = await SshRelayClient.OpenAsync(options, config.GetProperty("deviceKey").GetString()!, timeout.Token);
            Task bridge = BridgeAsync(listener, tunnel, timeout.Token);
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string user = config.GetProperty("sshUsername").GetString()!;
            using AuthenticationMethod authentication = config.TryGetProperty("sshKeyPath", out var key)
                ? new PrivateKeyAuthenticationMethod(user, new PrivateKeyFile(key.GetString()!))
                : new PasswordAuthenticationMethod(user, config.GetProperty("sshPassword").GetString()!);
            using var ssh = new SshClient(new ConnectionInfo("127.0.0.1", port, user, authentication) { Timeout = TimeSpan.FromSeconds(15) });
            bool hostKeyMatched = false;
            string pin = config.GetProperty("sshHostKeySha256").GetString()!;
            ssh.HostKeyReceived += (_, e) =>
            {
                string actual = "SHA256:" + Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('=');
                e.CanTrust = hostKeyMatched = actual == pin;
            };
            await Task.Run(ssh.Connect, timeout.Token);
            if (!ssh.IsConnected || !hostKeyMatched) throw new Exception("Fixture SSH login or host identity failed.");
            using var shell = ssh.CreateShellStream("xterm-256color", 80, 24, 800, 480, 4096);
            await DrainUntilAsync(shell, "", timeout.Token);
            shell.WriteLine("printf '\\122\\104\\137\\123\\123\\110\\137\\117\\113\\137\\344\\270\\255\\346\\226\\207\\n'");
            await DrainUntilAsync(shell, "RD_SSH_OK_中文", timeout.Token);
            shell.ChangeWindowSize(101, 37, 1010, 740);
            shell.WriteLine("stty size");
            await DrainUntilAsync(shell, "37 101", timeout.Token);
            shell.WriteLine("sleep 30");
            await Task.Delay(300, timeout.Token);
            shell.WriteByte(3); shell.Flush();
            shell.WriteLine("printf '\\122\\104\\137\\103\\124\\122\\114\\137\\103\\137\\117\\113\\n'");
            await DrainUntilAsync(shell, "RD_CTRL_C_OK", timeout.Token);
            shell.WriteLine("exit");
            ssh.Disconnect();
            tunnel.Dispose();
            try { await bridge.WaitAsync(TimeSpan.FromSeconds(5)); } catch (IOException) { }
            File.WriteAllText(Path.Combine(output, "ssh-relay-fixture.json"), JsonSerializer.Serialize(new {
                passed = true, scope = testWindowsHost
                    ? "Windows client -> isolated TLS relay -> Windows SSH host bridge -> owned WSL OpenSSH sshd (not Windows OS login)"
                    : "Windows product SSH tunnel -> isolated TLS relay -> Linux product SSH host -> real sshd",
                directoryCapability = true, deviceGate = true, independentSshUserAuthentication = true,
                pinnedSshHostKey = hostKeyMatched, pty = true, unicodeOutput = true, resize = true, ctrlC = true,
                desktopViewerCreated = false, commandsReplayed = false, productionSettingsChanged = false
            }, Program.Json));
            Console.WriteLine("SSH fixture: identity, login, PTY, Unicode, resize and Ctrl+C passed.");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "ssh-relay-fixture.json"), JsonSerializer.Serialize(new {
                passed = false, errorType = ex.GetType().Name, error = ex.Message,
                scope = "Owned fixture only; credentials/terminal content intentionally not recorded"
            }, Program.Json));
            return 1;
        }
        finally { listener.Stop(); }
    }

    private static async Task BridgeAsync(TcpListener listener, SshRelayConnection tunnel, CancellationToken token)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        listener.Stop();
        await RelayStreamBridge.RunAsync(tunnel.Stream, client.GetStream(), tunnel, client, token);
    }

    private static async Task DrainUntilAsync(ShellStream shell, string marker, CancellationToken token)
    {
        var text = new StringBuilder();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (shell.DataAvailable)
            {
                text.Append(shell.Read());
                if (marker.Length == 0 || text.ToString().Contains(marker, StringComparison.Ordinal)) return;
                if (text.Length > 65536) throw new InvalidOperationException("SSH fixture output exceeded bound.");
            }
            await Task.Delay(30, deadline.Token);
        }
    }
}
