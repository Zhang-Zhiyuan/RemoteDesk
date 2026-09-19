using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using RemoteDesk;

// Real local WinForms/clipboard on a private station; encrypted synthetic peers
// deliberately model menu actions without injecting input into anyone's desktop.
internal static class ClipboardContextMenuProbe
{
    internal static async Task<int> RunCasesAsync(string output)
    {
        var checks = new List<object>();
        int failures = 0;
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed });
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {name}");
            if (!passed) failures++;
        }
        foreach (string platform in new[] { "Windows", "Linux" })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            string remoteText = "远端连接前旧内容";
            string? remoteMenuAction = null;
            string? pasted = null;
            int writes = 0, fullReplies = 0;
            bool acknowledged = false;
            var commands = new ConcurrentQueue<(RemoteInputCommand Input, bool Ack)>();
            TaskCompletionSource<bool>? snapshotSeen = null, releaseSnapshot = null;
            Task host = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = peer.GetStream();
                using var writeLock = new SemaphoreSlim(1, 1);
                var auth = await Protocol.AuthenticateServerDetailedAsync(stream, "context-fixture", timeout.Token);
                using var session = auth.Session!;
                RemoteDeviceCapabilities viewerCapabilities = RemoteDeviceCapabilities.None;
                await Protocol.WriteMessageAsync(stream, MessageType.Control,
                    RemoteMessageCodec.EncodeDeviceInfo(new RemoteDeviceDescriptor("menu-fixture", platform,
                        RemoteDeviceCapabilities.InputControl | RemoteDeviceCapabilities.ClipboardText |
                        RemoteDeviceCapabilities.ClipboardSnapshotV1)), session, writeLock, timeout.Token);
                try
                {
                    while (true)
                    {
                        var message = await Protocol.ReadMessageAsync(stream, session, timeout.Token);
                        if (message.Type == MessageType.Input)
                        {
                            var input = RemoteMessageCodec.DecodeInput(message.PayloadSpan);
                            commands.Enqueue((input, Volatile.Read(ref acknowledged)));
                            if (input is { Kind: RemoteInputKind.MouseUp, Button: RemoteMouseButton.Left })
                            {
                                if (Volatile.Read(ref remoteMenuAction) == "paste")
                                    Volatile.Write(ref pasted, Volatile.Read(ref remoteText));
                                if (Volatile.Read(ref remoteMenuAction) == "copy")
                                    Volatile.Write(ref remoteText, "远端右键复制中文😀\r\n不是 Ctrl+C");
                            }
                            continue;
                        }
                        if (message.Type != MessageType.Control) continue;
                        var control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
                        if (control.Kind == RemoteControlKind.ViewerCapabilities)
                            viewerCapabilities = control.Capabilities;
                        if (control.Kind == RemoteControlKind.ClipboardSetText)
                        {
                            Interlocked.Increment(ref writes);
                            await Task.Delay(350, timeout.Token);
                            Volatile.Write(ref remoteText, control.Text!);
                            Volatile.Write(ref acknowledged, true);
                            await Protocol.WriteMessageAsync(stream, MessageType.Control,
                                RemoteMessageCodec.EncodeClipboardStatus(true, "fixture applied"), session, writeLock, timeout.Token);
                        }
                        if (control.Kind == RemoteControlKind.ClipboardSnapshotRequest)
                        {
                            const RemoteDeviceCapabilities required = RemoteDeviceCapabilities.ClipboardText |
                                RemoteDeviceCapabilities.ClipboardSnapshotV1;
                            if ((viewerCapabilities & required) != required)
                                throw new InvalidOperationException("Snapshot without negotiated clipboard capability");
                            string text = Volatile.Read(ref remoteText);
                            string revision = ClipboardAutoSyncCoordinator.Revision(text);
                            bool changed = revision != control.ClipboardRevision;
                            if (changed && text.Length > 0) Interlocked.Increment(ref fullReplies);
                            if (Volatile.Read(ref snapshotSeen) is { } seen)
                            {
                                seen.TrySetResult(true);
                                await releaseSnapshot!.Task.WaitAsync(timeout.Token);
                            }
                            // Unmatched snapshots must never touch the clipboard or satisfy this request.
                            string unsolicited = "错误请求的旧内容";
                            await Protocol.WriteMessageAsync(stream, MessageType.Control,
                                RemoteMessageCodec.EncodeClipboardSnapshot("unmatched", true,
                                    ClipboardAutoSyncCoordinator.Revision(unsolicited), true, true, unsolicited, ""),
                                session, writeLock, timeout.Token);
                            await Protocol.WriteMessageAsync(stream, MessageType.Control,
                                RemoteMessageCodec.EncodeClipboardSnapshot(control.TransferId!, true, revision,
                                    text.Length > 0, changed, changed ? text : "", ""), session, writeLock, timeout.Token);
                        }
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            });
            await ClipboardTextService.SetTextAsync("本机连接前旧内容");
            using var client = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
            await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "context-fixture", ViewerVideoMode.StableJpeg);
            using var viewer = new RemoteViewerWindow(client, "Context menu " + platform, true, true, false, false, false, false)
                { ShowInTaskbar = false, ClipboardForegroundForEntityTests = true };
            viewer.SetRemotePlatform(platform);
            viewer.Show();
            Invoke(viewer, "UninstallSystemKeyboardCapture");
            var picture = viewer.Controls.OfType<PictureBox>().Single();
            picture.Focus();
            Field(viewer, "_remoteImageSizePacked").SetValue(viewer, ((long)640 << 32) | 480L);
            var timer = (System.Windows.Forms.Timer)Field(viewer, "_clipboardAutoTimer").GetValue(viewer)!;
            timer.Stop();
            Check(platform + " handshake snapshot negotiated", client.SupportsClipboardSnapshots);
            await viewer.SyncClipboardForEntityTestsAsync();
            Check(platform + " initial baseline/unmatched reply cannot replace local text",
                await ClipboardTextService.GetTextAsync() == "本机连接前旧内容" && Volatile.Read(ref writes) == 0);

            using var source = new TextBox { Text = "本机右键复制中文😀\r\n第二行", Multiline = true };
            using var destination = new TextBox { Multiline = true };
            _ = source.Handle;
            _ = destination.Handle;
            using var localMenu = new ContextMenuStrip();
            var copy = localMenu.Items.Add("复制", null, (_, _) => source.Copy());
            var paste = localMenu.Items.Add("粘贴", null, (_, _) => destination.Paste());
            source.SelectAll();
            copy.PerformClick();
            Volatile.Write(ref remoteMenuAction, "paste");
            void Click(MouseButtons button)
            {
                var args = new MouseEventArgs(button, 1, picture.Width / 2, picture.Height / 2, 0);
                Invoke(viewer, "PictureBox_MouseDown", picture, args);
                Invoke(viewer, "PictureBox_MouseUp", picture, args);
            }
            Click(MouseButtons.Right);
            Click(MouseButtons.Left); // Deliberately arrive before the 350 ms clipboard ACK.
            await WaitUntilAsync(() => Volatile.Read(ref pasted) != null, timeout.Token);
            var firstCommands = commands.ToArray();
            Check(platform + " right-click paste receives current Chinese text", Volatile.Read(ref pasted) == source.Text);
            Check(platform + " delayed ACK preserves down/up/menu-click FIFO",
                firstCommands.Select(x => (x.Input.Kind, x.Input.Button)).SequenceEqual(new[] {
                    (RemoteInputKind.MouseDown, RemoteMouseButton.Right), (RemoteInputKind.MouseUp, RemoteMouseButton.Right),
                    (RemoteInputKind.MouseDown, RemoteMouseButton.Left), (RemoteInputKind.MouseUp, RemoteMouseButton.Left) }) &&
                firstCommands.All(x => x.Ack));

            Volatile.Write(ref remoteMenuAction, "copy");
            Click(MouseButtons.Right);
            Click(MouseButtons.Left);
            await WaitUntilAsync(() => Volatile.Read(ref remoteText).StartsWith("远端右键"), timeout.Token);
            timer.Start(); // Exercise the production automatic timer, not just a manually triggered read.
            await WaitUntilAsync(() => Clipboard.ContainsText() && Clipboard.GetText().StartsWith("远端右键"), timeout.Token);
            timer.Stop();
            paste.PerformClick();
            Check(platform + " remote right-click copy then local menu paste", destination.Text == Volatile.Read(ref remoteText));
            Check(platform + " no Ctrl+C/V keys were synthesized", commands.All(x => x.Input.Kind is not
                (RemoteInputKind.KeyDown or RemoteInputKind.KeyUp or RemoteInputKind.TextInput)));
            int previousWrites = Volatile.Read(ref writes), previousFull = Volatile.Read(ref fullReplies);
            for (int i = 0; i < 3; i++) await viewer.SyncClipboardForEntityTestsAsync();
            Check(platform + " unchanged polls are metadata-only without clipboard echo",
                Volatile.Read(ref writes) == previousWrites && Volatile.Read(ref fullReplies) == previousFull);

            Volatile.Write(ref remoteText, "延迟回来的远端旧文本");
            snapshotSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
            releaseSnapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool> delayed = viewer.SyncClipboardForEntityTestsAsync();
            await snapshotSeen.Task.WaitAsync(timeout.Token);
            source.Text = "等待期间本机新复制，绝不能被旧回复覆盖";
            source.SelectAll();
            copy.PerformClick();
            releaseSnapshot.TrySetResult(true);
            await delayed;
            snapshotSeen = null;
            Check(platform + " late snapshot cannot overwrite a new local copy", Clipboard.GetText() == source.Text);
            await viewer.SyncClipboardForEntityTestsAsync();
            Check(platform + " local copy made in flight is retried", Volatile.Read(ref remoteText) == source.Text);

            using var image = new Bitmap(3, 3);
            Clipboard.SetImage(image);
            previousWrites = Volatile.Read(ref writes);
            await viewer.SyncClipboardForEntityTestsAsync();
            Check(platform + " image clipboard neither uploads text nor restores stale text",
                Clipboard.ContainsImage() && Volatile.Read(ref writes) == previousWrites);
            Volatile.Write(ref remoteText, "");
            await viewer.SyncClipboardForEntityTestsAsync();
            Check(platform + " empty remote clipboard never clears local image", Clipboard.ContainsImage());
            await ClipboardTextService.SetTextAsync(new string('大', 256001));
            Volatile.Write(ref remoteMenuAction, "paste");
            int previousCommands = commands.Count;
            Click(MouseButtons.Right);
            await ((Task)Field(viewer, "_clipboardPointerTask").GetValue(viewer)!).WaitAsync(TimeSpan.FromSeconds(5));
            await client.FlushInputAsync(timeout.Token);
            await Task.Delay(150, timeout.Token);
            Check(platform + " oversized local text does not disable native right-click menus",
                Volatile.Read(ref writes) == previousWrites && commands.Count >= previousCommands + 2);
            if (commands.Count < previousCommands + 2)
                Console.WriteLine($"pointer debug: count={commands.Count - previousCommands}, connected={client.IsConnected}, pending={Field(viewer, "_clipboardPointerPending").GetValue(viewer)}, task={((Task)Field(viewer, "_clipboardPointerTask").GetValue(viewer)!).Status}");
            await ClipboardTextService.SetTextAsync("焦点离开前复制的测试文字");
            previousCommands = commands.Count;
            Click(MouseButtons.Right);
            Invoke(viewer, "ReleaseAllRemoteInputs");
            await ((Task)Field(viewer, "_clipboardPointerTask").GetValue(viewer)!).WaitAsync(TimeSpan.FromSeconds(5));
            await client.FlushInputAsync(timeout.Token);
            await Task.Delay(150, timeout.Token);
            Check(platform + " focus release cancels delayed context-menu mouse input", commands.Count == previousCommands);
            previousWrites = Volatile.Read(ref writes);
            Check(platform + " stale connection generation cannot push clipboard text",
                !await client.SendClipboardTextToRemoteAsync("过期连接文字", expectedGeneration: client.InputConnectionGeneration - 1) &&
                Volatile.Read(ref writes) == previousWrites);
            Clipboard.SetImage(image);
            viewer.SetClipboardTextEnabled(false);
            Volatile.Write(ref remoteText, "禁用后远端文字");
            Check(platform + " disabled clipboard sync leaves local data untouched",
                !await viewer.SyncClipboardForEntityTestsAsync() && Clipboard.ContainsImage());
            if (platform == "Windows")
            {
                var writeLock = (SemaphoreSlim)Field(client, "_writeLock").GetValue(client)!;
                var sendGate = (ClipboardSnapshotSendGate)Field(client, "_clipboardSnapshotSendGate").GetValue(client)!;
                await writeLock.WaitAsync(timeout.Token);
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                int uiTicks = 0;
                using var uiTimer = new System.Windows.Forms.Timer { Interval = 100 };
                uiTimer.Tick += (_, _) => uiTicks++;
                uiTimer.Start();
                try
                {
                    var stalled = await client.GetClipboardSnapshotAsync("", client.InputConnectionGeneration, timeout.Token);
                    Check("snapshot total deadline includes a blocked TCP write lock", stalled is null &&
                        elapsed.Elapsed.TotalSeconds is >= 7.5 and < 11 && client.IsConnected && sendGate.HasPendingSend);
                    var retryClock = System.Diagnostics.Stopwatch.StartNew();
                    for (int i = 0; i < 3; i++)
                        Check("blocked snapshot skips additional queued send " + i,
                            await client.GetClipboardSnapshotAsync("", client.InputConnectionGeneration, timeout.Token) is null);
                    Check("blocked snapshot keeps UI responsive and retries bounded", uiTicks >= 20 &&
                        retryClock.Elapsed < TimeSpan.FromSeconds(1));
                }
                finally { writeLock.Release(); uiTimer.Stop(); }
                await WaitUntilAsync(() => !sendGate.HasPendingSend, timeout.Token);
                var resumed = await client.GetClipboardSnapshotAsync("", client.InputConnectionGeneration, timeout.Token);
                Check("late read-only send drains and snapshots recover without disconnect",
                    resumed is { Success: true } && client.IsConnected && Clipboard.ContainsImage());
            }
            viewer.Close();
            await client.DisconnectAsync();
            timeout.Cancel();
            await host;
        }
        Program.Save(Path.Combine(output, "clipboard-context-menus.json"), new { complete = true, failures,
            scope = "Private Windows clipboard, native TextBox menu handlers, real viewer mouse routing; encrypted synthetic Windows/Linux peers; no user clipboard/input access", checks });
        return failures == 0 ? 0 : 1;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken token)
    {
        while (!predicate()) await Task.Delay(25, token);
    }
    private static FieldInfo Field(object instance, string name) => instance.GetType().GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static object? Invoke(object instance, string name, params object[] args) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
}
