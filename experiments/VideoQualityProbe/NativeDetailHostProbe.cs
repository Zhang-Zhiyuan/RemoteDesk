using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteDesk;
using RemoteDesk.NativeDetail;

// Actual RemoteHostServer, desktop duplication, hardware encoding, encrypted
// loopback (or an explicitly selected public relay), production receive/decode/present and the UI switch. The source
// monitor is completely covered by an owned fixture; the viewer uses another
// monitor. Only an owned input field receives test characters; installed
// host/settings, real relay directory entries and the clipboard are unchanged.
internal static class NativeDetailHostProbe
{
    internal static void Run(string fixtures, string outputDirectory, string? expectedRelay = null)
    {
        string output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output)) throw new IOException("Preserving existing host evidence.");
        Directory.CreateDirectory(output);
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { RunCore(fixtures, output, expectedRelay); }
            catch (Exception ex)
            {
                error = ex;
                if (!File.Exists(Path.Combine(output, "summary.json")))
                    File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { passed = false, failure = ex.ToString() }));
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(200))) throw new TimeoutException("Owned host probe did not close.");
        if (error is not null) throw new IOException("Production host probe failed; see summary.json.", error);
    }

    private static void RunCore(string fixtures, string output, string? expectedRelay)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        WindowsFormsSynchronizationContext.AutoInstall = false;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var token = cancel.Token;
        Screen[] screens = Screen.AllScreens;
        Screen viewerScreen = screens.FirstOrDefault(screen => !screen.Primary)
            ?? throw new IOException("Two monitors required to isolate the source and real viewer.");
        Screen sourceScreen = screens.First(screen => screen.Primary);
        using var original = new Bitmap(Path.Combine(fixtures, "native-reference.png"));
        if (sourceScreen.Bounds.Width > original.Width || sourceScreen.Bounds.Height > original.Height)
            throw new IOException("Fixture does not cover the source monitor.");
        using var source = new Canvas(original)
        {
            AutoScaleMode = AutoScaleMode.None, FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual, Location = sourceScreen.Bounds.Location,
            ClientSize = sourceScreen.Bounds.Size, TopMost = true, ShowInTaskbar = false
        };
        source.Show(); Application.DoEvents();
        if (source.RectangleToScreen(source.ClientRectangle) != sourceScreen.Bounds)
            throw new IOException("Source does not completely cover its monitor.");
        using var host = new RemoteHostServer();
        using var client = new RemoteViewerClient();
        using var connector = expectedRelay is null ? null : new RelayHostConnector(addressProvider: () => []);
        RelayConnectionOptions? relay = expectedRelay is null ? null : ReadRelayOptions(expectedRelay);
        var logs = new ConcurrentQueue<string>();
        host.Log += line => { logs.Enqueue("HOST " + line); Console.WriteLine(line); };
        client.Log += line => logs.Enqueue("CLIENT " + line);
        using var viewer = new RemoteViewerWindow(client, "RemoteDesk 正式被控链路测试（自动关闭）",
            inputEnabled: false, clipboardTextEnabled: false, filePasteEnabled: false,
            fileDropPasteEnabled: false, remoteFilePullEnabled: false, isAndroidRemote: false)
        {
            StartPosition = FormStartPosition.Manual, Location = new(viewerScreen.Bounds.X + 20, viewerScreen.Bounds.Y + 20),
            ShowInTaskbar = false
        };
        string secret = Guid.NewGuid().ToString("N");
        int nativeBases = 0, legacyBases = 0;
        long nativeDraws = 0;
        client.FrameReceived += frame =>
        {
            if (frame.NativeDetails is null) Interlocked.Increment(ref legacyBases);
            else Interlocked.Increment(ref nativeBases);
        };
        string phase = "startup", failure = "";
        var checks = new List<string>();
        var inputMeasurements = new Dictionary<string, double[]>();
        var renderMeasurements = new Dictionary<string, object>();
        long nextProgress = 0;
        long nextCoverageCheck = 0;
        Point sourceCenter = new(sourceScreen.Bounds.X + sourceScreen.Bounds.Width / 2, sourceScreen.Bounds.Y + sourceScreen.Bounds.Height / 2);
        using var pacing = new WindowsHighResolutionPacingWaiter();
        D3D11HwndVideoPresenter? Presenter() => typeof(RemoteViewerWindow)
            .GetField("_d3d11VideoPresenter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer) as D3D11HwndVideoPresenter;
        void Pump(Func<bool> condition)
        {
            while (!condition())
            {
                token.ThrowIfCancellationRequested();
                if (source.IsDisposed || source.RectangleToScreen(source.ClientRectangle) != sourceScreen.Bounds)
                    throw new IOException("Fixture coverage changed; stop capture.");
                if (Environment.TickCount64 >= nextCoverageCheck)
                {
                    nextCoverageCheck = Environment.TickCount64 + 100;
                    if (WindowFromPoint(sourceCenter) != source.Handle)
                        throw new IOException("Owned source became occluded; refusing to test another application.");
                }
                if (Environment.TickCount64 >= nextProgress)
                {
                    nextProgress = Environment.TickCount64 + 2000;
                    Console.WriteLine($"{phase}: legacy={legacyBases}, native={nativeBases}, applied={client.NativeDetails.AppliedChunks}, " +
                        $"presented={client.NativeDetails.LastPresentedSequence}, draws={nativeDraws}, detail={Presenter()?.NativeDetailStatus}");
                }
                if (Presenter() is { } p) p.NativeDetailAfterDrawForTests = () => Interlocked.Increment(ref nativeDraws);
                Application.DoEvents(); pacing.Wait(TimeSpan.FromMilliseconds(1), token);
            }
            Application.DoEvents();
        }
        void Await(Task task) { Pump(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        void MeasureInput(string label, bool enhanced)
        {
            phase = label;
            var measurements = new List<double>();
            for (int i = 0; i < 12; i++)
            {
                FocusOwnedInput(source); Application.DoEvents();
                if (GetForegroundWindow() != source.Handle || !source.InputTarget.Focused)
                    throw new IOException("Owned input target is not foreground; refusing to send test text.");
                long priorDraws = nativeDraws;
                string text = "中文" + (char)('甲' + i), expected = source.InputTarget.Text + text;
                long beganAt = Stopwatch.GetTimestamp();
                long appliedAt = 0;
                void Applied(object? sender, EventArgs args)
                { if (source.InputTarget.Text == expected) appliedAt = Stopwatch.GetTimestamp(); }
                source.InputTarget.TextChanged += Applied;
                try
                {
                    var sent = client.SendTextInput(text);
                    if (sent.SentCodePoints != 3) throw new IOException("Real input queue refused Chinese test text.");
                    if (enhanced && client.NativeDetails.IsEnabled) throw new IOException("Text input left stale detail active.");
                    Pump(() => appliedAt != 0);
                    measurements.Add(Stopwatch.GetElapsedTime(beganAt, appliedAt).TotalMilliseconds);
                }
                finally { source.InputTarget.TextChanged -= Applied; }
                if (enhanced) Pump(() => client.NativeDetails.IsEnabled && nativeDraws >= priorDraws + 2);
                else { long idleAt = Environment.TickCount64; Pump(() => Environment.TickCount64 - idleAt >= 400); }
            }
            inputMeasurements[label] = measurements.ToArray();
            renderMeasurements[label] = viewer.CollectRenderTelemetrySnapshot(includeLatencyDistributions: true);
            checks.Add($"{label}: {measurements.Count} real Chinese input samples, queue-to-owned-TextChanged median " +
                $"{measurements.Order().ElementAt(measurements.Count / 2):F2} ms, max {measurements.Max():F2} ms (not glass latency)");
        }
        try
        {
            viewer.Show(); Application.DoEvents();
            viewer.ClientSize = new(Math.Min(2600, viewerScreen.Bounds.Width - 60), Math.Min(1500, viewerScreen.Bounds.Height - 100));
            viewer.Location = new(viewerScreen.Bounds.X + 20, viewerScreen.Bounds.Y + 20);
            Application.DoEvents();
            SetWindowPos(source.Handle, new nint(-1), 0, 0, 0, 0, 0x53); // show/topmost, no move/resize/activation
            Application.DoEvents();
            nint covering = WindowFromPoint(sourceCenter);
            if (covering != source.Handle)
            {
                GetWindowThreadProcessId(covering, out uint pid);
                var className = new System.Text.StringBuilder(256); GetClassName(covering, className, className.Capacity);
                string process = pid == 0 ? "none" : Process.GetProcessById((int)pid).ProcessName;
                throw new IOException($"Source is occluded before test: canvas={source.Bounds}, visible={source.Visible}, viewer={viewer.Bounds}, " +
                    $"topmost={source.TopMost}, covering process={process}, class={className}, own={pid == Environment.ProcessId}.");
            }
            Await(host.StartAsync(0, secret, 30, 85, 50,
                new(sourceScreen.DeviceName, "Owned full-monitor text fixture", sourceScreen.Bounds), adaptiveQuality: false));
            if (host.ListeningPort <= 0) throw new IOException("Host did not expose its owned ephemeral port.");
            if (connector is not null && relay is not null)
            {
                phase = "register isolated relay fixture";
                Await(connector.StartAsync(relay, host.ListeningPort, token));
                Await(WaitForRelayFixture(relay, token));
                Await(client.ConnectViaRelayAsync(relay, secret, ViewerVideoMode.Automatic, token));
            }
            else Await(client.ConnectAsync("127.0.0.1", host.ListeningPort, secret, ViewerVideoMode.Automatic, token));
            Pump(() => legacyBases >= 15 && client.NativeOffer is { Available: true });
            if (nativeBases != 0 || client.NativeDetails.IsEnabled) throw new IOException("Off-by-default feature changed base negotiation.");
            checks.Add("ordinary default session delivers frames; no native activation");
            MeasureInput("ordinary input", enhanced: false);
            phase = "native via actual UI toggle";
            Await(viewer.ToggleNativeDetailsAsync());
            Pump(() => client.NativeDetails.AppliedChunks >= 3 && nativeDraws >= 10);
            checks.Add("actual UI -> host request -> hardware source -> native encrypted base/chunks -> real GPU overlay");
            phase = "steady native / real receiver pixel validation";
            long stableAt = Environment.TickCount64, steadyDraws = nativeDraws;
            Pump(() => Environment.TickCount64 - stableAt >= 12_000);
            if (nativeDraws < steadyDraws + 120) throw new IOException("Native detail does not stay visible during a static desktop.");
            int exact = ValidateReceivedPixels(client, original, output);
            if (exact < 3) throw new IOException("Insufficient exact native patches reached the production receiver.");
            checks.Add($"12-second steady run, {nativeDraws - steadyDraws} native GPU draws, {exact} received patches match original RGBA exactly");
            MeasureInput("native input", enhanced: true);
            phase = "input cutoff";
            long applied = client.NativeDetails.AppliedChunks;
            // A release with no associated down is harmless and exercises the
            // real input queue/host without typing into any user application.
            Await(client.SendInputAsync(RemoteInputCommand.KeyUp(0x87))); // F24
            if (client.NativeDetails.IsEnabled) throw new IOException("Input did not invalidate old detail synchronously.");
            Pump(() => client.NativeDetails.IsEnabled && client.NativeDetails.AppliedChunks > applied);
            checks.Add("input invalidates immediately; UI idle timer sends a fresh request and resumes");
            phase = "off / normal capture recovery";
            int legacyAtOff = legacyBases;
            Await(viewer.ToggleNativeDetailsAsync());
            Pump(() => legacyBases >= legacyAtOff + 12);
            if (expectedRelay is null)
                Pump(() => logs.Any(line => line.Contains("UDP 低延迟视频已恢复", StringComparison.Ordinal)));
            if (client.NativeDetails.IsEnabled) throw new IOException("Explicit off left detail active.");
            checks.Add("explicit off recovers existing capture with the same authenticated connection");
            phase = "re-enable";
            long priorDraws = nativeDraws;
            Await(viewer.ToggleNativeDetailsAsync());
            Pump(() => nativeDraws >= priorDraws + 10);
            checks.Add("re-enable after backend teardown uses advancing source identity");
            phase = "second off / resumed input";
            legacyAtOff = legacyBases;
            Await(viewer.ToggleNativeDetailsAsync());
            Pump(() => legacyBases >= legacyAtOff + 12);
            if (expectedRelay is null)
                Pump(() => logs.Count(line => line.Contains("UDP 低延迟视频已恢复", StringComparison.Ordinal)) >= 2);
            if (client.NativeDetails.IsEnabled) throw new IOException("Second off was undone by late initialization.");
            MeasureInput("restored input", enhanced: false);
            checks.Add("second off restores ordinary capture/UDP without reconnect; real Chinese input still applies");
            Console.WriteLine("PASS production host: " + string.Join("; ", checks));
        }
        catch (Exception ex) { failure = ex.ToString(); throw; }
        finally
        {
            cancel.Cancel();
            viewer.Close();
            client.DisconnectAsync().GetAwaiter().GetResult();
            connector?.StopAsync().GetAwaiter().GetResult();
            host.StopAsync().GetAwaiter().GetResult();
            source.Close();
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new
            {
                passed = failure.Length == 0, failure, phase, nativeBases, legacyBases, nativeDraws, relayServer = expectedRelay,
                applied = client.NativeDetails.AppliedChunks, source = sourceScreen.Bounds, viewer = viewerScreen.Bounds,
                checks, inputMeasurements, renderMeasurements, logs = logs.ToArray(), telemetry = viewer.CollectRenderTelemetrySnapshot(includeLatencyDistributions: true),
                scope = expectedRelay is null
                    ? "Real Windows host/source/hardware encoder/client/UI/GPU compositor over encrypted loopback, isolated fixture. Not WAN or glass-to-glass latency proof."
                    : "Real Windows host/source/client/UI/GPU compositor through the specified public TLS relay. Isolated temporary node; no installed endpoint updated. Not glass-to-glass latency proof."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static RelayConnectionOptions ReadRelayOptions(string expectedServer)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteDesk", "settings.json");
        RelaySettings relay = JsonSerializer.Deserialize<RemoteDeskSettings>(File.ReadAllText(path))?.Relay
            ?? throw new IOException("No saved relay configuration.");
        if (!string.Equals(relay.ServerAddress, expectedServer, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Saved relay differs from the explicitly requested server.");
        return new RelayConnectionOptions(relay.ServerAddress!, relay.RelayPort,
            AppSettingsService.UnprotectSecret(relay.ProtectedAccessToken), relay.TlsCertificateSha256 ?? "", Guid.NewGuid().ToString()).Validate();
    }

    private static async Task WaitForRelayFixture(RelayConnectionOptions options, CancellationToken token)
    {
        while (!(await RelayTunnelClient.ListDevicesAsync(options, token)).Any(device => device.DeviceId == options.DeviceId))
            await Task.Delay(200, token);
    }

    private sealed class Canvas : Form
    {
        private readonly Bitmap _bitmap;
        internal TextBox InputTarget { get; } = new() { Bounds = new(32, 32, 600, 40), Multiline = true, TabStop = true };
        internal Canvas(Bitmap bitmap) { _bitmap = bitmap; Controls.Add(InputTarget); }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            Rectangle clip = Rectangle.Intersect(ClientRectangle, e.ClipRectangle);
            e.Graphics.DrawImage(_bitmap, clip, clip, GraphicsUnit.Pixel);
        }
    }

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint current, uint foreground, bool attach);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, System.Text.StringBuilder name, int count);

    private static void FocusOwnedInput(Canvas source)
    {
        uint foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _), current = GetCurrentThreadId();
        bool attached = foreground != 0 && foreground != current && AttachThreadInput(current, foreground, true);
        try
        {
            source.BringToFront(); source.Activate(); SetForegroundWindow(source.Handle);
            source.InputTarget.Select(); source.InputTarget.Focus();
        }
        finally { if (attached) AttachThreadInput(current, foreground, false); }
    }

    private static int ValidateReceivedPixels(RemoteViewerClient client, Bitmap reference, string output)
    {
        object gate = typeof(NativeDetailViewerSession).GetField("_cacheGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client.NativeDetails)!;
        DetailPatch[] patches;
        lock (gate)
        {
            var cache = (DetailCache)typeof(NativeDetailViewerSession).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client.NativeDetails)!;
            patches = cache.Patches.ToArray();
        }
        foreach (var patch in patches)
        {
            var rect = patch.Rect;
            using var crop = reference.Clone(new Rectangle(rect.X, rect.Y, rect.Width, rect.Height), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            if (!NativeDetailProbe.Rgba(crop).AsSpan().SequenceEqual(patch.Rgba.Span))
            {
                crop.Save(Path.Combine(output, "mismatch-expected.png"));
                using var actual = NativeDetailProbe.BitmapFromRgba(patch.Rgba.ToArray(), crop.Size);
                actual.Save(Path.Combine(output, "mismatch-received.png"));
                File.WriteAllText(Path.Combine(output, "mismatch.json"), JsonSerializer.Serialize(new { patch.Tile, patch.Version, patch.Rect,
                    referenceDpi = reference.HorizontalResolution }));
                throw new IOException($"Receiver patch {patch.Tile} does not match the owned original pixels.");
            }
        }
        return patches.Length;
    }
}
