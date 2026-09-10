using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;

namespace RemoteDesk;

internal sealed class RelayHostConnector : IDisposable
{
    private static readonly TimeSpan HeartbeatInterval =
        TimeSpan.FromSeconds(10);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20)
    ];

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, Task> _dataBridges = new();
    private readonly TimeSpan _handshakeTimeout;
    private readonly TimeSpan _heartbeatTimeout;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private bool _disposed;

    public RelayHostConnector(TimeSpan? handshakeTimeout = null, TimeSpan? heartbeatTimeout = null)
    {
        _handshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(10);
        _heartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(45);
    }

    public event Action<string>? StatusChanged;

    public bool IsRunning => _runTask is { IsCompleted: false };

    public async Task StartAsync(
        RelayConnectionOptions options,
        int localHostPort,
        CancellationToken cancellationToken = default)
    {
        options = options.Validate();
        if (localHostPort is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(localHostPort));
        }

        await _lifecycleLock.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopCoreAsync().ConfigureAwait(false);
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runTask = RunAsync(
                options,
                localHostPort,
                _runCancellation.Token);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task RunAsync(
        RelayConnectionOptions options,
        int localHostPort,
        CancellationToken cancellationToken)
    {
        int failedAttempts = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunControlConnectionAsync(
                        options,
                        localHostPort,
                        cancellationToken)
                    .ConfigureAwait(false);
                failedAttempts = 0;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (
                ex is RelayAccessDeniedException or
                    System.Security.Authentication.AuthenticationException)
            {
                PublishStatus(
                    $"中继身份校验失败，已停止重试：{ex.Message}");
                return;
            }
            catch (Exception ex) when (
                ex is IOException or SocketException or TimeoutException)
            {
                TimeSpan delay = RetryDelays[Math.Min(
                    failedAttempts,
                    RetryDelays.Length - 1)];
                failedAttempts++;
                PublishStatus(
                    $"中继离线：{ex.Message}，{delay.TotalSeconds:0} 秒后重试");
                try
                {
                    await Task.Delay(delay, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        PublishStatus("中继注册已停止");
    }

    private async Task RunControlConnectionAsync(
        RelayConnectionOptions options,
        int localHostPort,
        CancellationToken cancellationToken)
    {
        (TcpClient relayClient, SslStream relayStream) =
            await RelayTls.ConnectAsync(options, cancellationToken)
                .ConfigureAwait(false);
        using (relayClient)
        using (relayStream)
        {
            using var connectionStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken connectionToken = connectionStop.Token;
            using var writeLock = new SemaphoreSlim(1, 1);

            await WriteControlJsonAsync(
                relayStream,
                writeLock,
                new
                {
                    version = 1,
                    role = "host-control",
                    token = options.AccessToken,
                    deviceId = options.DeviceId,
                    machineName = Environment.MachineName,
                    platform = "Windows",
                    buildStamp = RemoteDeskBuildInfo.BuildStamp
                },
                cancellationToken)
            .ConfigureAwait(false);
            bool heartbeatAcknowledged;
            using (JsonDocument response = await RelayTls.ReadJsonWithTimeoutAsync(
                   relayStream,
                   _handshakeTimeout,
                   cancellationToken)
               .ConfigureAwait(false))
            {
                RelayTls.EnsureSuccess(response.RootElement);
                heartbeatAcknowledged = response.RootElement.TryGetProperty("heartbeatAck", out JsonElement ack) &&
                    ack.ValueKind == JsonValueKind.True;
            }

            PublishStatus(
                $"已上线到中继 {options.ServerAddress}:{options.Port} · 本地端点 " +
                RelayNetworkPathSelector.DescribeLocalEndpoint(relayClient.Client.LocalEndPoint));
            Task heartbeat = RunHeartbeatAsync(
                relayStream,
                writeLock,
                connectionToken);
            Task<JsonDocument>? pendingRead = null;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    pendingRead = RelayTls.ReadJsonWithTimeoutAsync(
                            relayStream,
                            heartbeatAcknowledged ? _heartbeatTimeout : Timeout.InfiniteTimeSpan,
                            connectionToken);
                    if (await Task.WhenAny(pendingRead, heartbeat).ConfigureAwait(false) == heartbeat)
                    {
                        await heartbeat.ConfigureAwait(false);
                        connectionToken.ThrowIfCancellationRequested();
                        throw new IOException("中继心跳发送已停止。");
                    }
                    using JsonDocument message = await pendingRead.ConfigureAwait(false);
                    pendingRead = null;
                    JsonElement root = message.RootElement;
                    string? type = root.TryGetProperty(
                            "type",
                            out JsonElement typeValue) &&
                        typeValue.ValueKind == JsonValueKind.String
                            ? typeValue.GetString()
                            : null;
                    if (!string.Equals(
                            type,
                            "open",
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string? sessionId = root.TryGetProperty(
                            "sessionId",
                            out JsonElement sessionValue) &&
                        sessionValue.ValueKind == JsonValueKind.String
                            ? sessionValue.GetString()
                            : null;
                    if (!Guid.TryParse(sessionId, out _))
                    {
                        continue;
                    }

                    StartDataBridge(
                        options,
                        localHostPort,
                        sessionId!,
                        connectionToken);
                }
            }
            finally
            {
                connectionStop.Cancel();
                relayClient.Close();
                if (pendingRead is not null)
                {
                    try { (await pendingRead.ConfigureAwait(false)).Dispose(); }
                    catch (Exception ex) when (ex is IOException or SocketException or
                        ObjectDisposedException or OperationCanceledException or TimeoutException) { }
                }
                try
                {
                    await heartbeat.ConfigureAwait(false);
                }
                catch (Exception ex) when (
                    ex is IOException or SocketException or
                        ObjectDisposedException or OperationCanceledException)
                {
                }
            }
        }
    }

    private void StartDataBridge(
        RelayConnectionOptions options,
        int localHostPort,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || _dataBridges.Count >= 16)
        {
            return;
        }
        Guid bridgeId = Guid.NewGuid();
        Task bridge = RunDataBridgeAsync(
            options,
            localHostPort,
            sessionId,
            cancellationToken);
        _dataBridges[bridgeId] = bridge;
        _ = bridge.ContinueWith(
            static (completed, state) =>
            {
                var tuple = ((RelayHostConnector Owner, Guid Id))state!;
                tuple.Owner._dataBridges.TryRemove(tuple.Id, out _);
                if (completed.IsFaulted)
                {
                    _ = completed.Exception;
                }
            },
            (this, bridgeId),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task RunDataBridgeAsync(
        RelayConnectionOptions options,
        int localHostPort,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var localClient = new TcpClient();
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(TimeSpan.FromSeconds(10));
        RelayLoopbackPolicy.Configure(localClient);
        try
        {
            await localClient.ConnectAsync(
                    IPAddress.Loopback,
                    localHostPort,
                    handshake.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            localClient.Dispose();
            throw;
        }

        TcpClient? relayClient = null;
        SslStream? relayStream = null;
        try
        {
            (relayClient, relayStream) = await RelayTls.ConnectAsync(
                    options,
                    handshake.Token,
                    dataTunnel: true)
                .ConfigureAwait(false);
            await RelayTls.WriteJsonAsync(
                    relayStream,
                    new
                    {
                        version = 1,
                        role = "host-data",
                        token = options.AccessToken,
                        deviceId = options.DeviceId,
                        sessionId
                    },
                    handshake.Token)
                .ConfigureAwait(false);
            using (JsonDocument response = await RelayTls.ReadJsonAsync(
                       relayStream,
                       handshake.Token)
                   .ConfigureAwait(false))
            {
                RelayTls.EnsureSuccess(response.RootElement);
            }
            handshake.CancelAfter(Timeout.InfiniteTimeSpan);

            await RelayStreamBridge.RunAsync(
                    localClient.GetStream(),
                    relayStream,
                    localClient,
                    relayClient,
                    cancellationToken)
                .ConfigureAwait(false);
            relayClient = null;
            localClient = null!;
        }
        finally
        {
            relayStream?.Dispose();
            relayClient?.Dispose();
            localClient?.Dispose();
        }
    }

    private static async Task RunHeartbeatAsync(
        Stream stream,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(HeartbeatInterval, cancellationToken)
                .ConfigureAwait(false);
            await WriteControlJsonAsync(
                    stream,
                    writeLock,
                    new
                    {
                        version = 1,
                        type = "heartbeat",
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteControlJsonAsync<T>(
        Stream stream,
        SemaphoreSlim writeLock,
        T value,
        CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await RelayTls.WriteJsonAsync(
                    stream,
                    value,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        CancellationTokenSource? cancellation = _runCancellation;
        Task? runTask = _runTask;
        _runCancellation = null;
        _runTask = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            if (runTask is not null)
            {
                await runTask.WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
            }
            await Task.WhenAll(_dataBridges.Values.ToArray())
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or TimeoutException or
                IOException or SocketException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void PublishStatus(string status)
    {
        try
        {
            StatusChanged?.Invoke(status);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }
        // RunningChanged and checkbox callbacks can already be waiting in StopAsync.
        // The managed semaphore remains valid until those callbacks have drained.
    }
}
