using System.Net;
using System.Net.Sockets;

namespace RemoteDesk;

// One owned OpenSSH client, one authenticated raw SSH stream. Never accepts
// external interfaces, a second client, or a second channel after disconnect.
internal sealed class SshLocalForwarder : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Stream _remote;
    private readonly IDisposable _owner;
    private readonly CancellationTokenSource _cancel = new();
    private readonly CancellationToken _token;
    private TcpClient? _local;
    private int _disposed;
    internal int Port { get; }
    internal Task Completion { get; }

    internal SshLocalForwarder(Stream remote, IDisposable owner, TimeSpan? acceptTimeout = null)
    {
        _remote = remote;
        _owner = owner;
        _token = _cancel.Token;
        _listener.Start(1);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Completion = RunAsync(acceptTimeout ?? TimeSpan.FromSeconds(60));
    }

    private async Task RunAsync(TimeSpan acceptTimeout)
    {
        try
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_token))
            {
                deadline.CancelAfter(acceptTimeout);
                try { _local = await _listener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!_token.IsCancellationRequested)
                { throw new TimeoutException("SSH 终端未连接，临时本机端口已关闭。"); }
            }
            _listener.Stop();
            _token.ThrowIfCancellationRequested();
            _local.NoDelay = true;
            using NetworkStream local = _local.GetStream();
            Task outgoing = local.CopyToAsync(_remote, _token);
            Task incoming = _remote.CopyToAsync(local, _token);
            await Task.WhenAny(outgoing, incoming).ConfigureAwait(false);
            Dispose(); // EOF/error on either side must release both ends.
            try { await Task.WhenAll(outgoing, incoming).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
        catch (Exception error) when (_token.IsCancellationRequested && error is OperationCanceledException or ObjectDisposedException or SocketException) { }
        finally { _local?.Dispose(); Dispose(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancel.Cancel();
        _listener.Stop();
        _local?.Dispose();
        _owner.Dispose();
        _cancel.Dispose();
    }
}
