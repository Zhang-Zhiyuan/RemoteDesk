using System.Threading.Channels;

namespace RemoteDesk;

// A Pong must use the normal serialized/encrypted writer, but waiting for that
// writer must not stop the reader from receiving mouse/key releases. At most
// one reply is active and one pending, even if a peer floods Ping messages.
internal sealed class HostHeartbeatResponder : IAsyncDisposable
{
    private readonly Channel<bool> _pending = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false
        });
    private readonly CancellationTokenSource _stop;
    private readonly Func<CancellationToken, Task> _send;
    private readonly Task _worker;

    public HostHeartbeatResponder(
        Func<CancellationToken, Task> send,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);
        _send = send;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _worker = Task.Run(RunAsync);
    }

    public CancellationToken Token => _stop.Token;
    public Exception? Failure { get; private set; }

    public bool Request() => !_stop.IsCancellationRequested && _pending.Writer.TryWrite(true);

    private async Task RunAsync()
    {
        try
        {
            while (await _pending.Reader.WaitToReadAsync(_stop.Token))
            {
                if (_pending.Reader.TryRead(out _))
                {
                    _stop.Token.ThrowIfCancellationRequested();
                    await _send(_stop.Token);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            Failure = error;
            // Wake the session's read immediately; do not leave a failed Pong
            // writer and an apparently live input connection running forever.
            _stop.Cancel();
        }
        finally { _pending.Writer.TryComplete(); }
    }

    public async ValueTask DisposeAsync()
    {
        _pending.Writer.TryComplete();
        _stop.Cancel();
        await _worker;
        _stop.Dispose();
    }
}
