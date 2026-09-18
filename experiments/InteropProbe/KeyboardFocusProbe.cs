using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using RemoteDesk;

// Two owned real windows on separate input queues; callbacks receive synthetic
// key records directly. No SendInput, clipboard access or production session.
internal static class KeyboardFocusProbe
{
    internal static int Run(string output)
    {
        if (GetForegroundWindow() == 0) throw new InvalidOperationException("Interactive desktop unavailable");
        using var client = new RemoteViewerClient();
        using var viewer = new RemoteViewerWindow(client, "RemoteDesk keyboard focus test", true, false, false, false, false, false);
        int result = 1;
        viewer.Shown += async (_, _) =>
        {
            try { result = await CheckAsync(viewer, client, output); }
            catch (Exception error) { Program.Save(Path.Combine(output, "keyboard-focus.json"), new { passed = false, error = error.ToString() }); }
            finally { viewer.Close(); }
        };
        Application.Run(viewer);
        return result;
    }

    private static async Task<int> CheckAsync(RemoteViewerWindow viewer, RemoteViewerClient client, string output)
    {
        Invoke(viewer, "UninstallSystemKeyboardCapture");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var input = new ConcurrentQueue<RemoteInputCommand>();
        Task host = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = peer.GetStream();
            var authenticated = await Protocol.AuthenticateServerDetailedAsync(stream, "focus-fixture", timeout.Token);
            using var session = authenticated.Session!;
            try
            {
                while (true)
                {
                    var message = await Protocol.ReadMessageAsync(stream, session, timeout.Token);
                    if (message.Type == MessageType.Input) input.Enqueue(RemoteMessageCodec.DecodeInput(message.PayloadSpan));
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException) { }
        });
        Form? local = null;
        var ready = new TaskCompletionSource<Form>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "focus-fixture", ViewerVideoMode.StableJpeg, timeout.Token);
            var picture = viewer.Controls.OfType<PictureBox>().Single();
            ActivateOwned(viewer);
            picture.Focus();
            if (GetForegroundWindow() != viewer.Handle) throw new InvalidOperationException("Owned viewer did not become foreground");
            bool foregroundConsumed = SendCopy(viewer).All(x => x == 1);
            await client.FlushInputAsync(timeout.Token);
            await Task.Delay(150, timeout.Token);
            int foregroundCopyCount = input.Count(x => x.Kind == RemoteInputKind.KeyDown && x.Data == (int)Keys.C);

            var thread = new Thread(() =>
            {
                try
                {
                    using var form = new Form { Text = "RemoteDesk local copy focus fixture", Size = new Size(400, 200) };
                    form.Controls.Add(new TextBox { Text = "No user clipboard is read or changed", Dock = DockStyle.Top });
                    form.Shown += (_, _) => { ActivateOwned(form); form.Controls[0].Focus(); ready.TrySetResult(form); };
                    Application.Run(form);
                    stopped.TrySetResult();
                }
                catch (Exception error) { ready.TrySetException(error); stopped.TrySetException(error); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            local = await ready.Task.WaitAsync(timeout.Token);
            await Task.Delay(150, timeout.Token);
            if (GetForegroundWindow() != local.Handle) throw new InvalidOperationException("Owned local window did not become foreground");
            // Async toolbar operations in the viewer restore surface focus
            // when they finish, including after switching to a local app.
            picture.Focus();
            bool stillLocalForeground = GetForegroundWindow() == local.Handle;
            bool staleSurfaceFocus = picture.ContainsFocus;
            nint[] backgroundResults = SendCopy(viewer);
            await client.FlushInputAsync(timeout.Token);
            await Task.Delay(150, timeout.Token);
            int backgroundCopies = input.Count(x => x.Kind == RemoteInputKind.KeyDown && x.Data == (int)Keys.C) - foregroundCopyCount;
            bool passed = stillLocalForeground && foregroundConsumed && foregroundCopyCount == 1 &&
                backgroundResults.All(x => x == 0) && backgroundCopies == 0;
            Program.Save(Path.Combine(output, "keyboard-focus.json"), new
            {
                passed, foregroundConsumed, foregroundCopyCount, staleSurfaceFocus, stillLocalForeground,
                backgroundCallbacksConsumed = backgroundResults.Count(x => x != 0), backgroundCopies,
                scope = "Real foreground window switch across two UI threads, product keyboard callback and authenticated loopback peer; no OS key injection or clipboard access"
            });
            return passed ? 0 : 1;
        }
        finally
        {
            if (local is not null) { local.BeginInvoke((Action)local.Close); await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            await client.DisconnectAsync();
            timeout.Cancel();
            await host;
        }
    }

    private static nint[] SendCopy(RemoteViewerWindow window)
    {
        var results = new List<nint>();
        foreach (var key in new[] { (Keys.LControlKey, true), (Keys.C, true), (Keys.C, false), (Keys.LControlKey, false) })
        {
            nint buffer = Marshal.AllocHGlobal(Marshal.SizeOf<HookData>());
            try
            {
                Marshal.StructureToPtr(new HookData { VirtualKey = (uint)key.Item1 }, buffer, false);
                results.Add((nint)Invoke(window, "LowLevelKeyboardCallback", 0, (nint)(key.Item2 ? 0x100 : 0x101), buffer)!);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return results.ToArray();
    }

    private static object? Invoke(object instance, string name, params object[] args) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static void ActivateOwned(Form window)
    {
        uint current = GetCurrentThreadId();
        uint foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        bool attached = current != foreground && foreground != 0 && AttachThreadInput(current, foreground, true);
        try { window.BringToFront(); window.Activate(); SetForegroundWindow(window.Handle); }
        finally { if (attached) AttachThreadInput(current, foreground, false); }
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint first, uint second, bool attach);
    [StructLayout(LayoutKind.Sequential)] private struct HookData
    {
        public uint VirtualKey, ScanCode, Flags, Time;
        public nuint ExtraInfo;
    }
}
