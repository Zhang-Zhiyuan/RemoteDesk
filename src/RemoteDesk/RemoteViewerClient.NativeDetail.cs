using RemoteDesk.NativeDetail;
using System.Diagnostics;

namespace RemoteDesk;

internal sealed partial class RemoteViewerClient
{
    // Capability negotiation is independent of the viewer's quality decision.
    internal bool EnableNativeDetailReception { get; init; } = true;
    private byte[] EncodeViewerVideoInfo(RemoteVideoCodecs codecs) => RemoteMessageCodec.EncodeViewerInfo(
        EnableNativeDetailReception ? codecs | RemoteVideoCodecs.NativeDetailH264 : codecs & ~RemoteVideoCodecs.NativeDetailH264);
    internal NativeDetailViewerSession NativeDetails { get; } = new();
    private readonly SemaphoreSlim _nativeRequestLock = new(1, 1);
    private long _nativeRequestSequence;
    private long _nativeRequestIntentRevision;
    private long _nativeInputRevision;
    internal event Action? NativeDetailRequested;
    internal event Action<NativeDetailOffer?>? NativeDetailOfferChanged;
    private NativeDetailOffer? _nativeOffer;
    internal NativeDetailOffer? NativeOffer => Volatile.Read(ref _nativeOffer);
    internal long NativeInputRevision => Interlocked.Read(ref _nativeInputRevision);
    private NativeFeedbackOwner? _nativeFeedbackOwner;
    private NativeFeedbackOwner? _nativeUdpFeedbackOwner;
    private sealed class NativeFeedbackOwner(CancellationTokenSource connection, bool datagramBase)
    {
        internal CancellationTokenSource Connection { get; } = connection;
        internal bool DatagramBase { get; } = datagramBase;
        internal NativeDetailFeedback? Latest;
        internal long WireBytes;
    }

    private RemoteDeviceCapabilities NegotiatedViewerCapabilities(bool relay)
    {
        var capabilities = (relay ? RelayViewerCapabilities : LocalViewerCapabilities) |
            RemoteDeviceCapabilities.IndependentScreenSessions;
        if (!EnableNativeDetailReception) return capabilities;
        return capabilities | RemoteDeviceCapabilities.NativeDetailV1;
    }

    internal void InvalidateNativeDetails()
    {
        if (EnableNativeDetailReception) Interlocked.Increment(ref _nativeInputRevision);
        if (NativeDetails.IsEnabled) NativeDetails.Invalidate();
    }

    internal Task<bool> StopNativeDetailsAsync()
    {
        // Availability can be revoked during a GPU failure/backoff. It must
        // never revoke the user's authority to stop the last request.
        var context = NativeDetails.RequestedContext;
        Size size = context is { } previous ? new(previous.Width, previous.Height) : NativeOffer?.NativeSize ?? new(48, 48);
        return RequestNativeDetailsAsync(size, new(Point.Empty, size), enabled: false);
    }

    internal async Task<bool> RequestNativeDetailsAsync(Size nativeSize, Rectangle viewport, bool enabled = true)
    {
        // Local off is immediate even if disconnected, negotiating, or a
        // previous request/control write is still waiting for the network.
        long intent = Interlocked.Increment(ref _nativeRequestIntentRevision);
        if (!enabled) InvalidateNativeDetails();
        CancellationTokenSource? owner = _cancellationTokenSource;
        if (!EnableNativeDetailReception || owner is null || !IsCurrentConnection(owner)) return false;
        await _nativeRequestLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsCurrentConnection(owner) || owner.IsCancellationRequested ||
                intent != Interlocked.Read(ref _nativeRequestIntentRevision)) return false;
            await WaitForRemoteCapabilityNegotiationAsync(owner.Token).ConfigureAwait(false);
            if (!IsCurrentConnection(owner) || !_remoteCapabilities.HasFlag(RemoteDeviceCapabilities.NativeDetailV1)) return false;
            // A re-request after input must be ordered AFTER the reliable input
            // writes. This is not on the input or base-frame execution path.
            await FlushInputAsync(owner.Token).ConfigureAwait(false);
            NativeDetailRequest request;
            long inputRevision;
            lock (_inputLock)
            {
                if (!CanQueueInput(owner) || _inputQueue.Count != 0 || _inputWriteInProgress) return false;
                inputRevision = Interlocked.Read(ref _nativeInputRevision);
                request = new(new(_inputConnectionGeneration, checked(++_nativeRequestSequence),
                    nativeSize.Width, nativeSize.Height),
                    new(viewport.X, viewport.Y, viewport.Width, viewport.Height), enabled, NativeOffer is { Extended: true });
            }
            // Inflation may hold the cache lock; never hold the input lock
            // while resetting it. A racing input invalidates this request.
            NativeDetails.BeginRequest(request);
            if (intent != Interlocked.Read(ref _nativeRequestIntentRevision) ||
                (enabled && inputRevision != Interlocked.Read(ref _nativeInputRevision)))
            { NativeDetails.Invalidate(); return false; }
            bool sent = await SendControlAsync(NativeDetailSessionProtocol.EncodeRequest(request), owner,
                MessageType.NativeDetailRequest).ConfigureAwait(false);
            if (!sent || !IsCurrentConnection(owner)) { NativeDetails.Invalidate(); return false; }
            NativeDetailRequested?.Invoke();
            return !enabled || NativeDetails.IsEnabled;
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested)
        {
            NativeDetails.Invalidate(); return false;
        }
        finally { _nativeRequestLock.Release(); }
    }

    private void ReceiveNativeBase(CancellationTokenSource owner, ReadOnlyMemory<byte> payload)
    {
        lock (_framePublishLock)
        {
            if (!EnableNativeDetailReception || !IsCurrentConnection(owner) ||
                !_remoteCapabilities.HasFlag(RemoteDeviceCapabilities.NativeDetailV1)) return;
            RemoteFrame frame = NativeDetailSessionProtocol.DecodeBase(payload);
            if (ObserveNativeFrame(owner, ref frame, payload.Length + 25, datagramBase: false))
                PublishTcpVideoFrame(owner, frame, nativeEnvelope: true);
        }
    }

    // Caller holds _framePublishLock, including for borrowed UDP reassembly buffers.
    private bool ObserveNativeFrame(CancellationTokenSource owner, ref RemoteFrame frame, int reliableBytes, bool datagramBase)
    {
        if (datagramBase && NativeOffer is not { Extended: true }) return false;
        var identity = frame.NativeDetails!;
        ObserveNativeWire(owner, identity.Manifest, reliableBytes, datagramBase);
        if (!NativeDetails.Requested(identity)) return false;
        NativeDetails.ObserveNativeBase(identity);
        if (!NativeDetails.Accepts(identity)) frame = frame with { NativeDetails = null };
        return true;
    }

    private void ReceiveNativeChunk(CancellationTokenSource owner, ReadOnlySpan<byte> payload, bool datagramBase = false)
    {
        lock (_framePublishLock)
        {
            if (EnableNativeDetailReception && IsCurrentConnection(owner) &&
                _remoteCapabilities.HasFlag(RemoteDeviceCapabilities.NativeDetailV1) &&
                (!datagramBase || NativeOffer is { Extended: true }))
            {
                var feedback = GetNativeFeedbackOwner(owner, datagramBase);
                feedback.WireBytes = checked(feedback.WireBytes + payload.Length + 25);
                if (feedback.Latest is { } previous)
                    Volatile.Write(ref feedback.Latest, previous with { ReceivedWireBytes = feedback.WireBytes,
                        LocalReceivedAt = Stopwatch.GetTimestamp() });
                NativeDetails.TryQueueChunk(payload);
            }
        }
    }

    private void ReceiveNativeOffer(CancellationTokenSource owner, ReadOnlySpan<byte> payload)
    {
        if (!EnableNativeDetailReception || !IsCurrentConnection(owner) ||
            !_remoteCapabilities.HasFlag(RemoteDeviceCapabilities.NativeDetailV1)) return;
        NativeDetailOffer offer = NativeDetailSessionProtocol.DecodeOffer(payload);
        Volatile.Write(ref _nativeOffer, offer);
        NativeDetailOfferChanged?.Invoke(offer);
    }

    private void ReceiveNativeResume(CancellationTokenSource owner, ReadOnlySpan<byte> payload)
    {
        if (!EnableNativeDetailReception || !IsCurrentConnection(owner) ||
            !_remoteCapabilities.HasFlag(RemoteDeviceCapabilities.NativeDetailV1)) return;
        NativeDetailUdpResume resume = NativeDetailSessionProtocol.DecodeResume(payload);
        lock (_framePublishLock)
        {
            if (!IsCurrentConnection(owner) || !NativeDetails.IsStopRequest(resume.Context) ||
                _lowLatencyVideoTransport?.ResumeAfterNativeStop(resume) != true) return;
        }
        _ = SendControlAsync(NativeDetailSessionProtocol.EncodeResume(resume), owner, MessageType.NativeDetailUdpResumeAck);
    }

    private void ResetNativeConnection()
    {
        Interlocked.Exchange(ref _reliableFeedbackOwner, null)?.Receipts.Writer.TryComplete();
        NativeDetails.ResetConnection();
        Interlocked.Exchange(ref _nativeFeedbackOwner, null);
        Interlocked.Exchange(ref _nativeUdpFeedbackOwner, null);
        Volatile.Write(ref _nativeOffer, null);
        NativeDetailOfferChanged?.Invoke(null);
    }

    // Called only by the owning receive loop. The worker coalesces ACKs and
    // never makes decoding, rendering or input wait on a network write.
    private NativeFeedbackOwner GetNativeFeedbackOwner(CancellationTokenSource owner, bool datagramBase)
    {
        ref NativeFeedbackOwner? lane = ref (datagramBase ? ref _nativeUdpFeedbackOwner : ref _nativeFeedbackOwner);
        NativeFeedbackOwner? feedback = lane;
        if (feedback is null || !ReferenceEquals(feedback.Connection, owner))
        {
            feedback = new(owner, datagramBase);
            lane = feedback;
            _ = Task.Run(() => SendNativeFeedbackAsync(feedback));
        }
        return feedback;
    }

    private void ObserveNativeWire(CancellationTokenSource owner, DetailManifest manifest, int wireBytes, bool datagramBase)
    {
        NativeFeedbackOwner feedback = GetNativeFeedbackOwner(owner, datagramBase);
        feedback.WireBytes = checked(feedback.WireBytes + wireBytes);
        Volatile.Write(ref feedback.Latest, new(manifest.Context, manifest.Sequence, 0, feedback.WireBytes)
            { LocalReceivedAt = Stopwatch.GetTimestamp() });
    }

    private async Task SendNativeFeedbackAsync(NativeFeedbackOwner feedback)
    {
        long sent = 0, receivedSequence = 0, presentedSequence = 0;
        DetailContext sentContext = default;
        int nextPage = 0;
        try
        {
            while (IsCurrentConnection(feedback.Connection) && !feedback.Connection.IsCancellationRequested)
            {
                await Task.Delay(30, feedback.Connection.Token).ConfigureAwait(false);
                NativeDetailFeedback? latest = Volatile.Read(ref feedback.Latest);
                if (latest is null) continue;
                bool sameContext = latest.Context == sentContext;
                bool delivery = latest.ReceivedWireBytes > sent ||
                    (feedback.DatagramBase && (!sameContext || latest.ReceivedSequence != receivedSequence));
                // The last frame can leave the decoder after all the wire bytes have been
                // acknowledged. Report that advance once, even on an idle desktop.
                if (!delivery && (!sameContext || presentedSequence >= latest.ReceivedSequence ||
                    NativeDetails.LastPresentedSequence <= presentedSequence || !NativeDetails.IsEnabled ||
                    NativeDetails.RequestedContext != latest.Context)) continue;
                var receipt = NativeDetails.ReadReceipt(latest.Context, nextPage);
                long presented = Math.Min(latest.ReceivedSequence, receipt.Sequence);
                if (!delivery && presented <= presentedSequence) continue;
                if (sameContext && presented < presentedSequence && NativeDetails.IsEnabled &&
                    NativeDetails.RequestedContext == latest.Context)
                    presented = Math.Min(latest.ReceivedSequence, presentedSequence);
                nextPage = receipt.Page + 1;
                var message = latest with { PresentedSequence = presented,
                    CachedTileMask = receipt.Sequence == presented ? receipt.Tiles : 0,
                    TilePage = receipt.Sequence == presented ? receipt.Page : 0,
                    AcknowledgementDelayMilliseconds = delivery
                        ? Math.Clamp((int)Stopwatch.GetElapsedTime(latest.LocalReceivedAt).TotalMilliseconds, 0, 500) : 0 };
                // ReadReceipt assigns a nonzero page only to an explicitly negotiated request.
                if (!await SendControlAsync(NativeDetailSessionProtocol.EncodeFeedback(message, feedback.DatagramBase, extendedViewport: message.TilePage != 0), feedback.Connection,
                        feedback.DatagramBase ? MessageType.NativeDetailUdpFeedback : MessageType.NativeDetailFeedback).ConfigureAwait(false)) return;
                sent = latest.ReceivedWireBytes;
                receivedSequence = latest.ReceivedSequence;
                presentedSequence = presented;
                sentContext = latest.Context;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
    }
}
