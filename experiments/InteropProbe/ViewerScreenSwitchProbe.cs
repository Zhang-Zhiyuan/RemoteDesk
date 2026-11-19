using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using RemoteDesk;

// Runs the Windows viewer on the controller's interactive desktop. Receipt
// contains frame counters and timings only. All desktop input is paused.
internal static class ViewerScreenSwitchProbe
{
    internal static int Run(JsonElement config, string output)
    {
        var settings = new AppSettingsService().Load();
        var relay = settings.Relay;
        string target = config.GetProperty("target").GetString()!;
        if (string.IsNullOrWhiteSpace(relay.ServerAddress) ||
            !RemoteDeviceIdentity.Same(relay.DeviceId, config.GetProperty("controller").GetString()) ||
            RemoteDeviceIdentity.Same(relay.DeviceId, target) ||
            relay.ServerAddress != config.GetProperty("expectedServer").GetString())
            throw new InvalidOperationException("Unexpected controller, target or relay.");
        RemoteDeviceIdentity.LocalId = relay.DeviceId;
        var route = new RelayConnectionOptions(relay.ServerAddress, relay.RelayPort,
            AppSettingsService.UnprotectSecret(relay.ProtectedAccessToken), relay.TlsCertificateSha256!, target).Validate();
        string? address = config.TryGetProperty("address", out var direct) ? direct.GetString() : null;
        using var client = new RemoteViewerClient();
        client.SetLocalInputPaused(true);
        using var viewer = new RemoteViewerWindow(client, "RemoteDesk 双屏切换测试（仅观看）", false, false, false, false, false, false);
        int idleSeconds = config.TryGetProperty("idleSeconds", out var duration) ? duration.GetInt32() : 30;
        if (idleSeconds is < 10 or > 180) throw new ArgumentOutOfRangeException(nameof(idleSeconds));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(idleSeconds + 75));
        using var heartbeat = new System.Windows.Forms.Timer { Interval = 50 };
        var identity = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var screens = new TaskCompletionSource<CaptureTargetInfo[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? selected = null;
        long videoFrames = 0, otherFrames = 0;
        int orientation = 0, ticks = 0;
        var clock = Stopwatch.StartNew();
        long lastTick = 0, longestTick = 0, longestIdleTick = 0;
        bool observingIdle = false;
        var stages = new List<object>();
        bool passed = false;
        string? failure = null;
        client.DeviceInfoReceived += info => { if (info.DeviceId != null) identity.TrySetResult(info.DeviceId); };
        client.CaptureTargetsReceived += list => screens.TrySetResult(list.ToArray());
        client.CaptureTargetChanged += screen => Volatile.Write(ref selected, screen.Id);
        client.FrameReceived += frame =>
        {
            if (frame.Encoding != RemoteFrameEncoding.H264AnnexB) { Interlocked.Increment(ref otherFrames); return; }
            Volatile.Write(ref orientation, Math.Sign(frame.Width - frame.Height));
            Interlocked.Increment(ref videoFrames);
        };
        heartbeat.Tick += (_, _) =>
        {
            long now = clock.ElapsedMilliseconds;
            longestTick = Math.Max(longestTick, now - lastTick);
            if (observingIdle) longestIdleTick = Math.Max(longestIdleTick, now - lastTick);
            lastTick = now;
            ticks++;
        };
        viewer.Shown += async (_, _) =>
        {
            try
            {
                lastTick = clock.ElapsedMilliseconds;
                heartbeat.Start();
                var device = (await RelayTunnelClient.ListDevicesAsync(route, deadline.Token)).Single(d => d.DeviceId == target);
                if (device.Busy || (address != null && !device.DirectAddresses.Contains(address)))
                    throw new InvalidOperationException("Device busy or address not advertised by that device.");
                string? key = RelayWindowsUpdateProbe.Credentials(settings, route, device).FirstOrDefault().Secret;
                if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("No saved key for the selected device.");
                if (address == null) await client.ConnectViaRelayAsync(route, key, ViewerVideoMode.Automatic, deadline.Token);
                else await client.ConnectAsync(address, device.DirectPort, key, ViewerVideoMode.Automatic, deadline.Token);
                if (!RemoteDeviceIdentity.Same(await identity.Task.WaitAsync(deadline.Token), target))
                    throw new InvalidOperationException("Authenticated device identity mismatch.");
                var displays = await screens.Task.WaitAsync(deadline.Token);
                CaptureTargetInfo FindScreen(int desired) => displays.First(s =>
                {
                    var size = Regex.Match(s.DisplayName, @"\((\d+)[x×](\d+)");
                    return s.Id != ScreenCaptureTarget.AllScreensId && size.Success &&
                        Math.Sign(int.Parse(size.Groups[1].Value) - int.Parse(size.Groups[2].Value)) == desired;
                });
                var landscape = FindScreen(1);
                var portrait = FindScreen(-1);
                string? original = Volatile.Read(ref selected);
                async Task SwitchAndMeasure(CaptureTargetInfo screen, int desired)
                {
                    long start = clock.ElapsedMilliseconds;
                    long before = viewer.CollectRenderTelemetrySnapshot().DirectHardwarePresentedFrames;
                    await client.SelectCaptureTargetAsync(screen.Id);
                    while (!string.Equals(Volatile.Read(ref selected), screen.Id, StringComparison.OrdinalIgnoreCase) ||
                        Volatile.Read(ref orientation) != desired ||
                        viewer.CollectRenderTelemetrySnapshot().DirectHardwarePresentedFrames < before + 3)
                    {
                        if (!client.IsConnected || clock.ElapsedMilliseconds - start > 15000)
                            throw new IOException("No ongoing hardware output on " + screen.DisplayName);
                        await Task.Delay(25, deadline.Token);
                    }
                    stages.Add(new { screen.DisplayName, elapsedMs = clock.ElapsedMilliseconds - start });
                }
                try
                {
                    await SwitchAndMeasure(landscape, 1);
                    await SwitchAndMeasure(portrait, -1);
                    viewer.WindowState = FormWindowState.Minimized;
                    await Task.Delay(350, deadline.Token);
                    viewer.WindowState = FormWindowState.Normal;
                    await SwitchAndMeasure(landscape, 1);
                    observingIdle = true;
                    await Task.Delay(TimeSpan.FromSeconds(idleSeconds), deadline.Token);
                    observingIdle = false;
                    await SwitchAndMeasure(portrait, -1);
                }
                finally
                {
                    if (original != null && client.IsConnected && original != Volatile.Read(ref selected))
                        await client.SelectCaptureTargetAsync(original);
                }
                var render = viewer.CollectRenderTelemetrySnapshot();
                passed = client.IsConnected && ticks > 100 && !render.DirectHardwarePathDisabled &&
                    render.DirectHardwarePresentationFailures == 0 && Interlocked.Read(ref otherFrames) == 0;
            }
            catch (Exception e) { failure = e.Message; }
            finally
            {
                heartbeat.Stop();
                var render = viewer.CollectRenderTelemetrySnapshot(includeLatencyDistributions: true);
                Program.Save(Path.Combine(output, "screen-switch.json"), new { passed, failure, via = address == null ? "relay" : "direct",
                    seconds = clock.Elapsed.TotalSeconds, stages, ticks, longestUiTickMs = longestTick, videoFrames, otherFrames,
                    idleSeconds, longestIdleUiTickMs = longestIdleTick,
                    render.DirectHardwarePresentedFrames, render.DirectHardwarePresentationFailures, render.LastRenderedDecoderBackend,
                    latency = render.DirectHardwareDecodeToPresentLatency.SummarizeSince(default) });
                await client.DisconnectAsync();
                viewer.Close();
            }
        };
        Application.Run(viewer);
        return passed ? 0 : 1;
    }
}
