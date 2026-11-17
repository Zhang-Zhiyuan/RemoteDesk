using System.Net.Sockets;

namespace RemoteDesk;

internal sealed partial class RemoteHostServer
{
    private static string DescribeReliableVideoWindow(ViewerSessionState viewer) =>
        viewer.Capabilities.HasFlag(RemoteDeviceCapabilities.ReliableVideoFeedback)
            ? $"，收帧回执 {viewer.ReliableVideo.LastRoundTripMilliseconds:F0}ms，未确认 {viewer.ReliableVideo.PendingBytes / 1024}KiB/{viewer.ReliableVideo.PendingFrames} 帧，发送前跳过 {viewer.ReliableVideo.DroppedFrames} 帧"
            : string.Empty;

    private static async Task<ProtocolFrameWriteTimings?> WriteReliableH264Async(NetworkStream stream,
        ViewerSessionState viewer, int width, int height, RemoteFrameFlags flags,
        double captureMs, double encodeMs, ReadOnlyMemory<byte> bytes, SecureSession session,
        SemaphoreSlim writeLock, CancellationToken token, bool independentFrames = true)
    {
        if (independentFrames && viewer.Capabilities.HasFlag(RemoteDeviceCapabilities.ReliableVideoFeedback))
        {
            if (!viewer.ReliableVideo.TryReserve(checked(bytes.Length + ReliableVideoFeedbackProtocol.FrameOverhead),
                    Environment.TickCount64, out long sequence)) return null;
            return await Protocol.WriteTrackedVideoFrameAsync(stream, sequence, width, height, flags,
                captureMs, encodeMs, bytes, session, writeLock, token).ConfigureAwait(false);
        }
        return await Protocol.WriteVideoFrameMessageAsync(stream, width, height, RemoteFrameEncoding.H264AnnexB,
            flags, captureMs, encodeMs, bytes, session, writeLock, token).ConfigureAwait(false);
    }
}
