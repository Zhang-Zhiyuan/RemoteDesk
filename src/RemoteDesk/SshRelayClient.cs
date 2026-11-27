using System.Net.Sockets;
using System.Text.Json;

namespace RemoteDesk;

internal sealed class SshRelayConnection : IDisposable
{
    private readonly TcpClient _client;
    private readonly Stream _relay;
    private readonly RelayTls.RelayTlsConnection? _direct;
    private CancellationTokenRegistration _registration;
    private int _disposed;
    internal Stream Stream { get; }

    internal bool UsesLan => _direct is not null;

    internal SshRelayConnection(TcpClient client, Stream stream, CancellationToken token, RelayTls.RelayTlsConnection? direct = null)
    {
        _client = client;
        _relay = stream;
        _direct = direct;
        Stream = direct?.Stream ?? stream;
        _registration = token.Register(static state => ((SshRelayConnection)state!).CloseTransport(), this);
        if (direct is not null) _ = WatchRelayAsync();
    }

    private async Task WatchRelayAsync()
    {
        try
        {
            // The host may already have forwarded one SSH identification block on the relay.
            // EOF still revokes the direct data stream, as does unexpected further data.
            byte[] buffer = new byte[1024];
            int remaining = SshLanRoute.MaximumPreface;
            while (remaining >= 0)
            {
                int read = await _relay.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1))).ConfigureAwait(false);
                if (read == 0) break;
                remaining -= read;
            }
        }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        finally { CloseTransport(); }
    }

    private void CloseTransport()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _direct?.Dispose(); }
        finally { try { _relay.Dispose(); } finally { _client.Dispose(); } }
    }

    public void Dispose()
    {
        _registration.Dispose();
        CloseTransport();
    }
}

internal static class SshRelayClient
{
    internal static async Task<SshRelayConnection> OpenAsync(RelayConnectionOptions options,
        string deviceKey, CancellationToken cancellationToken, bool allowLan = true)
    {
        options = options.Validate();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            try { return await OpenOnceAsync(allowLan).ConfigureAwait(false); }
            catch (SshLanRoute.CommitFailedException) when (allowLan && !deadline.IsCancellationRequested)
            { return await OpenOnceAsync(false).ConfigureAwait(false); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("SSH 中继连接超时；请检查目标在线状态和 SSH 服务。"); }

        async Task<SshRelayConnection> OpenOnceAsync(bool offerLan)
        {
            TcpClient? client = null;
            Stream? stream = null;
            try
            {
                (client, var tls) = await RelayTls.ConnectAsync(options, deadline.Token, dataTunnel: true).ConfigureAwait(false);
                stream = tls;
                await RelayTls.WriteJsonAsync(stream, new { version = 1, role = "ssh-viewer",
                    token = options.AccessToken, deviceId = options.DeviceId }, deadline.Token).ConfigureAwait(false);
                using JsonDocument response = await RelayTls.ReadJsonAsync(stream, deadline.Token).ConfigureAwait(false);
                EnsureSshPair(response.RootElement);
                var direct = await SshGateProtocol.OpenClientTransportAsync(stream, deviceKey, options.DeviceId, offerLan,
                    deadline.Token).ConfigureAwait(false);
                return new SshRelayConnection(client, stream, cancellationToken, direct);
            }
            catch { stream?.Dispose(); client?.Dispose(); throw; }
        }
    }

    internal static void EnsureSshPair(JsonElement response)
    {
        RelayTls.EnsureSuccess(response);
        if (!response.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "paired" ||
            !response.TryGetProperty("service", out var service) || service.ValueKind != JsonValueKind.String || service.GetString() != "ssh")
            throw new InvalidDataException("服务器未建立独立 SSH 通道，请更新中继服务器和目标客户端。");
    }
}
