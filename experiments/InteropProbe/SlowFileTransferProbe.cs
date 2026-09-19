using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using RemoteDesk;

// An owned loopback peer, random credentials and generated files only. A large
// receive window allows normal client writes to queue behind slow file reads,
// reproducing relay/TCP head-of-line delay without accessing relay settings,
// the interactive desktop, a user's clipboard, or a production host.
internal static class SlowFileTransferProbe
{
    internal static async Task<int> RunAsync(string output, bool legacyPeer = false)
    {
        const int fileLength = 1024 * 1024;
        const int chunkDelayMilliseconds = 1000;
        const int receiveBufferBytes = 2 * 1024 * 1024;
        output = Path.GetFullPath(output);
        string reportPath = Path.Combine(output, "slow-file-transfer.json");
        string fixture = Path.Combine(output, "slow-file-fixture");
        if (File.Exists(reportPath) || Directory.Exists(fixture))
            throw new InvalidOperationException("Use a new output directory; existing evidence is not overwritten.");
        Directory.CreateDirectory(fixture);
        string received = Path.Combine(fixture, "received");
        string unusedViewerReceive = Path.Combine(fixture, "unused-viewer-receive");
        Directory.CreateDirectory(received);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.ReceiveBufferSize = receiveBufferBytes;
        listener.Start();
        using var viewer = new RemoteViewerClient(() => unusedViewerReceive, _ => Task.CompletedTask);
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        byte[] sourceBytes = RandomNumberGenerator.GetBytes(fileLength);
        string sourcePath = Path.Combine(fixture, "generated-1MiB.bin");
        await File.WriteAllBytesAsync(sourcePath, sourceBytes, deadline.Token);
        string sourceSha256 = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
        var elapsed = Stopwatch.StartNew();
        var events = new ConcurrentQueue<string>();
        var chunks = new ConcurrentQueue<object>();
        var firstPongWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerSaveCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Record(string text)
        {
            events.Enqueue($"{elapsed.Elapsed.TotalSeconds:F3}s {text}");
            while (events.Count > 128) events.TryDequeue(out _);
        }
        viewer.Log += Record;
        viewer.FileTransferStatusReceived += (ok, text) => Record($"fileStatus({ok}): {text}");
        viewer.ConnectedChanged += connected => Record($"connected: {connected}");

        long peerBytes = 0;
        int peerPings = 0, peerChunks = 0, actualReceiveBuffer = 0;
        string peerStage = "accept", receivedSha256 = "", savedPath = "";
        string? failure = null, peerFailure = null;
        bool checksumReceived = false, receiptWritten = false, uploadReturned = false;
        bool passed = false, connectedAfterTransfer = false;
        double? transferStartedAt = null, transferCompletedAt = null, firstPingAt = null, uploadReturnedAt = null;
        Task peerTask = Task.Run(async () =>
        {
            try
            {
                using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
                peer.NoDelay = true;
                peer.ReceiveBufferSize = receiveBufferBytes;
                actualReceiveBuffer = peer.ReceiveBufferSize;
                await using var stream = peer.GetStream();
                using var writeLock = new SemaphoreSlim(1, 1);
                peerStage = "authenticate";
                var auth = await Protocol.AuthenticateServerDetailedAsync(stream, password, deadline.Token);
                if (!auth.IsAuthenticated) throw new InvalidOperationException("Owned fixture authentication failed.");
                using var session = auth.Session!;
                using var receiver = new FileTransferReceiver(Record, () => received,
                    setFileDropListAsync: _ => throw new InvalidOperationException("Fixture must not access a clipboard."),
                    sendPasteShortcut: () => throw new InvalidOperationException("Fixture must not inject paste input."))
                { RequireChecksum = true };
                Task Write(byte[] payload) => Protocol.WriteMessageAsync(stream, MessageType.Control,
                    payload, session, writeLock, deadline.Token);
                await Write(RemoteMessageCodec.EncodeDeviceInfo(new RemoteDeviceDescriptor(
                    "RemoteDesk slow file fixture", "Windows",
                    RemoteDeviceCapabilities.FileReceive | RemoteDeviceCapabilities.FileChecksum |
                    RemoteDeviceCapabilities.FileTransferCancel |
                    (legacyPeer ? RemoteDeviceCapabilities.None : RemoteDeviceCapabilities.FileTransferReceipt))));
                peerStage = "read";
                while (!deadline.IsCancellationRequested)
                {
                    var message = await Protocol.ReadMessageAsync(stream, session, deadline.Token);
                    if (message.Type == MessageType.Ping)
                    {
                        int count = Interlocked.Increment(ref peerPings);
                        if (count == 1) firstPingAt = elapsed.Elapsed.TotalSeconds;
                        // A slow consumer is not a dead peer: every Ping is
                        // answered as soon as TCP delivers it to this reader.
                        await Protocol.WriteMessageAsync(stream, MessageType.Pong, message.PayloadMemory,
                            session, writeLock, deadline.Token);
                        Record($"peer replied to Ping #{count}");
                        firstPongWritten.TrySetResult();
                        continue;
                    }
                    if (message.Type != MessageType.Control) continue;
                    var control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
                    peerStage = control.Kind.ToString();
                    switch (control.Kind)
                    {
                        case RemoteControlKind.FileTransferStart:
                            if (control.FileLength != fileLength)
                                throw new InvalidDataException("Unexpected fixture file length.");
                            receiver.Start(control);
                            Record("peer accepted file start");
                            break;
                        case RemoteControlKind.FileTransferChunk:
                            if (control.FileBytes.Length != 32768)
                                throw new InvalidDataException("Fixture requires production 32 KiB chunks.");
                            await receiver.WriteChunkAsync(control, deadline.Token);
                            long bytes = Interlocked.Add(ref peerBytes, control.FileBytes.Length);
                            int count = Interlocked.Increment(ref peerChunks);
                            chunks.Enqueue(new { index = count, receivedBytes = bytes, seconds = elapsed.Elapsed.TotalSeconds });
                            // Delay consumption, not control semantics or ACKs.
                            // Ping remains queued behind earlier file messages.
                            await Task.Delay(chunkDelayMilliseconds, deadline.Token);
                            break;
                        case RemoteControlKind.FileTransferChecksum:
                            receiver.SetExpectedChecksum(control);
                            checksumReceived = true;
                            break;
                        case RemoteControlKind.FileTransferComplete:
                            string result = await receiver.CompleteAsync(control, deadline.Token);
                            savedPath = receiver.LastCompletedFilePath ?? throw new IOException("No saved-file receipt path.");
                            await using (var saved = File.OpenRead(savedPath))
                                receivedSha256 = Convert.ToHexString(await SHA256.HashDataAsync(saved, deadline.Token)).ToLowerInvariant();
                            if (!legacyPeer)
                            {
                                await Write(RemoteMessageCodec.EncodeFileTransferReceipt(control.TransferId!, true, result));
                                receiptWritten = true;
                            }
                            Record(legacyPeer ? "legacy peer saved file (no receipt capability)" : "peer wrote checksum-verified save receipt");
                            peerSaveCompleted.TrySetResult();
                            break;
                        case RemoteControlKind.FileTransferCancel:
                            receiver.Cancel(control);
                            Record("peer received transfer cancellation");
                            break;
                    }
                }
            }
            catch (Exception error) when (deadline.IsCancellationRequested &&
                error is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            { }
            catch (Exception error)
            {
                peerFailure = error.GetType().Name + ": " + error.Message;
                Record("peer failed: " + peerFailure);
                throw;
            }
        }, deadline.Token);
        try
        {
            await viewer.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
                password, ViewerVideoMode.StableJpeg, deadline.Token);
            if (!await viewer.WaitForCurrentDeviceInfoAsync(TimeSpan.FromSeconds(10), deadline.Token))
                throw new TimeoutException("Owned fixture device information did not arrive.");
            transferStartedAt = elapsed.Elapsed.TotalSeconds;
            await viewer.SendFileToRemoteAsync(sourcePath).WaitAsync(deadline.Token);
            uploadReturnedAt = elapsed.Elapsed.TotalSeconds;
            uploadReturned = true;
            // A legacy sender returning only means local writes completed, not
            // that the relay queue has delivered the final chunk to its peer.
            await peerSaveCompleted.Task.WaitAsync(deadline.Token);
            transferCompletedAt = elapsed.Elapsed.TotalSeconds;
            // Confirm the reader drains the queued Ping and really answers it.
            await firstPongWritten.Task.WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
            connectedAfterTransfer = viewer.IsConnected;
            passed = checksumReceived && receiptWritten == !legacyPeer && receivedSha256 == sourceSha256 &&
                Interlocked.Read(ref peerBytes) == fileLength && Volatile.Read(ref peerChunks) == 32 &&
                Volatile.Read(ref peerPings) > 0 && connectedAfterTransfer &&
                transferCompletedAt - transferStartedAt >= 30;
            if (!passed) throw new InvalidOperationException("Slow transfer did not satisfy all checksum, receipt and liveness checks.");
        }
        catch (Exception error)
        {
            failure = error.GetType().Name + ": " + error.Message;
            Record("probe failed: " + failure);
        }
        finally
        {
            deadline.Cancel();
            listener.Stop();
            try { await viewer.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) { Record("cleanup: " + error.GetType().Name); }
            try { await peerTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) when (error is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
            catch (Exception error) { peerFailure ??= error.GetType().Name + ": " + error.Message; }
        }
        Program.Save(reportPath, new
        {
            passed,
            scope = "Owned loopback TCP fixture only; production client/protocol/FileTransferReceiver. No user settings, relay credentials, clipboard or desktop accessed; no video quality changes.",
            deadlineSeconds = 90,
            legacyPeer,
            fileLength,
            chunkBytes = 32768,
            chunkDelayMilliseconds,
            requestedReceiveBufferBytes = receiveBufferBytes,
            actualReceiveBufferBytes = actualReceiveBuffer,
            receivedBytes = Interlocked.Read(ref peerBytes),
            receivedChunks = Volatile.Read(ref peerChunks),
            peerPings = Volatile.Read(ref peerPings),
            firstPingAtSeconds = firstPingAt,
            durationSeconds = elapsed.Elapsed.TotalSeconds,
            uploadReturnedAtSeconds = uploadReturnedAt,
            transferDurationSeconds = transferStartedAt.HasValue
                ? (transferCompletedAt ?? elapsed.Elapsed.TotalSeconds) - transferStartedAt.Value : (double?)null,
            uploadReturned, checksumReceived, receiptWritten, connectedAfterTransfer,
            sourceSha256, receivedSha256, savedPath, peerStage,
            error = failure, peerFailure,
            chunks = chunks.ToArray(),
            events = events.ToArray()
        });
        return passed ? 0 : 1;
    }
}
