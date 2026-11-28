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
            (long)snapshot.Bounds.Width * snapshot.Bounds.Height <= 16_777_216 &&
            WindowsInteractiveDesktopProbe.InspectCurrent().IsAvailable;
        // Do not let the worker supervisor restart DDA/MF on the secure
        // desktop. Those retries can interrupt the working H.264 fallback.
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
        var offer = new NativeDetailOffer(snapshot.Version, offeredSize, profile is not null && native.MayStart,
            codecs.HasFlag(RemoteVideoCodecs.NativeDetailH264));
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
        bool extended = native.Request is { Extended: true } && viewer.SupportedVideoCodecs.HasFlag(RemoteVideoCodecs.NativeDetailH264);
        if (!extended) await lowLatencyVideo.StopVideoForCaptureTargetChangeAsync().ConfigureAwait(false);
        native.UdpLink.BeginCapture();
        native.Link.BeginCapture();
        DetailSendQueue NewQueue(bool udp) => new(extended && !udp ? DetailWire.NarrowLinkChunkBytes : 512);
        var queue = NewQueue(native.DatagramBase);
        byte[]? pendingChunk = null;
        DetailChunk? pendingHeader = null;
        long pendingAt = 0;
        DetailManifest? sentManifest = null;
        NativeCapturedFrame? latestBase = null;
        long checkedDesktopAt = 0, networkSampleAt = 0, loggedAt = Environment.TickCount64;
        int frames = 0, chunks = 0;
        log($"原生补清已接入：GPU 同源采集 / 硬编 {profile.OutputSize.Width}×{profile.OutputSize.Height}，接收反馈限速。");
        while (!token.IsCancellationRequested && !viewer.ScreenStreamPaused && ReferenceEquals(native.Worker, worker) && worker.IsReady &&
            native.Request is { Enabled: true } && capture.TargetVersion == profile.TargetGeneration &&
            viewer.SupportedVideoCodecs.HasFlag(RemoteVideoCodecs.H264AnnexB))
        {
            long now = Environment.TickCount64;
            bool udp = extended && lowLatencyVideo.CanSendVideo;
            if (udp != native.DatagramBase)
            {
                native.DatagramBase = udp;
                queue = NewQueue(udp); pendingChunk = null; pendingHeader = null; sentManifest = null;
                if (udp) native.UdpLink.BeginCapture(); else native.Link.BeginCapture();
            }
            if (udp && now >= networkSampleAt)
            {
                native.UdpLink.ObserveNetwork(lowLatencyVideo.CollectNetworkSnapshot(), now);
                networkSampleAt = now + 250;
            }
            if (!udp && native.Link.FeedbackStalled(now))
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
            // Credit can open between captures. Keep the latest fresh IDR for that
            // instant instead of throwing away a scroll update and awaiting a repeat.
            latestBase = worker.TakeLatest() ?? latestBase;
            if (latestBase is { } waiting && (native.Request?.Context != waiting.Manifest.Context ||
                Stopwatch.GetElapsedTime(waiting.CapturedAt).TotalMilliseconds > 100))
                latestBase = null;
            if (latestBase is { } frame &&
                (udp || (native.Link.CanSendBase(now, frame.Video.AnnexBBytes.Length) && !priority.HasPendingControlWrite)))
            {
                latestBase = null;
                var video = frame.Video;
                if (!capture.TryObserveEncodedFrame(profile.TargetGeneration, profile.Target.Bounds, video.OutputSize,
                        Math.Clamp((int)(video.OutputSize.Width * 100L / profile.NativeSize.Width), 25, 100))) break;
                var baseFrame = new RemoteFrame(video.OutputSize.Width, video.OutputSize.Height, RemoteFrameEncoding.H264AnnexB,
                        RemoteFrameFlags.KeyFrame | RemoteFrameFlags.CodecConfig, video.AnnexBBytes, 0,
                        video.AnnexBBytes.Length, 0,
                        Stopwatch.GetElapsedTime(video.SubmittedAtTimestamp, video.CompletedAtTimestamp).TotalMilliseconds);
                bool admitted = await publication.AdmitFrameIfCurrentAsync(profile.TargetGeneration,
                    capture.IsCurrentTargetGeneration, async sendToken =>
                    {
                        if (udp)
                        {
                            // Viewer input invalidates its overlay immediately. The same encoder
                            // can supply a normal H.264 IDR without serializing any tile metadata.
                            if (native.Interacting)
                            {
                                if (lowLatencyVideo.TryQueueVideoFrame(baseFrame.Width, baseFrame.Height, baseFrame.Encoding,
                                    baseFrame.Flags, baseFrame.CaptureMilliseconds, baseFrame.EncodeMilliseconds,
                                    video.AnnexBBytes, allowLatencyBudgetDrop: true)) frames++;
                                return;
                            }
                            if (!lowLatencyVideo.TryQueueNativeVideoFrame(frame.Manifest, baseFrame))
                            {
                                if (lowLatencyVideo.CanSendVideo)
                                    native.Suspend("当前 UDP 预算不足以发送原生补清底图，恢复普通 H.264。");
                                return;
                            }
                            native.UdpLink.RecordBase(frame.Manifest.Context, frame.Manifest.Sequence);
                        }
                        else
                        {
                            byte[] payload = NativeDetailSessionProtocol.EncodeBase(frame.Manifest, baseFrame);
                            native.Link.RecordSent(frame.Manifest.Context, frame.Manifest.Sequence, payload.Length + 25, Environment.TickCount64);
                            await Protocol.WriteMessageAsync(stream, MessageType.NativeVideoFrame, payload, session, priority.Lock, sendToken).ConfigureAwait(false);
                        }
                        sentManifest = frame.Manifest; frames++;
                    }, token).ConfigureAwait(false);
                if (!admitted) break;
            }
            now = Environment.TickCount64;
            if (native.Interacting || native.Request?.Context != sentManifest?.Context)
            { queue.Clear(); pendingChunk = null; pendingHeader = null; }
            while (worker.TryTakeTransfer(out DetailTransfer? transfer))
                if (transfer is not null && !native.Interacting) queue.Enqueue(transfer, Environment.TickCount64);
            long detailRate = native.DetailBitsPerSecond(now);
            worker.ObserveSendBacklog(queue.Count, queue.QueuedBytes, detailRate);
            if (sentManifest is not null && !priority.HasPendingControlWrite && !native.Interacting && detailRate > 0)
            {
                bool allowWholeTile = extended && detailRate > NativeDetailHeadroom.StandardLimit;
                if (!allowWholeTile && pendingHeader?.ChunkDataBytes == DetailWire.ChunkBytes)
                { pendingChunk = null; pendingHeader = null; }
                if (pendingHeader is { } held && (now - pendingAt > 200 || held.Context != sentManifest.Context ||
                    sentManifest[held.Tile] != held.Version || worker.LatestManifest is not { } current ||
                    current.Context != held.Context || current[held.Tile] != held.Version))
                { pendingChunk = null; pendingHeader = null; queue.Clear(); }
                byte[]? chunk = pendingChunk ?? queue.TryTake(Environment.TickCount64, detailRate,
                    priority.HasPendingControlWrite, native.Interacting, 0,
                    transfer => native.Request?.Context == transfer.Context && sentManifest.Context == transfer.Context &&
                        sentManifest.Sequence >= transfer.ReferenceSequence && sentManifest[transfer.Tile] == transfer.Version &&
                        worker.LatestManifest is { } latest && latest.Context == transfer.Context && latest[transfer.Tile] == transfer.Version,
                    allowWholeTile);
                if (chunk is not null)
                {
                    if (pendingChunk is null)
                    { pendingChunk = chunk; pendingHeader = DetailWire.DecodeChunk(chunk); pendingAt = now; }
                    // Only the capture sender handles optional writes: no
                    // background fragment competes with the next base.
                    long requiredRate = pendingHeader!.ChunkDataBytes == DetailWire.ChunkBytes ? NativeDetailHeadroom.StandardLimit : 0;
                    DetailContext chunkContext = pendingHeader.Context;
                    bool MayWrite() => !priority.HasPendingControlWrite && !native.Interacting && native.DatagramBase == udp &&
                        native.Request?.Context == chunkContext &&
                        native.DetailBitsPerSecond(Environment.TickCount64, chunk.Length + DetailWire.OuterRecordBytes) > requiredRate;
                    bool accepted = udp
                        ? native.TryWriteUdpChunk(stream, chunk, session, priority.Lock, MayWrite, token)
                        : await Protocol.TryWriteNativeChunkAsync(stream, chunk, session, priority.Lock, MayWrite,
                            () => native.Link.RecordSent(sentManifest.Context, sentManifest.Sequence, chunk.Length + 25, Environment.TickCount64, isBase: false),
                            token).ConfigureAwait(false);
                    if (accepted)
                    { chunks++; pendingChunk = null; pendingHeader = null; }
                    // A racing control/ACK wins this iteration. Keep just this
                    // small fragment, not a socket waiter and not a lost hole
                    // in the middle of an otherwise valid tile transfer.
                }
            }
            if (now - loggedAt >= 2000)
            {
                log($"原生补清统计：{(udp ? "UDP" : "TCP")}，{frames} 底图 / {chunks} 细节分片，" +
                    $"细节预算 {native.DetailBitsPerSecond(now) / 1000} kbps，" +
                    (udp ? "" : native.Link.DescribeDelivery(now) + "，") + worker.Diagnostics + "。");
                loggedAt = now; frames = chunks = 0;
            }
            await worker.WaitForFrameAsync(2, token).ConfigureAwait(false);
        }
        queue.Clear();
        log("原生补清退出，恢复兼容捕获链路。");
    }
}
