using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class RelayTunnelIntegrationTests
{
    [Fact]
    public async Task PinnedTlsDirectoryAndOpaqueTunnelRoundTrip()
    {
        string? serverScript = FindWorkspaceFile(
            Path.Combine(
                "scripts",
                "relay",
                "remotedesk_relay_server.py"));
        Assert.NotNull(serverScript);
        Assert.True(CanStartPython(), "This integration test requires Python on PATH; it must not silently pass without running.");

        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            "RemoteDeskRelayTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        Process? serverProcess = null;
        var hostConnector = new RelayHostConnector();
        var localListener = new TcpListener(
            IPAddress.Loopback,
            0);
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(25));
        try
        {
            string certificatePath = Path.Combine(
                testDirectory,
                "relay.crt");
            string keyPath = Path.Combine(
                testDirectory,
                "relay.key");
            byte[] certificateBytes = CreateCertificate(
                certificatePath,
                keyPath);
            string token = Convert.ToHexString(
                RandomNumberGenerator.GetBytes(32))
                .ToLowerInvariant();
            string configPath = Path.Combine(
                testDirectory,
                "config.json");
            File.WriteAllText(
                configPath,
                JsonSerializer.Serialize(
                    new Dictionary<string, object>
                    {
                        ["access_token"] = token,
                        ["bind"] = "127.0.0.1",
                        // The test launcher replaces only the listening port
                        // with 0 and reports the OS-assigned, still-owned port.
                        // Production config validation remains unchanged.
                        ["port"] = 56567,
                        ["cert_file"] = certificatePath,
                        ["key_file"] = keyPath
                    }));

            serverProcess = StartPythonServer(
                serverScript,
                configPath);
            int relayPort = await ReadBoundPortAsync(serverProcess, timeout.Token);
            // The readiness message must describe a socket which is ALREADY
            // owned, not a candidate another parallel test could take over.
            using (var competingListener = new TcpListener(IPAddress.Loopback, relayPort) { ExclusiveAddressUse = true })
                Assert.Throws<SocketException>(() => competingListener.Start());
            string deviceId = Guid.NewGuid().ToString("D");
            var options = new RelayConnectionOptions(
                "127.0.0.1",
                relayPort,
                token,
                RelayTls.GetSha256Fingerprint(certificateBytes),
                deviceId);
            await WaitForRelayAsync(
                options,
                serverProcess,
                timeout.Token);

            await Assert.ThrowsAnyAsync<
                System.Security.Authentication.AuthenticationException>(
                () => RelayTunnelClient.ListDevicesAsync(
                    options with
                    {
                        TlsCertificateSha256 = new string('0', 64)
                    },
                    timeout.Token));

            localListener.Start();
            int localPort = ((IPEndPoint)
                localListener.LocalEndpoint).Port;
            await hostConnector.StartAsync(
                options,
                localPort,
                timeout.Token);
            IReadOnlyList<RelayOnlineDevice> devices =
                await WaitForDeviceAsync(
                    options,
                    deviceId,
                    timeout.Token);
            Assert.Contains(
                devices,
                device => device.DeviceId == deviceId);

            RelayOnlineDevice originalDevice = Assert.Single(devices, device => device.DeviceId == deviceId);
            Assert.True(originalDevice.CanRename);
            await RelayTunnelClient.RenameDeviceAsync(options, "  广州工作站 🖥  ", timeout.Token);
            var otherClient = options with { DeviceId = Guid.NewGuid().ToString("D") };
            RelayOnlineDevice named = Assert.Single(await RelayTunnelClient.ListDevicesAsync(otherClient, timeout.Token));
            Assert.Equal("广州工作站 🖥", named.MachineName);
            Assert.Equal("广州工作站 🖥", named.SharedName);
            Assert.Equal(originalDevice.MachineName, named.OriginalMachineName);
            Assert.True(File.Exists(Path.Combine(testDirectory, "device-names.json")));
            await Assert.ThrowsAsync<RelayAccessDeniedException>(() => RelayTunnelClient.RenameDeviceAsync(
                options with { AccessToken = new string('f', 64) }, "不可修改", timeout.Token));
            Assert.Equal("广州工作站 🖥", Assert.Single(await RelayTunnelClient.ListDevicesAsync(otherClient, timeout.Token)).SharedName);
            await RelayTunnelClient.RenameDeviceAsync(options, "", timeout.Token);
            RelayOnlineDevice restored = Assert.Single(await RelayTunnelClient.ListDevicesAsync(otherClient, timeout.Token));
            Assert.Equal(originalDevice.MachineName, restored.MachineName);
            Assert.Empty(restored.SharedName);

            Task<TcpClient> acceptTask =
                localListener.AcceptTcpClientAsync(
                    timeout.Token).AsTask();
            using var viewer = new TcpClient();
            await RelayTunnelClient.ConnectViewerIntoAsync(
                viewer,
                options,
                timeout.Token);
            using TcpClient local = await acceptTask;
            NetworkStream viewerStream = viewer.GetStream();
            NetworkStream localStream = local.GetStream();
            byte[] request = "viewer-to-host"u8.ToArray();
            byte[] response = "host-to-viewer"u8.ToArray();
            await viewerStream.WriteAsync(request, timeout.Token);
            byte[] receivedRequest = new byte[request.Length];
            await localStream.ReadExactlyAsync(
                receivedRequest,
                timeout.Token);
            Assert.Equal(request, receivedRequest);

            await localStream.WriteAsync(response, timeout.Token);
            byte[] receivedResponse = new byte[response.Length];
            await viewerStream.ReadExactlyAsync(
                receivedResponse,
                timeout.Token);
            Assert.Equal(response, receivedResponse);
            viewer.Close();
            local.Close();

            var releaseProtocolHost =
                new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var protocolCapabilities =
                new TaskCompletionSource<RemoteDeviceCapabilities>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            Task protocolHost =
                RunProtocolHostAsync(
                    localListener,
                    "relay-remote-password",
                    protocolCapabilities,
                    releaseProtocolHost.Task,
                    timeout.Token);
            using var remoteViewer = new RemoteViewerClient(
                incomingFileReceiveDirectoryProvider: null,
                setFileDropListAsync: null,
                connectTimeout: TimeSpan.FromSeconds(8));
            await remoteViewer.ConnectViaRelayAsync(
                options,
                "relay-remote-password",
                ViewerVideoMode.StableJpeg,
                timeout.Token);
            Assert.True(await remoteViewer.WaitForCurrentDeviceInfoAsync(
                RemoteViewerClient.DeviceInfoHandshakeTimeout,
                timeout.Token));
            Assert.True(remoteViewer.IsConnected);
            RemoteDeviceCapabilities advertisedCapabilities =
                await protocolCapabilities.Task.WaitAsync(timeout.Token);
            Assert.False(advertisedCapabilities.HasFlag(
                RemoteDeviceCapabilities.LowLatencyUdpVideo));
            Assert.False(advertisedCapabilities.HasFlag(
                RemoteDeviceCapabilities.LowLatencyUdpMouseInput));
            Assert.True(advertisedCapabilities.HasFlag(
                RemoteDeviceCapabilities.HighQualityJpeg));
            await remoteViewer.DisconnectAsync();
            releaseProtocolHost.TrySetResult();
            await protocolHost.WaitAsync(timeout.Token);
        }
        finally
        {
            localListener.Stop();
            await hostConnector.StopAsync();
            hostConnector.Dispose();
            if (serverProcess is not null)
            {
                TryStopProcess(serverProcess);
                serverProcess.Dispose();
            }

            try
            {
                Directory.Delete(testDirectory, recursive: true);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public void ConnectionOptionsRejectUnpinnedOrMalformedConfiguration()
    {
        string deviceId = Guid.NewGuid().ToString("D");
        Assert.Throws<InvalidOperationException>(() =>
            new RelayConnectionOptions(
                "relay.example.com",
                56567,
                "ab",
                new string('A', 64),
                deviceId).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new RelayConnectionOptions(
                "relay.example.com",
                56567,
                new string('a', 64),
                string.Empty,
                deviceId).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new RelayConnectionOptions(
                "relay.example.com",
                56567,
                new string('a', 64),
                new string('A', 64),
                "not-a-device").Validate());
    }

    private static byte[] CreateCertificate(
        string certificatePath,
        string keyPath)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=RemoteDesk Relay Test",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                false,
                false,
                0,
                false));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(
                request.PublicKey,
                false));
        using X509Certificate2 certificate =
            request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllText(
            certificatePath,
            certificate.ExportCertificatePem());
        File.WriteAllText(
            keyPath,
            key.ExportPkcs8PrivateKeyPem());
        return certificate.RawData;
    }

    private static async Task
        RunProtocolHostAsync(
            TcpListener listener,
            string password,
            TaskCompletionSource<RemoteDeviceCapabilities>
                capabilitiesReceived,
            Task release,
            CancellationToken cancellationToken)
    {
        using TcpClient client =
            await listener.AcceptTcpClientAsync(cancellationToken);
        NetworkUtils.ConfigureLowLatencyTcpClient(
            client,
            32 * 1024,
            256 * 1024);
        await using NetworkStream stream = client.GetStream();
        using var writeLock = new SemaphoreSlim(1, 1);
        ServerAuthenticationResult authentication =
            await Protocol.AuthenticateServerDetailedAsync(
                stream,
                password,
                cancellationToken);
        Assert.True(authentication.IsAuthenticated);
        using SecureSession session = authentication.Session!;

        bool viewerInfoReceived = false;
        RemoteDeviceCapabilities viewerCapabilities =
            RemoteDeviceCapabilities.None;
        while (!viewerInfoReceived ||
               viewerCapabilities == RemoteDeviceCapabilities.None)
        {
            ProtocolMessage message = await Protocol.ReadMessageAsync(
                stream,
                session,
                cancellationToken);
            Assert.Equal(MessageType.Control, message.Type);
            RemoteControlMessage control =
                RemoteMessageCodec.DecodeControl(
                    message.PayloadMemory);
            if (control.Kind == RemoteControlKind.ViewerInfo)
            {
                viewerInfoReceived = true;
            }
            else if (control.Kind ==
                     RemoteControlKind.ViewerCapabilities)
            {
                viewerCapabilities = control.Capabilities;
            }
        }

        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeDeviceInfo(
                new RemoteDeviceDescriptor(
                    "relay-protocol-host",
                    RemoteDevicePlatforms.Windows,
                    RemoteDeviceCapabilities.RemoteDesktop |
                    RemoteDeviceCapabilities.InputControl)),
            session,
            writeLock,
            cancellationToken);
        capabilitiesReceived.TrySetResult(viewerCapabilities);
        await release.WaitAsync(cancellationToken);
    }

    private static Process StartPythonServer(
        string serverScript,
        string configPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "python",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Never "reserve" and release a port before another process binds it:
        // a concurrent loopback test can take it, and readiness probes then
        // send TLS bytes to that unrelated test's RDK authentication listener.
        // Run the REAL relay entry point; only socket port selection is adapted
        // in this child process. Its listener stays bound until test cleanup.
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("""
            import asyncio, json, runpy, sys
            relay = runpy.run_path(sys.argv[1], run_name="remotedesk_relay_test")
            real_start_server = asyncio.start_server
            async def start_owned_listener(callback, host=None, port=None, **kwargs):
                if host != "127.0.0.1":
                    raise RuntimeError("Relay fixture must bind loopback only")
                server = await real_start_server(callback, host, 0, **kwargs)
                print(json.dumps({"port": server.sockets[0].getsockname()[1]}), flush=True)
                return server
            asyncio.start_server = start_owned_listener
            asyncio.run(relay["run"](sys.argv[2]))
            """);
        startInfo.ArgumentList.Add(serverScript);
        startInfo.ArgumentList.Add(configPath);
        return Process.Start(startInfo) ??
            throw new InvalidOperationException(
                "无法启动本地中继测试服务。");
    }

    private static async Task<int> ReadBoundPortAsync(Process process, CancellationToken cancellationToken)
    {
        string? ready = await process.StandardOutput.ReadLineAsync(cancellationToken);
        if (ready is null)
            throw new InvalidOperationException("本地中继没有启动：" + await process.StandardError.ReadToEndAsync(cancellationToken));
        if (ready.Length > 128) throw new InvalidDataException("Invalid relay fixture readiness message.");
        using var document = JsonDocument.Parse(ready);
        int port = document.RootElement.GetProperty("port").GetInt32();
        if (port is < 1 or > 65535) throw new InvalidDataException("Invalid bound relay fixture port.");
        return port;
    }

    private static async Task WaitForRelayAsync(
        RelayConnectionOptions options,
        Process process,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < 80; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                string error = await process.StandardError.ReadToEndAsync(
                    cancellationToken);
                throw new InvalidOperationException(
                    $"本地中继测试服务提前退出：{error}");
            }

            try
            {
                await RelayTunnelClient.ListDevicesAsync(
                    options,
                    cancellationToken);
                return;
            }
            catch (Exception ex) when (
                ex is IOException or SocketException)
            {
                lastError = ex;
                await Task.Delay(50, cancellationToken);
            }
        }

        throw new TimeoutException(
            $"本地中继测试服务未就绪：{lastError?.Message}");
    }

    private static async Task<IReadOnlyList<RelayOnlineDevice>>
        WaitForDeviceAsync(
            RelayConnectionOptions options,
            string deviceId,
            CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 80; attempt++)
        {
            IReadOnlyList<RelayOnlineDevice> devices =
                await RelayTunnelClient.ListDevicesAsync(
                    options,
                    cancellationToken);
            if (devices.Any(device =>
                    device.DeviceId == deviceId))
            {
                return devices;
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new TimeoutException(
            "主机未出现在本地中继在线目录中。");
    }

    private static bool CanStartPython()
    {
        try
        {
            using Process? process = Process.Start(
                new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = "--version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
            process?.WaitForExit(3000);
            return process is { ExitCode: 0 };
        }
        catch
        {
            return false;
        }
    }

    private static string? FindWorkspaceFile(
        string relativePath)
    {
        DirectoryInfo? directory =
            new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static void TryStopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or
                System.ComponentModel.Win32Exception)
        {
        }
    }
}
