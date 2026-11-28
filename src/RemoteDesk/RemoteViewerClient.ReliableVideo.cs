using System.Diagnostics;
using System.Threading.Channels;

namespace RemoteDesk;

internal sealed partial class RemoteViewerClient
{
    private ReliableFeedbackOwner? _reliableFeedbackOwner;
    private sealed class ReliableFeedbackOwner(CancellationTokenSource connection)
    {
        internal CancellationTokenSource Connection { get; } = connection;
        internal long Sequence;
        internal long WireBytes;
        internal readonly Channel<ReliableVideoReceipt> Receipts = Channel.CreateBounded<ReliableVideoReceipt>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
    }

    private void ReceiveTrackedVideoFrame(CancellationTokenSource owner, ReadOnlyMemory<byte> payload)
    {
        lock (_framePublishLock)
        {
            if (!IsCurrentConnection(owner)) return;
            if (!_remoteCapabilities.HasFlag(RemoteDeviceCapabilities.ReliableVideoFeedback))
                throw new InvalidDataException("Unnegotiated reliable video frame.");
            var (sequence, frame) = ReliableVideoFeedbackProtocol.DecodeFrame(payload);
            ReliableFeedbackOwner? feedback = _reliableFeedbackOwner;
            bool start = feedback is null || !ReferenceEquals(feedback.Connection, owner);
            if (start) _reliableFeedbackOwner = feedback = new(owner);
            if (sequence != feedback!.Sequence + 1)
                throw new InvalidDataException("Non-contiguous reliable video sequence.");
            feedback.Sequence = sequence;
            feedback.WireBytes = checked(feedback.WireBytes + payload.Length + 25);
            feedback.Receipts.Writer.TryWrite(new(sequence, feedback.WireBytes) { ReceivedAtTimestamp = Stopwatch.GetTimestamp() });
            if (start) _ = Task.Run(() => SendReliableVideoFeedbackAsync(feedback));
            PublishTcpVideoFrame(owner, frame);
        }
    }

    // At most one coalescing worker per connection. Network ACKs do not wait
    // for the UI, decoder or GPU, and never make the receive loop wait to send.
    private async Task SendReliableVideoFeedbackAsync(ReliableFeedbackOwner owner)
    {
        long lastSentAt = 0;
        TimeSpan minimumInterval = TimeSpan.FromMilliseconds(20);
        try
        {
            CancellationToken token = owner.Connection.Token;
            while (await owner.Receipts.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                // An isolated frame frees the host's send window immediately. At high
                // frame rates take the newest cumulative receipt after the rate limit.
                if (lastSentAt != 0)
                {
                    TimeSpan remaining = minimumInterval - Stopwatch.GetElapsedTime(lastSentAt);
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token).ConfigureAwait(false);
                }
                if (!IsCurrentConnection(owner.Connection) || token.IsCancellationRequested) return;
                if (!owner.Receipts.Reader.TryRead(out var latest)) continue;
                var receipt = latest with { AckDelayMilliseconds = Math.Clamp(
                    (int)Stopwatch.GetElapsedTime(latest.ReceivedAtTimestamp).TotalMilliseconds, 0, 500) };
                if (!await SendControlAsync(ReliableVideoFeedbackProtocol.EncodeReceipt(receipt), owner.Connection,
                        MessageType.ReliableVideoFeedback).ConfigureAwait(false)) return;
                lastSentAt = Stopwatch.GetTimestamp();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
    }
}
