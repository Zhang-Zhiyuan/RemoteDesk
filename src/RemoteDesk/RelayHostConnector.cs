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
    private readonly SemaphoreSlim _addressRefresh = new(0, 1);
    private readonly Func<IReadOnlyList<string>> _addressProvider;
    private readonly Func<RelayConnectionOptions, CancellationToken, Task>? _prepareConnection;
    private readonly Func<RelaySshHostOptions> _sshOptionsProvider;
    private readonly ConcurrentDictionary<Guid, Task> _dataBridges = new();
    private readonly ConcurrentDictionary<Guid, SshBridgeState> _sshBridges = new();
    private readonly SemaphoreSlim _sshSlots = new(4, 4);
    private sealed class SshBridgeState(RelaySshHostOptions options, CancellationTokenSource cancellation)
    {
        internal RelaySshHostOptions Options { get; } = options;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal Task Task { get; set; } = Task.CompletedTask;
    }
    private readonly TimeSpan _handshakeTimeout;
    private readonly TimeSpan _heartbeatTimeout;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private RelayConnectionOptions? _activeOptions;
    private int _localHostPort;
    private bool _disposed;

    public RelayHostConnector(TimeSpan? handshakeTimeout = null, TimeSpan? heartbeatTimeout = null,
        Func<IReadOnlyList<string>>? addressProvider = null,
        Func<RelayConnectionOptions, CancellationToken, Task>? prepareConnection = null,
        Func<RelaySshHostOptions>? sshOptionsProvider = null)
    {
        _addressProvider = addressProvider ?? RelayAddressReport.LocalAddresses;
        _prepareConnection = prepareConnection;
        _sshOptionsProvider = sshOptionsProvider ?? (() => RelaySshHostOptions.Disabled);
        _handshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(10);
        _heartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(45);
    }

    public event Action<string>? StatusChanged;

    public bool IsRunning => _runTask is { IsCompleted: false };

    public bool RequestAddressRefresh()
    {
        if (_disposed || !IsRunning) return false;
        RefreshSshConfiguration();
        try { _addressRefresh.Release(); } catch (SemaphoreFullException) { }
        return true;
    }

    public async Task StartAsync(
        RelayConnectionOptions options,
        int localHostPort,
        CancellationToken cancellationToken = default)
    {
        options = options.Validate();
        if (localHostPort is < 0 or > 65535 || (localHostPort == 0 && !_sshOptionsProvider().IsAvailable))
        {
            throw new ArgumentOutOfRangeException(
                nameof(localHostPort));
        }

        await _lifecycleLock.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning && _runCancellation?.IsCancellationRequested != true && _activeOptions == options)
            {
                // Switching desktop availability must not end an independent
                // authenticated SSH shell on the same registered device.
                Volatile.Write(ref _localHostPort, localHostPort);
                RequestAddressRefresh();
                return;
            }
            await StopCoreAsync().ConfigureAwait(false);
            _activeOptions = options;
            Volatile.Write(ref _localHostPort, localHostPort);
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
                // Reconnection used to bypass the desktop's route optimizer,
                // permanently keeping the system fallback after a Wi-Fi change.
                // Prepare before opening any replacement registration socket.
                if (_prepareConnection is not null)
                {
                    try
                    {
                        await _prepareConnection(options, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        // Optimization is optional; normal TLS below still
                        // verifies the saved identity and reports its own errors.
                        PublishStatus("中继线路优化暂不可用，继续尝试正常连接");
                    }
                }
                await RunControlConnectionAsync(
                        options,
                        localHostPort,
                        () => failedAttempts = 0,
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
        Action registered,
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
            IReadOnlyList<string> registeredAddresses = RelayAddressReport.Normalize(_addressProvider());
            localHostPort = Volatile.Read(ref _localHostPort);

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
                    buildStamp = RemoteDeskBuildInfo.BuildStamp,
                    directAddresses = registeredAddresses,
                    directPort = localHostPort,
                    services = Services(localHostPort)
                },
                cancellationToken)
            .ConfigureAwait(false);
            bool heartbeatAcknowledged;
            bool addressReporting;
            bool sshTunneling;
            using (JsonDocument response = await RelayTls.ReadJsonWithTimeoutAsync(
                   relayStream,
                   _handshakeTimeout,
                   cancellationToken)
               .ConfigureAwait(false))
            {
                RelayTls.EnsureSuccess(response.RootElement);
                heartbeatAcknowledged = response.RootElement.TryGetProperty("heartbeatAck", out JsonElement ack) &&
                    ack.ValueKind == JsonValueKind.True;
                addressReporting = response.RootElement.TryGetProperty("addressReporting", out var reportAck) &&
                    reportAck.ValueKind == JsonValueKind.True;
                sshTunneling = response.RootElement.TryGetProperty("sshTunneling", out var sshAck) &&
                    sshAck.ValueKind == JsonValueKind.True;
            }

            registered();
            PublishStatus(
                $"已上线到中继 {options.ServerAddress}:{options.Port} · 本地端点 " +
                RelayNetworkPathSelector.DescribeLocalEndpoint(relayClient.Client.LocalEndPoint) +
                (addressReporting ? " · IP / 端口自动更新已启用" : " · 服务器需更新才能显示 IP") +
                (_sshOptionsProvider().IsAvailable && !sshTunneling ? " · 中继服务器需更新才能使用 SSH" : ""));
            Task heartbeat = RunHeartbeatAsync(
                relayStream,
                writeLock,
                localHostPort,
                registeredAddresses,
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
                    if (type is not ("open" or "open-ssh"))
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

                    if (type == "open-ssh")
                    {
                        if (sshTunneling) StartSshBridge(options, sessionId!, connectionToken);
                        continue;
                    }
                    int desktopPort = Volatile.Read(ref _localHostPort);
                    if (desktopPort == 0) continue;
                    StartDataBridge(
                        options,
                        desktopPort,
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

    private string[] Services(int localHostPort)
    {
        RelaySshHostOptions ssh = RefreshSshConfiguration();
        return localHostPort > 0 ? (ssh.IsAvailable ? ["desktop", "ssh"] : ["desktop"])
            : (ssh.IsAvailable ? ["ssh"] : []);
    }

    private RelaySshHostOptions RefreshSshConfiguration()
    {
        RelaySshHostOptions current = _sshOptionsProvider();
        foreach (SshBridgeState bridge in _sshBridges.Values)
        {
            if (bridge.Options == current && current.IsAvailable) continue;
            try { bridge.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
        return current;
    }

    private void StartSshBridge(RelayConnectionOptions options, string sessionId, CancellationToken token)
    {
        RelaySshHostOptions ssh = RefreshSshConfiguration();
        if (!ssh.IsAvailable || token.IsCancellationRequested || !_sshSlots.Wait(0)) return;
        Guid id = Guid.NewGuid();
        var state = new SshBridgeState(ssh, CancellationTokenSource.CreateLinkedTokenSource(token));
        _sshBridges[id] = state;
        // A UI revocation can occur after the first options snapshot but before
        // this state becomes visible. Revalidate after publication so that it
        // cannot miss this bridge; subsequent refreshes can always cancel it.
        RefreshSshConfiguration();
        state.Task = RunSshBridgeAsync(options, sessionId, ssh, state.Cancellation.Token);
        _ = state.Task.ContinueWith(completed =>
        {
            _sshBridges.TryRemove(id, out _);
            state.Cancellation.Dispose();
            _sshSlots.Release();
            if (completed.IsFaulted) _ = completed.Exception;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task RunSshBridgeAsync(RelayConnectionOptions options, string sessionId,
        RelaySshHostOptions ssh, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var (client, stream) = await RelayTls.ConnectAsync(options, deadline.Token, dataTunnel: true).ConfigureAwait(false);
        using (client)
        using (stream)
        {
            await RelayTls.WriteJsonAsync(stream, new { version = 1, role = "host-data", service = "ssh",
                token = options.AccessToken, deviceId = options.DeviceId, sessionId }, deadline.Token).ConfigureAwait(false);
            using (JsonDocument response = await RelayTls.ReadJsonAsync(stream, deadline.Token).ConfigureAwait(false))
                SshRelayClient.EnsureSshPair(response.RootElement);
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            await SshHostBridge.RunAsync(stream, client, ssh, options.DeviceId, token).ConfigureAwait(false);
        }
    }

    private async Task RunDataBridgeAsync(
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
            PublishStatus("中继会话线路：" +
                RelayNetworkPathSelector.DescribeLocalEndpoint(relayClient.Client.LocalEndPoint) +
                $" → {options.ServerAddress}:{options.Port}");
            // The host's socket is loopback for relay sessions. Only this actual
            // WAN socket identifies whether a wireless interface is in use.
            using IDisposable wlanMediaStreaming = WindowsWlanMediaStreaming.TryAcquireInteractive(
                relayClient.Client.LocalEndPoint, PublishStatus);

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

    private async Task RunHeartbeatAsync(
        Stream stream,
        SemaphoreSlim writeLock,
        int localHostPort,
        IReadOnlyList<string> registeredAddresses,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _addressRefresh.WaitAsync(HeartbeatInterval, cancellationToken)
                .ConfigureAwait(false);
            localHostPort = Volatile.Read(ref _localHostPort);
            IReadOnlyList<string> currentAddresses = RelayAddressReport.Normalize(_addressProvider());
            if (_prepareConnection is not null && ShouldRefreshNetwork(registeredAddresses,
                currentAddresses, _dataBridges.Values.Any(task => !task.IsCompleted) || !_sshBridges.IsEmpty))
            {
                // Wait until the real session finishes; changing another NIC
                // must not terminate an otherwise healthy active desktop.
                throw new IOException("网络地址已变化，重新选择中继线路");
            }
            await WriteControlJsonAsync(
                    stream,
                    writeLock,
                    new
                    {
                        version = 1,
                        type = "heartbeat",
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        directAddresses = currentAddresses,
                        directPort = localHostPort,
                        services = Services(localHostPort)
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal static bool ShouldRefreshNetwork(IReadOnlyList<string> registeredAddresses,
        IReadOnlyList<string> currentAddresses, bool hasActiveSession) =>
        !hasActiveSession && currentAddresses.Count > 0 &&
        !new HashSet<string>(registeredAddresses, StringComparer.Ordinal).SetEquals(currentAddresses);

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
        _activeOptions = null;
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
            await Task.WhenAll(_sshBridges.Values.Select(state => state.Task).ToArray())
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
