using System.Net.Sockets;
using System.Text.Json;

namespace RemoteDesk;

internal sealed class SshRelayConnection : IDisposable
{
    private readonly TcpClient _client;
    private CancellationTokenRegistration _registration;
    private int _disposed;
    internal Stream Stream { get; }

    internal SshRelayConnection(TcpClient client, Stream stream, CancellationToken token)
    {
        _client = client;
        Stream = stream;
        _registration = token.Register(static state => ((SshRelayConnection)state!).CloseTransport(), this);
    }

    private void CloseTransport()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { Stream.Dispose(); } finally { _client.Dispose(); }
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
        string deviceKey, CancellationToken cancellationToken)
    {
        options = options.Validate();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
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
            await SshGateProtocol.AuthenticateClientAsync(stream, deviceKey, options.DeviceId, deadline.Token).ConfigureAwait(false);
            return new SshRelayConnection(client, stream, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stream?.Dispose(); client?.Dispose();
            throw new TimeoutException("SSH 中继连接超时；请检查目标在线状态和 SSH 服务。");
        }
        catch { stream?.Dispose(); client?.Dispose(); throw; }
    }

    internal static void EnsureSshPair(JsonElement response)
    {
        RelayTls.EnsureSuccess(response);
        if (!response.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "paired" ||
            !response.TryGetProperty("service", out var service) || service.ValueKind != JsonValueKind.String || service.GetString() != "ssh")
            throw new InvalidDataException("服务器未建立独立 SSH 通道，请更新中继服务器和目标客户端。");
    }
}
