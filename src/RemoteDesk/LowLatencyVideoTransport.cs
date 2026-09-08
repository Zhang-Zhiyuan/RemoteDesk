using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace RemoteDesk;

internal static class LowLatencyVideoSocketSupport
{
    public static IPAddress NormalizeTcpPeerAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.IsIPv4MappedToIPv6
            ? address.MapToIPv4()
            : address;
    }

    public static string FormatSocketException(SocketException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return $"SocketError={exception.SocketErrorCode} ({(int)exception.SocketErrorCode})，" +
            $"NativeError={exception.NativeErrorCode}，ErrorCode={exception.ErrorCode}，" +
            exception.Message;
    }

    public static string FormatLocalEndpoint(Socket? socket)
    {
        if (socket is null)
        {
            return "未创建";
        }

        try
        {
            return socket.LocalEndPoint?.ToString() ?? "未绑定";
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
        {
            return $"不可用（{ex.GetType().Name}）";
        }
    }

    internal static bool IsTransientDatagramSendError(
        SocketException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.SocketErrorCode is
            SocketError.NoBufferSpaceAvailable or
            SocketError.WouldBlock;
    }
}

internal sealed class LowLatencyVideoErrorLogThrottler
{
    private readonly object _lock = new();
    private readonly long _intervalMilliseconds;
    private long _lastLoggedAt = long.MinValue;
    private int _suppressedCount;

    public LowLatencyVideoErrorLogThrottler(TimeSpan interval)
    {
        _intervalMilliseconds = Math.Max(
            1,
            checked((long)interval.TotalMilliseconds));
    }

    public string? CreateMessage(string message)
    {
        lock (_lock)
        {
            long now = Environment.TickCount64;
            if (_lastLoggedAt != long.MinValue &&
                now - _lastLoggedAt < _intervalMilliseconds)
            {
                _suppressedCount++;
                return null;
            }

            string suffix = _suppressedCount == 0
                ? string.Empty
                : $"（此前已合并 {_suppressedCount} 次）";
            _lastLoggedAt = now;
            _suppressedCount = 0;
            return $"{message}{suffix}";
        }
    }
}

internal static class LowLatencyVideoBindPolicy
{
    internal const int ViewerTimeoutMilliseconds = 3000;
    internal const int HostProbeDeliveryMarginMilliseconds = 500;
    internal const int HostReadyTimeoutMilliseconds = 2000;
    internal const int ProbeIntervalMilliseconds = 200;
    internal const int NominalProbeAttemptBudget =
        ViewerTimeoutMilliseconds / ProbeIntervalMilliseconds;

    internal static readonly TimeSpan ViewerTimeout =
        TimeSpan.FromMilliseconds(ViewerTimeoutMilliseconds);
    // The host's offer clock starts before the viewer receives the TCP offer.
    // This first-stage margin lets the viewer's final probe cross the network.
    internal static readonly TimeSpan HostProbeTimeout =
        TimeSpan.FromMilliseconds(
            ViewerTimeoutMilliseconds +
            HostProbeDeliveryMarginMilliseconds);
    // A valid authenticated probe starts a fresh second-stage deadline. It is
    // deliberately independent of when the offer was sent.
    internal static readonly TimeSpan HostReadyTimeout =
        TimeSpan.FromMilliseconds(HostReadyTimeoutMilliseconds);
    internal static readonly TimeSpan ProbeInterval =
        TimeSpan.FromMilliseconds(ProbeIntervalMilliseconds);
}

internal static class LowLatencyVideoFeedbackPolicy
{
    // Match the viewer's established-frame stall deadline. A one-second
    // feedback gap is possible during a transient wireless/driver stall and
    // must not permanently demote the current TCP session to video-over-TCP.
    internal const int HardTimeoutMilliseconds = 2500;
    internal static readonly TimeSpan HardTimeout =
        TimeSpan.FromMilliseconds(HardTimeoutMilliseconds);

    internal static bool HasExpired(TimeSpan feedbackAge) =>
        feedbackAge > HardTimeout;
}

internal static class LowLatencyVideoFallbackReasons
{
    // Both peers negotiated authenticated heartbeats. Stop UDP video only,
    // while retaining the authenticated route for feedback and mouse input.
    internal const byte PreserveUdpInput = 4;
}

internal readonly record struct LowLatencyVideoSendSnapshot(
    long SuppressedShortGopDependentFrames,
    long SerializedShortGopRecoveryFrames,
    long SerializedShortGopDependentFrames,
    long ReplacedPendingFrames);

internal readonly record struct LowLatencyVideoHostHandshakeSnapshot(
    long RawPacketCount,
    long AddressRejectedCount,
    long DecryptFailureCount,
    long ValidProbeCount,
    long AckSentCount);

internal readonly record struct LowLatencyVideoViewerHandshakeSnapshot(
    long RawPacketCount,
    long AddressRejectedCount,
    long DecryptFailureCount,
    long ValidAckCount);

internal sealed class LowLatencyVideoHostTransport : IAsyncDisposable
{
    // Task.Delay cannot accurately schedule the sub-millisecond pauses that
    // eight 1200-byte packets require on a fast LAN. The accumulated timer
    // rounding made a 100 KiB recovery frame miss its following 16.7 ms P
    // frame. A bounded 32-packet burst keeps pacing effective on constrained
    // links while reducing high-rate timer wakeups from about eleven to three
    // per Full-HD IDR.
    private const int PacingBurstPackets = 32;
    // Drain only datagrams that are already waiting in the kernel. This lets
    // the host collapse a delayed burst of obsolete pointer positions without
    // adding a timer or wait to the normal one-datagram path. The bound keeps
    // authenticated or spoofed floods from monopolizing one receive batch.
    internal const int ReceiveBacklogBatchDatagramLimit = 256;
    internal static readonly TimeSpan ProbeTimeout =
        LowLatencyVideoBindPolicy.HostProbeTimeout;
    internal static readonly TimeSpan ReadyTimeout =
        LowLatencyVideoBindPolicy.HostReadyTimeout;
    internal static readonly TimeSpan FeedbackTimeout =
        LowLatencyVideoFeedbackPolicy.HardTimeout;
    internal static readonly TimeSpan MouseInputAckMinimumInterval =
        TimeSpan.FromMilliseconds(4);
    internal static readonly TimeSpan HeartbeatInterval =
        TimeSpan.FromMilliseconds(250);
    // Mouse movement is validated and applied synchronously on the host
    // receive worker. It must be able to preempt the high-throughput video
    // sender during a 4K60 burst; otherwise an otherwise healthy stream can
    // still produce a visible pointer-latency tail.
    internal const ThreadPriority ReceiveThreadPriority =
        ThreadPriority.Highest;
    // This worker only wakes for one tiny acknowledgement datagram. Keeping
    // it at the same priority makes the applied-input telemetry faithfully
    // track the already-completed injection instead of adding a second,
    // avoidable scheduling tail.
    internal const ThreadPriority MouseInputAckThreadPriority =
        ThreadPriority.Highest;

    private readonly object _startLock = new();
    private readonly Action<string> _log;
    private readonly CancellationToken _connectionCancellationToken;
    private readonly TimeProvider _feedbackTimeProvider;
    private readonly Func<ulong, uint, byte, Task>? _sendFallbackBarrier;
    private readonly Action<RemoteInputCommand>? _applyUdpMouseMove;
    private readonly Action<Socket, IPEndPoint> _connectPeerSocket;
    private readonly RemoteHostServer.InputErrorLogThrottler
        _udpMouseInputErrorLog =
            new(TimeSpan.FromSeconds(2));
    private readonly LowLatencyVideoErrorLogThrottler
        _transientDatagramSendErrorLog =
            new(TimeSpan.FromSeconds(2));
    private readonly SemaphoreSlim _sendSignal = new(0, 1);
    private readonly SemaphoreSlim _mouseInputAckSignal = new(0, 1);
    private readonly object _fallbackTaskLock = new();
    private readonly object _ioShutdownLock = new();
    private readonly object _shortGopChainLock = new();
    private readonly object _setupStateLock = new();
    private readonly TaskCompletionSource _validProbeReceivedSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _readyReceivedSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _transportCancellation;
    private Socket? _socket;
    private Task? _receiveTask;
    private Task? _sendTask;
    private Task? _heartbeatTask;
    private Task? _mouseInputAckTask;
    private Task? _fallbackTask;
    private Task? _setupTimeoutTask;
    private Task? _ioShutdownTask;
    private LowLatencyVideoSendCipher? _sendCipher;
    private LowLatencyVideoReceiveCipher? _receiveCipher;
    private LowLatencyVideoCongestionController? _congestionController;
    private WindowsQwaveVideoFlow? _qwaveVideoFlow;
    private LowLatencyVideoOffer? _offer;
    private LowLatencyVideoFeatures _features;
    private IPAddress? _expectedPeerAddress;
    private IPEndPoint? _boundPeerEndpoint;
    private PendingFrame? _pendingFrame;
    private long _offeredAt;
    private long _firstValidProbeAt;
    private long _lastFeedbackAt;
    private long _nextFrameSequence;
    private long _latestShortGopRecoveryGeneration;
    private long _serializedShortGopRecoveryGeneration;
    private long _serializingShortGopRecoveryGeneration;
    private long _admittedShortGopDependentGeneration;
    private long _suppressedShortGopDependentFrames;
    private long _serializedShortGopRecoveryFrames;
    private long _serializedShortGopDependentFrames;
    private long _replacedPendingFrames;
    private long _lastSerializedGop1RecoveryAt;
    private long _serializedGop1StreamSignature;
    private long _latestUdpMouseMoveSequence = -1;
    private long _pendingUdpMouseInputAckSequence = -1;
    private long _lastUdpMouseInputAckSentAt;
    private long _udpMouseInputAckDatagramCount;
    private long _handshakeRawPacketCount;
    private long _handshakeAddressRejectedCount;
    private long _handshakeDecryptFailureCount;
    private long _handshakeValidProbeCount;
    private long _handshakeAckSentCount;
    private int _configuredVideoBitrateBitsPerSecond;
    private int _configuredVideoFramesPerSecond;
    private int _videoRouteDisabled;
    private ulong _latestBindProbePacketSequence;
    private bool _hasBindProbePacketSequence;
    private int _peerSocketConnected;
    private int _udpMouseInputAckDisabled;
    private int _state;
    private int _disposed;

    public LowLatencyVideoHostTransport(
        Action<string> log,
        CancellationToken connectionCancellationToken,
        Func<ulong, uint, byte, Task>? sendFallbackBarrier = null,
        Action<RemoteInputCommand>? applyUdpMouseMove = null,
        Action<Socket, IPEndPoint>? connectPeerSocket = null,
        TimeProvider? feedbackTimeProvider = null)
    {
        _log = log;
        _connectionCancellationToken = connectionCancellationToken;
        _feedbackTimeProvider =
            feedbackTimeProvider ?? TimeProvider.System;
        _sendFallbackBarrier = sendFallbackBarrier;
        _applyUdpMouseMove = applyUdpMouseMove;
        _connectPeerSocket =
            connectPeerSocket ??
            ConnectPeerSocket;
    }

    public bool IsRouteActive
    {
        get
        {
            if (Volatile.Read(ref _state) != 3)
            {
                return false;
            }

            long lastFeedbackAt = Volatile.Read(ref _lastFeedbackAt);
            if (lastFeedbackAt == 0 ||
                LowLatencyVideoFeedbackPolicy.HasExpired(
                    _feedbackTimeProvider.GetElapsedTime(
                        lastFeedbackAt)))
            {
                DisableRoute("低延迟 UDP 画面反馈超时，已回退 TCP。");
                return false;
            }

            return true;
        }
    }

    internal bool IsIoShutdownCompleted =>
        Volatile.Read(ref _ioShutdownTask)?.IsCompleted == true;

    internal bool HasPendingFrame =>
        Volatile.Read(ref _pendingFrame) is not null;

    internal bool IsPeerSocketConnected =>
        Volatile.Read(ref _peerSocketConnected) != 0;

    public void ConfigureVideoRateBudget(
        int encoderBitrateBitsPerSecond,
        int framesPerSecond)
    {
        if (encoderBitrateBitsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(encoderBitrateBitsPerSecond));
        }

        if (framesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(framesPerSecond));
        }

        lock (_startLock)
        {
            _configuredVideoBitrateBitsPerSecond =
                encoderBitrateBitsPerSecond;
            _configuredVideoFramesPerSecond =
                framesPerSecond;
            _congestionController?.ConfigureVideoRateBudget(
                encoderBitrateBitsPerSecond,
                framesPerSecond);
        }
    }

    internal static bool IsIndependentlyRecoverableFrame(
        MessageType frameKind,
        RemoteFrameFlags flags)
    {
        const RemoteFrameFlags recoveryFlags =
            RemoteFrameFlags.KeyFrame | RemoteFrameFlags.CodecConfig;
        return frameKind == MessageType.Frame ||
            (frameKind == MessageType.VideoFrame &&
                flags == recoveryFlags);
    }

    public LowLatencyVideoOffer? TryCreateOffer(
        IPAddress expectedPeerAddress,
        LowLatencyVideoFeatures features = LowLatencyVideoFeatures.None,
        IPEndPoint? preferredLocalEndpoint = null)
    {
        ArgumentNullException.ThrowIfNull(expectedPeerAddress);
        expectedPeerAddress =
            LowLatencyVideoSocketSupport.NormalizeTcpPeerAddress(
                expectedPeerAddress);
        if (preferredLocalEndpoint is not null)
        {
            IPAddress preferredAddress =
                LowLatencyVideoSocketSupport.NormalizeTcpPeerAddress(
                    preferredLocalEndpoint.Address);
            if (preferredAddress.AddressFamily !=
                expectedPeerAddress.AddressFamily)
            {
                throw new ArgumentException(
                    "The preferred UDP local endpoint address family must " +
                    "match the TCP peer address family.",
                    nameof(preferredLocalEndpoint));
            }

            preferredLocalEndpoint = new IPEndPoint(
                preferredAddress,
                preferredLocalEndpoint.Port);
        }

        features = LowLatencyVideoFeatureNegotiation.Normalize(features);
        lock (_startLock)
        {
            if (_state != 0 || Volatile.Read(ref _disposed) != 0)
            {
                return null;
            }

            byte[] hostToViewerKey = RandomNumberGenerator.GetBytes(LowLatencyVideoProtocol.KeyLength);
            byte[] viewerToHostKey = RandomNumberGenerator.GetBytes(LowLatencyVideoProtocol.KeyLength);
            byte[] hostNoncePrefix = RandomNumberGenerator.GetBytes(LowLatencyVideoProtocol.NoncePrefixLength);
            byte[] viewerNoncePrefix = RandomNumberGenerator.GetBytes(LowLatencyVideoProtocol.NoncePrefixLength);
            byte[] challenge = RandomNumberGenerator.GetBytes(LowLatencyVideoProtocol.ChallengeLength);
            Socket? socket = null;
            try
            {
                ulong channelId = CreateNonZeroUInt64();
                uint epoch = CreateNonZeroUInt32();
                AddressFamily addressFamily = expectedPeerAddress.AddressFamily;
                socket = new Socket(addressFamily, SocketType.Dgram, ProtocolType.Udp);
                ConfigureSocket(socket);
                IPEndPoint bindEndpoint =
                    preferredLocalEndpoint ??
                    new IPEndPoint(
                        addressFamily == AddressFamily.InterNetwork
                            ? IPAddress.Any
                            : IPAddress.IPv6Any,
                        0);
                try
                {
                    socket.Bind(bindEndpoint);
                }
                catch (SocketException ex)
                    when (preferredLocalEndpoint is not null &&
                        preferredLocalEndpoint.Port != 0)
                {
                    _log(
                        "低延迟 UDP 无法复用 TCP 本地端口，" +
                        "将改用同一本地地址的临时端口：" +
                        LowLatencyVideoSocketSupport
                            .FormatSocketException(ex) +
                        $"；请求={preferredLocalEndpoint}。");
                    socket.Dispose();
                    socket = new Socket(
                        addressFamily,
                        SocketType.Dgram,
                        ProtocolType.Udp);
                    ConfigureSocket(socket);
                    socket.Bind(new IPEndPoint(
                        preferredLocalEndpoint.Address,
                        0));
                }

                _socket = socket;
                int port = ((IPEndPoint)socket.LocalEndPoint!).Port;
                var offer = new LowLatencyVideoOffer(
                    port,
                    LowLatencyVideoProtocol.DefaultMaxDatagramBytes,
                    LowLatencyVideoProtocol.DefaultMaxFrameBytes,
                    channelId,
                    epoch,
                    hostToViewerKey,
                    viewerToHostKey,
                    hostNoncePrefix,
                    viewerNoncePrefix,
                    challenge);
                LowLatencyVideoProtocol.ValidateOffer(offer);

                _sendCipher = new LowLatencyVideoSendCipher(
                    hostToViewerKey,
                    hostNoncePrefix,
                    channelId,
                    epoch);
                _receiveCipher = new LowLatencyVideoReceiveCipher(
                    viewerToHostKey,
                    viewerNoncePrefix,
                    channelId,
                    epoch);
                _expectedPeerAddress = expectedPeerAddress;
                _offer = offer;
                _features = features;
                if (features.HasFlag(
                        LowLatencyVideoFeatures.CongestionFeedback))
                {
                    _congestionController =
                        new LowLatencyVideoCongestionController(features);
                    if (_configuredVideoBitrateBitsPerSecond > 0 &&
                        _configuredVideoFramesPerSecond > 0)
                    {
                        _congestionController
                            .ConfigureVideoRateBudget(
                                _configuredVideoBitrateBitsPerSecond,
                                _configuredVideoFramesPerSecond);
                    }
                }

                _transportCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _connectionCancellationToken);
                Volatile.Write(ref _state, 1);
                _receiveTask = LowLatencyDedicatedThread.Start(
                    "RemoteDesk UDP host receiver",
                    () => ReceiveLoop(
                        socket,
                        _transportCancellation.Token),
                    ReceiveThreadPriority);
                // Frame serialization and encryption must not miss a 30 Hz
                // slot while the UI or thread pool is busy. This worker
                // blocks in the pacer/socket between short bursts, so Highest
                // improves tail scheduling without creating a busy loop.
                _sendTask = LowLatencyDedicatedThread.Start(
                    "RemoteDesk UDP video sender",
                    () => SendLoop(
                        socket,
                        _transportCancellation.Token),
                    ThreadPriority.Highest);
                if (features.HasFlag(
                        LowLatencyVideoFeatures
                            .AuthenticatedHeartbeat))
                {
                    _heartbeatTask = Task.Run(
                        () => SendHeartbeatLoopAsync(
                            socket,
                            _transportCancellation.Token));
                }
                if (features.HasFlag(
                        LowLatencyVideoFeatures
                            .UdpMouseInputAppliedAck))
                {
                    _mouseInputAckTask =
                        LowLatencyDedicatedThread.Start(
                            "RemoteDesk UDP mouse ACK sender",
                            () => SendMouseInputAckLoop(
                                socket,
                                _transportCancellation.Token),
                            MouseInputAckThreadPriority);
                }

                _log(
                    "低延迟 UDP 建链端点已准备：" +
                    $"本地={socket.LocalEndPoint}，" +
                    $"预期查看端={expectedPeerAddress}。");
                return offer;
            }
            catch (Exception ex)
            {
                if (ex is SocketException socketException)
                {
                    _log(
                        "低延迟 UDP 被控端 socket 创建或绑定失败：" +
                        LowLatencyVideoSocketSupport.FormatSocketException(
                            socketException) +
                        $"；本地={LowLatencyVideoSocketSupport.FormatLocalEndpoint(socket)}，" +
                        $"预期查看端={expectedPeerAddress}。");
                }

                CryptographicOperations.ZeroMemory(hostToViewerKey);
                CryptographicOperations.ZeroMemory(viewerToHostKey);
                CryptographicOperations.ZeroMemory(hostNoncePrefix);
                CryptographicOperations.ZeroMemory(viewerNoncePrefix);
                CryptographicOperations.ZeroMemory(challenge);
                _sendCipher?.Dispose();
                _receiveCipher?.Dispose();
                socket?.Dispose();
                _transportCancellation?.Dispose();
                _sendCipher = null;
                _receiveCipher = null;
                _socket = null;
                _transportCancellation = null;
                _offer = null;
                _congestionController = null;
                _features = LowLatencyVideoFeatures.None;
                Volatile.Write(ref _state, 4);
                throw;
            }
        }
    }

    public void ClearOfferSecrets()
    {
        LowLatencyVideoOffer? offer = _offer;
        if (offer is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(offer.HostToViewerKey);
        CryptographicOperations.ZeroMemory(offer.ViewerToHostKey);
        CryptographicOperations.ZeroMemory(offer.HostNoncePrefix);
        CryptographicOperations.ZeroMemory(offer.ViewerNoncePrefix);
    }

    public void MarkOfferSent()
    {
        if (Volatile.Read(ref _state) is 1 or 2)
        {
            long sentAt = Interlocked.CompareExchange(
                ref _offeredAt,
                Stopwatch.GetTimestamp(),
                0);
            if (sentAt == 0)
            {
                _setupTimeoutTask =
                    LowLatencyDedicatedThread.Start(
                        "RemoteDesk UDP host setup timeout",
                        MonitorSetupTimeout);
            }
        }
    }

    public bool TryMarkReady(ulong channelId, uint epoch)
    {
        LowLatencyVideoOffer? offer = _offer;
        if (offer is null)
        {
            return false;
        }

        if (offer.ChannelId != channelId ||
            offer.Epoch != epoch)
        {
            return false;
        }

        bool qwaveAttached = false;
        bool peerSocketConnected = false;
        string? peerSocketConnectFailure = null;
        lock (_setupStateLock)
        {
            int state = Volatile.Read(ref _state);
            if (state == 3)
            {
                return true;
            }

            long firstValidProbeAt =
                Volatile.Read(ref _firstValidProbeAt);
            if (state != 2 ||
                Volatile.Read(ref _boundPeerEndpoint) is null ||
                firstValidProbeAt == 0 ||
                Stopwatch.GetElapsedTime(firstValidProbeAt) >
                    ReadyTimeout)
            {
                return false;
            }

            // Publish the feedback baseline before the active state. Otherwise
            // a concurrent IsRouteActive read can observe state 3 with a zero
            // timestamp and immediately tear down a freshly completed route.
            Volatile.Write(
                ref _lastFeedbackAt,
                _feedbackTimeProvider.GetTimestamp());
            Socket socket = _socket!;
            IPEndPoint peerEndpoint =
                Volatile.Read(ref _boundPeerEndpoint)!;
            try
            {
                // Ready is the point at which the authenticated peer endpoint
                // becomes final. A connected UDP socket can use Send(Span)
                // instead of SendTo(Span, EndPoint), avoiding roughly 72 B of
                // endpoint marshalling garbage for every 4K video packet.
                // The receive loop may already be blocked in ReceiveFrom;
                // Windows supports connecting the socket concurrently, and
                // subsequent receives are then kernel-filtered to this peer.
                _connectPeerSocket(socket, peerEndpoint);
                Volatile.Write(ref _peerSocketConnected, 1);
                peerSocketConnected = true;
            }
            catch (ObjectDisposedException)
            {
                Volatile.Write(ref _peerSocketConnected, 0);
                return false;
            }
            catch (SocketException ex)
            {
                Volatile.Write(ref _peerSocketConnected, 0);
                if (_transportCancellation?.IsCancellationRequested ==
                    true)
                {
                    return false;
                }

                peerSocketConnectFailure =
                    LowLatencyVideoSocketSupport
                        .FormatSocketException(ex);
            }
            catch (InvalidOperationException ex)
            {
                Volatile.Write(ref _peerSocketConnected, 0);
                peerSocketConnectFailure = ex.Message;
            }

            // DisableRoute also takes _setupStateLock. Attach and publish the
            // lease before state 3, so shutdown cannot observe an active
            // route without also owning (and later disposing) its qWAVE flow.
            WindowsQwaveVideoFlow? qwaveVideoFlow =
                WindowsQwaveVideoFlow.TryAttach(
                    socket,
                    peerEndpoint);
            _qwaveVideoFlow = qwaveVideoFlow;
            qwaveAttached = qwaveVideoFlow is not null;
            if (Interlocked.CompareExchange(ref _state, 3, 2) != 2)
            {
                _qwaveVideoFlow = null;
                qwaveVideoFlow?.Dispose();
                return false;
            }
        }

        _readyReceivedSignal.TrySetResult();
        if (qwaveAttached)
        {
            _log(
                "Windows qWAVE 已将低延迟 UDP 画面标记为 AudioVideo 流量。");
        }

        if (peerSocketConnected)
        {
            _log(
                "低延迟 UDP 已固定认证对端并启用零分配发送。");
        }
        else if (!string.IsNullOrWhiteSpace(
                     peerSocketConnectFailure))
        {
            _log(
                "低延迟 UDP 固定认证对端失败，继续使用兼容发送路径：" +
                peerSocketConnectFailure);
        }

        _log(
            (_features.HasFlag(
                    LowLatencyVideoFeatures.UdpMouseInput)
                    ? "低延迟 UDP 画面与鼠标移动通道已启用；" +
                        "点击、滚轮、键盘、控制和文件仍走可靠 TCP。"
                    : "低延迟 UDP 画面通道已启用；" +
                        "控制、输入和文件仍走 TCP。") +
            " " +
            FormatHandshakeDiagnostics(
                state: 3,
                phase: "已就绪"));
        return true;
    }

    public bool Matches(ulong channelId, uint epoch)
    {
        LowLatencyVideoOffer? offer = _offer;
        return offer is not null &&
            offer.ChannelId == channelId &&
            offer.Epoch == epoch;
    }

    public bool TryQueueJpegFrame(
        int width,
        int height,
        double captureMilliseconds,
        double encodeMilliseconds,
        ReadOnlyMemory<byte> jpegBytes,
        bool allowLatencyBudgetDrop = false)
    {
        return TryQueueFrame(
            MessageType.Frame,
            width,
            height,
            RemoteFrameEncoding.Jpeg,
            RemoteFrameFlags.KeyFrame,
            captureMilliseconds,
            encodeMilliseconds,
            jpegBytes,
            allowLatencyBudgetDrop);
    }

    public bool TryQueueVideoFrame(
        int width,
        int height,
        RemoteFrameEncoding encoding,
        RemoteFrameFlags flags,
        double captureMilliseconds,
        double encodeMilliseconds,
        ReadOnlyMemory<byte> encodedBytes,
        bool allowLatencyBudgetDrop = false)
    {
        bool recovery = IsIndependentlyRecoverableFrame(
            MessageType.VideoFrame,
            flags);
        bool negotiatedDependentFrame =
            encoding == RemoteFrameEncoding.H264AnnexB &&
            !flags.HasFlag(RemoteFrameFlags.KeyFrame) &&
            _features.HasFlag(
                LowLatencyVideoFeatures.ShortGopH264);
        if (!recovery && !negotiatedDependentFrame)
        {
            DisableRoute(
                "低延迟 UDP 视频收到未协商的依赖帧，已回退 TCP。");
            return false;
        }

        return TryQueueFrame(
            MessageType.VideoFrame,
            width,
            height,
            encoding,
            flags,
            captureMilliseconds,
            encodeMilliseconds,
            encodedBytes,
            allowLatencyBudgetDrop);
    }

    private bool TryQueueFrame(
        MessageType frameKind,
        int width,
        int height,
        RemoteFrameEncoding encoding,
        RemoteFrameFlags flags,
        double captureMilliseconds,
        double encodeMilliseconds,
        ReadOnlyMemory<byte> encodedBytes,
        bool allowLatencyBudgetDrop)
    {
        if (!IsRouteActive ||
            Volatile.Read(ref _videoRouteDisabled) != 0)
        {
            return false;
        }

        LowLatencyVideoCongestionController? congestionController =
            _congestionController;
        bool shortGopVideo =
            frameKind == MessageType.VideoFrame &&
            encoding == RemoteFrameEncoding.H264AnnexB &&
            _features.HasFlag(
                LowLatencyVideoFeatures.ShortGopH264);
        bool independentlyRecoverable =
            IsIndependentlyRecoverableFrame(
                frameKind,
                flags);
        long recoveryGeneration = 0;
        if (shortGopVideo)
        {
            if (independentlyRecoverable)
            {
                lock (_shortGopChainLock)
                {
                    recoveryGeneration = checked(
                        _latestShortGopRecoveryGeneration + 1);
                    Volatile.Write(
                        ref _latestShortGopRecoveryGeneration,
                        recoveryGeneration);
                }
            }
            else
            {
                lock (_shortGopChainLock)
                {
                    recoveryGeneration =
                        _latestShortGopRecoveryGeneration;
                }
            }
        }
        else if (Volatile.Read(
                     ref _latestShortGopRecoveryGeneration) != 0)
        {
            // A JPEG or legacy video frame changes the decoder chain. Any P
            // frame that arrives afterward must wait for a fresh short-GOP
            // recovery point.
            lock (_shortGopChainLock)
            {
                checked
                {
                    Volatile.Write(
                        ref _latestShortGopRecoveryGeneration,
                        _latestShortGopRecoveryGeneration + 1);
                }
            }
        }

        LowLatencyVideoOffer offer = _offer!;
        int frameHeaderLength = frameKind switch
        {
            MessageType.Frame => RemoteMessageCodec.FrameHeaderLength,
            MessageType.VideoFrame => RemoteMessageCodec.VideoFrameHeaderLength,
            _ => throw new ArgumentOutOfRangeException(
                nameof(frameKind),
                frameKind,
                "消息类型不是画面帧。")
        };
        if (encodedBytes.Length > offer.MaxFrameBytes - frameHeaderLength)
        {
            DisableRoute("画面帧超过 UDP 通道上限，已回退 TCP。");
            return false;
        }

        int frameLength = checked(frameHeaderLength + encodedBytes.Length);
        if (congestionController is not null &&
            frameLength > congestionController.GetFrameBudgetBytes())
        {
            if (!allowLatencyBudgetDrop)
            {
                DisableRoute(
                    "画面帧无法在 UDP 延迟预算内完成，已回退 TCP。");
                return false;
            }

            congestionController.RecordQueuedFrame(
                replacedPendingFrame: false);
            congestionController.RecordOversizedFrameDrop();
            return true;
        }

        byte[] frameBuffer = ArrayPool<byte>.Shared.Rent(frameLength);
        if (frameKind == MessageType.Frame)
        {
            RemoteMessageCodec.WriteFrameHeader(
                frameBuffer.AsSpan(0, frameHeaderLength),
                width,
                height,
                captureMilliseconds,
                encodeMilliseconds);
        }
        else
        {
            RemoteMessageCodec.WriteVideoFrameHeader(
                frameBuffer.AsSpan(0, frameHeaderLength),
                width,
                height,
                encoding,
                flags,
                captureMilliseconds,
                encodeMilliseconds);
        }

        encodedBytes.CopyTo(frameBuffer.AsMemory(frameHeaderLength));
        var frame = new PendingFrame(
            frameKind,
            width,
            height,
            frameBuffer,
            frameLength,
            allowLatencyBudgetDrop,
            independentlyRecoverable,
            shortGopVideo,
            recoveryGeneration);
        PendingFrame? previous;
        if (shortGopVideo)
        {
            bool admitted;
            lock (_shortGopChainLock)
            {
                admitted =
                    Volatile.Read(ref _state) == 3 &&
                    (independentlyRecoverable
                        ? _latestShortGopRecoveryGeneration ==
                            recoveryGeneration
                        : TryAdmitShortGopDependentLocked(
                            recoveryGeneration));
                previous = admitted
                    ? Interlocked.Exchange(ref _pendingFrame, frame)
                    : null;
            }

            if (!admitted)
            {
                // The matching IDR was dropped, superseded, or has not entered
                // serialization yet. Suppress this P frame without switching
                // transports; the next IDR is at most two source periods away.
                // This is reference-chain admission, not queue congestion.
                if (!independentlyRecoverable)
                {
                    Interlocked.Increment(
                        ref _suppressedShortGopDependentFrames);
                }

                ReturnFrame(frame);
                return true;
            }
        }
        else
        {
            previous = Interlocked.Exchange(ref _pendingFrame, frame);
        }

        congestionController?.RecordQueuedFrame(previous is not null);
        if (previous is not null)
        {
            Interlocked.Increment(ref _replacedPendingFrames);
            ReturnFrame(previous);
        }

        if (!IsRouteActive)
        {
            PendingFrame? removed = Interlocked.CompareExchange(
                ref _pendingFrame,
                null,
                frame);
            if (ReferenceEquals(removed, frame))
            {
                ReturnFrame(frame);
            }

            return false;
        }

        if (previous is null)
        {
            SignalSender();
        }

        return true;
    }

    private bool TryAdmitShortGopDependentLocked(
        long recoveryGeneration)
    {
        if (recoveryGeneration <= 0 ||
            _latestShortGopRecoveryGeneration != recoveryGeneration ||
            (_serializedShortGopRecoveryGeneration != recoveryGeneration &&
             _serializingShortGopRecoveryGeneration != recoveryGeneration) ||
            _admittedShortGopDependentGeneration == recoveryGeneration)
        {
            return false;
        }

        Volatile.Write(
            ref _admittedShortGopDependentGeneration,
            recoveryGeneration);
        return true;
    }

    public LowLatencyVideoNetworkSnapshot CollectNetworkSnapshot()
    {
        return _congestionController is not null && IsRouteActive
            ? _congestionController.CollectSnapshot()
            : default;
    }

    public LowLatencyVideoSendSnapshot CollectSendSnapshot()
    {
        return new LowLatencyVideoSendSnapshot(
            Volatile.Read(
                ref _suppressedShortGopDependentFrames),
            Volatile.Read(
                ref _serializedShortGopRecoveryFrames),
            Volatile.Read(
                ref _serializedShortGopDependentFrames),
            Volatile.Read(ref _replacedPendingFrames));
    }

    internal LowLatencyVideoHostHandshakeSnapshot
        CollectHandshakeSnapshot() =>
            new(
                Volatile.Read(ref _handshakeRawPacketCount),
                Volatile.Read(ref _handshakeAddressRejectedCount),
                Volatile.Read(ref _handshakeDecryptFailureCount),
                Volatile.Read(ref _handshakeValidProbeCount),
                Volatile.Read(ref _handshakeAckSentCount));

    public void DisableRoute(
        string? logMessage = null,
        bool notifyViewer = true,
        byte reason = 1)
    {
        int previous;
        lock (_setupStateLock)
        {
            previous = Interlocked.Exchange(ref _state, 4);
            Volatile.Write(ref _boundPeerEndpoint, null);
        }

        PendingFrame? pendingFrame;
        lock (_shortGopChainLock)
        {
            Volatile.Write(
                ref _latestShortGopRecoveryGeneration,
                0);
            Volatile.Write(
                ref _serializedShortGopRecoveryGeneration,
                0);
            Volatile.Write(
                ref _serializingShortGopRecoveryGeneration,
                0);
            Volatile.Write(
                ref _admittedShortGopDependentGeneration,
                0);
            pendingFrame = Interlocked.Exchange(
                ref _pendingFrame,
                null);
        }
        if (pendingFrame is not null)
        {
            ReturnFrame(pendingFrame);
        }

        if (previous is 1 or 2 or 3 && !string.IsNullOrWhiteSpace(logMessage))
        {
            _log(logMessage);
        }

        if (previous is 2 or 3 && notifyViewer)
        {
            StartFallbackBarrier(reason);
        }

        if (previous is 1 or 2 or 3)
        {
            _ = BeginIoShutdown();
        }
    }

    public bool TryDisableVideoRouteKeepingUdpInput()
    {
        long lastFeedbackAt = Volatile.Read(
            ref _lastFeedbackAt);
        if (lastFeedbackAt == 0 ||
            LowLatencyVideoFeedbackPolicy.HasExpired(
                _feedbackTimeProvider.GetElapsedTime(
                    lastFeedbackAt)))
        {
            return false;
        }

        lock (_setupStateLock)
        {
            if (Volatile.Read(ref _state) != 3 ||
                !_features.HasFlag(
                    LowLatencyVideoFeatures
                        .AuthenticatedHeartbeat) ||
                !_features.HasFlag(
                    LowLatencyVideoFeatures.UdpMouseInput))
            {
                return false;
            }

            Volatile.Write(ref _videoRouteDisabled, 1);
        }

        PendingFrame? pendingFrame;
        lock (_shortGopChainLock)
        {
            Volatile.Write(
                ref _latestShortGopRecoveryGeneration,
                0);
            Volatile.Write(
                ref _serializedShortGopRecoveryGeneration,
                0);
            Volatile.Write(
                ref _serializingShortGopRecoveryGeneration,
                0);
            Volatile.Write(
                ref _admittedShortGopDependentGeneration,
                0);
            pendingFrame = Interlocked.Exchange(
                ref _pendingFrame,
                null);
        }

        if (pendingFrame is not null)
        {
            ReturnFrame(pendingFrame);
        }

        _log(
            "低延迟 UDP 视频已暂停并切换 TCP；" +
            "认证 UDP 心跳与鼠标输入继续保持。");
        return true;
    }

    public async Task StopVideoForCaptureTargetChangeAsync()
    {
        int previous = Volatile.Read(ref _state);
        if (previous is not (1 or 2 or 3))
        {
            return;
        }

        // Once video has been fenced off while the authenticated UDP input
        // route remains alive, later target transitions need no second wire
        // barrier: the viewer is already in TCP-video state 5 and rejects all
        // late UDP video. Re-sending reason 4 would be interpreted as an
        // invalid 5 -> 5 transition and unnecessarily tear down UDP input.
        if (previous == 3 &&
            Volatile.Read(ref _videoRouteDisabled) != 0)
        {
            return;
        }

        LowLatencyVideoOffer? offer = _offer;
        bool preservedUdpInput =
            TryDisableVideoRouteKeepingUdpInput();
        byte reason = preservedUdpInput
            ? LowLatencyVideoFallbackReasons.PreserveUdpInput
            : (byte)1;
        if (!preservedUdpInput)
        {
            DisableRoute(
                "捕获目标已变化，低延迟 UDP 画面已有序回退 TCP，" +
                "避免旧屏幕数据包越过目标切换边界。",
                notifyViewer: false);
        }

        if (offer is not null &&
            _sendFallbackBarrier is not null)
        {
            await _sendFallbackBarrier(
                    offer.ChannelId,
                    offer.Epoch,
                    reason)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        DisableRoute(notifyViewer: false);
        Task ioShutdownTask = BeginIoShutdown();
        Task? fallbackTask;
        lock (_fallbackTaskLock)
        {
            fallbackTask = _fallbackTask;
        }

        Task[] tasks = new[] { ioShutdownTask, _setupTimeoutTask, fallbackTask }
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!IsStopException(ex))
                {
                    _log($"低延迟 UDP 清理异常：{ex.Message}");
                }
            }
        }

        _sendSignal.Dispose();
        _mouseInputAckSignal.Dispose();
    }

    private void ReceiveLoop(
        Socket socket,
        CancellationToken cancellationToken)
    {
        byte[] receiveBuffer = new byte[ushort.MaxValue];
        byte[] plaintextBuffer = ArrayPool<byte>.Shared.Rent(
            _offer!.MaxDatagramBytes);
        EndPoint receiveFrom = _expectedPeerAddress!.AddressFamily == AddressFamily.InterNetwork
            ? new IPEndPoint(IPAddress.Any, 0)
            : new IPEndPoint(IPAddress.IPv6Any, 0);
        try
        {
            using CancellationTokenRegistration cancellationRegistration =
                cancellationToken.Register(socket.Dispose);
            while (!cancellationToken.IsCancellationRequested)
            {
                PendingHostUdpMouseMove? latestMouseMove = null;
                int batchDatagrams = 0;
                do
                {
                    int receivedBytes = socket.ReceiveFrom(
                        receiveBuffer.AsSpan(),
                        SocketFlags.None,
                        ref receiveFrom);
                    batchDatagrams++;
                    bool setupPending =
                        Volatile.Read(ref _state) is 1 or 2;
                    if (setupPending)
                    {
                        Interlocked.Increment(
                            ref _handshakeRawPacketCount);
                    }

                    if (receiveFrom is not IPEndPoint remoteEndpoint ||
                        !AddressesEqual(
                            remoteEndpoint.Address,
                            _expectedPeerAddress!))
                    {
                        if (setupPending)
                        {
                            Interlocked.Increment(
                                ref _handshakeAddressRejectedCount);
                        }

                        continue;
                    }

                    if (receivedBytes > _offer!.MaxDatagramBytes ||
                        !_receiveCipher!.TryDecrypt(
                            receiveBuffer.AsSpan(0, receivedBytes),
                            plaintextBuffer,
                            out LowLatencyVideoDatagram packet))
                    {
                        if (setupPending)
                        {
                            Interlocked.Increment(
                                ref _handshakeDecryptFailureCount);
                        }

                        continue;
                    }

                    try
                    {
                        switch (packet.Kind)
                        {
                            case LowLatencyVideoDatagramKind.BindProbe:
                                HandleBindProbe(
                                    socket,
                                    remoteEndpoint,
                                    packet,
                                    cancellationToken);
                                break;
                            case LowLatencyVideoDatagramKind.Feedback:
                                HandleFeedback(
                                    remoteEndpoint,
                                    packet);
                                break;
                            case LowLatencyVideoDatagramKind.FeedbackV2:
                                HandleFeedbackV2(
                                    remoteEndpoint,
                                    packet);
                                break;
                            case LowLatencyVideoDatagramKind.MouseMove:
                                if (TryDecodeUdpMouseMove(
                                        remoteEndpoint,
                                        packet,
                                        out PendingHostUdpMouseMove
                                            mouseMove) &&
                                    (latestMouseMove is null ||
                                        mouseMove.Sequence >
                                            latestMouseMove.Value
                                                .Sequence))
                                {
                                    latestMouseMove = mouseMove;
                                }

                                break;
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(
                            packet.Plaintext.Span);
                    }
                }
                while (
                    batchDatagrams <
                        ReceiveBacklogBatchDatagramLimit &&
                    socket.Available > 0);

                if (latestMouseMove is { } mouseMoveToApply)
                {
                    ApplyUdpMouseMove(mouseMoveToApply);
                }
            }
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested &&
                Volatile.Read(ref _disposed) == 0)
            {
                DisableRoute($"低延迟 UDP 接收异常，已回退 TCP：{ex.Message}");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(receiveBuffer);
            ArrayPool<byte>.Shared.Return(
                plaintextBuffer,
                clearArray: true);
        }
    }

    private void HandleBindProbe(
        Socket socket,
        IPEndPoint remoteEndpoint,
        LowLatencyVideoDatagram packet,
        CancellationToken cancellationToken)
    {
        LowLatencyVideoOffer offer = _offer!;
        if (packet.FrameSequence != 0 ||
            packet.FrameLength != offer.Challenge.Length ||
            packet.FragmentOffset != 0 ||
            packet.FragmentIndex != 0 ||
            packet.FragmentCount != 1 ||
            packet.FrameKind != 0 ||
            packet.Flags != 0 ||
            !CryptographicOperations.FixedTimeEquals(
                packet.Plaintext.Span,
                offer.Challenge))
        {
            return;
        }

        bool firstValidProbe = false;
        bool peerEndpointChanged = false;
        lock (_setupStateLock)
        {
            int state = Volatile.Read(ref _state);
            if (state is not (1 or 2))
            {
                return;
            }

            if (_hasBindProbePacketSequence &&
                packet.PacketSequence <=
                    _latestBindProbePacketSequence)
            {
                return;
            }

            IPEndPoint? pinnedEndpoint =
                Volatile.Read(ref _boundPeerEndpoint);
            long now = Stopwatch.GetTimestamp();
            if (state == 1)
            {
                long offeredAt = Volatile.Read(ref _offeredAt);
                if (offeredAt != 0 &&
                    Stopwatch.GetElapsedTime(offeredAt, now) >
                        ProbeTimeout)
                {
                    return;
                }

                Volatile.Write(ref _boundPeerEndpoint, remoteEndpoint);
                Volatile.Write(ref _firstValidProbeAt, now);
                if (Interlocked.CompareExchange(
                        ref _state,
                        2,
                        1) != 1)
                {
                    return;
                }

                firstValidProbe = true;
            }
            else
            {
                long firstValidProbeAt =
                    Volatile.Read(ref _firstValidProbeAt);
                if (firstValidProbeAt == 0 ||
                    Stopwatch.GetElapsedTime(
                        firstValidProbeAt,
                        now) > ReadyTimeout)
                {
                    return;
                }

                if (pinnedEndpoint is not null &&
                    !pinnedEndpoint.Equals(remoteEndpoint))
                {
                    // An otherwise healthy routed path can black-hole one
                    // UDP source port. Before TCP Ready, an authenticated
                    // challenge response from the same TCP peer is allowed
                    // to move the pin so the viewer can rotate candidates.
                    // State 3 never enters this branch, so an active route
                    // remains immutable.
                    Volatile.Write(
                        ref _boundPeerEndpoint,
                        remoteEndpoint);
                    // A candidate whose reverse Ack path was black-holed
                    // receives its own bounded Ready interval. The setup
                    // monitor loops and recalculates this timestamp after
                    // every wake-up, so refreshing it cannot orphan state 2.
                    Volatile.Write(
                        ref _firstValidProbeAt,
                        now);
                    peerEndpointChanged = true;
                }
            }

            Interlocked.Increment(
                ref _handshakeValidProbeCount);
            _latestBindProbePacketSequence =
                packet.PacketSequence;
            _hasBindProbePacketSequence = true;
        }

        if (firstValidProbe)
        {
            _validProbeReceivedSignal.TrySetResult();
        }

        if (peerEndpointChanged)
        {
            _log(
                "低延迟 UDP 查看端握手端口已通过认证并重新绑定：" +
                $"{remoteEndpoint}。");
        }

        byte[] ack = _sendCipher!.Encrypt(
            LowLatencyVideoDatagramKind.BindAck,
            0,
            offer.Challenge.Length,
            0,
            0,
            1,
            0,
            0,
            offer.Challenge);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            int sent = socket.SendTo(
                ack,
                SocketFlags.None,
                remoteEndpoint);
            if (sent != ack.Length)
            {
                throw new IOException(
                    "UDP bind acknowledgement was only partially sent.");
            }

            Interlocked.Increment(ref _handshakeAckSentCount);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ack);
        }
    }

    private void HandleFeedback(
        IPEndPoint remoteEndpoint,
        LowLatencyVideoDatagram packet)
    {
        IPEndPoint? pinnedEndpoint = Volatile.Read(ref _boundPeerEndpoint);
        if (pinnedEndpoint is null ||
            !pinnedEndpoint.Equals(remoteEndpoint) ||
            packet.FrameSequence != 0 ||
            packet.FrameLength != sizeof(ulong) ||
            packet.FragmentOffset != 0 ||
            packet.FragmentIndex != 0 ||
            packet.FragmentCount != 1 ||
            packet.FrameKind != 0 ||
            packet.Flags != 0 ||
            packet.Plaintext.Length != sizeof(ulong))
        {
            return;
        }

        if (Volatile.Read(ref _state) == 3)
        {
            Volatile.Write(
                ref _lastFeedbackAt,
                _feedbackTimeProvider.GetTimestamp());
        }
    }

    private void HandleFeedbackV2(
        IPEndPoint remoteEndpoint,
        LowLatencyVideoDatagram packet)
    {
        IPEndPoint? pinnedEndpoint = Volatile.Read(ref _boundPeerEndpoint);
        if (!_features.HasFlag(
                LowLatencyVideoFeatures.CongestionFeedback) ||
            pinnedEndpoint is null ||
            !pinnedEndpoint.Equals(remoteEndpoint) ||
            packet.FrameSequence != 0 ||
            packet.FrameLength !=
                LowLatencyVideoFeedbackV2Codec.PayloadLength ||
            packet.FragmentOffset != 0 ||
            packet.FragmentIndex != 0 ||
            packet.FragmentCount != 1 ||
            packet.FrameKind != 0 ||
            packet.Flags != 0 ||
            !LowLatencyVideoFeedbackV2Codec.TryDecode(
                packet.Plaintext.Span,
                out LowLatencyVideoFeedbackV2 feedback) ||
            _congestionController?.TryObserveFeedback(feedback) != true)
        {
            return;
        }

        if (Volatile.Read(ref _state) == 3)
        {
            Volatile.Write(
                ref _lastFeedbackAt,
                _feedbackTimeProvider.GetTimestamp());
        }
    }

    private bool TryDecodeUdpMouseMove(
        IPEndPoint remoteEndpoint,
        LowLatencyVideoDatagram packet,
        out PendingHostUdpMouseMove mouseMove)
    {
        mouseMove = default;
        IPEndPoint? pinnedEndpoint = Volatile.Read(
            ref _boundPeerEndpoint);
        if (!_features.HasFlag(
                LowLatencyVideoFeatures.UdpMouseInput) ||
            _applyUdpMouseMove is null ||
            Volatile.Read(ref _state) is not (2 or 3) ||
            pinnedEndpoint is null ||
            !pinnedEndpoint.Equals(remoteEndpoint) ||
            packet.FrameSequence > (ulong)long.MaxValue ||
            packet.FrameLength !=
                RemoteMessageCodec.InputPayloadLength ||
            packet.FragmentOffset != 0 ||
            packet.FragmentIndex != 0 ||
            packet.FragmentCount != 1 ||
            packet.FrameKind != MessageType.Input ||
            packet.Flags != 0 ||
            packet.Plaintext.Length !=
                RemoteMessageCodec.InputPayloadLength)
        {
            return false;
        }

        long sequence = checked((long)packet.FrameSequence);
        if (sequence <= Volatile.Read(
                ref _latestUdpMouseMoveSequence))
        {
            return false;
        }

        try
        {
            RemoteInputCommand command =
                RemoteMessageCodec.DecodeInput(
                    packet.Plaintext.Span);
            if (command.Kind != RemoteInputKind.MouseMove)
            {
                return false;
            }

            mouseMove = new PendingHostUdpMouseMove(
                remoteEndpoint,
                sequence,
                command);
            return true;
        }
        catch (Exception ex) when (
            ex is InvalidDataException or
                ArgumentException or
                InvalidOperationException)
        {
            string? message =
                _udpMouseInputErrorLog.CreateMessage(
                    $"低延迟鼠标输入：{ex.Message}",
                    Environment.TickCount64);
            if (message is not null)
            {
                _log(message);
            }

            return false;
        }
    }

    private void ApplyUdpMouseMove(
        PendingHostUdpMouseMove mouseMove)
    {
        IPEndPoint? pinnedEndpoint = Volatile.Read(
            ref _boundPeerEndpoint);
        if (!_features.HasFlag(
                LowLatencyVideoFeatures.UdpMouseInput) ||
            _applyUdpMouseMove is null ||
            Volatile.Read(ref _state) is not (2 or 3) ||
            pinnedEndpoint is null ||
            !pinnedEndpoint.Equals(mouseMove.RemoteEndpoint) ||
            mouseMove.Sequence <= Volatile.Read(
                ref _latestUdpMouseMoveSequence))
        {
            return;
        }

        try
        {
            // The receive loop is serialized, so publishing only the newest
            // authenticated sequence after draining an existing backlog
            // preserves strict ordering while avoiding stale cursor replay.
            Volatile.Write(
                ref _latestUdpMouseMoveSequence,
                mouseMove.Sequence);
            _applyUdpMouseMove(mouseMove.Command);
            QueueMouseInputAppliedAck(mouseMove.Sequence);
        }
        catch (Exception ex) when (
            ex is InvalidDataException or
                ArgumentException or
                InvalidOperationException)
        {
            string? message =
                _udpMouseInputErrorLog.CreateMessage(
                    $"低延迟鼠标输入：{ex.Message}",
                    Environment.TickCount64);
            if (message is not null)
            {
                _log(message);
            }
        }
    }

    private void QueueMouseInputAppliedAck(long sequence)
    {
        if (!_features.HasFlag(
                LowLatencyVideoFeatures
                    .UdpMouseInputAppliedAck) ||
            Volatile.Read(
                ref _udpMouseInputAckDisabled) != 0 ||
            Volatile.Read(ref _state) is not (2 or 3))
        {
            return;
        }

        long previous = Interlocked.Exchange(
            ref _pendingUdpMouseInputAckSequence,
            sequence);
        if (previous >= 0)
        {
            return;
        }

        try
        {
            _mouseInputAckSignal.Release();
        }
        catch (Exception ex) when (
            ex is SemaphoreFullException or
                ObjectDisposedException)
        {
        }
    }

    private void SendMouseInputAckLoop(
        Socket socket,
        CancellationToken cancellationToken)
    {
        using var pacingWaiter =
            new WindowsHighResolutionPacingWaiter();
        byte[] datagram =
            new byte[
                LowLatencyVideoProtocol.HeaderLength +
                LowLatencyVideoProtocol.TagLength];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                _mouseInputAckSignal.Wait(cancellationToken);
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (Volatile.Read(ref _state) is not (2 or 3))
                    {
                        Interlocked.Exchange(
                            ref _pendingUdpMouseInputAckSequence,
                            -1);
                        break;
                    }

                    long lastSentAt = Volatile.Read(
                        ref _lastUdpMouseInputAckSentAt);
                    if (lastSentAt != 0)
                    {
                        TimeSpan remaining =
                            MouseInputAckMinimumInterval -
                            Stopwatch.GetElapsedTime(lastSentAt);
                        if (remaining > TimeSpan.Zero)
                        {
                            pacingWaiter.Wait(
                                remaining,
                                cancellationToken);
                        }
                    }

                    long sequence = Interlocked.Exchange(
                        ref _pendingUdpMouseInputAckSequence,
                        -1);
                    if (sequence < 0)
                    {
                        break;
                    }

                    IPEndPoint? endpoint = Volatile.Read(
                        ref _boundPeerEndpoint);
                    if (endpoint is null)
                    {
                        continue;
                    }

                    int datagramLength =
                        _sendCipher!.EncryptInto(
                        datagram,
                        LowLatencyVideoDatagramKind
                            .MouseMoveAppliedAck,
                        checked((ulong)sequence),
                        0,
                        0,
                        0,
                        1,
                        MessageType.Input,
                        0,
                        ReadOnlySpan<byte>.Empty);
                    cancellationToken.ThrowIfCancellationRequested();
                    int sent;
                    try
                    {
                        sent = Volatile.Read(
                                ref _peerSocketConnected) != 0
                            ? socket.Send(
                                datagram.AsSpan(
                                    0,
                                    datagramLength),
                                SocketFlags.None)
                            : socket.SendTo(
                                datagram.AsSpan(
                                    0,
                                    datagramLength),
                                SocketFlags.None,
                                endpoint);
                    }
                    catch (SocketException ex) when (
                        LowLatencyVideoSocketSupport
                            .IsTransientDatagramSendError(ex))
                    {
                        string? message =
                            _transientDatagramSendErrorLog
                                .CreateMessage(
                                    "低延迟鼠标应用回执发送队列暂时已满，" +
                                    "已保留通道并等待下一条回执：" +
                                    LowLatencyVideoSocketSupport
                                        .FormatSocketException(ex));
                        if (message is not null)
                        {
                            _log(message);
                        }

                        continue;
                    }
                    if (sent != datagramLength)
                    {
                        throw new IOException(
                            "UDP mouse acknowledgement was only partially sent.");
                    }

                    Volatile.Write(
                        ref _lastUdpMouseInputAckSentAt,
                        Stopwatch.GetTimestamp());
                    Interlocked.Increment(
                        ref _udpMouseInputAckDatagramCount);

                    if (Volatile.Read(
                            ref _pendingUdpMouseInputAckSequence) <
                        0)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or
                ObjectDisposedException)
        {
        }
        catch (Exception ex) when (
            ex is SocketException or
                IOException or
                CryptographicException)
        {
            Interlocked.Exchange(
                ref _udpMouseInputAckDisabled,
                1);
            Interlocked.Exchange(
                ref _pendingUdpMouseInputAckSequence,
                -1);
            if (!cancellationToken.IsCancellationRequested)
            {
                _log(
                    $"低延迟鼠标应用回执已停用：{ex.Message}");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(datagram);
        }
    }

    private void SendLoop(
        Socket socket,
        CancellationToken cancellationToken)
    {
        using var pacingWaiter =
            new WindowsHighResolutionPacingWaiter();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                _sendSignal.Wait(cancellationToken);
                PendingFrame? frame;
                while ((frame = Interlocked.Exchange(ref _pendingFrame, null)) is not null)
                {
                    bool trackingShortGopRecovery = false;
                    bool completed = false;
                    try
                    {
                        if (!IsRouteActive)
                        {
                            continue;
                        }

                        if (frame.IsShortGopVideo &&
                            frame.IsIndependentlyRecoverable)
                        {
                            lock (_shortGopChainLock)
                            {
                                Volatile.Write(
                                    ref _serializingShortGopRecoveryGeneration,
                                    frame.RecoveryGeneration);
                                trackingShortGopRecovery = true;
                            }
                        }

                        completed = SendFrame(
                            socket,
                            frame,
                            pacingWaiter,
                            cancellationToken);
                        if (completed &&
                            frame.IsGop1Video)
                        {
                            Volatile.Write(
                                ref _serializedGop1StreamSignature,
                                frame.Gop1StreamSignature);
                            Volatile.Write(
                                ref _lastSerializedGop1RecoveryAt,
                                Stopwatch.GetTimestamp());
                        }

                        if (completed && frame.IsShortGopVideo)
                        {
                            Interlocked.Increment(
                                ref (frame.IsIndependentlyRecoverable
                                    ? ref _serializedShortGopRecoveryFrames
                                    : ref _serializedShortGopDependentFrames));
                        }
                    }
                    catch (SocketException ex) when (
                        LowLatencyVideoSocketSupport
                            .IsTransientDatagramSendError(ex))
                    {
                        // Queue exhaustion is a transient congestion signal,
                        // not proof that the authenticated route disappeared.
                        // Abandon this incomplete latest-only frame and keep
                        // UDP alive so the next independently decodable frame
                        // can recover immediately.
                        _congestionController?
                            .RecordAbortedFrameSend();
                        string? message =
                            _transientDatagramSendErrorLog
                                .CreateMessage(
                                    "低延迟 UDP 发送队列暂时已满，" +
                                    "已丢弃当前帧并保持 UDP：" +
                                    LowLatencyVideoSocketSupport
                                        .FormatSocketException(ex));
                        if (message is not null)
                        {
                            _log(message);
                        }
                    }
                    finally
                    {
                        if (trackingShortGopRecovery)
                        {
                            CompleteShortGopRecoverySerialization(
                                frame.RecoveryGeneration,
                                completed);
                        }

                        ReturnFrame(frame);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested &&
                Volatile.Read(ref _disposed) == 0)
            {
                DisableRoute($"低延迟 UDP 发送异常，已回退 TCP：{ex.Message}");
            }
        }
    }

    private async Task SendHeartbeatLoopAsync(
        Socket socket,
        CancellationToken cancellationToken)
    {
        byte[] datagram =
            new byte[
                LowLatencyVideoProtocol.HeaderLength +
                LowLatencyVideoProtocol.TagLength];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(
                        HeartbeatInterval,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (Volatile.Read(ref _state) != 3)
                {
                    continue;
                }

                // WGC may legitimately block forever waiting for a changed
                // desktop frame. During that silence the capture loop does
                // not call TryQueueFrame, so it cannot be the sole owner of
                // the feedback deadline. Keep the route check on this
                // independent heartbeat cadence: a live host-to-viewer path
                // must not hide a failed viewer-to-host path and leave UDP
                // mouse input advertised while every move is being lost.
                if (!IsRouteActive)
                {
                    continue;
                }

                IPEndPoint? endpoint = Volatile.Read(
                    ref _boundPeerEndpoint);
                if (endpoint is null)
                {
                    continue;
                }

                int datagramLength =
                    _sendCipher!.EncryptInto(
                        datagram,
                        LowLatencyVideoDatagramKind.Heartbeat,
                        0,
                        0,
                        0,
                        0,
                        1,
                        0,
                        0,
                        ReadOnlySpan<byte>.Empty);
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    int sent = Volatile.Read(
                            ref _peerSocketConnected) != 0
                        ? socket.Send(
                            datagram.AsSpan(
                                0,
                                datagramLength),
                            SocketFlags.None)
                        : socket.SendTo(
                            datagram.AsSpan(
                                0,
                                datagramLength),
                            SocketFlags.None,
                            endpoint);
                    if (sent != datagramLength)
                    {
                        throw new IOException(
                            "UDP heartbeat was only partially sent.");
                    }
                }
                catch (SocketException ex) when (
                    LowLatencyVideoSocketSupport
                        .IsTransientDatagramSendError(ex))
                {
                    string? message =
                        _transientDatagramSendErrorLog
                            .CreateMessage(
                                "低延迟 UDP 心跳发送队列暂时已满，" +
                                "将在下一周期重试：" +
                                LowLatencyVideoSocketSupport
                                    .FormatSocketException(ex));
                    if (message is not null)
                    {
                        _log(message);
                    }
                }
            }
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or
                ObjectDisposedException)
        {
        }
        catch (Exception ex) when (
            ex is SocketException or
                IOException or
                CryptographicException)
        {
            if (!cancellationToken.IsCancellationRequested &&
                Volatile.Read(ref _disposed) == 0)
            {
                DisableRoute(
                    $"低延迟 UDP 认证心跳发送异常，已回退 TCP：{ex.Message}");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(datagram);
        }
    }

    private void CompleteShortGopRecoverySerialization(
        long recoveryGeneration,
        bool completed)
    {
        PendingFrame? orphanedDependent = null;
        lock (_shortGopChainLock)
        {
            if (_serializingShortGopRecoveryGeneration ==
                recoveryGeneration)
            {
                Volatile.Write(
                    ref _serializingShortGopRecoveryGeneration,
                    0);
            }

            if (completed &&
                _latestShortGopRecoveryGeneration ==
                    recoveryGeneration)
            {
                Volatile.Write(
                    ref _serializedShortGopRecoveryGeneration,
                    recoveryGeneration);
            }
            else if (!completed &&
                Volatile.Read(ref _pendingFrame) is
                {
                    IsShortGopVideo: true,
                    IsIndependentlyRecoverable: false
                } pending &&
                pending.RecoveryGeneration == recoveryGeneration)
            {
                PendingFrame? removed = Interlocked.CompareExchange(
                    ref _pendingFrame,
                    null,
                    pending);
                if (ReferenceEquals(removed, pending))
                {
                    orphanedDependent = pending;
                }
            }
        }

        if (orphanedDependent is not null)
        {
            ReturnFrame(orphanedDependent);
        }
    }

    private bool SendFrame(
        Socket socket,
        PendingFrame frame,
        WindowsHighResolutionPacingWaiter pacingWaiter,
        CancellationToken cancellationToken)
    {
        IPEndPoint? endpoint = Volatile.Read(ref _boundPeerEndpoint);
        if (endpoint is null)
        {
            return false;
        }

        byte[] framePayload = frame.FrameBuffer;
        int frameLength = frame.FrameLength;
        LowLatencyVideoCongestionController? congestionController =
            _congestionController;
        if (congestionController is not null &&
            frameLength > congestionController.GetFrameBudgetBytes())
        {
            if (frame.AllowLatencyBudgetDrop)
            {
                congestionController.RecordOversizedFrameDrop();
            }
            else
            {
                DisableRoute(
                    "排队画面已超过更新后的 UDP 延迟预算，已回退 TCP。");
            }

            return false;
        }

        int maxPayload = LowLatencyVideoProtocol.GetMaxFragmentPayloadBytes(
            _offer!.MaxDatagramBytes);
        int fragmentCount = LowLatencyVideoProtocol.GetFrameFragmentCount(
            frameLength,
            _offer.MaxDatagramBytes);

        ulong frameSequence = checked((ulong)Interlocked.Increment(ref _nextFrameSequence));
        byte[] datagramBuffer = ArrayPool<byte>.Shared.Rent(_offer.MaxDatagramBytes);
        bool useXorFec =
            congestionController?.XorFecEnabled == true &&
            _features.HasFlag(LowLatencyVideoFeatures.XorFec) &&
            LowLatencyVideoProtocol.ShouldSendXorFec(fragmentCount);
        byte[]? parityBuffer = useXorFec
            ? ArrayPool<byte>.Shared.Rent(maxPayload)
            : null;
        bool allowLargeIndependentVideoBurst =
            frame.FrameKind == MessageType.VideoFrame &&
            frame.IsIndependentlyRecoverable;
        bool peerSocketConnected =
            Volatile.Read(ref _peerSocketConnected) != 0;
        TimeSpan controllerFrameSerializationBudget =
            congestionController?
                .GetFrameSerializationBudget() ??
            LowLatencyVideoCongestionController
                .FrameSerializationBudget;
        // A short-GOP IDR is the only recovery point for the dependent
        // frames that follow it. The 60 fps controller budget can be shorter
        // than the serialization time of an IDR that was still admitted by
        // MinimumFrameBudgetBytes under congestion. Keep the legacy hard
        // ceiling for that recovery point; dependent frames still use the
        // tighter controller budget.
        TimeSpan frameSerializationBudget =
            frame.IsShortGopVideo &&
            frame.IsIndependentlyRecoverable
                ? LowLatencyVideoCongestionController
                    .FrameSerializationBudget
                : controllerFrameSerializationBudget;

        long frameSendStartedAt = Stopwatch.GetTimestamp();
        long wireBytesSent = 0;
        int packetsSincePacing = 0;
        try
        {
            int groupCount = LowLatencyVideoProtocol.GetXorFecGroupCount(
                fragmentCount);
            for (int groupIndex = 0; groupIndex < groupCount; groupIndex++)
            {
                int firstFragment = checked(
                    groupIndex *
                    LowLatencyVideoProtocol.XorFecDataFragmentsPerGroup);
                int endFragment = Math.Min(
                    fragmentCount,
                    firstFragment +
                        LowLatencyVideoProtocol.XorFecDataFragmentsPerGroup);
                for (int fragmentIndex = firstFragment;
                    fragmentIndex < endFragment;
                    fragmentIndex++)
                {
                    if (!IsRouteActive ||
                        ShouldAbandonCurrentFrame(
                            frameSendStartedAt,
                            frame.IsIndependentlyRecoverable,
                            congestionController,
                            frame.IsGop1Video,
                            frameSerializationBudget,
                            frame.Gop1StreamSignature))
                    {
                        return false;
                    }

                    int offset = checked(fragmentIndex * maxPayload);
                    int length = Math.Min(maxPayload, frameLength - offset);
                    int datagramLength = _sendCipher!.EncryptInto(
                        datagramBuffer,
                        LowLatencyVideoDatagramKind.FrameFragment,
                        frameSequence,
                        frameLength,
                        offset,
                        checked((ushort)fragmentIndex),
                        checked((ushort)fragmentCount),
                        frame.FrameKind,
                        0,
                        framePayload.AsSpan(offset, length));
                    cancellationToken.ThrowIfCancellationRequested();
                    int sent = peerSocketConnected
                        ? socket.Send(
                            datagramBuffer.AsSpan(
                                0,
                                datagramLength),
                            SocketFlags.None)
                        : socket.SendTo(
                            datagramBuffer.AsSpan(
                                0,
                                datagramLength),
                            SocketFlags.None,
                            endpoint);
                    if (sent != datagramLength)
                    {
                        throw new IOException(
                            "UDP frame fragment was only partially sent.");
                    }

                    wireBytesSent += datagramLength;
                    packetsSincePacing++;
                    if (packetsSincePacing >= PacingBurstPackets)
                    {
                        PaceFrame(
                            frameSendStartedAt,
                            wireBytesSent,
                            congestionController,
                            frame.FrameLength,
                            allowLargeIndependentVideoBurst,
                            frame.IsShortGopVideo,
                            pacingWaiter,
                            cancellationToken);
                        packetsSincePacing = 0;
                    }
                }

                if (!useXorFec)
                {
                    continue;
                }

                if (!IsRouteActive ||
                    ShouldAbandonCurrentFrame(
                        frameSendStartedAt,
                        frame.IsIndependentlyRecoverable,
                        congestionController,
                        frame.IsGop1Video,
                        frameSerializationBudget,
                        frame.Gop1StreamSignature))
                {
                    return false;
                }

                int parityLength =
                    LowLatencyVideoProtocol.WriteXorFecParityPayload(
                        framePayload.AsSpan(0, frameLength),
                        groupIndex,
                        _offer.MaxDatagramBytes,
                        parityBuffer!);
                int parityOffset = checked(firstFragment * maxPayload);
                int parityDatagramLength = _sendCipher!.EncryptInto(
                    datagramBuffer,
                    LowLatencyVideoDatagramKind.FrameXorParity,
                    frameSequence,
                    frameLength,
                    parityOffset,
                    checked((ushort)groupIndex),
                    checked((ushort)fragmentCount),
                    frame.FrameKind,
                    0,
                    parityBuffer.AsSpan(0, parityLength));
                cancellationToken.ThrowIfCancellationRequested();
                int paritySent = peerSocketConnected
                    ? socket.Send(
                        datagramBuffer.AsSpan(
                            0,
                            parityDatagramLength),
                        SocketFlags.None)
                    : socket.SendTo(
                        datagramBuffer.AsSpan(
                            0,
                            parityDatagramLength),
                        SocketFlags.None,
                        endpoint);
                if (paritySent != parityDatagramLength)
                {
                    throw new IOException(
                        "UDP FEC datagram was only partially sent.");
                }

                wireBytesSent += parityDatagramLength;
                packetsSincePacing++;
                if (packetsSincePacing >= PacingBurstPackets)
                {
                    PaceFrame(
                        frameSendStartedAt,
                        wireBytesSent,
                        congestionController,
                        frame.FrameLength,
                        allowLargeIndependentVideoBurst,
                        frame.IsShortGopVideo,
                        pacingWaiter,
                        cancellationToken);
                    packetsSincePacing = 0;
                }
            }

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(datagramBuffer, clearArray: true);
            if (parityBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(
                    parityBuffer,
                    clearArray: true);
            }
        }
    }

    internal bool ShouldAbandonCurrentFrame(
        long frameSendStartedAt,
        bool currentFrameIsIndependentlyRecoverable,
        LowLatencyVideoCongestionController? congestionController,
        bool currentFrameIsGop1Video = false,
        TimeSpan? frameSerializationBudget = null,
        long currentGop1StreamSignature = 0)
    {
        long now = Stopwatch.GetTimestamp();
        TimeSpan currentFrameAge =
            Stopwatch.GetElapsedTime(
                frameSendStartedAt,
                now);
        TimeSpan serializationBudget =
            frameSerializationBudget ??
            congestionController?.GetFrameSerializationBudget() ??
            LowLatencyVideoCongestionController
                .FrameSerializationBudget;
        TimeSpan gop1PreemptionMinimumAge =
            CalculateGop1PreemptionMinimumAge(
                Volatile.Read(
                    ref _configuredVideoFramesPerSecond),
                serializationBudget);
        bool hasRecentSerializedGop1Recovery =
            HasRecentSerializedGop1Recovery(
                currentGop1StreamSignature,
                Volatile.Read(
                    ref _serializedGop1StreamSignature),
                Volatile.Read(
                    ref _lastSerializedGop1RecoveryAt),
                now);
        PendingFrame? pendingFrame =
            Volatile.Read(ref _pendingFrame);
        if (ShouldPreemptForNewerRecovery(
                currentFrameIsIndependentlyRecoverable,
                currentFrameIsGop1Video,
                pendingFrame?.IsIndependentlyRecoverable == true,
                pendingFrame?.IsGop1Video == true,
                hasRecentSerializedGop1Recovery,
                currentFrameAge >=
                    gop1PreemptionMinimumAge))
        {
            congestionController?.RecordAbortedFrameSend();
            return true;
        }

        // Give the first GOP1 recovery point, the first point after a stream
        // size change, and a periodic recovery after sustained replacement the
        // legacy hard ceiling instead of the tighter adaptive budget. This
        // guarantees that repeated latest-frame preemption cannot permanently
        // reduce the wire to prefixes that the viewer cannot decode.
        if (currentFrameIsGop1Video &&
            !hasRecentSerializedGop1Recovery &&
            currentFrameAge <
                LowLatencyVideoCongestionController
                    .FrameSerializationBudget)
        {
            return false;
        }

        if (currentFrameAge < serializationBudget)
        {
            return false;
        }

        congestionController?.RecordAbortedFrameSend();
        return true;
    }

    internal static bool ShouldPreemptForNewerRecovery(
        bool currentFrameIsIndependentlyRecoverable,
        bool currentFrameIsGop1Video,
        bool pendingFrameIsIndependentlyRecoverable,
        bool pendingFrameIsGop1Video,
        bool hasRecentSerializedGop1Recovery,
        bool currentFrameReachedPreemptionAge)
    {
        if (!currentFrameIsIndependentlyRecoverable)
        {
            return pendingFrameIsIndependentlyRecoverable;
        }

        // A GOP1 stream can safely replace an IDR that is already in flight,
        // but only after the viewer has received one complete GOP1 recovery
        // point. Protecting that first access unit prevents sustained capture
        // from replacing every prefix before any frame can be decoded.
        return currentFrameIsGop1Video &&
            pendingFrameIsGop1Video &&
            hasRecentSerializedGop1Recovery &&
            currentFrameReachedPreemptionAge;
    }

    internal static TimeSpan CalculateGop1PreemptionMinimumAge(
        int configuredFramesPerSecond,
        TimeSpan serializationBudget)
    {
        if (serializationBudget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(serializationBudget));
        }

        return configuredFramesPerSecond > 0
            ? TimeSpan.FromSeconds(
                1d / configuredFramesPerSecond)
            : serializationBudget;
    }

    internal static long CreateGop1StreamSignature(
        int width,
        int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        return checked(
            ((long)(uint)width << 32) |
            (uint)height);
    }

    internal static bool HasRecentSerializedGop1Recovery(
        long currentStreamSignature,
        long serializedStreamSignature,
        long serializedAtTimestamp,
        long nowTimestamp)
    {
        return currentStreamSignature != 0 &&
            currentStreamSignature ==
                serializedStreamSignature &&
            serializedAtTimestamp > 0 &&
            nowTimestamp >= serializedAtTimestamp &&
            Stopwatch.GetElapsedTime(
                serializedAtTimestamp,
                nowTimestamp) <
                LowLatencyVideoCongestionController
                    .FrameSerializationBudget;
    }

    private static void PaceFrame(
        long frameSendStartedAt,
        long wireBytesSent,
        LowLatencyVideoCongestionController? congestionController,
        int frameBytes,
        bool allowLargeIndependentVideoBurst,
        bool allowShortGopLanBurst,
        WindowsHighResolutionPacingWaiter pacingWaiter,
        CancellationToken cancellationToken)
    {
        if (congestionController is null)
        {
            return;
        }

        long targetBitsPerSecond =
            congestionController.GetFramePacingTargetBitsPerSecond(
                frameBytes,
                allowLargeIndependentVideoBurst);
        if (allowShortGopLanBurst &&
            LowLatencyVideoPacer
                .ShouldBypassDelayForShortGopLanBurst(
                    wireBytesSent,
                    targetBitsPerSecond))
        {
            return;
        }

        TimeSpan delay = LowLatencyVideoPacer.CalculateDelay(
            Stopwatch.GetElapsedTime(frameSendStartedAt),
            wireBytesSent,
            targetBitsPerSecond);
        if (delay > TimeSpan.Zero)
        {
            pacingWaiter.Wait(
                delay,
                cancellationToken);
        }
    }

    private void SignalSender()
    {
        try
        {
            if (_sendSignal.CurrentCount == 0)
            {
                _sendSignal.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private void StartFallbackBarrier(byte reason)
    {
        LowLatencyVideoOffer? offer = _offer;
        if (offer is null || _sendFallbackBarrier is null)
        {
            return;
        }

        lock (_fallbackTaskLock)
        {
            _fallbackTask ??= SendFallbackBarrierAsync(
                offer.ChannelId,
                offer.Epoch,
                reason);
        }
    }

    private void MonitorSetupTimeout()
    {
        CancellationToken cancellationToken =
            _transportCancellation?.Token ??
            _connectionCancellationToken;
        // Timed waits may wake a fraction before their requested deadline.
        // Recompute both setup stages after every wake instead of treating an
        // early timeout as completion; otherwise state 1 can be orphaned with
        // its UDP socket and offer secrets still live forever.
        while (!cancellationToken.IsCancellationRequested)
        {
            Task setupSignal;
            TimeSpan setupRemaining;
            lock (_setupStateLock)
            {
                int state = Volatile.Read(ref _state);
                long firstValidProbeAt =
                    Volatile.Read(ref _firstValidProbeAt);
                if (state is 3 or 4)
                {
                    return;
                }

                if (firstValidProbeAt == 0)
                {
                    long offeredAt =
                        Volatile.Read(ref _offeredAt);
                    if (state != 1 || offeredAt == 0)
                    {
                        return;
                    }

                    setupRemaining = ProbeTimeout -
                        Stopwatch.GetElapsedTime(offeredAt);
                    if (setupRemaining <= TimeSpan.Zero)
                    {
                        DisableRoute(
                            "低延迟 UDP 画面握手超时，继续使用 TCP；" +
                            FormatHandshakeDiagnostics(
                                state,
                                "等待合法 Probe"),
                            notifyViewer: true,
                            reason: 3);
                        return;
                    }

                    setupSignal =
                        _validProbeReceivedSignal.Task;
                }
                else
                {
                    if (state != 2)
                    {
                        return;
                    }

                    // An authenticated pre-Ready repin refreshes this
                    // timestamp. The next loop observes the new deadline.
                    setupRemaining = ReadyTimeout -
                        Stopwatch.GetElapsedTime(
                            firstValidProbeAt);
                    if (setupRemaining <= TimeSpan.Zero)
                    {
                        DisableRoute(
                            "低延迟 UDP 画面握手超时，继续使用 TCP；" +
                            FormatHandshakeDiagnostics(
                                state,
                                "等待 TCP Ready"),
                            notifyViewer: true,
                            reason: 3);
                        return;
                    }

                    setupSignal =
                        _readyReceivedSignal.Task;
                }
            }

            try
            {
                setupSignal.Wait(
                    setupRemaining,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private string FormatHandshakeDiagnostics(
        int state,
        string phase)
    {
        LowLatencyVideoHostHandshakeSnapshot snapshot =
            CollectHandshakeSnapshot();
        return
            $"建链统计：state={state}，阶段={phase}，" +
            $"原始包={snapshot.RawPacketCount}，" +
            $"地址拒绝={snapshot.AddressRejectedCount}，" +
            $"解密失败={snapshot.DecryptFailureCount}，" +
            $"合法Probe={snapshot.ValidProbeCount}，" +
            $"Ack成功={snapshot.AckSentCount}。";
    }

    private Task BeginIoShutdown()
    {
        lock (_ioShutdownLock)
        {
            if (_ioShutdownTask is not null)
            {
                return _ioShutdownTask;
            }

            if (_transportCancellation is null)
            {
                return Task.CompletedTask;
            }

            return _ioShutdownTask =
                LowLatencyDedicatedThread.Start(
                    "RemoteDesk UDP host shutdown",
                    ShutdownIoCore);
        }
    }

    private void ShutdownIoCore()
    {
        CancellationTokenSource? cancellation = _transportCancellation;
        Interlocked.Exchange(
                ref _qwaveVideoFlow,
                null)
            ?.Dispose();
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _socket?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        SignalSender();
        try
        {
            _mouseInputAckSignal.Release();
        }
        catch (Exception ex) when (
            ex is SemaphoreFullException or
                ObjectDisposedException)
        {
        }

        Task[] ioTasks = new[]
            {
                _receiveTask,
                _sendTask,
                _heartbeatTask,
                _mouseInputAckTask
            }
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (ioTasks.Length > 0)
        {
            foreach (Task ioTask in ioTasks)
            {
                try
                {
                    ioTask.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    if (!IsStopException(ex))
                    {
                        _log($"低延迟 UDP I/O 清理异常：{ex.Message}");
                    }
                }
            }
        }

        PendingFrame? pendingFrame = Interlocked.Exchange(
            ref _pendingFrame,
            null);
        if (pendingFrame is not null)
        {
            ReturnFrame(pendingFrame);
        }

        ClearOfferSecrets();
        if (_offer is { } offer)
        {
            CryptographicOperations.ZeroMemory(offer.Challenge);
        }

        _sendCipher?.Dispose();
        _receiveCipher?.Dispose();
        cancellation?.Dispose();
    }

    private async Task SendFallbackBarrierAsync(
        ulong channelId,
        uint epoch,
        byte reason)
    {
        try
        {
            await _sendFallbackBarrier!(channelId, epoch, reason).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or
                ObjectDisposedException or
                IOException or
                SocketException or
                CryptographicException)
        {
        }
    }

    private static ulong CreateNonZeroUInt64()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        ulong value;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            value = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        }
        while (value == 0);

        return value;
    }

    private static uint CreateNonZeroUInt32()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        uint value;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }
        while (value == 0);

        return value;
    }

    private static void ConfigureSocket(Socket socket)
    {
        // The host normally reuses the accepted TCP port number for UDP.
        // Prevent another local socket from sharing that endpoint and
        // intercepting a reconnect's authenticated probes.
        socket.ExclusiveAddressUse = true;
        socket.ReceiveBufferSize = 256 * 1024;
        socket.SendBufferSize = 1024 * 1024;
        if (socket.AddressFamily == AddressFamily.InterNetworkV6)
        {
            socket.DualMode = false;
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                socket.IOControl(
                    unchecked((IOControlCode)(-1744830452)), // SIO_UDP_CONNRESET
                    [0, 0, 0, 0],
                    null);
            }
            catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException)
            {
            }
        }
    }

    private static void ConnectPeerSocket(
        Socket socket,
        IPEndPoint endpoint)
    {
        socket.Connect(endpoint);
    }

    private static bool AddressesEqual(IPAddress first, IPAddress second)
    {
        return first.Equals(second) ||
            first.MapToIPv6().Equals(second.MapToIPv6());
    }

    private static bool IsStopException(Exception ex)
    {
        return ex is OperationCanceledException or ObjectDisposedException;
    }

    private static void ReturnFrame(PendingFrame frame)
    {
        CryptographicOperations.ZeroMemory(
            frame.FrameBuffer.AsSpan(0, frame.FrameLength));
        ArrayPool<byte>.Shared.Return(frame.FrameBuffer);
    }

    private readonly record struct PendingHostUdpMouseMove(
        IPEndPoint RemoteEndpoint,
        long Sequence,
        RemoteInputCommand Command);

    private sealed record PendingFrame(
        MessageType FrameKind,
        int Width,
        int Height,
        byte[] FrameBuffer,
        int FrameLength,
        bool AllowLatencyBudgetDrop,
        bool IsIndependentlyRecoverable,
        bool IsShortGopVideo,
        long RecoveryGeneration)
    {
        public bool IsGop1Video =>
            FrameKind == MessageType.VideoFrame &&
            IsIndependentlyRecoverable &&
            !IsShortGopVideo;

        public long Gop1StreamSignature =>
            IsGop1Video
                ? CreateGop1StreamSignature(
                    Width,
                    Height)
                : 0;
    }
}

internal readonly record struct LowLatencyMouseInputLatencySnapshot(
    long SentMouseMoveCount,
    long AcknowledgedMouseMoveCount,
    long MatchedLatencySampleCount,
    ulong LatestSentSequence,
    ulong LatestAcknowledgedSequence,
    double LatestRoundTripMilliseconds,
    double SmoothedRoundTripMilliseconds,
    double P95RoundTripMilliseconds,
    double P99RoundTripMilliseconds,
    double MaximumRoundTripMilliseconds,
    ulong MaximumRoundTripSequence,
    long RoundTripsAbove25Milliseconds,
    long RoundTripsAbove50Milliseconds,
    long RoundTripsAbove100Milliseconds,
    long RoundTripsAbove150Milliseconds);

internal sealed class LowLatencyMouseInputLatencyTracker
{
    private const int HistoryCapacity = 256;
    private const int LatencyHistoryCapacity = 4096;

    private readonly object _lock = new();
    private readonly ulong[] _sentSequences =
        new ulong[HistoryCapacity];
    private readonly long[] _sentTimestamps =
        new long[HistoryCapacity];
    private readonly double[] _latencyHistory =
        new double[LatencyHistoryCapacity];
    private long _sentMouseMoveCount;
    private long _acknowledgedMouseMoveCount;
    private long _matchedLatencySampleCount;
    private ulong _latestSentSequence;
    private ulong _latestAcknowledgedSequence;
    private long _unacknowledgedSinceTimestamp;
    private double _latestRoundTripMilliseconds;
    private double _smoothedRoundTripMilliseconds;
    private double _maximumRoundTripMilliseconds;
    private ulong _maximumRoundTripSequence;
    private long _roundTripsAbove25Milliseconds;
    private long _roundTripsAbove50Milliseconds;
    private long _roundTripsAbove100Milliseconds;
    private long _roundTripsAbove150Milliseconds;
    private int _latencyHistoryCount;
    private int _nextLatencyHistoryIndex;

    public void RecordSent(
        ulong sequence,
        long sentAtTimestamp)
    {
        if (sequence == 0 || sentAtTimestamp <= 0)
        {
            throw new ArgumentOutOfRangeException(
                sequence == 0
                    ? nameof(sequence)
                    : nameof(sentAtTimestamp));
        }

        lock (_lock)
        {
            if (_latestSentSequence <=
                _latestAcknowledgedSequence)
            {
                _unacknowledgedSinceTimestamp =
                    sentAtTimestamp;
            }

            int slot = checked(
                (int)(sequence %
                    (ulong)HistoryCapacity));
            _sentSequences[slot] = sequence;
            _sentTimestamps[slot] = sentAtTimestamp;
            _sentMouseMoveCount++;
            _latestSentSequence = sequence;
        }
    }

    public bool TryRecordAcknowledged(
        ulong sequence,
        long receivedAtTimestamp)
    {
        if (sequence == 0 || receivedAtTimestamp <= 0)
        {
            return false;
        }

        lock (_lock)
        {
            if (sequence >
                    _latestSentSequence ||
                sequence <=
                    _latestAcknowledgedSequence)
            {
                return false;
            }

            _latestAcknowledgedSequence = sequence;
            _acknowledgedMouseMoveCount++;
            _unacknowledgedSinceTimestamp =
                sequence >= _latestSentSequence
                    ? 0
                    : receivedAtTimestamp;
            int slot = checked(
                (int)(sequence %
                    (ulong)HistoryCapacity));
            if (_sentSequences[slot] != sequence ||
                _sentTimestamps[slot] <= 0 ||
                receivedAtTimestamp <
                    _sentTimestamps[slot])
            {
                return true;
            }

            double milliseconds =
                Stopwatch.GetElapsedTime(
                        _sentTimestamps[slot],
                        receivedAtTimestamp)
                    .TotalMilliseconds;
            _sentSequences[slot] = 0;
            _sentTimestamps[slot] = 0;
            _latestRoundTripMilliseconds =
                milliseconds;
            if (milliseconds >
                _maximumRoundTripMilliseconds)
            {
                _maximumRoundTripMilliseconds =
                    milliseconds;
                _maximumRoundTripSequence =
                    sequence;
            }

            if (milliseconds > 25)
            {
                _roundTripsAbove25Milliseconds++;
            }

            if (milliseconds > 50)
            {
                _roundTripsAbove50Milliseconds++;
            }

            if (milliseconds > 100)
            {
                _roundTripsAbove100Milliseconds++;
            }

            if (milliseconds > 150)
            {
                _roundTripsAbove150Milliseconds++;
            }

            _matchedLatencySampleCount++;
            _latencyHistory[_nextLatencyHistoryIndex] =
                milliseconds;
            _nextLatencyHistoryIndex =
                (_nextLatencyHistoryIndex + 1) %
                LatencyHistoryCapacity;
            _latencyHistoryCount = Math.Min(
                LatencyHistoryCapacity,
                _latencyHistoryCount + 1);
            _smoothedRoundTripMilliseconds =
                _matchedLatencySampleCount == 1
                    ? milliseconds
                    : (_smoothedRoundTripMilliseconds *
                            0.875d) +
                        (milliseconds * 0.125d);
            return true;
        }
    }

    public LowLatencyMouseInputLatencySnapshot
        CollectSnapshot()
    {
        lock (_lock)
        {
            double p95 = CalculatePercentileLocked(0.95d);
            double p99 = CalculatePercentileLocked(0.99d);
            return new LowLatencyMouseInputLatencySnapshot(
                _sentMouseMoveCount,
                _acknowledgedMouseMoveCount,
                _matchedLatencySampleCount,
                _latestSentSequence,
                _latestAcknowledgedSequence,
                _latestRoundTripMilliseconds,
                _smoothedRoundTripMilliseconds,
                p95,
                p99,
                _maximumRoundTripMilliseconds,
                _maximumRoundTripSequence,
                _roundTripsAbove25Milliseconds,
                _roundTripsAbove50Milliseconds,
                _roundTripsAbove100Milliseconds,
                _roundTripsAbove150Milliseconds);
        }
    }

    public bool HasAcknowledgementTimedOut(
        long nowTimestamp,
        TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout));
        }

        if (nowTimestamp <= 0)
        {
            return false;
        }

        lock (_lock)
        {
            return _latestSentSequence >
                    _latestAcknowledgedSequence &&
                _unacknowledgedSinceTimestamp > 0 &&
                nowTimestamp >=
                    _unacknowledgedSinceTimestamp &&
                Stopwatch.GetElapsedTime(
                    _unacknowledgedSinceTimestamp,
                    nowTimestamp) >= timeout;
        }
    }

    private double CalculatePercentileLocked(double percentile)
    {
        if (_latencyHistoryCount == 0)
        {
            return 0;
        }

        double[] ordered =
            new double[_latencyHistoryCount];
        Array.Copy(
            _latencyHistory,
            ordered,
            _latencyHistoryCount);
        Array.Sort(ordered);
        int index = Math.Clamp(
            checked(
                (int)Math.Ceiling(
                    percentile * ordered.Length) -
                1),
            0,
            ordered.Length - 1);
        return ordered[index];
    }
}

internal sealed class LowLatencyVideoViewerTransport : IAsyncDisposable
{
    internal static readonly TimeSpan ProbeInterval =
        LowLatencyVideoBindPolicy.ProbeInterval;
    // Two probes cover ordinary LAN jitter while escaping a deterministic
    // source-port black hole after about 400 ms instead of spending the whole
    // three-second bind budget retrying the same unusable 5-tuple.
    internal const int BindProbeAttemptsPerSocket = 2;
    // The same receive priority must survive a handshake source-port rotation.
    // Falling back to AboveNormal on the replacement worker reintroduces
    // two-frame batches whenever the UI or thread pool is busy.
    internal const ThreadPriority ReceiveThreadPriority =
        ThreadPriority.Highest;
    // Latest-only mouse transmission is a short, blocking worker. Match the
    // video receive priority so a saturated 4K60 decode/presentation process
    // cannot leave a newly queued pointer position waiting behind bulk work.
    internal const ThreadPriority MouseInputThreadPriority =
        ThreadPriority.Highest;
    private static readonly TimeSpan ExtendedFeedbackInterval =
        TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan BindTimeout =
        LowLatencyVideoBindPolicy.ViewerTimeout;
    // A first-time machine may still spend up to eighteen seconds discovering
    // native encoders. Proven DDA reconnects use a learned 1.5-2.5 second
    // fast path and switch directly to GDI hardware encoding on failure.
    // This absolute startup deadline adds setup/serialization headroom without
    // weakening the shorter steady-state stall detector below.
    internal static readonly TimeSpan InitialFrameTimeout =
        TimeSpan.FromSeconds(20);
    // The host supports settings down to 1 FPS. Allow more than two minimum-rate frame
    // intervals so a valid low-FPS session does not continuously tear down its UDP route.
    internal static readonly TimeSpan SteadyFrameTimeout =
        LowLatencyVideoFeedbackPolicy.HardTimeout;
    internal static readonly TimeSpan FallbackBarrierTimeout =
        TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan UdpMouseAppliedAckTimeout =
        TimeSpan.FromSeconds(1);

    private readonly LowLatencyVideoOffer _offer;
    private readonly IPAddress _expectedHostAddress;
    private readonly IPAddress _localBindAddress;
    private readonly IPEndPoint _hostEndpoint;
    private readonly object _socketLock = new();
    private IPEndPoint _localEndpoint;
    private Socket _socket;
    private readonly LowLatencyVideoSendCipher _sendCipher;
    private readonly LowLatencyVideoReceiveCipher _receiveCipher;
    private readonly LowLatencyVideoAdjacentFrameReassembler _reassembler;
    private readonly LowLatencyVideoPacketArrivalTracker? _arrivalTracker;
    private readonly LowLatencyMouseInputLatencyTracker
        _mouseInputLatencyTracker = new();
    private readonly LowLatencyVideoErrorLogThrottler
        _transientDatagramSendErrorLog =
            new(TimeSpan.FromSeconds(2));
    private readonly LowLatencyVideoFeatures _features;
    private readonly CancellationTokenSource _transportCancellation;
    private readonly Func<byte[], Task<bool>> _sendTcpControl;
    private readonly Action<RemoteFrame> _publishFrame;
    private readonly Action<
        ulong,
        MessageType,
        ReadOnlyMemory<byte>>
        _publishCompletedFrame;
    private readonly Action<string> _log;
    private readonly Action? _abortConnection;
    private readonly Func<TimeSpan, CancellationToken, Task>
        _fallbackBarrierDelayAsync;
    private readonly Func<CancellationToken, Task>?
        _beforeMouseInputSend;
    private readonly Func<
        Socket,
        IPEndPoint,
        WindowsQwaveTrafficType,
        IDisposable?> _attachQwaveFlow;
    private Task _receiveTask;
    private readonly Task _feedbackTask;
    private readonly Task _mouseInputTask;
    private readonly SemaphoreSlim _mouseInputSignal =
        new(0, 1);
    private readonly object _shutdownLock = new();
    private readonly object _fallbackBarrierDeadlineLock = new();
    private readonly long _startedAt;
    private Task? _shutdownTask;
    private CancellationTokenSource?
        _fallbackBarrierDeadlineCancellation;
    private long _initialFrameWaitStartedAt;
    private long _lastAuthenticatedFramePacketAt;
    private long _lastAuthenticatedHeartbeatAt;
    private long _lastCompleteFrameAt;
    private long _authenticatedFramePacketCount;
    private long _highestAuthenticatedFramePacketSequence;
    private long _highestCompleteFrameSequence;
    private long _lastPublishedShortGopFrameSequence;
    private long _nextMouseInputSequence;
    private long _bindProbeSendSuccessCount;
    private long _handshakeRawPacketCount;
    private long _handshakeAddressRejectedCount;
    private long _handshakeDecryptFailureCount;
    private long _handshakeValidAckCount;
    private IDisposable? _qwaveControlFlow;
    private PendingUdpMouseMove? _pendingMouseMove;
    private int _hasCompleteFrame;
    private int _pendingFallbackReason;
    private int _fatalRouteFailure;
    private int _connectionAbortRequested;
    private int _udpMouseInputDisabled;
    private bool _waitingForShortGopRecovery = true;
    private int _activeSocketGeneration = 1;
    private int _state = 1;

    public LowLatencyVideoViewerTransport(
        LowLatencyVideoOffer offer,
        IPAddress expectedHostAddress,
        Func<byte[], Task<bool>> sendTcpControl,
        Action<RemoteFrame> publishFrame,
        Action<string> log,
        CancellationToken connectionCancellationToken,
        LowLatencyVideoFeatures features = LowLatencyVideoFeatures.None,
        Func<CancellationToken, Task>? beforeMouseInputSend = null,
        IPAddress? localAddress = null,
        Func<
            Socket,
            IPEndPoint,
            WindowsQwaveTrafficType,
            IDisposable?>? attachQwaveFlow = null,
        Action? abortConnection = null,
        Func<TimeSpan, CancellationToken, Task>?
            fallbackBarrierDelayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(expectedHostAddress);
        ArgumentNullException.ThrowIfNull(sendTcpControl);
        ArgumentNullException.ThrowIfNull(publishFrame);
        ArgumentNullException.ThrowIfNull(log);
        LowLatencyVideoProtocol.ValidateOffer(offer);
        features = LowLatencyVideoFeatureNegotiation.Normalize(features);
        expectedHostAddress =
            LowLatencyVideoSocketSupport.NormalizeTcpPeerAddress(
                expectedHostAddress);
        if (localAddress is not null)
        {
            localAddress =
                LowLatencyVideoSocketSupport.NormalizeTcpPeerAddress(
                    localAddress);
            if (localAddress.AddressFamily !=
                expectedHostAddress.AddressFamily)
            {
                throw new ArgumentException(
                    "The UDP local address family must match the TCP peer address family.",
                    nameof(localAddress));
            }
        }

        IPAddress localBindAddress =
            localAddress ??
            (expectedHostAddress.AddressFamily ==
                AddressFamily.InterNetwork
                    ? IPAddress.Any
                    : IPAddress.IPv6Any);
        _offer = offer;
        _expectedHostAddress = expectedHostAddress;
        _localBindAddress = localBindAddress;
        _hostEndpoint = new IPEndPoint(expectedHostAddress, offer.Port);
        _sendTcpControl = sendTcpControl;
        _publishFrame = publishFrame;
        _publishCompletedFrame =
            PublishCompletedFrame;
        _log = log;
        _abortConnection = abortConnection;
        _fallbackBarrierDelayAsync =
            fallbackBarrierDelayAsync ??
            ((delay, cancellationToken) =>
                Task.Delay(delay, cancellationToken));
        _beforeMouseInputSend = beforeMouseInputSend;
        _attachQwaveFlow =
            attachQwaveFlow ??
            (static (socket, destination, trafficType) =>
                WindowsQwaveVideoFlow.TryAttach(
                    socket,
                    destination,
                    trafficType));
        _features = features;
        _reassembler = new LowLatencyVideoAdjacentFrameReassembler(
            offer.MaxFrameBytes,
            offer.MaxDatagramBytes,
            _publishCompletedFrame,
            enableXorFec:
                features.HasFlag(LowLatencyVideoFeatures.XorFec));
        if (features.HasFlag(
                LowLatencyVideoFeatures.CongestionFeedback))
        {
            _arrivalTracker = new LowLatencyVideoPacketArrivalTracker();
        }

        Socket? socket = null;
        LowLatencyVideoSendCipher? sendCipher = null;
        LowLatencyVideoReceiveCipher? receiveCipher = null;
        CancellationTokenSource? transportCancellation = null;
        IDisposable? qwaveControlFlow = null;
        try
        {
            socket = new Socket(expectedHostAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            ConfigureSocket(socket);
            socket.Bind(new IPEndPoint(
                localBindAddress,
                0));
            // The host endpoint is authenticated by the encrypted offer and
            // remains fixed for this socket generation. Connecting the UDP
            // socket lets the kernel reject other sources and enables the
            // allocation-free Receive(Span) path. ReceiveFrom(Span, ref
            // EndPoint) allocates an endpoint object for every packet on
            // Windows, which is over 7,000 short-lived objects/s at native 4K.
            socket.Connect(_hostEndpoint);
            // This socket only sends bind/feedback and interactive input. Mark
            // it as qWAVE Control rather than A/V so a saturated local uplink
            // cannot queue pointer positions behind bulk traffic.
            qwaveControlFlow = _attachQwaveFlow(
                socket,
                _hostEndpoint,
                WindowsQwaveTrafficType.Control);
            sendCipher = new LowLatencyVideoSendCipher(
                offer.ViewerToHostKey,
                offer.ViewerNoncePrefix,
                offer.ChannelId,
                offer.Epoch);
            receiveCipher = new LowLatencyVideoReceiveCipher(
                offer.HostToViewerKey,
                offer.HostNoncePrefix,
                offer.ChannelId,
                offer.Epoch);
            transportCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                connectionCancellationToken);
        }
        catch (Exception ex)
        {
            if (ex is SocketException socketException)
            {
                _log(
                    "低延迟 UDP 查看端 socket 创建或绑定失败：" +
                    LowLatencyVideoSocketSupport.FormatSocketException(
                        socketException) +
                    $"；本地={LowLatencyVideoSocketSupport.FormatLocalEndpoint(socket)}，" +
                    $"目标={_hostEndpoint}。");
            }

            qwaveControlFlow?.Dispose();
            socket?.Dispose();
            sendCipher?.Dispose();
            receiveCipher?.Dispose();
            transportCancellation?.Dispose();
            throw;
        }

        _socket = socket;
        _localEndpoint =
            (IPEndPoint)_socket.LocalEndPoint!;
        _sendCipher = sendCipher;
        _receiveCipher = receiveCipher;
        _transportCancellation = transportCancellation;
        _qwaveControlFlow = qwaveControlFlow;
        _startedAt = Stopwatch.GetTimestamp();
        Socket initialSocket = _socket;
        int initialSocketGeneration = _activeSocketGeneration;
        // A delayed receive worker makes already-buffered UDP frames arrive
        // in a visible two-frame batch. Keep this blocking, dedicated worker
        // above UI/thread-pool work so mouse motion and video remain current.
        _receiveTask = LowLatencyDedicatedThread.Start(
            "RemoteDesk UDP viewer receiver",
            () => ReceiveLoop(
                initialSocket,
                initialSocketGeneration,
                _transportCancellation.Token),
            ReceiveThreadPriority);
        _feedbackTask = FeedbackLoopAsync(_transportCancellation.Token);
        _mouseInputTask = LowLatencyDedicatedThread.Start(
            "RemoteDesk UDP mouse sender",
            () => SendMouseInputLoop(
                _transportCancellation.Token),
            MouseInputThreadPriority);
    }

    public bool ShouldIgnoreTcpFrames =>
        Volatile.Read(ref _state) is 2 or 3;

    internal bool IsShutdownCompleted =>
        Volatile.Read(ref _shutdownTask)?.IsCompleted == true;

    internal IPEndPoint LocalEndpoint =>
        Volatile.Read(ref _localEndpoint);

    internal long BindProbeSendSuccessCount =>
        Volatile.Read(ref _bindProbeSendSuccessCount);

    internal LowLatencyVideoViewerHandshakeSnapshot
        CollectHandshakeSnapshot() =>
            new(
                Volatile.Read(ref _handshakeRawPacketCount),
                Volatile.Read(ref _handshakeAddressRejectedCount),
                Volatile.Read(ref _handshakeDecryptFailureCount),
                Volatile.Read(ref _handshakeValidAckCount));

    internal Task RequestFallbackForTestsAsync(byte reason) =>
        RequestFallbackAsync(reason);

    public bool Matches(ulong channelId, uint epoch)
    {
        return _offer.ChannelId == channelId && _offer.Epoch == epoch;
    }

    internal LowLatencyMouseInputLatencySnapshot
        CollectMouseInputLatencySnapshot() =>
            _mouseInputLatencyTracker.CollectSnapshot();

    public bool TryQueueMouseMove(
        RemoteInputCommand command)
    {
        if (command.Kind != RemoteInputKind.MouseMove ||
            !_features.HasFlag(
                LowLatencyVideoFeatures.UdpMouseInput) ||
            Volatile.Read(ref _state) is not (2 or 5) ||
            Volatile.Read(ref _fatalRouteFailure) != 0 ||
            _mouseInputTask.IsCompleted)
        {
            return false;
        }

        if (!CanUseUdpMouseInputNow())
        {
            return false;
        }

        var pending = new PendingUdpMouseMove(
            command,
            Stopwatch.GetTimestamp());
        PendingUdpMouseMove? previous =
            Interlocked.Exchange(
                ref _pendingMouseMove,
                pending);
        if (previous is null)
        {
            try
            {
                _mouseInputSignal.Release();
            }
            catch (Exception ex) when (
                ex is SemaphoreFullException or
                    ObjectDisposedException)
            {
                Interlocked.CompareExchange(
                    ref _pendingMouseMove,
                    null,
                    pending);
                return false;
            }
        }

        if (Volatile.Read(ref _state) is not (2 or 5) ||
            Volatile.Read(ref _fatalRouteFailure) != 0 ||
            _mouseInputTask.IsCompleted ||
            !CanUseUdpMouseInputNow())
        {
            Interlocked.CompareExchange(
                ref _pendingMouseMove,
                null,
                pending);
            return false;
        }

        return true;
    }

    private bool CanUseUdpMouseInputNow()
    {
        if (Volatile.Read(
                ref _udpMouseInputDisabled) != 0)
        {
            return false;
        }

        if (_features.HasFlag(
                LowLatencyVideoFeatures
                    .UdpMouseInputAppliedAck) &&
            _mouseInputLatencyTracker
                .HasAcknowledgementTimedOut(
                    Stopwatch.GetTimestamp(),
                    UdpMouseAppliedAckTimeout))
        {
            if (Interlocked.CompareExchange(
                    ref _udpMouseInputDisabled,
                    1,
                    0) == 0)
            {
                DiscardPendingMouseMove();
                _log(
                    "连续 1 秒未收到鼠标移动落地回执，" +
                    "鼠标移动已改走可靠 TCP；UDP 画面保持连接。");
            }

            return false;
        }

        int state = Volatile.Read(ref _state);
        if (state == 2)
        {
            return true;
        }

        if (state != 5)
        {
            return false;
        }

        if (!IsAuthenticatedHeartbeatFresh())
        {
            if (Interlocked.CompareExchange(
                    ref _fatalRouteFailure,
                    1,
                    0) == 0)
            {
                Volatile.Write(ref _pendingFallbackReason, 1);
                _log(
                    "低延迟 UDP 心跳已过期，鼠标移动立即回退 TCP，" +
                    "避免画面仍在但输入丢失。");
                _ = RequestFallbackAsync(1);
            }

            return false;
        }

        return true;
    }

    private bool IsAuthenticatedHeartbeatFresh()
    {
        if (!_features.HasFlag(
                LowLatencyVideoFeatures
                    .AuthenticatedHeartbeat))
        {
            return false;
        }

        long lastAuthenticatedHeartbeatAt =
            Volatile.Read(ref _lastAuthenticatedHeartbeatAt);
        return lastAuthenticatedHeartbeatAt != 0 &&
            Stopwatch.GetElapsedTime(
                lastAuthenticatedHeartbeatAt) <
            SteadyFrameTimeout;
    }

    public void DiscardPendingMouseMove()
    {
        Interlocked.Exchange(
            ref _pendingMouseMove,
            null);
    }

    public void AcknowledgeStopped(
        ulong channelId,
        uint epoch,
        byte reason = 0,
        bool allowAuthenticatedHostInitiatedPreserve = false)
    {
        if (!Matches(channelId, epoch))
        {
            return;
        }

        CancelFallbackBarrierDeadline();

        int currentState = Volatile.Read(ref _state);
        bool localPreserveBarrier =
            currentState == 3 &&
            Volatile.Read(ref _pendingFallbackReason) ==
                LowLatencyVideoFallbackReasons
                    .PreserveUdpInput;
        bool authenticatedHostPreserveBarrier =
            allowAuthenticatedHostInitiatedPreserve &&
            currentState == 2;
        if (reason ==
                LowLatencyVideoFallbackReasons
                    .PreserveUdpInput &&
            (localPreserveBarrier ||
                authenticatedHostPreserveBarrier) &&
            _features.HasFlag(
                LowLatencyVideoFeatures
                    .AuthenticatedHeartbeat) &&
            _features.HasFlag(
                LowLatencyVideoFeatures.UdpMouseInput) &&
            Volatile.Read(ref _fatalRouteFailure) == 0 &&
            !_mouseInputTask.IsCompleted &&
            Interlocked.CompareExchange(
                ref _state,
                5,
                currentState) == currentState)
        {
            if (Volatile.Read(ref _fatalRouteFailure) != 0 ||
                _mouseInputTask.IsCompleted)
            {
                // A UDP worker failed between the eligibility check and the
                // state transition.  Immediately leave state 5 again; a dead
                // mouse worker must never be advertised as an active route.
                Volatile.Write(ref _pendingFallbackReason, 1);
                _ = RequestFallbackAsync(1);
                return;
            }

            // The TCP Stopped acknowledgement proves both peers agreed to keep
            // the authenticated UDP input route, but the first post-barrier
            // heartbeat may not have arrived yet. Use the promotion time as a
            // short initial lease; real heartbeats must refresh it before the
            // normal hard deadline or mouse motion falls back to TCP.
            Volatile.Write(
                ref _lastAuthenticatedHeartbeatAt,
                Stopwatch.GetTimestamp());
            Volatile.Write(ref _pendingFallbackReason, 0);
            _log(
                "低延迟 UDP 视频已回退 TCP；" +
                "UDP 心跳与鼠标输入继续保持。");
            return;
        }

        int previous = Interlocked.Exchange(ref _state, 4);
        Volatile.Write(ref _pendingFallbackReason, 0);
        if (previous is 1 or 2 or 3 or 5)
        {
            TryCancelTransport();
            if (previous is 2 or 3 or 5)
            {
                _log("低延迟 UDP 画面已回退 TCP。");
            }

            _ = BeginShutdown();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await BeginShutdown().ConfigureAwait(false);
    }

    private void ReceiveLoop(
        Socket socket,
        int socketGeneration,
        CancellationToken cancellationToken)
    {
        byte[] receiveBuffer = new byte[ushort.MaxValue];
        byte[] plaintextBuffer = ArrayPool<byte>.Shared.Rent(
            _offer.MaxDatagramBytes);
        try
        {
            using CancellationTokenRegistration cancellationRegistration =
                cancellationToken.Register(socket.Dispose);
            while (!cancellationToken.IsCancellationRequested)
            {
                int receivedBytes = socket.Receive(
                    receiveBuffer.AsSpan(),
                    SocketFlags.None);
                bool setupPending =
                    Volatile.Read(ref _state) == 1;
                if (setupPending)
                {
                    Interlocked.Increment(
                        ref _handshakeRawPacketCount);
                }

                if (receivedBytes > _offer.MaxDatagramBytes ||
                    !_receiveCipher.TryDecrypt(
                        receiveBuffer.AsSpan(0, receivedBytes),
                        plaintextBuffer,
                        out LowLatencyVideoDatagram packet))
                {
                    if (setupPending)
                    {
                        Interlocked.Increment(
                            ref _handshakeDecryptFailureCount);
                    }

                    continue;
                }

                try
                {
                    // The authenticated packet sequence is shared by every
                    // host-to-viewer datagram kind. Track all accepted packets,
                    // including BindAck, so a late handshake acknowledgement
                    // cannot look like video loss to congestion control.
                    _arrivalTracker?.Observe(
                        packet.PacketSequence,
                        receivedBytes);
                    if (packet.Kind == LowLatencyVideoDatagramKind.BindAck)
                    {
                        HandleBindAckAsync(
                                packet,
                                socket,
                                socketGeneration)
                            .GetAwaiter()
                            .GetResult();
                    }
                    else if (packet.Kind ==
                        LowLatencyVideoDatagramKind
                            .MouseMoveAppliedAck)
                    {
                        HandleMouseMoveAppliedAck(packet);
                    }
                    else if (packet.Kind ==
                            LowLatencyVideoDatagramKind.Heartbeat &&
                        _features.HasFlag(
                            LowLatencyVideoFeatures
                                .AuthenticatedHeartbeat))
                    {
                        HandleHeartbeat(packet);
                    }
                    else if (packet.Kind ==
                            LowLatencyVideoDatagramKind.FrameFragment ||
                        (packet.Kind ==
                            LowLatencyVideoDatagramKind.FrameXorParity &&
                            _features.HasFlag(
                                LowLatencyVideoFeatures.XorFec)))
                    {
                        Volatile.Write(
                            ref _lastAuthenticatedFramePacketAt,
                            Stopwatch.GetTimestamp());
                        Interlocked.Increment(
                            ref _authenticatedFramePacketCount);
                        if (packet.FrameSequence <=
                            (ulong)long.MaxValue)
                        {
                            UpdateMaximum(
                                ref _highestAuthenticatedFramePacketSequence,
                                checked((long)packet.FrameSequence));
                        }
                        HandleFramePacket(packet);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(
                        packet.Plaintext.Span);
                }
            }
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested &&
                IsCurrentSocketGeneration(
                    socket,
                    socketGeneration))
            {
                MarkFatalRouteFailure();
                int state = Volatile.Read(ref _state);
                if (state is 2 or 5)
                {
                    RequestFallbackAsync(1)
                        .GetAwaiter()
                        .GetResult();
                }
                else if (state == 3)
                {
                    // The already-in-flight Stop is now forced to a complete
                    // UDP teardown by the fatal marker and by the host's
                    // actual-state acknowledgement.
                }
                else if (Interlocked.CompareExchange(ref _state, 4, 1) == 1)
                {
                    _ = BeginShutdown();
                }

                _log($"低延迟 UDP 接收异常：{ex.Message}");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(receiveBuffer);
            ArrayPool<byte>.Shared.Return(
                plaintextBuffer,
                clearArray: true);
        }
    }

    private void HandleMouseMoveAppliedAck(
        LowLatencyVideoDatagram packet)
    {
        if (!_features.HasFlag(
                LowLatencyVideoFeatures
                    .UdpMouseInputAppliedAck) ||
            Volatile.Read(ref _state) is not (2 or 3 or 5) ||
            packet.FrameSequence == 0 ||
            packet.FrameLength != 0 ||
            packet.FragmentOffset != 0 ||
            packet.FragmentIndex != 0 ||
            packet.FragmentCount != 1 ||
            packet.FrameKind != MessageType.Input ||
            packet.Flags != 0 ||
            packet.Plaintext.Length != 0)
        {
            return;
        }

        _mouseInputLatencyTracker
            .TryRecordAcknowledged(
                packet.FrameSequence,
                Stopwatch.GetTimestamp());
    }

    private void HandleHeartbeat(
        LowLatencyVideoDatagram packet)
    {
        if (Volatile.Read(ref _state) is not (2 or 3 or 5) ||
            packet.FrameSequence != 0 ||
            packet.FrameLength != 0 ||
            packet.FragmentOffset != 0 ||
            packet.FragmentIndex != 0 ||
            packet.FragmentCount != 1 ||
            packet.FrameKind != 0 ||
            packet.Flags != 0 ||
            packet.Plaintext.Length != 0)
        {
            return;
        }

        Volatile.Write(
            ref _lastAuthenticatedHeartbeatAt,
            Stopwatch.GetTimestamp());
    }

    private async Task HandleBindAckAsync(
        LowLatencyVideoDatagram packet,
        Socket socket,
        int socketGeneration)
    {
        if (packet.FrameSequence != 0 ||
            packet.FrameLength != _offer.Challenge.Length ||
            packet.FragmentOffset != 0 ||
            packet.FragmentIndex != 0 ||
            packet.FragmentCount != 1 ||
            packet.FrameKind != 0 ||
            packet.Flags != 0 ||
            !CryptographicOperations.FixedTimeEquals(
                packet.Plaintext.Span,
                _offer.Challenge))
        {
            return;
        }

        lock (_socketLock)
        {
            if (socketGeneration !=
                    _activeSocketGeneration ||
                !ReferenceEquals(socket, _socket) ||
                Interlocked.CompareExchange(
                    ref _state,
                    2,
                    1) != 1)
            {
                return;
            }

            Interlocked.Increment(
                ref _handshakeValidAckCount);
        }

        byte[] ready = RemoteMessageCodec.EncodeLowLatencyVideoReady(
            _offer.ChannelId,
            _offer.Epoch);
        if (!await _sendTcpControl(ready))
        {
            ShutdownAfterControlSendFailure(
                expectedState: 2,
                "低延迟 UDP Ready 发送失败，继续使用 TCP。");
            return;
        }

        // Start the cold-encoder allowance only after the Ready control write
        // succeeds. A congested TCP control path must not consume the host's
        // hardware-probe and first-frame budget before it can start UDP video.
        Volatile.Write(
            ref _initialFrameWaitStartedAt,
            Stopwatch.GetTimestamp());
        _log(
            "低延迟 UDP 画面握手完成，正在切换画面通道；" +
            FormatHandshakeDiagnostics(
                state: 2,
                phase: "已收到合法 BindAck") +
            "，" +
            $"本地={_localEndpoint}，目标={_hostEndpoint}。");
    }

    private void HandleFramePacket(LowLatencyVideoDatagram packet)
    {
        if (!CanAcceptUdpVideoPacket(
                Volatile.Read(ref _state)))
        {
            return;
        }

        _reassembler.TryAdd(packet);
    }

    private void PublishCompletedFrame(
        ulong frameSequence,
        MessageType frameKind,
        ReadOnlyMemory<byte> payload)
    {
        try
        {
            RemoteFrame frame = frameKind == MessageType.Frame
                ? RemoteMessageCodec.DecodeFrame(payload)
                : RemoteMessageCodec.DecodeVideoFrame(payload);
            Volatile.Write(
                ref _highestCompleteFrameSequence,
                checked((long)frameSequence));
            Volatile.Write(ref _lastCompleteFrameAt, Stopwatch.GetTimestamp());
            Volatile.Write(ref _hasCompleteFrame, 1);
            if (CanAcceptUdpVideoPacket(
                    Volatile.Read(ref _state)) &&
                ShouldPublishCompletedFrame(
                    frameSequence,
                    frame))
            {
                // The decoded frame borrows the reassembler buffer. Event
                // subscribers may inspect it synchronously, and must copy it
                // before returning if they need to retain the encoded bytes.
                _publishFrame(frame);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            _log($"已忽略无效 UDP 画面帧：{ex.Message}");
        }
    }

    internal static bool CanAcceptUdpVideoPacket(
        int state) => state == 2;

    private bool ShouldPublishCompletedFrame(
        ulong frameSequence,
        RemoteFrame frame)
    {
        if (!_features.HasFlag(
                LowLatencyVideoFeatures.ShortGopH264))
        {
            return true;
        }

        return ShouldPublishShortGopFrame(
            frameSequence,
            frame,
            ref _lastPublishedShortGopFrameSequence,
            ref _waitingForShortGopRecovery);
    }

    internal static bool ShouldPublishShortGopFrame(
        ulong frameSequence,
        RemoteFrame frame,
        ref long lastPublishedFrameSequence,
        ref bool waitingForRecovery)
    {
        if (frameSequence > long.MaxValue)
        {
            return false;
        }

        long sequence = checked((long)frameSequence);
        long previous = lastPublishedFrameSequence;
        if (previous != 0 && sequence <= previous)
        {
            return false;
        }

        if (frame.Encoding !=
            RemoteFrameEncoding.H264AnnexB)
        {
            waitingForRecovery = true;
            lastPublishedFrameSequence = sequence;
            return true;
        }

        bool recovery =
            frame.Flags.HasFlag(
                RemoteFrameFlags.KeyFrame) &&
            frame.Flags.HasFlag(
                RemoteFrameFlags.CodecConfig);
        if (recovery)
        {
            waitingForRecovery = false;
            lastPublishedFrameSequence = sequence;
            return true;
        }

        if (waitingForRecovery ||
            previous == 0 ||
            sequence != previous + 1)
        {
            waitingForRecovery = true;
            return false;
        }

        lastPublishedFrameSequence = sequence;
        return true;
    }

    private async Task FeedbackLoopAsync(CancellationToken cancellationToken)
    {
        int probesOnCurrentSocket = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int state = Volatile.Read(ref _state);
                if (state == 1)
                {
                    if (Stopwatch.GetElapsedTime(_startedAt) >= BindTimeout)
                    {
                        if (Interlocked.CompareExchange(ref _state, 4, 1) == 1)
                        {
                            _log(
                                "低延迟 UDP 画面握手超时，继续使用 TCP；" +
                                FormatHandshakeDiagnostics(
                                    state: 1,
                                    phase: "等待 BindAck") +
                                "，" +
                                $"本地={_localEndpoint}，目标={_hostEndpoint}。");
                            _ = BeginShutdown();
                        }

                        return;
                    }

                    if (probesOnCurrentSocket >=
                        BindProbeAttemptsPerSocket)
                    {
                        bool rotated =
                            await RotateHandshakeSocketAsync(
                                cancellationToken);
                        probesOnCurrentSocket = 0;
                        if (!rotated &&
                            Volatile.Read(ref _state) != 1)
                        {
                            continue;
                        }
                    }

                    if (Volatile.Read(ref _state) != 1)
                    {
                        continue;
                    }

                    try
                    {
                        await SendBindProbeAsync(cancellationToken);
                        probesOnCurrentSocket++;
                    }
                    catch (SocketException ex) when (
                        LowLatencyVideoSocketSupport
                            .IsTransientDatagramSendError(ex))
                    {
                        string? message =
                            _transientDatagramSendErrorLog
                                .CreateMessage(
                                    "低延迟 UDP BindProbe 发送队列暂时已满，" +
                                    "将在当前握手预算内重试：" +
                                    LowLatencyVideoSocketSupport
                                        .FormatSocketException(ex));
                        if (message is not null)
                        {
                            _log(message);
                        }
                    }
                }
                else if (state is 2 or 5 ||
                    IsPreserveUdpInputBarrierPending(state))
                {
                    try
                    {
                        if (_features.HasFlag(
                                LowLatencyVideoFeatures.CongestionFeedback))
                        {
                            await SendFeedbackV2Async(cancellationToken);
                        }
                        else
                        {
                            await SendFeedbackAsync(cancellationToken);
                        }
                    }
                    catch (SocketException ex) when (
                        LowLatencyVideoSocketSupport
                            .IsTransientDatagramSendError(ex))
                    {
                        string? message =
                            _transientDatagramSendErrorLog
                                .CreateMessage(
                                    "低延迟 UDP 反馈发送队列暂时已满，" +
                                    "已保持通道并将在下一周期重试：" +
                                    LowLatencyVideoSocketSupport
                                        .FormatSocketException(ex));
                        if (message is not null)
                        {
                            _log(message);
                        }
                    }

                    if (state == 2 &&
                        HasFrameDeliveryTimedOut())
                    {
                        await RequestFallbackAsync(
                            SelectFrameStallFallbackReason());
                    }
                    else if (state == 5 &&
                        HasAuthenticatedHeartbeatTimedOut())
                    {
                        _log(
                            "低延迟 UDP 心跳超时，已无法继续保证鼠标输入送达；" +
                            "正在回退 TCP 输入。");
                        await RequestFallbackAsync(1);
                    }
                }
                else if (state == 3)
                {
                    // The Stop control and Stopped acknowledgement share the
                    // reliable TCP control stream.  Do not terminate the sole
                    // feedback/watchdog task while that barrier is in flight;
                    // a later preserve acknowledgement may promote state 3 to
                    // the TCP-video/UDP-input state 5.
                }
                else if (state == 4)
                {
                    return;
                }

                TimeSpan interval =
                    state is 2 or 5 &&
                    _features.HasFlag(
                        LowLatencyVideoFeatures.CongestionFeedback)
                        ? ExtendedFeedbackInterval
                        : ProbeInterval;
                await Task.Delay(interval, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (SocketException ex)
        {
            MarkFatalRouteFailure();
            int state = Volatile.Read(ref _state);
            _log(
                $"低延迟 UDP {(state == 1 ? "BindProbe" : "反馈")}发送失败：" +
                LowLatencyVideoSocketSupport.FormatSocketException(ex) +
                $"；本地={LowLatencyVideoSocketSupport.FormatLocalEndpoint(
                    GetSocketSnapshot())}，" +
                $"目标={_hostEndpoint}，Probe发送成功={BindProbeSendSuccessCount}，" +
                "已回退 TCP。");
            if (state is 2 or 5)
            {
                await RequestFallbackAsync(1);
            }
            else if (state == 3)
            {
                // Keep the Stop barrier authoritative; the fatal marker
                // prevents a preserve acknowledgement from entering state 5.
            }
            else if (Interlocked.CompareExchange(ref _state, 4, 1) == 1)
            {
                _ = BeginShutdown();
            }
        }
        catch (IOException ex)
        {
            MarkFatalRouteFailure();
            int state = Volatile.Read(ref _state);
            _log(
                $"低延迟 UDP {(state == 1 ? "BindProbe" : "反馈")}发送失败：" +
                ex.Message +
                $"；本地={LowLatencyVideoSocketSupport.FormatLocalEndpoint(
                    GetSocketSnapshot())}，" +
                $"目标={_hostEndpoint}，Probe发送成功={BindProbeSendSuccessCount}，" +
                "已回退 TCP。");
            if (state is 2 or 5)
            {
                await RequestFallbackAsync(1);
            }
            else if (state == 3)
            {
                // Keep the Stop barrier authoritative; the fatal marker
                // prevents a preserve acknowledgement from entering state 5.
            }
            else if (Interlocked.CompareExchange(ref _state, 4, 1) == 1)
            {
                _ = BeginShutdown();
            }
        }
    }

    private async Task<bool> RotateHandshakeSocketAsync(
        CancellationToken cancellationToken)
    {
        Socket? replacement = null;
        IDisposable? replacementQwaveControlFlow = null;
        try
        {
            replacement = new Socket(
                _expectedHostAddress.AddressFamily,
                SocketType.Dgram,
                ProtocolType.Udp);
            ConfigureSocket(replacement);
            replacement.Bind(new IPEndPoint(
                _localBindAddress,
                0));
            replacement.Connect(_hostEndpoint);
            replacementQwaveControlFlow = _attachQwaveFlow(
                replacement,
                _hostEndpoint,
                WindowsQwaveTrafficType.Control);
        }
        catch (Exception ex) when (
            ex is SocketException or
                ObjectDisposedException)
        {
            replacementQwaveControlFlow?.Dispose();
            replacement?.Dispose();
            _log(
                "低延迟 UDP 查看端轮换握手端口失败：" +
                ex.Message +
                $"；当前本地={LowLatencyVideoSocketSupport.FormatLocalEndpoint(
                    GetSocketSnapshot())}，目标={_hostEndpoint}。");
            return false;
        }

        Socket retiredSocket;
        Task retiredReceiveTask;
        IDisposable? retiredQwaveControlFlow;
        int replacementGeneration;
        IPEndPoint replacementEndpoint =
            (IPEndPoint)replacement.LocalEndPoint!;
        lock (_socketLock)
        {
            if (Volatile.Read(ref _state) != 1 ||
                cancellationToken.IsCancellationRequested)
            {
                replacementQwaveControlFlow?.Dispose();
                replacement.Dispose();
                return false;
            }

            retiredSocket = _socket;
            retiredReceiveTask = _receiveTask;
            retiredQwaveControlFlow = _qwaveControlFlow;
            replacementGeneration =
                checked(_activeSocketGeneration + 1);
            _activeSocketGeneration =
                replacementGeneration;
            _socket = replacement;
            _qwaveControlFlow =
                replacementQwaveControlFlow;
            Volatile.Write(
                ref _localEndpoint,
                replacementEndpoint);
            // qWAVE owns the socket handle registered with the flow. Remove
            // the flow while that handle is still valid, then retire the
            // socket and its blocking receive worker.
            retiredQwaveControlFlow?.Dispose();
            retiredSocket.Dispose();
        }

        try
        {
            await retiredReceiveTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or
                ObjectDisposedException or
                SocketException)
        {
        }

        lock (_socketLock)
        {
            if (Volatile.Read(ref _state) != 1 ||
                cancellationToken.IsCancellationRequested ||
                replacementGeneration !=
                    _activeSocketGeneration ||
                !ReferenceEquals(_socket, replacement))
            {
                if (ReferenceEquals(_socket, replacement))
                {
                    _qwaveControlFlow?.Dispose();
                    _qwaveControlFlow = null;
                }

                replacement.Dispose();
                return false;
            }

            Socket activeReplacement = replacement;
            _receiveTask = LowLatencyDedicatedThread.Start(
                "RemoteDesk UDP viewer receiver",
                () => ReceiveLoop(
                    activeReplacement,
                    replacementGeneration,
                    _transportCancellation.Token),
                ReceiveThreadPriority);
        }

        _log(
            "低延迟 UDP 尚未收到 BindAck，已轮换查看端握手端口；" +
            $"本地={replacementEndpoint}，目标={_hostEndpoint}。");
        return true;
    }

    private Socket GetSocketSnapshot()
    {
        lock (_socketLock)
        {
            return _socket;
        }
    }

    private bool IsCurrentSocketGeneration(
        Socket socket,
        int socketGeneration)
    {
        lock (_socketLock)
        {
            return socketGeneration ==
                    _activeSocketGeneration &&
                ReferenceEquals(socket, _socket);
        }
    }

    private string FormatHandshakeDiagnostics(
        int state,
        string phase)
    {
        LowLatencyVideoViewerHandshakeSnapshot snapshot =
            CollectHandshakeSnapshot();
        return
            $"建链统计：state={state}，阶段={phase}，" +
            $"Probe发送成功={BindProbeSendSuccessCount}，" +
            $"原始包={snapshot.RawPacketCount}，" +
            $"地址拒绝={snapshot.AddressRejectedCount}，" +
            $"解密失败={snapshot.DecryptFailureCount}，" +
            $"合法Ack={snapshot.ValidAckCount}";
    }

    private void SendMouseInputLoop(
        CancellationToken cancellationToken)
    {
        byte[] payload =
            new byte[RemoteMessageCodec.InputPayloadLength];
        byte[] datagram =
            new byte[
                LowLatencyVideoProtocol.HeaderLength +
                payload.Length +
                LowLatencyVideoProtocol.TagLength];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                _mouseInputSignal.Wait(cancellationToken);
                if (_beforeMouseInputSend is not null)
                {
                    _beforeMouseInputSend(cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                }

                PendingUdpMouseMove? pending;
                while ((pending = Interlocked.Exchange(
                    ref _pendingMouseMove,
                    null)) is not null)
                {
                    if (Volatile.Read(ref _state) is not (2 or 5) ||
                        Volatile.Read(
                            ref _udpMouseInputDisabled) != 0)
                    {
                        continue;
                    }

                    RemoteMessageCodec.WriteInputPayload(
                        pending.Command,
                        payload);
                    ulong inputSequence = checked(
                        (ulong)Interlocked.Increment(
                            ref _nextMouseInputSequence));
                    int datagramLength = _sendCipher.EncryptInto(
                        datagram,
                        LowLatencyVideoDatagramKind.MouseMove,
                        inputSequence,
                        payload.Length,
                        0,
                        0,
                        1,
                        MessageType.Input,
                        0,
                        payload);
                    _mouseInputLatencyTracker.RecordSent(
                        inputSequence,
                        pending.EnqueuedAtTimestamp);
                    cancellationToken.ThrowIfCancellationRequested();
                    int sent;
                    try
                    {
                        sent = GetSocketSnapshot().SendTo(
                            datagram.AsSpan(0, datagramLength),
                            SocketFlags.None,
                            _hostEndpoint);
                    }
                    catch (SocketException ex) when (
                        LowLatencyVideoSocketSupport
                            .IsTransientDatagramSendError(ex))
                    {
                        string? message =
                            _transientDatagramSendErrorLog
                                .CreateMessage(
                                    "低延迟 UDP 鼠标发送队列暂时已满，" +
                                    "已丢弃旧位置并等待最新位置：" +
                                    LowLatencyVideoSocketSupport
                                        .FormatSocketException(ex));
                        if (message is not null)
                        {
                            _log(message);
                        }

                        continue;
                    }

                    if (sent != datagramLength)
                    {
                        throw new IOException(
                            "UDP mouse datagram was only partially sent.");
                    }
                }
            }
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or
                ObjectDisposedException)
        {
        }
        catch (Exception ex) when (
            ex is SocketException or
                IOException or
                CryptographicException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                MarkFatalRouteFailure();
                _log(
                    $"低延迟 UDP 鼠标发送失败：{ex.Message}；" +
                    "正在回退 TCP 输入。");
                RequestFallbackAsync(1)
                    .GetAwaiter()
                    .GetResult();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(datagram);
        }
    }

    private async Task SendBindProbeAsync(CancellationToken cancellationToken)
    {
        Socket socket = GetSocketSnapshot();
        byte[] datagram = _sendCipher.Encrypt(
            LowLatencyVideoDatagramKind.BindProbe,
            0,
            _offer.Challenge.Length,
            0,
            0,
            1,
            0,
            0,
            _offer.Challenge);
        try
        {
            int sent = await socket.SendToAsync(
                datagram,
                SocketFlags.None,
                _hostEndpoint,
                cancellationToken);
            if (sent != datagram.Length)
            {
                throw new IOException(
                    "UDP bind probe was only partially sent.");
            }

            Interlocked.Increment(
                ref _bindProbeSendSuccessCount);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(datagram);
        }
    }

    private async Task SendFeedbackAsync(CancellationToken cancellationToken)
    {
        Socket socket = GetSocketSnapshot();
        byte[] feedback = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(
            feedback,
            checked((ulong)Math.Max(0, Volatile.Read(ref _highestCompleteFrameSequence))));
        byte[] datagram = _sendCipher.Encrypt(
            LowLatencyVideoDatagramKind.Feedback,
            0,
            feedback.Length,
            0,
            0,
            1,
            0,
            0,
            feedback);
        await socket.SendToAsync(
            datagram,
            SocketFlags.None,
            _hostEndpoint,
            cancellationToken);
    }

    private async Task SendFeedbackV2Async(
        CancellationToken cancellationToken)
    {
        Socket socket = GetSocketSnapshot();
        LowLatencyVideoPacketArrivalTracker tracker =
            _arrivalTracker ??
            throw new InvalidOperationException(
                "FeedbackV2 到达统计器尚未初始化。");
        var feedback = tracker.CreateFeedback(
            checked((ulong)Math.Max(
                0,
                Volatile.Read(ref _highestCompleteFrameSequence))),
            checked((ulong)Math.Max(
                0,
                _reassembler.CompletedFrameCount)),
            checked((ulong)Math.Max(
                0,
                _reassembler.AbandonedIncompleteFrameCount)));
        byte[] feedbackPayload =
            LowLatencyVideoFeedbackV2Codec.Encode(feedback);
        byte[] datagram = _sendCipher.Encrypt(
            LowLatencyVideoDatagramKind.FeedbackV2,
            0,
            feedbackPayload.Length,
            0,
            0,
            1,
            0,
            0,
            feedbackPayload);
        try
        {
            await socket.SendToAsync(
                datagram,
                SocketFlags.None,
                _hostEndpoint,
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(feedbackPayload);
            CryptographicOperations.ZeroMemory(datagram);
        }
    }

    private async Task RequestFallbackAsync(byte reason)
    {
        int previousState;
        while (true)
        {
            previousState = Volatile.Read(ref _state);
            if (previousState is not (2 or 5) ||
                Interlocked.CompareExchange(
                    ref _state,
                    3,
                    previousState) == previousState)
            {
                break;
            }
        }

        if (previousState is not (2 or 5))
        {
            return;
        }

        bool preserveUdpInput =
            previousState == 2 &&
            reason ==
                LowLatencyVideoFallbackReasons
                    .PreserveUdpInput &&
            _features.HasFlag(
                LowLatencyVideoFeatures
                    .AuthenticatedHeartbeat) &&
            _features.HasFlag(
                LowLatencyVideoFeatures.UdpMouseInput);
        if (!preserveUdpInput)
        {
            reason = reason ==
                    LowLatencyVideoFallbackReasons
                        .PreserveUdpInput
                ? (byte)2
                : reason;
        }

        Volatile.Write(ref _pendingFallbackReason, reason);
        if (Volatile.Read(ref _fatalRouteFailure) != 0)
        {
            reason = 1;
            Volatile.Write(ref _pendingFallbackReason, reason);
        }

        CancellationToken fallbackBarrierDeadline =
            StartFallbackBarrierDeadline();

        if (reason is 2 or
                LowLatencyVideoFallbackReasons
                    .PreserveUdpInput)
        {
            bool hasCompleteFrame =
                Volatile.Read(ref _hasCompleteFrame) != 0;
            long lastAuthenticatedFramePacketAt =
                Volatile.Read(ref _lastAuthenticatedFramePacketAt);
            string lastPacketAge = lastAuthenticatedFramePacketAt == 0
                ? "无"
                : $"{Stopwatch.GetElapsedTime(lastAuthenticatedFramePacketAt).TotalMilliseconds:F0}ms 前";
            long lastAuthenticatedHeartbeatAt =
                Volatile.Read(ref _lastAuthenticatedHeartbeatAt);
            string lastHeartbeatAge =
                lastAuthenticatedHeartbeatAt == 0
                    ? "未协商/无"
                    : $"{Stopwatch.GetElapsedTime(lastAuthenticatedHeartbeatAt).TotalMilliseconds:F0}ms 前";
            _log(
                $"低延迟 UDP {(hasCompleteFrame ? "完整帧停滞" : "首帧等待")}超时，" +
                $"认证画面包 {Interlocked.Read(ref _authenticatedFramePacketCount)}，" +
                $"最近认证画面包 {lastPacketAge}，" +
                $"最近认证心跳 {lastHeartbeatAge}，" +
                $"完整帧 {_reassembler.CompletedFrameCount}，" +
                $"未完整帧 {_reassembler.AbandonedIncompleteFrameCount}；" +
                (reason ==
                    LowLatencyVideoFallbackReasons
                        .PreserveUdpInput
                    ? "正在仅将视频回退 TCP，并保留 UDP 鼠标。"
                    : "正在回退 TCP。"));
        }

        byte[] stop = RemoteMessageCodec.EncodeLowLatencyVideoStop(
            _offer.ChannelId,
            _offer.Epoch,
            reason);
        Task<bool>? stopSendTask = null;
        bool stopSent;
        try
        {
            stopSendTask = _sendTcpControl(stop);
            stopSent = !fallbackBarrierDeadline.CanBeCanceled
                ? await stopSendTask.ConfigureAwait(false)
                : await stopSendTask.WaitAsync(
                        fallbackBarrierDeadline)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            fallbackBarrierDeadline.IsCancellationRequested)
        {
            ObserveAbandonedControlSend(stopSendTask);
            return;
        }

        if (!stopSent)
        {
            CancelFallbackBarrierDeadline();
            RequestConnectionAbort();
            ShutdownAfterControlSendFailure(
                expectedState: 3,
                "低延迟 UDP Stop 发送失败，已中止当前连接并交由自动重连恢复。");
            return;
        }
    }

    private bool HasFrameDeliveryTimedOut()
    {
        if (Volatile.Read(ref _hasCompleteFrame) != 0)
        {
            long lastCompleteFrameAt =
                Volatile.Read(ref _lastCompleteFrameAt);
            if (lastCompleteFrameAt == 0)
            {
                return false;
            }

            if (Stopwatch.GetElapsedTime(lastCompleteFrameAt) <
                SteadyFrameTimeout)
            {
                return false;
            }

            long lastAuthenticatedHeartbeatAt =
                Volatile.Read(ref _lastAuthenticatedHeartbeatAt);
            TimeSpan heartbeatAge =
                lastAuthenticatedHeartbeatAt == 0
                    ? TimeSpan.MaxValue
                    : Stopwatch.GetElapsedTime(
                        lastAuthenticatedHeartbeatAt);
            return !IsAuthenticatedStaticSourceSilence(
                Volatile.Read(
                    ref _highestAuthenticatedFramePacketSequence),
                Volatile.Read(
                    ref _highestCompleteFrameSequence),
                heartbeatAge,
                _features.HasFlag(
                    LowLatencyVideoFeatures
                        .AuthenticatedHeartbeat));
        }

        long initialFrameWaitStartedAt =
            Volatile.Read(ref _initialFrameWaitStartedAt);
        if (initialFrameWaitStartedAt == 0 ||
            Stopwatch.GetElapsedTime(initialFrameWaitStartedAt) <
                InitialFrameTimeout)
        {
            return false;
        }

        // Authenticated fragments prove that the UDP path is alive, but not
        // that it can deliver a usable frame. Keep them for diagnostics and
        // feedback only: allowing fragments to renew this deadline can leave
        // the viewer black forever when every frame remains incomplete.
        return true;
    }

    internal static bool IsAuthenticatedStaticSourceSilence(
        long highestAuthenticatedFramePacketSequence,
        long highestCompleteFrameSequence,
        TimeSpan heartbeatAge,
        bool authenticatedHeartbeatNegotiated)
    {
        if (heartbeatAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(heartbeatAge));
        }

        // WGC is allowed to emit nothing while the desktop is unchanged.  A
        // fresh authenticated heartbeat proves that the UDP route is alive;
        // if no newer authenticated frame sequence arrived after the last
        // completed frame, there is no incomplete frame to recover and the
        // last picture remains the correct presentation. A late duplicate or
        // XOR-parity packet for that same completed frame is harmless. A
        // higher sequence keeps the existing deadline authoritative so
        // repeated incomplete frames cannot leave the viewer black or frozen
        // indefinitely.
        return authenticatedHeartbeatNegotiated &&
            heartbeatAge < SteadyFrameTimeout &&
            highestAuthenticatedFramePacketSequence > 0 &&
            highestCompleteFrameSequence > 0 &&
            highestAuthenticatedFramePacketSequence <=
                highestCompleteFrameSequence;
    }

    private static void UpdateMaximum(
        ref long target,
        long candidate)
    {
        long observed = Volatile.Read(ref target);
        while (candidate > observed)
        {
            long previous = Interlocked.CompareExchange(
                ref target,
                candidate,
                observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }

    private byte SelectFrameStallFallbackReason()
    {
        long lastAuthenticatedHeartbeatAt =
            Volatile.Read(ref _lastAuthenticatedHeartbeatAt);
        TimeSpan heartbeatAge =
            lastAuthenticatedHeartbeatAt == 0
                ? TimeSpan.MaxValue
                : Stopwatch.GetElapsedTime(
                    lastAuthenticatedHeartbeatAt);
        return SelectFrameStallFallbackReason(
            heartbeatAge,
            _features.HasFlag(
                LowLatencyVideoFeatures
                    .AuthenticatedHeartbeat),
            _features.HasFlag(
                LowLatencyVideoFeatures.UdpMouseInput));
    }

    private bool HasAuthenticatedHeartbeatTimedOut() =>
        !IsAuthenticatedHeartbeatFresh();

    private bool IsPreserveUdpInputBarrierPending(int state) =>
        state == 3 &&
        Volatile.Read(ref _pendingFallbackReason) ==
            LowLatencyVideoFallbackReasons.PreserveUdpInput &&
        Volatile.Read(ref _fatalRouteFailure) == 0;

    private void MarkFatalRouteFailure()
    {
        Interlocked.Exchange(ref _fatalRouteFailure, 1);
        Volatile.Write(ref _pendingFallbackReason, 1);
    }

    internal void MarkFatalRouteFailureForTests() =>
        MarkFatalRouteFailure();

    internal static byte SelectFrameStallFallbackReason(
        TimeSpan heartbeatAge,
        bool authenticatedHeartbeatNegotiated,
        bool udpMouseInputNegotiated)
    {
        if (heartbeatAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(heartbeatAge));
        }

        return authenticatedHeartbeatNegotiated &&
            udpMouseInputNegotiated &&
            heartbeatAge < SteadyFrameTimeout
                ? LowLatencyVideoFallbackReasons
                    .PreserveUdpInput
                : (byte)2;
    }

    private void ShutdownAfterControlSendFailure(
        int expectedState,
        string logMessage)
    {
        if (Interlocked.CompareExchange(
                ref _state,
                4,
                expectedState) != expectedState)
        {
            return;
        }

        TryCancelTransport();
        _ = BeginShutdown();
        _log(logMessage);
    }

    private CancellationToken
        StartFallbackBarrierDeadline()
    {
        if (Volatile.Read(ref _state) != 3)
        {
            return default;
        }

        var deadlineCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                _transportCancellation.Token);
        CancellationToken deadlineToken =
            deadlineCancellation.Token;
        CancellationTokenSource? previous;
        lock (_fallbackBarrierDeadlineLock)
        {
            if (Volatile.Read(ref _state) != 3)
            {
                deadlineCancellation.Dispose();
                return default;
            }

            previous = _fallbackBarrierDeadlineCancellation;
            _fallbackBarrierDeadlineCancellation =
                deadlineCancellation;
        }

        TryCancelDeadline(previous);
        _ = MonitorFallbackBarrierDeadlineAsync(
            deadlineCancellation);
        return deadlineToken;
    }

    private async Task MonitorFallbackBarrierDeadlineAsync(
        CancellationTokenSource deadlineCancellation)
    {
        try
        {
            await _fallbackBarrierDelayAsync(
                    FallbackBarrierTimeout,
                    deadlineCancellation.Token)
                .ConfigureAwait(false);
            if (Interlocked.CompareExchange(
                    ref _state,
                    4,
                    3) != 3)
            {
                return;
            }

            Volatile.Write(ref _pendingFallbackReason, 0);
            RequestConnectionAbort();
            TryCancelTransport();
            _ = BeginShutdown();
            _log(
                "低延迟 UDP Stop 确认等待超过 5 秒，" +
                "已中止当前连接并交由自动重连恢复。");
        }
        catch (OperationCanceledException) when (
            deadlineCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_fallbackBarrierDeadlineLock)
            {
                if (ReferenceEquals(
                        _fallbackBarrierDeadlineCancellation,
                        deadlineCancellation))
                {
                    _fallbackBarrierDeadlineCancellation = null;
                }
            }

            deadlineCancellation.Dispose();
        }
    }

    private void CancelFallbackBarrierDeadline()
    {
        CancellationTokenSource? deadlineCancellation;
        lock (_fallbackBarrierDeadlineLock)
        {
            deadlineCancellation =
                _fallbackBarrierDeadlineCancellation;
            _fallbackBarrierDeadlineCancellation = null;
        }

        TryCancelDeadline(deadlineCancellation);
    }

    private static void TryCancelDeadline(
        CancellationTokenSource? deadlineCancellation)
    {
        if (deadlineCancellation is null)
        {
            return;
        }

        try
        {
            deadlineCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void RequestConnectionAbort()
    {
        if (Interlocked.Exchange(
                ref _connectionAbortRequested,
                1) != 0)
        {
            return;
        }

        try
        {
            _abortConnection?.Invoke();
        }
        catch (Exception ex)
        {
            _log(
                "低延迟 UDP 回退无法中止当前连接：" +
                ex.Message);
        }
    }

    private static void ObserveAbandonedControlSend(
        Task<bool>? sendTask)
    {
        if (sendTask is null || sendTask.IsCompletedSuccessfully)
        {
            return;
        }

        _ = sendTask.ContinueWith(
            static completedTask =>
            {
                _ = completedTask.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void TryCancelTransport()
    {
        try
        {
            _transportCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private Task BeginShutdown()
    {
        lock (_shutdownLock)
        {
            return _shutdownTask ??= Task.Run(ShutdownCoreAsync);
        }
    }

    private async Task ShutdownCoreAsync()
    {
        Interlocked.Exchange(ref _state, 4);
        Socket socket;
        Task receiveTask;
        lock (_socketLock)
        {
            socket = _socket;
            receiveTask = _receiveTask;
            // qWAVE retains the native socket handle. Always unregister the
            // flow before closing that handle, including shutdown racing a
            // handshake source-port rotation.
            _qwaveControlFlow?.Dispose();
            _qwaveControlFlow = null;
        }
        TryCancelTransport();
        try
        {
            socket.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            await Task.WhenAll(
                    receiveTask,
                    _feedbackTask,
                    _mouseInputTask)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ex is not (OperationCanceledException or ObjectDisposedException))
            {
                _log($"低延迟 UDP 清理异常：{ex.Message}");
            }
        }

        _reassembler.Reset();
        Interlocked.Exchange(
            ref _pendingMouseMove,
            null);
        _sendCipher.Dispose();
        _receiveCipher.Dispose();
        CryptographicOperations.ZeroMemory(_offer.HostToViewerKey);
        CryptographicOperations.ZeroMemory(_offer.ViewerToHostKey);
        CryptographicOperations.ZeroMemory(_offer.HostNoncePrefix);
        CryptographicOperations.ZeroMemory(_offer.ViewerNoncePrefix);
        CryptographicOperations.ZeroMemory(_offer.Challenge);
        _mouseInputSignal.Dispose();
        _transportCancellation.Dispose();
    }

    private static void ConfigureSocket(Socket socket)
    {
        // A quality-preserving 4K60 IDR is roughly 311 KiB before encrypted
        // UDP fragmentation/FEC. Keep several bursts in the kernel so a
        // scheduler hiccup does not become avoidable packet loss.
        socket.ReceiveBufferSize = 1024 * 1024;
        socket.SendBufferSize = 64 * 1024;
        if (socket.AddressFamily == AddressFamily.InterNetworkV6)
        {
            socket.DualMode = false;
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                socket.IOControl(
                    unchecked((IOControlCode)(-1744830452)),
                    [0, 0, 0, 0],
                    null);
            }
            catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException)
            {
            }
        }
    }

    private sealed record PendingUdpMouseMove(
        RemoteInputCommand Command,
        long EnqueuedAtTimestamp);
}
