using System.Diagnostics;
using System.Net.Sockets;
using RemoteDesk.NativeDetail;

namespace RemoteDesk;

internal sealed partial class RemoteHostServer
{
    internal static Size ChooseNativeCaptureOutputSize(Size configured, Size observed)
    {
        // The saved setting is a ceiling, not necessarily the currently
        // delivered size: reliable-video adaptation may already be at 1080p.
        // Turning on detail must not silently restore a congested 4K stream.
        if (observed.Width < 48 || observed.Height < 48 || observed.Width > configured.Width ||
            observed.Height > configured.Height || configured.Width <= 0 || configured.Height <= 0 ||
            Math.Abs(observed.Width / (double)observed.Height - configured.Width / (double)configured.Height) > .01)
            return configured;
        return FitEvenSizeWithin(configured, observed);
    }

    private static async Task ConfigureNativeCaptureAsync(NetworkStream stream, SecureSession session,
        SessionWritePriority priority, CaptureSessionState capture, ViewerSessionState viewer,
        int scalePercent, int fps, RemoteVideoCodecs codecs, CancellationToken token)
    {
        if (viewer.NativeDetails is not { } native) return;
        var snapshot = capture.GetTargetSnapshot();
        bool supported = viewer.Capabilities.HasFlag(RemoteDeviceCapabilities.NativeDetailV1) &&
            codecs.HasFlag(RemoteVideoCodecs.H264AnnexB) && snapshot.IsAvailable && !snapshot.Target.IsAllScreens &&
            snapshot.Bounds.Width is >= 48 and <= 8192 && snapshot.Bounds.Height is >= 48 and <= 8192 &&
            (long)snapshot.Bounds.Width * snapshot.Bounds.Height <= 16_777_216;
        NativeCaptureProfile? profile = null;
        if (supported && WindowsGraphicsCaptureTargetResolver.TryResolveCandidates(snapshot.Target.Id, snapshot.Bounds,
                out _, out WindowsDesktopDuplicationTarget? target, out _) && target is not null)
        {
            Size output = CalculateH264FrameSize(snapshot.Bounds, scalePercent);
            // Preserve the user's existing size; the opt-in path never lowers
            // the system resolution. Unsupported encoder sizes keep FFmpeg.
            int rate = Math.Clamp(fps, 1, 60);
            var options = new MediaFoundationD3D11H264EncoderOptions(snapshot.Bounds.Size, output, rate,
                FfmpegDesktopH264Capture.CalculateBitrateBitsPerSecond(output, rate));
            if (options.Validate() is null)
                profile = new(target, snapshot.Version, output, rate, options.BitrateBitsPerSecond);
        }
        native.Configure(profile);
        if (!viewer.Capabilities.HasFlag(RemoteDeviceCapabilities.NativeDetailV1)) return;
        Size offeredSize = profile?.NativeSize ?? new Size(48, 48);
        var offer = new NativeDetailOffer(snapshot.Version, offeredSize, profile is not null && native.MayStart);
        if (viewer.LastNativeOffer == offer) return;
        viewer.LastNativeOffer = offer;
        using (priority.BeginControlWritePriority())
            await Protocol.WriteMessageAsync(stream, MessageType.NativeDetailOffer,
                NativeDetailSessionProtocol.EncodeOffer(offer), session, priority.Lock, token).ConfigureAwait(false);
    }

    private static async Task RunNativeCaptureLoopAsync(NetworkStream stream, SecureSession session,
        SessionWritePriority priority, CaptureSessionState capture,
        CaptureTargetPublicationCoordinator publication, ViewerSessionState viewer,
        NativeDetailCaptureWorker worker, LowLatencyVideoHostTransport lowLatencyVideo,
        Action<string> log, CancellationToken token)
    {
        NativeDetailHostSession native = viewer.NativeDetails!;
        NativeCaptureProfile profile = worker.Profile;
        // Existing authenticated barrier discards late UDP video while keeping
        // the independent UDP mouse input/ACK route. Never switch at capability
        // negotiation alone; default/off sessions keep their existing UDP path.
        await lowLatencyVideo.StopVideoForCaptureTargetChangeAsync().ConfigureAwait(false);
        native.Link.BeginCapture();
        var queue = new DetailSendQueue(chunkBytes: 512);
        byte[]? pendingChunk = null;
        DetailChunk? pendingHeader = null;
        long pendingAt = 0;
        DetailManifest? sentManifest = null;
        long checkedDesktopAt = 0, loggedAt = Environment.TickCount64;
        int frames = 0, chunks = 0;
        log($"原生补清已接入：GPU 同源采集 / 硬编 {profile.OutputSize.Width}×{profile.OutputSize.Height}，接收反馈限速。");
        while (!token.IsCancellationRequested && ReferenceEquals(native.Worker, worker) && worker.IsReady &&
            native.Request is { Enabled: true } && capture.TargetVersion == profile.TargetGeneration &&
            viewer.SupportedVideoCodecs.HasFlag(RemoteVideoCodecs.H264AnnexB))
        {
            long now = Environment.TickCount64;
            if (native.Link.FeedbackStalled(now))
            {
                native.Suspend("原生补清缺少接收反馈，已回退普通画面，避免无反馈时持续排队。");
                break;
            }
            if (now - checkedDesktopAt >= 200)
            {
                checkedDesktopAt = now;
                if (!WindowsInteractiveDesktopProbe.InspectCurrent().IsAvailable)
                { worker.Stop(); break; }
                capture.RefreshCaptureBounds();
                if (!capture.IsTargetAvailable || capture.TargetVersion != profile.TargetGeneration) break;
            }
            NativeCapturedFrame? frame = worker.TakeLatest();
            if (frame is not null && native.Request?.Context == frame.Manifest.Context &&
                native.Link.CanSendBase(now) && !priority.HasPendingControlWrite)
            {
                var video = frame.Video;
                if (Stopwatch.GetElapsedTime(frame.CapturedAt).TotalMilliseconds > 100)
                    continue; // latest all-IDR base; no old-frame catch-up queue
                if (!capture.TryObserveEncodedFrame(profile.TargetGeneration, profile.Target.Bounds, video.OutputSize,
                        Math.Clamp((int)(video.OutputSize.Width * 100L / profile.NativeSize.Width), 25, 100))) break;
                byte[] payload = NativeDetailSessionProtocol.EncodeBase(frame.Manifest,
                    new(video.OutputSize.Width, video.OutputSize.Height, RemoteFrameEncoding.H264AnnexB,
                        RemoteFrameFlags.KeyFrame | RemoteFrameFlags.CodecConfig, video.AnnexBBytes, 0,
                        video.AnnexBBytes.Length, 0,
                        Stopwatch.GetElapsedTime(video.SubmittedAtTimestamp, video.CompletedAtTimestamp).TotalMilliseconds));
                bool admitted = await publication.AdmitFrameIfCurrentAsync(profile.TargetGeneration,
                    capture.IsCurrentTargetGeneration, async sendToken =>
                    {
                        native.Link.RecordSent(frame.Manifest.Context, frame.Manifest.Sequence, payload.Length + 25, Environment.TickCount64);
                        await Protocol.WriteMessageAsync(stream, MessageType.NativeVideoFrame, payload, session, priority.Lock, sendToken).ConfigureAwait(false);
                        sentManifest = frame.Manifest; frames++;
                    }, token).ConfigureAwait(false);
                if (!admitted) break;
            }
            if (native.Interacting || native.Request?.Context != sentManifest?.Context)
            { queue.Clear(); pendingChunk = null; pendingHeader = null; }
            while (worker.TryTakeTransfer(out DetailTransfer? transfer))
                if (transfer is not null && !native.Interacting) queue.Enqueue(transfer, Environment.TickCount64);
            if (sentManifest is not null && !priority.HasPendingControlWrite && native.CanEnhance)
            {
                if (pendingHeader is { } held && (now - pendingAt > 200 || held.Context != sentManifest.Context ||
                    sentManifest[held.Tile] != held.Version || worker.LatestManifest is not { } current ||
                    current.Context != held.Context || current[held.Tile] != held.Version))
                { pendingChunk = null; pendingHeader = null; queue.Clear(); }
                byte[]? chunk = pendingChunk ?? queue.TryTake(Environment.TickCount64, native.Link.DetailBitsPerSecond(Environment.TickCount64),
                    priority.HasPendingControlWrite, native.Interacting, 0,
                    transfer => native.Request?.Context == transfer.Context && sentManifest.Context == transfer.Context &&
                        sentManifest.Sequence >= transfer.ReferenceSequence && sentManifest[transfer.Tile] == transfer.Version &&
                        worker.LatestManifest is { } latest && latest.Context == transfer.Context && latest[transfer.Tile] == transfer.Version);
                if (chunk is not null)
                {
                    if (pendingChunk is null)
                    { pendingChunk = chunk; pendingHeader = DetailWire.DecodeChunk(chunk); pendingAt = now; }
                    // Only the capture sender handles optional writes: no
                    // background fragment competes with the next base.
                    if (await Protocol.TryWriteNativeChunkAsync(stream, chunk, session, priority.Lock,
                            () => !priority.HasPendingControlWrite && native.CanEnhance && native.Request?.Context == sentManifest.Context,
                            () => native.Link.RecordSent(sentManifest.Context, sentManifest.Sequence, chunk.Length + 25, Environment.TickCount64),
                            token).ConfigureAwait(false))
                    { chunks++; pendingChunk = null; pendingHeader = null; }
                    // A racing control/ACK wins this iteration. Keep just this
                    // small fragment, not a socket waiter and not a lost hole
                    // in the middle of an otherwise valid tile transfer.
                }
            }
            if (now - loggedAt >= 2000)
            {
                log($"原生补清统计：{frames} 底图 / {chunks} 细节分片，未确认 {native.Link.SentBytes - native.Link.AcknowledgedBytes} B，" +
                    $"细节预算 {native.Link.DetailBitsPerSecond(now) / 1000} kbps（无余量时暂停），{worker.Diagnostics}。");
                loggedAt = now; frames = chunks = 0;
            }
            await Task.Delay(2, token).ConfigureAwait(false);
        }
        queue.Clear();
        log("原生补清退出，恢复兼容捕获链路。");
    }
}
