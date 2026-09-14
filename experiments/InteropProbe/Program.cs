using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteDesk;

// Authorized physical-machine probe. Credentials arrive only on stdin. Each
// host is independent of the installed product, with a bounded lifetime.
internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    [STAThread]
    static int Main(string[] args)
    {
        Console.InputEncoding = System.Text.Encoding.UTF8;
        if (args.Length > 0 && args[0] == "layout-isolated") return AdaptiveLayoutProbe.Run();
        if (args.Length > 0 && args[0] == "clipboard-isolated") return ClipboardSystemProbe.Run();
        if (args.Length > 0 && args[0] == "clipboard-shortcuts-isolated") return ClipboardSystemProbe.Run(shortcuts: true);
        if (args.Length > 0 && args[0] == "file-relay-isolated") return ClipboardSystemProbe.Run(relayFiles: true);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        // Surface UI-thread failures as evidence, not an unattended modal dialog.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.EnableVisualStyles();
        using var config = JsonDocument.Parse(Console.ReadLine() ?? throw new Exception("stdin configuration required"));
        var c = config.RootElement;
        var output = Path.GetFullPath(c.GetProperty("output").GetString()!);
        Directory.CreateDirectory(output);
        if (args[0] == "relay-path-quality") return RelayPathQualityProbe.RunAsync(c.Clone(), output).GetAwaiter().GetResult();
        if (args[0] == "relay-windows-update") return RelayWindowsUpdateProbe.RunAsync(c.Clone(), output).GetAwaiter().GetResult();
        if (args[0] == "relay-names") return RelayNamesProbe.RunAsync(c.Clone(), output).GetAwaiter().GetResult();
        if (args[0] == "secure-desktop") return WindowsSecureDesktopProbe.Run(c.Clone(), output);
        if (args[0] == "desktop-status")
        {
            Save(Path.Combine(output, "desktop.json"), new {
                desktop = WindowsInteractiveDesktopProbe.InspectCurrent(),
                foregroundAvailable = GetForegroundWindow() != nint.Zero,
                scope = "Read-only current Windows desktop availability; no capture or input"
            });
            return 0;
        }
        if (args[0] == "startup-status")
        {
            Save(Path.Combine(output, "status.json"), new
            {
                persistent = WindowsPersistentStartup.GetStatus(),
                startup = StartupService.GetStatus(),
                elevated = WindowsProcessElevation.IsCurrentProcessElevated()
            });
            return 0;
        }
        if (args[0] == "relay-login")
        {
            string server = c.GetProperty("serverAddress").GetString()!;
            if (server is not ("8.138.5.232" or "127.0.0.1")) throw new InvalidOperationException("Unexpected authorized server");
            try
            {
                RelayProvisionResult result = RelayAdminLogin.LoginAsync(new(server, c.GetProperty("sshPort").GetInt32(),
                    "root", c.GetProperty("password").GetString()!, 56567,
                    c.TryGetProperty("sshIdentity", out var identity) ? identity.GetString() : null)).GetAwaiter().GetResult();
                Save(Path.Combine(output, "login.json"), new {complete=true, server, port=result.RelayPort, installed=result.Installed,
                    scope="Read-only administrator login and pinned directory; no deployment or real desktop input"});
                return 0;
            }
            catch (Exception error)
            {
                Save(Path.Combine(output, "login.json"), new {complete=false, failureType=error.GetType().Name, failure=error.Message});
                return 1;
            }
        }
        if (args[0] == "transport") return RelayThroughputProbe.RunAsync(c.Clone(), output).GetAwaiter().GetResult();
        if (args[0] == "features") return FeatureAuditProbe.RunAsync(c.Clone(), output).GetAwaiter().GetResult();
        if (args[0] == "android-lock") return AndroidLockContinuityProbe.RunAsync(c.Clone(), output).GetAwaiter().GetResult();
        var cursor = Cursor.Position;
        // Hardware Present correctly reports occlusion when the local monitor
        // sleeps. Hold only this test thread's display request, never change
        // the user's power plan, and release it even if the probe fails.
        uint previousExecutionState = SetThreadExecutionState(0x80000003);
        try
        {
            if (args[0] == "ime") WindowsImeProbe.Run(c.Clone(), output);
            else if (args[0] == "host") Application.Run(new Target(c.Clone(), output));
            else RunViewer(c.Clone(), output);
            return File.Exists(Path.Combine(output, "failure.txt")) ? 1 : 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString());
            Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
            return 1;
        }
        finally {
            Cursor.Position = cursor;
            if (previousExecutionState != 0) SetThreadExecutionState(previousExecutionState);
        }
    }

    internal static void Save(string path, object value)
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Json));
        File.Move(path + ".tmp", path, true);
    }

    internal static RelayConnectionOptions? RelayOptions(JsonElement config)
    {
        if (!config.TryGetProperty("relay", out var value)) return null;
        return new RelayConnectionOptions(value.GetProperty("serverAddress").GetString()!, value.GetProperty("port").GetInt32(),
            value.GetProperty("accessToken").GetString()!, value.GetProperty("tlsCertificateSha256").GetString()!,
            value.GetProperty("deviceId").GetString()!).Validate();
    }

    static object? Field(object instance, string name) => instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);

    static void RunViewer(JsonElement c, string output)
    {
        if (GetForegroundWindow() == nint.Zero)
            throw new InvalidOperationException("Interactive Windows input desktop is unavailable; unlock/restore the test desktop before a visible-rendering test.");
        using var client = new RemoteViewerClient();
        // Automated input is sent explicitly through the product client below.
        // Never forward incidental keys/mouse from this workstation as well.
        using var viewer = new RemoteViewerWindow(client, "RemoteDesk isolated interoperability test",
            false, false, false, false, false, c.GetProperty("android").GetBoolean());
        viewer.StartPosition = FormStartPosition.Manual;
        viewer.Bounds = new Rectangle(100, 100, 1200, 850);
        // Keep the owned, non-input-forwarding test surface uncovered even when
        // Windows refuses foreground activation by a background CLI parent.
        viewer.TopMost = true;
        using var log = new StreamWriter(Path.Combine(output, "client.log")) { AutoFlush = true };
        var logLock = new object();
        log.WriteLine("PROBE viewer created");
        viewer.HandleCreated += (_, _) => { lock (logLock) log.WriteLine("PROBE viewer handle created"); };
        viewer.Load += (_, _) => { lock (logLock) log.WriteLine("PROBE viewer Load"); };
        client.Log += line => { lock (logLock) log.WriteLine(line); };
        long frames = 0, bytes = 0;
        int width = 0, height = 0;
        var clock = Stopwatch.StartNew();
        using var displayTimer = new System.Windows.Forms.Timer { Interval = 30 };
        var picture = (PictureBox)Field(viewer, "_pictureBox")!;
        Image? lastDisplayedImage = null;
        int renderedImageChanges = 0;
        Image? lastPaintedImage = null;
        int paintedImageChanges = 0;
        picture.Paint += (_, _) => {
            if (picture.Image is { } painted && !ReferenceEquals(lastPaintedImage, painted)) {
                lastPaintedImage = painted; paintedImageChanges++;
            }
        };
        displayTimer.Tick += (_, _) => {
            if (picture.Image is { } displayed && !ReferenceEquals(lastDisplayedImage, displayed)) {
                lastDisplayedImage = displayed; renderedImageChanges++;
            }
        };
        double firstMs = -1;
        client.FrameReceived += frame => {
            Interlocked.Increment(ref frames); Interlocked.Add(ref bytes, frame.EncodedLength);
            width = frame.Width; height = frame.Height;
            if (firstMs < 0) firstMs = clock.Elapsed.TotalMilliseconds;
        };
        viewer.Shown += async (_, _) => {
            lock (logLock) log.WriteLine("PROBE viewer Shown; beginning connection");
            // The invoking terminal may start hidden. Explicitly show only this
            // owned test window so inherited startup flags cannot mask rendering.
            ShowWindow(viewer.Handle, 9 /* SW_RESTORE */);
            viewer.Activate();
            displayTimer.Start();
            try
            {
                var relay = RelayOptions(c);
                using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(relay == null ? 12 : 45));
                if (relay == null)
                    await client.ConnectAsync(c.GetProperty("host").GetString()!, c.GetProperty("port").GetInt32(),
                        c.GetProperty("password").GetString()!, ViewerVideoMode.Automatic, connectTimeout.Token);
                else
                    await client.ConnectViaRelayAsync(relay, c.GetProperty("password").GetString()!,
                        ViewerVideoMode.Automatic, connectTimeout.Token);
                var deadline = Stopwatch.StartNew();
                while (frames < 10 && deadline.Elapsed.TotalSeconds < (relay == null ? 15 : 60)) await Task.Delay(100);
                if (frames < 10) throw new Exception("No continuous remote frames");
                if (c.TryGetProperty("inputDelaySeconds", out var inputDelay))
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(inputDelay.GetDouble(), 0, 15)));
                // Coordinates describe only the owned target, normalized to its
                // actual physical screen; the product maps frame -> host input.
                async Task Click(string key)
                {
                    var point = c.GetProperty(key);
                    int x = (int)Math.Round(point[0].GetDouble() * (width - 1));
                    int y = (int)Math.Round(point[1].GetDouble() * (height - 1));
                    await client.SendInputAsync(new(RemoteInputKind.MouseMove, RemoteMouseButton.None, x, y, 0));
                    await client.SendInputAsync(new(RemoteInputKind.MouseDown, RemoteMouseButton.Left, x, y, 0));
                    await client.SendInputAsync(new(RemoteInputKind.MouseUp, RemoteMouseButton.Left, x, y, 0));
                    await Task.Delay(400);
                }
                await Click("button"); await Click("editor");
                var sent = client.SendTextInput(c.GetProperty("text").GetString()!);
                // Require progress after startup: a dozen JPEG warm-up paints
                // must not qualify a later H.264 path that stays occluded.
                await Task.Delay(2000);
                var initialTelemetry = viewer.CollectRenderTelemetrySnapshot();
                int initialPainted = paintedImageChanges;
                await Task.Delay(TimeSpan.FromSeconds(relay == null ? 15 : 30));
                var telemetry = viewer.CollectRenderTelemetrySnapshot();
                var encoding = Field(viewer, "_lastRenderedEncoding")?.ToString();
                // WM_TIMER is low priority and can be starved even while WM_PAINT
                // keeps presenting new bitmaps. Count actual paint completions,
                // not a timer's occasional observations of PictureBox.Image.
                long steadyHardwarePresented = telemetry.DirectHardwarePresentedFrames - initialTelemetry.DirectHardwarePresentedFrames;
                int steadyPainted = paintedImageChanges - initialPainted;
                bool continuouslyRendered = steadyHardwarePresented >= 10 || steadyPainted >= 10;
                Save(Path.Combine(output, "viewer.json"), new {
                    complete = client.IsConnected && encoding != null && frames >= 30 && continuouslyRendered,
                    frames, width, height, firstMs, bytes, encoding, sent, telemetry, renderedImageChanges, paintedImageChanges,
                    steadyHardwarePresented, steadyPainted, windowVisible = IsWindowVisible(viewer.Handle),
                    windowState = viewer.WindowState.ToString(),
                    pictureVisible = picture.Visible, pictureWindowVisible = IsWindowVisible(picture.Handle),
                    pictureBounds = picture.RectangleToScreen(picture.ClientRectangle).ToString(),
                    viewerBounds = viewer.Bounds.ToString(), monitor = Screen.FromControl(viewer).Bounds.ToString(),
                    foregroundOwned = GetAncestor(GetForegroundWindow(), 2) == viewer.Handle,
                    status = ((Control)Field(viewer, "_statusBar")!).Text,
                    route = relay == null ? "direct LAN" : "native public relay TLS/TCP (no UDP/LAN fallback)",
                    manualInputForwarding = false,
                    scope = "production Windows client and WinForms/D3D renderer; OS input checked separately"
                });
                if (picture.Image is { } finalBitmap) finalBitmap.Save(Path.Combine(output, "rendered.png"), System.Drawing.Imaging.ImageFormat.Png);
                if (GetAncestor(GetForegroundWindow(), 2) == viewer.Handle) {
                    using var surface = new Bitmap(viewer.ClientSize.Width, viewer.ClientSize.Height);
                    using var graphics = Graphics.FromImage(surface);
                    graphics.CopyFromScreen(viewer.PointToScreen(Point.Empty), Point.Empty, surface.Size);
                    surface.Save(Path.Combine(output, "visible-surface.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                if (!client.IsConnected || encoding == null || frames < 30 || !continuouslyRendered) throw new Exception("Viewer continuous rendering not established");
            }
            catch (Exception error) { File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString()); }
            finally { displayTimer.Stop(); await client.DisconnectAsync(); viewer.Close(); }
        };
        Application.Run(viewer);
    }

    [DllImport("user32.dll")]
    static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(nint window);
    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);
    [DllImport("user32.dll")]
    static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    static extern nint GetAncestor(nint window, uint flags);

    sealed class Target : Form
    {
        readonly RemoteHostServer host = new();
        readonly RelayHostConnector relay = new();
        readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
        readonly Stopwatch lifetime = Stopwatch.StartNew();
        readonly Button button = new() { Bounds = new Rectangle(100, 350, 700, 150), Text = "Remote click target: 0" };
        readonly TextBox editor = new() { Bounds = new Rectangle(100, 600, 900, 120), Font = new Font("Segoe UI", 32) };
        readonly JsonElement config;
        readonly string output;
        readonly ScreenCaptureTarget target;
        readonly StreamWriter log;
        int clicks, tick;
        bool closing, foregroundGuardArmed;
        [DllImport("user32.dll")]
        static extern nint GetForegroundWindow();
        [DllImport("user32.dll")]
        static extern nint GetAncestor(nint window, uint flags);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachInput);
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(nint window);
        bool OwnsForeground => Visible && WindowState != FormWindowState.Minimized &&
            GetAncestor(GetForegroundWindow(), 2 /* GA_ROOT */) == Handle;
        void ActivateInitialFixture()
        {
            // Same bounded initial-focus procedure used by the repository's
            // physical keyboard fixture. No synthetic keys/clicks are needed.
            uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            uint thisThread = GetCurrentThreadId();
            bool attached = foregroundThread != 0 && foregroundThread != thisThread &&
                AttachThreadInput(thisThread, foregroundThread, true);
            try { BringToFront(); Activate(); SetForegroundWindow(Handle); }
            finally { if (attached) AttachThreadInput(thisThread, foregroundThread, false); }
        }
        void CheckForeground()
        {
            if (!foregroundGuardArmed || closing || OwnsForeground) return;
            GetWindowThreadProcessId(GetForegroundWindow(), out uint foregroundPid);
            string foregroundProcess = "unavailable";
            try { if (foregroundPid != 0) { using var process = Process.GetProcessById((int)foregroundPid); foregroundProcess = process.ProcessName; } }
            catch (Exception error) when (error is ArgumentException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
            try { File.WriteAllText(Path.Combine(output, "failure.txt"),
                "Safety stop: owned Windows input target lost foreground. No automatic refocus/retry. " +
                $"Foreground process: {foregroundProcess} (PID {foregroundPid}); pointer={Cursor.Position}; " +
                $"ownedBounds={Bounds}; expectedButton={button.RectangleToScreen(button.ClientRectangle)}; " +
                $"desktop={WindowsInteractiveDesktopProbe.InspectCurrent()}."); }
            finally { Close(); }
        }
        public Target(JsonElement config, string output)
        {
            this.config = config; this.output = output;
            target = ScreenCaptureService.GetAvailableTargets().First(t => t.IsPrimary && !t.IsAllScreens);
            AutoScaleMode = AutoScaleMode.None; FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual; Bounds = target.Bounds;
            TopMost = true; DoubleBuffered = true;
            Controls.AddRange([button, editor]);
            button.Click += (_, _) => { clicks++; button.Text = $"Remote click target: {clicks}"; Snapshot(); };
            editor.TextChanged += (_, _) => Snapshot();
            log = new StreamWriter(Path.Combine(output, "host.log")) { AutoFlush = true };
            host.Log += line => { lock (log) log.WriteLine(line); };
            relay.StatusChanged += line => { lock (log) log.WriteLine(line); };
            Deactivate += (_, _) => CheckForeground();
            timer.Tick += (_, _) => {
                CheckForeground();
                if (closing) return;
                tick++; Invalidate(new Rectangle(100, 850, 1400, 200)); Snapshot();
                if (lifetime.Elapsed.TotalSeconds > 1800 || File.Exists(Path.Combine(output, "stop"))) Close();
            };
            Shown += async (_, _) => {
                try {
                    // Do not start capturing an unrelated foreground window.
                    // Initial activation belongs to launching this interactive
                    // fixture; never steal focus again once testing has begun.
                    ActivateInitialFixture();
                    var focusDeadline = Stopwatch.StartNew();
                    while (!OwnsForeground && !closing && focusDeadline.Elapsed.TotalSeconds < 60) await Task.Delay(100);
                    if (closing) return;
                    if (!OwnsForeground) throw new Exception("Owned Windows input target is not foreground");
                    foregroundGuardArmed = true;
                    // Physical primary monitor at 50% on this 4K test machine.
                    await host.StartAsync(config.GetProperty("port").GetInt32(), config.GetProperty("password").GetString()!,
                        30, 90, config.GetProperty("scale").GetInt32(), target, false);
                    if (RelayOptions(config) is { } options) await relay.StartAsync(options, config.GetProperty("port").GetInt32());
                    timer.Start(); Snapshot();
                    Console.WriteLine("HOST_READY");
                } catch (Exception e) { File.WriteAllText(Path.Combine(output, "failure.txt"), e.ToString()); Close(); }
            };
            FormClosing += async (_, e) => {
                if (closing) return;
                e.Cancel = true; closing = true; timer.Stop();
                await Task.WhenAll(host.StopAsync(), relay.StopAsync());
                relay.Dispose(); host.Dispose(); log.Dispose(); Close();
            };
        }
        void Snapshot() => Save(Path.Combine(output, "target.json"), new {
            clicks, text = editor.Text, foregroundOwned = OwnsForeground,
            screenWidth = target.Bounds.Width, screenHeight = target.Bounds.Height,
            button = new[] { 450d / (target.Bounds.Width - 1), 425d / (target.Bounds.Height - 1) },
            editor = new[] { 450d / (target.Bounds.Width - 1), 630d / (target.Bounds.Height - 1) }
        });
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(21, 32, 48));
            e.Graphics.FillRectangle(Brushes.White, 0, 0, Width / 2, Height);
            using var font = new Font("Segoe UI", 34);
            e.Graphics.DrawString("RemoteDesk owned Windows target", font, Brushes.Navy, 100, 130);
            e.Graphics.FillRectangle(Brushes.RoyalBlue, 100 + tick * 13 % 1000, 850, 100, 130);
            base.OnPaint(e);
        }
    }
}
