using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using RemoteDesk;

// Must run on ClipboardSystemProbe's private window station. Native event
// records are synthetic; the window, TCP authentication, codec and user32
// keyboard-map/text translation are real. Never calls SendInput, changes the
// input desktop/layout, or reads/writes the interactive user's clipboard.
internal static class KeyboardModifierProbe
{
    internal static Task<int> RunAsync(string output, nint desktop)
    {
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (!SetThreadDesktop(desktop)) throw new System.ComponentModel.Win32Exception();
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                using var pump = new Form { ShowInTaskbar = false };
                pump.Shown += async (_, _) =>
                {
                    try { done.TrySetResult(await RunCasesAsync(output)); }
                    catch (Exception error) { done.TrySetException(error); }
                    finally { pump.Close(); }
                };
                Application.Run(pump);
            }
            catch (Exception error) { done.TrySetException(error); }
        }) { IsBackground = true, Name = "Private keyboard protocol UI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private readonly record struct Key(int Vk, int Scan, bool Down, bool Extended = false, bool Raw = false);
    private sealed record Scenario(string Name, string ExpectedText, Key[] Keys, bool ReleaseOnFocusLoss = false);

    private static async Task<int> RunCasesAsync(string output)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var received = new ConcurrentQueue<RemoteInputCommand>();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task peerTask = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            var auth = await Protocol.AuthenticateServerDetailedAsync(stream, "owned-keyboard-fixture", deadline.Token);
            using var session = auth.Session!;
            try
            {
                while (!stopped.Task.IsCompleted)
                {
                    var message = await Protocol.ReadMessageAsync(stream, session, deadline.Token);
                    if (message.Type == MessageType.Input) received.Enqueue(RemoteMessageCodec.DecodeInput(message.PayloadSpan));
                }
            }
            catch (Exception ex) when (stopped.Task.IsCompleted && ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        });
        using var client = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        var checks = new List<object>();
        bool allPassed = false;
        string? failure = null;
        try
        {
            await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
                "owned-keyboard-fixture", ViewerVideoMode.StableJpeg, deadline.Token);
            using var window = new RemoteViewerWindow(client, "Isolated keyboard fixture", true, false, false, false, false, false)
                { ShowInTaskbar = false };
            window.Show();
            Invoke(window, "UninstallSystemKeyboardCapture");
            var picture = window.Controls.OfType<PictureBox>().Single();
            picture.Focus();
            if (!picture.ContainsFocus) throw new InvalidOperationException("Private viewer surface did not receive focus.");
            nint layout = GetKeyboardLayout(0);
            foreach (var scenario in new[]
            {
                new Scenario("right Shift hook extended + 1, raw generic release", "!", [
                    new(0xA1,0x36,true,true), new(0x31,0x02,true), new(0x31,0x02,false), new(0x10,0x36,false,true,true)]),
                new Scenario("right Shift raw generic + A, hook sided release", "A", [
                    new(0x10,0x36,true,true,true), new(0x41,0x1E,true), new(0x41,0x1E,false), new(0xA1,0x36,false,true)]),
                new Scenario("left Shift + A", "A", [
                    new(0xA0,0x2A,true), new(0x41,0x1E,true), new(0x41,0x1E,false), new(0x10,0x2A,false,false,true)]),
                new Scenario("release right Shift while left remains held", "A", [
                    new(0x10,0x2A,true,false,true), new(0xA1,0x36,true,true), new(0x10,0x36,false,true,true),
                    new(0x41,0x1E,true), new(0x41,0x1E,false), new(0xA0,0x2A,false)]),
                new Scenario("both Control keys and mixed-source release", "", [
                    new(0xA2,0x1D,true), new(0xA3,0x1D,true,true), new(0x11,0x1D,false,true,true), new(0x11,0x1D,false,false,true)]),
                new Scenario("both Alt keys and mixed-source release", "", [
                    new(0xA4,0x38,true), new(0xA5,0x38,true,true), new(0x12,0x38,false,true,true), new(0x12,0x38,false,false,true)]),
                new Scenario("right Shift released on focus loss", "", [new(0xA1,0x36,true,true)], true),
                new Scenario("genuine E0 navigation key remains extended", "", [new(0x2D,0x52,true,true),new(0x2D,0x52,false,true)])
            })
            {
                int start = received.Count;
                bool allForwarded = true;
                foreach (var key in scenario.Keys)
                {
                    int message = key.Down ? 0x100 : 0x101;
                    RemoteInputCommand command;
                    bool parsed = key.Raw
                        ? RemoteViewerWindow.TryCreateRawKeyboardCommand(message, key.Vk,
                            (nint)(((long)key.Scan << 16) | (key.Extended ? 1L << 24 : 0) | (!key.Down ? 3L << 30 : 0)), out command)
                        : RemoteViewerWindow.TryCreateLowLevelKeyboardCommand(message, key.Vk, key.Scan,
                            (key.Extended ? 1u : 0) | (!key.Down ? 0x80u : 0), out command);
                    if (!parsed) throw new InvalidOperationException("Synthetic native record was rejected.");
                    Keys keyData = (Keys)Invoke(window, "CreateLowLevelKeyData", command)!;
                    allForwarded &= (bool)Invoke(window, "TryForwardKeyboardCommand", command, keyData)!;
                }
                if (scenario.ReleaseOnFocusLoss) Invoke(window, "ReleaseAllRemoteInputs");
                int expected = scenario.Keys.Length + (scenario.ReleaseOnFocusLoss ? 1 : 0);
                await client.FlushInputAsync(deadline.Token);
                for (int attempt = 0; received.Count - start < expected && attempt < 100; attempt++)
                    await Task.Delay(10, deadline.Token);
                var commands = received.ToArray().Skip(start).ToArray();
                var state = new byte[256];
                var translated = new StringBuilder();
                bool recognized = true;
                foreach (var command in commands)
                {
                    NativeKeyboardInput native = InputInjector.CreateNativeKeyboardInput(command);
                    uint scan = (uint)(native.ScanCode | ((native.Flags & 1) != 0 ? 0xE000 : 0));
                    uint vk = native.VirtualKey != 0 ? native.VirtualKey : MapVirtualKeyEx(scan, 3, layout);
                    if (vk is 0 or > 254) { recognized = false; continue; }
                    state[vk] = command.Kind == RemoteInputKind.KeyDown ? (byte)0x80 : (byte)0;
                    state[0x10] = (byte)(state[0xA0] | state[0xA1]);
                    state[0x11] = (byte)(state[0xA2] | state[0xA3]);
                    state[0x12] = (byte)(state[0xA4] | state[0xA5]);
                    if (command.Kind == RemoteInputKind.KeyDown && (vk is 0x31 or 0x41))
                    {
                        var text = new StringBuilder(16);
                        // Flag 4 leaves the OS dead-key state unchanged. The
                        // supplied state array is owned, not GetKeyboardState.
                        int count = ToUnicodeEx(vk, native.ScanCode, state, text, text.Capacity, 4, layout);
                        if (count > 0) translated.Append(text.ToString(0, count));
                    }
                }
                bool released = state.All(value => value == 0);
                bool passed = allForwarded && commands.Length == expected && recognized && released && translated.ToString() == scenario.ExpectedText;
                checks.Add(new { scenario.Name, passed, allForwarded, events = commands.Length, expectedEvents = expected,
                    nativeKeysRecognized = recognized, allReleased = released, text = translated.ToString(), expectedText = scenario.ExpectedText });
                if (!passed) throw new InvalidOperationException("Isolated keyboard scenario failed: " + scenario.Name);
            }
            var routingChecks = await ViewerKeyboardRoutingProbe.RunAsync(window, client, received, deadline.Token);
            checks.AddRange(routingChecks);
            if (routingChecks.Any(check => !check.Passed))
                throw new InvalidOperationException("Isolated viewer keyboard routing checks failed.");
            allPassed = true;
            return 0;
        }
        catch (Exception error)
        {
            allPassed = false;
            failure = error.GetType().Name + ": " + error.Message;
            throw;
        }
        finally
        {
            stopped.TrySetResult();
            bool peerStopped = false;
            try
            {
                await client.DisconnectAsync();
                deadline.Cancel();
                await peerTask.WaitAsync(TimeSpan.FromSeconds(5));
                peerStopped = true;
            }
            catch (Exception error)
            {
                failure ??= error.GetType().Name + ": " + error.Message;
                throw;
            }
            finally
            {
                Program.Save(Path.Combine(output, "keyboard-modifiers.json"), new
                {
                    complete = allPassed && peerStopped, failure, checks,
                    scope = "Private WinForms viewer, synthetic native records, authenticated owned loopback, real user32 translation with synthetic key state; no SendInput, live keyboard capture, user clipboard, remote endpoint or layout changes"
                });
            }
        }
    }

    private static object? Invoke(object instance, string method, params object[] args) =>
        instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(nint desktop);
    [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyExW")] private static extern uint MapVirtualKeyEx(uint code, uint mode, nint layout);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ToUnicodeEx(uint key, uint scan, byte[] state,
        [Out] StringBuilder text, int count, uint flags, nint layout);
}
