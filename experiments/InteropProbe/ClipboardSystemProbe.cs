using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteDesk;

// A private window station owns a separate clipboard. Never open or save the
// user's interactive WinSta0 clipboard for an automated test.
internal static class ClipboardSystemProbe
{
    internal static int Run(bool relayFiles = false)
    {
        using var config = JsonDocument.Parse(Console.ReadLine()!);
        string output = config.RootElement.GetProperty("output").GetString()!;
        Directory.CreateDirectory(output);
        nint station = CreateWindowStation(null, 0, 0x000F037F, 0);
        if (station == 0 || !SetProcessWindowStation(station)) throw new Win32Exception();
        nint desktop = CreateDesktop("Default", null, 0, 0, 0x000F01FF, 0);
        if (desktop == 0) throw new Win32Exception();
        // The process deliberately exits on this private station. Switching its
        // worker threads back would risk a late clipboard call touching WinSta0.
        // The STA entry thread can already own an OLE window. Use a fresh
        // thread with no windows/hooks to attach to the private desktop first.
        int result = 1;
        Exception? failure = null;
        var worker = new Thread(() => {
            try {
                if (!SetThreadDesktop(desktop)) throw new Win32Exception();
                result = (relayFiles ? FileClipboardRelayProbe.RunAsync(output,
                    config.RootElement.GetProperty("expectedServer").GetString()!) : RunAsync(output)).GetAwaiter().GetResult();
            } catch (Exception error) { failure = error; }
        });
        worker.Start();
        worker.Join();
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    private static async Task<int> RunAsync(string output)
    {
        const string sample = "Windows 中文😀\r\n第二行\t缩进\n";
        var checks = new List<object>();
        void Check(string name, bool pass)
        {
            checks.Add(new { name, passed = pass });
            if (!pass) throw new InvalidOperationException(name);
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        await ClipboardTextService.SetTextAsync(sample);
        Check("real Windows clipboard Unicode/newline roundtrip", await ClipboardTextService.GetTextAsync() == sample);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var cases = System.Threading.Channels.Channel.CreateUnbounded<ReadCase>();
        var shutdown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task host = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = peer.GetStream();
            using var writeLock = new SemaphoreSlim(1, 1);
            var auth = await Protocol.AuthenticateServerDetailedAsync(stream, "synthetic-clipboard", timeout.Token);
            using var session = auth.Session!;
            try
            {
                while (!shutdown.Task.IsCompleted)
                {
                    var message = await Protocol.ReadMessageAsync(stream, session, timeout.Token);
                    if (message.Type != MessageType.Control) continue;
                    var control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
                    if (control.Kind != RemoteControlKind.ClipboardGetText) continue;
                    var next = await cases.Reader.ReadAsync(timeout.Token);
                    next.Received.TrySetResult(true);
                    await next.Release.Task.WaitAsync(timeout.Token);
                    await Protocol.WriteMessageAsync(stream, MessageType.Control,
                        RemoteMessageCodec.EncodeClipboardText(next.Text), session, writeLock, timeout.Token);
                }
            }
            catch (Exception error) when (shutdown.Task.IsCompleted && error is IOException or SocketException) { }
        });
        using var viewer = new RemoteViewerClient();
        await viewer.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "synthetic-clipboard", ViewerVideoMode.StableJpeg);
        async Task Read(string reply, string? copiedWhileWaiting, string expected, string name)
        {
            var test = new ReadCase(reply);
            await cases.Writer.WriteAsync(test, timeout.Token);
            var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Status(string text) => done.TrySetResult(text);
            viewer.ClipboardStatusReceived += Status;
            try
            {
                await viewer.ReadRemoteClipboardAsync(notifyRequest: false);
                await test.Received.Task.WaitAsync(timeout.Token);
                if (copiedWhileWaiting != null) await ClipboardTextService.SetTextAsync(copiedWhileWaiting);
                test.Release.TrySetResult(true);
                await done.Task.WaitAsync(timeout.Token);
                Check(name, await ClipboardTextService.GetTextAsync() == expected);
            }
            finally { viewer.ClipboardStatusReceived -= Status; }
        }
        await Read("远端回传😀\n两行", null, "远端回传😀\n两行", "encrypted viewer reply writes real Windows clipboard");
        await Read("", null, "远端回传😀\n两行", "empty remote clipboard does not clear local data");
        await Read("stale remote", "用户新复制的中文", "用户新复制的中文", "in-flight old reply cannot replace a newer local copy");
        shutdown.TrySetResult(true);
        await viewer.DisconnectAsync();
        await host.WaitAsync(timeout.Token);
        Program.Save(Path.Combine(output, "windows-clipboard.json"), new {
            complete = true, scope = "private Windows window station and clipboard; authenticated synthetic loopback peer; no interactive user clipboard access", checks });
        return 0;
    }

    private sealed class ReadCase(string text)
    {
        internal string Text { get; } = text;
        internal TaskCompletionSource<bool> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowStation(string? name, uint flags, uint access, nint security);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessWindowStation(nint station);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateDesktop(string name, string? device, nint devmode, uint flags, uint access, nint security);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(nint desktop);
}
