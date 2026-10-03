using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteDesk;

// Real WinForms, authentication and host lifecycle on an owned window station.
// No installed settings, real desktop frames, clipboard, or user input involved.
internal static class RemoteControlNoticeProbe
{
    internal static int Run()
    {
        using var config = JsonDocument.Parse(Console.ReadLine()!);
        string output = Path.GetFullPath(config.RootElement.GetProperty("output").GetString()!);
        Directory.CreateDirectory(output);
        nint station = CreateWindowStation(null, 0, 0x000F037F, 0);
        if (station == 0 || !SetProcessWindowStation(station)) throw new Win32Exception();
        nint desktop = CreateDesktop("Default", null, 0, 0, 0x000F01FF, 0);
        if (desktop == 0) throw new Win32Exception();
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                if (!SetThreadDesktop(desktop)) throw new Win32Exception();
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.EnableVisualStyles();
                Verify(output);
            }
            catch (Exception error) { failure = error; }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start(); worker.Join();
        if (failure is not null) throw failure;
        return 0;
    }

    private static void Verify(string output)
    {
        var checks = new List<object>();
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed });
            Program.Save(Path.Combine(output, "notice.json"), new { checks });
            if (!passed) throw new InvalidOperationException(name);
        }
        using var main = new MainForm(new RemoteDeskSettings(), Path.Combine(output, "logs"));
        main.Show(); Pump(); main.Hide();
        var host = Field<RemoteHostServer>(main, "_hostServer")!;
        using var editor = new Form { Text = "Owned typing target", Size = new Size(800, 500) };
        using var entry = new TextBox { Dock = DockStyle.Fill, Text = "Owned keyboard-focus fixture" };
        editor.Controls.Add(entry);
        editor.Show(); editor.Activate(); entry.Focus(); Pump();
        nint focus = GetFocus();
        Check("fixture owns keyboard focus", focus == entry.Handle);

        // Pause before capture/input starts, retaining the real authenticated
        // network session so a test never captures the user's desktop.
        using var connected = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        host.ClientStatusChanged += status =>
        {
            if (!status.StartsWith("客户端已连接", StringComparison.Ordinal)) return;
            connected.Set();
            release.Wait(TimeSpan.FromSeconds(25));
        };
        Await(host.StartAsync(0, "owned-notice-fixture", 1, 70, 100,
            new ScreenCaptureTarget("fixture", "Owned fixture", new Rectangle(0, 0, 320, 240), true), false));
        try
        {
            using (var tcpProbe = new TcpClient())
            {
                Await(tcpProbe.ConnectAsync(IPAddress.Loopback, host.ListeningPort));
                Pump();
                Check("unauthenticated TCP probe has no notice", !host.HasActiveRemoteControl && Field<RemoteControlNotice>(main, "_remoteControlNotice") is null);
            }
            using var peer = new TcpClient();
            Await(peer.ConnectAsync(IPAddress.Loopback, host.ListeningPort));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var auth = Protocol.AuthenticateClientAsync(peer.GetStream(), "owned-notice-fixture", timeout.Token);
            Await(auth);
            using SecureSession session = auth.Result;
            Wait(() => connected.IsSet && Field<RemoteControlNotice>(main, "_remoteControlNotice")?.Visible == true);
            var notice = Field<RemoteControlNotice>(main, "_remoteControlNotice")!;
            Program.Save(Path.Combine(output, "focus.json"), new { before = focus.ToInt64(), after = GetFocus().ToInt64(),
                active = GetActiveWindow().ToInt64(), main = main.Handle.ToInt64(), editor = editor.Handle.ToInt64(),
                notice = notice.Handle.ToInt64(), button = notice.Controls.OfType<Button>().Single().Handle.ToInt64() });
            Check("authenticated session shows notice while main window is hidden", notice.Visible && !main.Visible);
            Check("showing notice preserves editor focus", GetFocus() == focus);
            Check("notice has no owner and is always on top", notice.Owner is null && (GetWindowLongPtr(notice.Handle, -20).ToInt64() & 0x8) != 0);
            foreach (float fontSize in new[] { 9f, 14f, 24f })
            {
                using var font = new Font("Microsoft YaHei UI", fontSize);
                notice.Font = font;
                foreach (int width in new[] { 320, 800, 1920 })
                {
                    var area = new Rectangle(0, 0, width, 600);
                    notice.ArrangeOnScreen(area); Pump();
                    var label = notice.Controls.OfType<Label>().Single();
                    var button = notice.Controls.OfType<Button>().Single();
                    Check($"layout width={width} font={fontSize}", area.Contains(notice.Bounds) &&
                        notice.ClientRectangle.Contains(label.Bounds) && notice.ClientRectangle.Contains(button.Bounds) &&
                        !label.Bounds.IntersectsWith(button.Bounds));
                    using var bitmap = new Bitmap(notice.Width, notice.Height);
                    notice.DrawToBitmap(bitmap, notice.ClientRectangle);
                    bitmap.Save(Path.Combine(output, $"notice-{width}-{fontSize}.png"));
                }
            }
            var rejection = Protocol.ReadMessageAsync(peer.GetStream(), session, timeout.Token);
            notice.Font = SystemFonts.MessageBoxFont!;
            notice.ArrangeOnScreen(); Pump();
            var disconnect = notice.Controls.OfType<Button>().Single();
            // Check mouse-down focus and dispatch its native BN_CLICKED command.
            // WindowFromPoint cannot hit-test a non-input private desktop, so do
            // not use a global mouse move or switch the user's desktop to test it.
            SendMessage(disconnect.Handle, 0x0201, 1, (nint)(5 | (5 << 16)));
            Check("button mouse-down preserves keyboard focus", GetFocus() == focus);
            SendMessage(notice.Handle, 0x0111, 0, disconnect.Handle);
            Await(rejection);
            var message = RemoteMessageCodec.DecodeControl(rejection.Result.PayloadMemory);
            Check("local disconnect sends explicit no-reconnect rejection", message.Kind == RemoteControlKind.SessionRejected && message.StatusMessage!.Contains("主动断开"));
            release.Set();
            Wait(() => !host.HasActiveRemoteControl && !notice.Visible);
            Check("disconnect clears notice without stopping listener", host.IsRunning && !notice.Visible);
            Check("local mouse click does not steal typing focus", GetFocus() == focus);
        }
        finally { release.Set(); Await(host.StopAsync()); }
        Check("host shutdown leaves no notice", Field<RemoteControlNotice>(main, "_remoteControlNotice")?.Visible != true);
    }

    private static T? Field<T>(object target, string name) where T : class =>
        (T?)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static void Pump() { Application.DoEvents(); Thread.Sleep(10); Application.DoEvents(); }
    private static void Wait(Func<bool> ready)
    {
        var clock = Stopwatch.StartNew();
        while (!ready()) { if (clock.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(); Pump(); }
        Pump();
    }
    private static void Await(Task task) { Wait(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern nint GetActiveWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint CreateWindowStation(string? name, uint flags, uint access, nint security);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetProcessWindowStation(nint station);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint CreateDesktop(string name, string? device, nint devmode, uint flags, uint access, nint security);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(nint desktop);
}
