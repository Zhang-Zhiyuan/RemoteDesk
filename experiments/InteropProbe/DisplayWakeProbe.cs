using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteDesk;

// Explicit physical-machine diagnostics. No credential entry, power-plan edits,
// clipboard access or arbitrary remote commands. Screen-off/lock require opt-in.
internal static class DisplayWakeProbe
{
    internal static int Run(JsonElement config, string output)
    {
        string mode = config.GetProperty("mode").GetString()!;
        if (mode is not ("observe" or "power-request" or "monitor-on" or "lock-off-cycle" or "host-connection-wake"))
            throw new ArgumentException("Unknown display probe mode.");
        using var power = new DisplayStateWindow();
        var samples = new List<object>();
        void Sample(string stage)
        {
            var desktop = WindowsInteractiveDesktopProbe.InspectCurrent();
            string? error = null;
            object? pixels = null;
            try
            {
                using var capture = new ScreenCaptureService(ScreenCaptureService.GetDefaultTarget());
                var frame = capture.CaptureJpeg(80, 35);
                pixels = Measure(frame.JpegBytes.ToArray());
                // Only lock-screen evidence, never an ordinary application's content.
                if (!desktop.IsAvailable) File.WriteAllBytes(Path.Combine(output, stage + ".jpg"), frame.JpegBytes.ToArray());
            }
            catch (Exception ex) { error = ex.Message; }
            samples.Add(new { stage, elapsedMs = power.ElapsedMilliseconds, display = power.State, desktop, pixels, error });
            Program.Save(Path.Combine(output, "progress.json"), new { mode, samples, events = power.Events.ToArray() });
        }
        bool screenOffRequested = false;
        try
        {
            Pump(1000);
            Sample("before");
            if (mode == "observe") return 0;
            if (mode is "lock-off-cycle" or "host-connection-wake")
            {
                if (!config.TryGetProperty("allowLockAndScreenOff", out var allowed) || !allowed.GetBoolean())
                    throw new InvalidOperationException("Explicit lock and screen-off opt-in required.");
                AssertLocalIdle();
                if (WindowsInteractiveDesktopProbe.InspectCurrent().IsAvailable && !LockWorkStation())
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                Pump(2200);
                if (WindowsInteractiveDesktopProbe.InspectCurrent().IsAvailable)
                    throw new InvalidOperationException("Windows did not lock; no display-off command sent.");
                screenOffRequested = true;
                MonitorPower(2);
                Pump(1800);
                Sample("locked-off");
            }
            if (mode == "host-connection-wake")
            {
                // Do not wake the display from this probe: observe the installed
                // host's real authenticated connection request instead.
                Task<int> connection = Task.Run(() => ObserveRelayAsync(config, output));
                while (!connection.IsCompleted) Pump(50);
                int result = connection.GetAwaiter().GetResult();
                Sample("after-host-connection");
                return result;
            }
            if (mode is "power-request" or "lock-off-cycle")
            {
                using var request = WindowsRemoteSessionPowerRequest.TryAcquire();
                Pump(2000);
                Sample("after-power-request");
                Pump(4000);
                Sample("after-power-hold");
            }
            if (mode is "monitor-on" or "lock-off-cycle")
            {
                MonitorPower(-1);
                Pump(2000);
                Sample("after-monitor-on");
            }
            return 0;
        }
        finally
        {
            if (screenOffRequested) { MonitorPower(-1); Pump(500); }
            Program.Save(Path.Combine(output, mode == "host-connection-wake" ? "display.json" : "result.json"), new { mode, samples, events = power.Events.ToArray(),
                scope = "Physical console display and captured-pixel observation; no unlock or credentials, no sleep/power-plan edits" });
        }
    }

    internal static async Task<int> ObserveRelayAsync(JsonElement config, string output)
    {
        var settings = new AppSettingsService().Load();
        var saved = settings.Relay;
        if (string.IsNullOrWhiteSpace(saved.ServerAddress) || saved.ServerAddress != config.GetProperty("expectedServer").GetString() || string.IsNullOrEmpty(saved.SshHostKeySha256))
            throw new InvalidOperationException("Unexpected saved relay identity.");
        var options = new RelayConnectionOptions(saved.ServerAddress, saved.RelayPort,
            AppSettingsService.UnprotectSecret(saved.ProtectedAccessToken), saved.TlsCertificateSha256!, saved.DeviceId).Validate();
        string id = Guid.Parse(config.GetProperty("deviceId").GetString()!).ToString("D");
        int seconds = config.TryGetProperty("seconds", out var duration) ? duration.GetInt32() : 8;
        if (seconds is < 5 or > 120) throw new ArgumentOutOfRangeException(nameof(seconds));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 30));
        var device = (await RelayTunnelClient.ListDevicesAsync(options, timeout.Token)).Single(x => x.DeviceId == id);
        if (device.Busy) throw new InvalidOperationException("Device has an active viewer; not taking over.");
        if (device.Platform != RemoteDevicePlatforms.Windows) throw new InvalidOperationException("Windows target required.");
        string? protectedKey = RelayDeviceKeys.Find(saved.DeviceKeys, options with { DeviceId = id });
        if (id == saved.DeviceId) protectedKey = settings.Host.ProtectedPassword;
        if (string.IsNullOrEmpty(protectedKey))
            protectedKey = settings.Viewer.RecentDevices.FirstOrDefault(x => x.DeviceId == id ||
                x.Address is not null && device.DirectAddresses.Contains(x.Address) &&
                (x.MachineName == device.MachineName || x.MachineName == device.OriginalMachineName))?.ProtectedPassword;
        string key = AppSettingsService.UnprotectSecret(protectedKey);
        // An explicitly supplied device key stays in memory and is never saved
        // in reports or settings. Do not enumerate/guess possible passwords.
        if (config.TryGetProperty("deviceKey", out var suppliedKey)) key = suppliedKey.GetString() ?? "";
        if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("No saved device credential; nothing guessed.");
        using var client = new RemoteViewerClient(allowLocalConnectionsForTesting: id == saved.DeviceId);
        var log = new ConcurrentQueue<string>();
        var samples = new ConcurrentQueue<object>();
        long count = 0;
        bool directLocal = config.TryGetProperty("directLocal", out var local) && local.GetBoolean();
        if (directLocal && id != saved.DeviceId) throw new InvalidOperationException("Direct fixture is restricted to the local host.");
        string? diagnostics = null;
        string? failure = null;
        RemoteDeviceDescriptor? descriptor = null;
        client.Log += log.Enqueue;
        client.DeviceInfoReceived += value => descriptor = value;
        client.HostVideoDiagnosticsReceived += value => diagnostics = value;
        var started = Stopwatch.StartNew();
        client.FrameReceived += frame =>
        {
            long number = Interlocked.Increment(ref count);
            if (frame.Encoding != RemoteFrameEncoding.Jpeg || number > 3) return;
            byte[] bytes = frame.EncodedBuffer.AsSpan(frame.EncodedOffset, frame.EncodedLength).ToArray();
            samples.Enqueue(new { number, elapsedMs = started.ElapsedMilliseconds, pixels = Measure(bytes) });
            // Statistics are sufficient; do not save the remote user's desktop.
        };
        try
        {
            if (directLocal)
                await client.ConnectAsync("127.0.0.1", settings.Host.Port, key, ViewerVideoMode.StableJpeg, timeout.Token);
            else
                await client.ConnectViaRelayAsync(options with { DeviceId = id }, key, ViewerVideoMode.StableJpeg, timeout.Token);
            await Task.Delay(TimeSpan.FromSeconds(seconds - 1), timeout.Token);
            await client.RequestHostVideoDiagnosticsAsync();
            await Task.Delay(1000, timeout.Token);
            return count > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
            return 1;
        }
        finally
        {
            var captureAvailability = client.LatestCaptureTargetAvailability;
            await client.DisconnectAsync();
            Program.Save(Path.Combine(output, "result.json"), new { device.DeviceId, device.MachineName, descriptor,
                directLocal, seconds, frames = count, samples = samples.ToArray(), captureAvailability,
                diagnostics, failure, log = log.ToArray(), scope = "Authenticated direct/relay capture only; no input, clipboard, settings or remote updates" });
        }
    }

    internal static object Measure(byte[] jpeg)
    {
        using var stream = new MemoryStream(jpeg, false);
        using var bitmap = new Bitmap(stream);
        int dark = 0, total = 0, brightest = 0;
        for (int y = 0; y < bitmap.Height; y += Math.Max(1, bitmap.Height / 90))
            for (int x = 0; x < bitmap.Width; x += Math.Max(1, bitmap.Width / 160))
            {
                Color pixel = bitmap.GetPixel(x, y);
                int light = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
                if (light <= 8) dark++;
                brightest = Math.Max(brightest, light); total++;
            }
        return new { bitmap.Width, bitmap.Height, bytes = jpeg.Length, darkFraction = (double)dark / total, brightest };
    }
    private static void Pump(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(10); }
    }
    private static void MonitorPower(int state) => SendMessageTimeout((nint)0xffff, 0x112, (nuint)0xf170, state, 2, 200, out _);
    private static void AssertLocalIdle()
    {
        var idle = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref idle) || unchecked((uint)Environment.TickCount - idle.Time) < 30000)
            throw new InvalidOperationException("Recent local input; not locking an actively used console.");
        var saved = new AppSettingsService().Load().Relay;
        if (saved.ServerAddress != "8.138.5.232" || string.IsNullOrEmpty(saved.SshHostKeySha256))
            throw new InvalidOperationException("Cannot verify the authorized console is idle.");
        var relay = new RelayConnectionOptions(saved.ServerAddress, saved.RelayPort,
            AppSettingsService.UnprotectSecret(saved.ProtectedAccessToken), saved.TlsCertificateSha256!, saved.DeviceId).Validate();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var current = RelayTunnelClient.ListDevicesAsync(relay, deadline.Token).GetAwaiter().GetResult().Single(x => x.DeviceId == saved.DeviceId);
        if (current.Busy) throw new InvalidOperationException("Console has an active viewer; not taking over.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { public uint Size, Time; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool LockWorkStation();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SendMessageTimeout(nint window, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);

    private sealed class DisplayStateWindow : NativeWindow, IDisposable
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private nint _registration;
        internal readonly ConcurrentQueue<object> Events = new();
        internal int State { get; private set; } = -1;
        internal long ElapsedMilliseconds => _clock.ElapsedMilliseconds;
        internal DisplayStateWindow()
        {
            CreateHandle(new CreateParams { Caption = "RemoteDesk owned display diagnostic", Parent = (nint)(-3) });
            Guid guid = new("6fe69556-704a-47a0-8f24-c28d936fda47");
            _registration = RegisterPowerSettingNotification(Handle, ref guid, 0);
            if (_registration == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x218 && m.WParam == (nint)0x8013 && m.LParam != 0 && Marshal.ReadInt32(m.LParam, 16) == 4)
            {
                State = Marshal.ReadInt32(m.LParam, 20);
                Events.Enqueue(new { elapsedMs = _clock.ElapsedMilliseconds, state = State });
            }
            base.WndProc(ref m);
        }
        public void Dispose() { if (_registration != 0) UnregisterPowerSettingNotification(_registration); _registration = 0; DestroyHandle(); }
        [DllImport("user32.dll", SetLastError = true)] private static extern nint RegisterPowerSettingNotification(nint recipient, ref Guid guid, uint flags);
        [DllImport("user32.dll")] private static extern bool UnregisterPowerSettingNotification(nint registration);
    }
}
