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
    private static WindowsFormsSynchronizationContext? _uiContext;
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
            release.Wait(TimeSpan.FromSeconds(45));
        };
        Await(host.StartAsync(0, "owned-notice-fixture", 1, 70, 100,
            new ScreenCaptureTarget("fixture", "Owned fixture", new Rectangle(0, 0, 320, 240), true), false));
        try
        {
            using (var tcpProbe = new TcpClient())
            {
                Await(tcpProbe.ConnectAsync(IPAddress.Loopback, host.ListeningPort));
                Pump();
                Check("unauthenticated TCP probe has no notice", !host.HasActiveRemoteControl && Field<RemoteControlNoticeManager>(main, "_remoteControlNotices") is null);
            }
            using var peer = new TcpClient();
            Await(peer.ConnectAsync(IPAddress.Loopback, host.ListeningPort));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var auth = Protocol.AuthenticateClientAsync(peer.GetStream(), "owned-notice-fixture", timeout.Token);
            Await(auth);
            using SecureSession session = auth.Result;
            Wait(() => connected.IsSet && Field<RemoteControlNoticeManager>(main, "_remoteControlNotices")?.Notices.Count > 0);
            var manager = Field<RemoteControlNoticeManager>(main, "_remoteControlNotices")!;
            // Exercise the disconnect button on the non-primary display.
            var secondary = Screen.AllScreens.FirstOrDefault(screen => !screen.Primary) ?? Screen.AllScreens[0];
            var notice = manager.Notices[secondary.DeviceName];
            Program.Save(Path.Combine(output, "focus.json"), new { before = focus.ToInt64(), after = GetFocus().ToInt64(),
                active = GetActiveWindow().ToInt64(), main = main.Handle.ToInt64(), editor = editor.Handle.ToInt64(),
                notice = notice.Handle.ToInt64(), button = notice.Controls.OfType<Button>().Single().Handle.ToInt64() });
            Check("authenticated session shows notice while main window is hidden", notice.Visible && !main.Visible);
            Screen[] monitors = Screen.AllScreens;
            var displayedNotices = Application.OpenForms.OfType<RemoteControlNotice>().Where(window => window.Visible).ToArray();
            Program.Save(Path.Combine(output, "monitor-notices.json"), new
            {
                monitors = monitors.Select(screen => new { screen.DeviceName, screen.Bounds, screen.WorkingArea }),
                notices = displayedNotices.Select(window => new { window.Bounds, window.DeviceDpi })
            });
            Check("every enabled monitor has its own authenticated control notice", displayedNotices.Length == monitors.Length &&
                monitors.All(screen => displayedNotices.Any(window => screen.WorkingArea.Contains(window.Bounds))));
            Check("showing notice preserves editor focus", GetFocus() == focus);
            Check("all notices have no owner and remain topmost", displayedNotices.All(window =>
                window.Owner is null && (GetWindowLongPtr(window.Handle, -20).ToInt64() & 0x8) != 0));
            foreach (RemoteControlNotice window in displayedNotices)
            {
                using var bitmap = new Bitmap(window.Width, window.Height);
                window.DrawToBitmap(bitmap, window.ClientRectangle);
                bitmap.Save(Path.Combine(output, ReferenceEquals(window, notice) ? "secondary-notice.png" : "primary-notice.png"));
            }
            var primary = monitors.First(screen => screen.Primary);
            RemoteControlNotice previousPrimary = manager.Notices[primary.DeviceName];
            previousPrimary.Dispose();
            SendMessage(main.Handle, 0x007E, 0, 0); // Display topology notification, not a real settings change.
            Wait(() => manager.Notices[primary.DeviceName] is { IsDisposed: false, Visible: true });
            Check("hidden launcher refreshes every notice on display-change notification",
                manager.Notices.Count == monitors.Length && !ReferenceEquals(manager.Notices[primary.DeviceName], previousPrimary));
            displayedNotices = manager.Notices.Values.ToArray();
            Check("reconciling displays preserves keyboard focus", GetFocus() == focus);

            VerifyTopology(Check, focus);
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
            Check("disconnecting from either screen disables all disconnect buttons together",
                manager.Notices.Values.All(window => !window.Controls.OfType<Button>().Single().Enabled));
            typeof(MainForm).GetMethod("UpdateRemoteControlNotice", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);
            Check("activity refresh cannot re-enable buttons during disconnect",
                manager.Notices.Values.All(window => !window.Controls.OfType<Button>().Single().Enabled));
            Await(rejection);
            var message = RemoteMessageCodec.DecodeControl(rejection.Result.PayloadMemory);
            Check("local disconnect sends explicit no-reconnect rejection", message.Kind == RemoteControlKind.SessionRejected && message.StatusMessage!.Contains("主动断开"));
            release.Set();
            Wait(() => !host.HasActiveRemoteControl && !notice.Visible);
            Check("disconnect on the secondary removes all notices without stopping listener", host.IsRunning && manager.Notices.Count == 0 && displayedNotices.All(window => window.IsDisposed));
            Check("local mouse click does not steal typing focus", GetFocus() == focus);
        }
        finally { release.Set(); Await(host.StopAsync()); }
        Check("host shutdown leaves no notice", Field<RemoteControlNoticeManager>(main, "_remoteControlNotices")?.Notices.Count == 0);
    }

    private static void VerifyTopology(Action<string, bool> check, nint originalFocus)
    {
        var landscape = new RemoteControlNoticeDisplay("landscape", new Rectangle(0, 0, 1920, 1040));
        var portrait = new RemoteControlNoticeDisplay("portrait", new Rectangle(-1080, -400, 1080, 1880));
        RemoteControlNoticeDisplay[] inventory = [landscape, portrait];
        using var manager = new RemoteControlNoticeManager(() => inventory);
        manager.SetActive(true); Pump();
        RemoteControlNotice first = manager.Notices[landscape.Id], second = manager.Notices[portrait.Id];
        check("simulated horizontal and negative-origin portrait displays each have a notice",
            manager.Notices.Count == 2 && inventory.All(display => display.WorkingArea.Contains(manager.Notices[display.Id].Bounds)));
        first.MoveWithinDisplay(new Point(5000, 4000));
        second.MoveWithinDisplay(new Point(5000, 4000));
        check("dragging cannot leave either monitor without its indicator", landscape.WorkingArea.Contains(first.Bounds) && portrait.WorkingArea.Contains(second.Bounds));
        Rectangle dragged = first.Bounds;
        inventory = [portrait, landscape];
        manager.SetActive(true); Pump();
        check("repeated activity and reordered monitors reuse notices and preserve their positions",
            manager.Notices.Count == 2 && ReferenceEquals(manager.Notices[landscape.Id], first) &&
            ReferenceEquals(manager.Notices[portrait.Id], second) && first.Bounds == dragged);

        Action staleRequest = (Action)typeof(RemoteControlNotice).GetField("DisconnectRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(second)!;
        inventory = [landscape with { WorkingArea = new Rectangle(0, 40, 800, 600) }];
        manager.RefreshDisplays(); Pump();
        check("unplugging a screen disposes only its notice and reflows the survivor",
            second.IsDisposed && manager.Notices.Count == 1 && ReferenceEquals(manager.Notices[landscape.Id], first) && inventory[0].WorkingArea.Contains(first.Bounds));
        int disconnects = 0;
        manager.DisconnectRequested += () => disconnects++;
        manager.SetDisconnecting();
        inventory = [landscape, portrait];
        manager.RefreshDisplays(); Pump();
        check("a hot-plugged monitor inherits the in-progress disconnect state", manager.Notices.Count == 2 &&
            manager.Notices.Values.All(window => !window.Controls.OfType<Button>().Single().Enabled));
        manager.SetDisconnecting(false);
        staleRequest();
        check("removed notice callbacks cannot disconnect a newer screen instance", disconnects == 0);
        manager.Notices[portrait.Id].Controls.OfType<Button>().Single().PerformClick();
        ((Action)typeof(RemoteControlNotice).GetField("DisconnectRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(first)!)();
        check("requests from both indicators produce only one group disconnect", disconnects == 1 &&
            manager.Notices.Values.All(window => !window.Controls.OfType<Button>().Single().Enabled));
        RemoteControlNotice[] beforeDisconnect = manager.Notices.Values.ToArray();
        manager.SetActive(false); Pump();
        check("ending the final session removes every indicator", manager.Notices.Count == 0 && beforeDisconnect.All(window => window.IsDisposed));
        manager.SetDisconnecting(false);
        manager.SetActive(true); Pump();
        check("reconnecting recreates exactly one enabled indicator per display", manager.Notices.Count == 2 &&
            manager.Notices.Values.All(window => window.Visible && window.Controls.OfType<Button>().Single().Enabled));
        check("topology changes never take keyboard focus", GetFocus() == originalFocus);
        RemoteControlNotice[] beforeExit = manager.Notices.Values.ToArray();
        manager.Dispose();
        check("application cleanup disposes all remaining indicator windows", beforeExit.All(window => window.IsDisposed));
    }

    private static T? Field<T>(object target, string name) where T : class =>
        (T?)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static void Pump()
    {
        Application.DoEvents(); Thread.Sleep(10); Application.DoEvents();
        SynchronizationContext.SetSynchronizationContext(_uiContext ??= new WindowsFormsSynchronizationContext());
    }
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
