using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using RemoteDesk;

// Owned synthetic peer + real FileTransferReceiver + real Windows clipboard on
// a private window station. The public relay sees only encrypted test traffic.
internal static class FileClipboardRelayProbe
{
    internal static async Task<int> RunAsync(string output, string expectedServer)
    {
        RelaySettings saved = new AppSettingsService().Load().Relay;
        if (string.IsNullOrWhiteSpace(expectedServer) || saved.ServerAddress != expectedServer)
            throw new InvalidOperationException("Unexpected relay; no connection attempted");
        var options = new RelayConnectionOptions(saved.ServerAddress, saved.RelayPort,
            AppSettingsService.UnprotectSecret(saved.ProtectedAccessToken), saved.TlsCertificateSha256!, Guid.NewGuid().ToString("D")).Validate();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var checks = new List<object>();
        void Check(string name, bool pass) { checks.Add(new { name, passed = pass }); if (!pass) throw new InvalidOperationException(name); }
        string source = Path.Combine(output, "source"), received = Path.Combine(output, "remote"), returned = Path.Combine(output, "returned");
        Directory.CreateDirectory(source); Directory.CreateDirectory(received); Directory.CreateDirectory(returned);
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        byte[] bytes = RandomNumberGenerator.GetBytes(1024 * 1024 + 31);
        string binary = Path.Combine(source, "中文😀.bin");
        await File.WriteAllBytesAsync(binary, bytes, timeout.Token);
        string empty = Path.Combine(source, "empty.txt"); await File.WriteAllBytesAsync(empty, [], timeout.Token);
        string rejected = Path.Combine(source, "reject.bin"); await File.WriteAllBytesAsync(rejected, [1], timeout.Token);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var connector = new RelayHostConnector();
        using var viewer = new RemoteViewerClient(() => returned, _ => Task.CompletedTask);
        string remoteClipboard = "";
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? host = null;
        try
        {
            await connector.StartAsync(options, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
            for (int i = 0; ; i++) {
                if ((await RelayTunnelClient.ListDevicesAsync(options, timeout.Token)).Any(d => d.DeviceId == options.DeviceId)) break;
                if (i > 30) throw new TimeoutException("temporary relay node registration");
                await Task.Delay(200, timeout.Token);
            }
            host = Task.Run(async () => {
                using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = peer.GetStream(); using var writeLock = new SemaphoreSlim(1, 1);
                var auth = await Protocol.AuthenticateServerDetailedAsync(stream, password, timeout.Token);
                if (!auth.IsAuthenticated) throw new InvalidOperationException("fixture authentication");
                using var session = auth.Session!;
                using var receiver = new FileTransferReceiver(_ => { }, () => received) { RequireChecksum = true };
                Task Write(byte[] payload) => Protocol.WriteMessageAsync(stream, MessageType.Control, payload, session, writeLock, timeout.Token);
                await Write(RemoteMessageCodec.EncodeDeviceInfo(new RemoteDeviceDescriptor("RemoteDesk transfer fixture", "Windows",
                    RemoteDeviceCapabilities.ClipboardText | RemoteDeviceCapabilities.FileReceive | RemoteDeviceCapabilities.FileSend |
                    RemoteDeviceCapabilities.FileChecksum | RemoteDeviceCapabilities.FileTransferCancel |
                    RemoteDeviceCapabilities.FileTransferPreview | RemoteDeviceCapabilities.FileTransferReceipt)));
                try {
                    while (!shutdown.Task.IsCompleted) {
                        var message = await Protocol.ReadMessageAsync(stream, session, timeout.Token);
                        if (message.Type == MessageType.Ping) { await Protocol.WriteMessageAsync(stream, MessageType.Pong, ReadOnlyMemory<byte>.Empty, session, writeLock, timeout.Token); continue; }
                        if (message.Type != MessageType.Control) continue;
                        var control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
                        switch (control.Kind) {
                            case RemoteControlKind.ClipboardSetText:
                                remoteClipboard = control.Text!;
                                await Write(RemoteMessageCodec.EncodeClipboardStatus(true, "Updated fixture clipboard")); break;
                            case RemoteControlKind.ClipboardGetText:
                                await Write(RemoteMessageCodec.EncodeClipboardText(remoteClipboard)); break;
                            case RemoteControlKind.FileTransferStart:
                                if (control.FileName == "reject.bin") await Write(RemoteMessageCodec.EncodeFileTransferReceipt(control.TransferId!, false, "fixture disk full"));
                                else receiver.Start(control);
                                break;
                            case RemoteControlKind.FileTransferChunk:
                                if (receiver.HasActiveTransfer) await receiver.WriteChunkAsync(control, timeout.Token); break;
                            case RemoteControlKind.FileTransferChecksum:
                                if (receiver.HasActiveTransfer) receiver.SetExpectedChecksum(control); break;
                            case RemoteControlKind.FileTransferComplete:
                                if (receiver.HasActiveTransfer) {
                                    string result = await receiver.CompleteAsync(control, timeout.Token);
                                    await Write(RemoteMessageCodec.EncodeFileTransferReceipt(control.TransferId!, true, result));
                                }
                                break;
                            case RemoteControlKind.FileTransferCancel:
                                if (receiver.HasActiveTransfer) receiver.Cancel(control); break;
                            case RemoteControlKind.FileTransferRequestClipboardFiles:
                                await Write(RemoteMessageCodec.EncodeFileTransferClipboardFilesPreview(
                                    [new("文件", binary, "return.bin", bytes.Length, "isolated receiver")], "owned fixture")); break;
                            case RemoteControlKind.FileTransferConfirmClipboardFiles:
                                string id = Guid.NewGuid().ToString("N");
                                await Write(RemoteMessageCodec.EncodeFileTransferStart(id, "return.bin", bytes.Length));
                                for (int at = 0; at < bytes.Length; at += 32768)
                                    await Write(RemoteMessageCodec.EncodeFileTransferChunk(id, at, bytes.AsMemory(at, Math.Min(32768, bytes.Length - at)).ToArray()));
                                await Write(RemoteMessageCodec.EncodeFileTransferChecksum(id, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
                                await Write(RemoteMessageCodec.EncodeFileTransferComplete(id));
                                await Write(RemoteMessageCodec.EncodeFileTransferStatus(true, "远端文件回传完成：1 个")); break;
                        }
                    }
                } catch (Exception error) when (shutdown.Task.IsCompleted && error is IOException or SocketException or OperationCanceledException) { }
            }, timeout.Token);
            await viewer.ConnectViaRelayAsync(options, password, ViewerVideoMode.StableJpeg, timeout.Token);
            Check("pinned public relay authenticated device handshake", await viewer.WaitForCurrentDeviceInfoAsync(TimeSpan.FromSeconds(10), timeout.Token));
            const string clipboard = "公网剪贴板 中文😀\r\n第二行\t缩进";
            Check("public relay clipboard send ACK", await viewer.SendClipboardTextToRemoteAsync(clipboard));
            Check("remote fixture exact clipboard text", remoteClipboard == clipboard);
            await ClipboardTextService.SetTextAsync("private station sentinel");
            var clipboardDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            viewer.ClipboardStatusReceived += _ => clipboardDone.TrySetResult();
            await viewer.ReadRemoteClipboardAsync(notifyRequest: false);
            await clipboardDone.Task.WaitAsync(timeout.Token);
            Check("public relay text return to real isolated Windows clipboard", await ClipboardTextService.GetTextAsync() == clipboard);
            await viewer.SendFileToRemoteAsync(binary);
            Check("public relay 1 MiB upload saved and SHA256 verified", File.ReadAllBytes(Directory.GetFiles(received).Single()).SequenceEqual(bytes));
            await viewer.SendFileToRemoteAsync(binary);
            Check("same-name upload preserves both files", Directory.GetFiles(received).Length == 2 && Directory.GetFiles(received).All(p => File.ReadAllBytes(p).SequenceEqual(bytes)));
            await viewer.SendFileToRemoteAsync(empty);
            Check("empty file saved before completion", File.Exists(Path.Combine(received, "empty.txt")) && new FileInfo(Path.Combine(received, "empty.txt")).Length == 0);
            bool failed = false;
            try { await viewer.SendFileToRemoteAsync(rejected); } catch (IOException error) { failed = error.Message.Contains("disk full"); }
            Check("negative save receipt fails upload without false success", failed && !File.Exists(Path.Combine(received, "reject.bin")));
            viewer.ConfirmRemoteClipboardFileTransfer = (items, _) => items.Count == 1 && items[0].TransferName == "return.bin" && items[0].SizeBytes == bytes.Length;
            var download = await viewer.RequestRemoteClipboardFilesForDragOutAsync(timeout.Token);
            Check("confirmed reverse file transfer over public relay", download.Success && download.LocalPaths.Count == 1 && File.ReadAllBytes(download.LocalPaths[0]).SequenceEqual(bytes));
            Program.Save(Path.Combine(output, "public-relay.json"), new { passed = true, server = options.ServerAddress, checks,
                scope = "Real pinned public relay; synthetic authenticated peer and production file receiver; private Windows clipboard. No production desktop or files touched." });
            return 0;
        }
        catch (Exception error) {
            Program.Save(Path.Combine(output, "public-relay.json"), new { passed = false, checks, error = error.GetType().Name + ": " + error.Message });
            return 1;
        }
        finally {
            shutdown.TrySetResult();
            await viewer.DisconnectAsync(); await connector.StopAsync(); listener.Stop(); timeout.Cancel();
            if (host is not null) { try { await host.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { } }
        }
    }
}
