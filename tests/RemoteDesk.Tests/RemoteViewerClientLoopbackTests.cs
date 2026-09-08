using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class RemoteViewerClientLoopbackTests
{
    private const string LoopbackPassword = "loopback-password";

    [Fact]
    public async Task BufferedCaptureTargetCallbackDoesNotRunUnderConnectionStartupLock()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var sent = CreateCompletionSource<bool>();
        var releaseHost = CreateCompletionSource<bool>();
        async Task Host()
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync(timeout.Token);
            await using NetworkStream stream = peer.GetStream();
            using var writeLock = new SemaphoreSlim(1, 1);
            ServerAuthenticationResult auth = await Protocol.AuthenticateServerDetailedAsync(
                stream, LoopbackPassword, timeout.Token);
            Assert.True(auth.IsAuthenticated);
            using SecureSession session = auth.Session!;
            // Android can send this immediately, before reading viewer info.
            await Protocol.WriteMessageAsync(stream, MessageType.Control,
                RemoteMessageCodec.EncodeCaptureTargetChanged(new CaptureTargetInfo("phone", "Phone")),
                session, writeLock, timeout.Token);
            sent.TrySetResult(true);
            await releaseHost.Task.WaitAsync(timeout.Token);
        }
        Task host = Host();
        using var client = new RemoteViewerClient();
        object connectionLock = typeof(RemoteViewerClient).GetField("_connectionStateLock",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        var writeGate = (SemaphoreSlim)typeof(RemoteViewerClient).GetField("_writeLock",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        var callbackHeldStartupLock = CreateCompletionSource<bool>();
        client.CaptureTargetSelectionChanged += _ =>
            callbackHeldStartupLock.TrySetResult(Monitor.IsEntered(connectionLock));
        await writeGate.WaitAsync(timeout.Token);
        bool gateHeld = true;
        try
        {
            Task connecting = client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
                LoopbackPassword, ViewerVideoMode.StableJpeg, timeout.Token);
            await sent.Task.WaitAsync(timeout.Token);
            // Hold the first viewer write until the early host packet is buffered.
            var socketField = typeof(RemoteViewerClient).GetField("_tcpClient",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            while (socketField.GetValue(client) is not TcpClient tcp || tcp.Available == 0)
                await Task.Delay(10, timeout.Token);
            writeGate.Release(); gateHeld = false;
            await connecting.WaitAsync(timeout.Token);
            Assert.False(await callbackHeldStartupLock.Task.WaitAsync(timeout.Token),
                "Buffered receive callbacks must not hold the connection lock while invoking UI subscribers.");
            Assert.True(client.IsConnected);
        }
        finally
        {
            if (gateHeld) writeGate.Release();
            releaseHost.TrySetResult(true);
            await client.DisconnectAsync().WaitAsync(timeout.Token);
            await host.WaitAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task StaleGenerationDisconnectCannotCloseNewConnection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var firstListener = new TcpListener(IPAddress.Loopback, 0);
        using var secondListener = new TcpListener(IPAddress.Loopback, 0);
        firstListener.Start();
        secondListener.Start();

        var releaseFirst = CreateCompletionSource<bool>();
        var releaseSecond = CreateCompletionSource<bool>();
        Task firstHost = RunCapabilityHostUntilReleasedAsync(
            firstListener,
            LoopbackPassword,
            releaseFirst.Task,
            timeout.Token,
            machineName: "stale-disconnect-old-host");
        Task secondHost = RunCapabilityHostUntilReleasedAsync(
            secondListener,
            LoopbackPassword,
            releaseSecond.Task,
            timeout.Token,
            machineName: "stale-disconnect-new-host");

        using var client = new RemoteViewerClient();
        var firstDevice = CreateCompletionSource<bool>();
        var secondDevice = CreateCompletionSource<bool>();
        client.DeviceInfoReceived += device =>
        {
            if (device.MachineName == "stale-disconnect-old-host")
            {
                firstDevice.TrySetResult(true);
            }
            else if (device.MachineName == "stale-disconnect-new-host")
            {
                secondDevice.TrySetResult(true);
            }
        };

        try
        {
            await client.ConnectAsync(
                IPAddress.Loopback.ToString(),
                ((IPEndPoint)firstListener.LocalEndpoint).Port,
                LoopbackPassword,
                ViewerVideoMode.StableJpeg);
            await firstDevice.Task.WaitAsync(timeout.Token);
            long oldGeneration = client.InputConnectionGeneration;

            releaseFirst.TrySetResult(true);
            await client.DisconnectAsync();
            await firstHost.WaitAsync(timeout.Token);

            await client.ConnectAsync(
                IPAddress.Loopback.ToString(),
                ((IPEndPoint)secondListener.LocalEndpoint).Port,
                LoopbackPassword,
                ViewerVideoMode.StableJpeg);
            await secondDevice.Task.WaitAsync(timeout.Token);
            long newGeneration = client.InputConnectionGeneration;
            Assert.NotEqual(oldGeneration, newGeneration);

            Assert.False(
                await client.DisconnectIfCurrentGenerationAsync(oldGeneration));
            Assert.True(client.IsConnected);
            Assert.Equal(newGeneration, client.InputConnectionGeneration);

            releaseSecond.TrySetResult(true);
            await client.DisconnectAsync();
            await secondHost.WaitAsync(timeout.Token);
        }
        finally
        {
            releaseFirst.TrySetResult(true);
            releaseSecond.TrySetResult(true);
            await client.DisconnectAsync();
        }
    }

    [Fact]
    public async Task InvalidGenerationDisconnectDoesNotCloseCurrentConnection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var release = CreateCompletionSource<bool>();
        Task host = RunCapabilityHostUntilReleasedAsync(
            listener,
            LoopbackPassword,
            release.Task,
            timeout.Token,
            machineName: "invalid-generation-host");
        using var client = new RemoteViewerClient();

        try
        {
            await client.ConnectAsync(
                IPAddress.Loopback.ToString(),
                ((IPEndPoint)listener.LocalEndpoint).Port,
                LoopbackPassword,
                ViewerVideoMode.StableJpeg);
            Assert.True(client.IsConnected);
            Assert.False(
                await client.DisconnectIfCurrentGenerationAsync(
                    long.MinValue));
            Assert.True(client.IsConnected);
        }
        finally
        {
            release.TrySetResult(true);
            await client.DisconnectAsync();
            await host.WaitAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task AuthenticationTimeoutClosesSilentConnection()
    {
        using var listener =
            new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new RemoteViewerClient(
            incomingFileReceiveDirectoryProvider: null,
            setFileDropListAsync: null,
            connectTimeout: TimeSpan.FromMilliseconds(250));

        Task connectTask = client.ConnectAsync(
            IPAddress.Loopback.ToString(),
            ((IPEndPoint)listener.LocalEndpoint).Port,
            LoopbackPassword,
            ViewerVideoMode.StableJpeg);
        using TcpClient silentHost =
            await listener.AcceptTcpClientAsync()
                .WaitAsync(TimeSpan.FromSeconds(2));

        TimeoutException failure =
            await Assert.ThrowsAsync<TimeoutException>(
                () => connectTask.WaitAsync(
                    TimeSpan.FromSeconds(2)));

        Assert.Contains("连接或认证超时", failure.Message);
        Assert.False(client.IsConnected);
        await client.DisconnectAsync()
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DisconnectCancelsAuthenticationBeforeItIsPublished()
    {
        using var listener =
            new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new RemoteViewerClient(
            incomingFileReceiveDirectoryProvider: null,
            setFileDropListAsync: null,
            connectTimeout: TimeSpan.FromSeconds(30));

        Task connectTask = client.ConnectAsync(
            IPAddress.Loopback.ToString(),
            ((IPEndPoint)listener.LocalEndpoint).Port,
            LoopbackPassword,
            ViewerVideoMode.StableJpeg);
        using TcpClient silentHost =
            await listener.AcceptTcpClientAsync()
                .WaitAsync(TimeSpan.FromSeconds(2));

        Task disconnectTask = client.DisconnectAsync();
        OperationCanceledException failure =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => connectTask.WaitAsync(
                    TimeSpan.FromSeconds(2)));
        await disconnectTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains("连接已取消", failure.Message);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task CallerCancellationClosesAuthenticationSocket()
    {
        using var listener =
            new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new RemoteViewerClient(
            incomingFileReceiveDirectoryProvider: null,
            setFileDropListAsync: null,
            connectTimeout: TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();

        Task connectTask = client.ConnectAsync(
            IPAddress.Loopback.ToString(),
            ((IPEndPoint)listener.LocalEndpoint).Port,
            LoopbackPassword,
            ViewerVideoMode.StableJpeg,
            cancellation.Token);
        using TcpClient silentHost =
            await listener.AcceptTcpClientAsync()
                .WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();
        OperationCanceledException failure =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => connectTask.WaitAsync(
                    TimeSpan.FromSeconds(2)));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task AuthenticatedSessionRejectionReportsBusyReason()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener =
            new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task serverTask = Task.Run(async () =>
        {
            using TcpClient hostClient =
                await listener.AcceptTcpClientAsync(
                    timeout.Token);
            NetworkUtils.ConfigureLowLatencyTcpClient(
                hostClient,
                32 * 1024,
                256 * 1024);
            await using NetworkStream stream =
                hostClient.GetStream();
            using var writeLock =
                new SemaphoreSlim(1, 1);
            ServerAuthenticationResult authentication =
                await Protocol.AuthenticateServerDetailedAsync(
                    stream,
                    LoopbackPassword,
                    timeout.Token);
            Assert.True(authentication.IsAuthenticated);
            using SecureSession session =
                authentication.Session!;

            for (int index = 0; index < 2; index++)
            {
                ProtocolMessage viewerMessage =
                    await Protocol.ReadMessageAsync(
                        stream,
                        session,
                        timeout.Token);
                Assert.Equal(
                    MessageType.Control,
                    viewerMessage.Type);
            }

            await Protocol.WriteMessageAsync(
                stream,
                MessageType.Control,
                RemoteMessageCodec.EncodeSessionRejected(
                    "被控端正由另一台查看端占用。"),
                session,
                writeLock,
                timeout.Token);
        }, timeout.Token);

        using var client = new RemoteViewerClient();
        var rejectionLog = CreateCompletionSource<string>();
        client.Log += message =>
        {
            if (message.Contains(
                    "另一台查看端占用",
                    StringComparison.Ordinal))
            {
                rejectionLog.TrySetResult(message);
            }
        };

        RemoteSessionRejectedException rejection =
            await Assert.ThrowsAsync<RemoteSessionRejectedException>(
                async () =>
                {
                    await client.ConnectAsync(
                        IPAddress.Loopback.ToString(),
                        ((IPEndPoint)listener.LocalEndpoint).Port,
                        LoopbackPassword,
                        ViewerVideoMode.StableJpeg);
                    await client.WaitForCurrentDeviceInfoAsync(
                        TimeSpan.FromSeconds(2),
                        timeout.Token);
                });
        string message = await rejectionLog.Task.WaitAsync(
            timeout.Token);

        Assert.Contains(
            "另一台查看端占用",
            rejection.Message,
            StringComparison.Ordinal);
        Assert.Equal(
            "连接中断：被控端正由另一台查看端占用。",
            message);
        await serverTask.WaitAsync(timeout.Token);
        for (int attempt = 0;
             attempt < 100 && client.IsConnected;
             attempt++)
        {
            await Task.Delay(10, timeout.Token);
        }

        RemoteSessionRejectedException rememberedRejection =
            await Assert.ThrowsAsync<RemoteSessionRejectedException>(
                () => client.WaitForCurrentDeviceInfoAsync(
                    TimeSpan.FromSeconds(1),
                    timeout.Token));
        Assert.Equal(rejection.Message, rememberedRejection.Message);
        await client.DisconnectAsync();
    }

    [Fact]
    public async Task QueuedOldCaptureTargetUiUpdatesAreRejectedAfterReconnect()
    {
        using var timeout =
            new CancellationTokenSource(
                TimeSpan.FromSeconds(12));
        using var firstListener =
            new TcpListener(IPAddress.Loopback, 0);
        using var secondListener =
            new TcpListener(IPAddress.Loopback, 0);
        firstListener.Start();
        secondListener.Start();
        var displayA = new CaptureTargetInfo(
            "display-a",
            "屏幕 A");
        var displayB = new CaptureTargetInfo(
            "display-b",
            "屏幕 B");
        var releaseFirst = CreateCompletionSource<bool>();
        var releaseSecond = CreateCompletionSource<bool>();
        Task firstHost = RunCaptureTargetHostUntilReleasedAsync(
            firstListener,
            displayA,
            releaseFirst.Task,
            timeout.Token);
        Task secondHost = RunCaptureTargetHostUntilReleasedAsync(
            secondListener,
            displayB,
            releaseSecond.Task,
            timeout.Token);

        using var client = new RemoteViewerClient();
        var queuedLists = new List<CaptureTargetsUpdate>();
        var queuedSelections =
            new List<CaptureTargetChangedUpdate>();
        var firstReceived = CreateCompletionSource<bool>();
        var secondReceived = CreateCompletionSource<bool>();
        var firstSelectionReceived =
            CreateCompletionSource<bool>();
        var secondSelectionReceived =
            CreateCompletionSource<bool>();
        var disconnected = CreateCompletionSource<bool>();
        object syncRoot = new();
        client.CaptureTargetsUpdated += update =>
        {
            lock (syncRoot)
            {
                queuedLists.Add(update);
                if (update.Targets.Any(
                        target => target.Id == displayA.Id))
                {
                    firstReceived.TrySetResult(true);
                }
                else if (update.Targets.Any(
                             target => target.Id == displayB.Id))
                {
                    secondReceived.TrySetResult(true);
                }
            }
        };
        client.CaptureTargetSelectionChanged += update =>
        {
            lock (syncRoot)
            {
                queuedSelections.Add(update);
                if (update.Target.Id == displayA.Id)
                {
                    firstSelectionReceived.TrySetResult(true);
                }
                else if (update.Target.Id == displayB.Id)
                {
                    secondSelectionReceived.TrySetResult(true);
                }
            }
        };
        client.ConnectedChanged += connected =>
        {
            if (!connected)
            {
                disconnected.TrySetResult(true);
            }
        };

        await client.ConnectAsync(
            IPAddress.Loopback.ToString(),
            ((IPEndPoint)firstListener.LocalEndpoint).Port,
            LoopbackPassword,
            ViewerVideoMode.StableJpeg);
        await firstReceived.Task.WaitAsync(timeout.Token);
        await firstSelectionReceived.Task.WaitAsync(
            timeout.Token);
        releaseFirst.TrySetResult(true);
        await disconnected.Task.WaitAsync(timeout.Token);
        await firstHost.WaitAsync(timeout.Token);

        await client.ConnectAsync(
            IPAddress.Loopback.ToString(),
            ((IPEndPoint)secondListener.LocalEndpoint).Port,
            LoopbackPassword,
            ViewerVideoMode.StableJpeg);
        await secondReceived.Task.WaitAsync(timeout.Token);
        await secondSelectionReceived.Task.WaitAsync(
            timeout.Token);

        IReadOnlyList<CaptureTargetInfo>? appliedTargets = null;
        CaptureTargetInfo? appliedSelection = null;
        lock (syncRoot)
        {
            foreach (CaptureTargetsUpdate update in queuedLists)
            {
                if (client.IsCurrentCaptureTargetsUpdate(update))
                {
                    appliedTargets = update.Targets;
                }
            }

            foreach (CaptureTargetChangedUpdate update in
                     queuedSelections)
            {
                if (client.IsCurrentCaptureTargetChangedUpdate(
                        update))
                {
                    appliedSelection = update.Target;
                }
            }
        }

        Assert.NotNull(appliedTargets);
        Assert.Equal(
            displayB,
            Assert.Single(appliedTargets));
        Assert.Equal(displayB, appliedSelection);

        releaseSecond.TrySetResult(true);
        await client.DisconnectAsync();
        await secondHost.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task CaptureAvailabilityUsesDedicatedEventAndPreservesClipboardStatus()
    {
        using var timeout =
            new CancellationTokenSource(
                TimeSpan.FromSeconds(10));
        using var listener =
            new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port =
            ((IPEndPoint)listener.LocalEndpoint).Port;
        var target = new CaptureTargetInfo(
            "display-2",
            "屏幕 2");
        CaptureTargetAvailabilityStatusData unavailable =
            CaptureTargetAvailabilityStatusCodec.Create(
                false,
                target);
        CaptureTargetAvailabilityStatusData recovered =
            CaptureTargetAvailabilityStatusCodec.Create(
                true,
                target);
        string unavailableWire =
            CaptureTargetAvailabilityStatusCodec.Encode(
                unavailable);
        string similarClipboard =
            "普通剪贴板文本\n" +
            "RemoteDesk.CaptureTargetStatus/v10|" +
            "unavailable|ZGlzcGxheS0y|5bGP5bmVIDI=";
        string recoveredWire =
            CaptureTargetAvailabilityStatusCodec.Encode(
                recovered);
        Task serverTask =
            RunCaptureAvailabilitySyntheticHostAsync(
                listener,
                [
                    unavailableWire,
                    similarClipboard,
                    recoveredWire
                ],
                timeout.Token);

        using var client = new RemoteViewerClient();
        var updates =
            new List<CaptureTargetAvailabilityUpdate>();
        var clipboardStatuses = new List<string>();
        var allReceived = CreateCompletionSource<bool>();
        object syncRoot = new();
        client.CaptureTargetAvailabilityChanged += update =>
        {
            lock (syncRoot)
            {
                updates.Add(update);
                if (updates.Count == 2 &&
                    clipboardStatuses.Count == 3)
                {
                    allReceived.TrySetResult(true);
                }
            }
        };
        client.ClipboardStatusReceived += message =>
        {
            lock (syncRoot)
            {
                clipboardStatuses.Add(message);
                if (updates.Count == 2 &&
                    clipboardStatuses.Count == 3)
                {
                    allReceived.TrySetResult(true);
                }
            }
        };

        await client.ConnectAsync(
            IPAddress.Loopback.ToString(),
            port,
            LoopbackPassword,
            ViewerVideoMode.StableJpeg);
        await allReceived.Task.WaitAsync(timeout.Token);

        lock (syncRoot)
        {
            Assert.Equal(2, updates.Count);
            Assert.False(updates[0].IsAvailable);
            Assert.True(updates[1].IsAvailable);
            Assert.All(updates, update =>
                Assert.True(
                    client
                        .IsCurrentCaptureTargetAvailabilityUpdate(
                            update)));
            Assert.Equal(
                [
                    unavailableWire,
                    similarClipboard,
                    recoveredWire
                ],
                clipboardStatuses);
        }

        await client.DisconnectAsync();
        await serverTask.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task ViewerDefersRemoteFileRequestUntilDeviceCapabilitiesAreNegotiated()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var viewerCapabilitiesReceived = CreateCompletionSource<RemoteDeviceCapabilities>();
        var releaseDeviceInfo = CreateCompletionSource<bool>();
        var requestReceived = CreateCompletionSource<RemoteControlKind>();
        Task serverTask = RunDelayedDeviceInfoSyntheticHostAsync(
            listener,
            LoopbackPassword,
            viewerCapabilitiesReceived,
            releaseDeviceInfo.Task,
            requestReceived,
            timeout.Token);

        using var client = new RemoteViewerClient();
        var deviceReceived = CreateCompletionSource<RemoteDeviceDescriptor>();
        client.DeviceInfoReceived += device => deviceReceived.TrySetResult(device);
        await client.ConnectAsync(
            IPAddress.Loopback.ToString(),
            port,
            LoopbackPassword,
            ViewerVideoMode.StableJpeg);

        RemoteDeviceCapabilities viewerCapabilities =
            await viewerCapabilitiesReceived.Task.WaitAsync(timeout.Token);
        Assert.True(viewerCapabilities.HasFlag(RemoteDeviceCapabilities.FileTransferPreview));
        Assert.True(viewerCapabilities.HasFlag(RemoteDeviceCapabilities.FileChecksum));
        Assert.True(viewerCapabilities.HasFlag(RemoteDeviceCapabilities.FileTransferCancel));
        Assert.True(viewerCapabilities.HasFlag(RemoteDeviceCapabilities.LowLatencyUdpVideo));
        Assert.True(viewerCapabilities.HasFlag(
            RemoteDeviceCapabilities.UdpVideoCongestionFeedback));
        Assert.True(viewerCapabilities.HasFlag(
            RemoteDeviceCapabilities.LowLatencyUdpVideoXorFec));
        // The protected UDP tier remains enabled, but the production Windows
        // viewer keeps independently decodable GOP1 until DDA/MF GOP2 startup
        // and presentation pass the real-machine acceptance budget.
        Assert.False(viewerCapabilities.HasFlag(
            RemoteDeviceCapabilities.ShortGopH264));

        ReturnedClipboardFileBatchResult earlyResult =
            await client.RequestRemoteClipboardFilesForDragOutAsync(
                timeout.Token,
                notifyRequest: false);
        Assert.Equal(ReturnedClipboardFileBatchOutcome.Busy, earlyResult.Outcome);
        Assert.False(requestReceived.Task.IsCompleted);

        releaseDeviceInfo.TrySetResult(true);
        RemoteDeviceDescriptor device = await deviceReceived.Task.WaitAsync(timeout.Token);
        Assert.True(device.Capabilities.HasFlag(RemoteDeviceCapabilities.FileTransferPreview));

        ReturnedClipboardFileBatchResult negotiatedResult =
            await client.RequestRemoteClipboardFilesForDragOutAsync(
                timeout.Token,
                notifyRequest: false);
        Assert.Equal(
            RemoteControlKind.FileTransferRequestClipboardFiles,
            await requestReceived.Task.WaitAsync(timeout.Token));
        Assert.Equal(ReturnedClipboardFileBatchOutcome.Failed, negotiatedResult.Outcome);

        await client.DisconnectAsync();
        await serverTask.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task ViewerClearsPeerCapabilitiesAndReservationsAfterSpontaneousDisconnect()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var firstListener = new TcpListener(IPAddress.Loopback, 0);
        firstListener.Start();
        int firstPort = ((IPEndPoint)firstListener.LocalEndpoint).Port;
        var releaseFirstConnection = CreateCompletionSource<bool>();
        Task firstServerTask = RunCapabilityHostUntilReleasedAsync(
            firstListener,
            LoopbackPassword,
            releaseFirstConnection.Task,
            timeout.Token);

        string receiveDirectory = Path.Combine(
            Path.GetTempPath(),
            $"RemoteDesk-reconnect-capabilities-{Guid.NewGuid():N}");
        Directory.CreateDirectory(receiveDirectory);
        using var client = new RemoteViewerClient(
            () => receiveDirectory,
            _ => Task.CompletedTask);
        var disconnected = CreateCompletionSource<bool>();
        var firstDeviceReceived = CreateCompletionSource<RemoteDeviceDescriptor>();
        client.DeviceInfoReceived += device => firstDeviceReceived.TrySetResult(device);
        client.ConnectedChanged += connected =>
        {
            if (!connected)
            {
                disconnected.TrySetResult(true);
            }
        };

        try
        {
            await client.ConnectAsync(
                IPAddress.Loopback.ToString(),
                firstPort,
                LoopbackPassword,
                ViewerVideoMode.StableJpeg);
            RemoteDeviceDescriptor firstDevice =
                await firstDeviceReceived.Task.WaitAsync(timeout.Token);
            Assert.True(firstDevice.Capabilities.HasFlag(
                RemoteDeviceCapabilities.ClipboardSequenceTracking));
            Assert.True(client.SupportsRemoteClipboardSequenceTracking);
            Assert.True(client.TryReserveSelfUpdatePackageRequest());

            releaseFirstConnection.TrySetResult(true);
            await disconnected.Task.WaitAsync(timeout.Token);
            await firstServerTask.WaitAsync(timeout.Token);
            Assert.False(client.SupportsRemoteClipboardSequenceTracking);
            Assert.True(client.TryReserveSelfUpdatePackageRequest());

            using var secondListener = new TcpListener(IPAddress.Loopback, 0);
            secondListener.Start();
            int secondPort = ((IPEndPoint)secondListener.LocalEndpoint).Port;
            var requestReceived = CreateCompletionSource<RemoteControlKind>();
            var hostReceivedStatus = CreateCompletionSource<string>();
            Task secondServerTask = RunFileReturningSyntheticHostAsync(
                secondListener,
                LoopbackPassword,
                "legacy-after-reconnect.txt",
                [7, 6, 5, 4],
                requestReceived,
                hostReceivedStatus,
                timeout.Token,
                sendReturnCompleteStatus: true,
                advertisedCapabilities: RemoteDeviceCapabilities.FileSend);
            var secondDeviceReceived = CreateCompletionSource<RemoteDeviceDescriptor>();
            client.DeviceInfoReceived += device =>
            {
                if (device.MachineName == "legacy-file-return-loopback")
                {
                    secondDeviceReceived.TrySetResult(device);
                }
            };

            await client.ConnectAsync(
                IPAddress.Loopback.ToString(),
                secondPort,
                LoopbackPassword,
                ViewerVideoMode.StableJpeg);
            await secondDeviceReceived.Task.WaitAsync(timeout.Token);
            ReturnedClipboardFileBatchResult result =
                await client.RequestRemoteClipboardFilesForDragOutAsync(timeout.Token);

            Assert.True(result.Success, result.Message);
            Assert.Equal(
                Path.Combine(receiveDirectory, "legacy-after-reconnect.txt"),
                Assert.Single(result.LocalPaths));
            await client.DisconnectAsync();
            await secondServerTask.WaitAsync(timeout.Token);
        }
        finally
        {
            await client.DisconnectAsync();
            try
            {
                Directory.Delete(receiveDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task DeviceInfoQualificationIsBoundToCurrentConnectionOwner()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var firstListener =
            new TcpListener(IPAddress.Loopback, 0);
        firstListener.Start();
        var releaseFirstHost = CreateCompletionSource<bool>();
        Task firstServerTask =
            RunAuthenticatedHostWithoutDeviceInfoAsync(
                firstListener,
                LoopbackPassword,
                releaseFirstHost.Task,
                timeout.Token);

        using var client = new RemoteViewerClient();
        await client.ConnectAsync(
            IPAddress.Loopback.ToString(),
            ((IPEndPoint)firstListener.LocalEndpoint).Port,
            LoopbackPassword,
            ViewerVideoMode.StableJpeg);
        Assert.False(
            await client.WaitForCurrentDeviceInfoAsync(
                TimeSpan.FromSeconds(2),
                timeout.Token));
        releaseFirstHost.TrySetResult(true);
        await firstServerTask.WaitAsync(timeout.Token);

        using var secondListener =
            new TcpListener(IPAddress.Loopback, 0);
        secondListener.Start();
        var releaseSecondHost = CreateCompletionSource<bool>();
        Task secondServerTask =
            RunCapabilityHostUntilReleasedAsync(
                secondListener,
                LoopbackPassword,
                releaseSecondHost.Task,
                timeout.Token,
                machineName: "qualified-generation-host");
        await client.ConnectAsync(
            IPAddress.Loopback.ToString(),
            ((IPEndPoint)secondListener.LocalEndpoint).Port,
            LoopbackPassword,
            ViewerVideoMode.StableJpeg);

        Assert.True(
            await client.WaitForCurrentDeviceInfoAsync(
                TimeSpan.FromSeconds(2),
                timeout.Token));

        releaseSecondHost.TrySetResult(true);
        await client.DisconnectAsync();
        await secondServerTask.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task DeviceInfoCallbackRunsBeforeImmediateDisconnectNotification()
    {
        using var timeout =
            new CancellationTokenSource(
                TimeSpan.FromSeconds(10));
        using var listener =
            new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task serverTask =
            RunCapabilityHostThenCloseAsync(
                listener,
                LoopbackPassword,
                timeout.Token);
        using var client = new RemoteViewerClient();
        var events = new List<string>();
        var eventsLock = new object();
        var disconnected = CreateCompletionSource<bool>();
        client.DeviceInfoUpdated += update =>
        {
            lock (eventsLock)
            {
                events.Add(
                    $"device:{update.ConnectionGeneration}");
            }
        };
        client.ConnectedChanged += connected =>
        {
            lock (eventsLock)
            {
                events.Add(
                    connected ? "connected" : "disconnected");
            }

            if (!connected)
            {
                disconnected.TrySetResult(true);
            }
        };

        await client.ConnectAsync(
            IPAddress.Loopback.ToString(),
            ((IPEndPoint)listener.LocalEndpoint).Port,
            LoopbackPassword,
            ViewerVideoMode.StableJpeg);
        await disconnected.Task.WaitAsync(timeout.Token);
        await serverTask.WaitAsync(timeout.Token);

        string[] ordered;
        lock (eventsLock)
        {
            ordered = events.ToArray();
        }

        int deviceIndex = Array.FindIndex(
            ordered,
            entry => entry.StartsWith(
                "device:",
                StringComparison.Ordinal));
        int disconnectedIndex = Array.IndexOf(
            ordered,
            "disconnected");
        Assert.InRange(deviceIndex, 0, ordered.Length - 1);
        Assert.True(
            disconnectedIndex > deviceIndex,
            string.Join(", ", ordered));
    }

    [Fact]
    public async Task ImmediateReconnectWaitsForPreviousReceiveLoopCleanupBeforePublishingNewOwner()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var firstListener = new TcpListener(IPAddress.Loopback, 0);
        firstListener.Start();
        var releaseFirstHost = CreateCompletionSource<bool>();
        Task firstServerTask = RunCapabilityHostUntilReleasedAsync(
            firstListener,
            LoopbackPassword,
            releaseFirstHost.Task,
            timeout.Token,
            machineName: "stale-owner-host");

        using var client = new RemoteViewerClient();
        var firstDeviceReceived = CreateCompletionSource<bool>();
        var secondDeviceReceived = CreateCompletionSource<bool>();
        client.DeviceInfoReceived += device =>
        {
            if (device.MachineName == "stale-owner-host")
            {
                firstDeviceReceived.TrySetResult(true);
            }
            else if (device.MachineName == "new-owner-host")
            {
                secondDeviceReceived.TrySetResult(true);
            }
        };

        using var releaseOldReceiveFinally = new ManualResetEventSlim(false);
        var oldReceiveFaultObserved = CreateCompletionSource<bool>();
        client.Log += message =>
        {
            if (message.StartsWith("连接中断：", StringComparison.Ordinal))
            {
                oldReceiveFaultObserved.TrySetResult(true);
                releaseOldReceiveFinally.Wait(TimeSpan.FromSeconds(5));
            }
        };

        try
        {
            await client.ConnectAsync(
                IPAddress.Loopback.ToString(),
                ((IPEndPoint)firstListener.LocalEndpoint).Port,
                LoopbackPassword,
                ViewerVideoMode.StableJpeg);
            await firstDeviceReceived.Task.WaitAsync(timeout.Token);
            Assert.True(client.SupportsRemoteClipboardSequenceTracking);

            FieldInfo tcpClientField = typeof(RemoteViewerClient).GetField(
                "_tcpClient",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var oldTcpClient = Assert.IsType<TcpClient>(tcpClientField.GetValue(client));
            oldTcpClient.Close();
            await oldReceiveFaultObserved.Task.WaitAsync(timeout.Token);

            using var secondListener = new TcpListener(IPAddress.Loopback, 0);
            secondListener.Start();
            var releaseSecondHost = CreateCompletionSource<bool>();
            Task secondServerTask = RunCapabilityHostUntilReleasedAsync(
                secondListener,
                LoopbackPassword,
                releaseSecondHost.Task,
                timeout.Token,
                machineName: "new-owner-host",
                advertisedCapabilities: RemoteDeviceCapabilities.FileSend);

            Task reconnectTask = client.ConnectAsync(
                IPAddress.Loopback.ToString(),
                ((IPEndPoint)secondListener.LocalEndpoint).Port,
                LoopbackPassword,
                ViewerVideoMode.StableJpeg);

            await Task.Delay(100, timeout.Token);
            Assert.False(reconnectTask.IsCompleted);
            releaseOldReceiveFinally.Set();

            await reconnectTask.WaitAsync(timeout.Token);
            await secondDeviceReceived.Task.WaitAsync(timeout.Token);
            Assert.False(client.SupportsRemoteClipboardSequenceTracking);

            releaseFirstHost.TrySetResult(true);
            releaseSecondHost.TrySetResult(true);
            await firstServerTask.WaitAsync(timeout.Token);
            await client.DisconnectAsync();
            await secondServerTask.WaitAsync(timeout.Token);
        }
        finally
        {
            releaseOldReceiveFinally.Set();
            releaseFirstHost.TrySetResult(true);
            await client.DisconnectAsync();
        }
    }

    [Fact]
    public async Task ReconnectFromDisconnectedCallbackCannotLoseFirstNewGenerationInputWakeup()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var firstListener = new TcpListener(IPAddress.Loopback, 0);
        using var secondListener = new TcpListener(IPAddress.Loopback, 0);
        firstListener.Start();
        secondListener.Start();

        var releaseFirstHost = CreateCompletionSource<bool>();
        Task firstServerTask = RunCapabilityHostUntilReleasedAsync(
            firstListener,
            LoopbackPassword,
            releaseFirstHost.Task,
            timeout.Token,
            machineName: "callback-reconnect-old-host");
        var receivedInputs = CreateCompletionSource<IReadOnlyList<RemoteInputCommand>>();
        Task secondServerTask = RunSingleInputBatchReceivingSyntheticHostAsync(
            secondListener,
            LoopbackPassword,
            expectedInputCount: 2,
            receivedInputs,
            timeout.Token);

        using var client = new RemoteViewerClient();
        var firstDeviceReceived = CreateCompletionSource<bool>();
        var reconnected = CreateCompletionSource<bool>();
        client.DeviceInfoReceived += device =>
        {
            if (device.MachineName == "callback-reconnect-old-host")
            {
                firstDeviceReceived.TrySetResult(true);
            }
        };

        int reconnectStarted = 0;
        client.ConnectedChanged += connected =>
        {
            if (connected || Interlocked.Exchange(ref reconnectStarted, 1) != 0)
            {
                return;
            }

            try
            {
                client.ConnectAsync(
                        IPAddress.Loopback.ToString(),
                        ((IPEndPoint)secondListener.LocalEndpoint).Port,
                        LoopbackPassword,
                        ViewerVideoMode.StableJpeg)
                    .GetAwaiter()
                    .GetResult();
                reconnected.TrySetResult(true);
            }
            catch (Exception ex)
            {
                reconnected.TrySetException(ex);
            }
        };

        try
        {
            await client.ConnectAsync(
                IPAddress.Loopback.ToString(),
                ((IPEndPoint)firstListener.LocalEndpoint).Port,
                LoopbackPassword,
                ViewerVideoMode.StableJpeg);
            await firstDeviceReceived.Task.WaitAsync(timeout.Token);

            releaseFirstHost.TrySetResult(true);
            await reconnected.Task.WaitAsync(timeout.Token);

            await client.SendInputsAsync([
                RemoteInputCommand.KeyDown(65),
                RemoteInputCommand.KeyUp(65)]);
            await client.FlushInputAsync(timeout.Token);

            IReadOnlyList<RemoteInputCommand> inputs =
                await receivedInputs.Task.WaitAsync(timeout.Token);
            Assert.Collection(
                inputs,
                input =>
                {
                    Assert.Equal(RemoteInputKind.KeyDown, input.Kind);
                    Assert.Equal(65, input.Data);
                },
                input =>
                {
                    Assert.Equal(RemoteInputKind.KeyUp, input.Kind);
                    Assert.Equal(65, input.Data);
                });

            await firstServerTask.WaitAsync(timeout.Token);
            await client.DisconnectAsync();
            await secondServerTask.WaitAsync(timeout.Token);
        }
        finally
        {
            releaseFirstHost.TrySetResult(true);
            await client.DisconnectAsync();
        }
    }

    [Fact]
    public async Task ConnectionNotificationsStayOrderedWhenPeerClosesDuringConnectedCallback()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var releaseHost = CreateCompletionSource<bool>();
        Task serverTask = RunCapabilityHostUntilReleasedAsync(
            listener,
            LoopbackPassword,
            releaseHost.Task,
            timeout.Token,
            machineName: "notification-order-host");

        using var client = new RemoteViewerClient();
        var connectedCallbackEntered = CreateCompletionSource<bool>();
        var disconnectedCallbackObserved = CreateCompletionSource<bool>();
        using var releaseConnectedCallback = new ManualResetEventSlim(false);
        var notificationSequence = new List<bool>();
        object notificationLock = new();
        client.ConnectedChanged += connected =>
        {
            lock (notificationLock)
            {
                notificationSequence.Add(connected);
            }

            if (connected)
            {
                connectedCallbackEntered.TrySetResult(true);
                releaseConnectedCallback.Wait(TimeSpan.FromSeconds(5));
            }
            else
            {
                disconnectedCallbackObserved.TrySetResult(true);
            }
        };

        try
        {
            await client.ConnectAsync(
                IPAddress.Loopback.ToString(),
                ((IPEndPoint)listener.LocalEndpoint).Port,
                LoopbackPassword,
                ViewerVideoMode.StableJpeg);
            await connectedCallbackEntered.Task.WaitAsync(timeout.Token);

            releaseHost.TrySetResult(true);
            await serverTask.WaitAsync(timeout.Token);
            for (int attempt = 0; attempt < 100 && client.IsConnected; attempt++)
            {
                await Task.Delay(10, timeout.Token);
            }

            Assert.False(client.IsConnected);
            Assert.False(disconnectedCallbackObserved.Task.IsCompleted);

            releaseConnectedCallback.Set();
            await disconnectedCallbackObserved.Task.WaitAsync(timeout.Token);
            lock (notificationLock)
            {
                Assert.Equal([true, false], notificationSequence);
            }
        }
        finally
        {
            releaseConnectedCallback.Set();
            releaseHost.TrySetResult(true);
            await client.DisconnectAsync();
        }
    }

    [Fact]
    public async Task ViewerFlushesRejectedPreviewBeforeImmediateRetryRequest()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var wireOrder = CreateCompletionSource<IReadOnlyList<RemoteControlKind>>();
        var rejectObserved = CreateCompletionSource<bool>();
        var allowRejectAcknowledgement = CreateCompletionSource<bool>();
        Task serverTask = RunRejectThenRetrySyntheticHostAsync(
            listener,
            LoopbackPassword,
            wireOrder,
            rejectObserved,
            allowRejectAcknowledgement.Task,
            timeout.Token);

        using var client = new RemoteViewerClient();
        var deviceReceived = CreateCompletionSource<bool>();
        var firstCompleted = CreateCompletionSource<ReturnedClipboardFileBatchResult>();
        var secondCompleted = CreateCompletionSource<ReturnedClipboardFileBatchResult>();
        var secondRequestPublished = CreateCompletionSource<Task<bool>>();
        int completionCount = 0;
        client.DeviceInfoReceived += _ => deviceReceived.TrySetResult(true);
        client.ConfirmRemoteClipboardFileTransfer = (_items, _note) => false;
        client.RemoteClipboardFileBatchCompleted += result =>
        {
            if (Interlocked.Increment(ref completionCount) == 1)
            {
                firstCompleted.TrySetResult(result);
                secondRequestPublished.TrySetResult(
                    client.RequestRemoteClipboardFilesAsync(notifyRequest: false));
            }
            else
            {
                secondCompleted.TrySetResult(result);
            }
        };

        await client.ConnectAsync(
            IPAddress.Loopback.ToString(),
            port,
            LoopbackPassword,
            ViewerVideoMode.StableJpeg);
        await deviceReceived.Task.WaitAsync(timeout.Token);
        Assert.True(await client.RequestRemoteClipboardFilesAsync(notifyRequest: false));

        await rejectObserved.Task.WaitAsync(timeout.Token);
        await Task.Delay(100, timeout.Token);
        Assert.False(firstCompleted.Task.IsCompleted);
        Assert.True(client.IsRemoteClipboardFileRequestPending);
        allowRejectAcknowledgement.TrySetResult(true);

        ReturnedClipboardFileBatchResult first =
            await firstCompleted.Task.WaitAsync(timeout.Token);
        Assert.Equal(ReturnedClipboardFileBatchOutcome.Cancelled, first.Outcome);
        Task<bool> secondRequest = await secondRequestPublished.Task.WaitAsync(timeout.Token);
        Assert.True(await secondRequest.WaitAsync(timeout.Token));
        ReturnedClipboardFileBatchResult second =
            await secondCompleted.Task.WaitAsync(timeout.Token);
        Assert.Equal(ReturnedClipboardFileBatchOutcome.Failed, second.Outcome);
        Assert.Equal(
            [
                RemoteControlKind.FileTransferRejectClipboardFiles,
                RemoteControlKind.FileTransferRequestClipboardFiles
            ],
            await wireOrder.Task.WaitAsync(timeout.Token));

        await client.DisconnectAsync();
        await serverTask.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task ViewerConnectsToCompatibleHostAndReceivesAndroidMetadataTargetsAndFrame()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var viewerInfoReceived = CreateCompletionSource<RemoteVideoCodecs>();
        Task serverTask = RunSyntheticHostAsync(listener, LoopbackPassword, viewerInfoReceived, timeout.Token);

        using var client = new RemoteViewerClient();
        var connectedChanged = CreateCompletionSource<bool>();
        var deviceReceived = CreateCompletionSource<RemoteDeviceDescriptor>();
        var targetsReceived = CreateCompletionSource<IReadOnlyList<CaptureTargetInfo>>();
        var targetChanged = CreateCompletionSource<CaptureTargetInfo>();
        var frameReceived = CreateCompletionSource<RemoteFrame>();

        client.ConnectedChanged += connected => connectedChanged.TrySetResult(connected);
        client.DeviceInfoReceived += device => deviceReceived.TrySetResult(device);
        client.CaptureTargetsReceived += targets => targetsReceived.TrySetResult(targets);
        client.CaptureTargetChanged += target => targetChanged.TrySetResult(target);
        client.FrameReceived += frame => frameReceived.TrySetResult(frame);

        await client.ConnectAsync(IPAddress.Loopback.ToString(), port, LoopbackPassword, ViewerVideoMode.StableJpeg);

        Assert.True(await connectedChanged.Task.WaitAsync(timeout.Token));
        Assert.Equal(RemoteVideoCodecs.Jpeg, await viewerInfoReceived.Task.WaitAsync(timeout.Token));

        RemoteDeviceDescriptor device = await deviceReceived.Task.WaitAsync(timeout.Token);
        Assert.Equal("android-loopback", device.MachineName);
        Assert.Equal(RemoteDevicePlatforms.Android, device.Platform);
        Assert.True(device.Capabilities.HasFlag(RemoteDeviceCapabilities.RemoteDesktop));
        Assert.True(device.Capabilities.HasFlag(RemoteDeviceCapabilities.InputControl));
        Assert.True(device.Capabilities.HasFlag(RemoteDeviceCapabilities.FileReceive));

        IReadOnlyList<CaptureTargetInfo> targets = await targetsReceived.Task.WaitAsync(timeout.Token);
        CaptureTargetInfo onlyTarget = Assert.Single(targets);
        Assert.Equal("android-screen", onlyTarget.Id);
        Assert.Equal("Android Screen", onlyTarget.DisplayName);

        CaptureTargetInfo changed = await targetChanged.Task.WaitAsync(timeout.Token);
        Assert.Equal("android-screen", changed.Id);

        RemoteFrame frame = await frameReceived.Task.WaitAsync(timeout.Token);
        Assert.Equal(2, frame.Width);
        Assert.Equal(2, frame.Height);
        Assert.Equal(RemoteFrameEncoding.Jpeg, frame.Encoding);
        Assert.Equal(RemoteFrameFlags.KeyFrame, frame.Flags);
        Assert.True(frame.EncodedLength > 0);

        await client.DisconnectAsync();
        await serverTask.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task ViewerSendsFileTransferStartChunksAndCompleteToCompatibleHost()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        byte[] expectedBytes = Enumerable.Range(0, RemoteMessageCodec.FileTransferChunkBytes + 17)
            .Select(index => (byte)(index % 251))
            .ToArray();
        string tempFile = Path.Combine(Path.GetTempPath(), $"RemoteDesk-loopback-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(tempFile, expectedBytes, timeout.Token);

        var receivedFile = CreateCompletionSource<ReceivedLoopbackFile>();
        Task serverTask = RunFileReceivingSyntheticHostAsync(listener, LoopbackPassword, receivedFile, timeout.Token);

        using var client = new RemoteViewerClient();
        var remoteStatus = CreateCompletionSource<string>();
        client.FileTransferStatusReceived += (success, message) =>
        {
            if (success && message.Contains("loopback host saved", StringComparison.OrdinalIgnoreCase))
            {
                remoteStatus.TrySetResult(message);
            }
        };

        try
        {
            await client.ConnectAsync(IPAddress.Loopback.ToString(), port, LoopbackPassword, ViewerVideoMode.StableJpeg);
            await client.SendFileToRemoteAsync(tempFile);

            ReceivedLoopbackFile file = await receivedFile.Task.WaitAsync(timeout.Token);
            Assert.Equal(Path.GetFileName(tempFile), file.FileName);
            Assert.Equal(expectedBytes.Length, file.FileLength);
            Assert.True(file.ChunkCount >= 2);
            Assert.Equal(expectedBytes, file.Bytes);

            Assert.Equal("loopback host saved file", await remoteStatus.Task.WaitAsync(timeout.Token));
            await client.DisconnectAsync();
            await serverTask.WaitAsync(timeout.Token);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task ViewerSendsRemoteUpdateStartWhenHostAdvertisesRemoteUpdate()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        byte[] expectedBytes = Enumerable.Range(0, 4096)
            .Select(index => (byte)(index % 233))
            .ToArray();
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"RemoteDesk-update-loopback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        string updatePackage = Path.Combine(tempDirectory, "RemoteDesk.exe");
        await File.WriteAllBytesAsync(updatePackage, expectedBytes, timeout.Token);

        var receivedFile = CreateCompletionSource<ReceivedLoopbackFile>();
        Task serverTask = RunFileReceivingSyntheticHostAsync(
            listener,
            LoopbackPassword,
            receivedFile,
            timeout.Token,
            advertiseChecksum: true,
            expectChecksum: true,
            advertisedCapabilities:
                RemoteDeviceCapabilities.FileReceive |
                RemoteDeviceCapabilities.FileChecksum |
                RemoteDeviceCapabilities.FileTransferCancel |
                RemoteDeviceCapabilities.RemoteUpdate,
            expectedStartKind: RemoteControlKind.RemoteUpdateStart);

        using var client = new RemoteViewerClient();
        try
        {
            var deviceInfoReceived = CreateCompletionSource<RemoteDeviceDescriptor>();
            client.DeviceInfoReceived += device => deviceInfoReceived.TrySetResult(device);
            await client.ConnectAsync(IPAddress.Loopback.ToString(), port, LoopbackPassword, ViewerVideoMode.StableJpeg);
            RemoteDeviceDescriptor device = await deviceInfoReceived.Task.WaitAsync(timeout.Token);
            Assert.True(device.Capabilities.HasFlag(RemoteDeviceCapabilities.RemoteUpdate));
            await client.SendRemoteUpdateAsync(updatePackage);

            ReceivedLoopbackFile file = await receivedFile.Task.WaitAsync(timeout.Token);
            Assert.Equal("RemoteDesk.exe", file.FileName);
            Assert.Equal(expectedBytes.Length, file.FileLength);
            Assert.Equal(expectedBytes, file.Bytes);

            await client.DisconnectAsync();
            await serverTask.WaitAsync(timeout.Token);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ViewerSendsFileChecksumWhenHostAdvertisesChecksumCapability()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        byte[] expectedBytes = Enumerable.Range(0, RemoteMessageCodec.FileTransferChunkBytes + 31)
            .Select(index => (byte)(index % 241))
            .ToArray();
        string tempFile = Path.Combine(Path.GetTempPath(), $"RemoteDesk-checksum-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(tempFile, expectedBytes, timeout.Token);

        var receivedFile = CreateCompletionSource<ReceivedLoopbackFile>();
        Task serverTask = RunFileReceivingSyntheticHostAsync(
            listener,
            LoopbackPassword,
            receivedFile,
            timeout.Token,
            advertiseChecksum: true,
            expectChecksum: true);

        using var client = new RemoteViewerClient();
        var deviceInfoReceived = CreateCompletionSource<RemoteDeviceDescriptor>();
        client.DeviceInfoReceived += device => deviceInfoReceived.TrySetResult(device);

        try
        {
            await client.ConnectAsync(IPAddress.Loopback.ToString(), port, LoopbackPassword, ViewerVideoMode.StableJpeg);
            RemoteDeviceDescriptor device = await deviceInfoReceived.Task.WaitAsync(timeout.Token);
            Assert.True(device.Capabilities.HasFlag(RemoteDeviceCapabilities.FileChecksum));
            Assert.True(device.Capabilities.HasFlag(RemoteDeviceCapabilities.FileTransferCancel));

            await client.SendFileToRemoteAsync(tempFile);

            ReceivedLoopbackFile file = await receivedFile.Task.WaitAsync(timeout.Token);
            Assert.Equal(Path.GetFileName(tempFile), file.FileName);
            Assert.Equal(expectedBytes, file.Bytes);

            await client.DisconnectAsync();
            await serverTask.WaitAsync(timeout.Token);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task ViewerArchivesDirectoryBeforeSendingToCompatibleHost()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        string sourceDirectory = Path.Combine(Path.GetTempPath(), $"RemoteDesk-folder-{Guid.NewGuid():N}");
        string nestedDirectory = Path.Combine(sourceDirectory, "nested");
        Directory.CreateDirectory(nestedDirectory);
        string nestedFile = Path.Combine(nestedDirectory, "hello.txt");
        await File.WriteAllTextAsync(nestedFile, "folder payload", timeout.Token);

        var receivedFile = CreateCompletionSource<ReceivedLoopbackFile>();
        Task serverTask = RunFileReceivingSyntheticHostAsync(listener, LoopbackPassword, receivedFile, timeout.Token);

        using var client = new RemoteViewerClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback.ToString(), port, LoopbackPassword, ViewerVideoMode.StableJpeg);
            RemoteFilePasteResult result = await client.SendFilesToRemoteAsync([sourceDirectory], maxFiles: 32);

            Assert.Equal(1, result.SentFiles);
            Assert.Equal(0, result.FailedFiles);
            Assert.Equal(1, result.ArchivedDirectories);

            ReceivedLoopbackFile file = await receivedFile.Task.WaitAsync(timeout.Token);
            Assert.Equal($"{Path.GetFileName(sourceDirectory)}.zip", file.FileName);
            Assert.Equal(file.FileLength, file.Bytes.Length);

            using var archiveStream = new MemoryStream(file.Bytes);
            using var archive = new System.IO.Compression.ZipArchive(
                archiveStream,
                System.IO.Compression.ZipArchiveMode.Read);
            string entryName = $"{Path.GetFileName(sourceDirectory)}/nested/hello.txt";
            System.IO.Compression.ZipArchiveEntry? entry = archive.GetEntry(entryName);
            Assert.NotNull(entry);
            using Stream entryStream = entry.Open();
            using var reader = new StreamReader(entryStream);
            Assert.Equal("folder payload", await reader.ReadToEndAsync(timeout.Token));

            await client.DisconnectAsync();
            await serverTask.WaitAsync(timeout.Token);
        }
        finally
        {
            try
            {
                Directory.Delete(sourceDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task ViewerRequestsRemoteClipboardFilesAndReceivesFileToLocalDirectory()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        byte[] expectedBytes = Enumerable.Range(0, RemoteMessageCodec.FileTransferChunkBytes + 23)
            .Select(index => (byte)(index % 239))
            .ToArray();
        string receiveDirectory = Path.Combine(
            Path.GetTempPath(),
            $"RemoteDesk-return-{Guid.NewGuid():N}");
        Directory.CreateDirectory(receiveDirectory);

        var requestReceived = CreateCompletionSource<RemoteControlKind>();
        var hostReceivedStatus = CreateCompletionSource<string>();
        Task serverTask = RunFileReturningSyntheticHostAsync(
            listener,
            LoopbackPassword,
            "remote-report.txt",
            expectedBytes,
            requestReceived,
            hostReceivedStatus,
            timeout.Token);

        using var client = new RemoteViewerClient(() => receiveDirectory);
        var localSaveStatus = CreateCompletionSource<string>();
        var receiveProgressStatus = CreateCompletionSource<string>();
        client.FileTransferStatusReceived += (success, message) =>
        {
            if (success && message.Contains("文件已保存到本机", StringComparison.Ordinal))
            {
                localSaveStatus.TrySetResult(message);
            }

            if (success && message.Contains("正在接收文件：remote-report.txt", StringComparison.Ordinal))
            {
                receiveProgressStatus.TrySetResult(message);
            }
        };

        try
        {
            await client.ConnectAsync(IPAddress.Loopback.ToString(), port, LoopbackPassword, ViewerVideoMode.StableJpeg);
            Assert.True(await client.RequestRemoteClipboardFilesAsync());

            Assert.Equal(
                RemoteControlKind.FileTransferRequestClipboardFiles,
                await requestReceived.Task.WaitAsync(timeout.Token));

            string savedPath = Path.Combine(receiveDirectory, "remote-report.txt");
            Assert.Contains(" 99% ", await receiveProgressStatus.Task.WaitAsync(timeout.Token), StringComparison.Ordinal);
            string saveStatus = await localSaveStatus.Task.WaitAsync(timeout.Token);
            Assert.Contains(savedPath, saveStatus, StringComparison.Ordinal);
            Assert.True(File.Exists(savedPath));
            Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(savedPath, timeout.Token));
            Assert.Contains("文件已保存到本机", await hostReceivedStatus.Task.WaitAsync(timeout.Token), StringComparison.Ordinal);

            await client.DisconnectAsync();
            await serverTask.WaitAsync(timeout.Token);
        }
        finally
        {
            try
            {
                Directory.Delete(receiveDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task ViewerAcceptsLegacyFileReturnWithoutPreviewCapability()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string receiveDirectory = Path.Combine(
            Path.GetTempPath(),
            $"RemoteDesk-legacy-return-{Guid.NewGuid():N}");
        Directory.CreateDirectory(receiveDirectory);

        var requestReceived = CreateCompletionSource<RemoteControlKind>();
        var hostReceivedStatus = CreateCompletionSource<string>();
        Task serverTask = RunFileReturningSyntheticHostAsync(
            listener,
            LoopbackPassword,
            "legacy-return.txt",
            [1, 2, 3, 4],
            requestReceived,
            hostReceivedStatus,
            timeout.Token,
            sendReturnCompleteStatus: true,
            advertisedCapabilities: RemoteDeviceCapabilities.FileSend);

        using var client = new RemoteViewerClient(
            () => receiveDirectory,
            _ => Task.CompletedTask);
        var deviceInfoReceived = CreateCompletionSource<RemoteDeviceDescriptor>();
        client.DeviceInfoReceived += device => deviceInfoReceived.TrySetResult(device);
        try
        {
            await client.ConnectAsync(
                IPAddress.Loopback.ToString(),
                port,
                LoopbackPassword,
                ViewerVideoMode.StableJpeg);
            await deviceInfoReceived.Task.WaitAsync(timeout.Token);

            ReturnedClipboardFileBatchResult result =
                await client.RequestRemoteClipboardFilesForDragOutAsync(timeout.Token);

            Assert.True(result.Success, result.Message);
            Assert.Equal(ReturnedClipboardFileBatchOutcome.Succeeded, result.Outcome);
            Assert.Equal(
                Path.Combine(receiveDirectory, "legacy-return.txt"),
                Assert.Single(result.LocalPaths));
            Assert.Equal(
                RemoteControlKind.FileTransferRequestClipboardFiles,
                await requestReceived.Task.WaitAsync(timeout.Token));

            await client.DisconnectAsync();
            await serverTask.WaitAsync(timeout.Token);
        }
        finally
        {
            try
            {
                Directory.Delete(receiveDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task ViewerDoesNotExposePartialLegacyBatchWhenTerminalStatusFailed()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string receiveDirectory = Path.Combine(
            Path.GetTempPath(),
            $"RemoteDesk-partial-legacy-return-{Guid.NewGuid():N}");
        Directory.CreateDirectory(receiveDirectory);
        var requestReceived = CreateCompletionSource<RemoteControlKind>();
        var hostReceivedStatus = CreateCompletionSource<string>();
        Task serverTask = RunFileReturningSyntheticHostAsync(
            listener,
            LoopbackPassword,
            "partial-legacy-return.txt",
            [4, 3, 2, 1],
            requestReceived,
            hostReceivedStatus,
            timeout.Token,
            sendReturnCompleteStatus: true,
            advertisedCapabilities: RemoteDeviceCapabilities.FileSend,
            terminalSuccess: false,
            terminalMessage: "远端文件回传失败：1 个成功，1 个失败");
        int clipboardWrites = 0;
        using var client = new RemoteViewerClient(
            () => receiveDirectory,
            _ =>
            {
                Interlocked.Increment(ref clipboardWrites);
                return Task.CompletedTask;
            });
        try
        {
            await client.ConnectAsync(
                IPAddress.Loopback.ToString(),
                port,
                LoopbackPassword,
                ViewerVideoMode.StableJpeg);

            ReturnedClipboardFileBatchResult result =
                await client.RequestRemoteClipboardFilesForDragOutAsync(timeout.Token);

            Assert.Equal(ReturnedClipboardFileBatchOutcome.Failed, result.Outcome);
            Assert.Empty(result.LocalPaths);
            Assert.False(result.ClipboardUpdated);
            Assert.Equal(0, clipboardWrites);
            Assert.Contains("部分文件仅保留在接收目录", result.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(receiveDirectory, "partial-legacy-return.txt")));

            await client.DisconnectAsync();
            await serverTask.WaitAsync(timeout.Token);
        }
        finally
        {
            try
            {
                Directory.Delete(receiveDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task ViewerAbortsActiveLegacyPartialFileWhenTerminalStatusClaimsSuccess()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string receiveDirectory = Path.Combine(
            Path.GetTempPath(),
            $"RemoteDesk-active-partial-legacy-return-{Guid.NewGuid():N}");
        Directory.CreateDirectory(receiveDirectory);
        var releaseHost = CreateCompletionSource<bool>();
        Task serverTask = RunActivePartialLegacyReturnSyntheticHostAsync(
            listener,
            LoopbackPassword,
            "active-partial.bin",
            [8, 7, 6, 5],
            releaseHost.Task,
            timeout.Token);

        int clipboardWrites = 0;
        using var client = new RemoteViewerClient(
            () => receiveDirectory,
            _ =>
            {
                Interlocked.Increment(ref clipboardWrites);
                return Task.CompletedTask;
            });
        var deviceInfoReceived = CreateCompletionSource<RemoteDeviceDescriptor>();
        client.DeviceInfoReceived += device => deviceInfoReceived.TrySetResult(device);
        try
        {
            await client.ConnectAsync(
                IPAddress.Loopback.ToString(),
                port,
                LoopbackPassword,
                ViewerVideoMode.StableJpeg);
            await deviceInfoReceived.Task.WaitAsync(timeout.Token);

            ReturnedClipboardFileBatchResult result =
                await client.RequestRemoteClipboardFilesForDragOutAsync(timeout.Token);

            Assert.Equal(ReturnedClipboardFileBatchOutcome.Failed, result.Outcome);
            Assert.Empty(result.LocalPaths);
            Assert.False(result.ClipboardUpdated);
            Assert.Equal(0, clipboardWrites);
            Assert.Contains("确认清单不一致", result.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(receiveDirectory, "active-partial.bin")));
            Assert.DoesNotContain(
                Directory.GetFiles(receiveDirectory),
                path => FileTransferReceiver.IsOwnedTemporaryFileName(Path.GetFileName(path)));

            releaseHost.TrySetResult(true);
            await client.DisconnectAsync();
            await serverTask.WaitAsync(timeout.Token);
        }
        finally
        {
            releaseHost.TrySetResult(true);
            await client.DisconnectAsync();
            try
            {
                Directory.Delete(receiveDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task ViewerRejectsDirectReturnWhenPeerAdvertisedPreviewCapability()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string receiveDirectory = Path.Combine(
            Path.GetTempPath(),
            $"RemoteDesk-unconfirmed-return-{Guid.NewGuid():N}");
        Directory.CreateDirectory(receiveDirectory);

        var requestReceived = CreateCompletionSource<RemoteControlKind>();
        var unusedSaveStatus = CreateCompletionSource<string>();
        Task serverTask = RunFileReturningSyntheticHostAsync(
            listener,
            LoopbackPassword,
            "unconfirmed.txt",
            [1, 2, 3, 4],
            requestReceived,
            unusedSaveStatus,
            timeout.Token,
            sendReturnCompleteStatus: true,
            advertisedCapabilities:
                RemoteDeviceCapabilities.FileSend |
                RemoteDeviceCapabilities.FileTransferPreview,
            terminalStatusWithoutWaitingForSave: true);

        using var client = new RemoteViewerClient(
            () => receiveDirectory,
            _ => Task.CompletedTask);
        var deviceInfoReceived = CreateCompletionSource<bool>();
        client.DeviceInfoReceived += _ => deviceInfoReceived.TrySetResult(true);
        try
        {
            await client.ConnectAsync(
                IPAddress.Loopback.ToString(),
                port,
                LoopbackPassword,
                ViewerVideoMode.StableJpeg);
            await deviceInfoReceived.Task.WaitAsync(timeout.Token);

            ReturnedClipboardFileBatchResult result =
                await client.RequestRemoteClipboardFilesForDragOutAsync(timeout.Token);

            Assert.Equal(ReturnedClipboardFileBatchOutcome.Failed, result.Outcome);
            Assert.Empty(result.LocalPaths);
            Assert.Contains("确认清单不一致", result.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(receiveDirectory, "unconfirmed.txt")));
            Assert.Equal(
                RemoteControlKind.FileTransferRequestClipboardFiles,
                await requestReceived.Task.WaitAsync(timeout.Token));

            await client.DisconnectAsync();
            await serverTask.WaitAsync(timeout.Token);
        }
        finally
        {
            await client.DisconnectAsync();
            try
            {
                Directory.Delete(receiveDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task ViewerCoalescesDuplicateRemoteClipboardFileRequestsUntilTerminalStatus()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var firstRequestCount = CreateCompletionSource<int>();
        var secondRequestCount = CreateCompletionSource<int>();
        Task serverTask = RunDuplicateRequestGuardSyntheticHostAsync(
            listener,
            LoopbackPassword,
            firstRequestCount,
            secondRequestCount,
            timeout.Token);

        using var client = new RemoteViewerClient();
        var duplicateStatus = CreateCompletionSource<string>();
        var firstTerminalStatus = CreateCompletionSource<string>();
        var secondTerminalStatus = CreateCompletionSource<string>();
        int terminalStatuses = 0;
        var pendingTransitions = new List<bool>();
        object transitionLock = new();
        client.RemoteClipboardFileRequestPendingChanged += pending =>
        {
            lock (transitionLock)
            {
                pendingTransitions.Add(pending);
            }
        };
        client.FileTransferStatusReceived += (_success, message) =>
        {
            if (message.Contains("无需重复请求", StringComparison.Ordinal))
            {
                duplicateStatus.TrySetResult(message);
            }

            if (RemoteViewerClient.IsReturnedClipboardFileBatchEmptyStatus(message))
            {
                if (Interlocked.Increment(ref terminalStatuses) == 1)
                {
                    firstTerminalStatus.TrySetResult(message);
                }
                else
                {
                    secondTerminalStatus.TrySetResult(message);
                }
            }
        };

        await client.ConnectAsync(IPAddress.Loopback.ToString(), port, LoopbackPassword, ViewerVideoMode.StableJpeg);
        Assert.False(client.IsRemoteClipboardFileRequestPending);

        Assert.True(await client.RequestRemoteClipboardFilesAsync());
        Assert.True(client.IsRemoteClipboardFileRequestPending);
        Assert.True(await client.RequestRemoteClipboardFilesAsync());
        ReturnedClipboardFileBatchResult busyDragResult =
            await client.RequestRemoteClipboardFilesForDragOutAsync(timeout.Token, notifyRequest: false);
        Assert.True(busyDragResult.Busy);
        Assert.False(busyDragResult.Success);
        Assert.Empty(busyDragResult.LocalPaths);
        Assert.Contains("无需重复请求", await duplicateStatus.Task.WaitAsync(timeout.Token), StringComparison.Ordinal);
        await client.ReadRemoteClipboardAsync(notifyRequest: false);

        Assert.Equal(1, await firstRequestCount.Task.WaitAsync(timeout.Token));
        await firstTerminalStatus.Task.WaitAsync(timeout.Token);
        Assert.False(client.IsRemoteClipboardFileRequestPending);

        Assert.True(await client.RequestRemoteClipboardFilesAsync());
        Assert.True(client.IsRemoteClipboardFileRequestPending);
        await client.ReadRemoteClipboardAsync(notifyRequest: false);

        Assert.Equal(1, await secondRequestCount.Task.WaitAsync(timeout.Token));
        await secondTerminalStatus.Task.WaitAsync(timeout.Token);
        Assert.False(client.IsRemoteClipboardFileRequestPending);

        lock (transitionLock)
        {
            Assert.Equal([true, false, true, false], pendingTransitions);
        }

        await client.DisconnectAsync();
        await serverTask.WaitAsync(timeout.Token);
    }

    [Theory]
    [InlineData(true, (int)RemoteControlKind.FileTransferConfirmClipboardFiles)]
    [InlineData(false, (int)RemoteControlKind.FileTransferRejectClipboardFiles)]
    public async Task ViewerConfirmsOrRejectsRemoteClipboardFilePreview(
        bool confirmPreview,
        int expectedDecision)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var decisionReceived = CreateCompletionSource<RemoteControlKind>();
        Task serverTask = RunPreviewReturningSyntheticHostAsync(
            listener,
            LoopbackPassword,
            decisionReceived,
            timeout.Token,
            previewTransferName: @"..\remote-preview.txt");

        using var client = new RemoteViewerClient();
        var deviceReceived = CreateCompletionSource<RemoteDeviceDescriptor>();
        var previewReceived = CreateCompletionSource<IReadOnlyList<FileTransferConfirmationItem>>();
        var waitingStatus = CreateCompletionSource<string>();
        var decisionStatus = CreateCompletionSource<string>();

        client.DeviceInfoReceived += device => deviceReceived.TrySetResult(device);
        client.FileTransferStatusReceived += (_success, message) =>
        {
            if (message.Contains("等待确认", StringComparison.Ordinal))
            {
                waitingStatus.TrySetResult(message);
            }

            if (message.Contains(confirmPreview ? "已确认" : "已取消", StringComparison.Ordinal))
            {
                decisionStatus.TrySetResult(message);
            }
        };
        client.ConfirmRemoteClipboardFileTransfer = (items, note) =>
        {
            Assert.Equal("preview-note", note);
            previewReceived.TrySetResult(items);
            return confirmPreview;
        };

        await client.ConnectAsync(IPAddress.Loopback.ToString(), port, LoopbackPassword, ViewerVideoMode.StableJpeg);
        RemoteDeviceDescriptor device = await deviceReceived.Task.WaitAsync(timeout.Token);
        Assert.True(device.Capabilities.HasFlag(RemoteDeviceCapabilities.FileSend));

        Assert.True(await client.RequestRemoteClipboardFilesAsync());

        IReadOnlyList<FileTransferConfirmationItem> previewItems =
            await previewReceived.Task.WaitAsync(timeout.Token);
        FileTransferConfirmationItem item = Assert.Single(previewItems);
        Assert.Equal("remote-preview.txt", item.TransferName);
        Assert.Contains("RemoteDeskReceived", item.DestinationPath, StringComparison.Ordinal);
        Assert.DoesNotContain("..", item.DestinationPath, StringComparison.Ordinal);

        Assert.Contains("等待确认", await waitingStatus.Task.WaitAsync(timeout.Token), StringComparison.Ordinal);
        Assert.Contains(
            confirmPreview ? "已确认" : "已取消",
            await decisionStatus.Task.WaitAsync(timeout.Token),
            StringComparison.Ordinal);
        Assert.Equal((RemoteControlKind)expectedDecision, await decisionReceived.Task.WaitAsync(timeout.Token));

        await client.DisconnectAsync();
        await serverTask.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task ViewerDragOutAutoConfirmsPreviewAndReturnsPathsWhenClipboardIsBusy()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        byte[] expectedBytes = [9, 8, 7, 6];
        string receiveDirectory = Path.Combine(
            Path.GetTempPath(),
            $"RemoteDesk-drag-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(receiveDirectory);
        var decisionReceived = CreateCompletionSource<RemoteControlKind>();
        Task serverTask = RunPreviewReturningSyntheticHostAsync(
            listener,
            LoopbackPassword,
            decisionReceived,
            timeout.Token,
            returnedFileName: "drag-out.txt",
            returnedFileBytes: expectedBytes);

        int confirmationCallbacks = 0;
        using var client = new RemoteViewerClient(
            () => receiveDirectory,
            _ => throw new InvalidOperationException("clipboard busy"));
        client.ConfirmRemoteClipboardFileTransfer = (_items, _note) =>
        {
            Interlocked.Increment(ref confirmationCallbacks);
            return false;
        };
        var batchCompleted = CreateCompletionSource<ReturnedClipboardFileBatchResult>();
        client.RemoteClipboardFileBatchCompleted += result => batchCompleted.TrySetResult(result);

        try
        {
            await client.ConnectAsync(
                IPAddress.Loopback.ToString(),
                port,
                LoopbackPassword,
                ViewerVideoMode.StableJpeg);

            ReturnedClipboardFileBatchResult result =
                await client.RequestRemoteClipboardFilesForDragOutAsync(timeout.Token);

            Assert.Equal(
                RemoteControlKind.FileTransferConfirmClipboardFiles,
                await decisionReceived.Task.WaitAsync(timeout.Token));
            Assert.True(result.Success, $"{result.Outcome}: {result.Message}");
            Assert.Equal(ReturnedClipboardFileBatchOutcome.Succeeded, result.Outcome);
            Assert.False(result.Cancelled);
            Assert.False(result.Busy);
            Assert.False(result.ClipboardUpdated);
            Assert.Equal(0, confirmationCallbacks);
            string savedPath = Assert.Single(result.LocalPaths);
            Assert.Equal(Path.Combine(receiveDirectory, "drag-out.txt"), savedPath);
            Assert.True(File.Exists(savedPath));
            Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(savedPath, timeout.Token));
            Assert.Contains("仍可直接拖出", result.Message, StringComparison.Ordinal);
            Assert.False(client.IsRemoteClipboardFileRequestPending);

            ReturnedClipboardFileBatchResult eventResult =
                await batchCompleted.Task.WaitAsync(timeout.Token);
            Assert.True(eventResult.Success);
            Assert.Equal([savedPath], eventResult.LocalPaths);
            IList<string> readOnlyPaths = Assert.IsAssignableFrom<IList<string>>(result.LocalPaths);
            Assert.True(readOnlyPaths.IsReadOnly);

            await client.DisconnectAsync();
            await serverTask.WaitAsync(timeout.Token);
        }
        finally
        {
            try
            {
                Directory.Delete(receiveDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public static TheoryData<string, string, string, long, long?, int, string> PreviewMismatchCases => new()
    {
        { "injected.txt", "approved.txt", "文件", 3, null, (int)RemoteControlKind.FileTransferConfirmClipboardFiles, "确认清单不一致" },
        { "same-name.txt", "same-name.txt", "文件", 1, null, (int)RemoteControlKind.FileTransferConfirmClipboardFiles, "确认清单不一致" },
        { "oversized.txt", "oversized.txt", "文件", RemoteMessageCodec.MaxFileTransferBytes + 1, null, (int)RemoteControlKind.FileTransferRejectClipboardFiles, "超出接收上限" },
        { "folder.txt", "folder.txt", "文件夹", 0, null, (int)RemoteControlKind.FileTransferRejectClipboardFiles, "zip 传输名称" },
        { "folder.zip", "folder.zip", "文件夹", 1, 16L * 1024 * 1024 + 2, (int)RemoteControlKind.FileTransferConfirmClipboardFiles, "确认清单不一致" }
    };

    [Theory]
    [MemberData(nameof(PreviewMismatchCases))]
    public async Task ViewerDragOutRejectsTransferThatDoesNotMatchConfirmedPreview(
        string returnedFileName,
        string previewTransferName,
        string previewKind,
        long previewSizeBytes,
        long? returnedFileLength,
        int expectedDecision,
        string expectedMessage)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string receiveDirectory = Path.Combine(
            Path.GetTempPath(),
            $"RemoteDesk-drag-out-mismatch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(receiveDirectory);
        var decisionReceived = CreateCompletionSource<RemoteControlKind>();
        Task serverTask = RunPreviewReturningSyntheticHostAsync(
            listener,
            LoopbackPassword,
            decisionReceived,
            timeout.Token,
            returnedFileName: returnedFileName,
            returnedFileBytes: [1, 2, 3],
            previewTransferName: previewTransferName,
            previewSizeBytes: previewSizeBytes,
            previewKind: previewKind,
            returnedFileLength: returnedFileLength);

        using var client = new RemoteViewerClient(
            () => receiveDirectory,
            _ => Task.CompletedTask);
        var deviceInfoReceived = CreateCompletionSource<RemoteDeviceDescriptor>();
        client.DeviceInfoReceived += device => deviceInfoReceived.TrySetResult(device);
        try
        {
            await client.ConnectAsync(
                IPAddress.Loopback.ToString(),
                port,
                LoopbackPassword,
                ViewerVideoMode.StableJpeg);
            await deviceInfoReceived.Task.WaitAsync(timeout.Token);

            ReturnedClipboardFileBatchResult result =
                await client.RequestRemoteClipboardFilesForDragOutAsync(timeout.Token);

            Assert.Equal(
                (RemoteControlKind)expectedDecision,
                await decisionReceived.Task.WaitAsync(timeout.Token));
            Assert.Equal(ReturnedClipboardFileBatchOutcome.Failed, result.Outcome);
            Assert.Empty(result.LocalPaths);
            Assert.Contains(expectedMessage, result.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(receiveDirectory, returnedFileName)));
            Assert.False(client.IsRemoteClipboardFileRequestPending);

            await client.DisconnectAsync();
            await serverTask.WaitAsync(timeout.Token);
        }
        finally
        {
            try
            {
                Directory.Delete(receiveDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task ViewerAcceptsDeferredSizeDirectoryArchiveAboveCompatibilityFloor()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        byte[] archiveBytes = new byte[16 * 1024 * 1024 + 1];
        archiveBytes[0] = 0x50;
        archiveBytes[1] = 0x4b;
        archiveBytes[^1] = 0x7f;
        string receiveDirectory = Path.Combine(
            Path.GetTempPath(),
            $"RemoteDesk-deferred-folder-size-{Guid.NewGuid():N}");
        Directory.CreateDirectory(receiveDirectory);
        var decisionReceived = CreateCompletionSource<RemoteControlKind>();
        Task serverTask = RunPreviewReturningSyntheticHostAsync(
            listener,
            LoopbackPassword,
            decisionReceived,
            timeout.Token,
            returnedFileName: "deferred-folder.zip",
            returnedFileBytes: archiveBytes,
            previewTransferName: "deferred-folder.zip",
            previewSizeBytes: 0,
            previewKind: "文件夹");

        using var client = new RemoteViewerClient(
            () => receiveDirectory,
            _ => Task.CompletedTask);
        try
        {
            await client.ConnectAsync(
                IPAddress.Loopback.ToString(),
                port,
                LoopbackPassword,
                ViewerVideoMode.StableJpeg);

            ReturnedClipboardFileBatchResult result =
                await client.RequestRemoteClipboardFilesForDragOutAsync(timeout.Token);

            Assert.Equal(
                RemoteControlKind.FileTransferConfirmClipboardFiles,
                await decisionReceived.Task.WaitAsync(timeout.Token));
            Assert.True(result.Success, $"{result.Outcome}: {result.Message}");
            string savedPath = Assert.Single(result.LocalPaths);
            Assert.Equal(archiveBytes.Length, new FileInfo(savedPath).Length);
            Assert.Equal(archiveBytes, await File.ReadAllBytesAsync(savedPath, timeout.Token));

            await client.DisconnectAsync();
            await serverTask.WaitAsync(timeout.Token);
        }
        finally
        {
            try
            {
                Directory.Delete(receiveDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task ViewerDragOutCancellationRejectsRequestAndCompletesWithoutStalePaths()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var dragCancellation = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var requestReceived = CreateCompletionSource<RemoteControlKind>();
        var rejectionReceived = CreateCompletionSource<RemoteControlKind>();
        var allowPreview = CreateCompletionSource<bool>();
        Task serverTask = RunPreviewReturningSyntheticHostAsync(
            listener,
            LoopbackPassword,
            rejectionReceived,
            timeout.Token,
            previewGate: allowPreview.Task,
            requestReceived: requestReceived);

        using var client = new RemoteViewerClient();
        var batchCompleted = CreateCompletionSource<ReturnedClipboardFileBatchResult>();
        client.RemoteClipboardFileBatchCompleted += result => batchCompleted.TrySetResult(result);
        await client.ConnectAsync(
            IPAddress.Loopback.ToString(),
            port,
            LoopbackPassword,
            ViewerVideoMode.StableJpeg);

        Task<ReturnedClipboardFileBatchResult> dragTask =
            client.RequestRemoteClipboardFilesForDragOutAsync(
                dragCancellation.Token,
                notifyRequest: false);
        Assert.Equal(
            RemoteControlKind.FileTransferRequestClipboardFiles,
            await requestReceived.Task.WaitAsync(timeout.Token));
        dragCancellation.Cancel();

        ReturnedClipboardFileBatchResult result = await dragTask.WaitAsync(timeout.Token);
        Assert.True(result.Cancelled);
        Assert.False(result.Success);
        Assert.Empty(result.LocalPaths);
        Assert.False(result.ClipboardUpdated);
        Assert.True(client.IsRemoteClipboardFileRequestPending);
        Assert.Equal(0, client.SendTextInput("must-not-cross-cancelled-return").SentCodePoints);

        ReturnedClipboardFileBatchResult overlappingResult =
            await client.RequestRemoteClipboardFilesForDragOutAsync(
                timeout.Token,
                notifyRequest: false);
        Assert.True(overlappingResult.Busy);
        Assert.Empty(overlappingResult.LocalPaths);

        allowPreview.TrySetResult(true);
        Assert.Equal(
            RemoteControlKind.FileTransferRejectClipboardFiles,
            await rejectionReceived.Task.WaitAsync(timeout.Token));

        ReturnedClipboardFileBatchResult eventResult =
            await batchCompleted.Task.WaitAsync(timeout.Token);
        Assert.True(eventResult.Cancelled);
        Assert.Empty(eventResult.LocalPaths);
        Assert.False(client.IsRemoteClipboardFileRequestPending);

        await client.DisconnectAsync();
        await serverTask.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task ViewerDragOutCancellationDuringConfirmationOrdersConfirmBeforeRejectAndAllowsRetry()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var dragCancellation = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var wireOrder = CreateCompletionSource<IReadOnlyList<RemoteControlKind>>();
        var responsesObserved = CreateCompletionSource<bool>();
        var allowCancellationAcknowledgement = CreateCompletionSource<bool>();
        Task serverTask = RunConfirmCancellationThenRetrySyntheticHostAsync(
            listener,
            LoopbackPassword,
            wireOrder,
            responsesObserved,
            allowCancellationAcknowledgement.Task,
            timeout.Token);

        using var client = new RemoteViewerClient();
        var deviceReceived = CreateCompletionSource<bool>();
        var firstCompleted = CreateCompletionSource<ReturnedClipboardFileBatchResult>();
        var secondCompleted = CreateCompletionSource<ReturnedClipboardFileBatchResult>();
        int completionCount = 0;
        client.DeviceInfoReceived += _ => deviceReceived.TrySetResult(true);
        client.FileTransferStatusReceived += (_, message) =>
        {
            if (message.StartsWith("拖出手势已确认", StringComparison.Ordinal))
            {
                dragCancellation.Cancel();
            }
        };
        client.RemoteClipboardFileBatchCompleted += result =>
        {
            if (Interlocked.Increment(ref completionCount) == 1)
            {
                firstCompleted.TrySetResult(result);
            }
            else
            {
                secondCompleted.TrySetResult(result);
            }
        };

        await client.ConnectAsync(
            IPAddress.Loopback.ToString(),
            port,
            LoopbackPassword,
            ViewerVideoMode.StableJpeg);
        await deviceReceived.Task.WaitAsync(timeout.Token);

        ReturnedClipboardFileBatchResult dragResult =
            await client.RequestRemoteClipboardFilesForDragOutAsync(
                dragCancellation.Token,
                notifyRequest: false).WaitAsync(timeout.Token);
        Assert.True(dragResult.Cancelled);
        await responsesObserved.Task.WaitAsync(timeout.Token);
        Assert.True(client.IsRemoteClipboardFileRequestPending);
        Assert.False(firstCompleted.Task.IsCompleted);

        allowCancellationAcknowledgement.TrySetResult(true);
        ReturnedClipboardFileBatchResult first =
            await firstCompleted.Task.WaitAsync(timeout.Token);
        Assert.Equal(ReturnedClipboardFileBatchOutcome.Cancelled, first.Outcome);
        Assert.False(client.IsRemoteClipboardFileRequestPending);

        Assert.True(await client.RequestRemoteClipboardFilesAsync(notifyRequest: false));
        ReturnedClipboardFileBatchResult second =
            await secondCompleted.Task.WaitAsync(timeout.Token);
        Assert.Equal(ReturnedClipboardFileBatchOutcome.Failed, second.Outcome);
        Assert.Equal(
            [
                RemoteControlKind.FileTransferConfirmClipboardFiles,
                RemoteControlKind.FileTransferRejectClipboardFiles,
                RemoteControlKind.FileTransferRequestClipboardFiles
            ],
            await wireOrder.Task.WaitAsync(timeout.Token));

        await client.DisconnectAsync();
        await serverTask.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task ViewerReturnRequestIdleTimeoutClearsPendingAndDisconnectsProtocolSession()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requestReceived = CreateCompletionSource<RemoteControlKind>();
        var peerDisconnected = CreateCompletionSource<bool>();
        Task serverTask = RunIdleReturnRequestSyntheticHostAsync(
            listener,
            LoopbackPassword,
            requestReceived,
            peerDisconnected,
            activityCount: 0,
            activityDelay: TimeSpan.Zero,
            sendTerminalStatus: false,
            timeout.Token);

        using var client = new RemoteViewerClient(
            incomingFileReceiveDirectoryProvider: null,
            setFileDropListAsync: null,
            returnedClipboardFileRequestIdleTimeout: TimeSpan.FromMilliseconds(300));
        await client.ConnectAsync(
            IPAddress.Loopback.ToString(),
            port,
            LoopbackPassword,
            ViewerVideoMode.StableJpeg);

        Task<ReturnedClipboardFileBatchResult> requestTask =
            client.RequestRemoteClipboardFilesForDragOutAsync(timeout.Token, notifyRequest: false);
        Assert.Equal(
            RemoteControlKind.FileTransferRequestClipboardFiles,
            await requestReceived.Task.WaitAsync(timeout.Token));

        ReturnedClipboardFileBatchResult result = await requestTask.WaitAsync(timeout.Token);
        Assert.Equal(ReturnedClipboardFileBatchOutcome.TimedOut, result.Outcome);
        Assert.False(result.Success);
        Assert.Empty(result.LocalPaths);
        Assert.False(client.IsRemoteClipboardFileRequestPending);
        await peerDisconnected.Task.WaitAsync(timeout.Token);
        await serverTask.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task ViewerReturnRequestActivityRefreshesIdleDeadline()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requestReceived = CreateCompletionSource<RemoteControlKind>();
        var peerDisconnected = CreateCompletionSource<bool>();
        Task serverTask = RunIdleReturnRequestSyntheticHostAsync(
            listener,
            LoopbackPassword,
            requestReceived,
            peerDisconnected,
            activityCount: 5,
            activityDelay: TimeSpan.FromMilliseconds(120),
            sendTerminalStatus: true,
            timeout.Token);

        using var client = new RemoteViewerClient(
            incomingFileReceiveDirectoryProvider: null,
            setFileDropListAsync: null,
            returnedClipboardFileRequestIdleTimeout: TimeSpan.FromMilliseconds(500));
        await client.ConnectAsync(
            IPAddress.Loopback.ToString(),
            port,
            LoopbackPassword,
            ViewerVideoMode.StableJpeg);

        ReturnedClipboardFileBatchResult result =
            await client.RequestRemoteClipboardFilesForDragOutAsync(
                timeout.Token,
                notifyRequest: false);

        Assert.Equal(ReturnedClipboardFileBatchOutcome.Failed, result.Outcome);
        Assert.NotEqual(ReturnedClipboardFileBatchOutcome.TimedOut, result.Outcome);
        Assert.False(client.IsRemoteClipboardFileRequestPending);
        Assert.True(client.IsConnected);

        await client.DisconnectAsync();
        await peerDisconnected.Task.WaitAsync(timeout.Token);
        await serverTask.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task ViewerKeepsReturnedClipboardFilesWhenLocalClipboardWriteFailsAndRetries()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        byte[] expectedBytes = [5, 4, 3, 2, 1];
        string receiveDirectory = Path.Combine(
            Path.GetTempPath(),
            $"RemoteDesk-return-clipboard-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(receiveDirectory);

        var requestReceived = CreateCompletionSource<RemoteControlKind>();
        var hostReceivedStatus = CreateCompletionSource<string>();
        Task serverTask = RunFileReturningSyntheticHostAsync(
            listener,
            LoopbackPassword,
            "remote-retry.txt",
            expectedBytes,
            requestReceived,
            hostReceivedStatus,
            timeout.Token,
            sendReturnCompleteStatus: true);

        int clipboardWrites = 0;
        string[]? retriedFiles = null;
        using var client = new RemoteViewerClient(
            () => receiveDirectory,
            paths =>
            {
                clipboardWrites++;
                string[] pathArray = paths.ToArray();
                if (clipboardWrites == 1)
                {
                    throw new InvalidOperationException("clipboard busy");
                }

                retriedFiles = pathArray;
                return Task.CompletedTask;
            });
        var clipboardFailureStatus = CreateCompletionSource<string>();
        var clipboardRetryStatus = CreateCompletionSource<string>();
        client.FileTransferStatusReceived += (success, message) =>
        {
            if (!success && message.Contains("写入本机文件剪贴板失败", StringComparison.Ordinal))
            {
                clipboardFailureStatus.TrySetResult(message);
            }

            if (success && message.Contains("放入本机剪贴板", StringComparison.Ordinal))
            {
                clipboardRetryStatus.TrySetResult(message);
            }
        };

        try
        {
            await client.ConnectAsync(IPAddress.Loopback.ToString(), port, LoopbackPassword, ViewerVideoMode.StableJpeg);
            Assert.True(await client.RequestRemoteClipboardFilesAsync());

            Assert.Equal(
                RemoteControlKind.FileTransferRequestClipboardFiles,
                await requestReceived.Task.WaitAsync(timeout.Token));

            string savedPath = Path.Combine(receiveDirectory, "remote-retry.txt");
            Assert.Contains("文件已保存到本机", await hostReceivedStatus.Task.WaitAsync(timeout.Token), StringComparison.Ordinal);
            string failureStatus = await clipboardFailureStatus.Task.WaitAsync(timeout.Token);
            Assert.Contains("再次点击拉取文件可重试", failureStatus, StringComparison.Ordinal);
            Assert.Equal(1, clipboardWrites);
            Assert.True(File.Exists(savedPath));

            Assert.True(await client.RequestRemoteClipboardFilesAsync());
            string retryStatus = await clipboardRetryStatus.Task.WaitAsync(timeout.Token);

            Assert.Equal(2, clipboardWrites);
            Assert.NotNull(retriedFiles);
            Assert.Equal([savedPath], retriedFiles);
            Assert.Contains("放入本机剪贴板", retryStatus, StringComparison.Ordinal);

            await client.DisconnectAsync();
            await serverTask.WaitAsync(timeout.Token);
        }
        finally
        {
            try
            {
                Directory.Delete(receiveDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task ViewerAdvertisesChecksumAndVerifiesReturnedRemoteFile()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        byte[] expectedBytes = Enumerable.Range(0, RemoteMessageCodec.FileTransferChunkBytes + 41)
            .Select(index => (byte)(index % 233))
            .ToArray();
        string receiveDirectory = Path.Combine(
            Path.GetTempPath(),
            $"RemoteDesk-return-checksum-{Guid.NewGuid():N}");
        Directory.CreateDirectory(receiveDirectory);

        var requestReceived = CreateCompletionSource<RemoteControlKind>();
        var viewerCapabilitiesReceived = CreateCompletionSource<RemoteDeviceCapabilities>();
        var hostReceivedStatus = CreateCompletionSource<string>();
        Task serverTask = RunChecksumFileReturningSyntheticHostAsync(
            listener,
            LoopbackPassword,
            "remote-checked.bin",
            expectedBytes,
            requestReceived,
            viewerCapabilitiesReceived,
            hostReceivedStatus,
            timeout.Token);

        using var client = new RemoteViewerClient(() => receiveDirectory);
        var verifiedSaveStatus = CreateCompletionSource<string>();
        var deviceInfoReceived = CreateCompletionSource<bool>();
        client.DeviceInfoReceived += _ => deviceInfoReceived.TrySetResult(true);
        client.FileTransferStatusReceived += (success, message) =>
        {
            if (success && message.Contains("SHA-256 已校验", StringComparison.Ordinal))
            {
                verifiedSaveStatus.TrySetResult(message);
            }
        };

        try
        {
            await client.ConnectAsync(IPAddress.Loopback.ToString(), port, LoopbackPassword, ViewerVideoMode.StableJpeg);
            await deviceInfoReceived.Task.WaitAsync(timeout.Token);
            Assert.True(await client.RequestRemoteClipboardFilesAsync());

            RemoteDeviceCapabilities viewerCapabilities = await viewerCapabilitiesReceived.Task.WaitAsync(timeout.Token);
            Assert.True(viewerCapabilities.HasFlag(RemoteDeviceCapabilities.FileChecksum));
            Assert.True(viewerCapabilities.HasFlag(RemoteDeviceCapabilities.FileTransferCancel));
            Assert.Equal(
                RemoteControlKind.FileTransferRequestClipboardFiles,
                await requestReceived.Task.WaitAsync(timeout.Token));

            string savedPath = Path.Combine(receiveDirectory, "remote-checked.bin");
            string saveStatus = await verifiedSaveStatus.Task.WaitAsync(timeout.Token);
            Assert.Contains(savedPath, saveStatus, StringComparison.Ordinal);
            Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(savedPath, timeout.Token));
            Assert.Contains("SHA-256 已校验", await hostReceivedStatus.Task.WaitAsync(timeout.Token), StringComparison.Ordinal);

            await client.DisconnectAsync();
            await serverTask.WaitAsync(timeout.Token);
        }
        finally
        {
            try
            {
                Directory.Delete(receiveDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task ViewerRejectsReturnedRemoteFileWhenNegotiatedChecksumIsMissing()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        byte[] expectedBytes = Enumerable.Range(0, 4096)
            .Select(index => (byte)(index % 227))
            .ToArray();
        string receiveDirectory = Path.Combine(
            Path.GetTempPath(),
            $"RemoteDesk-return-missing-checksum-{Guid.NewGuid():N}");
        Directory.CreateDirectory(receiveDirectory);

        var requestReceived = CreateCompletionSource<RemoteControlKind>();
        var viewerCapabilitiesReceived = CreateCompletionSource<RemoteDeviceCapabilities>();
        var hostReceivedStatus = CreateCompletionSource<string>();
        Task serverTask = RunChecksumFileReturningSyntheticHostAsync(
            listener,
            LoopbackPassword,
            "remote-missing-checksum.bin",
            expectedBytes,
            requestReceived,
            viewerCapabilitiesReceived,
            hostReceivedStatus,
            timeout.Token,
            sendChecksum: false,
            expectSuccessfulSave: false);

        using var client = new RemoteViewerClient(() => receiveDirectory);
        var localFailureStatus = CreateCompletionSource<string>();
        var deviceInfoReceived = CreateCompletionSource<bool>();
        client.DeviceInfoReceived += _ => deviceInfoReceived.TrySetResult(true);
        client.FileTransferStatusReceived += (success, message) =>
        {
            if (!success && message.Contains("未发送文件 SHA-256 校验值", StringComparison.Ordinal))
            {
                localFailureStatus.TrySetResult(message);
            }
        };

        try
        {
            await client.ConnectAsync(IPAddress.Loopback.ToString(), port, LoopbackPassword, ViewerVideoMode.StableJpeg);
            await deviceInfoReceived.Task.WaitAsync(timeout.Token);
            Assert.True(await client.RequestRemoteClipboardFilesAsync());

            RemoteDeviceCapabilities viewerCapabilities = await viewerCapabilitiesReceived.Task.WaitAsync(timeout.Token);
            Assert.True(viewerCapabilities.HasFlag(RemoteDeviceCapabilities.FileChecksum));
            Assert.True(viewerCapabilities.HasFlag(RemoteDeviceCapabilities.FileTransferCancel));
            Assert.Equal(
                RemoteControlKind.FileTransferRequestClipboardFiles,
                await requestReceived.Task.WaitAsync(timeout.Token));

            string failure = await localFailureStatus.Task.WaitAsync(timeout.Token);
            Assert.Contains("未发送文件 SHA-256 校验值", failure, StringComparison.Ordinal);
            Assert.Contains("未发送文件 SHA-256 校验值", await hostReceivedStatus.Task.WaitAsync(timeout.Token), StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(receiveDirectory, "remote-missing-checksum.bin")));

            await client.DisconnectAsync();
            await serverTask.WaitAsync(timeout.Token);
        }
        finally
        {
            try
            {
                Directory.Delete(receiveDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task ViewerHandlesRemoteFileTransferCancelAndDeletesTemporaryFile()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        byte[] fileBytes = Enumerable.Range(0, 4096)
            .Select(index => (byte)(index % 229))
            .ToArray();
        string receiveDirectory = Path.Combine(
            Path.GetTempPath(),
            $"RemoteDesk-return-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(receiveDirectory);

        var hostReceivedStatus = CreateCompletionSource<string>();
        Task serverTask = RunCancelFileReturningSyntheticHostAsync(
            listener,
            LoopbackPassword,
            "remote-cancel.bin",
            fileBytes,
            hostReceivedStatus,
            timeout.Token);

        using var client = new RemoteViewerClient(() => receiveDirectory);
        var cancelStatus = CreateCompletionSource<string>();
        client.FileTransferStatusReceived += (success, message) =>
        {
            if (success && message.Contains("文件传输已取消", StringComparison.Ordinal))
            {
                cancelStatus.TrySetResult(message);
            }
        };

        try
        {
            await client.ConnectAsync(IPAddress.Loopback.ToString(), port, LoopbackPassword, ViewerVideoMode.StableJpeg);

            string status = await cancelStatus.Task.WaitAsync(timeout.Token);
            Assert.Contains("synthetic host cancelled", status, StringComparison.Ordinal);
            Assert.Contains("synthetic host cancelled", await hostReceivedStatus.Task.WaitAsync(timeout.Token), StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(receiveDirectory, "remote-cancel.bin")));
            Assert.DoesNotContain(
                Directory.GetFiles(receiveDirectory),
                path => FileTransferReceiver.IsOwnedTemporaryFileName(Path.GetFileName(path)));

            await client.DisconnectAsync();
            await serverTask.WaitAsync(timeout.Token);
        }
        finally
        {
            try
            {
                Directory.Delete(receiveDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task ViewerKeepsActiveIncomingTransferAfterMismatchedCancel()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string receiveDirectory = Path.Combine(
            Path.GetTempPath(),
            $"RemoteDesk-stale-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(receiveDirectory);
        byte[] firstChunk = [1, 2, 3];
        byte[] secondChunk = [4, 5, 6];
        var releaseHost = CreateCompletionSource<bool>();
        Task serverTask = RunMismatchedCancelDuringFileReturningSyntheticHostAsync(
            listener,
            LoopbackPassword,
            firstChunk,
            secondChunk,
            releaseHost.Task,
            timeout.Token);

        using var client = new RemoteViewerClient(() => receiveDirectory);
        var mismatchReported = CreateCompletionSource<string>();
        var completed = CreateCompletionSource<string>();
        client.FileTransferStatusReceived += (success, message) =>
        {
            if (!success && message.Contains("没有匹配的文件传输会话", StringComparison.Ordinal))
            {
                mismatchReported.TrySetResult(message);
            }

            if (success &&
                message.Contains("文件已保存", StringComparison.Ordinal) &&
                message.Contains("stale-cancel.bin", StringComparison.Ordinal))
            {
                completed.TrySetResult(message);
            }
        };

        try
        {
            await client.ConnectAsync(IPAddress.Loopback.ToString(), port, LoopbackPassword, ViewerVideoMode.StableJpeg);

            await mismatchReported.Task.WaitAsync(timeout.Token);
            await completed.Task.WaitAsync(timeout.Token);
            Assert.Equal(
                firstChunk.Concat(secondChunk).ToArray(),
                await File.ReadAllBytesAsync(Path.Combine(receiveDirectory, "stale-cancel.bin"), timeout.Token));
            Assert.DoesNotContain(
                Directory.GetFiles(receiveDirectory),
                path => FileTransferReceiver.IsOwnedTemporaryFileName(Path.GetFileName(path)));

            releaseHost.TrySetResult(true);
            await client.DisconnectAsync();
            await serverTask.WaitAsync(timeout.Token);
        }
        finally
        {
            releaseHost.TrySetResult(true);
            try
            {
                Directory.Delete(receiveDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task ViewerContinuesIncomingTransferAfterUnrelatedInvalidControlMessage()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        byte[] partialBytes = Enumerable.Range(0, 2048)
            .Select(index => (byte)(index % 223))
            .ToArray();
        string receiveDirectory = Path.Combine(
            Path.GetTempPath(),
            $"RemoteDesk-return-invalid-control-{Guid.NewGuid():N}");
        Directory.CreateDirectory(receiveDirectory);

        var continueTransfer = CreateCompletionSource<bool>();
        var releaseHost = CreateCompletionSource<bool>();
        Task serverTask = RunInvalidControlDuringFileReturningSyntheticHostAsync(
            listener,
            LoopbackPassword,
            "remote-invalid-control.bin",
            partialBytes,
            continueTransfer.Task,
            releaseHost.Task,
            timeout.Token);

        using var client = new RemoteViewerClient(() => receiveDirectory);
        var invalidControlLogged = CreateCompletionSource<string>();
        var transferCompleted = CreateCompletionSource<string>();
        client.Log += message =>
        {
            if (message.Contains("已忽略无效控制消息", StringComparison.Ordinal))
            {
                invalidControlLogged.TrySetResult(message);
            }
        };
        client.FileTransferStatusReceived += (success, message) =>
        {
            if (success &&
                message.Contains("文件已保存", StringComparison.Ordinal) &&
                message.Contains("remote-invalid-control.bin", StringComparison.Ordinal))
            {
                transferCompleted.TrySetResult(message);
            }
        };

        try
        {
            await client.ConnectAsync(IPAddress.Loopback.ToString(), port, LoopbackPassword, ViewerVideoMode.StableJpeg);

            await invalidControlLogged.Task.WaitAsync(timeout.Token);

            Assert.False(File.Exists(Path.Combine(receiveDirectory, "remote-invalid-control.bin")));
            Assert.Single(
                Directory.GetFiles(receiveDirectory),
                path => FileTransferReceiver.IsOwnedTemporaryFileName(Path.GetFileName(path)));

            continueTransfer.TrySetResult(true);
            await transferCompleted.Task.WaitAsync(timeout.Token);
            byte[] expectedBytes = partialBytes.Append((byte)0x5A).ToArray();
            Assert.Equal(
                expectedBytes,
                await File.ReadAllBytesAsync(
                    Path.Combine(receiveDirectory, "remote-invalid-control.bin"),
                    timeout.Token));
            Assert.DoesNotContain(
                Directory.GetFiles(receiveDirectory),
                path => FileTransferReceiver.IsOwnedTemporaryFileName(Path.GetFileName(path)));

            releaseHost.TrySetResult(true);
            await client.DisconnectAsync();
            await serverTask.WaitAsync(timeout.Token);
        }
        finally
        {
            continueTransfer.TrySetResult(true);
            releaseHost.TrySetResult(true);
            try
            {
                Directory.Delete(receiveDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task ViewerSendsFileDropPasteBatchAroundDraggedFiles()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        byte[] expectedBytes = Enumerable.Range(0, 4096)
            .Select(index => (byte)(index % 251))
            .ToArray();
        string tempFile = Path.Combine(Path.GetTempPath(), $"RemoteDesk-drop-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(tempFile, expectedBytes, timeout.Token);

        var receivedBatch = CreateCompletionSource<LoopbackDropPasteBatch>();
        Task serverTask = RunDropPasteReceivingSyntheticHostAsync(
            listener,
            LoopbackPassword,
            receivedBatch,
            timeout.Token);

        using var client = new RemoteViewerClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback.ToString(), port, LoopbackPassword, ViewerVideoMode.StableJpeg);
            RemoteFileDropPasteResult result = await client.SendFilesToRemoteDropPasteAsync([tempFile], maxFiles: 32);

            Assert.Equal(1, result.TransferResult.SentFiles);
            Assert.Equal(0, result.TransferResult.FailedFiles);
            Assert.True(result.RemotePasteRequested);

            LoopbackDropPasteBatch batch = await receivedBatch.Task.WaitAsync(timeout.Token);
            Assert.Equal(Path.GetFileName(tempFile), batch.File.FileName);
            Assert.Equal(expectedBytes, batch.File.Bytes);
            AssertSubsequence(
                [
                    RemoteControlKind.FileDropPasteBegin,
                    RemoteControlKind.FileTransferStart,
                    RemoteControlKind.FileTransferChunk,
                    RemoteControlKind.FileTransferComplete,
                    RemoteControlKind.FileDropPasteCommit
                ],
                batch.ControlKinds);

            await client.DisconnectAsync();
            await serverTask.WaitAsync(timeout.Token);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task ViewerSendsWindowsControlInputClipboardAndCaptureTargetMessages()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var receivedCommands = CreateCompletionSource<LoopbackViewerCommands>();
        Task serverTask = RunWindowsControlReceivingSyntheticHostAsync(
            listener,
            LoopbackPassword,
            receivedCommands,
            timeout.Token);

        using var client = new RemoteViewerClient();
        await client.ConnectAsync(IPAddress.Loopback.ToString(), port, LoopbackPassword, ViewerVideoMode.StableJpeg);

        await client.SendInputsAsync(
        [
            RemoteInputCommand.MouseMove(30, 40),
            RemoteInputCommand.MouseDown(RemoteMouseButton.Left, 30, 40)
        ]);
        RemoteTextInputResult textInput = client.SendTextInput("Win");
        await client.FlushInputAsync(timeout.Token);
        await client.SelectCaptureTargetAsync("screen-2");
        await client.SendClipboardTextToRemoteAsync("local clipboard");
        await client.ReadRemoteClipboardAsync();

        LoopbackViewerCommands commands = await receivedCommands.Task.WaitAsync(timeout.Token);
        Assert.Equal(RemoteVideoCodecs.Jpeg, commands.ViewerCodecs);
        Assert.Equal(3, textInput.SentCodePoints);
        Assert.False(textInput.Truncated);
        Assert.Contains(commands.Inputs, input => input.Kind == RemoteInputKind.MouseMove && input.X == 30 && input.Y == 40);
        Assert.Contains(commands.Inputs, input => input.Kind == RemoteInputKind.MouseDown && input.Button == RemoteMouseButton.Left);
        Assert.Contains(commands.Inputs, input => input.Kind == RemoteInputKind.TextInput && input.Data == 'W');
        Assert.Contains(commands.Inputs, input => input.Kind == RemoteInputKind.TextInput && input.Data == 'i');
        Assert.Contains(commands.Inputs, input => input.Kind == RemoteInputKind.TextInput && input.Data == 'n');
        Assert.False(commands.ControlArrivedBeforeInputFlush);
        Assert.Contains(commands.Controls, control => control.Kind == RemoteControlKind.SelectCaptureTarget && control.TargetId == "screen-2");
        Assert.Contains(commands.Controls, control => control.Kind == RemoteControlKind.ClipboardSetText && control.Text == "local clipboard");
        Assert.Contains(commands.Controls, control => control.Kind == RemoteControlKind.ClipboardGetText);

        await client.DisconnectAsync();
        await serverTask.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task ViewerSendsLargeTextInputBurstAcrossInputBatches()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        string text = new('w', 80);
        var receivedText = CreateCompletionSource<string>();
        Task serverTask = RunTextInputBurstReceivingSyntheticHostAsync(
            listener,
            LoopbackPassword,
            expectedCodePoints: text.Length,
            receivedText,
            timeout.Token);

        using var client = new RemoteViewerClient();
        await client.ConnectAsync(IPAddress.Loopback.ToString(), port, LoopbackPassword, ViewerVideoMode.StableJpeg);

        RemoteTextInputResult result = client.SendTextInput(text);

        Assert.Equal(text.Length, result.SentCodePoints);
        Assert.False(result.Truncated);
        Assert.Equal(text, await receivedText.Task.WaitAsync(timeout.Token));

        await client.DisconnectAsync();
        await serverTask.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task StaleInputLoopCleanupCannotClearReconnectedSessionQueueOrFlush()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var firstConnectionReady = CreateCompletionSource<bool>();
        var receivedText = CreateCompletionSource<string>();
        string text = new('g', 256);
        Task serverTask = RunReconnectInputReceivingSyntheticHostAsync(
            listener,
            LoopbackPassword,
            firstConnectionReady,
            text.Length,
            receivedText,
            timeout.Token);

        using var client = new RemoteViewerClient();
        await client.ConnectAsync(
            IPAddress.Loopback.ToString(),
            port,
            LoopbackPassword,
            ViewerVideoMode.StableJpeg);
        await firstConnectionReady.Task.WaitAsync(timeout.Token);
        long oldGeneration = client.InputConnectionGeneration;
        await client.DisconnectAsync();

        await client.ConnectAsync(
            IPAddress.Loopback.ToString(),
            port,
            LoopbackPassword,
            ViewerVideoMode.StableJpeg);
        Assert.NotEqual(oldGeneration, client.InputConnectionGeneration);

        RemoteTextInputResult result = client.SendTextInput(text);
        Task flushTask = client.FlushInputAsync(timeout.Token);
        client.ClearInputStateForConnectionGeneration(oldGeneration);

        await flushTask;
        Assert.Equal(text.Length, result.SentCodePoints);
        Assert.Equal(text, await receivedText.Task.WaitAsync(timeout.Token));

        await client.DisconnectAsync();
        await serverTask.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task OwnedInputAdmissionRejectsPriorConnectionGeneration()
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(
            IPAddress.Loopback,
            0);
        listener.Start();
        int port =
            ((IPEndPoint)listener.LocalEndpoint).Port;
        var firstConnectionReady =
            CreateCompletionSource<bool>();
        var receivedText =
            CreateCompletionSource<string>();
        Task serverTask =
            RunReconnectInputReceivingSyntheticHostAsync(
                listener,
                LoopbackPassword,
                firstConnectionReady,
                expectedCodePoints: 1,
                receivedText,
                timeout.Token);

        using var client = new RemoteViewerClient();
        await client.ConnectAsync(
            IPAddress.Loopback.ToString(),
            port,
            LoopbackPassword,
            ViewerVideoMode.StableJpeg);
        await firstConnectionReady.Task.WaitAsync(
            timeout.Token);
        long oldGeneration =
            client.InputConnectionGeneration;
        await client.DisconnectAsync();

        await client.ConnectAsync(
            IPAddress.Loopback.ToString(),
            port,
            LoopbackPassword,
            ViewerVideoMode.StableJpeg);
        long currentGeneration =
            client.InputConnectionGeneration;
        Assert.NotEqual(
            oldGeneration,
            currentGeneration);
        Assert.False(client.TryQueueOwnedInput(
            RemoteInputCommand.MouseUp(
                RemoteMouseButton.Left,
                1,
                2),
            oldGeneration));
        Assert.True(client.TryQueueOwnedInput(
            RemoteInputCommand.TextInput('n'),
            currentGeneration));

        await client.FlushInputAsync(timeout.Token);
        Assert.Equal(
            "n",
            await receivedText.Task.WaitAsync(
                timeout.Token));
        await client.DisconnectAsync();
        await serverTask.WaitAsync(timeout.Token);
    }

    private static async Task RunDuplicateRequestGuardSyntheticHostAsync(
        TcpListener listener,
        string password,
        TaskCompletionSource<int> firstRequestCount,
        TaskCompletionSource<int> secondRequestCount,
        CancellationToken cancellationToken)
    {
        using TcpClient hostClient = await listener.AcceptTcpClientAsync(cancellationToken);
        NetworkUtils.ConfigureLowLatencyTcpClient(hostClient, 32 * 1024, 256 * 1024);
        await using NetworkStream stream = hostClient.GetStream();
        using var writeLock = new SemaphoreSlim(1, 1);

        ServerAuthenticationResult authentication = await Protocol.AuthenticateServerDetailedAsync(
            stream,
            password,
            cancellationToken);
        Assert.True(authentication.IsAuthenticated);

        using SecureSession session = authentication.Session!;
        ProtocolMessage viewerInfo = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
        Assert.Equal(MessageType.Control, viewerInfo.Type);
        Assert.Equal(
            RemoteControlKind.ViewerInfo,
            RemoteMessageCodec.DecodeControl(viewerInfo.PayloadMemory).Kind);

        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeDeviceInfo(new RemoteDeviceDescriptor(
                "request-guard-loopback",
                RemoteDevicePlatforms.Windows,
                RemoteDeviceCapabilities.FileSend)),
            session,
            writeLock,
            cancellationToken);

        TaskCompletionSource<int>[] phaseResults = [firstRequestCount, secondRequestCount];
        foreach (TaskCompletionSource<int> phaseResult in phaseResults)
        {
            int requests = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                ProtocolMessage message = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
                if (message.Type != MessageType.Control)
                {
                    continue;
                }

                RemoteControlMessage control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
                if (control.Kind == RemoteControlKind.FileTransferRequestClipboardFiles)
                {
                    requests++;
                }
                else if (control.Kind == RemoteControlKind.ClipboardGetText)
                {
                    break;
                }
            }

            phaseResult.TrySetResult(requests);
            await Protocol.WriteMessageAsync(
                stream,
                MessageType.Control,
                RemoteMessageCodec.EncodeFileTransferStatus(
                    false,
                    "远端剪贴板没有可回传的文件。"),
                session,
                writeLock,
                cancellationToken);
        }
    }

    private static async Task RunRejectThenRetrySyntheticHostAsync(
        TcpListener listener,
        string password,
        TaskCompletionSource<IReadOnlyList<RemoteControlKind>> wireOrder,
        TaskCompletionSource<bool> rejectObserved,
        Task allowRejectAcknowledgement,
        CancellationToken cancellationToken)
    {
        using TcpClient hostClient = await listener.AcceptTcpClientAsync(cancellationToken);
        NetworkUtils.ConfigureLowLatencyTcpClient(hostClient, 32 * 1024, 256 * 1024);
        await using NetworkStream stream = hostClient.GetStream();
        using var writeLock = new SemaphoreSlim(1, 1);
        ServerAuthenticationResult authentication = await Protocol.AuthenticateServerDetailedAsync(
            stream,
            password,
            cancellationToken);
        Assert.True(authentication.IsAuthenticated);
        using SecureSession session = authentication.Session!;

        bool viewerInfoReceived = false;
        bool viewerCapabilitiesReceived = false;
        while (!viewerInfoReceived || !viewerCapabilitiesReceived)
        {
            ProtocolMessage message = await Protocol.ReadMessageAsync(
                stream,
                session,
                cancellationToken);
            Assert.Equal(MessageType.Control, message.Type);
            RemoteControlMessage control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
            viewerInfoReceived |= control.Kind == RemoteControlKind.ViewerInfo;
            viewerCapabilitiesReceived |= control.Kind == RemoteControlKind.ViewerCapabilities;
        }

        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeDeviceInfo(new RemoteDeviceDescriptor(
                "reject-retry-loopback",
                RemoteDevicePlatforms.Windows,
                RemoteDeviceCapabilities.FileSend |
                    RemoteDeviceCapabilities.FileTransferPreview)),
            session,
            writeLock,
            cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            ProtocolMessage message = await Protocol.ReadMessageAsync(
                stream,
                session,
                cancellationToken);
            if (message.Type == MessageType.Control &&
                RemoteMessageCodec.DecodeControl(message.PayloadMemory).Kind ==
                    RemoteControlKind.FileTransferRequestClipboardFiles)
            {
                break;
            }
        }

        var previewItem = new FileTransferConfirmationItem(
            "文件",
            @"C:\remote\reject-me.txt",
            "reject-me.txt",
            4,
            @"C:\ignored\reject-me.txt");
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferClipboardFilesPreview([previewItem], null),
            session,
            writeLock,
            cancellationToken);

        var order = new List<RemoteControlKind>(2);
        while (!cancellationToken.IsCancellationRequested)
        {
            ProtocolMessage message = await Protocol.ReadMessageAsync(
                stream,
                session,
                cancellationToken);
            if (message.Type != MessageType.Control)
            {
                continue;
            }

            RemoteControlKind kind = RemoteMessageCodec.DecodeControl(message.PayloadMemory).Kind;
            if (kind == RemoteControlKind.FileTransferRejectClipboardFiles)
            {
                order.Add(kind);
                rejectObserved.TrySetResult(true);
                break;
            }
        }

        await allowRejectAcknowledgement.WaitAsync(cancellationToken);
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferStatus(
                false,
                "远端文件回传已取消。"),
            session,
            writeLock,
            cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            ProtocolMessage message = await Protocol.ReadMessageAsync(
                stream,
                session,
                cancellationToken);
            if (message.Type != MessageType.Control)
            {
                continue;
            }

            RemoteControlKind kind = RemoteMessageCodec.DecodeControl(message.PayloadMemory).Kind;
            if (kind == RemoteControlKind.FileTransferRequestClipboardFiles)
            {
                order.Add(kind);
                break;
            }
        }

        wireOrder.TrySetResult(order.ToArray());
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferStatus(
                false,
                "远端剪贴板没有可回传的文件。"),
            session,
            writeLock,
            cancellationToken);

        await DrainSyntheticHostUntilDisconnectedAsync(
            stream,
            session,
            writeLock,
            cancellationToken);
    }

    private static async Task RunConfirmCancellationThenRetrySyntheticHostAsync(
        TcpListener listener,
        string password,
        TaskCompletionSource<IReadOnlyList<RemoteControlKind>> wireOrder,
        TaskCompletionSource<bool> responsesObserved,
        Task allowCancellationAcknowledgement,
        CancellationToken cancellationToken)
    {
        using TcpClient hostClient = await listener.AcceptTcpClientAsync(cancellationToken);
        NetworkUtils.ConfigureLowLatencyTcpClient(hostClient, 32 * 1024, 256 * 1024);
        await using NetworkStream stream = hostClient.GetStream();
        using var writeLock = new SemaphoreSlim(1, 1);
        ServerAuthenticationResult authentication = await Protocol.AuthenticateServerDetailedAsync(
            stream,
            password,
            cancellationToken);
        Assert.True(authentication.IsAuthenticated);
        using SecureSession session = authentication.Session!;

        bool viewerInfoReceived = false;
        bool viewerCapabilitiesReceived = false;
        while (!viewerInfoReceived || !viewerCapabilitiesReceived)
        {
            ProtocolMessage message = await Protocol.ReadMessageAsync(
                stream,
                session,
                cancellationToken);
            Assert.Equal(MessageType.Control, message.Type);
            RemoteControlKind kind = RemoteMessageCodec.DecodeControl(message.PayloadMemory).Kind;
            viewerInfoReceived |= kind == RemoteControlKind.ViewerInfo;
            viewerCapabilitiesReceived |= kind == RemoteControlKind.ViewerCapabilities;
        }

        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeDeviceInfo(new RemoteDeviceDescriptor(
                "confirm-cancel-retry-loopback",
                RemoteDevicePlatforms.Windows,
                RemoteDeviceCapabilities.FileSend |
                    RemoteDeviceCapabilities.FileTransferPreview)),
            session,
            writeLock,
            cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            ProtocolMessage message = await Protocol.ReadMessageAsync(
                stream,
                session,
                cancellationToken);
            if (message.Type == MessageType.Control &&
                RemoteMessageCodec.DecodeControl(message.PayloadMemory).Kind ==
                    RemoteControlKind.FileTransferRequestClipboardFiles)
            {
                break;
            }
        }

        var previewItem = new FileTransferConfirmationItem(
            "文件",
            @"C:\remote\cancel-during-confirm.txt",
            "cancel-during-confirm.txt",
            4,
            @"C:\ignored\cancel-during-confirm.txt");
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferClipboardFilesPreview([previewItem], null),
            session,
            writeLock,
            cancellationToken);

        var order = new List<RemoteControlKind>(3);
        while (order.Count < 2 && !cancellationToken.IsCancellationRequested)
        {
            ProtocolMessage message = await Protocol.ReadMessageAsync(
                stream,
                session,
                cancellationToken);
            if (message.Type != MessageType.Control)
            {
                continue;
            }

            RemoteControlKind kind = RemoteMessageCodec.DecodeControl(message.PayloadMemory).Kind;
            if (kind is RemoteControlKind.FileTransferConfirmClipboardFiles or
                RemoteControlKind.FileTransferRejectClipboardFiles)
            {
                order.Add(kind);
            }
        }

        responsesObserved.TrySetResult(true);
        await allowCancellationAcknowledgement.WaitAsync(cancellationToken);
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferStatus(false, "远端文件回传已取消。"),
            session,
            writeLock,
            cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            ProtocolMessage message = await Protocol.ReadMessageAsync(
                stream,
                session,
                cancellationToken);
            if (message.Type != MessageType.Control)
            {
                continue;
            }

            RemoteControlKind kind = RemoteMessageCodec.DecodeControl(message.PayloadMemory).Kind;
            if (kind == RemoteControlKind.FileTransferRequestClipboardFiles)
            {
                order.Add(kind);
                break;
            }
        }

        wireOrder.TrySetResult(order.ToArray());
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferStatus(
                false,
                "远端剪贴板没有可回传的文件。"),
            session,
            writeLock,
            cancellationToken);
        await DrainSyntheticHostUntilDisconnectedAsync(
            stream,
            session,
            writeLock,
            cancellationToken);
    }

    private static async Task RunCapabilityHostUntilReleasedAsync(
        TcpListener listener,
        string password,
        Task releaseConnection,
        CancellationToken cancellationToken,
        string machineName = "first-capability-host",
        RemoteDeviceCapabilities advertisedCapabilities =
            RemoteDeviceCapabilities.FileSend |
            RemoteDeviceCapabilities.FileTransferPreview |
            RemoteDeviceCapabilities.FileChecksum |
            RemoteDeviceCapabilities.FileTransferCancel |
            RemoteDeviceCapabilities.RemoteUpdate |
            RemoteDeviceCapabilities.ClipboardSequenceTracking)
    {
        using TcpClient hostClient = await listener.AcceptTcpClientAsync(cancellationToken);
        NetworkUtils.ConfigureLowLatencyTcpClient(hostClient, 32 * 1024, 256 * 1024);
        await using NetworkStream stream = hostClient.GetStream();
        using var writeLock = new SemaphoreSlim(1, 1);
        ServerAuthenticationResult authentication = await Protocol.AuthenticateServerDetailedAsync(
            stream,
            password,
            cancellationToken);
        Assert.True(authentication.IsAuthenticated);
        using SecureSession session = authentication.Session!;

        ProtocolMessage viewerInfo = await Protocol.ReadMessageAsync(
            stream,
            session,
            cancellationToken);
        Assert.Equal(MessageType.Control, viewerInfo.Type);
        Assert.Equal(
            RemoteControlKind.ViewerInfo,
            RemoteMessageCodec.DecodeControl(viewerInfo.PayloadMemory).Kind);
        ProtocolMessage viewerCapabilities = await Protocol.ReadMessageAsync(
            stream,
            session,
            cancellationToken);
        Assert.Equal(MessageType.Control, viewerCapabilities.Type);
        Assert.Equal(
            RemoteControlKind.ViewerCapabilities,
            RemoteMessageCodec.DecodeControl(viewerCapabilities.PayloadMemory).Kind);

        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeDeviceInfo(new RemoteDeviceDescriptor(
                machineName,
                RemoteDevicePlatforms.Windows,
                advertisedCapabilities)),
            session,
            writeLock,
            cancellationToken);

        await releaseConnection.WaitAsync(cancellationToken);
    }

    private static async Task RunCapabilityHostThenCloseAsync(
        TcpListener listener,
        string password,
        CancellationToken cancellationToken)
    {
        using TcpClient hostClient =
            await listener.AcceptTcpClientAsync(
                cancellationToken);
        NetworkUtils.ConfigureLowLatencyTcpClient(
            hostClient,
            32 * 1024,
            256 * 1024);
        await using NetworkStream stream =
            hostClient.GetStream();
        using var writeLock = new SemaphoreSlim(1, 1);
        ServerAuthenticationResult authentication =
            await Protocol.AuthenticateServerDetailedAsync(
                stream,
                password,
                cancellationToken);
        Assert.True(authentication.IsAuthenticated);
        using SecureSession session = authentication.Session!;

        ProtocolMessage viewerInfo =
            await Protocol.ReadMessageAsync(
                stream,
                session,
                cancellationToken);
        Assert.Equal(MessageType.Control, viewerInfo.Type);
        ProtocolMessage viewerCapabilities =
            await Protocol.ReadMessageAsync(
                stream,
                session,
                cancellationToken);
        Assert.Equal(
            MessageType.Control,
            viewerCapabilities.Type);
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeDeviceInfo(
                new RemoteDeviceDescriptor(
                    "immediate-close-host",
                    RemoteDevicePlatforms.Windows,
                    RemoteDeviceCapabilities.InputControl)),
            session,
            writeLock,
            cancellationToken);
    }

    private static async Task
        RunAuthenticatedHostWithoutDeviceInfoAsync(
            TcpListener listener,
            string password,
            Task releaseConnection,
            CancellationToken cancellationToken)
    {
        using TcpClient hostClient =
            await listener.AcceptTcpClientAsync(
                cancellationToken);
        NetworkUtils.ConfigureLowLatencyTcpClient(
            hostClient,
            32 * 1024,
            256 * 1024);
        await using NetworkStream stream =
            hostClient.GetStream();
        ServerAuthenticationResult authentication =
            await Protocol.AuthenticateServerDetailedAsync(
                stream,
                password,
                cancellationToken);
        Assert.True(authentication.IsAuthenticated);
        using SecureSession session = authentication.Session!;

        for (int index = 0; index < 2; index++)
        {
            ProtocolMessage message =
                await Protocol.ReadMessageAsync(
                    stream,
                    session,
                    cancellationToken);
            Assert.Equal(MessageType.Control, message.Type);
        }

        // Authentication alone must not qualify this owner for a successful
        // reconnect. Keep the socket alive while the client checks the
        // generation so Release optimizations cannot turn an immediate EOF
        // race into an unrelated ConnectAsync failure.
        await releaseConnection.WaitAsync(cancellationToken);
    }

    private static async Task RunSingleInputBatchReceivingSyntheticHostAsync(
        TcpListener listener,
        string password,
        int expectedInputCount,
        TaskCompletionSource<IReadOnlyList<RemoteInputCommand>> receivedInputs,
        CancellationToken cancellationToken)
    {
        using TcpClient hostClient = await listener.AcceptTcpClientAsync(cancellationToken);
        NetworkUtils.ConfigureLowLatencyTcpClient(hostClient, 32 * 1024, 256 * 1024);
        await using NetworkStream stream = hostClient.GetStream();
        ServerAuthenticationResult authentication = await Protocol.AuthenticateServerDetailedAsync(
            stream,
            password,
            cancellationToken);
        Assert.True(authentication.IsAuthenticated);
        using SecureSession session = authentication.Session!;

        var inputs = new List<RemoteInputCommand>(expectedInputCount);
        while (!cancellationToken.IsCancellationRequested)
        {
            ProtocolMessage message = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
            if (message.Type != MessageType.Input)
            {
                continue;
            }

            inputs.Add(RemoteMessageCodec.DecodeInput(message.PayloadSpan));
            if (inputs.Count >= expectedInputCount)
            {
                receivedInputs.TrySetResult(inputs.ToArray());
                return;
            }
        }
    }

    private static async Task RunDelayedDeviceInfoSyntheticHostAsync(
        TcpListener listener,
        string password,
        TaskCompletionSource<RemoteDeviceCapabilities> viewerCapabilitiesReceived,
        Task releaseDeviceInfo,
        TaskCompletionSource<RemoteControlKind> requestReceived,
        CancellationToken cancellationToken)
    {
        using TcpClient hostClient = await listener.AcceptTcpClientAsync(cancellationToken);
        NetworkUtils.ConfigureLowLatencyTcpClient(hostClient, 32 * 1024, 256 * 1024);
        await using NetworkStream stream = hostClient.GetStream();
        using var writeLock = new SemaphoreSlim(1, 1);
        ServerAuthenticationResult authentication = await Protocol.AuthenticateServerDetailedAsync(
            stream,
            password,
            cancellationToken);
        Assert.True(authentication.IsAuthenticated);
        using SecureSession session = authentication.Session!;

        bool viewerInfoReceived = false;
        while (!viewerInfoReceived || !viewerCapabilitiesReceived.Task.IsCompleted)
        {
            ProtocolMessage message = await Protocol.ReadMessageAsync(
                stream,
                session,
                cancellationToken);
            if (message.Type != MessageType.Control)
            {
                continue;
            }

            RemoteControlMessage control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
            if (control.Kind == RemoteControlKind.ViewerInfo)
            {
                viewerInfoReceived = true;
            }
            else if (control.Kind == RemoteControlKind.ViewerCapabilities)
            {
                viewerCapabilitiesReceived.TrySetResult(control.Capabilities);
            }
        }

        await releaseDeviceInfo.WaitAsync(cancellationToken);
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeDeviceInfo(new RemoteDeviceDescriptor(
                "delayed-capabilities-loopback",
                RemoteDevicePlatforms.Windows,
                RemoteDeviceCapabilities.FileSend |
                    RemoteDeviceCapabilities.FileTransferPreview |
                    RemoteDeviceCapabilities.FileChecksum |
                    RemoteDeviceCapabilities.FileTransferCancel)),
            session,
            writeLock,
            cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            ProtocolMessage message = await Protocol.ReadMessageAsync(
                stream,
                session,
                cancellationToken);
            if (message.Type != MessageType.Control)
            {
                continue;
            }

            RemoteControlMessage control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
            if (control.Kind != RemoteControlKind.FileTransferRequestClipboardFiles)
            {
                continue;
            }

            requestReceived.TrySetResult(control.Kind);
            await Protocol.WriteMessageAsync(
                stream,
                MessageType.Control,
                RemoteMessageCodec.EncodeFileTransferStatus(
                    false,
                    "远端剪贴板没有可回传的文件。"),
                session,
                writeLock,
                cancellationToken);
            break;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Protocol.ReadMessageAsync(stream, session, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or
            SocketException or ObjectDisposedException or CryptographicException)
        {
        }
    }

    private static async Task RunSyntheticHostAsync(
        TcpListener listener,
        string password,
        TaskCompletionSource<RemoteVideoCodecs> viewerInfoReceived,
        CancellationToken cancellationToken)
    {
        using TcpClient hostClient = await listener.AcceptTcpClientAsync(cancellationToken);
        NetworkUtils.ConfigureLowLatencyTcpClient(hostClient, 32 * 1024, 256 * 1024);
        await using NetworkStream stream = hostClient.GetStream();
        using var writeLock = new SemaphoreSlim(1, 1);

        ServerAuthenticationResult authentication = await Protocol.AuthenticateServerDetailedAsync(
            stream,
            password,
            cancellationToken);
        Assert.True(authentication.IsAuthenticated);

        using SecureSession session = authentication.Session!;

        ProtocolMessage viewerInfoMessage = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
        Assert.Equal(MessageType.Control, viewerInfoMessage.Type);
        RemoteControlMessage viewerInfo = RemoteMessageCodec.DecodeControl(viewerInfoMessage.PayloadMemory);
        Assert.Equal(RemoteControlKind.ViewerInfo, viewerInfo.Kind);
        viewerInfoReceived.TrySetResult(viewerInfo.SupportedVideoCodecs);

        var capabilities =
            RemoteDeviceCapabilities.RemoteDesktop |
            RemoteDeviceCapabilities.InputControl |
            RemoteDeviceCapabilities.ClipboardText |
            RemoteDeviceCapabilities.FileReceive;
        var target = new CaptureTargetInfo("android-screen", "Android Screen");

        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeDeviceInfo(new RemoteDeviceDescriptor(
                "android-loopback",
                RemoteDevicePlatforms.Android,
                capabilities)),
            session,
            writeLock,
            cancellationToken);

        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeCaptureTargetList([target]),
            session,
            writeLock,
            cancellationToken);

        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeCaptureTargetChanged(target),
            session,
            writeLock,
            cancellationToken);

        await Protocol.WriteFrameMessageAsync(
            stream,
            width: 2,
            height: 2,
            captureMilliseconds: 1.5,
            encodeMilliseconds: 0.7,
            jpegBytes: new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 },
            session,
            writeLock,
            cancellationToken);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Protocol.ReadMessageAsync(stream, session, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or
            SocketException or ObjectDisposedException or CryptographicException)
        {
        }
    }

    private static async Task
        RunCaptureAvailabilitySyntheticHostAsync(
            TcpListener listener,
            IReadOnlyList<string> statuses,
            CancellationToken cancellationToken)
    {
        using TcpClient hostClient =
            await listener.AcceptTcpClientAsync(
                cancellationToken);
        NetworkUtils.ConfigureLowLatencyTcpClient(
            hostClient,
            32 * 1024,
            256 * 1024);
        await using NetworkStream stream =
            hostClient.GetStream();
        using var writeLock =
            new SemaphoreSlim(1, 1);

        ServerAuthenticationResult authentication =
            await Protocol.AuthenticateServerDetailedAsync(
                stream,
                LoopbackPassword,
                cancellationToken);
        Assert.True(authentication.IsAuthenticated);
        using SecureSession session =
            authentication.Session!;

        ProtocolMessage viewerInfo =
            await Protocol.ReadMessageAsync(
                stream,
                session,
                cancellationToken);
        Assert.Equal(MessageType.Control, viewerInfo.Type);
        Assert.Equal(
            RemoteControlKind.ViewerInfo,
            RemoteMessageCodec.DecodeControl(
                viewerInfo.PayloadMemory).Kind);
        ProtocolMessage viewerCapabilities =
            await Protocol.ReadMessageAsync(
                stream,
                session,
                cancellationToken);
        Assert.Equal(
            RemoteControlKind.ViewerCapabilities,
            RemoteMessageCodec.DecodeControl(
                viewerCapabilities.PayloadMemory).Kind);

        foreach (string status in statuses)
        {
            await Protocol.WriteMessageAsync(
                stream,
                MessageType.Control,
                RemoteMessageCodec.EncodeClipboardStatus(
                    success: true,
                    status),
                session,
                writeLock,
                cancellationToken);
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Protocol.ReadMessageAsync(
                    stream,
                    session,
                    cancellationToken);
            }
        }
        catch (Exception ex) when (
            ex is IOException or
                EndOfStreamException or
                SocketException or
                ObjectDisposedException or
                CryptographicException or
                OperationCanceledException)
        {
        }
    }

    private static async Task
        RunCaptureTargetHostUntilReleasedAsync(
            TcpListener listener,
            CaptureTargetInfo target,
            Task releaseConnection,
            CancellationToken cancellationToken)
    {
        using TcpClient hostClient =
            await listener.AcceptTcpClientAsync(
                cancellationToken);
        NetworkUtils.ConfigureLowLatencyTcpClient(
            hostClient,
            32 * 1024,
            256 * 1024);
        await using NetworkStream stream =
            hostClient.GetStream();
        using var writeLock = new SemaphoreSlim(1, 1);
        ServerAuthenticationResult authentication =
            await Protocol.AuthenticateServerDetailedAsync(
                stream,
                LoopbackPassword,
                cancellationToken);
        Assert.True(authentication.IsAuthenticated);
        using SecureSession session = authentication.Session!;

        for (int index = 0; index < 2; index++)
        {
            ProtocolMessage viewerMessage =
                await Protocol.ReadMessageAsync(
                    stream,
                    session,
                    cancellationToken);
            Assert.Equal(MessageType.Control, viewerMessage.Type);
        }

        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeCaptureTargetList([target]),
            session,
            writeLock,
            cancellationToken);
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeCaptureTargetChanged(target),
            session,
            writeLock,
            cancellationToken);
        await releaseConnection.WaitAsync(cancellationToken);
    }

    private static async Task RunMismatchedCancelDuringFileReturningSyntheticHostAsync(
        TcpListener listener,
        string password,
        byte[] firstChunk,
        byte[] secondChunk,
        Task releaseHost,
        CancellationToken cancellationToken)
    {
        using TcpClient hostClient = await listener.AcceptTcpClientAsync(cancellationToken);
        NetworkUtils.ConfigureLowLatencyTcpClient(hostClient, 32 * 1024, 256 * 1024);
        await using NetworkStream stream = hostClient.GetStream();
        using var writeLock = new SemaphoreSlim(1, 1);

        ServerAuthenticationResult authentication = await Protocol.AuthenticateServerDetailedAsync(
            stream,
            password,
            cancellationToken);
        Assert.True(authentication.IsAuthenticated);
        using SecureSession session = authentication.Session!;

        ProtocolMessage viewerInfoMessage = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
        Assert.Equal(MessageType.Control, viewerInfoMessage.Type);
        Assert.Equal(
            RemoteControlKind.ViewerInfo,
            RemoteMessageCodec.DecodeControl(viewerInfoMessage.PayloadMemory).Kind);

        const string transferId = "active-transfer";
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferStart(
                transferId,
                "stale-cancel.bin",
                firstChunk.Length + secondChunk.Length),
            session,
            writeLock,
            cancellationToken);
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferChunk(transferId, 0, firstChunk),
            session,
            writeLock,
            cancellationToken);
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferCancel("stale-transfer", "late cancel"),
            session,
            writeLock,
            cancellationToken);
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferChunk(transferId, firstChunk.Length, secondChunk),
            session,
            writeLock,
            cancellationToken);
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferComplete(transferId),
            session,
            writeLock,
            cancellationToken);

        await releaseHost.WaitAsync(cancellationToken);
    }

    private static async Task RunFileReceivingSyntheticHostAsync(
        TcpListener listener,
        string password,
        TaskCompletionSource<ReceivedLoopbackFile> receivedFile,
        CancellationToken cancellationToken,
        bool advertiseChecksum = false,
        bool expectChecksum = false,
        RemoteDeviceCapabilities? advertisedCapabilities = null,
        RemoteControlKind expectedStartKind = RemoteControlKind.FileTransferStart)
    {
        using TcpClient hostClient = await listener.AcceptTcpClientAsync(cancellationToken);
        NetworkUtils.ConfigureLowLatencyTcpClient(hostClient, 32 * 1024, 256 * 1024);
        await using NetworkStream stream = hostClient.GetStream();
        using var writeLock = new SemaphoreSlim(1, 1);

        ServerAuthenticationResult authentication = await Protocol.AuthenticateServerDetailedAsync(
            stream,
            password,
            cancellationToken);
        Assert.True(authentication.IsAuthenticated);

        using SecureSession session = authentication.Session!;

        ProtocolMessage viewerInfoMessage = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
        Assert.Equal(MessageType.Control, viewerInfoMessage.Type);
        Assert.Equal(
            RemoteControlKind.ViewerInfo,
            RemoteMessageCodec.DecodeControl(viewerInfoMessage.PayloadMemory).Kind);

        RemoteDeviceCapabilities capabilities = advertisedCapabilities ??
            (advertiseChecksum
                ? RemoteDeviceCapabilities.FileReceive |
                    RemoteDeviceCapabilities.FileChecksum |
                    RemoteDeviceCapabilities.FileTransferCancel
                : RemoteDeviceCapabilities.None);
        if (capabilities != RemoteDeviceCapabilities.None)
        {
            await Protocol.WriteMessageAsync(
                stream,
                MessageType.Control,
                RemoteMessageCodec.EncodeDeviceInfo(new RemoteDeviceDescriptor(
                    "windows-checksum-loopback",
                    RemoteDevicePlatforms.Windows,
                    capabilities)),
                session,
                writeLock,
                cancellationToken);
        }

        string? transferId = null;
        string? fileName = null;
        string? checksumHex = null;
        long fileLength = -1;
        long nextOffset = 0;
        int chunkCount = 0;
        bool viewerChecksumCapabilityReceived = false;
        using var output = new MemoryStream();

        while (!cancellationToken.IsCancellationRequested)
        {
            ProtocolMessage message = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
            Assert.Equal(MessageType.Control, message.Type);
            RemoteControlMessage control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);

            switch (control.Kind)
            {
                case RemoteControlKind.ViewerCapabilities:
                    if (advertiseChecksum)
                    {
                        Assert.True(control.Capabilities.HasFlag(RemoteDeviceCapabilities.FileChecksum));
                        Assert.True(control.Capabilities.HasFlag(RemoteDeviceCapabilities.FileTransferCancel));
                        viewerChecksumCapabilityReceived = true;
                    }

                    break;
                case RemoteControlKind.FileTransferStart:
                case RemoteControlKind.RemoteUpdateStart:
                    Assert.Equal(expectedStartKind, control.Kind);
                    if (advertiseChecksum)
                    {
                        Assert.True(viewerChecksumCapabilityReceived);
                    }

                    transferId = control.TransferId;
                    fileName = control.FileName;
                    fileLength = control.FileLength;
                    Assert.False(string.IsNullOrWhiteSpace(transferId));
                    Assert.False(string.IsNullOrWhiteSpace(fileName));
                    Assert.True(fileLength >= 0);
                    break;
                case RemoteControlKind.FileTransferChunk:
                    Assert.Equal(transferId, control.TransferId);
                    Assert.Equal(nextOffset, control.FileOffset);
                    Assert.InRange(
                        control.FileBytes.Length,
                        1,
                        RemoteMessageCodec.RecommendedFileTransferChunkBytes);
                    output.Write(control.FileBytes.Span);
                    nextOffset += control.FileBytes.Length;
                    chunkCount++;
                    break;
                case RemoteControlKind.FileTransferChecksum:
                    Assert.Equal(transferId, control.TransferId);
                    Assert.Equal(RemoteMessageCodec.FileTransferChecksumAlgorithm, control.ChecksumAlgorithm);
                    checksumHex = control.ChecksumHex;
                    break;
                case RemoteControlKind.FileTransferComplete:
                    Assert.Equal(transferId, control.TransferId);
                    Assert.Equal(fileLength, output.Length);
                    if (expectChecksum)
                    {
                        Assert.False(string.IsNullOrWhiteSpace(checksumHex));
                        Assert.Equal(
                            Convert.ToHexString(SHA256.HashData(output.ToArray())).ToLowerInvariant(),
                            checksumHex);
                    }

                    receivedFile.TrySetResult(new ReceivedLoopbackFile(
                        fileName ?? string.Empty,
                        fileLength,
                        output.ToArray(),
                        chunkCount));
                    await Protocol.WriteMessageAsync(
                        stream,
                        MessageType.Control,
                        RemoteMessageCodec.EncodeFileTransferStatus(true, "loopback host saved file"),
                        session,
                        writeLock,
                        cancellationToken);
                    return;
            }
        }
    }

    private static async Task RunIdleReturnRequestSyntheticHostAsync(
        TcpListener listener,
        string password,
        TaskCompletionSource<RemoteControlKind> requestReceived,
        TaskCompletionSource<bool> peerDisconnected,
        int activityCount,
        TimeSpan activityDelay,
        bool sendTerminalStatus,
        CancellationToken cancellationToken)
    {
        using TcpClient hostClient = await listener.AcceptTcpClientAsync(cancellationToken);
        NetworkUtils.ConfigureLowLatencyTcpClient(hostClient, 32 * 1024, 256 * 1024);
        await using NetworkStream stream = hostClient.GetStream();
        using var writeLock = new SemaphoreSlim(1, 1);

        ServerAuthenticationResult authentication = await Protocol.AuthenticateServerDetailedAsync(
            stream,
            password,
            cancellationToken);
        Assert.True(authentication.IsAuthenticated);
        using SecureSession session = authentication.Session!;

        ProtocolMessage viewerInfoMessage = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
        Assert.Equal(MessageType.Control, viewerInfoMessage.Type);
        Assert.Equal(
            RemoteControlKind.ViewerInfo,
            RemoteMessageCodec.DecodeControl(viewerInfoMessage.PayloadMemory).Kind);

        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeDeviceInfo(new RemoteDeviceDescriptor(
                "idle-return-loopback",
                RemoteDevicePlatforms.Windows,
                RemoteDeviceCapabilities.FileSend |
                    RemoteDeviceCapabilities.FileTransferPreview)),
            session,
            writeLock,
            cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            ProtocolMessage message = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
            if (message.Type != MessageType.Control)
            {
                continue;
            }

            RemoteControlMessage control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
            if (control.Kind == RemoteControlKind.FileTransferRequestClipboardFiles)
            {
                requestReceived.TrySetResult(control.Kind);
                break;
            }
        }

        for (int index = 0; index < activityCount; index++)
        {
            await Task.Delay(activityDelay, cancellationToken);
            await Protocol.WriteMessageAsync(
                stream,
                MessageType.Control,
                RemoteMessageCodec.EncodeFileTransferStatus(
                    true,
                    $"远端文件仍在准备：{index + 1}/{activityCount}"),
                session,
                writeLock,
                cancellationToken);
        }

        if (sendTerminalStatus)
        {
            await Protocol.WriteMessageAsync(
                stream,
                MessageType.Control,
                RemoteMessageCodec.EncodeFileTransferStatus(
                    false,
                    "远端剪贴板没有可回传的文件。"),
                session,
                writeLock,
                cancellationToken);
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Protocol.ReadMessageAsync(stream, session, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or
            SocketException or ObjectDisposedException or CryptographicException)
        {
            peerDisconnected.TrySetResult(true);
        }
    }

    private static async Task RunActivePartialLegacyReturnSyntheticHostAsync(
        TcpListener listener,
        string password,
        string fileName,
        byte[] partialBytes,
        Task releaseHost,
        CancellationToken cancellationToken)
    {
        using TcpClient hostClient = await listener.AcceptTcpClientAsync(cancellationToken);
        NetworkUtils.ConfigureLowLatencyTcpClient(hostClient, 32 * 1024, 256 * 1024);
        await using NetworkStream stream = hostClient.GetStream();
        using var writeLock = new SemaphoreSlim(1, 1);
        ServerAuthenticationResult authentication = await Protocol.AuthenticateServerDetailedAsync(
            stream,
            password,
            cancellationToken);
        Assert.True(authentication.IsAuthenticated);
        using SecureSession session = authentication.Session!;

        bool viewerInfoReceived = false;
        bool viewerCapabilitiesReceived = false;
        while (!viewerInfoReceived || !viewerCapabilitiesReceived)
        {
            ProtocolMessage message = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
            if (message.Type != MessageType.Control)
            {
                continue;
            }

            RemoteControlKind kind = RemoteMessageCodec.DecodeControl(message.PayloadMemory).Kind;
            viewerInfoReceived |= kind == RemoteControlKind.ViewerInfo;
            viewerCapabilitiesReceived |= kind == RemoteControlKind.ViewerCapabilities;
        }

        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeDeviceInfo(new RemoteDeviceDescriptor(
                "active-partial-legacy-loopback",
                RemoteDevicePlatforms.Windows,
                RemoteDeviceCapabilities.FileSend)),
            session,
            writeLock,
            cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            ProtocolMessage message = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
            if (message.Type == MessageType.Control &&
                RemoteMessageCodec.DecodeControl(message.PayloadMemory).Kind ==
                    RemoteControlKind.FileTransferRequestClipboardFiles)
            {
                break;
            }
        }

        const string transferId = "active-partial-legacy-transfer";
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferStart(
                transferId,
                fileName,
                partialBytes.Length + 1L),
            session,
            writeLock,
            cancellationToken);
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferChunk(transferId, 0, partialBytes),
            session,
            writeLock,
            cancellationToken);
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferStatus(true, "远端文件回传完成：1 个"),
            session,
            writeLock,
            cancellationToken);

        await releaseHost.WaitAsync(cancellationToken);
    }

    private static async Task RunFileReturningSyntheticHostAsync(
        TcpListener listener,
        string password,
        string fileName,
        byte[] fileBytes,
        TaskCompletionSource<RemoteControlKind> requestReceived,
        TaskCompletionSource<string> hostReceivedStatus,
        CancellationToken cancellationToken,
        bool sendReturnCompleteStatus = false,
        RemoteDeviceCapabilities? advertisedCapabilities = null,
        bool terminalStatusWithoutWaitingForSave = false,
        bool terminalSuccess = true,
        string? terminalMessage = null)
    {
        using TcpClient hostClient = await listener.AcceptTcpClientAsync(cancellationToken);
        NetworkUtils.ConfigureLowLatencyTcpClient(hostClient, 32 * 1024, 256 * 1024);
        await using NetworkStream stream = hostClient.GetStream();
        using var writeLock = new SemaphoreSlim(1, 1);

        ServerAuthenticationResult authentication = await Protocol.AuthenticateServerDetailedAsync(
            stream,
            password,
            cancellationToken);
        Assert.True(authentication.IsAuthenticated);

        using SecureSession session = authentication.Session!;

        ProtocolMessage viewerInfoMessage = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
        Assert.Equal(MessageType.Control, viewerInfoMessage.Type);
        Assert.Equal(
            RemoteControlKind.ViewerInfo,
            RemoteMessageCodec.DecodeControl(viewerInfoMessage.PayloadMemory).Kind);

        RemoteDeviceCapabilities capabilities =
            advertisedCapabilities ?? RemoteDeviceCapabilities.FileSend;
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeDeviceInfo(new RemoteDeviceDescriptor(
                "legacy-file-return-loopback",
                RemoteDevicePlatforms.Windows,
                capabilities)),
            session,
            writeLock,
            cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            ProtocolMessage message = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
            Assert.Equal(MessageType.Control, message.Type);
            RemoteControlMessage control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
            if (control.Kind != RemoteControlKind.FileTransferRequestClipboardFiles)
            {
                continue;
            }

            requestReceived.TrySetResult(control.Kind);
            break;
        }

        string transferId = "return-file-transfer";
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferStart(transferId, fileName, fileBytes.Length),
            session,
            writeLock,
            cancellationToken);

        long offset = 0;
        while (offset < fileBytes.Length)
        {
            int length = (int)Math.Min(RemoteMessageCodec.FileTransferChunkBytes, fileBytes.Length - offset);
            await Protocol.WriteMessageAsync(
                stream,
                MessageType.Control,
                RemoteMessageCodec.EncodeFileTransferChunk(
                    transferId,
                    offset,
                    fileBytes.AsMemory((int)offset, length)),
                session,
                writeLock,
                cancellationToken);
            offset += length;
        }

        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferComplete(transferId),
            session,
            writeLock,
            cancellationToken);

        if (sendReturnCompleteStatus && terminalStatusWithoutWaitingForSave)
        {
            await Protocol.WriteMessageAsync(
                stream,
                MessageType.Control,
                RemoteMessageCodec.EncodeFileTransferStatus(
                    terminalSuccess,
                    terminalMessage ?? "远端文件回传完成：1 个"),
                session,
                writeLock,
                cancellationToken);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Protocol.ReadMessageAsync(stream, session, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or
                SocketException or ObjectDisposedException or CryptographicException)
            {
            }

            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            ProtocolMessage statusMessage = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
            if (statusMessage.Type != MessageType.Control)
            {
                continue;
            }

            RemoteControlMessage status = RemoteMessageCodec.DecodeControl(statusMessage.PayloadMemory);
            if (status.Kind == RemoteControlKind.FileTransferStatus &&
                status.Success &&
                status.StatusMessage?.Contains("文件已保存到本机", StringComparison.Ordinal) == true)
            {
                hostReceivedStatus.TrySetResult(status.StatusMessage);
                if (sendReturnCompleteStatus)
                {
                    await Protocol.WriteMessageAsync(
                        stream,
                        MessageType.Control,
                        RemoteMessageCodec.EncodeFileTransferStatus(
                            terminalSuccess,
                            terminalMessage ?? "远端文件回传完成：1 个"),
                        session,
                        writeLock,
                        cancellationToken);
                }

                return;
            }
        }
    }

    private static async Task RunPreviewReturningSyntheticHostAsync(
        TcpListener listener,
        string password,
        TaskCompletionSource<RemoteControlKind> decisionReceived,
        CancellationToken cancellationToken,
        string? returnedFileName = null,
        byte[]? returnedFileBytes = null,
        Task? previewGate = null,
        TaskCompletionSource<RemoteControlKind>? requestReceived = null,
        string? previewTransferName = null,
        long? previewSizeBytes = null,
        string previewKind = "文件",
        long? returnedFileLength = null)
    {
        using TcpClient hostClient = await listener.AcceptTcpClientAsync(cancellationToken);
        NetworkUtils.ConfigureLowLatencyTcpClient(hostClient, 32 * 1024, 256 * 1024);
        await using NetworkStream stream = hostClient.GetStream();
        using var writeLock = new SemaphoreSlim(1, 1);

        ServerAuthenticationResult authentication = await Protocol.AuthenticateServerDetailedAsync(
            stream,
            password,
            cancellationToken);
        Assert.True(authentication.IsAuthenticated);

        using SecureSession session = authentication.Session!;

        ProtocolMessage viewerInfoMessage = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
        Assert.Equal(MessageType.Control, viewerInfoMessage.Type);
        Assert.Equal(
            RemoteControlKind.ViewerInfo,
            RemoteMessageCodec.DecodeControl(viewerInfoMessage.PayloadMemory).Kind);

        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeDeviceInfo(new RemoteDeviceDescriptor(
                "windows-preview-loopback",
                RemoteDevicePlatforms.Windows,
                RemoteDeviceCapabilities.FileSend |
                    RemoteDeviceCapabilities.FileTransferPreview)),
            session,
            writeLock,
            cancellationToken);

        bool viewerSupportsPreview = false;
        bool fileRequestReceived = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            ProtocolMessage message = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
            Assert.Equal(MessageType.Control, message.Type);
            RemoteControlMessage control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
            if (control.Kind == RemoteControlKind.ViewerCapabilities)
            {
                Assert.True(control.Capabilities.HasFlag(RemoteDeviceCapabilities.FileTransferPreview));
                viewerSupportsPreview = true;
            }
            else if (control.Kind == RemoteControlKind.FileTransferRequestClipboardFiles)
            {
                fileRequestReceived = true;
                requestReceived?.TrySetResult(control.Kind);
            }

            if (viewerSupportsPreview && fileRequestReceived)
            {
                break;
            }
        }

        if (previewGate is not null)
        {
            await previewGate.WaitAsync(cancellationToken);
        }

        string previewFileName = previewTransferName ?? returnedFileName ?? "remote-preview.txt";
        var previewItem = new FileTransferConfirmationItem(
            previewKind,
            $@"C:\remote\{previewFileName}",
            previewFileName,
            previewSizeBytes ?? returnedFileBytes?.Length ?? 4,
            $@"C:\ignored\{previewFileName}");
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferClipboardFilesPreview([previewItem], "preview-note"),
            session,
            writeLock,
            cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            ProtocolMessage message = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
            Assert.Equal(MessageType.Control, message.Type);
            RemoteControlMessage control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
            if (control.Kind is RemoteControlKind.FileTransferConfirmClipboardFiles or
                RemoteControlKind.FileTransferRejectClipboardFiles)
            {
                decisionReceived.TrySetResult(control.Kind);
                if (control.Kind == RemoteControlKind.FileTransferRejectClipboardFiles ||
                    string.IsNullOrWhiteSpace(returnedFileName) ||
                    returnedFileBytes is null)
                {
                    await Protocol.WriteMessageAsync(
                        stream,
                        MessageType.Control,
                        RemoteMessageCodec.EncodeFileTransferStatus(
                            false,
                            control.Kind == RemoteControlKind.FileTransferRejectClipboardFiles
                                ? "远端文件回传已取消。"
                                : "远端剪贴板没有可回传的文件。"),
                        session,
                        writeLock,
                        cancellationToken);
                    await DrainSyntheticHostUntilDisconnectedAsync(
                        stream,
                        session,
                        writeLock,
                        cancellationToken);
                    return;
                }

                break;
            }
        }

        string transferFileName = returnedFileName!;
        byte[] transferFileBytes = returnedFileBytes!;
        const string transferId = "drag-out-preview-transfer";
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferStart(
                transferId,
                transferFileName,
                returnedFileLength ?? transferFileBytes.Length),
            session,
            writeLock,
            cancellationToken);
        long transferOffset = 0;
        while (transferOffset < transferFileBytes.Length)
        {
            int chunkLength = Math.Min(
                RemoteMessageCodec.FileTransferChunkBytes,
                transferFileBytes.Length - checked((int)transferOffset));
            await Protocol.WriteMessageAsync(
                stream,
                MessageType.Control,
                RemoteMessageCodec.EncodeFileTransferChunk(
                    transferId,
                    transferOffset,
                    transferFileBytes.AsMemory(checked((int)transferOffset), chunkLength)),
                session,
                writeLock,
                cancellationToken);
            transferOffset += chunkLength;
        }
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferComplete(transferId),
            session,
            writeLock,
            cancellationToken);
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferStatus(true, "远端文件回传完成：1 个"),
            session,
            writeLock,
            cancellationToken);

        await DrainSyntheticHostUntilDisconnectedAsync(
            stream,
            session,
            writeLock,
            cancellationToken);
    }

    private static async Task DrainSyntheticHostUntilDisconnectedAsync(
        NetworkStream stream,
        SecureSession session,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        // Keep synthetic hosts alive until the viewer disconnects and drain all receipts.
        // Closing a Windows socket with unread viewer status messages can emit an RST and make
        // an already-written terminal status disappear intermittently on the client side.
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ProtocolMessage receipt = await Protocol.ReadMessageAsync(
                    stream,
                    session,
                    cancellationToken);
                if (receipt.Type == MessageType.Ping)
                {
                    await Protocol.WriteMessageAsync(
                        stream,
                        MessageType.Pong,
                        ReadOnlyMemory<byte>.Empty,
                        session,
                        writeLock,
                        cancellationToken);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or
            SocketException or ObjectDisposedException or CryptographicException)
        {
        }
    }

    private static async Task RunChecksumFileReturningSyntheticHostAsync(
        TcpListener listener,
        string password,
        string fileName,
        byte[] fileBytes,
        TaskCompletionSource<RemoteControlKind> requestReceived,
        TaskCompletionSource<RemoteDeviceCapabilities> viewerCapabilitiesReceived,
        TaskCompletionSource<string> hostReceivedStatus,
        CancellationToken cancellationToken,
        bool sendChecksum = true,
        bool expectSuccessfulSave = true)
    {
        using TcpClient hostClient = await listener.AcceptTcpClientAsync(cancellationToken);
        NetworkUtils.ConfigureLowLatencyTcpClient(hostClient, 32 * 1024, 256 * 1024);
        await using NetworkStream stream = hostClient.GetStream();
        using var writeLock = new SemaphoreSlim(1, 1);

        ServerAuthenticationResult authentication = await Protocol.AuthenticateServerDetailedAsync(
            stream,
            password,
            cancellationToken);
        Assert.True(authentication.IsAuthenticated);

        using SecureSession session = authentication.Session!;

        ProtocolMessage viewerInfoMessage = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
        Assert.Equal(MessageType.Control, viewerInfoMessage.Type);
        Assert.Equal(
            RemoteControlKind.ViewerInfo,
            RemoteMessageCodec.DecodeControl(viewerInfoMessage.PayloadMemory).Kind);

        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeDeviceInfo(new RemoteDeviceDescriptor(
                "windows-return-checksum-loopback",
                RemoteDevicePlatforms.Windows,
                RemoteDeviceCapabilities.FileSend |
                RemoteDeviceCapabilities.FileChecksum |
                RemoteDeviceCapabilities.FileTransferCancel)),
            session,
            writeLock,
            cancellationToken);

        bool remoteFileRequestReceived = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            ProtocolMessage message = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
            Assert.Equal(MessageType.Control, message.Type);
            RemoteControlMessage control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
            if (control.Kind == RemoteControlKind.ViewerCapabilities)
            {
                viewerCapabilitiesReceived.TrySetResult(control.Capabilities);
            }
            else if (control.Kind == RemoteControlKind.FileTransferRequestClipboardFiles)
            {
                requestReceived.TrySetResult(control.Kind);
                remoteFileRequestReceived = true;
            }

            if (remoteFileRequestReceived && viewerCapabilitiesReceived.Task.IsCompleted)
            {
                break;
            }
        }

        string transferId = "return-file-transfer-checksum";
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferStart(transferId, fileName, fileBytes.Length),
            session,
            writeLock,
            cancellationToken);

        long offset = 0;
        using var checksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (offset < fileBytes.Length)
        {
            int length = (int)Math.Min(RemoteMessageCodec.FileTransferChunkBytes, fileBytes.Length - offset);
            checksum.AppendData(fileBytes.AsSpan((int)offset, length));
            await Protocol.WriteMessageAsync(
                stream,
                MessageType.Control,
                RemoteMessageCodec.EncodeFileTransferChunk(
                    transferId,
                    offset,
                    fileBytes.AsMemory((int)offset, length)),
                session,
                writeLock,
                cancellationToken);
            offset += length;
        }

        if (sendChecksum)
        {
            await Protocol.WriteMessageAsync(
                stream,
                MessageType.Control,
                RemoteMessageCodec.EncodeFileTransferChecksum(
                    transferId,
                    Convert.ToHexString(checksum.GetHashAndReset()).ToLowerInvariant()),
                session,
                writeLock,
                cancellationToken);
        }

        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferComplete(transferId),
            session,
            writeLock,
            cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            ProtocolMessage statusMessage = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
            if (statusMessage.Type != MessageType.Control)
            {
                continue;
            }

            RemoteControlMessage status = RemoteMessageCodec.DecodeControl(statusMessage.PayloadMemory);
            if (status.Kind != RemoteControlKind.FileTransferStatus ||
                status.Success != expectSuccessfulSave ||
                string.IsNullOrWhiteSpace(status.StatusMessage))
            {
                continue;
            }

            bool isExpectedTerminalStatus = expectSuccessfulSave
                ? status.StatusMessage.Contains("SHA-256 已校验", StringComparison.Ordinal)
                : status.StatusMessage.Contains("未发送文件 SHA-256 校验值", StringComparison.Ordinal);
            if (isExpectedTerminalStatus)
            {
                hostReceivedStatus.TrySetResult(status.StatusMessage);
                return;
            }
        }
    }

    private static async Task RunCancelFileReturningSyntheticHostAsync(
        TcpListener listener,
        string password,
        string fileName,
        byte[] fileBytes,
        TaskCompletionSource<string> hostReceivedStatus,
        CancellationToken cancellationToken)
    {
        using TcpClient hostClient = await listener.AcceptTcpClientAsync(cancellationToken);
        NetworkUtils.ConfigureLowLatencyTcpClient(hostClient, 32 * 1024, 256 * 1024);
        await using NetworkStream stream = hostClient.GetStream();
        using var writeLock = new SemaphoreSlim(1, 1);

        ServerAuthenticationResult authentication = await Protocol.AuthenticateServerDetailedAsync(
            stream,
            password,
            cancellationToken);
        Assert.True(authentication.IsAuthenticated);

        using SecureSession session = authentication.Session!;

        ProtocolMessage viewerInfoMessage = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
        Assert.Equal(MessageType.Control, viewerInfoMessage.Type);
        Assert.Equal(
            RemoteControlKind.ViewerInfo,
            RemoteMessageCodec.DecodeControl(viewerInfoMessage.PayloadMemory).Kind);

        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeDeviceInfo(new RemoteDeviceDescriptor(
                "windows-cancel-loopback",
                RemoteDevicePlatforms.Windows,
                RemoteDeviceCapabilities.FileSend |
                RemoteDeviceCapabilities.FileChecksum |
                RemoteDeviceCapabilities.FileTransferCancel)),
            session,
            writeLock,
            cancellationToken);

        string transferId = "return-file-transfer-cancel";
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferStart(transferId, fileName, fileBytes.Length),
            session,
            writeLock,
            cancellationToken);
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferChunk(transferId, 0, fileBytes),
            session,
            writeLock,
            cancellationToken);
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferCancel(transferId, "synthetic host cancelled"),
            session,
            writeLock,
            cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            ProtocolMessage statusMessage = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
            if (statusMessage.Type != MessageType.Control)
            {
                continue;
            }

            RemoteControlMessage status = RemoteMessageCodec.DecodeControl(statusMessage.PayloadMemory);
            if (status.Kind == RemoteControlKind.FileTransferStatus &&
                status.Success &&
                status.StatusMessage?.Contains("synthetic host cancelled", StringComparison.Ordinal) == true)
            {
                hostReceivedStatus.TrySetResult(status.StatusMessage);
                return;
            }
        }
    }

    private static async Task RunInvalidControlDuringFileReturningSyntheticHostAsync(
        TcpListener listener,
        string password,
        string fileName,
        byte[] partialBytes,
        Task continueTransfer,
        Task releaseHost,
        CancellationToken cancellationToken)
    {
        using TcpClient hostClient = await listener.AcceptTcpClientAsync(cancellationToken);
        NetworkUtils.ConfigureLowLatencyTcpClient(hostClient, 32 * 1024, 256 * 1024);
        await using NetworkStream stream = hostClient.GetStream();
        using var writeLock = new SemaphoreSlim(1, 1);

        ServerAuthenticationResult authentication = await Protocol.AuthenticateServerDetailedAsync(
            stream,
            password,
            cancellationToken);
        Assert.True(authentication.IsAuthenticated);

        using SecureSession session = authentication.Session!;

        ProtocolMessage viewerInfoMessage = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
        Assert.Equal(MessageType.Control, viewerInfoMessage.Type);
        Assert.Equal(
            RemoteControlKind.ViewerInfo,
            RemoteMessageCodec.DecodeControl(viewerInfoMessage.PayloadMemory).Kind);

        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeDeviceInfo(new RemoteDeviceDescriptor(
                "windows-invalid-control-loopback",
                RemoteDevicePlatforms.Windows,
                RemoteDeviceCapabilities.FileSend)),
            session,
            writeLock,
            cancellationToken);

        string transferId = "return-file-transfer-invalid-control";
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferStart(transferId, fileName, partialBytes.Length + 1),
            session,
            writeLock,
            cancellationToken);
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferChunk(transferId, 0, partialBytes),
            session,
            writeLock,
            cancellationToken);

        byte[] malformedControl = [(byte)RemoteControlKind.ClipboardGetText, 0];
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            malformedControl,
            session,
            writeLock,
            cancellationToken);

        await continueTransfer.WaitAsync(cancellationToken);
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferChunk(
                transferId,
                partialBytes.Length,
                new byte[] { 0x5A }),
            session,
            writeLock,
            cancellationToken);
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferComplete(transferId),
            session,
            writeLock,
            cancellationToken);
        await releaseHost.WaitAsync(cancellationToken);
    }

    private static async Task RunDropPasteReceivingSyntheticHostAsync(
        TcpListener listener,
        string password,
        TaskCompletionSource<LoopbackDropPasteBatch> receivedBatch,
        CancellationToken cancellationToken)
    {
        using TcpClient hostClient = await listener.AcceptTcpClientAsync(cancellationToken);
        NetworkUtils.ConfigureLowLatencyTcpClient(hostClient, 32 * 1024, 256 * 1024);
        await using NetworkStream stream = hostClient.GetStream();
        using var writeLock = new SemaphoreSlim(1, 1);

        ServerAuthenticationResult authentication = await Protocol.AuthenticateServerDetailedAsync(
            stream,
            password,
            cancellationToken);
        Assert.True(authentication.IsAuthenticated);

        using SecureSession session = authentication.Session!;

        ProtocolMessage viewerInfoMessage = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
        Assert.Equal(MessageType.Control, viewerInfoMessage.Type);
        Assert.Equal(
            RemoteControlKind.ViewerInfo,
            RemoteMessageCodec.DecodeControl(viewerInfoMessage.PayloadMemory).Kind);

        string? transferId = null;
        string? fileName = null;
        long fileLength = -1;
        long nextOffset = 0;
        using var output = new MemoryStream();
        var controlKinds = new List<RemoteControlKind>();

        while (!cancellationToken.IsCancellationRequested)
        {
            ProtocolMessage message = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
            Assert.Equal(MessageType.Control, message.Type);
            RemoteControlMessage control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
            controlKinds.Add(control.Kind);

            switch (control.Kind)
            {
                case RemoteControlKind.FileDropPasteBegin:
                    await Protocol.WriteMessageAsync(
                        stream,
                        MessageType.Control,
                        RemoteMessageCodec.EncodeFileTransferStatus(true, "loopback drop paste started"),
                        session,
                        writeLock,
                        cancellationToken);
                    break;
                case RemoteControlKind.FileTransferStart:
                    transferId = control.TransferId;
                    fileName = control.FileName;
                    fileLength = control.FileLength;
                    break;
                case RemoteControlKind.FileTransferChunk:
                    Assert.Equal(transferId, control.TransferId);
                    Assert.Equal(nextOffset, control.FileOffset);
                    Assert.False(control.FileBytes.IsEmpty);
                    output.Write(control.FileBytes.Span);
                    nextOffset += control.FileBytes.Length;
                    break;
                case RemoteControlKind.FileTransferComplete:
                    Assert.Equal(transferId, control.TransferId);
                    Assert.Equal(fileLength, output.Length);
                    await Protocol.WriteMessageAsync(
                        stream,
                        MessageType.Control,
                        RemoteMessageCodec.EncodeFileTransferStatus(true, "loopback host saved drop file"),
                        session,
                        writeLock,
                        cancellationToken);
                    break;
                case RemoteControlKind.FileDropPasteCommit:
                    receivedBatch.TrySetResult(new LoopbackDropPasteBatch(
                        controlKinds.ToArray(),
                        new ReceivedLoopbackFile(
                            fileName ?? string.Empty,
                            fileLength,
                            output.ToArray(),
                            ChunkCount: controlKinds.Count(kind => kind == RemoteControlKind.FileTransferChunk))));
                    await Protocol.WriteMessageAsync(
                        stream,
                        MessageType.Control,
                        RemoteMessageCodec.EncodeFileTransferStatus(true, "loopback drop paste committed"),
                        session,
                        writeLock,
                        cancellationToken);
                    return;
            }
        }
    }

    private static async Task RunWindowsControlReceivingSyntheticHostAsync(
        TcpListener listener,
        string password,
        TaskCompletionSource<LoopbackViewerCommands> receivedCommands,
        CancellationToken cancellationToken)
    {
        using TcpClient hostClient = await listener.AcceptTcpClientAsync(cancellationToken);
        NetworkUtils.ConfigureLowLatencyTcpClient(hostClient, 32 * 1024, 256 * 1024);
        await using NetworkStream stream = hostClient.GetStream();

        ServerAuthenticationResult authentication = await Protocol.AuthenticateServerDetailedAsync(
            stream,
            password,
            cancellationToken);
        Assert.True(authentication.IsAuthenticated);

        using SecureSession session = authentication.Session!;
        RemoteVideoCodecs viewerCodecs = RemoteVideoCodecs.None;
        var inputs = new List<RemoteInputCommand>();
        var controls = new List<RemoteControlMessage>();
        bool controlArrivedBeforeInputFlush = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            ProtocolMessage message = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
            switch (message.Type)
            {
                case MessageType.Input:
                    inputs.Add(RemoteMessageCodec.DecodeInput(message.PayloadSpan));
                    break;
                case MessageType.Control:
                    RemoteControlMessage control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
                    if (control.Kind == RemoteControlKind.ViewerInfo)
                    {
                        viewerCodecs = control.SupportedVideoCodecs;
                    }
                    else if (control.Kind == RemoteControlKind.ViewerCapabilities)
                    {
                        // Connect-time capability negotiation is intentionally ordered before
                        // user input and is not an application control that FlushInput must gate.
                    }
                    else
                    {
                        if (inputs.Count < 5)
                        {
                            controlArrivedBeforeInputFlush = true;
                        }

                        controls.Add(control);
                    }

                    break;
            }

            bool hasMouseMove = inputs.Any(input => input.Kind == RemoteInputKind.MouseMove);
            bool hasMouseDown = inputs.Any(input => input.Kind == RemoteInputKind.MouseDown);
            bool hasText = inputs.Count(input => input.Kind == RemoteInputKind.TextInput) >= 3;
            bool hasCaptureTarget = controls.Any(control => control.Kind == RemoteControlKind.SelectCaptureTarget);
            bool hasClipboardSet = controls.Any(control => control.Kind == RemoteControlKind.ClipboardSetText);
            bool hasClipboardGet = controls.Any(control => control.Kind == RemoteControlKind.ClipboardGetText);
            if (viewerCodecs != RemoteVideoCodecs.None &&
                hasMouseMove &&
                hasMouseDown &&
                hasText &&
                hasCaptureTarget &&
                hasClipboardSet &&
                hasClipboardGet)
            {
                receivedCommands.TrySetResult(new LoopbackViewerCommands(
                    viewerCodecs,
                    inputs.ToArray(),
                    controls.ToArray(),
                    controlArrivedBeforeInputFlush));
                return;
            }
        }
    }

    private static async Task RunReconnectInputReceivingSyntheticHostAsync(
        TcpListener listener,
        string password,
        TaskCompletionSource<bool> firstConnectionReady,
        int expectedCodePoints,
        TaskCompletionSource<string> receivedText,
        CancellationToken cancellationToken)
    {
        using (TcpClient firstClient = await listener.AcceptTcpClientAsync(cancellationToken))
        {
            NetworkUtils.ConfigureLowLatencyTcpClient(firstClient, 32 * 1024, 256 * 1024);
            await using NetworkStream firstStream = firstClient.GetStream();
            ServerAuthenticationResult firstAuthentication =
                await Protocol.AuthenticateServerDetailedAsync(
                    firstStream,
                    password,
                    cancellationToken);
            Assert.True(firstAuthentication.IsAuthenticated);
            using SecureSession firstSession = firstAuthentication.Session!;
            ProtocolMessage viewerInfo =
                await Protocol.ReadMessageAsync(firstStream, firstSession, cancellationToken);
            Assert.Equal(MessageType.Control, viewerInfo.Type);
            firstConnectionReady.TrySetResult(true);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Protocol.ReadMessageAsync(firstStream, firstSession, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or
                SocketException or ObjectDisposedException or CryptographicException)
            {
            }
        }

        using TcpClient secondClient = await listener.AcceptTcpClientAsync(cancellationToken);
        NetworkUtils.ConfigureLowLatencyTcpClient(secondClient, 32 * 1024, 256 * 1024);
        await using NetworkStream secondStream = secondClient.GetStream();
        using var secondWriteLock = new SemaphoreSlim(1, 1);
        ServerAuthenticationResult secondAuthentication =
            await Protocol.AuthenticateServerDetailedAsync(
                secondStream,
                password,
                cancellationToken);
        Assert.True(secondAuthentication.IsAuthenticated);
        using SecureSession secondSession = secondAuthentication.Session!;
        var text = new List<int>(expectedCodePoints);

        while (!cancellationToken.IsCancellationRequested)
        {
            ProtocolMessage message =
                await Protocol.ReadMessageAsync(secondStream, secondSession, cancellationToken);
            if (message.Type != MessageType.Input)
            {
                continue;
            }

            RemoteInputCommand input = RemoteMessageCodec.DecodeInput(message.PayloadSpan);
            if (input.Kind != RemoteInputKind.TextInput)
            {
                continue;
            }

            text.Add(input.Data);
            if (text.Count >= expectedCodePoints)
            {
                receivedText.TrySetResult(string.Concat(text.Select(char.ConvertFromUtf32)));
                await DrainSyntheticHostUntilDisconnectedAsync(
                    secondStream,
                    secondSession,
                    secondWriteLock,
                    cancellationToken);
                return;
            }
        }
    }

    private static async Task RunTextInputBurstReceivingSyntheticHostAsync(
        TcpListener listener,
        string password,
        int expectedCodePoints,
        TaskCompletionSource<string> receivedText,
        CancellationToken cancellationToken)
    {
        using TcpClient hostClient = await listener.AcceptTcpClientAsync(cancellationToken);
        NetworkUtils.ConfigureLowLatencyTcpClient(hostClient, 32 * 1024, 256 * 1024);
        await using NetworkStream stream = hostClient.GetStream();

        ServerAuthenticationResult authentication = await Protocol.AuthenticateServerDetailedAsync(
            stream,
            password,
            cancellationToken);
        Assert.True(authentication.IsAuthenticated);

        using SecureSession session = authentication.Session!;
        var text = new List<int>(expectedCodePoints);

        while (!cancellationToken.IsCancellationRequested)
        {
            ProtocolMessage message = await Protocol.ReadMessageAsync(stream, session, cancellationToken);
            if (message.Type != MessageType.Input)
            {
                continue;
            }

            RemoteInputCommand input = RemoteMessageCodec.DecodeInput(message.PayloadSpan);
            if (input.Kind != RemoteInputKind.TextInput)
            {
                continue;
            }

            text.Add(input.Data);
            if (text.Count >= expectedCodePoints)
            {
                receivedText.TrySetResult(string.Concat(text.Select(char.ConvertFromUtf32)));
                return;
            }
        }
    }

    private static TaskCompletionSource<T> CreateCompletionSource<T>()
    {
        return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static void AssertSubsequence(
        IReadOnlyList<RemoteControlKind> expected,
        IReadOnlyList<RemoteControlKind> actual)
    {
        int searchIndex = 0;
        foreach (RemoteControlKind expectedKind in expected)
        {
            int foundIndex = -1;
            for (int index = searchIndex; index < actual.Count; index++)
            {
                if (actual[index] == expectedKind)
                {
                    foundIndex = index;
                    break;
                }
            }

            Assert.True(foundIndex >= 0, $"Missing {expectedKind} after index {searchIndex} in [{string.Join(", ", actual)}].");
            searchIndex = foundIndex + 1;
        }
    }

    private sealed record ReceivedLoopbackFile(
        string FileName,
        long FileLength,
        byte[] Bytes,
        int ChunkCount);

    private sealed record LoopbackDropPasteBatch(
        IReadOnlyList<RemoteControlKind> ControlKinds,
        ReceivedLoopbackFile File);

    private sealed record LoopbackViewerCommands(
        RemoteVideoCodecs ViewerCodecs,
        IReadOnlyList<RemoteInputCommand> Inputs,
        IReadOnlyList<RemoteControlMessage> Controls,
        bool ControlArrivedBeforeInputFlush);
}
