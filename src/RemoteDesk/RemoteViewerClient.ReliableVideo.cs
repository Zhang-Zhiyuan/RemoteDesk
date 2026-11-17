using System.Diagnostics;

namespace RemoteDesk;

internal sealed partial class RemoteViewerClient
{
    private ReliableFeedbackOwner? _reliableFeedbackOwner;
    private sealed class ReliableFeedbackOwner(CancellationTokenSource connection)
    {
        internal CancellationTokenSource Connection { get; } = connection;
        internal ReliableVideoReceipt? Latest;
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
            ReliableVideoReceipt? previous = Volatile.Read(ref feedback!.Latest);
            if (sequence != (previous?.Sequence ?? 0) + 1)
                throw new InvalidDataException("Non-contiguous reliable video sequence.");
            Volatile.Write(ref feedback.Latest, new(sequence, checked((previous?.WireBytes ?? 0) + payload.Length + 25))
                { ReceivedAtTimestamp = Stopwatch.GetTimestamp() });
            if (start) _ = Task.Run(() => SendReliableVideoFeedbackAsync(feedback));
            PublishTcpVideoFrame(owner, frame);
        }
    }

    // At most one coalescing worker per connection. Network ACKs do not wait
    // for the UI, decoder or GPU, and never make the receive loop wait to send.
    private async Task SendReliableVideoFeedbackAsync(ReliableFeedbackOwner owner)
    {
        long sentSequence = 0;
        try
        {
            while (IsCurrentConnection(owner.Connection) && !owner.Connection.IsCancellationRequested)
            {
                await Task.Delay(20, owner.Connection.Token).ConfigureAwait(false);
                ReliableVideoReceipt? latest = Volatile.Read(ref owner.Latest);
                if (latest is null || latest.Sequence <= sentSequence) continue;
                var receipt = latest with { AckDelayMilliseconds = Math.Clamp(
                    (int)Stopwatch.GetElapsedTime(latest.ReceivedAtTimestamp).TotalMilliseconds, 0, 500) };
                if (!await SendControlAsync(ReliableVideoFeedbackProtocol.EncodeReceipt(receipt), owner.Connection,
                        MessageType.ReliableVideoFeedback).ConfigureAwait(false)) return;
                sentSequence = latest.Sequence;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
    }
}
