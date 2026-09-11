using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteDesk;

internal sealed record DiscoveredHost(
    string MachineName,
    string Address,
    int Port,
    string CaptureTarget,
    bool IsHostRunning,
    bool CanRemoteStart,
    string Platform,
    RemoteDeviceCapabilities Capabilities,
    string? BuildStamp = null,
    string? DeviceId = null)
{
    public override string ToString()
    {
        string status = IsHostRunning ? "正在监听" : CanRemoteStart ? "可远程启动" : "仅软件运行";
        return $"{MachineName} [{Platform}] ({Address}:{Port}) - {status}";
    }
}

internal readonly record struct DiscoveryPresence(
    int HostPort,
    string CaptureTarget,
    bool IsHostRunning,
    bool CanRemoteStart,
    string Platform,
    RemoteDeviceCapabilities Capabilities,
    string? BuildStamp = null);

internal readonly record struct DiscoveryProbeTarget(string Address, int HostPort);

internal sealed record RemoteStartResult(bool Success, string Message, int Port);

internal enum RemoteDeskTcpProbeStatus
{
    RemoteDesk,
    NotRemoteDesk,
    ConnectionFailed,
    Timeout
}

internal sealed record RemoteDeskTcpProbeResult(
    string Address,
    int Port,
    RemoteDeskTcpProbeStatus Status,
    string Detail);

internal sealed class NetworkDiscoveryResponder : IDisposable
{
    private const int DiscoveryPort =
        RemotePortPolicy.DefaultDiscoveryPort;
    private static readonly TimeSpan DisposeStopTimeout = TimeSpan.FromSeconds(2);
    private static readonly byte[] DiscoveryRequest = Encoding.UTF8.GetBytes("RemoteDesk.Discover.v1");

    private UdpClient? _udpClient;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _listenTask;
    private readonly object _nonceLock = new();
    private readonly Dictionary<string, DateTimeOffset> _remoteStartNonces = new(StringComparer.Ordinal);
    private Func<DiscoveryPresence>? _presenceProvider;
    private Func<string?>? _passwordProvider;
    private Func<int, CancellationToken, Task<RemoteStartResult>>? _remoteStartHandler;
    private Action<string>? _log;

    public int ListeningPort { get; private set; }

    public void Start(
        Func<DiscoveryPresence> presenceProvider,
        Func<string?> passwordProvider,
        Func<int, CancellationToken, Task<RemoteStartResult>> remoteStartHandler,
        Action<string>? log)
    {
        if (_udpClient is not null)
        {
            return;
        }

        _presenceProvider = presenceProvider;
        _passwordProvider = passwordProvider;
        _remoteStartHandler = remoteStartHandler;
        _log = log;
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            UdpClient? udpClient = null;
            foreach (int candidatePort in
                RemotePortPolicy.GetDiscoveryProbePorts(
                    DiscoveryPort))
            {
                try
                {
                    udpClient =
                        new UdpClient(
                            AddressFamily.InterNetwork)
                        {
                            EnableBroadcast = true
                        };
                    udpClient.Client.SetSocketOption(
                        SocketOptionLevel.Socket,
                        SocketOptionName.ReuseAddress,
                        true);
                    udpClient.Client.Bind(
                        new IPEndPoint(
                            IPAddress.Any,
                            candidatePort));
                    ListeningPort = candidatePort;
                    break;
                }
                catch (SocketException ex) when (
                    candidatePort == DiscoveryPort &&
                    RemotePortPolicy
                        .CanFallbackFromDiscoveryBind(ex))
                {
                    udpClient?.Dispose();
                    udpClient = null;
                }
                catch
                {
                    udpClient?.Dispose();
                    throw;
                }
            }

            if (udpClient is null)
            {
                throw new InvalidOperationException(
                    "没有可用的局域网发现监听端口。");
            }

            _udpClient = udpClient;
            _listenTask = Task.Run(() => ListenAsync(udpClient, _cancellationTokenSource.Token));
        }
        catch
        {
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
            ListeningPort = 0;
            throw;
        }
    }

    public async Task StopAsync()
    {
        UdpClient? udpClient = _udpClient;
        CancellationTokenSource? cancellationTokenSource = _cancellationTokenSource;
        Task? listenTask = _listenTask;

        _udpClient = null;
        _cancellationTokenSource = null;
        _listenTask = null;
        ListeningPort = 0;

        if (udpClient is null)
        {
            cancellationTokenSource?.Cancel();
            cancellationTokenSource?.Dispose();
            return;
        }

        cancellationTokenSource?.Cancel();
        udpClient.Dispose();

        if (listenTask is not null)
        {
            try
            {
                await listenTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException or SocketException)
            {
            }
        }

        cancellationTokenSource?.Dispose();
    }

    public void Dispose()
    {
        try
        {
            Task stopTask = StopAsync();
            if (!stopTask.Wait(DisposeStopTimeout))
            {
                _log?.Invoke("局域网发现停止超时，退出时将强制结束残留监听。");
            }
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(IsStopException))
        {
        }
        catch (Exception ex) when (IsStopException(ex))
        {
        }
    }

    private static bool IsStopException(Exception ex)
    {
        return ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException or SocketException;
    }

    private async Task ListenAsync(UdpClient udpClient, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult request;
            try
            {
                request = await udpClient.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                _log?.Invoke($"局域网发现监听异常：{ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            if (request.Buffer.AsSpan().SequenceEqual(DiscoveryRequest))
            {
                await SendDiscoveryResponseAsync(udpClient, request.RemoteEndPoint, cancellationToken);
                continue;
            }

            if (NetworkDiscoveryService.TryParseRemoteStartRequest(request.Buffer, out RemoteStartRequestData? startRequest) &&
                startRequest is not null)
            {
                await HandleRemoteStartRequestAsync(udpClient, request.RemoteEndPoint, startRequest, cancellationToken);
            }
        }
    }

    private async Task SendDiscoveryResponseAsync(
        UdpClient udpClient,
        IPEndPoint remoteEndPoint,
        CancellationToken cancellationToken)
    {
        byte[] response = NetworkDiscoveryService.CreateResponse(GetPresence());
        try
        {
            await udpClient.SendAsync(response, remoteEndPoint, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _log?.Invoke($"局域网发现响应失败：{ex.Message}");
            }
        }
    }

    private async Task HandleRemoteStartRequestAsync(
        UdpClient udpClient,
        IPEndPoint remoteEndPoint,
        RemoteStartRequestData request,
        CancellationToken cancellationToken)
    {
        RemoteStartResult result;
        string? password = _passwordProvider?.Invoke();
        DiscoveryPresence presence = GetPresence();

        if (!presence.CanRemoteStart)
        {
            result = new RemoteStartResult(false, "对方未启用远程启动。", presence.HostPort);
        }
        else if (string.IsNullOrWhiteSpace(password) ||
            !NetworkDiscoveryService.ValidateRemoteStartRequest(request, password))
        {
            result = new RemoteStartResult(false, "远程启动口令校验失败。", presence.HostPort);
        }
        else if (!TryRememberRemoteStartNonce(request.Nonce))
        {
            result = new RemoteStartResult(false, "远程启动请求已被处理，已拒绝重复请求。", presence.HostPort);
        }
        else if (presence.IsHostRunning)
        {
            result = new RemoteStartResult(true, "被控端已在运行。", presence.HostPort);
        }
        else if (_remoteStartHandler is null)
        {
            result = new RemoteStartResult(false, "远程启动处理器不可用。", presence.HostPort);
        }
        else
        {
            try
            {
                result = await _remoteStartHandler(request.Port, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                result = new RemoteStartResult(false, $"远程启动失败：{ex.Message}", presence.HostPort);
            }
        }

        byte[] response = NetworkDiscoveryService.CreateRemoteStartResponse(request.Nonce, result);
        try
        {
            await udpClient.SendAsync(response, remoteEndPoint, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _log?.Invoke($"远程启动响应失败：{ex.Message}");
            }
        }
    }

    private DiscoveryPresence GetPresence()
    {
        try
        {
            return _presenceProvider?.Invoke() ?? new DiscoveryPresence(
                Protocol.DefaultPort,
                string.Empty,
                false,
                false,
                RemoteDevicePlatforms.Current,
                RemoteDeviceCapabilityInfo.LocalWindows(canRemoteStart: false));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return new DiscoveryPresence(
                Protocol.DefaultPort,
                string.Empty,
                false,
                false,
                RemoteDevicePlatforms.Current,
                RemoteDeviceCapabilityInfo.LocalWindows(canRemoteStart: false));
        }
    }

    private bool TryRememberRemoteStartNonce(string? nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce))
        {
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_nonceLock)
        {
            foreach (string expiredNonce in _remoteStartNonces
                .Where(item => now - item.Value > TimeSpan.FromMinutes(3))
                .Select(item => item.Key)
                .ToArray())
            {
                _remoteStartNonces.Remove(expiredNonce);
            }

            if (_remoteStartNonces.ContainsKey(nonce))
            {
                return false;
            }

            _remoteStartNonces[nonce] = now;
            return true;
        }
    }
}

internal sealed class RemoteStartRequestData
{
    public string? Type { get; set; }

    public int Port { get; set; }

    public long UnixTimeSeconds { get; set; }

    public string? Nonce { get; set; }

    public string? Proof { get; set; }
}

internal static class NetworkDiscoveryService
{
    private const int DiscoveryPort =
        RemotePortPolicy.DefaultDiscoveryPort;
    private const int MaxDiscoveryTextLength = 256;
    private const int MaxDirectedProbeHostsPerInterface = 254;
    private const int MaxDirectedProbeEndpoints = 768;
    private const int MaxConcurrentTcpProbes = 192;
    private const int RemoteStartNonceBytes = 16;
    private const int TcpProbeTimeoutMilliseconds = 300;
    private const string TcpProbeCaptureTarget = "TCP 被控端";
    private static readonly TimeSpan RemoteStartClockSkew = TimeSpan.FromMinutes(2);
    private static readonly byte[] DiscoveryRequest = Encoding.UTF8.GetBytes("RemoteDesk.Discover.v1");
    private const string DiscoveryResponseType = "RemoteDesk.Discover.Response.v1";
    private const string RemoteStartRequestType = "RemoteDesk.RemoteStart.Request.v1";
    private const string RemoteStartResponseType = "RemoteDesk.RemoteStart.Response.v1";

    public static async Task<IReadOnlyList<DiscoveredHost>> DiscoverAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IEnumerable<string>? directAddresses = null,
        int discoveryPort = DiscoveryPort,
        int hostProbePort = Protocol.DefaultPort,
        IEnumerable<DiscoveryProbeTarget>? directTargets = null,
        bool includeDirectedTcpProbes = false,
        bool includeBroadcast = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(discoveryPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(discoveryPort, IPEndPoint.MaxPort);
        ArgumentOutOfRangeException.ThrowIfLessThan(hostProbePort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hostProbePort, IPEndPoint.MaxPort);

        using var udpClient = new UdpClient(AddressFamily.InterNetwork)
        {
            EnableBroadcast = true
        };
        DisableUdpConnectionReset(udpClient.Client);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var endpoints = new HashSet<IPEndPoint>();
        IReadOnlyList<int> discoveryPorts =
            RemotePortPolicy.GetDiscoveryProbePorts(
                discoveryPort);
        foreach (IPAddress broadcastAddress in includeBroadcast ? GetBroadcastAddresses() : Array.Empty<IPAddress>())
        {
            foreach (int port in discoveryPorts)
            {
                endpoints.Add(
                    new IPEndPoint(
                        broadcastAddress,
                        port));
            }
        }

        foreach (IPAddress probeAddress in includeDirectedTcpProbes ? GetDirectedProbeAddresses() : Array.Empty<IPAddress>())
        {
            foreach (int port in discoveryPorts)
            {
                endpoints.Add(
                    new IPEndPoint(
                        probeAddress,
                        port));
            }
        }

        IReadOnlyList<IPAddress> resolvedDirectAddresses = await ResolveDirectProbeAddressesAsync(
            directAddresses,
            cancellationToken,
            timeoutSource.Token).ConfigureAwait(false);
        foreach (IPAddress ipAddress in resolvedDirectAddresses)
        {
            foreach (int port in discoveryPorts)
            {
                endpoints.Add(
                    new IPEndPoint(
                        ipAddress,
                        port));
            }
        }

        IReadOnlyList<IPEndPoint> resolvedDirectTargets = await ResolveDirectProbeTargetsAsync(
            directTargets,
            cancellationToken,
            timeoutSource.Token).ConfigureAwait(false);
        foreach (IPEndPoint directTarget in resolvedDirectTargets)
        {
            foreach (int port in discoveryPorts)
            {
                endpoints.Add(
                    new IPEndPoint(
                        directTarget.Address,
                        port));
            }
        }

        IReadOnlyList<IPEndPoint> directTcpProbeEndpoints = CreateTcpProbeEndpoints(
            resolvedDirectAddresses,
            resolvedDirectTargets,
            hostProbePort,
            includeDirectedTcpProbes);
        CancellationToken tcpProbeCancellation = includeDirectedTcpProbes
            ? timeoutSource.Token
            : cancellationToken;
        Task<IReadOnlyList<DiscoveredHost>> tcpProbeTask = directTcpProbeEndpoints.Count == 0
            ? Task.FromResult<IReadOnlyList<DiscoveredHost>>(Array.Empty<DiscoveredHost>())
            : ProbeDirectTcpHostsAsync(directTcpProbeEndpoints, tcpProbeCancellation);

        foreach (IPEndPoint endpoint in endpoints)
        {
            if (timeoutSource.IsCancellationRequested) break;
            try
            {
                await udpClient.SendAsync(
                    DiscoveryRequest,
                    endpoint,
                    timeoutSource.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        var discovered = new Dictionary<string, DiscoveredHost>(StringComparer.OrdinalIgnoreCase);
        while (!timeoutSource.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udpClient.ReceiveAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (!includeBroadcast && !resolvedDirectAddresses.Contains(result.RemoteEndPoint.Address) &&
                !resolvedDirectTargets.Any(target => target.Address.Equals(result.RemoteEndPoint.Address))) continue;
            DiscoveryResponse? response = ParseResponse(result.Buffer);
            if (response is null || response.Port <= 0 || response.Port > IPEndPoint.MaxPort)
            {
                continue;
            }

            string address = result.RemoteEndPoint.Address.ToString();
            string machineName = NormalizeDiscoveryText(response.MachineName, address);
            string key = $"{address}:{response.Port}";
            if (discovered.Count >= 64 && !discovered.ContainsKey(key)) continue;
            bool isHostRunning = response.IsHostRunning ?? true;
            bool canRemoteStart = response.CanRemoteStart ?? false;
            RemoteDeviceCapabilities capabilities = response.Capabilities ??
                RemoteDeviceCapabilityInfo.LegacyWindows(canRemoteStart);
            discovered[key] = new DiscoveredHost(
                machineName,
                address,
                response.Port,
                NormalizeDiscoveryText(response.CaptureTarget, string.Empty),
                isHostRunning,
                canRemoteStart,
                RemoteDevicePlatforms.Normalize(response.Platform, RemoteDevicePlatforms.Windows),
                capabilities,
                RemoteDeskBuildInfo.NormalizeBuildStamp(response.BuildStamp),
                RemoteDeviceIdentity.Normalize(response.DeviceId));
        }

        foreach (DiscoveredHost host in await tcpProbeTask.ConfigureAwait(false))
        {
            string key = $"{host.Address}:{host.Port}";
            if (!discovered.ContainsKey(key))
            {
                discovered[key] = host;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return discovered.Values
            .OrderBy(host => host.MachineName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(host => host.Address, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<IPEndPoint> CreateTcpProbeEndpoints(
        IEnumerable<IPAddress> directAddresses,
        IEnumerable<IPEndPoint> directTargets,
        int hostProbePort,
        bool includeDirectedTcpProbes,
        IEnumerable<IPAddress>? directedProbeAddresses = null)
    {
        if (hostProbePort <= 0 || hostProbePort > IPEndPoint.MaxPort)
        {
            return Array.Empty<IPEndPoint>();
        }

        var endpoints = new HashSet<IPEndPoint>();
        IReadOnlyList<int> hostProbePorts =
            RemotePortPolicy.GetCompatibleHostProbePorts(
                hostProbePort);
        foreach (IPAddress ipAddress in directAddresses)
        {
            if (ipAddress.AddressFamily == AddressFamily.InterNetwork)
            {
                foreach (int port in hostProbePorts)
                {
                    endpoints.Add(
                        new IPEndPoint(
                            ipAddress,
                            port));
                }
            }
        }

        foreach (IPEndPoint target in directTargets)
        {
            if (target.AddressFamily == AddressFamily.InterNetwork &&
                target.Port > 0 &&
                target.Port <= IPEndPoint.MaxPort)
            {
                foreach (int port in
                    RemotePortPolicy
                        .GetCompatibleHostProbePorts(
                            target.Port))
                {
                    endpoints.Add(
                        new IPEndPoint(
                            target.Address,
                            port));
                }
            }
        }

        if (includeDirectedTcpProbes)
        {
            foreach (IPAddress probeAddress in directedProbeAddresses ?? GetDirectedProbeAddresses())
            {
                if (probeAddress.AddressFamily == AddressFamily.InterNetwork)
                {
                    foreach (int port in hostProbePorts)
                    {
                        endpoints.Add(
                            new IPEndPoint(
                                probeAddress,
                                port));
                        if (endpoints.Count >=
                            MaxDirectedProbeEndpoints)
                        {
                            break;
                        }
                    }
                }

                if (endpoints.Count >= MaxDirectedProbeEndpoints)
                {
                    break;
                }
            }
        }

        return endpoints.ToArray();
    }

    public static async Task<RemoteStartResult> RequestRemoteStartAsync(
        string address,
        int port,
        string password,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        int discoveryPort = DiscoveryPort)
    {
        if (!IPAddress.TryParse(address, out IPAddress? ipAddress))
        {
            throw new InvalidOperationException("远程启动仅支持已发现的内网 IP 地址。");
        }

        if (port <= 0 || port > IPEndPoint.MaxPort)
        {
            throw new InvalidOperationException("远程启动端口异常。");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("请输入远程启动口令。");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(discoveryPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(discoveryPort, IPEndPoint.MaxPort);

        using var udpClient = new UdpClient(AddressFamily.InterNetwork);
        DisableUdpConnectionReset(udpClient.Client);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(RemoteStartNonceBytes));
        long unixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var request = new RemoteStartRequestData
        {
            Type = RemoteStartRequestType,
            Port = port,
            UnixTimeSeconds = unixTimeSeconds,
            Nonce = nonce,
            Proof = CreateRemoteStartProof(password, port, unixTimeSeconds, nonce)
        };

        byte[] requestBytes =
            JsonSerializer.SerializeToUtf8Bytes(
                request);
        foreach (int targetDiscoveryPort in
            RemotePortPolicy.GetDiscoveryProbePorts(
                discoveryPort))
        {
            try
            {
                await udpClient.SendAsync(
                    requestBytes,
                    new IPEndPoint(
                        ipAddress,
                        targetDiscoveryPort),
                    timeoutSource.Token);
            }
            catch (Exception ex) when (
                ex is SocketException or
                    ObjectDisposedException)
            {
            }
        }

        while (!timeoutSource.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udpClient.ReceiveAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            RemoteStartResponse? response = ParseRemoteStartResponse(result.Buffer);
            if (response is null || !string.Equals(response.Nonce, nonce, StringComparison.Ordinal))
            {
                continue;
            }

            return new RemoteStartResult(
                response.Success,
                NormalizeDiscoveryText(response.Message, response.Success ? "远程启动完成。" : "远程启动失败。"),
                response.Port > 0 && response.Port <= IPEndPoint.MaxPort ? response.Port : port);
        }

        throw new TimeoutException("远程启动请求超时。");
    }

    public static byte[] CreateResponse(DiscoveryPresence presence)
    {
        int port = presence.HostPort > 0 && presence.HostPort <= IPEndPoint.MaxPort
            ? presence.HostPort
            : Protocol.DefaultPort;

        var response = new DiscoveryResponse
        {
            Type = DiscoveryResponseType,
            MachineName = NormalizeDiscoveryText(Environment.MachineName, "RemoteDesk"),
            Port = port,
            CaptureTarget = NormalizeDiscoveryText(presence.CaptureTarget, string.Empty),
            IsHostRunning = presence.IsHostRunning,
            CanRemoteStart = presence.CanRemoteStart,
            Platform = RemoteDevicePlatforms.Normalize(presence.Platform, RemoteDevicePlatforms.Current),
            Capabilities = presence.Capabilities,
            BuildStamp = RemoteDeskBuildInfo.NormalizeBuildStamp(presence.BuildStamp) ?? RemoteDeskBuildInfo.BuildStamp,
            DeviceId = RemoteDeviceIdentity.LocalId
        };

        return JsonSerializer.SerializeToUtf8Bytes(response);
    }

    public static bool TryParseRemoteStartRequest(byte[] buffer, out RemoteStartRequestData? request)
    {
        request = null;
        try
        {
            RemoteStartRequestData? parsed = JsonSerializer.Deserialize<RemoteStartRequestData>(buffer);
            if (parsed?.Type != RemoteStartRequestType)
            {
                return false;
            }

            request = parsed;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    public static bool ValidateRemoteStartRequest(RemoteStartRequestData request, string password)
    {
        if (request.Type != RemoteStartRequestType ||
            request.Port <= 0 ||
            request.Port > IPEndPoint.MaxPort ||
            string.IsNullOrWhiteSpace(request.Nonce) ||
            string.IsNullOrWhiteSpace(request.Proof))
        {
            return false;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long maxSkewSeconds = (long)RemoteStartClockSkew.TotalSeconds;
        if (request.UnixTimeSeconds < now - maxSkewSeconds ||
            request.UnixTimeSeconds > now + maxSkewSeconds)
        {
            return false;
        }

        string expectedProof = CreateRemoteStartProof(password, request.Port, request.UnixTimeSeconds, request.Nonce);
        try
        {
            byte[] expectedBytes = Convert.FromBase64String(expectedProof);
            byte[] providedBytes = Convert.FromBase64String(request.Proof);
            return providedBytes.Length == expectedBytes.Length &&
                CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static byte[] CreateRemoteStartResponse(string? nonce, RemoteStartResult result)
    {
        var response = new RemoteStartResponse
        {
            Type = RemoteStartResponseType,
            Nonce = NormalizeDiscoveryText(nonce, string.Empty),
            Success = result.Success,
            Message = NormalizeDiscoveryText(result.Message, result.Success ? "远程启动完成。" : "远程启动失败。"),
            Port = result.Port
        };

        return JsonSerializer.SerializeToUtf8Bytes(response);
    }

    private static DiscoveryResponse? ParseResponse(byte[] buffer)
    {
        if (buffer.Length > 8192) return null;
        try
        {
            DiscoveryResponse? response = JsonSerializer.Deserialize<DiscoveryResponse>(buffer);
            return response?.Type == DiscoveryResponseType ? response : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static RemoteStartResponse? ParseRemoteStartResponse(byte[] buffer)
    {
        try
        {
            RemoteStartResponse? response = JsonSerializer.Deserialize<RemoteStartResponse>(buffer);
            return response?.Type == RemoteStartResponseType ? response : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    internal static async Task<IReadOnlyList<IPAddress>> ResolveDirectProbeAddressesAsync(
        IEnumerable<string>? directAddresses,
        CancellationToken cancellationToken,
        CancellationToken? dnsCancellationToken = null)
    {
        if (directAddresses is null)
        {
            return Array.Empty<IPAddress>();
        }

        var addresses = new HashSet<IPAddress>();
        foreach (string address in directAddresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string trimmedAddress = address?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmedAddress))
            {
                continue;
            }

            if (IPAddress.TryParse(trimmedAddress, out IPAddress? ipAddress))
            {
                if (ipAddress.AddressFamily == AddressFamily.InterNetwork)
                {
                    addresses.Add(ipAddress);
                }

                continue;
            }

            // A spent UDP/DNS budget must not discard literal IPs or prevent
            // their separately bounded TCP banner fallback. User cancellation
            // remains authoritative for every path.
            CancellationToken dnsToken = dnsCancellationToken ?? cancellationToken;
            if (dnsToken.IsCancellationRequested) continue;
            try
            {
                IPAddress[] resolved = await Dns.GetHostAddressesAsync(
                    trimmedAddress,
                    AddressFamily.InterNetwork,
                    dnsToken).ConfigureAwait(false);
                foreach (IPAddress resolvedAddress in resolved)
                {
                    if (resolvedAddress.AddressFamily == AddressFamily.InterNetwork)
                    {
                        addresses.Add(resolvedAddress);
                    }
                }
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException or InvalidOperationException)
            {
            }
            catch (OperationCanceledException) when (dnsToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
            }
        }

        return addresses.ToArray();
    }

    internal static async Task<IReadOnlyList<IPEndPoint>> ResolveDirectProbeTargetsAsync(
        IEnumerable<DiscoveryProbeTarget>? directTargets,
        CancellationToken cancellationToken,
        CancellationToken? dnsCancellationToken = null)
    {
        if (directTargets is null)
        {
            return Array.Empty<IPEndPoint>();
        }

        var endpoints = new HashSet<IPEndPoint>();
        foreach (DiscoveryProbeTarget target in directTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(target.Address) ||
                target.HostPort <= 0 ||
                target.HostPort > IPEndPoint.MaxPort)
            {
                continue;
            }

            IReadOnlyList<IPAddress> addresses = await ResolveDirectProbeAddressesAsync(
                [target.Address],
                cancellationToken,
                dnsCancellationToken).ConfigureAwait(false);
            foreach (IPAddress address in addresses)
            {
                endpoints.Add(new IPEndPoint(address, target.HostPort));
            }
        }

        return endpoints.ToArray();
    }

    private static async Task<IReadOnlyList<DiscoveredHost>> ProbeDirectTcpHostsAsync(
        IEnumerable<IPEndPoint> endpoints,
        CancellationToken cancellationToken)
    {
        IPEndPoint[] distinctEndpoints = endpoints
            .Distinct()
            .ToArray();
        using var semaphore = new SemaphoreSlim(MaxConcurrentTcpProbes);
        Task<DiscoveredHost?>[] tasks = distinctEndpoints
            .Select(endpoint => ProbeDirectTcpHostAsync(endpoint.Address, endpoint.Port, semaphore, cancellationToken))
            .ToArray();
        if (tasks.Length == 0)
        {
            return Array.Empty<DiscoveredHost>();
        }

        DiscoveredHost?[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results
            .Where(host => host is not null)
            .Select(host => host!)
            .ToArray();
    }

    private static async Task<DiscoveredHost?> ProbeDirectTcpHostAsync(
        IPAddress address,
        int port,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        try
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        RemoteDeskTcpProbeResult result;
        try
        {
            result = await ProbeRemoteDeskTcpAsync(
                address,
                port,
                TimeSpan.FromMilliseconds(TcpProbeTimeoutMilliseconds),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            semaphore.Release();
        }

        if (result.Status == RemoteDeskTcpProbeStatus.RemoteDesk)
        {
            return new DiscoveredHost(
                result.Address,
                result.Address,
                port,
                TcpProbeCaptureTarget,
                IsHostRunning: true,
                CanRemoteStart: false,
                RemoteDevicePlatforms.Unknown,
                RemoteDeviceCapabilities.RemoteDesktop);
        }

        return null;
    }

    internal static async Task<RemoteDeskTcpProbeResult> ProbeRemoteDeskTcpAsync(
        IPAddress address,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        NetworkUtils.ConfigureLowLatencyTcpClient(
            client,
            receiveBufferSize: 8 * 1024,
            sendBufferSize: 8 * 1024);

        string addressText = address.ToString();
        try
        {
            await client.ConnectAsync(address, port, timeoutSource.Token).ConfigureAwait(false);
            bool hasMagic = await Protocol.ReadServerMagicAsync(client.GetStream(), timeoutSource.Token).ConfigureAwait(false);
            return hasMagic
                ? new RemoteDeskTcpProbeResult(addressText, port, RemoteDeskTcpProbeStatus.RemoteDesk, "RemoteDesk 握手正常")
                : new RemoteDeskTcpProbeResult(addressText, port, RemoteDeskTcpProbeStatus.NotRemoteDesk, "端口已打开，但不是 RemoteDesk 握手");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RemoteDeskTcpProbeResult(addressText, port, RemoteDeskTcpProbeStatus.Timeout, "连接或读取握手超时");
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            return new RemoteDeskTcpProbeResult(addressText, port, RemoteDeskTcpProbeStatus.ConnectionFailed, NormalizeProbeError(ex.Message));
        }
    }

    internal static async Task<IReadOnlyList<RemoteDeskTcpProbeResult>> ProbeRemoteDeskTcpAsync(
        IEnumerable<IPAddress> addresses,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Task<RemoteDeskTcpProbeResult>[] tasks = addresses
            .Distinct()
            .Select(address => ProbeRemoteDeskTcpAsync(address, port, timeout, cancellationToken))
            .ToArray();
        if (tasks.Length == 0)
        {
            return Array.Empty<RemoteDeskTcpProbeResult>();
        }

        try
        {
            return await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Array.Empty<RemoteDeskTcpProbeResult>();
        }
    }

    private static string NormalizeProbeError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "连接失败";
        }

        return message.Trim();
    }

    private static string CreateRemoteStartProof(string password, int port, long unixTimeSeconds, string nonce)
    {
        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        using var hmac = new HMACSHA256(key);
        string payload = $"{RemoteStartRequestType}|{port}|{unixTimeSeconds}|{nonce}";
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    private static IEnumerable<IPAddress> GetBroadcastAddresses()
    {
        var addresses = new HashSet<IPAddress> { IPAddress.Broadcast };
        NetworkInterface[] adapters;
        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (Exception ex) when (ex is NetworkInformationException or SocketException or InvalidOperationException or ObjectDisposedException)
        {
            return addresses;
        }

        foreach (NetworkInterface adapter in adapters)
        {
            IPInterfaceProperties properties;
            try
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                properties = adapter.GetIPProperties();
            }
            catch (Exception ex) when (ex is NetworkInformationException or SocketException or InvalidOperationException or ObjectDisposedException)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                    unicast.IPv4Mask is null)
                {
                    continue;
                }

                try
                {
                    addresses.Add(GetBroadcastAddress(unicast.Address, unicast.IPv4Mask));
                }
                catch (ArgumentException)
                {
                }
            }
        }

        return addresses;
    }

    private static IEnumerable<IPAddress> GetDirectedProbeAddresses()
    {
        var probes = new HashSet<IPAddress>();
        NetworkInterface[] adapters;
        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (Exception ex) when (ex is NetworkInformationException or SocketException or InvalidOperationException or ObjectDisposedException)
        {
            return probes;
        }

        foreach (NetworkInterface adapter in adapters)
        {
            IPInterfaceProperties properties;
            try
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                properties = adapter.GetIPProperties();
            }
            catch (Exception ex) when (ex is NetworkInformationException or SocketException or InvalidOperationException or ObjectDisposedException)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                    unicast.IPv4Mask is null)
                {
                    continue;
                }

                try
                {
                    AddDirectedProbeAddresses(unicast.Address, unicast.IPv4Mask, probes);
                    if (probes.Count >= MaxDirectedProbeEndpoints)
                    {
                        return probes;
                    }
                }
                catch (ArgumentException)
                {
                }
            }
        }

        return probes;
    }

    private static void AddDirectedProbeAddresses(IPAddress address, IPAddress mask, HashSet<IPAddress> probes)
    {
        uint addressValue = ToUInt32(address);
        uint maskValue = ToUInt32(mask);
        uint networkValue = addressValue & maskValue;
        uint broadcastValue = networkValue | ~maskValue;
        uint usableHosts = broadcastValue > networkValue
            ? broadcastValue - networkValue - 1
            : 0;

        if (usableHosts == 0)
        {
            return;
        }

        if (usableHosts > MaxDirectedProbeHostsPerInterface)
        {
            networkValue = addressValue & 0xFFFFFF00u;
            broadcastValue = networkValue | 0x000000FFu;
        }

        for (uint candidate = networkValue + 1; candidate < broadcastValue; candidate++)
        {
            if (candidate == addressValue)
            {
                continue;
            }

            probes.Add(FromUInt32(candidate));
            if (probes.Count >= MaxDirectedProbeEndpoints)
            {
                return;
            }
        }
    }

    private static IPAddress GetBroadcastAddress(IPAddress address, IPAddress mask)
    {
        byte[] addressBytes = address.GetAddressBytes();
        byte[] maskBytes = mask.GetAddressBytes();
        byte[] broadcastBytes = new byte[addressBytes.Length];

        for (int index = 0; index < broadcastBytes.Length; index++)
        {
            broadcastBytes[index] = (byte)(addressBytes[index] | ~maskBytes[index]);
        }

        return new IPAddress(broadcastBytes);
    }

    private static uint ToUInt32(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            throw new ArgumentException("Only IPv4 addresses are supported.", nameof(address));
        }

        return ((uint)bytes[0] << 24) |
            ((uint)bytes[1] << 16) |
            ((uint)bytes[2] << 8) |
            bytes[3];
    }

    private static IPAddress FromUInt32(uint value)
    {
        return new IPAddress([
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        ]);
    }

    private static string NormalizeDiscoveryText(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        string trimmed = value.Trim();
        return trimmed.Length <= MaxDiscoveryTextLength
            ? trimmed
            : trimmed[..MaxDiscoveryTextLength];
    }

    private static void DisableUdpConnectionReset(Socket socket)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            const int sioUdpConnectionReset = unchecked((int)0x9800000C);
            socket.IOControl(sioUdpConnectionReset, [0], null);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or PlatformNotSupportedException)
        {
        }
    }

    private sealed class DiscoveryResponse
    {
        public string? DeviceId { get; set; }
        public string? Type { get; set; }

        public string? MachineName { get; set; }

        public int Port { get; set; }

        public string? CaptureTarget { get; set; }

        public bool? IsHostRunning { get; set; }

        public bool? CanRemoteStart { get; set; }

        public string? Platform { get; set; }

        public RemoteDeviceCapabilities? Capabilities { get; set; }

        public string? BuildStamp { get; set; }
    }

    private sealed class RemoteStartResponse
    {
        public string? Type { get; set; }

        public string? Nonce { get; set; }

        public bool Success { get; set; }

        public string? Message { get; set; }

        public int Port { get; set; }
    }
}
