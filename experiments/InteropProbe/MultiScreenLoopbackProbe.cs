using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using RemoteDesk;

// Owned ephemeral loopback host, actual local display capture and production
// clients. No input injection, clipboard access, installation or saved frames.
internal static class MultiScreenLoopbackProbe
{
    internal static async Task<int> RunAsync(string output)
    {
        var checks = new List<object>();
        var logs = new ConcurrentQueue<string>();
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed });
            Program.Save(Path.Combine(output, "multi-screen-loopback.json"), new { checks, logs = logs.ToArray().TakeLast(60) });
            if (!passed) throw new InvalidOperationException(name);
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        ScreenCaptureTarget[] screens = ScreenCaptureService.GetAvailableTargets().Where(target => !target.IsAllScreens).ToArray();
        if (screens.Length < 2) throw new InvalidOperationException("Two actual extended displays are required.");
        ScreenCaptureTarget first = screens.FirstOrDefault(screen => screen.IsPrimary) ?? screens[0];
        ScreenCaptureTarget second = screens.First(screen => screen.Id != first.Id);
        Program.Save(Path.Combine(output, "physical-displays.json"), screens.Select(screen => new { screen.Id, screen.Bounds, screen.IsPrimary }));
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        using var host = new RemoteHostServer();
        host.Log += message => logs.Enqueue(message);
        using var primary = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        primary.Log += message => logs.Enqueue("primary: " + message);
        int firstFrames = 0, secondFrames = 0, firstH264 = 0, secondH264 = 0;
        var firstSizes = new ConcurrentDictionary<string, bool>();
        var secondSizes = new ConcurrentDictionary<string, bool>();
        primary.FrameReceived += frame =>
        {
            Interlocked.Increment(ref firstFrames);
            if (frame.Encoding == RemoteFrameEncoding.H264AnnexB) Interlocked.Increment(ref firstH264);
            firstSizes.TryAdd($"{frame.Width}x{frame.Height}:{frame.Encoding}", true);
        };
        await host.StartAsync(0, password, 10, 90, 100, first, adaptiveQuality: false);
        try
        {
            await primary.ConnectAsync("127.0.0.1", host.ListeningPort, password, ViewerVideoMode.StableJpeg, deadline.Token);
            await Wait(() => primary.CanOpenAdditionalScreen && Volatile.Read(ref firstFrames) > 1, deadline.Token);
            Check("primary authenticates and receives actual display frames", primary.IsConnected && host.HasActiveRemoteControl);
            using var secondary = primary.CreateAdditionalScreenClient(second.Id);
            secondary.Log += message => logs.Enqueue("secondary: " + message);
            secondary.FrameReceived += frame =>
            {
                Interlocked.Increment(ref secondFrames);
                if (frame.Encoding == RemoteFrameEncoding.H264AnnexB) Interlocked.Increment(ref secondH264);
                secondSizes.TryAdd($"{frame.Width}x{frame.Height}:{frame.Encoding}", true);
            };
            await secondary.ConnectAsync("127.0.0.1", host.ListeningPort, password, ViewerVideoMode.StableJpeg, deadline.Token);
            await Wait(() => secondary.CanOpenAdditionalScreen && Volatile.Read(ref secondFrames) > 1, deadline.Token);
            Check("second monitor joins without taking over first", primary.IsConnected && secondary.IsConnected && primary.LastSessionRejection is null);
            Check("both monitors retain their own native pixel geometry",
                firstSizes.ContainsKey($"{first.Bounds.Width}x{first.Bounds.Height}:Jpeg") && secondSizes.ContainsKey($"{second.Bounds.Width}x{second.Bounds.Height}:Jpeg"));

            // AUT2 with a valid device password but an invalid group ticket must
            // fail closed without replacing either authenticated live window.
            using (var invalid = new TcpClient())
            {
                await invalid.ConnectAsync(IPAddress.Loopback, host.ListeningPort, deadline.Token);
                using var session = await Protocol.AuthenticateClientAsync(invalid.GetStream(), password, deadline.Token, screenAttachment: true);
                using var write = new SemaphoreSlim(1, 1);
                await Protocol.WriteMessageAsync(invalid.GetStream(), MessageType.Control,
                    RemoteMessageCodec.EncodeScreenAttachmentJoin(new(new string('0', 64), second.Id)), session, write, deadline.Token);
                var rejected = await Protocol.ReadMessageAsync(invalid.GetStream(), session, deadline.Token);
                Check("invalid attachment cannot evict either screen", RemoteMessageCodec.DecodeControl(rejected.PayloadMemory).Kind == RemoteControlKind.SessionRejected && primary.IsConnected && secondary.IsConnected);
            }

            await primary.SetScreenStreamPausedAsync(true);
            await Task.Delay(800, deadline.Token);
            int pausedFirst = Volatile.Read(ref firstFrames), liveSecond = Volatile.Read(ref secondFrames);
            await Task.Delay(700, deadline.Token);
            Check("closed first window can pause its capture while second continues", Volatile.Read(ref firstFrames) == pausedFirst && Volatile.Read(ref secondFrames) > liveSecond && primary.IsConnected);
            await primary.SetScreenStreamPausedAsync(false);
            await Wait(() => Volatile.Read(ref firstFrames) > pausedFirst, deadline.Token);
            Check("first window resumes without replacing second", secondary.IsConnected);

            await primary.UpdateViewerVideoCodecsAsync(RemoteVideoCodecs.H264AnnexB | RemoteVideoCodecs.Jpeg);
            await secondary.UpdateViewerVideoCodecsAsync(RemoteVideoCodecs.H264AnnexB | RemoteVideoCodecs.Jpeg);
            await Wait(() => Volatile.Read(ref firstH264) > 0 && Volatile.Read(ref secondH264) > 0, deadline.Token, 35);
            Check("two independent H264 streams deliver frames", primary.IsConnected && secondary.IsConnected);
            // Qualify the actual running encoder too, not only the JPEG loop.
            // A hidden primary must release capture work without stalling the
            // other display, and reopening must start fresh decodable H.264.
            await primary.SetScreenStreamPausedAsync(true);
            await Task.Delay(1500, deadline.Token);
            pausedFirst = Volatile.Read(ref firstFrames);
            int liveSecondH264 = Volatile.Read(ref secondH264);
            // WGC intentionally sends no repeated frames on a static desktop.
            // Request real fresh output rather than assuming a minimum idle FPS.
            await secondary.RequestVideoKeyFrameAsync();
            await Wait(() => Volatile.Read(ref secondH264) > liveSecondH264, deadline.Token, 20);
            Program.Save(Path.Combine(output, "h264-pause-observation.json"), new {
                pausedFirst, firstFrames = Volatile.Read(ref firstFrames), liveSecondH264,
                secondH264 = Volatile.Read(ref secondH264), primary.IsConnected,
                secondaryConnected = secondary.IsConnected });
            Check("pausing an H264 screen stops its frames while the other serves a fresh keyframe",
                Volatile.Read(ref firstFrames) == pausedFirst && Volatile.Read(ref secondH264) > liveSecondH264 && primary.IsConnected);
            int pausedFirstH264 = Volatile.Read(ref firstH264);
            await primary.SetScreenStreamPausedAsync(false);
            await Wait(() => Volatile.Read(ref firstH264) > pausedFirstH264, deadline.Token, 25);
            await primary.RequestVideoKeyFrameAsync();
            await Wait(() => Volatile.Read(ref firstH264) > pausedFirstH264 + 1, deadline.Token, 25);
            Check("reopening the paused H264 screen resumes without replacing the other transport",
                secondary.IsConnected && Volatile.Read(ref secondH264) > liveSecondH264);
            await secondary.DisconnectAsync();
            Check("closing secondary keeps original controller active", primary.IsConnected && host.HasActiveRemoteControl);
            using var reopened = primary.CreateAdditionalScreenClient(second.Id);
            await reopened.ConnectAsync("127.0.0.1", host.ListeningPort, password, ViewerVideoMode.StableJpeg, deadline.Token);
            Check("secondary can reopen without taking over", await reopened.WaitForCurrentDeviceInfoAsync(TimeSpan.FromSeconds(10), deadline.Token) && primary.IsConnected);
            using var stale = primary.CreateAdditionalScreenClient(second.Id);
            using var replacement = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
            await replacement.ConnectAsync("127.0.0.1", host.ListeningPort, password, ViewerVideoMode.StableJpeg, deadline.Token);
            await Wait(() => replacement.CanOpenAdditionalScreen && primary.LastSessionRejection is not null && reopened.LastSessionRejection is not null, deadline.Token);
            Check("a different controller still takes over every old screen", !primary.IsConnected && !reopened.IsConnected && replacement.IsConnected);
            bool staleRejected = false;
            try { await stale.ConnectAsync("127.0.0.1", host.ListeningPort, password, ViewerVideoMode.StableJpeg, deadline.Token); }
            catch (RemoteSessionRejectedException) { staleRejected = true; }
            Check("stale attachment gets explicit rejection and cannot take ownership back", staleRejected && replacement.IsConnected && !stale.IsConnected);
            Program.Save(Path.Combine(output, "frame-metadata.json"), new {
                firstFrames, secondFrames, firstH264, secondH264, firstSizes = firstSizes.Keys, secondSizes = secondSizes.Keys,
                scope = "Actual landscape+portrait extended-display capture and encrypted loopback; no input/clipboard operations or persisted desktop images" });
        }
        finally { await host.StopAsync(); }
        return 0;
    }

    private static async Task Wait(Func<bool> condition, CancellationToken token, int seconds = 15)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed.TotalSeconds > seconds) throw new TimeoutException("Multi-screen probe did not reach expected state.");
            await Task.Delay(30, token);
        }
    }
}
