using System.Buffers.Binary;
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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<(TcpClient Client, SslStream Stream)> ConnectAsync(
        RelayConnectionOptions options,
        CancellationToken cancellationToken,
        TimeSpan? connectionTimeout = null)
    {
        options = options.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(connectionTimeout ?? TimeSpan.FromSeconds(10));
        var client = new TcpClient();
        SslStream? stream = null;
        NetworkUtils.ConfigureLowLatencyTcpClient(
            client,
            RemoteViewerClient.FrameReceiveBufferBytes,
            32 * 1024);

        try
        {
            await client.ConnectAsync(
                    options.ServerAddress,
                    options.Port,
                    timeout.Token)
                .ConfigureAwait(false);

            string expectedFingerprint = options.TlsCertificateSha256;
            stream = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false,
                (_, certificate, _, _) =>
                    CertificateMatches(certificate, expectedFingerprint));
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
                    timeout.Token)
                .ConfigureAwait(false);
            return (client, stream);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stream?.Dispose();
            client.Dispose();
            throw new TimeoutException("连接中继服务器超时，请检查地址、端口和云安全组。");
        }
        catch
        {
            stream?.Dispose();
            client.Dispose();
            throw;
        }
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

        byte[] length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        await stream.WriteAsync(length, cancellationToken)
            .ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken)
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

internal static class RelayStreamBridge
{
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
        await source.CopyToAsync(
                destination,
                128 * 1024,
                cancellationToken)
            .ConfigureAwait(false);
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
