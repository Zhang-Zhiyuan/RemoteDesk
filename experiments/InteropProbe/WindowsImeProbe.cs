using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteDesk;

// Run only against the owned AndroidLockProbe editor, never an arbitrary user app.
// Credentials, if a host is provided, arrive through the existing stdin config.
internal static class WindowsImeProbe
{
    internal static void Run(JsonElement config, string output)
    {
        if (GetForegroundWindow() == 0) throw new InvalidOperationException("Windows input desktop unavailable");
        using var client = new RemoteViewerClient();
        using var viewer = new RemoteViewerWindow(client, "RemoteDesk Chinese IME test", true, false, false, false, false, true);
        using var log = new StreamWriter(Path.Combine(output, "ime-client.log")) { AutoFlush = true };
        client.Log += line => { lock (log) log.WriteLine(line); };
        viewer.Bounds = new Rectangle(120, 100, 1100, 800);
        var surface = (RemoteViewerWindow.BufferedPictureBox)typeof(RemoteViewerWindow)
            .GetField("_pictureBox", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer)!;
        var commits = new List<string>();
        var productCommit = surface.TextCommitted;
        surface.TextCommitted = (text, epoch) => { commits.Add(text); productCommit?.Invoke(text, epoch); };
        var originalLanguage = InputLanguage.CurrentInputLanguage;
        viewer.Shown += async (_, _) => {
            try
            {
                if (config.TryGetProperty("host", out var host))
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await client.ConnectAsync(host.GetString()!, config.GetProperty("port").GetInt32(),
                        config.GetProperty("password").GetString()!, ViewerVideoMode.Automatic, timeout.Token);
                    await Task.Delay(1500);
                }
                ShowWindow(viewer.Handle, 9);
                ActivateOwned(viewer);
                surface.Focus();
                var chinese = InputLanguage.InstalledInputLanguages.Cast<InputLanguage>()
                    .FirstOrDefault(language => language.Culture.LCID == 0x0804)
                    ?? throw new InvalidOperationException("No Simplified Chinese input method installed");
                InputLanguage.CurrentInputLanguage = chinese;
                await Task.Delay(500);
                nint context = ImmGetContext(surface.Handle);
                if (context == 0) throw new InvalidOperationException("Viewer did not obtain an IME context");
                try {
                    ImmSetOpenStatus(context, true);
                    ImmSetConversionStatus(context, 1 /* native Chinese */, 0);
                } finally { ImmReleaseContext(surface.Handle, context); }
                await Task.Delay(300);
                surface.Focus();
                foreach (char key in "NIHAO") {
                    SendKey(viewer, (ushort)key, false);
                    await Task.Delay(80);
                }
                await Task.Delay(700);
                bool composed = surface.IsImeComposing;
                int commitsBeforeSelection = commits.Count;
                SendKey(viewer, 0x20, false);
                await Task.Delay(700);
                if (client.IsConnected)
                    foreach (char unit in "😀") SendKey(viewer, unit, true);
                await Task.Delay(1500);
                string committed = string.Concat(commits);
                bool passed = composed && commitsBeforeSelection == 0 &&
                    committed == (client.IsConnected ? "你好😀" : "你好");
                Program.Save(Path.Combine(output, "ime.json"), new {
                    passed, composed, commitsBeforeSelection, committed, commits,
                    inputLanguage = chinese.LayoutName,
                    connected = client.IsConnected,
                    surfaceIme = surface.ImeMode.ToString(),
                    scope = "physical NIHAO + Space through local Windows IME; Unicode packet emoji; production viewer handlers"
                });
                if (!passed) throw new InvalidOperationException("IME did not commit the expected test text exactly once");
            }
            catch (Exception error) { File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString()); }
            finally {
                if (!viewer.IsDisposed) InputLanguage.CurrentInputLanguage = originalLanguage;
                await client.DisconnectAsync();
                viewer.Close();
            }
        };
        Application.Run(viewer);
    }

    private static void ActivateOwned(Form viewer)
    {
        uint currentThread = GetCurrentThreadId();
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        bool attached = currentThread != foregroundThread && foregroundThread != 0 &&
            AttachThreadInput(currentThread, foregroundThread, true);
        try { viewer.BringToFront(); viewer.Activate(); SetForegroundWindow(viewer.Handle); }
        finally { if (attached) AttachThreadInput(currentThread, foregroundThread, false); }
    }

    private static void SendKey(Form owner, ushort key, bool unicode)
    {
        if (GetForegroundWindow() != owner.Handle || !owner.ContainsFocus)
            throw new InvalidOperationException($"Owned IME test window lost focus; input aborted (foreground={GetForegroundWindow()}, owner={owner.Handle}, containsFocus={owner.ContainsFocus})");
        var events = new[] {
            new Input { Type = 1, Data = new InputUnion { Keyboard = new Keyboard { VirtualKey = unicode ? (ushort)0 : key, ScanCode = unicode ? key : (ushort)0, Flags = unicode ? 4u : 0u } } },
            new Input { Type = 1, Data = new InputUnion { Keyboard = new Keyboard { VirtualKey = unicode ? (ushort)0 : key, ScanCode = unicode ? key : (ushort)0, Flags = unicode ? 6u : 2u } } }
        };
        if (SendInput(2, events, Marshal.SizeOf<Input>()) != 2)
            throw new InvalidOperationException("Windows rejected synthetic test keystrokes");
    }

    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion {
        [FieldOffset(0)] public Keyboard Keyboard;
        [FieldOffset(0)] public Mouse Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Keyboard { public ushort VirtualKey, ScanCode; public uint Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct Mouse { public int X, Y; public uint Data, Flags, Time; public nuint ExtraInfo; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint first, uint second, bool attach);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("imm32.dll")] private static extern nint ImmGetContext(nint window);
    [DllImport("imm32.dll")] private static extern bool ImmReleaseContext(nint window, nint context);
    [DllImport("imm32.dll")] private static extern bool ImmSetOpenStatus(nint context, bool open);
    [DllImport("imm32.dll")] private static extern bool ImmSetConversionStatus(nint context, uint conversion, uint sentence);
}
