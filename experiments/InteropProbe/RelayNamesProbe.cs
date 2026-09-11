using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RemoteDesk;

// Own ephemeral node only. No writes to saved settings, real device names or device keys.
internal static class RelayNamesProbe
{
    internal static async Task<int> RunAsync(JsonElement config, string output)
    {
        RelaySettings saved = new AppSettingsService().Load().Relay;
        if (string.IsNullOrWhiteSpace(saved.ServerAddress) || !config.TryGetProperty("expectedServer", out var expectedServer) ||
            string.IsNullOrWhiteSpace(expectedServer.GetString()) || saved.ServerAddress != expectedServer.GetString() ||
            string.IsNullOrEmpty(saved.SshHostKeySha256))
            throw new InvalidOperationException("Unexpected relay identity; no connection attempted");
        var options = new RelayConnectionOptions(saved.ServerAddress, saved.RelayPort,
            AppSettingsService.UnprotectSecret(saved.ProtectedAccessToken), saved.TlsCertificateSha256!, Guid.NewGuid().ToString("D")).Validate();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var checks = new List<object>();
        void Check(string name, bool pass) { checks.Add(new { name, passed = pass }); if (!pass) throw new InvalidOperationException(name); }
        bool installed = false;
        if (config.TryGetProperty("deploy", out var deploy) && deploy.GetBoolean())
        {
            // The product installer authenticates loaded version/hash, refuses busy
            // upgrades, preserves credentials/certificates, and rolls back on failure.
            RelayProvisionResult result = await new RelayProvisioner().ProvisionAsync(
                new(saved.ServerAddress, saved.SshPort, "root", config.GetProperty("password").GetString()!,
                    saved.RelayPort, saved.SshHostKeySha256), timeout.Token);
            installed = result.Installed;
            Check("server update preserves saved login and certificate", result.AccessToken == options.AccessToken &&
                string.Equals(result.TlsCertificateSha256, options.TlsCertificateSha256, StringComparison.OrdinalIgnoreCase));
            Program.Save(Path.Combine(output, "deployment.json"), new { server = options.ServerAddress, installed, identityPreserved = true });
        }
        var (healthConnection, healthStream) = await RelayTls.ConnectAsync(options, timeout.Token);
        using (healthConnection)
        using (healthStream)
        {
            await RelayTls.WriteJsonAsync(healthStream, new { version = 1, role = "health", token = options.AccessToken }, timeout.Token);
            using var health = await RelayTls.ReadJsonAsync(healthStream, timeout.Token);
            RelayTls.EnsureSuccess(health.RootElement);
            Check("public server is running exactly the checked source", string.Equals(
                health.RootElement.GetProperty("serverSourceSha256").GetString(), Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync("scripts/relay/remotedesk_relay_server.py", timeout.Token))),
                StringComparison.OrdinalIgnoreCase));
            Program.Save(Path.Combine(output, "server-health.json"), new { version = health.RootElement.GetProperty("serverVersion").GetString(),
                sourceSha256 = health.RootElement.GetProperty("serverSourceSha256").GetString() });
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var host = new RelayHostConnector();
        bool registered = false;
        try
        {
            await host.StartAsync(options, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
            RelayOnlineDevice? original = null;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                original = (await RelayTunnelClient.ListDevicesAsync(options, timeout.Token)).FirstOrDefault(d => d.DeviceId == options.DeviceId);
                if (original is not null) break;
                await Task.Delay(300, timeout.Token);
            }
            Check("owned test node is registered with naming capability", original is { CanRename: true });
            registered = true;
            await RelayTunnelClient.RenameDeviceAsync(options, "Windows 共享测试机", timeout.Token);
            var observer = options with { DeviceId = Guid.NewGuid().ToString("D") };
            RelayOnlineDevice ReadTarget(IReadOnlyList<RelayOnlineDevice> nodes) => nodes.Single(d => d.DeviceId == options.DeviceId);
            Check("independent Windows client sees shared name", ReadTarget(await RelayTunnelClient.ListDevicesAsync(observer, timeout.Token)).MachineName == "Windows 共享测试机");
            string root = Directory.GetCurrentDirectory();
            object payload = new { serverAddress = options.ServerAddress, expectedServer = options.ServerAddress,
                port = options.Port, accessToken = options.AccessToken,
                tlsCertificateSha256 = options.TlsCertificateSha256, deviceId = options.DeviceId, originalName = original!.OriginalMachineName,
                publish = false, output };

            var linux = new ProcessStartInfo("wsl.exe") { WorkingDirectory = root };
            foreach (string arg in new[] { "-d", "Ubuntu-24.04", "--cd", "/mnt/" + char.ToLowerInvariant(root[0]) + root[2..].Replace('\\', '/'),
                         "--exec", "python3", "experiments/verify_relay_names.py", "linux" }) linux.ArgumentList.Add(arg);
            await RunChildAsync(linux, payload, timeout.Token);
            Check("Linux client sees Windows name and publishes a new Chinese name", ReadTarget(await RelayTunnelClient.ListDevicesAsync(observer, timeout.Token)).SharedName == "Linux 广州工作站");

            if (config.TryGetProperty("android", out var android) && android.GetBoolean())
            {
                var mobile = new ProcessStartInfo("python") { WorkingDirectory = root };
                mobile.ArgumentList.Add("experiments/verify_relay_names.py"); mobile.ArgumentList.Add("android");
                await RunChildAsync(mobile, payload, timeout.Token);
                Check("Android UI saves, cancels and clears the same shared name", ReadTarget(await RelayTunnelClient.ListDevicesAsync(observer, timeout.Token)).SharedName == "");
            }
            await RelayTunnelClient.RenameDeviceAsync(options, "", timeout.Token);
            Check("clearing shared name restores original name", ReadTarget(await RelayTunnelClient.ListDevicesAsync(observer, timeout.Token)).MachineName == original.MachineName);
            Program.Save(Path.Combine(output, "public-relay.json"), new { complete = true, server = options.ServerAddress, installed,
                scope = "One generated fixture UUID; no existing device names or local settings modified", checks });
            Console.WriteLine("PUBLIC_RELAY_NAMING_PASS");
            return 0;
        }
        finally
        {
            try
            {
                if (registered)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await RelayTunnelClient.RenameDeviceAsync(options, "", cleanup.Token);
                }
            }
            finally { await host.StopAsync(); }
        }
    }

    private static async Task RunChildAsync(ProcessStartInfo start, object payload, CancellationToken cancellationToken)
    {
        start.UseShellExecute = false; start.CreateNoWindow = true;
        start.RedirectStandardInput = true; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        start.StandardInputEncoding = new UTF8Encoding(false);
        using var child = Process.Start(start)!;
        try
        {
            var stdout = child.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = child.StandardError.ReadToEndAsync(cancellationToken);
            await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(payload));
            child.StandardInput.Close();
            await child.WaitForExitAsync(cancellationToken);
            string result = await stdout; _ = await stderr;
            if (child.ExitCode != 0) throw new InvalidOperationException("Owned cross-platform naming probe failed; inspect redacted evidence (no credentials logged)");
            if (!result.Contains("NAMING_PASS", StringComparison.Ordinal)) throw new InvalidOperationException("Naming probe did not report completion");
        }
        finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
    }
}
