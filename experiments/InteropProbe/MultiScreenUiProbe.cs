using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using RemoteDesk;

// Launcher, two real viewer windows and encrypted synthetic screen streams on
// a private desktop. Never reads/writes real clipboard or forwards user input.
internal static class MultiScreenUiProbe
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static WindowsFormsSynchronizationContext? _uiContext;
    private const RemoteDeviceCapabilities Capabilities = RemoteDeviceCapabilities.RemoteDesktop |
        RemoteDeviceCapabilities.InputControl | RemoteDeviceCapabilities.CaptureTargetSelection |
        RemoteDeviceCapabilities.IndependentScreenSessions;

    internal static void Verify(string output)
    {
        using var host = new FixtureHost();
        var checks = new List<object>();
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed });
            Program.Save(Path.Combine(output, "multi-screen-ui.json"), new { checks });
            if (!passed) throw new InvalidOperationException(name);
        }
        using var main = new MainForm(new RemoteDeskSettings(), Path.Combine(output, "logs"));
        main.Show(); Pump();
        var client = Field<RemoteViewerClient>(main, "_viewerClient");
        typeof(RemoteViewerClient).GetField("_allowLocalConnectionsForTesting", Private)!.SetValue(client, true);
        var connection = new ViewerConnectionSnapshot("127.0.0.1", host.Port, FixtureHost.Password, ViewerVideoMode.StableJpeg);
        object pending = Activator.CreateInstance(typeof(MainForm).GetNestedType("PendingViewerConnection", BindingFlags.NonPublic)!, connection)!;
        typeof(MainForm).GetField("_pendingViewerConnection", Private)!.SetValue(main, pending);
        typeof(MainForm).GetField("_connectedViewerCapabilities", Private)!.SetValue(main, Capabilities);
        typeof(MainForm).GetField("_lastConnectedDeviceInfo", Private)!.SetValue(main, new RemoteDeviceDescriptor("Owned two-screen fixture", "Windows", Capabilities));
        var targets = Field<ComboBox>(main, "_viewerCaptureTargetBox");
        targets.Items.AddRange(FixtureHost.Targets.Cast<object>().ToArray()); targets.SelectedIndex = 0;
        Await(client.ConnectAsync("127.0.0.1", host.Port, FixtureHost.Password, ViewerVideoMode.StableJpeg));
        Wait(() => client.CanOpenAdditionalScreen);
        typeof(MainForm).GetMethod("ShowViewerWindow", Private)!.Invoke(main, null); Pump();
        var first = Field<RemoteViewerWindow>(main, "_viewerWindow");
        first.ClientSize = new Size(1600, 800); Pump();
        var open = Field<Button>(first, "_additionalScreenButton");
        Check("multiscreen button is available after negotiated support", open.Enabled && open.Text == "多屏分窗");
        var overflow = Field<ViewerActionOverflow>(first, "_statusActionOverflow");
        overflow.RebuildMenu();
        if (open.Parent == Field<FlowLayoutPanel>(first, "_fileTransferActionsPanel")) open.PerformClick();
        else overflow.MenuForTests.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == open.Text).PerformClick();
        IDictionary sessions = Field<IDictionary>(main, "_additionalScreens");
        RemoteViewerWindow? Second() => sessions.Count == 1
            ? (RemoteViewerWindow?)sessions.Values.Cast<object>().Single().GetType().GetProperty("Window", Private)!.GetValue(sessions.Values.Cast<object>().Single()) : null;
        Wait(() => Second() is { Visible: true } && Field<PictureBox>(Second()!, "_pictureBox").Image is not null);
        var second = Second()!;
        Check("button opens a distinct ownerless second screen window", !ReferenceEquals(first, second) && second.Owner is null && second.ShowInTaskbar);
        Check("each window names the correct remote screen", first.Text.Contains("屏幕 1") && second.Text.Contains("屏幕 2"));
        main.WindowState = FormWindowState.Minimized; Pump();
        Check("minimizing launcher leaves both viewers unminimized", first.Visible && second.Visible && first.WindowState != FormWindowState.Minimized && second.WindowState != FormWindowState.Minimized);

        foreach ((RemoteViewerWindow window, string name) in new[] { (first, "screen-1"), (second, "screen-2") })
        {
            window.ClientSize = new Size(800, 600); Pump();
            var actions = Field<FlowLayoutPanel>(window, "_fileTransferActionsPanel");
            var statusFooter = Field<Panel>(window, "_statusFooterPanel");
            var actionOverflow = Field<ViewerActionOverflow>(window, "_statusActionOverflow");
            actionOverflow.RebuildMenu();
            Program.Save(Path.Combine(output, name + "-layout.json"), new { window.ClientSize,
                footer = statusFooter.Bounds, actions = actions.Bounds, statusFooter.Visible,
                buttons = actionOverflow.Buttons.Select(button => new { button.Text, button.Visible, button.Enabled, button.Bounds, parent = button.Parent?.GetType().Name }),
                menu = actionOverflow.MenuForTests.Items.Cast<ToolStripItem>().Select(item => item.Text) });
            Check(name + " footer keeps actions in bounds", statusFooter.ClientRectangle.Contains(actions.Bounds));
            using var bitmap = new Bitmap(window.Width, window.Height);
            window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size));
            bitmap.Save(Path.Combine(output, name + ".png"));
            var picture = Field<PictureBox>(window, "_pictureBox");
            var click = new MouseEventArgs(MouseButtons.Left, 1, picture.Width / 2, picture.Height / 2, 0);
            typeof(RemoteViewerWindow).GetMethod("PictureBox_MouseDown", Private)!.Invoke(window, [picture, click]);
            typeof(RemoteViewerWindow).GetMethod("PictureBox_MouseUp", Private)!.Invoke(window, [picture, click]);
        }
        Wait(() => host.Inputs.ContainsKey("screen-1") && host.Inputs.ContainsKey("screen-2"));
        Check("each window routes clicks through its own screen stream", host.Inputs.Count == 2 &&
            Math.Abs(host.Inputs["screen-1"].X - 320) <= 2 && Math.Abs(host.Inputs["screen-1"].Y - 180) <= 2 &&
            Math.Abs(host.Inputs["screen-2"].X - 180) <= 2 && Math.Abs(host.Inputs["screen-2"].Y - 320) <= 2);
        second.WindowState = FormWindowState.Minimized; Pump();
        Await((Task)typeof(RemoteViewerWindow).GetMethod("SwitchCaptureTargetAsync", Private)!.Invoke(first, null)!);
        Await(client.SendInputAsync(RemoteInputCommand.MouseUp(RemoteMouseButton.Left, 41, 42)));
        Wait(() => host.Inputs.Values.Any(input => input.X == 41 && input.Y == 42));
        Check("switching to an open screen restores its window without a duplicate stream",
            host.SelectionRequests.IsEmpty && second.WindowState != FormWindowState.Minimized && first.Text.Contains("屏幕 1"));
        second.WindowState = FormWindowState.Minimized; Pump();
        targets.SelectedIndex = 1;
        Await((Task)typeof(MainForm).GetMethod("ViewerCaptureTargetChangedAsync", Private)!.Invoke(main, null)!);
        Await(client.SendInputAsync(RemoteInputCommand.MouseUp(RemoteMouseButton.Left, 43, 44)));
        Wait(() => host.Inputs.Values.Any(input => input.X == 43 && input.Y == 44));
        Check("launcher screen selection activates the existing window and keeps its original target",
            host.SelectionRequests.IsEmpty && second.WindowState != FormWindowState.Minimized && targets.SelectedIndex == 0);
        Await((Task)typeof(MainForm).GetMethod("OpenAdditionalScreenAsync", Private)!.Invoke(main, [FixtureHost.Targets[1]])!);
        Check("repeated open reuses the window without a third connection", ReferenceEquals(second, Second()) && host.Connections == 2);

        for (int iteration = 0; iteration < 4; iteration++)
        {
            second.Close(); Pump();
            Wait(() => sessions.Count == 0 && host.Connections == 1);
            Await((Task)typeof(MainForm).GetMethod("OpenAdditionalScreenAsync", Private)!.Invoke(main, [FixtureHost.Targets[1]])!);
            Wait(() => Second() is { Visible: true } && Field<PictureBox>(Second()!, "_pictureBox").Image is not null);
            second = Second()!;
            Check($"close/reopen cycle {iteration + 1} retains the first stream and bounded connections",
                client.IsConnected && !first.IsDisposed && host.Connections == 2);
        }
        first.Close(); Pump();
        Wait(() => host.Paused.ContainsKey("screen-1"));
        Check("closing first window keeps second connected and pauses hidden video", !second.IsDisposed && client.IsConnected && host.Connections == 2);
        var reopen = Field<Button>(second, "_additionalScreenButton");
        var secondOverflow = Field<ViewerActionOverflow>(second, "_statusActionOverflow");
        secondOverflow.RebuildMenu();
        ToolStripMenuItem? reopenItem = secondOverflow.MenuForTests.Items.OfType<ToolStripMenuItem>().FirstOrDefault(item => item.Text == reopen.Text);
        bool reopenInToolbar = reopen.Parent == Field<FlowLayoutPanel>(second, "_fileTransferActionsPanel") && reopen.Visible;
        Check("the remaining window has a reachable command to reopen the first screen", reopen.Enabled && (reopenInToolbar || reopenItem is { Enabled: true }));
        if (reopenInToolbar) reopen.PerformClick(); else reopenItem!.PerformClick();
        Wait(() => typeof(MainForm).GetField("_viewerWindow", Private)!.GetValue(main) is RemoteViewerWindow { IsDisposed: false });
        first = Field<RemoteViewerWindow>(main, "_viewerWindow");
        Wait(() => host.Paused.TryGetValue("screen-1", out bool paused) && !paused && Field<PictureBox>(first, "_pictureBox").Image is not null);
        Check("reopening the first window resumes its stream without another connection", host.Connections == 2 && !second.IsDisposed);

        host.DropScreen("screen-2");
        Wait(() => second.IsDisposed && sessions.Count == 0 && host.Connections == 1);
        Check("a dropped secondary closes cleanly and leaves the first usable", client.IsConnected && !first.IsDisposed);
        Await((Task)typeof(RemoteViewerWindow).GetMethod("SwitchCaptureTargetAsync", Private)!.Invoke(first, null)!);
        Wait(() => first.SelectedCaptureTarget?.Id == "screen-2" && Field<PictureBox>(first, "_pictureBox").Image is { Width: 360, Height: 640 });
        Check("single-window switching still changes the captured screen after the extra closes", host.SelectionRequests.TryPeek(out string? switched) && switched == "screen-2");
        Await(client.SelectCaptureTargetAsync("screen-1"));
        Wait(() => first.SelectedCaptureTarget?.Id == "screen-1" && Field<PictureBox>(first, "_pictureBox").Image is { Width: 640, Height: 360 });
        Await((Task)typeof(MainForm).GetMethod("OpenAdditionalScreenAsync", Private)!.Invoke(main, [FixtureHost.Targets[1]])!);
        Wait(() => Second() is { Visible: true } && Field<PictureBox>(Second()!, "_pictureBox").Image is not null);
        second = Second()!;
        Check("a dropped secondary can rejoin the same controller", host.Connections == 2 && client.IsConnected);
        first.Close(); Pump();
        Wait(() => host.Paused.TryGetValue("screen-1", out bool paused) && paused);
        second.Close(); Pump();
        Wait(() => !client.IsConnected && host.Connections == 0);
        Check("closing last window releases both transports", sessions.Count == 0);
        VerifyDelayedCleanup(output, Check, pendingOnly: false);
        VerifyDelayedCleanup(output, Check, pendingOnly: true);
        VerifyFailedPendingOpen(output, Check);
    }

    private static void VerifyFailedPendingOpen(string output, Action<string, bool> check)
    {
        using var host = new FixtureHost { AttachmentAdmission = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var main = new MainForm(new RemoteDeskSettings(), Path.Combine(output, "pending-open-logs"));
        main.Show(); Pump();
        var client = Field<RemoteViewerClient>(main, "_viewerClient");
        typeof(RemoteViewerClient).GetField("_allowLocalConnectionsForTesting", Private)!.SetValue(client, true);
        var connection = new ViewerConnectionSnapshot("127.0.0.1", host.Port, FixtureHost.Password, ViewerVideoMode.StableJpeg);
        object pending = Activator.CreateInstance(typeof(MainForm).GetNestedType("PendingViewerConnection", BindingFlags.NonPublic)!, connection)!;
        typeof(MainForm).GetField("_pendingViewerConnection", Private)!.SetValue(main, pending);
        typeof(MainForm).GetField("_connectedViewerCapabilities", Private)!.SetValue(main, Capabilities);
        var targets = Field<ComboBox>(main, "_viewerCaptureTargetBox");
        targets.Items.AddRange(FixtureHost.Targets.Cast<object>().ToArray()); targets.SelectedIndex = 0;
        Await(client.ConnectAsync("127.0.0.1", host.Port, FixtureHost.Password, ViewerVideoMode.StableJpeg));
        Wait(() => client.CanOpenAdditionalScreen);
        typeof(MainForm).GetMethod("ShowViewerWindow", Private)!.Invoke(main, null); Pump();
        Task opening = (Task)typeof(MainForm).GetMethod("OpenAdditionalScreenAsync", Private)!.Invoke(main, [FixtureHost.Targets[1]])!;
        Wait(() => host.AttachmentJoinObserved.Task.IsCompleted);
        Field<RemoteViewerWindow>(main, "_viewerWindow").Close(); Pump();
        check("closing the first window during attachment setup retains the pending group", client.IsConnected && !opening.IsCompleted);
        host.AttachmentAdmission.SetResult(false);
        bool rejected = false;
        try { Await(opening); }
        catch (RemoteSessionRejectedException) { rejected = true; }
        Wait(() => !client.IsConnected && host.Connections == 0);
        check("failed pending attachment releases a headless controller without leaving a blank window",
            rejected && Field<IDictionary>(main, "_additionalScreens").Count == 0);
    }

    private static void VerifyDelayedCleanup(string output, Action<string, bool> check, bool pendingOnly)
    {
        using var host = new FixtureHost();
        using var replacement = new FixtureHost();
        using var main = new MainForm(new RemoteDeskSettings(), Path.Combine(output, "lifecycle-logs"));
        main.Show(); Pump();
        var client = Field<RemoteViewerClient>(main, "_viewerClient");
        var messages = new ConcurrentQueue<string>();
        client.Log += messages.Enqueue;
        typeof(RemoteViewerClient).GetField("_allowLocalConnectionsForTesting", Private)!.SetValue(client, true);
        void SetPending(FixtureHost endpoint)
        {
            var snapshot = new ViewerConnectionSnapshot("127.0.0.1", endpoint.Port, FixtureHost.Password, ViewerVideoMode.StableJpeg);
            object pending = Activator.CreateInstance(typeof(MainForm).GetNestedType("PendingViewerConnection", BindingFlags.NonPublic)!, snapshot)!;
            typeof(MainForm).GetField("_pendingViewerConnection", Private)!.SetValue(main, pending);
        }
        SetPending(host);
        typeof(MainForm).GetField("_connectedViewerCapabilities", Private)!.SetValue(main, Capabilities);
        var targets = Field<ComboBox>(main, "_viewerCaptureTargetBox");
        targets.Items.AddRange(FixtureHost.Targets.Cast<object>().ToArray()); targets.SelectedIndex = 0;
        Await(client.ConnectAsync("127.0.0.1", host.Port, FixtureHost.Password, ViewerVideoMode.StableJpeg));
        Wait(() => client.CanOpenAdditionalScreen);
        typeof(MainForm).GetMethod("ShowViewerWindow", Private)!.Invoke(main, null); Pump();
        Await((Task)typeof(MainForm).GetMethod("OpenAdditionalScreenAsync", Private)!.Invoke(main, [FixtureHost.Targets[1]])!);
        IDictionary screens = Field<IDictionary>(main, "_additionalScreens");
        object attached = screens.Values.Cast<object>().Single();
        var extraClient = (RemoteViewerClient)attached.GetType().GetProperty("Client", Private)!.GetValue(attached)!;
        Field<RemoteViewerWindow>(main, "_viewerWindow").Close(); Pump();

        // Deterministically hold only the old attachment's cleanup. A new main
        // connection can complete while that background disposal is still queued.
        SemaphoreSlim drain = Field<SemaphoreSlim>(extraClient, "_disconnectLock");
        drain.Wait();
        Task closing = (Task)typeof(MainForm).GetMethod("AdditionalScreenClosedAsync", Private)!.Invoke(main, ["screen-2", attached])!;
        long oldGeneration = client.InputConnectionGeneration;
        try
        {
            if (!pendingOnly) Await(client.DisconnectAsync());
            SetPending(replacement);
            if (!pendingOnly)
            {
                Await(client.ConnectAsync("127.0.0.1", replacement.Port, FixtureHost.Password, ViewerVideoMode.StableJpeg));
                Wait(() => client.CanOpenAdditionalScreen);
            }
        }
        finally { drain.Release(); }
        Await(closing);
        Program.Save(Path.Combine(output, pendingOnly ? "pending-cleanup-state.json" : "cleanup-state.json"), new { client.IsConnected, replacement.Connections,
            oldGeneration, generation = client.InputConnectionGeneration, messages = messages.ToArray() });
        if (pendingOnly)
            check("old cleanup cannot erase a pending connection before its new transport is published",
                typeof(MainForm).GetField("_pendingViewerConnection", Private)!.GetValue(main) is not null && client.IsConnected);
        else
            check("delayed old screen cleanup cannot disconnect a newer connection", client.IsConnected && replacement.Connections == 1);
    }

    private static T Field<T>(object instance, string field) => (T)instance.GetType().GetField(field, Private)!.GetValue(instance)!;
    private static void Pump()
    {
        Application.DoEvents(); Thread.Sleep(10); Application.DoEvents();
        // DoEvents uninstalls its temporary context when the nested pump exits.
        // Keep the same UI affinity that the product's Application.Run provides.
        SynchronizationContext.SetSynchronizationContext(_uiContext ??= new WindowsFormsSynchronizationContext());
    }
    private static void Wait(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("Multi-screen UI fixture timed out."); Pump(); }
        Pump();
    }
    private static void Await(Task task) { Wait(() => task.IsCompleted); task.GetAwaiter().GetResult(); }

    private sealed class FixtureHost : IDisposable
    {
        internal const string Password = "owned-private-screen-fixture";
        internal static readonly CaptureTargetInfo[] Targets = [new("screen-1", "屏幕 1 横屏"), new("screen-2", "屏幕 2 竖屏")];
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly RemoteHostServer.ActiveClientGate<TcpClient> _group = new();
        private readonly ConcurrentBag<Task> _workers = new();
        private readonly ConcurrentDictionary<TcpClient, string> _activeScreens = new();
        private readonly Task _accept;
        private int _connections;
        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        internal int Connections => Volatile.Read(ref _connections);
        internal ConcurrentDictionary<string, RemoteInputCommand> Inputs { get; } = new();
        internal ConcurrentQueue<string> SelectionRequests { get; } = new();
        internal ConcurrentDictionary<string, bool> Paused { get; } = new();
        internal TaskCompletionSource<bool>? AttachmentAdmission { get; init; }
        internal TaskCompletionSource<bool> AttachmentJoinObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal FixtureHost() { _listener.Start(); _accept = Task.Run(Accept); }
        internal void DropScreen(string target)
        {
            foreach (var entry in _activeScreens.Where(entry => entry.Value == target)) entry.Key.Dispose();
        }
        private async Task Accept()
        {
            try { while (!_stop.IsCancellationRequested) _workers.Add(Serve(await _listener.AcceptTcpClientAsync(_stop.Token))); }
            catch (OperationCanceledException) { }
        }
        private async Task Serve(TcpClient peer)
        {
            using (peer)
            using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            using (var write = new SemaphoreSlim(1, 1))
            {
                Interlocked.Increment(ref _connections);
                Task? frames = null;
                try
                {
                    var stream = peer.GetStream();
                    var auth = await Protocol.AuthenticateServerDetailedAsync(stream, Password, lifetime.Token);
                    using var session = auth.Session!;
                    string target = "screen-1";
                    Task Send(byte[] payload) => Protocol.WriteMessageAsync(stream, MessageType.Control, payload, session, write, lifetime.Token);
                    if (auth.IsScreenAttachment)
                    {
                        var message = await Protocol.ReadMessageAsync(stream, session, lifetime.Token);
                        var join = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
                        AttachmentJoinObserved.TrySetResult(true);
                        if (AttachmentAdmission is { } admission && !await admission.Task.WaitAsync(lifetime.Token))
                        {
                            await Send(RemoteMessageCodec.EncodeSessionRejected("Owned fixture rejected the pending screen."));
                            return;
                        }
                        if (!_group.TryAttach(peer, join.ScreenSessionToken)) throw new InvalidDataException("Fixture attachment rejected.");
                        target = join.TargetId!;
                        await Send(RemoteMessageCodec.EncodeScreenAttachmentAccepted());
                    }
                    else foreach (TcpClient old in _group.Activate(peer)) old.Dispose();
                    _activeScreens[peer] = target;
                    await Send(RemoteMessageCodec.EncodeDeviceInfo(new RemoteDeviceDescriptor("Owned fixture", "Windows", Capabilities)));
                    await Send(RemoteMessageCodec.EncodeCaptureTargetList(Targets));
                    await Send(RemoteMessageCodec.EncodeCaptureTargetChanged(Targets.Single(t => t.Id == target)));
                    int paused = 0;
                    byte[] frame = CreateFrame(target);
                    frames = Task.Run(async () =>
                    {
                        while (!lifetime.IsCancellationRequested)
                        {
                            if (Volatile.Read(ref paused) == 0) await Protocol.WriteMessageAsync(stream, MessageType.Frame, Volatile.Read(ref frame), session, write, lifetime.Token);
                            await Task.Delay(80, lifetime.Token);
                        }
                    });
                    while (!lifetime.IsCancellationRequested)
                    {
                        var message = await Protocol.ReadMessageAsync(stream, session, lifetime.Token);
                        if (message.Type == MessageType.Ping)
                            await Protocol.WriteMessageAsync(stream, MessageType.Pong, message.PayloadMemory, session, write, lifetime.Token);
                        else if (message.Type == MessageType.Input)
                        {
                            var input = RemoteMessageCodec.DecodeInput(message.PayloadSpan);
                            if (input.Kind == RemoteInputKind.MouseUp) Inputs[target] = input;
                        }
                        else if (message.Type == MessageType.Control)
                        {
                            var control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
                            if (control.Kind == RemoteControlKind.SelectCaptureTarget)
                            {
                                SelectionRequests.Enqueue(control.TargetId!);
                                target = Targets.Single(item => item.Id == control.TargetId).Id;
                                _activeScreens[peer] = target;
                                Volatile.Write(ref frame, CreateFrame(target));
                                await Send(RemoteMessageCodec.EncodeCaptureTargetChanged(Targets.Single(item => item.Id == target)));
                            }
                            if (control.Kind == RemoteControlKind.ViewerCapabilities)
                                await Send(RemoteMessageCodec.EncodeScreenAttachmentOffer(_group.GetAttachmentToken(peer)!));
                            if (control.Kind == RemoteControlKind.ScreenStreamPause)
                            {
                                Volatile.Write(ref paused, control.Success ? 1 : 0);
                                Paused[target] = control.Success;
                            }
                        }
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
                finally
                {
                    lifetime.Cancel();
                    if (frames is not null) { try { await frames; } catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { } }
                    _group.Release(peer);
                    _activeScreens.TryRemove(peer, out _);
                    Interlocked.Decrement(ref _connections);
                }
            }
        }
        private static byte[] CreateFrame(string target)
        {
            bool portrait = target == "screen-2";
            using var bitmap = new Bitmap(portrait ? 360 : 640, portrait ? 640 : 360);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(portrait ? Color.DarkGreen : Color.SteelBlue);
                using var font = new Font("Microsoft YaHei UI", 25);
                graphics.DrawString(portrait ? "屏幕 2\n竖屏独立窗口" : "屏幕 1\n横屏独立窗口", font, Brushes.White, 24, 40);
            }
            using var bytes = new MemoryStream(); bitmap.Save(bytes, ImageFormat.Jpeg);
            return RemoteMessageCodec.EncodeFrame(bitmap.Width, bitmap.Height, bytes.ToArray());
        }
        public void Dispose()
        {
            _stop.Cancel(); _listener.Stop();
            try { _accept.Wait(TimeSpan.FromSeconds(2)); Task.WhenAll(_workers).Wait(TimeSpan.FromSeconds(4)); } catch (Exception) { }
            _stop.Dispose();
        }
    }
}
