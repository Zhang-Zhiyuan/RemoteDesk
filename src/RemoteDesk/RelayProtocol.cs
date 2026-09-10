using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace RemoteDesk;

internal static class RelayTls
{
    private const int MaxJsonFrameBytes = 64 * 1024;
    internal const int DataSendBufferBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<(TcpClient Client, SslStream Stream)> ConnectAsync(
        RelayConnectionOptions options,
        CancellationToken cancellationToken,
        TimeSpan? connectionTimeout = null,
        bool dataTunnel = false)
    {
        options = options.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(connectionTimeout ?? TimeSpan.FromSeconds(10));
        try
        {
            RelayTlsConnection connection = await RelayNetworkPathSelector.Shared.ConnectAsync(options,
                (path, token) => ConnectPathAsync(options, path, token, dataTunnel), timeout.Token)
                .ConfigureAwait(false);
            try { RelayConnectionActivity.Shared.Track(connection.Client, connection.Stream); }
            catch { connection.Dispose(); throw; }
            return (connection.Client, connection.Stream);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("连接中继服务器超时，请检查地址、端口和云安全组。");
        }
    }

    internal sealed class RelayTlsConnection(TcpClient client, SslStream stream) : IDisposable
    {
        internal TcpClient Client { get; } = client;
        internal SslStream Stream { get; } = stream;
        public void Dispose()
        {
            try { Stream.Dispose(); }
            finally { Client.Dispose(); }
        }
    }

    internal static async Task<RelayTlsConnection> ConnectPathAsync(
        RelayConnectionOptions options, RelayNetworkPath? path,
        CancellationToken cancellationToken, bool dataTunnel)
    {
        var client = path is null ? new TcpClient() : new TcpClient(AddressFamily.InterNetwork);
        SslStream? stream = null;
        bool certificateRejected = false;
        ConfigureTcpClient(client, dataTunnel);

        try
        {
            if (path is not null)
            {
                path.Bind(client.Client);
                await client.ConnectAsync(path.RemoteAddress, options.Port, cancellationToken)
                    .ConfigureAwait(false);
            }
            else await client.ConnectAsync(
                    options.ServerAddress,
                    options.Port,
                    cancellationToken)
                .ConfigureAwait(false);

            string expectedFingerprint = options.TlsCertificateSha256;
            stream = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false,
                (_, certificate, _, _) =>
                {
                    bool matches = CertificateMatches(certificate, expectedFingerprint);
                    certificateRejected |= !matches;
                    return matches;
                });
            await stream.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = options.ServerAddress,
                        EnabledSslProtocols =
                            SslProtocols.Tls12 |
                            SslProtocols.Tls13,
                        CertificateRevocationCheckMode =
                            X509RevocationMode.NoCheck
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return new RelayTlsConnection(client, stream);
        }
        catch (AuthenticationException ex) when (!certificateRejected)
        {
            stream?.Dispose();
            client.Dispose();
            // Schannel also uses AuthenticationException for a peer's fatal
            // alert before certificate exchange. Only an actual pin rejection
            // is an identity failure; interrupted negotiation must be retryable.
            throw new IOException("中继 TLS 握手中断，请稍后重试。", ex);
        }
        catch
        {
            stream?.Dispose();
            client.Dispose();
            throw;
        }
    }

    internal static void ConfigureTcpClient(TcpClient client, bool dataTunnel)
    {
        // Setting SO_RCVBUF explicitly disables Windows receive-window
        // autotuning. A fixed 128 KiB window caps a 300 ms WAN at ~3.5 Mbps,
        // even though a LAN-backed viewer can drain frames immediately.
        // Leave data receive sizing to TCP; bound application copies and the
        // send buffer separately rather than shrinking the in-flight window.
        NetworkUtils.ConfigureLowLatencyTcpClient(
            client,
            receiveBufferSize: dataTunnel ? 0 : 64 * 1024,
            sendBufferSize: dataTunnel ? DataSendBufferBytes : 32 * 1024);
    }

    public static async Task WriteJsonAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            value,
            JsonOptions);
        if (payload.Length is 0 or > MaxJsonFrameBytes)
        {
            throw new RelayProtocolException("中继握手消息大小无效。");
        }

        // One TLS write per small control message avoids a separate record and
        // packet for its four-byte length prefix on high-RTT links.
        byte[] frame = GC.AllocateUninitializedArray<byte>(sizeof(int) + payload.Length);
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(sizeof(int)));
        await stream.WriteAsync(frame, cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<JsonDocument> ReadJsonAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] lengthBytes = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(lengthBytes, cancellationToken)
            .ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        if (length is <= 0 or > MaxJsonFrameBytes)
        {
            throw new RelayProtocolException("中继返回了无效的握手消息。");
        }

        byte[] payload = GC.AllocateUninitializedArray<byte>(length);
        await stream.ReadExactlyAsync(payload, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            JsonDocument document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new RelayProtocolException("中继返回的消息必须是对象。");
            }
            RelayConnectionActivity.Shared.Find(stream)?.Touch();
            return document;
        }
        catch (JsonException ex)
        {
            throw new RelayProtocolException(
                $"中继返回的消息无法解析：{ex.Message}");
        }
    }

    public static async Task<JsonDocument> ReadJsonWithTimeoutAsync(
        Stream stream,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return await ReadJsonAsync(stream, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("中继服务器响应超时。");
        }
    }

    public static void EnsureSuccess(JsonElement root)
    {
        if (root.TryGetProperty("ok", out JsonElement ok) &&
            ok.ValueKind == JsonValueKind.True)
        {
            return;
        }

        string message = root.TryGetProperty(
                "error",
                out JsonElement error) &&
            error.ValueKind == JsonValueKind.String
                ? error.GetString() ?? "中继拒绝了连接。"
                : "中继拒绝了连接。";
        if (message.Contains(
                "访问密钥",
                StringComparison.Ordinal))
        {
            throw new RelayAccessDeniedException(message);
        }

        throw new RelayProtocolException(message);
    }

    public static string NormalizeFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return string.Empty;
        }

        return new string(
            fingerprint
                .Where(Uri.IsHexDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());
    }

    public static string GetSha256Fingerprint(byte[] certificateBytes) =>
        Convert.ToHexString(SHA256.HashData(certificateBytes));

    private static bool CertificateMatches(
        X509Certificate? certificate,
        string expectedFingerprint)
    {
        if (certificate is null)
        {
            return false;
        }

        byte[] actual = SHA256.HashData(certificate.GetRawCertData());
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedFingerprint);
        }
        catch (FormatException)
        {
            return false;
        }

        return actual.Length == expected.Length &&
            CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

internal static class RelayLoopbackPolicy
{
    internal const int BufferBytes = 16 * 1024;

    internal static int HostSendBufferBytes(EndPoint? peer, int directBufferBytes) =>
        peer is IPEndPoint endpoint &&
        IPAddress.IsLoopback(endpoint.Address.IsIPv4MappedToIPv6
            ? endpoint.Address.MapToIPv4() : endpoint.Address)
            ? BufferBytes : directBufferBytes;

    // Only private loopback adapters call this. Public TLS retains receive
    // autotuning and its existing in-flight send budget.
    internal static void Configure(TcpClient client) =>
        NetworkUtils.ConfigureLowLatencyTcpClient(client, BufferBytes, BufferBytes);
}

internal static class RelayStreamBridge
{
    internal const int CopyBufferBytes = 16 * 1024;
    public static async Task RunAsync(
        Stream first,
        Stream second,
        IDisposable firstOwner,
        IDisposable secondOwner,
        CancellationToken cancellationToken)
    {
        using (firstOwner)
        using (secondOwner)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            Task forward = PumpAsync(first, second, stop.Token);
            Task reverse = PumpAsync(second, first, stop.Token);
            await Task.WhenAny(forward, reverse).ConfigureAwait(false);
            stop.Cancel();
            TryDispose(first);
            TryDispose(second);
            await IgnoreBridgeEndAsync(forward).ConfigureAwait(false);
            await IgnoreBridgeEndAsync(reverse).ConfigureAwait(false);
        }
    }

    private static async Task PumpAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        RelayConnectionActivity.Entry? activity = RelayConnectionActivity.Shared.Find(source);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            while (true)
            {
                int count = await source.ReadAsync(buffer.AsMemory(0, CopyBufferBytes), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                activity?.Touch();
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
        await destination.FlushAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task IgnoreBridgeEndAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is IOException or SocketException or
                ObjectDisposedException or OperationCanceledException or
                AuthenticationException)
        {
        }
    }

    private static void TryDispose(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch
        {
        }
    }
}
