using System.Security.Cryptography;
using System.Runtime.InteropServices;
using RemoteDesk;

// Opt-in, visible, bounded physical-display test. Temporarily covers the local
// screens, then always restores them. Uses owned colored windows and an ephemeral
// authenticated loopback host. No real input, clipboard, settings or saved frames.
internal static class PrivacyScreenProbe
{
    internal static int Run()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.EnableVisualStyles();
        if (!WindowsPrivacyScreen.IsDefaultInputDesktop()) throw new InvalidOperationException("Unlock the test desktop first.");
        var panes = Screen.AllScreens.Select(screen => new Pattern {
            StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(screen.Bounds.X + 48, screen.Bounds.Y + 48, 240, 160),
            FormBorderStyle = FormBorderStyle.None, BackColor = Color.FromArgb(32, 180, 96), ShowInTaskbar = false, TopMost = true
        }).ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var watchdog = new System.Threading.Timer(_ => Environment.Exit(2), null, 40000, Timeout.Infinite);
        int exitCode = 0;
        panes[0].Shown += async (_, _) =>
        {
            try
            {
                foreach (var pane in panes.Skip(1)) pane.Show();
                await Task.Delay(250, deadline.Token);
                await Verify(panes, deadline.Token);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
            finally { foreach (var pane in panes) pane.Close(); }
        };
        try { Application.Run(panes[0]); }
        finally { foreach (var pane in panes) pane.Dispose(); }
        return exitCode;
    }

    private static async Task Verify(Pattern[] panes, CancellationToken cancellation)
    {
        using var host = new RemoteHostServer();
        using var viewer = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        var target = ScreenCaptureService.GetAvailableTargets().First(screen => screen.IsPrimary && !screen.IsAllScreens);
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        int frames = 0;
        viewer.FrameReceived += _ => Interlocked.Increment(ref frames);
        await host.StartAsync(0, password, 2, 70, 25, target, false);
        try
        {
            async Task Connect()
            {
                await viewer.ConnectAsync("127.0.0.1", host.ListeningPort, password, ViewerVideoMode.StableJpeg, cancellation);
                Require(await viewer.WaitForCurrentDeviceInfoAsync(TimeSpan.FromSeconds(5), cancellation), "device capabilities");
            }
            Task<PrivacyScreenStatus> Request(PrivacyScreenAction action) => viewer.RequestPrivacyScreenAsync(action, viewer.InputConnectionGeneration);
            await Connect();
            Require(viewer.SupportsPrivacyScreen, "authenticated capability negotiated");
            var denied = await Request(PrivacyScreenAction.Enable);
            Require(!denied.Success && !denied.Enabled && !denied.Allowed, "local permission defaults off");
            await host.SetPrivacyScreenAllowedAsync(true);
            var enabled = await Request(PrivacyScreenAction.Enable);
            Require(enabled.Success && enabled.Enabled, "enable acknowledged by actual host and helper");
            await Task.Delay(700, cancellation);
            foreach (var pane in panes)
            {
                Require(WindowsPrivacyScreen.IsDefaultInputDesktop(), "test desktop remained unlocked");
                using var pixel = new Bitmap(1, 1);
                using var graphics = Graphics.FromImage(pixel);
                graphics.CopyFromScreen(pane.Left + 100, pane.Top + 80, 0, 0, new Size(1, 1));
                Require(pixel.GetPixel(0, 0).ToArgb() == pane.BackColor.ToArgb(), "underlying pixels remain available on " + pane.Bounds + "; actual=" + pixel.GetPixel(0,0));
                Require(WindowFromPoint(new Point(pane.Left + 100, pane.Top + 80)) == pane.Handle,
                    "curtain does not intercept pointer hit-testing on " + pane.Bounds);
            }
            int before = Volatile.Read(ref frames);
            await Task.Delay(900, cancellation);
            Require(Volatile.Read(ref frames) > before, "actual encrypted video continues while curtain active");
            long restoreStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            Require(WindowsPrivacyScreen.RequestLocalRestore(), "local viewer emergency-restore path recognizes active curtain");
            Require(System.Diagnostics.Stopwatch.GetElapsedTime(restoreStarted) < TimeSpan.FromMilliseconds(100),
                "keyboard hook restore path does not wait for helper process exit");
            await Task.Delay(700, cancellation);
            Require(!(await Request(PrivacyScreenAction.Query)).Enabled, "local viewer emergency restore removes curtain");
            Require((await Request(PrivacyScreenAction.Enable)).Enabled, "can re-enable after local emergency restore");
            await host.SetPrivacyScreenAllowedAsync(false);
            var revoked = await Request(PrivacyScreenAction.Query);
            Require(!revoked.Enabled && !revoked.Allowed, "host permission revocation restores immediately");
            await host.SetPrivacyScreenAllowedAsync(true);
            Require((await Request(PrivacyScreenAction.Enable)).Enabled, "can enable after permission restored");
            Require(!(await Request(PrivacyScreenAction.Disable)).Enabled, "viewer disable restores");
            Require((await Request(PrivacyScreenAction.Enable)).Enabled, "enable for disconnect test");
            await viewer.DisconnectAsync();
            await Task.Delay(300, cancellation);
            await Connect();
            Require(!(await Request(PrivacyScreenAction.Query)).Enabled, "disconnect restores; reconnect never auto-enables");
        }
        finally { await viewer.DisconnectAsync(); await host.SetPrivacyScreenAllowedAsync(false); await host.StopAsync(); }
    }
    private static void Require(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) throw new InvalidOperationException(name);
    }
    private sealed class Pattern : Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80; return p; } }
    }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
}
