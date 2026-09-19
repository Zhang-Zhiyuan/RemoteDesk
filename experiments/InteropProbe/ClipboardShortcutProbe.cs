using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using RemoteDesk;

// Runs only inside ClipboardSystemProbe's private window station. The local
// clipboard, WinForms window and encrypted connection are real; the peer owns
// synthetic text, never an interactive user's clipboard or input target.
internal static class ClipboardShortcutProbe
{
    internal static Task<int> RunAsync(string output, nint desktop, bool contextMenus = false)
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (!SetThreadDesktop(desktop)) throw new System.ComponentModel.Win32Exception();
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                using var pump = new Form { ShowInTaskbar = false };
                pump.Shown += async (_, _) =>
                {
                    try { completion.TrySetResult(contextMenus
                        ? await ClipboardContextMenuProbe.RunCasesAsync(output)
                        : await RunCasesAsync(output, desktop)); }
                    catch (Exception error) { completion.TrySetException(error); }
                    finally { pump.Close(); }
                };
                Application.Run(pump);
            }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "Private clipboard shortcut UI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(90));
    }

    private static async Task<int> RunCasesAsync(string output, nint desktop)
    {
        var checks = new List<object>();
        int failed = 0;
        foreach (var scenario in new[]
        {
            new Scenario("Windows native Ctrl+V", "Windows", true, Keys.Control | Keys.V),
            new Scenario("Linux native Ctrl+V", "Linux", true, Keys.Control | Keys.V),
            new Scenario("Windows fallback Ctrl+V", "Windows", false, Keys.Control | Keys.V),
            new Scenario("Android fallback Ctrl+V", "Android", false, Keys.Control | Keys.V),
            new Scenario("Windows right Ctrl+V held", "Windows", true, Keys.Control | Keys.V, Hold: true),
            new Scenario("Linux terminal Ctrl+Shift+V", "Linux", true, Keys.Control | Keys.Shift | Keys.V),
            new Scenario("Windows Shift+Insert", "Windows", true, Keys.Shift | Keys.Insert),
            new Scenario("Reject write does not paste old text", "Windows", true, Keys.Control | Keys.V, Reject: true),
            new Scenario("Lost focus cancels delayed paste", "Windows", true, Keys.Control | Keys.V, LoseFocus: true),
            new Scenario("Held V does not paste repeatedly", "Windows", true, Keys.Control | Keys.V, Repeat: true),
            new Scenario("Remote copy followed immediately by paste", "Windows", true, Keys.Control | Keys.V, CopyFirst: true),
            new Scenario("Remote non-text copy stays on the peer", "Windows", true, Keys.Control | Keys.V, CopyFirst: true, NonTextCopy: true)
        })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            const string sample = "本机新复制 中文😀\r\n第二行\t缩进";
            const string remoteCopy = "刚在远端复制的中文😀\n不能被本机旧内容覆盖";
            string expectedText = scenario.CopyFirst ? remoteCopy : sample;
            await ClipboardTextService.SetTextAsync(sample);
            var inputs = new ConcurrentQueue<(RemoteInputCommand Command, bool Acknowledged)>();
            var copied = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pasted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task host = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = peer.GetStream();
                using var writeLock = new SemaphoreSlim(1, 1);
                var authenticated = await Protocol.AuthenticateServerDetailedAsync(stream, "shortcut-fixture", timeout.Token);
                using var session = authenticated.Session!;
                await Protocol.WriteMessageAsync(stream, MessageType.Control,
                    RemoteMessageCodec.EncodeDeviceInfo(new RemoteDeviceDescriptor("clipboard-fixture", scenario.Platform,
                        RemoteDeviceCapabilities.InputControl | RemoteDeviceCapabilities.ClipboardText |
                        RemoteDeviceCapabilities.ClipboardPasteShortcut)), session, writeLock, timeout.Token);
                string remoteText = "远端旧内容";
                Task? acknowledgement = null;
                try
                {
                    while (true)
                    {
                        var message = await Protocol.ReadMessageAsync(stream, session, timeout.Token);
                        if (message.Type == MessageType.Input)
                        {
                            var command = RemoteMessageCodec.DecodeInput(message.PayloadSpan);
                            inputs.Enqueue((command, ack.Task.IsCompletedSuccessfully));
                            if (command.Kind == RemoteInputKind.KeyDown && command.Data == (int)Keys.C)
                                remoteText = remoteCopy;
                            if (command.Kind == RemoteInputKind.KeyDown && command.Data is (int)Keys.V or (int)Keys.Insert)
                                pasted.TrySetResult(remoteText);
                        }
                        else if (message.Type == MessageType.Control)
                        {
                            var control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
                            if (control.Kind == RemoteControlKind.ClipboardGetText)
                            {
                                await Task.Delay(250, timeout.Token);
                                await Protocol.WriteMessageAsync(stream, MessageType.Control,
                                    RemoteMessageCodec.EncodeClipboardText(scenario.NonTextCopy ? "" : remoteText), session, writeLock, timeout.Token);
                                continue;
                            }
                            if (control.Kind != RemoteControlKind.ClipboardSetText) continue;
                            copied.TrySetResult(control.Text!);
                            acknowledgement = Task.Run(async () =>
                            {
                                await Task.Delay(300, timeout.Token);
                                if (!scenario.Reject) remoteText = control.Text!;
                                ack.TrySetResult(!scenario.Reject);
                                await Protocol.WriteMessageAsync(stream, MessageType.Control,
                                    RemoteMessageCodec.EncodeClipboardStatus(!scenario.Reject, scenario.Reject ? "Fixture rejected write" : "Fixture clipboard updated"),
                                    session, writeLock, timeout.Token);
                            });
                        }
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
                finally { if (acknowledgement is not null) try { await acknowledgement; } catch (OperationCanceledException) { } }
            });
            using var client = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
            await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "shortcut-fixture", ViewerVideoMode.StableJpeg);
            using var window = new RemoteViewerWindow(client, scenario.Name, true, true, false, false, false,
                scenario.Platform == "Android") { ShowInTaskbar = false };
            window.SetRemotePlatform(scenario.Platform);
            window.Show();
            Invoke(window, "UninstallSystemKeyboardCapture"); // No global synthetic input is needed.
            var picture = window.Controls.OfType<PictureBox>().Single();
            picture.Focus();
            if (!picture.ContainsFocus) throw new InvalidOperationException("Private test surface did not get focus.");
            var modifiers = new List<Keys>();
            if (scenario.Shortcut.HasFlag(Keys.Control)) modifiers.Add(scenario.Hold ? Keys.RControlKey : Keys.LControlKey);
            if (scenario.Shortcut.HasFlag(Keys.Shift)) modifiers.Add(Keys.LShiftKey);
            void Send(Keys key, bool down)
            {
                Keys data = key | (scenario.Shortcut & Keys.Modifiers);
                if (scenario.Native)
                    Invoke(window, "TryForwardKeyboardCommand", down ? RemoteInputCommand.KeyDown((int)key) : RemoteInputCommand.KeyUp((int)key), data);
                else
                    Invoke(window, down ? "PictureBox_KeyDown" : "PictureBox_KeyUp", picture, new KeyEventArgs(data));
            }
            foreach (var modifier in modifiers) Send(modifier, true);
            Keys mainKey = scenario.Shortcut & Keys.KeyCode;
            if (scenario.CopyFirst) { Send(Keys.C, true); Send(Keys.C, false); }
            Send(mainKey, true);
            if (scenario.Repeat) { Send(mainKey, true); Send(mainKey, true); }
            Send(mainKey, false);
            if (!scenario.Hold) foreach (var modifier in modifiers.AsEnumerable().Reverse()) Send(modifier, false);
            if (scenario.LoseFocus)
            {
                await copied.Task.WaitAsync(timeout.Token);
                Invoke(window, "ReleaseAllRemoteInputs");
            }
            await Task.Delay(scenario.CopyFirst ? 1700 : 850, timeout.Token);
            var commands = inputs.ToArray();
            bool expectPaste = !scenario.Reject && !scenario.LoseFocus;
            bool expectedClipboardWrite = !scenario.NonTextCopy;
            bool passed = (expectedClipboardWrite ? copied.Task.IsCompletedSuccessfully && copied.Task.Result == expectedText : !copied.Task.IsCompleted) &&
                (expectPaste ? pasted.Task.IsCompletedSuccessfully && pasted.Task.Result == expectedText : !pasted.Task.IsCompleted) &&
                commands.Where(item => item.Command.Kind == RemoteInputKind.KeyDown && item.Command.Data is (int)Keys.V or (int)Keys.Insert)
                    .All(item => item.Acknowledged || !expectedClipboardWrite) &&
                commands.Count(item => item.Command.Kind == RemoteInputKind.KeyDown && item.Command.Data == (int)mainKey) == (expectPaste ? 1 : 0);
            if (scenario.Shortcut.HasFlag(Keys.Control) && scenario.Shortcut.HasFlag(Keys.Shift))
                passed &= commands.Any(item => item.Acknowledged && item.Command.Kind == RemoteInputKind.KeyDown && item.Command.Data == (int)Keys.ShiftKey);
            if (scenario.Hold)
                passed &= !commands.Any(item => item.Acknowledged && item.Command.Kind == RemoteInputKind.KeyUp && item.Command.Data is (int)Keys.ControlKey or (int)Keys.RControlKey);
            checks.Add(new { scenario.Name, passed, synchronized = copied.Task.IsCompletedSuccessfully,
                pastedNewText = pasted.Task.IsCompletedSuccessfully && pasted.Task.Result == expectedText,
                pasteCount = commands.Count(item => item.Command.Kind == RemoteInputKind.KeyDown && item.Command.Data == (int)mainKey) });
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {scenario.Name}");
            if (!passed) failed++;
            if (scenario.Hold) foreach (var modifier in modifiers.AsEnumerable().Reverse()) Send(modifier, false);
            window.Close();
            await client.DisconnectAsync();
            timeout.Cancel();
            await host;
        }
        // A Win32 focus handle belongs to an input queue, not necessarily to
        // the foreground application. Reproduce switching to another app on
        // another UI thread without ever entering the interactive desktop.
        using (var client = new RemoteViewerClient(allowLocalConnectionsForTesting: true))
        using (var window = new RemoteViewerWindow(client, "Background hook fixture", true, true, false, false, false, false)
            { ShowInTaskbar = false })
        {
            window.Show();
            Invoke(window, "UninstallSystemKeyboardCapture");
            var picture = window.Controls.OfType<PictureBox>().Single();
            picture.Focus();
            var ready = new TaskCompletionSource<Form>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var otherThread = new Thread(() =>
            {
                try
                {
                    if (!SetThreadDesktop(desktop)) throw new System.ComponentModel.Win32Exception();
                    using var other = new Form { ShowInTaskbar = false, Text = "Local copy fixture" };
                    var text = new TextBox { Text = "Owned local text", Dock = DockStyle.Fill };
                    other.Controls.Add(text);
                    other.Shown += (_, _) => { text.Focus(); SetForegroundWindow(other.Handle); ready.TrySetResult(other); };
                    Application.Run(other);
                    stopped.TrySetResult();
                }
                catch (Exception error) { ready.TrySetException(error); stopped.TrySetException(error); }
            }) { IsBackground = true };
            otherThread.SetApartmentState(ApartmentState.STA);
            otherThread.Start();
            Form other = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await Task.Delay(100);
                nint foreground = GetForegroundWindow();
                // The own-injected-event guard used to swallow an event even
                // with no connection, if the background picture kept focus.
                nint data = Marshal.AllocHGlobal(32);
                nint consumed;
                try
                {
                    Marshal.StructureToPtr(new HookData { VirtualKey = (uint)Keys.C, Flags = 0x10,
                        ExtraInfo = InputInjector.InjectedInputMarker }, data, false);
                    consumed = (nint)Invoke(window, "LowLevelKeyboardCallback", 0, (nint)0x100, data)!;
                }
                finally { Marshal.FreeHGlobal(data); }
                // An isolated noninteractive window station has no OS
                // foreground window. This is also an ownership boundary:
                // a stale focused child must not authorize the global hook.
                bool pass = foreground != window.Handle && consumed == 0;
                checks.Add(new { Name = "Non-foreground viewer must not swallow keyboard input", passed = pass,
                    backgroundSurfaceContainsFocus = picture.ContainsFocus,
                    viewerIsForeground = foreground == window.Handle,
                    noninteractiveStation = foreground == 0, consumed = consumed.ToInt64() });
                Console.WriteLine($"{(pass ? "PASS" : "FAIL")}: Background viewer keyboard isolation");
                if (!pass) failed++;
            }
            finally
            {
                other.BeginInvoke((Action)other.Close);
                await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
                window.Close();
            }
        }
        Program.Save(Path.Combine(output, "clipboard-shortcuts.json"), new
        {
            complete = true, failed,
            scope = "Private Windows clipboard and WinForms routing; encrypted synthetic peers; no interactive user data", checks
        });
        return failed == 0 ? 0 : 1;
    }

    private static object? Invoke(object instance, string name, params object[] args) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);

    private sealed record Scenario(string Name, string Platform, bool Native, Keys Shortcut,
        bool Reject = false, bool LoseFocus = false, bool Repeat = false, bool Hold = false, bool CopyFirst = false, bool NonTextCopy = false);

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(nint desktop);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [StructLayout(LayoutKind.Sequential)] private struct HookData
    {
        public uint VirtualKey, ScanCode, Flags, Time;
        public nuint ExtraInfo;
    }
}
