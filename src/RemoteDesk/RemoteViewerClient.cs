using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace RemoteDesk;

internal readonly record struct RemoteTextInputResult(int SentCodePoints, bool Truncated);

internal enum ReturnedClipboardFileBatchOutcome
{
    Succeeded,
    Failed,
    Cancelled,
    Busy,
    Disconnected,
    TimedOut
}

internal sealed record ReturnedClipboardFileBatchResult(
    ReturnedClipboardFileBatchOutcome Outcome,
    IReadOnlyList<string> LocalPaths,
    string Message,
    bool ClipboardUpdated,
    IReadOnlyList<string>? SavedPaths = null)
{
    // Failed clipboard commits can still have saved files. These are display-only;
    // LocalPaths keeps its successful-delivery-only contract for clipboard/drag consumers.
    public IReadOnlyList<string> FilesForDisplay => SavedPaths ?? LocalPaths;
    public bool Success => Outcome == ReturnedClipboardFileBatchOutcome.Succeeded;

    public bool Cancelled => Outcome == ReturnedClipboardFileBatchOutcome.Cancelled;

    public bool Busy => Outcome == ReturnedClipboardFileBatchOutcome.Busy;
}

internal readonly record struct ReturnedClipboardFinalizeResult(
    bool Success,
    string Message,
    IReadOnlyList<string> LocalPaths,
    bool ClipboardUpdated);

internal sealed record CaptureTargetAvailabilityUpdate(
    long ConnectionGeneration,
    bool IsAvailable,
    CaptureTargetInfo Target,
    int TargetGeneration,
    string DisplayMessage);

internal sealed record CaptureTargetChangedUpdate(
    long ConnectionGeneration,
    CaptureTargetInfo Target);

internal sealed record CaptureTargetsUpdate(
    long ConnectionGeneration,
    IReadOnlyList<CaptureTargetInfo> Targets);

internal sealed record RemoteDeviceInfoUpdate(
    long ConnectionGeneration,
    RemoteDeviceDescriptor Device);

internal sealed class RemoteSessionRejectedException : IOException
{
    public RemoteSessionRejectedException(string message)
        : base(message)
    {
    }
}

internal sealed partial class RemoteViewerClient : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan DeviceInfoHandshakeTimeout =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisposeDisconnectTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(18);
    internal static readonly TimeSpan HeartbeatWatchdogInterval =
        TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan SuppressedHeartbeatTimeout =
        TimeSpan.FromMinutes(2.5);
    private static readonly TimeSpan ReturnedClipboardFileRequestIdleTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReturnedClipboardFileRejectDrainTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RemoteCapabilityRequestGracePeriod = TimeSpan.FromMilliseconds(150);
    internal const int FrameReceiveBufferBytes = 128 * 1024;
    private const int InputSendBufferBytes = 32 * 1024;
    internal const int MaxQueuedInputs = 256;
    internal const int MaxInputBatchSize = 16;
    internal const int ReliablePointerUdpResumeDelayMilliseconds = 25;
    private const int MaxTextInputCodePoints = 1024;
    private const RemoteDeviceCapabilities
        ShortGopH264PrerequisiteCapabilities =
            RemoteDeviceCapabilities.LowLatencyUdpVideo |
            RemoteDeviceCapabilities.UdpVideoCongestionFeedback |
            RemoteDeviceCapabilities.LowLatencyUdpVideoXorFec;
    private const RemoteDeviceCapabilities LocalViewerBaselineCapabilities =
        RemoteDeviceCapabilities.ClipboardText |
        RemoteDeviceCapabilities.ClipboardSnapshotV1 |
        RemoteDeviceCapabilities.FileChecksum |
        RemoteDeviceCapabilities.FileTransferReceipt |
        RemoteDeviceCapabilities.FileTransferCancel |
        RemoteDeviceCapabilities.FileTransferPreview |
        RemoteDeviceCapabilities.LowLatencyUdpVideo |
        RemoteDeviceCapabilities.UdpVideoCongestionFeedback |
        RemoteDeviceCapabilities.LowLatencyUdpVideoXorFec |
        RemoteDeviceCapabilities.LowLatencyUdpMouseInput |
        RemoteDeviceCapabilities.LowLatencyUdpMouseInputAppliedAck |
        RemoteDeviceCapabilities.HighFrameRateH264 |
        RemoteDeviceCapabilities.AuthenticatedUdpHeartbeat |
        RemoteDeviceCapabilities.HighQualityJpeg;
    // Keep GOP2 behind the complete protected UDP tier and an explicit local
    // opt-in. Production remains on independently decodable GOP1 because the
    // Windows DDA/MF path has not met the real-machine startup deadline with
    // dependent frames. The gate stays available for bounded experiments.
    private static readonly RemoteDeviceCapabilities
        LocalViewerCapabilities =
            ResolveLocalViewerCapabilities(
                LocalViewerBaselineCapabilities,
                enableShortGopH264: false);
    private static readonly RemoteDeviceCapabilities
        RelayViewerCapabilities =
            LocalViewerCapabilities &
            ~(RemoteDeviceCapabilities.LowLatencyUdpVideo |
              RemoteDeviceCapabilities.UdpVideoCongestionFeedback |
              RemoteDeviceCapabilities.LowLatencyUdpVideoXorFec |
              RemoteDeviceCapabilities.LowLatencyUdpMouseInput |
              RemoteDeviceCapabilities.LowLatencyUdpMouseInputAppliedAck |
              RemoteDeviceCapabilities.HighFrameRateH264 |
              RemoteDeviceCapabilities.ShortGopH264 |
              RemoteDeviceCapabilities.AuthenticatedUdpHeartbeat);

    internal static bool LocalH264SamplesAreAllIndependent =>
        !LocalViewerCapabilities.HasFlag(
            RemoteDeviceCapabilities.ShortGopH264);

    internal static RemoteDeviceCapabilities
        RelayTransportViewerCapabilities =>
            RelayViewerCapabilities;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _inputSignal = new(0, 1);
    private readonly SemaphoreSlim _fileTransferLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemoteControlMessage>> _fileReceipts = new();
    internal TimeSpan FileSaveConfirmationTimeout { get; set; } = TimeSpan.FromSeconds(120);
    private readonly FileTransferReceiver _incomingFileReceiver;
    private readonly object _connectionStateLock = new();
    private readonly object _connectedChangedQueueLock = new();
    private readonly object _inputLock = new();
    private readonly object _framePublishLock = new();
    private readonly List<TaskCompletionSource<bool>> _inputFlushWaiters = [];
    private readonly object _selfUpdateLock = new();
    private readonly object _returnedClipboardFileRequestLock = new();
    private readonly RemoteInputQueue _inputQueue = new();
    private readonly SemaphoreSlim _disconnectLock = new(1, 1);
    private readonly TimeSpan _returnedClipboardFileRequestIdleTimeout;
    private readonly TimeSpan _connectTimeout;

    private enum ConnectAttemptOutcome
    {
        ExistingConnection,
        NewConnection,
        NeedsStaleOwnerCleanup
    }

    private readonly record struct ConnectAttemptResult(
        ConnectAttemptOutcome Outcome,
        long ConnectionGeneration);

    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private SecureSession? _session;
    private CancellationTokenSource? _cancellationTokenSource;
    private TcpClient? _pendingTcpClient;
    private CancellationTokenSource? _pendingConnectionCancellationTokenSource;
    private Task? _receiveLoopTask;
    private Task? _inputLoopTask;
    private Task? _heartbeatLoopTask;
    private LowLatencyVideoViewerTransport? _lowLatencyVideoTransport;
    private Task _connectedChangedQueueTail = Task.CompletedTask;
    private CancellationTokenSource? _connectedEventOwner;
    private long _lastMessageTicks;
    private long _lastPingSentTicks;
    private int _pingAwaitingPong;
    private int _fileTransferConfirmationWaiters;
    private sealed record OutgoingFileTransferHeartbeatState(
        CancellationTokenSource OwnerConnection,
        long? DrainStartedTicks = null);

    private OutgoingFileTransferHeartbeatState? _outgoingFileTransferHeartbeat;
    private int _returnedClipboardFileRequestPending;
    private int _suppressInputUntilReturnedClipboardRequestDrained;
    private bool _inputWriteInProgress;
    private int _pendingReliablePointerInputs;
    private int _udpMouseRouteObserved;
    private long _inputConnectionGeneration;
    private long _mouseRouteGeneration;
    private long _reliablePointerInputGateUntil;
    private long _returnedClipboardFileRequestSequence;
    private ReturnedClipboardFileRequestContext? _returnedClipboardFileRequest;
    private bool _returnedClipboardProtocolDesynchronized;
    private bool _allowVideoFallback = true;
    private RemoteDeviceCapabilities _remoteCapabilities = RemoteDeviceCapabilities.None;
    private int _remoteCapabilitiesInitialized;
    private string? _remoteBuildStamp;
    private RemoteDeviceDescriptor? _remoteDeviceInfo;
    private bool _deviceIdentityRequested;
    private TaskCompletionSource<bool>? _remoteCapabilitiesReady;
    private RemoteSessionRejectedException? _lastSessionRejection;
    private bool _selfUpdatePackageRequested;
    private string? _pendingSelfUpdateTransferId;
    private CaptureTargetAvailabilityUpdate?
        _latestCaptureTargetAvailability;

    public event Action<RemoteFrame>? FrameReceived;
    public event Action<RemoteDeviceDescriptor>? DeviceInfoReceived;
    internal event Action<RemoteDeviceInfoUpdate>?
        DeviceInfoUpdated;
    public event Action<IReadOnlyList<CaptureTargetInfo>>? CaptureTargetsReceived;
    internal event Action<CaptureTargetsUpdate>?
        CaptureTargetsUpdated;
    public event Action<CaptureTargetInfo>? CaptureTargetChanged;
    internal event Action<CaptureTargetChangedUpdate>?
        CaptureTargetSelectionChanged;
    public event Action<CaptureTargetAvailabilityUpdate>?
        CaptureTargetAvailabilityChanged;
    public event Action<string>? ClipboardStatusReceived;
    public event Action<bool, string>? FileTransferStatusReceived;
    public event Action<bool>? RemoteClipboardFileRequestPendingChanged;
    public event Action<ReturnedClipboardFileBatchResult>? RemoteClipboardFileBatchCompleted;
    public event Action<ReturnedClipboardFileBatchResult>? RemoteClipboardFileResultReady;
    public event Action<string>? Log;
    public event Action<bool>? ConnectedChanged;
    public event Action<TimeSpan>? RoundTripUpdated;
    public event Action<string>? HostVideoDiagnosticsReceived;
    internal Func<IReadOnlyList<FileTransferConfirmationItem>, string?, bool>? ConfirmRemoteClipboardFileTransfer { get; set; }

    public bool IsConnected
    {
        get
        {
            lock (_connectionStateLock)
            {
                return _stream is not null && _tcpClient?.Connected == true;
            }
        }
    }

    public bool IsRemoteClipboardFileRequestPending =>
        Volatile.Read(ref _returnedClipboardFileRequestPending) != 0;

    internal RemoteSessionRejectedException? LastSessionRejection =>
        Volatile.Read(ref _lastSessionRejection);

    internal async Task<bool> WaitForCurrentDeviceInfoAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        CancellationTokenSource? ownerConnection;
        TaskCompletionSource<bool>? readiness;
        lock (_connectionStateLock)
        {
            ownerConnection = _cancellationTokenSource;
            readiness = Volatile.Read(
                ref _remoteCapabilitiesReady);
            if (ownerConnection is null ||
                ownerConnection.IsCancellationRequested ||
                readiness is null)
            {
                ThrowRememberedSessionRejection();
                return false;
            }

            if (Volatile.Read(
                    ref _remoteCapabilitiesInitialized) != 0)
            {
                return true;
            }
        }

        bool received;
        try
        {
            received = await readiness.Task.WaitAsync(
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            bool qualified;
            lock (_connectionStateLock)
            {
                // DeviceInfo can win the exact timer boundary. Re-check the
                // captured owner before classifying this attempt as an
                // unqualified reconnect and tearing down a valid session.
                qualified = ReferenceEquals(
                        _cancellationTokenSource,
                        ownerConnection) &&
                    !ownerConnection.IsCancellationRequested &&
                    ReferenceEquals(
                        Volatile.Read(
                            ref _remoteCapabilitiesReady),
                        readiness) &&
                    Volatile.Read(
                        ref _remoteCapabilitiesInitialized) != 0;
            }

            if (!qualified)
            {
                ThrowRememberedSessionRejection();
            }

            return qualified;
        }

        bool isCurrentQualifiedConnection;
        lock (_connectionStateLock)
        {
            isCurrentQualifiedConnection = received &&
                ReferenceEquals(
                    _cancellationTokenSource,
                    ownerConnection) &&
                !ownerConnection.IsCancellationRequested &&
                ReferenceEquals(
                    Volatile.Read(ref _remoteCapabilitiesReady),
                    readiness) &&
                Volatile.Read(
                    ref _remoteCapabilitiesInitialized) != 0;
        }

        if (!isCurrentQualifiedConnection)
        {
            ThrowRememberedSessionRejection();
        }

        return isCurrentQualifiedConnection;
    }

    private void ThrowRememberedSessionRejection()
    {
        if (Volatile.Read(ref _lastSessionRejection) is { } rejection)
        {
            throw new RemoteSessionRejectedException(
                rejection.Message);
        }
    }

    internal bool SupportsRemoteClipboardSequenceTracking =>
        _remoteCapabilities.HasFlag(RemoteDeviceCapabilities.ClipboardSequenceTracking);

    internal bool SupportsRemoteClipboardPasteShortcut =>
        _remoteCapabilities.HasFlag(RemoteDeviceCapabilities.ClipboardPasteShortcut);

    internal long InputConnectionGeneration
    {
        get
        {
            lock (_inputLock)
            {
                return _inputConnectionGeneration;
            }
        }
    }

    internal CaptureTargetAvailabilityUpdate?
        LatestCaptureTargetAvailability =>
            Volatile.Read(
                ref _latestCaptureTargetAvailability);

    internal bool IsCurrentCaptureTargetAvailabilityUpdate(
        CaptureTargetAvailabilityUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return IsConnected &&
            Volatile.Read(ref _inputConnectionGeneration) ==
                update.ConnectionGeneration;
    }

    internal bool IsCurrentCaptureTargetChangedUpdate(
        CaptureTargetChangedUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return IsConnected &&
            Volatile.Read(ref _inputConnectionGeneration) ==
                update.ConnectionGeneration;
    }

    internal bool IsCurrentCaptureTargetsUpdate(
        CaptureTargetsUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return IsConnected &&
            Volatile.Read(ref _inputConnectionGeneration) ==
                update.ConnectionGeneration;
    }

    internal bool IsCurrentDeviceInfoUpdate(
        RemoteDeviceInfoUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return IsConnected &&
            Volatile.Read(ref _inputConnectionGeneration) ==
                update.ConnectionGeneration;
    }

    public RemoteViewerClient() : this(null, null)
    {
    }

    internal RemoteViewerClient(Func<string>? incomingFileReceiveDirectoryProvider)
        : this(incomingFileReceiveDirectoryProvider, null)
    {
    }

    internal RemoteViewerClient(
        Func<string>? incomingFileReceiveDirectoryProvider,
        Func<IEnumerable<string>, Task>? setFileDropListAsync,
        TimeSpan? returnedClipboardFileRequestIdleTimeout = null,
        TimeSpan? connectTimeout = null)
    {
        _returnedClipboardFileRequestIdleTimeout =
            returnedClipboardFileRequestIdleTimeout is { } configuredTimeout && configuredTimeout > TimeSpan.Zero
                ? configuredTimeout
                : ReturnedClipboardFileRequestIdleTimeout;
        _incomingFileReceiver = incomingFileReceiveDirectoryProvider is null
            ? new FileTransferReceiver(
                message => Log?.Invoke(message),
                FileTransferReceiver.GetReceiveDirectory,
                "本机",
                setFileDropListAsync: setFileDropListAsync)
            : new FileTransferReceiver(
                message => Log?.Invoke(message),
                incomingFileReceiveDirectoryProvider,
                "本机",
                setFileDropListAsync: setFileDropListAsync);
        _connectTimeout =
            connectTimeout is { } configuredConnectTimeout &&
                configuredConnectTimeout > TimeSpan.Zero
                    ? configuredConnectTimeout
                    : ConnectTimeout;
    }

    public bool AllowVideoFallback => _allowVideoFallback;

    public async Task ConnectAsync(
        string host,
        int port,
        string password,
        ViewerVideoMode videoMode = ViewerVideoMode.Automatic,
        CancellationToken cancellationToken = default)
    {
        await ConnectCoreAsync(
                host,
                port,
                password,
                videoMode,
                relayRoute: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ConnectViaRelayAsync(
        RelayConnectionOptions relayRoute,
        string password,
        ViewerVideoMode videoMode = ViewerVideoMode.Automatic,
        CancellationToken cancellationToken = default)
    {
        relayRoute = relayRoute.Validate();
        await ConnectCoreAsync(
                relayRoute.ServerAddress,
                relayRoute.Port,
                password,
                videoMode,
                relayRoute,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ConnectCoreAsync(
        string host,
        int port,
        string password,
        ViewerVideoMode videoMode,
        RelayConnectionOptions? relayRoute,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("请输入被控电脑的内网 IP。");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("请输入目标设备的设备密钥。");
        }

        // A remote close can make TcpClient.Connected false a little before the receive loop has
        // finished its owner-bound cleanup. Never publish a new connection over that old owner:
        // drain it first so delayed control handlers cannot mutate the new session's capabilities,
        // file receiver, or pending drag-out request.
        while (true)
        {
            ConnectAttemptResult attempt =
                await TryConnectSerializedAsync(
                        host,
                        port,
                        password,
                        videoMode,
                        relayRoute,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (attempt.Outcome is
                    ConnectAttemptOutcome.ExistingConnection or
                    ConnectAttemptOutcome.NewConnection)
            {
                return;
            }

            await DisconnectIfCurrentGenerationAsync(
                    attempt.ConnectionGeneration)
                .ConfigureAwait(false);
        }
    }

    private async Task<ConnectAttemptResult> TryConnectSerializedAsync(
        string host,
        int port,
        string password,
        ViewerVideoMode videoMode,
        RelayConnectionOptions? relayRoute,
        CancellationToken cancellationToken)
    {
        string? connectedLog = null;
        string? videoCapabilityLog = null;
        await _disconnectLock.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected)
            {
                return new ConnectAttemptResult(
                    ConnectAttemptOutcome.ExistingConnection,
                    long.MinValue);
            }

            lock (_connectionStateLock)
            {
                if (HasPublishedConnectionStateNoLock())
                {
                    return new ConnectAttemptResult(
                        ConnectAttemptOutcome.NeedsStaleOwnerCleanup,
                        InputConnectionGeneration);
                }
            }

            Interlocked.Exchange(
                ref _lastSessionRejection,
                null);

            string? ffmpegPath = videoMode == ViewerVideoMode.StableJpeg ? null : FfmpegH264Decoder.AvailablePath;
            bool hasNativeHardwareDecoder =
                videoMode != ViewerVideoMode.StableJpeg &&
                MediaFoundationD3D11H264Decoder
                    .IsPlatformPotentiallySupported;
            RemoteVideoCodecs supportedVideoCodecs =
                ResolveSupportedVideoCodecs(
                    videoMode,
                    ffmpegPath,
                    hasNativeHardwareDecoder);
            var tcpClient = new TcpClient();
            NetworkUtils.ConfigureLowLatencyTcpClient(
                tcpClient,
                FrameReceiveBufferBytes,
                InputSendBufferBytes);
            var cancellationTokenSource = new CancellationTokenSource();
            SecureSession? session = null;
            Task? receiveLoopTask = null;
            var remoteCapabilitiesReady = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenSource? connectTimeout = null;
            CancellationTokenSource? connectionAttemptCancellation = null;
            CancellationTokenRegistration closeOnCancellation = default;

            try
            {
                connectTimeout =
                    new CancellationTokenSource(_connectTimeout);
                connectionAttemptCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        connectTimeout.Token,
                        cancellationToken,
                        cancellationTokenSource.Token);
                CancellationToken connectionAttemptToken =
                    connectionAttemptCancellation.Token;
                closeOnCancellation =
                    connectionAttemptToken.UnsafeRegister(
                        static state =>
                            TryCloseTcpClient((TcpClient?)state),
                        tcpClient);
                PublishPendingConnection(
                    tcpClient,
                    cancellationTokenSource);

                if (relayRoute is null)
                {
                    await tcpClient.ConnectAsync(
                        host,
                        port,
                        connectionAttemptToken);
                }
                else
                {
                    await RelayTunnelClient.ConnectViewerIntoAsync(
                            tcpClient,
                            relayRoute,
                            connectionAttemptToken,
                            message => Log?.Invoke(message))
                        .ConfigureAwait(false);
                }
                NetworkStream stream = tcpClient.GetStream();
                session = await Protocol.AuthenticateClientAsync(
                    stream,
                    password,
                    connectionAttemptToken);
                connectionAttemptToken.ThrowIfCancellationRequested();

                long inputConnectionGeneration;
                lock (_connectionStateLock)
                {
                    if (!ReferenceEquals(
                            _pendingTcpClient,
                            tcpClient) ||
                        !ReferenceEquals(
                            _pendingConnectionCancellationTokenSource,
                            cancellationTokenSource))
                    {
                        throw new OperationCanceledException(
                            "连接已取消。",
                            connectionAttemptToken);
                    }

                    _pendingTcpClient = null;
                    _pendingConnectionCancellationTokenSource = null;
                    ResetRemotePeerState();
                    Volatile.Write(ref _remoteCapabilitiesReady, remoteCapabilitiesReady);
                    _tcpClient = tcpClient;
                    _stream = stream;
                    _session = session;
                    _cancellationTokenSource = cancellationTokenSource;
                    _allowVideoFallback = true;
                    lock (_returnedClipboardFileRequestLock)
                    {
                        _returnedClipboardProtocolDesynchronized = false;
                    }
                    ResetHeartbeatMetrics();
                    MarkMessageReceived();
                    inputConnectionGeneration = BeginInputConnectionGeneration();
                }

                await Protocol.WriteMessageAsync(
                    stream,
                    MessageType.Control,
                    RemoteMessageCodec.EncodeViewerInfo(supportedVideoCodecs),
                    session,
                    _writeLock,
                    connectionAttemptToken);
                // Advertise the viewer's safeguards before any user/API request can be written.
                // Stream ordering then guarantees a modern host enables preview/checksum/cancel
                // before it observes FileTransferRequestClipboardFiles.
                await Protocol.WriteMessageAsync(
                    stream,
                    MessageType.Control,
                    RemoteMessageCodec.EncodeViewerCapabilities(
                        NegotiatedViewerCapabilities(relayRoute is not null)),
                    session,
                    _writeLock,
                    connectionAttemptToken);

                lock (_connectionStateLock)
                {
                    if (!ReferenceEquals(_cancellationTokenSource, cancellationTokenSource))
                    {
                        throw new IOException("连接状态已切换，无法启动后台通信循环。");
                    }

                    // Enqueue the connected transition before the receive loop can detach this
                    // owner. The queue invokes subscribers off-lock and preserves true -> false
                    // ordering even when the peer closes immediately after authentication.
                    _connectedEventOwner = cancellationTokenSource;
                    _manualClipboardReplyTimeoutMilliseconds = relayRoute is null
                        ? ClipboardRequestTracker.TimeoutMilliseconds : ClipboardRequestTracker.RelayTimeoutMilliseconds;
                    EnqueueConnectedChanged(true);
                    // An async method can execute inline until its first incomplete
                    // await. A host may already have buffered screen metadata, so
                    // starting ReceiveLoopAsync directly here can invoke a synchronous
                    // UI subscriber while this lock is held. The UI queries connection
                    // state and then deadlocks with the receiver. Queue owned loops
                    // instead; do not cancel their scheduling, since teardown must run.
                    receiveLoopTask = Task.Run(() => ReceiveLoopAsync(
                        tcpClient,
                        stream,
                        session,
                        cancellationTokenSource,
                        inputConnectionGeneration,
                        remoteCapabilitiesReady), CancellationToken.None);
                    if (!ReferenceEquals(_cancellationTokenSource, cancellationTokenSource))
                    {
                        throw new IOException("连接在后台接收循环启动时已中断。");
                    }

                    _receiveLoopTask = receiveLoopTask;

                    _inputLoopTask = Task.Run(() => InputLoopAsync(
                        tcpClient,
                        stream,
                        session,
                        cancellationTokenSource,
                        inputConnectionGeneration), CancellationToken.None);
                    _heartbeatLoopTask = Task.Run(() => HeartbeatLoopAsync(
                        tcpClient,
                        stream,
                        session,
                        cancellationTokenSource), CancellationToken.None);
                }

                videoCapabilityLog =
                    FormatViewerVideoCapabilityLog(
                        supportedVideoCodecs,
                        videoMode,
                        ffmpegPath,
                        hasNativeHardwareDecoder);
                connectedLog = relayRoute is null
                    ? $"已加密直连 {host}:{port}"
                    : $"已通过加密中继连接设备 {relayRoute.DeviceId}";
            }
            catch (Exception ex) when (
                IsConnectionAttemptCancellation(
                    ex,
                    connectionAttemptCancellation))
            {
                bool timedOut =
                    connectTimeout?.IsCancellationRequested == true &&
                    !cancellationToken.IsCancellationRequested &&
                    !cancellationTokenSource.IsCancellationRequested;
                ClearPendingConnection(
                    tcpClient,
                    cancellationTokenSource);
                ClearConnectionState(cancellationTokenSource);
                _allowVideoFallback = true;
                TryCancel(cancellationTokenSource);
                tcpClient.Close();
                if (receiveLoopTask is not null)
                {
                    await IgnoreDisconnectExceptionAsync(receiveLoopTask).ConfigureAwait(false);
                }
                RemoteSessionRejectedException? sessionRejection =
                    GetSessionRejection(remoteCapabilitiesReady);
                session?.Dispose();
                tcpClient.Dispose();
                cancellationTokenSource.Dispose();
                if (sessionRejection is not null)
                {
                    throw sessionRejection;
                }

                if (timedOut)
                {
                    throw new TimeoutException(
                        "连接或认证超时，请检查 IP、端口、防火墙和被控端状态。",
                        ex);
                }

                throw new OperationCanceledException(
                    "连接已取消。",
                    ex,
                    cancellationToken.IsCancellationRequested
                        ? cancellationToken
                        : new CancellationToken(canceled: true));
            }
            catch
            {
                ClearPendingConnection(
                    tcpClient,
                    cancellationTokenSource);
                ClearConnectionState(cancellationTokenSource);
                _allowVideoFallback = true;
                TryCancel(cancellationTokenSource);
                tcpClient.Close();
                if (receiveLoopTask is not null)
                {
                    await IgnoreDisconnectExceptionAsync(receiveLoopTask).ConfigureAwait(false);
                }
                RemoteSessionRejectedException? sessionRejection =
                    GetSessionRejection(remoteCapabilitiesReady);
                session?.Dispose();
                tcpClient.Dispose();
                cancellationTokenSource.Dispose();
                if (sessionRejection is not null)
                {
                    throw sessionRejection;
                }

                throw;
            }
            finally
            {
                ClearPendingConnection(
                    tcpClient,
                    cancellationTokenSource);
                closeOnCancellation.Dispose();
                connectionAttemptCancellation?.Dispose();
                connectTimeout?.Dispose();
            }

        }
        finally
        {
            _disconnectLock.Release();
        }

        if (videoCapabilityLog is not null)
        {
            Log?.Invoke(videoCapabilityLog);
        }

        if (connectedLog is not null)
        {
            Log?.Invoke(connectedLog);
        }

        return new ConnectAttemptResult(
            ConnectAttemptOutcome.NewConnection,
            InputConnectionGeneration);
    }

    private bool HasPublishedConnectionStateNoLock()
    {
        return _tcpClient is not null ||
            _stream is not null ||
            _session is not null ||
            _cancellationTokenSource is not null ||
            _receiveLoopTask is not null ||
            _inputLoopTask is not null ||
            _heartbeatLoopTask is not null ||
            _lowLatencyVideoTransport is not null;
    }

    private static RemoteSessionRejectedException? GetSessionRejection(
        TaskCompletionSource<bool> remoteCapabilitiesReady)
    {
        if (!remoteCapabilitiesReady.Task.IsFaulted)
        {
            return null;
        }

        return remoteCapabilitiesReady.Task.Exception?
            .Flatten()
            .InnerExceptions
            .OfType<RemoteSessionRejectedException>()
            .FirstOrDefault();
    }

    private bool IsCurrentConnection(CancellationTokenSource? ownerConnection)
    {
        lock (_connectionStateLock)
        {
            return ownerConnection is not null && ReferenceEquals(_cancellationTokenSource, ownerConnection);
        }
    }

    internal LowLatencyMouseInputLatencySnapshot
        CollectUdpMouseInputLatencySnapshot() =>
            Volatile.Read(
                    ref _lowLatencyVideoTransport)?
                .CollectMouseInputLatencySnapshot() ??
            default;

    public async Task DisconnectAsync()
    {
        await DisconnectCoreAsync(
                expectedConnectionGeneration: null)
            .ConfigureAwait(false);
    }

    // Reconnect work runs concurrently with manual connect/disconnect actions.
    // A stale reconnect must only tear down the generation it created; a global
    // DisconnectAsync call could otherwise close a newer manual connection.
    internal async Task<bool> DisconnectIfCurrentGenerationAsync(
        long expectedConnectionGeneration)
    {
        return await DisconnectCoreAsync(
                expectedConnectionGeneration)
            .ConfigureAwait(false);
    }

    private async Task<bool> DisconnectCoreAsync(
        long? expectedConnectionGeneration)
    {
        bool notifyDisconnected = false;
        if (expectedConnectionGeneration is null)
        {
            CancelPendingConnection();
        }

        await _disconnectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (expectedConnectionGeneration is { } expected &&
                !IsCurrentInputConnectionGeneration(expected))
            {
                return false;
            }

            TcpClient? tcpClient;
            SecureSession? session;
            CancellationTokenSource? cancellationTokenSource;
            Task? receiveLoopTask;
            Task? inputLoopTask;
            Task? heartbeatLoopTask;
            LowLatencyVideoViewerTransport? lowLatencyVideoTransport;
            bool hadConnection;
            lock (_connectionStateLock)
            {
                tcpClient = _tcpClient;
                session = _session;
                cancellationTokenSource = _cancellationTokenSource;
                receiveLoopTask = _receiveLoopTask;
                inputLoopTask = _inputLoopTask;
                heartbeatLoopTask = _heartbeatLoopTask;
                lowLatencyVideoTransport = _lowLatencyVideoTransport;
                hadConnection = HasPublishedConnectionStateNoLock();

                _tcpClient = null;
                _stream = null;
                _session = null;
                _cancellationTokenSource = null;
                _receiveLoopTask = null;
                _inputLoopTask = null;
                _heartbeatLoopTask = null;
                _lowLatencyVideoTransport = null;
                _allowVideoFallback = true;
                // The notification runs on another thread: retire peer capabilities
                // and operation reservations before subscribers observe disconnection.
                ResetRemotePeerState();
                if (cancellationTokenSource is not null &&
                    ReferenceEquals(_connectedEventOwner, cancellationTokenSource))
                {
                    _connectedEventOwner = null;
                    EnqueueConnectedChanged(false);
                }
            }
            CompleteActiveReturnedClipboardFileRequest(
                CreateReturnedClipboardFileBatchResult(
                    ReturnedClipboardFileBatchOutcome.Disconnected,
                    "连接已断开，远端文件回传未完成。"));
            CancelReturnedClipboardFileBatchIfNoPendingCommit();
            ClearInputQueueAndFailFlushWaiters("连接已断开，输入命令未能全部发送。");

            _incomingFileReceiver.AbortActiveTransfer();
            Task? lowLatencyVideoDisposeTask =
                lowLatencyVideoTransport?.DisposeAsync().AsTask();
            TryCancel(cancellationTokenSource);
            ResetHeartbeatMetrics();
            ReleaseInputLoop();
            tcpClient?.Close();

            if (receiveLoopTask is not null)
            {
                await IgnoreDisconnectExceptionAsync(receiveLoopTask).ConfigureAwait(false);
            }

            if (inputLoopTask is not null)
            {
                await IgnoreDisconnectExceptionAsync(inputLoopTask).ConfigureAwait(false);
            }

            if (heartbeatLoopTask is not null)
            {
                await IgnoreDisconnectExceptionAsync(heartbeatLoopTask).ConfigureAwait(false);
            }

            if (lowLatencyVideoDisposeTask is not null)
            {
                await IgnoreDisconnectExceptionAsync(lowLatencyVideoDisposeTask).ConfigureAwait(false);
            }

            // A control handler can already be in flight when DisconnectAsync detaches the owner.
            // Wait for that receive loop above, then reset the shared peer/receiver state once more
            // so a late old-generation DeviceInfo/FileChunk cannot survive into the next connect.
            lock (_connectionStateLock)
            {
                if (!HasPublishedConnectionStateNoLock())
                {
                    ResetRemotePeerState();
                }
            }
            CompleteActiveReturnedClipboardFileRequest(
                CreateReturnedClipboardFileBatchResult(
                    ReturnedClipboardFileBatchOutcome.Disconnected,
                    "连接已断开，远端文件回传未完成。"));
            CancelReturnedClipboardFileBatchIfNoPendingCommit();
            _incomingFileReceiver.AbortActiveTransfer();

            session?.Dispose();
            cancellationTokenSource?.Dispose();

            if (hadConnection)
            {
                notifyDisconnected = true;
            }
        }
        finally
        {
            _disconnectLock.Release();
        }

        if (notifyDisconnected)
        {
            Log?.Invoke("连接已断开。");
        }

        return true;
    }

    private bool ClearConnectionState(CancellationTokenSource expectedConnection)
    {
        lock (_connectionStateLock)
        {
            if (!ReferenceEquals(_cancellationTokenSource, expectedConnection))
            {
                return false;
            }

            _tcpClient = null;
            _stream = null;
            _session = null;
            _cancellationTokenSource = null;
            _receiveLoopTask = null;
            _inputLoopTask = null;
            _heartbeatLoopTask = null;
            _lowLatencyVideoTransport = null;
            _allowVideoFallback = true;
            ResetRemotePeerState();
            if (ReferenceEquals(_connectedEventOwner, expectedConnection))
            {
                _connectedEventOwner = null;
                EnqueueConnectedChanged(false);
            }
        }
        CompleteActiveReturnedClipboardFileRequest(
            CreateReturnedClipboardFileBatchResult(
                ReturnedClipboardFileBatchOutcome.Disconnected,
                "连接已断开，远端文件回传未完成。"));
        CancelReturnedClipboardFileBatchIfNoPendingCommit();
        ResetHeartbeatMetrics();
        ClearInputQueueAndFailFlushWaiters("连接已断开，输入命令未能全部发送。");

        _incomingFileReceiver.AbortActiveTransfer();
        return true;
    }

    private void PublishPendingConnection(
        TcpClient tcpClient,
        CancellationTokenSource cancellationTokenSource)
    {
        lock (_connectionStateLock)
        {
            _pendingTcpClient = tcpClient;
            _pendingConnectionCancellationTokenSource =
                cancellationTokenSource;
        }
    }

    private void ClearPendingConnection(
        TcpClient tcpClient,
        CancellationTokenSource cancellationTokenSource)
    {
        lock (_connectionStateLock)
        {
            if (ReferenceEquals(_pendingTcpClient, tcpClient) &&
                ReferenceEquals(
                    _pendingConnectionCancellationTokenSource,
                    cancellationTokenSource))
            {
                _pendingTcpClient = null;
                _pendingConnectionCancellationTokenSource = null;
            }
        }
    }

    private void CancelPendingConnection()
    {
        TcpClient? tcpClient;
        CancellationTokenSource? cancellationTokenSource;
        lock (_connectionStateLock)
        {
            tcpClient = _pendingTcpClient;
            cancellationTokenSource =
                _pendingConnectionCancellationTokenSource;
        }

        TryCancel(cancellationTokenSource);
        TryCloseTcpClient(tcpClient);
    }

    private static bool IsConnectionAttemptCancellation(
        Exception exception,
        CancellationTokenSource? connectionAttemptCancellation)
    {
        return (exception is OperationCanceledException ||
                exception is IOException ||
                exception is SocketException ||
                exception is ObjectDisposedException) &&
            connectionAttemptCancellation?.IsCancellationRequested == true;
    }

    private static void TryCloseTcpClient(TcpClient? tcpClient)
    {
        try
        {
            tcpClient?.Close();
        }
        catch (Exception ex) when (
            ex is ObjectDisposedException or SocketException)
        {
        }
    }

    private void NotifyConnectedChanged(bool connected)
    {
        try
        {
            ConnectedChanged?.Invoke(connected);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"连接状态通知失败：{ex.Message}");
        }
    }

    private void EnqueueConnectedChanged(bool connected)
    {
        lock (_connectedChangedQueueLock)
        {
            Task previous = _connectedChangedQueueTail;
            _connectedChangedQueueTail = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                }
                catch
                {
                    // Subscriber failures must not break ordering for later lifecycle events.
                }

                NotifyConnectedChanged(connected);
            });
        }
    }

    private void ResetRemotePeerState()
    {
        ResetNativeConnection();
        Interlocked.Exchange(ref _remoteCapabilitiesReady, null)?.TrySetResult(false);
        Volatile.Write(ref _remoteCapabilitiesInitialized, 0);
        _remoteCapabilities = RemoteDeviceCapabilities.None;
        _remoteBuildStamp = null;
        _remoteDeviceInfo = null;
        _deviceIdentityRequested = false;
        Volatile.Write(
            ref _latestCaptureTargetAvailability,
            null);
        _incomingFileReceiver.RequireChecksum = false;
        lock (_selfUpdateLock)
        {
            _selfUpdatePackageRequested = false;
            _pendingSelfUpdateTransferId = null;
        }
    }

    public Task SendInputAsync(RemoteInputCommand command)
    {
        _ = TryQueueOwnedInput(command);
        return Task.CompletedTask;
    }

    internal Task<bool> RequestHostVideoDiagnosticsAsync() =>
        _remoteCapabilities.HasFlag(RemoteDeviceCapabilities.HostVideoDiagnostics)
            ? SendControlAsync(RemoteMessageCodec.EncodeHostVideoDiagnosticsRequest())
            : Task.FromResult(false);

    internal RemoteInputQueueAdmission TryQueueOwnedInput(
        RemoteInputCommand command)
    {
        CancellationTokenSource? expectedConnection =
            _cancellationTokenSource;
        bool queuedForTcp = false;
        long connectionGeneration;
        lock (_inputLock)
        {
            connectionGeneration =
                _inputConnectionGeneration;
            if (CanQueueInput(expectedConnection))
            {
                queuedForTcp =
                    TryQueueInputLocked(command);
            }
        }

        if (queuedForTcp)
        {
            ReleaseInputLoop();
        }

        return new RemoteInputQueueAdmission(
            queuedForTcp,
            connectionGeneration);
    }

    internal bool TryQueueOwnedInput(
        RemoteInputCommand command,
        long expectedConnectionGeneration)
    {
        CancellationTokenSource? expectedConnection =
            _cancellationTokenSource;
        bool queuedForTcp = false;
        lock (_inputLock)
        {
            if (_inputConnectionGeneration ==
                    expectedConnectionGeneration &&
                CanQueueInput(expectedConnection))
            {
                queuedForTcp =
                    TryQueueInputLocked(command);
            }
        }

        if (queuedForTcp)
        {
            ReleaseInputLoop();
        }

        return queuedForTcp;
    }

    internal Task SendInputsAsync(IReadOnlyList<RemoteInputCommand> commands, long? expectedGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0)
        {
            return Task.CompletedTask;
        }

        CancellationTokenSource? expectedConnection = _cancellationTokenSource;
        bool queuedForTcp = false;
        lock (_inputLock)
        {
            if ((!expectedGeneration.HasValue || expectedGeneration.Value == _inputConnectionGeneration) &&
                CanQueueInput(expectedConnection))
            {
                foreach (RemoteInputCommand command in commands)
                {
                    queuedForTcp |=
                        TryQueueInputLocked(command);
                }
            }
        }

        // Wake the sender once for the whole logical gesture. Releasing once per key event leaves
        // redundant semaphore counts that the input loop has to drain after the batch is written.
        if (queuedForTcp)
        {
            ReleaseInputLoop();
        }

        return Task.CompletedTask;
    }

    private bool TryQueueInputLocked(
        RemoteInputCommand command)
    {
        InvalidateNativeDetails();
        if (command.Kind == RemoteInputKind.MouseMove &&
            _pendingReliablePointerInputs == 0 &&
            Environment.TickCount64 >=
                _reliablePointerInputGateUntil)
        {
            LowLatencyVideoViewerTransport? transport =
                Volatile.Read(
                    ref _lowLatencyVideoTransport);
            if (transport?.TryQueueMouseMove(command) == true)
            {
                ObserveUdpMouseRouteLocked(active: true);
                // A TCP move queued before UDP activation must not arrive
                // later and pull the remote pointer back to stale
                // coordinates.
                _inputQueue.RemovePendingMouseMoves();
                return false;
            }

            ObserveUdpMouseRouteLocked(active: false);
        }

        bool queued =
            _inputQueue.Enqueue(
                command,
                MaxQueuedInputs);
        if (queued &&
            IsReliablePointerInput(command))
        {
            _pendingReliablePointerInputs++;
            ObserveUdpMouseRouteLocked(active: false);
            Volatile.Read(
                ref _lowLatencyVideoTransport)?
                .DiscardPendingMouseMove();
        }

        return queued;
    }

    private void ObserveUdpMouseRouteLocked(
        bool active)
    {
        int next = active ? 1 : 0;
        if (_udpMouseRouteObserved == next)
        {
            return;
        }

        _udpMouseRouteObserved = next;
        Interlocked.Increment(
            ref _mouseRouteGeneration);
    }

    private static bool IsReliablePointerInput(
        RemoteInputCommand command) =>
        command.Kind is
            RemoteInputKind.MouseDown or
            RemoteInputKind.MouseUp or
            RemoteInputKind.MouseWheel;

    internal Task FlushInputAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        TaskCompletionSource<bool> completion;
        lock (_inputLock)
        {
            if (!CanQueueInput())
            {
                return Task.FromException(new IOException("连接已断开，无法确认输入命令已发送。"));
            }

            if (_inputQueue.Count == 0 && !_inputWriteInProgress)
            {
                return Task.CompletedTask;
            }

            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _inputFlushWaiters.Add(completion);
        }

        ReleaseInputLoop();
        return cancellationToken.CanBeCanceled
            ? WaitForInputFlushAsync(completion, cancellationToken)
            : completion.Task;
    }

    private async Task WaitForInputFlushAsync(
        TaskCompletionSource<bool> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_inputLock)
            {
                _inputFlushWaiters.Remove(completion);
            }

            throw;
        }
    }

    public RemoteTextInputResult SendTextInput(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new RemoteTextInputResult(0, false);
        }

        CancellationTokenSource? expectedConnection = _cancellationTokenSource;
        RemoteTextInputResult result;
        lock (_inputLock)
        {
            if (!CanQueueInput(expectedConnection))
            {
                return new RemoteTextInputResult(0, false);
            }

            InvalidateNativeDetails();
            result = QueueTextInput(_inputQueue, text);
        }

        if (result.SentCodePoints > 0)
        {
            ReleaseInputLoop();
        }

        return result;
    }

    internal static RemoteTextInputResult QueueTextInput(RemoteInputQueue inputQueue, string text)
    {
        ArgumentNullException.ThrowIfNull(inputQueue);
        if (string.IsNullOrEmpty(text))
        {
            return new RemoteTextInputResult(0, false);
        }

        int sent = 0;
        bool truncated = false;
        foreach (int codePoint in EnumerateTextInputCodePoints(text))
        {
            if (sent >= MaxTextInputCodePoints ||
                !inputQueue.Enqueue(RemoteInputCommand.TextInput(codePoint), MaxQueuedInputs))
            {
                truncated = true;
                break;
            }

            sent++;
        }

        return new RemoteTextInputResult(sent, truncated);
    }

    public async Task SendLocalClipboardToRemoteAsync()
    {
        CancellationTokenSource? owner = _cancellationTokenSource;
        string text = await ClipboardTextService.GetTextAsync();
        if (!IsCurrentConnection(owner)) return;
        if (string.IsNullOrEmpty(text))
        {
            ClipboardStatusReceived?.Invoke("本机剪贴板没有文本。");
            return;
        }

        await SendClipboardTextToRemoteAsync(text, "已写入远端文本剪贴板。");
    }

    private readonly ClipboardRequestTracker _clipboardRequests = new();
    private int _manualClipboardReplyTimeoutMilliseconds = ClipboardRequestTracker.TimeoutMilliseconds;

    public async Task<bool> SendClipboardTextToRemoteAsync(string text, string? successMessage = null,
        bool forPasteShortcut = false, long? expectedGeneration = null)
    {
        CancellationTokenSource? owner = _cancellationTokenSource;
        long requestGeneration = InputConnectionGeneration;
        if (!IsCurrentConnection(owner) ||
            (expectedGeneration is { } generation && requestGeneration != generation)) return false;
        if (string.IsNullOrEmpty(text))
        {
            ClipboardStatusReceived?.Invoke("本机剪贴板没有文本。");
            return false;
        }

        byte[] payload = RemoteMessageCodec.EncodeClipboardSetText(text);
        if (!IsCurrentConnection(owner) || InputConnectionGeneration != requestGeneration) return false;
        ClipboardRequestTracker.Request? request = _clipboardRequests.Begin(requestGeneration, read: false,
            timeoutMilliseconds: forPasteShortcut ? ClipboardRequestTracker.TimeoutMilliseconds
                : _manualClipboardReplyTimeoutMilliseconds);
        if (request is null)
        {
            WindowsDiagnosticLog.CreateDefault().Append("CLIPBOARD", "write:busy waiting for previous reply");
            ClipboardStatusReceived?.Invoke("上一项剪贴板操作仍在等待远端，请稍后重试；长时间无响应请重连。");
            return false;
        }
        if (!await SendControlAsync(payload, owner))
        {
            _clipboardRequests.Take(request.Generation, textReply: false, success: false)?.Completion.TrySetResult(false);
            ClipboardStatusReceived?.Invoke("连接已断开，无法发送文本剪贴板。");
            return false;
        }
        bool success = await WaitForClipboardReplyAsync(request, owner);
        if (success && IsCurrentConnection(owner) && successMessage != string.Empty)
            ClipboardStatusReceived?.Invoke(successMessage ?? "已写入远端文本剪贴板。");
        return success && IsCurrentConnection(owner);
    }

    public Task ReadRemoteClipboardAsync(bool notifyRequest = true) =>
        ReadRemoteClipboardCoreAsync(notifyRequest, waitForReply: false);

    internal Task<bool> ReadRemoteClipboardAndWaitAsync(bool notifyRequest = false) =>
        ReadRemoteClipboardCoreAsync(notifyRequest, waitForReply: true);

    private async Task<bool> ReadRemoteClipboardCoreAsync(bool notifyRequest, bool waitForReply)
    {
        CancellationTokenSource? owner = _cancellationTokenSource;
        ClipboardRequestTracker.Request? request = _clipboardRequests.Begin(
            InputConnectionGeneration, read: true, ClipboardTextService.ReadClipboardSequenceNumber(),
            timeoutMilliseconds: _manualClipboardReplyTimeoutMilliseconds);
        if (request is null)
        {
            if (notifyRequest) ClipboardStatusReceived?.Invoke("上一项剪贴板操作仍在等待远端，请稍后重试；长时间无响应请重连。");
            return false;
        }
        if (await SendControlAsync(RemoteMessageCodec.EncodeClipboardGetText(), owner))
        {
            if (notifyRequest)
            {
                ClipboardStatusReceived?.Invoke("已请求读取远程文本剪贴板。");
            }
            Task<bool> reply = WaitForClipboardReplyAsync(request, owner);
            return !waitForReply || await reply;
        }
        else
        {
            _clipboardRequests.Take(request.Generation, textReply: false, success: false)?.Completion.TrySetResult(false);
        }
        return false;
    }

    private async Task<bool> WaitForClipboardReplyAsync(ClipboardRequestTracker.Request request, CancellationTokenSource? owner)
    {
        void Trace(string state) => WindowsDiagnosticLog.CreateDefault().Append("CLIPBOARD",
            $"{(request.Read ? "read" : "write")}:{state} generation={request.Generation} revision={request.Revision} elapsedMs={request.ReplyTimeoutMilliseconds - request.RemainingTimeoutMilliseconds}");
        try
        {
            if (request.ReplyTimeoutMilliseconds > ClipboardRequestTracker.TimeoutMilliseconds &&
                !request.Completion.Task.IsCompleted && IsCurrentConnection(owner))
                ClipboardStatusReceived?.Invoke("正在等待远端剪贴板确认（公网慢链路最多等待 30 秒）。");
            bool success = await request.Completion.Task.WaitAsync(TimeSpan.FromMilliseconds(
                request.RemainingTimeoutMilliseconds), owner?.Token ?? new CancellationToken(true));
            Trace(success ? "acknowledged" : "not-applied");
            return success;
        }
        catch (TimeoutException)
        {
            Trace("timed-out");
            if (IsCurrentConnection(owner))
                ClipboardStatusReceived?.Invoke("等待远端剪贴板超时：未覆盖本机内容，也未触发粘贴。请稍后重试或重新连接。");
            return false;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            Trace("cancelled");
            return false;
        }
    }

    public async Task<bool> RequestRemoteClipboardFilesAsync(bool notifyRequest = true)
    {
        if (!_incomingFileReceiver.HasPendingReturnedClipboardFileCommit)
        {
            await WaitForRemoteCapabilityNegotiationAsync(CancellationToken.None);
        }

        if (!_incomingFileReceiver.HasPendingReturnedClipboardFileCommit &&
            !TryValidateRemoteClipboardFileRequest(
                out _,
                out string unavailableMessage))
        {
            if (notifyRequest)
            {
                FileTransferStatusReceived?.Invoke(false, unavailableMessage);
            }

            return false;
        }

        if (!TryBeginReturnedClipboardFileRequest(
            ReturnedClipboardFileRequestMode.Standard,
            out ReturnedClipboardFileRequestContext request))
        {
            if (notifyRequest)
            {
                FileTransferStatusReceived?.Invoke(true, "远端文件正在拉取中，无需重复请求。");
            }

            return true;
        }

        if (_incomingFileReceiver.HasPendingReturnedClipboardFileCommit)
        {
            ReturnedClipboardFinalizeResult retryResult =
                await TryCommitPendingReturnedClipboardFilesAsync(notifyRequest);
            CompleteReturnedClipboardFileRequest(
                request,
                CreateReturnedClipboardFileBatchResult(retryResult));
            return retryResult.Success;
        }

        if (!TryPrepareReturnedClipboardFileBatch(request))
        {
            return false;
        }

        bool requestSent = false;
        try
        {
            requestSent = await SendControlAsync(
                RemoteMessageCodec.EncodeFileTransferRequestClipboardFiles(),
                request.OwnerConnection);
        }
        finally
        {
            request.CompleteRequestSend(requestSent);
        }

        if (requestSent)
        {
            if (request.IsRejectRequested)
            {
                await TryRejectReturnedClipboardFilePreviewAsync(request);
            }

            if (!IsActiveReturnedClipboardFileRequest(request))
            {
                return true;
            }

            if (notifyRequest)
            {
                FileTransferStatusReceived?.Invoke(true, "已请求远端回传剪贴板文件。");
            }

            return true;
        }

        CancelReturnedClipboardFileBatchIfActive(request);
        const string disconnectedMessage = "连接已断开，无法请求远端文件。";
        CompleteReturnedClipboardFileRequest(
            request,
            CreateReturnedClipboardFileBatchResult(
                ReturnedClipboardFileBatchOutcome.Disconnected,
                disconnectedMessage));
        if (notifyRequest)
        {
            FileTransferStatusReceived?.Invoke(false, disconnectedMessage);
        }

        return false;
    }

    public async Task<ReturnedClipboardFileBatchResult> RequestRemoteClipboardFilesForDragOutAsync(
        CancellationToken cancellationToken = default,
        bool notifyRequest = true)
    {
        const string cancelledMessage = "已取消从远程画面拖出文件。";
        if (cancellationToken.IsCancellationRequested)
        {
            return CreateReturnedClipboardFileBatchResult(
                ReturnedClipboardFileBatchOutcome.Cancelled,
                cancelledMessage);
        }

        try
        {
            await WaitForRemoteCapabilityNegotiationAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CreateReturnedClipboardFileBatchResult(
                ReturnedClipboardFileBatchOutcome.Cancelled,
                cancelledMessage);
        }

        if (!TryValidateRemoteClipboardFileRequest(
                out ReturnedClipboardFileBatchOutcome unavailableOutcome,
                out string unavailableMessage))
        {
            if (notifyRequest)
            {
                FileTransferStatusReceived?.Invoke(false, unavailableMessage);
            }

            return CreateReturnedClipboardFileBatchResult(
                unavailableOutcome,
                unavailableMessage);
        }

        if (!TryBeginReturnedClipboardFileRequest(
            ReturnedClipboardFileRequestMode.DragOut,
            out ReturnedClipboardFileRequestContext request))
        {
            const string busyMessage = "远端文件正在拉取中，请等待当前操作完成后再拖出。";
            if (notifyRequest)
            {
                FileTransferStatusReceived?.Invoke(false, busyMessage);
            }

            return CreateReturnedClipboardFileBatchResult(
                ReturnedClipboardFileBatchOutcome.Busy,
                busyMessage);
        }

        // A drag gesture always represents a new explicit selection. Do not reuse files retained
        // from an earlier clipboard-write retry, because that could expose stale local paths.
        if (!TryPrepareReturnedClipboardFileBatch(request))
        {
            return request.Completion.Task.IsCompleted
                ? await request.Completion.Task.ConfigureAwait(false)
                : CreateReturnedClipboardFileBatchResult(
                    ReturnedClipboardFileBatchOutcome.Disconnected,
                    "连接已切换，无法从远程画面拖出文件。");
        }

        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(
            () => CancelReturnedClipboardFileWait(request));
        if (request.IsWaitCancelled)
        {
            request.CompleteRequestSend(sent: false);
            ReturnedClipboardFileBatchResult cancelledResult =
                CreateReturnedClipboardFileBatchResult(
                    ReturnedClipboardFileBatchOutcome.Cancelled,
                    cancelledMessage);
            CompleteReturnedClipboardFileRequest(request, cancelledResult);
            return cancelledResult;
        }

        bool requestSent = false;
        try
        {
            requestSent = await SendControlAsync(
                RemoteMessageCodec.EncodeFileTransferRequestClipboardFiles(),
                request.OwnerConnection);
        }
        finally
        {
            request.CompleteRequestSend(requestSent);
        }

        if (!requestSent)
        {
            CancelReturnedClipboardFileBatchIfActive(request);
            const string disconnectedMessage = "连接已断开，无法从远程画面拖出文件。";
            ReturnedClipboardFileBatchResult disconnectedResult =
                CreateReturnedClipboardFileBatchResult(
                    ReturnedClipboardFileBatchOutcome.Disconnected,
                    disconnectedMessage);
            CompleteReturnedClipboardFileRequest(request, disconnectedResult);
            if (notifyRequest)
            {
                FileTransferStatusReceived?.Invoke(false, disconnectedMessage);
            }

            return disconnectedResult;
        }

        if (request.IsWaitCancelled || request.IsRejectRequested)
        {
            await TryRejectReturnedClipboardFilePreviewAsync(request);
        }

        if (!IsActiveReturnedClipboardFileRequest(request))
        {
            return request.Completion.Task.IsCompleted
                ? await request.Completion.Task.ConfigureAwait(false)
                : CreateReturnedClipboardFileBatchResult(
                    ReturnedClipboardFileBatchOutcome.Disconnected,
                    "连接已切换，无法从远程画面拖出文件。");
        }

        if (notifyRequest)
        {
            FileTransferStatusReceived?.Invoke(true, "正在准备从远程画面拖出文件。");
        }

        try
        {
            return await request.Completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (request.Completion.Task.IsCompleted)
            {
                return await request.Completion.Task.ConfigureAwait(false);
            }

            CancelReturnedClipboardFileWait(request);
            // Keep the wire request reserved until its preview or terminal status arrives. The
            // protocol has no batch id, so retiring it here could let a late preview bind to a
            // newer drag request. HandleRemoteClipboardFilePreviewAsync observes the cancellation
            // marker, rejects that exact pending preview, and then releases the reservation.
            return CreateReturnedClipboardFileBatchResult(
                ReturnedClipboardFileBatchOutcome.Cancelled,
                cancelledMessage);
        }
    }

    public async Task UpdateViewerVideoCodecsAsync(RemoteVideoCodecs supportedVideoCodecs)
    {
        if (await SendControlAsync(RemoteMessageCodec.EncodeViewerInfo(supportedVideoCodecs)))
        {
            Log?.Invoke($"已更新查看端视频能力：{FormatVideoCodecs(supportedVideoCodecs)}");
        }
    }

    public async Task RequestVideoKeyFrameAsync()
    {
        await SendControlAsync(RemoteMessageCodec.EncodeVideoKeyFrameRequest());
    }

    public async Task SendFileToRemoteAsync(string path)
    {
        await _fileTransferLock.WaitAsync();
        try
        {
            CancellationTokenSource ownerConnection = RequireActiveFileTransferConnection();
            RemoteFilePasteItem item = Directory.Exists(path)
                ? new RemoteFilePasteItem(path, RemoteFilePasteItemKind.Directory)
                : new RemoteFilePasteItem(path, RemoteFilePasteItemKind.File);
            await SendTransferItemToRemoteCoreAsync(item, ownerConnection);
        }
        finally
        {
            ReleaseFileTransferLock();
        }
    }

    public async Task SendRemoteUpdateAsync(string packagePath)
    {
        ThrowIfRemoteClipboardFileRequestPendingForUpdate();
        if (!_remoteCapabilities.HasFlag(RemoteDeviceCapabilities.RemoteUpdate))
        {
            throw new InvalidOperationException("远程设备未声明自更新能力。");
        }

        if (!_remoteCapabilities.HasFlag(RemoteDeviceCapabilities.FileChecksum))
        {
            throw new InvalidOperationException("远程更新要求远端支持 SHA-256 文件校验。");
        }

        if (!_remoteCapabilities.HasFlag(RemoteDeviceCapabilities.FileTransferCancel))
        {
            throw new InvalidOperationException("远程更新要求远端支持传输取消与临时文件清理。");
        }

        RemoteUpdater.ValidateRemoteUpdatePackageName(packagePath);
        await _fileTransferLock.WaitAsync();
        try
        {
            ThrowIfRemoteClipboardFileRequestPendingForUpdate();
            CancellationTokenSource ownerConnection = RequireActiveFileTransferConnection();
            await SendFileToRemoteCoreAsync(
                packagePath,
                remoteUpdate: true,
                ownerConnection: ownerConnection);
        }
        finally
        {
            ReleaseFileTransferLock();
        }
    }

    public async Task RequestRemoteUpdatePackageAsync()
    {
        ThrowIfRemoteClipboardFileRequestPendingForUpdate();
        if (!RemoteUpdater.CanApplyRemoteUpdate)
        {
            throw new InvalidOperationException("当前控制端不是可自更新的 RemoteDesk.exe。");
        }

        if (!_remoteCapabilities.HasFlag(RemoteDeviceCapabilities.RemoteUpdate))
        {
            throw new InvalidOperationException("远程设备未声明自更新能力。");
        }

        if (!_remoteCapabilities.HasFlag(RemoteDeviceCapabilities.FileSend))
        {
            throw new InvalidOperationException("远程设备未声明文件回传能力，无法拉取更新包。");
        }

        if (!_remoteCapabilities.HasFlag(RemoteDeviceCapabilities.FileChecksum))
        {
            throw new InvalidOperationException("拉取远程更新要求远端支持 SHA-256 文件校验。");
        }

        if (!_remoteCapabilities.HasFlag(RemoteDeviceCapabilities.FileTransferCancel))
        {
            throw new InvalidOperationException("拉取远程更新要求远端支持传输取消与临时文件清理。");
        }

        if (!TryReserveSelfUpdatePackageRequest())
        {
            throw new InvalidOperationException("已有一个本机更新请求正在进行。");
        }

        if (IsRemoteClipboardFileRequestPending)
        {
            ClearPendingSelfUpdateState();
            ThrowIfRemoteClipboardFileRequestPendingForUpdate();
        }

        bool sent = await SendControlAsync(RemoteMessageCodec.EncodeRemoteUpdatePackageRequest());
        if (!sent)
        {
            lock (_selfUpdateLock)
            {
                _selfUpdatePackageRequested = false;
            }

            throw new IOException("连接已断开，无法请求远端回传更新包。");
        }

        FileTransferStatusReceived?.Invoke(true, "已请求远端回传 RemoteDesk.exe 更新包。");
    }

    private void ThrowIfRemoteClipboardFileRequestPendingForUpdate()
    {
        if (IsRemoteClipboardFileRequestPending)
        {
            throw new InvalidOperationException("远端文件回传尚未结束，暂不能同时执行远程更新。");
        }
    }

    public async Task<RemoteFilePasteResult> SendFilesToRemoteAsync(IEnumerable<string> paths, int maxFiles)
    {
        RemoteFilePastePlan plan = CreateFilePastePlan(
            paths,
            maxFiles,
            File.Exists,
            Directory.Exists,
            includeDirectories: true);

        return await SendFilePastePlanToRemoteAsync(plan);
    }

    public async Task<RemoteFilePasteResult> SendFilePastePlanToRemoteAsync(
        RemoteFilePastePlan plan,
        string failurePrefix = "粘贴文件失败",
        long? expectedGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        long generation = expectedGeneration ?? InputConnectionGeneration;
        await _fileTransferLock.WaitAsync();
        try
        {
            CancellationTokenSource ownerConnection = RequireActiveFileTransferConnection();
            if (InputConnectionGeneration != generation)
                throw new IOException("连接已改变，原文件确认已失效，请重新确认文件和接收位置。");
            return await SendFilesToRemoteCoreAsync(plan, failurePrefix, ownerConnection);
        }
        finally
        {
            ReleaseFileTransferLock();
        }
    }

    public async Task<RemoteFileDropPasteResult> SendFilesToRemoteDropPasteAsync(IEnumerable<string> paths, int maxFiles)
    {
        RemoteFilePastePlan plan = CreateFilePastePlan(
            paths,
            maxFiles,
            File.Exists,
            Directory.Exists,
            includeDirectories: true);

        return await SendFilePastePlanToRemoteDropPasteAsync(plan);
    }

    public async Task<RemoteFileDropPasteResult> SendFilePastePlanToRemoteDropPasteAsync(RemoteFilePastePlan plan, long? expectedGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        long generation = expectedGeneration ?? InputConnectionGeneration;
        await _fileTransferLock.WaitAsync();
        bool batchStarted = false;
        bool batchClosed = false;
        CancellationTokenSource? ownerConnection = null;
        try
        {
            ownerConnection = RequireActiveFileTransferConnection();
            if (InputConnectionGeneration != generation)
                throw new IOException("连接已改变，原文件确认已失效，请重新确认文件和接收位置。");
            if (plan.Files.Count > 0)
            {
                if (!await SendControlAsync(
                        RemoteMessageCodec.EncodeFileDropPasteBegin(),
                        ownerConnection))
                {
                    throw new IOException("连接已断开，无法准备远端当前位置粘贴。");
                }

                batchStarted = true;
            }

            RemoteFilePasteResult result = await SendFilesToRemoteCoreAsync(
                plan,
                "拖放文件失败",
                ownerConnection);
            bool remotePasteRequested = false;
            if (batchStarted && result.SentFiles > 0)
            {
                remotePasteRequested = await SendControlAsync(
                    RemoteMessageCodec.EncodeFileDropPasteCommit(),
                    ownerConnection);
                batchClosed = remotePasteRequested;
                if (!remotePasteRequested)
                {
                    FileTransferStatusReceived?.Invoke(false, "文件已保存到被控端接收目录，但未能请求远端当前位置粘贴。");
                }
            }
            else if (batchStarted)
            {
                batchClosed = await SendControlAsync(
                    RemoteMessageCodec.EncodeFileDropPasteCancel(),
                    ownerConnection);
            }

            return new RemoteFileDropPasteResult(result, remotePasteRequested);
        }
        finally
        {
            if (batchStarted && !batchClosed)
            {
                await TryCancelRemoteFileDropPasteAsync(ownerConnection);
            }

            ReleaseFileTransferLock();
        }
    }

    private async Task<RemoteFilePasteResult> SendFilesToRemoteCoreAsync(
        RemoteFilePastePlan plan,
        string failurePrefix,
        CancellationTokenSource ownerConnection)
    {
        int sentFiles = 0;
        int failedFiles = 0;
        int archivedDirectories = 0;
        string? failureMessage = null;
        var results = new List<RemoteFileItemResult>();
        foreach (RemoteFilePasteItem item in plan.TransferItems)
        {
            try
            {
                if (!IsCurrentConnection(ownerConnection)) throw new IOException("连接已断开或切换，未开始此项传输。");
                var (archivedDirectory, savedMessage) = await SendTransferItemToRemoteCoreAsync(
                    item,
                    ownerConnection);
                results.Add(new(RemoteFileTransfer.GetTransferDisplayName(item.Path), true, savedMessage));
                sentFiles++;
                if (archivedDirectory)
                {
                    archivedDirectories++;
                }
            }
            catch (Exception ex) when (RemoteFileTransfer.IsRecoverableTransferException(ex) || ex is OperationCanceledException)
            {
                failedFiles++;
                string fileName = RemoteFileTransfer.GetTransferDisplayName(item.Path);
                string details = ex is OperationCanceledException ? "连接已结束，保存状态未确认，请检查远端接收目录后再重试。" : ex.Message;
                results.Add(new(fileName, false, details));
                failureMessage ??= $"{fileName}：{details}";
                FileTransferStatusReceived?.Invoke(
                    false,
                    $"{failurePrefix}：{(string.IsNullOrWhiteSpace(fileName) ? item.Path : fileName)} - {ex.Message}");
            }
        }

        return new RemoteFilePasteResult(
            sentFiles,
            failedFiles,
            plan.SkippedDirectories,
            plan.SkippedMissing,
            plan.Truncated,
            archivedDirectories,
            failureMessage,
            results);
    }

    private async Task<(bool Archived, string Message)> SendTransferItemToRemoteCoreAsync(
        RemoteFilePasteItem item,
        CancellationTokenSource ownerConnection)
    {
        string? temporaryArchivePath = null;
        try
        {
            string sourcePath = item.Path;
            string transferFileName = Path.GetFileName(sourcePath);
            bool archivedDirectory = item.Kind == RemoteFilePasteItemKind.Directory;
            if (archivedDirectory)
            {
                if (!Directory.Exists(sourcePath))
                {
                    throw new DirectoryNotFoundException("文件夹不存在。");
                }

                transferFileName = RemoteFileTransfer.CreateDirectoryArchiveFileName(sourcePath);
                FileTransferStatusReceived?.Invoke(true, $"正在打包文件夹：{transferFileName}");
                CancellationToken cancellationToken = ownerConnection.Token;
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentConnection(ownerConnection))
                {
                    throw new IOException("连接已切换，已取消文件夹打包。");
                }

                // Directory enumeration and compression are synchronous and can take minutes.
                // Run them away from the UI continuation while preserving the connection owner
                // token so disconnect/cancel stops both the archive and the subsequent send.
                temporaryArchivePath = await Task.Run(
                    () => RemoteFileTransfer.CreateTemporaryDirectoryArchive(sourcePath, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentConnection(ownerConnection))
                {
                    throw new IOException("连接已切换，已取消文件夹发送。");
                }

                sourcePath = temporaryArchivePath;
            }

            string savedMessage = await SendFileToRemoteCoreAsync(
                sourcePath,
                transferFileName,
                ownerConnection: ownerConnection);
            return (archivedDirectory, savedMessage);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryArchivePath))
            {
                RemoteFileTransfer.TryDeleteTemporaryFile(temporaryArchivePath);
            }
        }
    }

    private async Task<string> SendFileToRemoteCoreAsync(
        string path,
        string? transferFileName = null,
        bool remoteUpdate = false,
        CancellationTokenSource? ownerConnection = null)
    {
        ownerConnection ??= RequireActiveFileTransferConnection();
        CancellationToken cancellationToken = ownerConnection.Token;
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("文件不存在。", path);
        }

        string fileName = string.IsNullOrWhiteSpace(transferFileName)
            ? Path.GetFileName(path)
            : transferFileName.Trim();
        if (remoteUpdate)
        {
            RemoteUpdater.ValidateRemoteUpdatePackageName(fileName);
        }

        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            RemoteMessageCodec.RecommendedFileTransferChunkBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        RemoteFileTransferSourceSnapshot sourceSnapshot = RemoteFileTransfer.CaptureSourceSnapshot(path, input);
        long fileLength = sourceSnapshot.Length;
        if (fileLength > RemoteMessageCodec.MaxFileTransferBytes)
        {
            throw new InvalidOperationException($"文件超过传输上限：{RemoteFileTransfer.FormatBytes(RemoteMessageCodec.MaxFileTransferBytes)}。");
        }

        if (remoteUpdate)
        {
            RemoteUpdater.ValidateRemoteUpdatePackageLength(fileLength);
        }

        string transferId = Guid.NewGuid().ToString("N");
        bool transferStarted = false;
        bool pendingFileDelivery = false;
        bool sendChecksum = _remoteCapabilities.HasFlag(RemoteDeviceCapabilities.FileChecksum);
        bool sendCancel = _remoteCapabilities.HasFlag(RemoteDeviceCapabilities.FileTransferCancel);
        bool requireReceipt = !remoteUpdate && _remoteCapabilities.HasFlag(RemoteDeviceCapabilities.FileTransferReceipt);
        var receipt = new TaskCompletionSource<RemoteControlMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (requireReceipt) _fileReceipts[transferId] = receipt;
        using IncrementalHash? checksum = sendChecksum
            ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            : null;
        string transferAction = remoteUpdate ? "正在发送远程更新包" : "正在发送文件";
        string transferStartedError = remoteUpdate ? "连接已断开，无法发送远程更新包。" : "连接已断开，无法发送文件。";
        BeginOutgoingFileTransfer(ownerConnection);
        try
        {
            FileTransferStatusReceived?.Invoke(true, $"{transferAction}：{fileName} ({RemoteFileTransfer.FormatBytes(fileLength)})");
            byte[] startPayload = remoteUpdate
                ? RemoteMessageCodec.EncodeRemoteUpdateStart(transferId, fileName, fileLength)
                : RemoteMessageCodec.EncodeFileTransferStart(transferId, fileName, fileLength);
            if (!await SendControlAsync(startPayload, ownerConnection))
            {
                throw new IOException(transferStartedError);
            }

            transferStarted = true;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(RemoteMessageCodec.RecommendedFileTransferChunkBytes);
            byte[] chunkPayload = ArrayPool<byte>.Shared.Rent(
                RemoteMessageCodec.GetFileTransferChunkPayloadLength(
                    transferId,
                    RemoteMessageCodec.RecommendedFileTransferChunkBytes));
            long offset = 0;
            int lastReportedPercent = -1;
            try
            {
                while (offset < fileLength)
                {
                    if (requireReceipt && receipt.Task.IsCompletedSuccessfully && !receipt.Task.Result.Success)
                        throw new IOException(receipt.Task.Result.StatusMessage ?? "远端拒绝接收文件。");
                    int bytesToRead = (int)Math.Min(
                        RemoteMessageCodec.RecommendedFileTransferChunkBytes,
                        fileLength - offset);
                    int read = await input.ReadAsync(
                        buffer.AsMemory(0, bytesToRead),
                        cancellationToken);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("文件在传输过程中被截断。");
                    }

                    checksum?.AppendData(buffer.AsSpan(0, read));
                    int payloadLength = RemoteMessageCodec.WriteFileTransferChunkPayload(
                        transferId,
                        offset,
                        buffer.AsSpan(0, read),
                        chunkPayload);
                    if (!await SendControlAsync(
                            chunkPayload.AsMemory(0, payloadLength),
                            ownerConnection))
                    {
                        throw new IOException("连接已断开，文件传输中止。");
                    }

                    offset += read;
                    int percent = fileLength == 0 ? 100 : (int)Math.Min(100, offset * 100 / fileLength);
                    if (percent >= lastReportedPercent + 10 || offset == fileLength)
                    {
                        FileTransferStatusReceived?.Invoke(true, $"{transferAction}：{fileName} {percent}%");
                        lastReportedPercent = percent;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunkPayload);
                ArrayPool<byte>.Shared.Return(buffer);
            }

            RemoteFileTransfer.EnsureSourceUnchanged(path, sourceSnapshot);

            if (checksum is not null)
            {
                string checksumHex = Convert.ToHexString(checksum.GetHashAndReset()).ToLowerInvariant();
                if (!await SendControlAsync(
                        RemoteMessageCodec.EncodeFileTransferChecksum(transferId, checksumHex),
                        ownerConnection))
                {
                    throw new IOException("连接已断开，无法发送文件校验值。");
                }
            }

            if (!await SendControlAsync(
                    RemoteMessageCodec.EncodeFileTransferComplete(transferId),
                    ownerConnection))
            {
                throw new IOException("连接已断开，无法完成文件传输。");
            }

            FileTransferStatusReceived?.Invoke(
                true,
                remoteUpdate
                    ? $"远程更新包已发送，等待被控端校验并重启：{fileName}"
                    : $"文件已发送，等待被控端保存：{fileName}");
            if (requireReceipt)
            {
                RemoteControlMessage result;
                try
                {
                    result = await receipt.Task.WaitAsync(FileSaveConfirmationTimeout, cancellationToken);
                }
                catch (TimeoutException ex)
                {
                    throw new IOException("等待远端保存确认超时，文件可能已保存，请检查接收目录后再重试。", ex);
                }
                if (!result.Success) throw new IOException(result.StatusMessage ?? "远端保存文件失败。");
                if (!IsCurrentConnection(ownerConnection)) throw new IOException("连接已切换，文件传输已结束。");
                FileTransferStatusReceived?.Invoke(true, result.StatusMessage ?? $"远端已保存：{fileName}");
                return result.StatusMessage ?? $"远端已确认保存 {fileName}，但未返回实际路径。";
            }
            // A completed local TCP write may still be buffered in the relay.
            // Legacy receivers and remote updates do not provide a correlated
            // save receipt, so retain only a bounded tail-delivery grace.
            pendingFileDelivery = true;
            return $"已发送 {fileName}；旧版远端未返回保存确认或实际路径，请到被控端检查。";
        }
        catch (Exception ex) when (transferStarted && RemoteFileTransfer.IsRecoverableTransferException(ex))
        {
            await TrySendFileTransferCancelAsync(
                transferId,
                $"发送端中止：{ex.Message}",
                ownerConnection,
                sendCancel);
            throw;
        }
        finally
        {
            EndOutgoingFileTransfer(ownerConnection, pendingFileDelivery);
            _fileReceipts.TryRemove(transferId, out _);
        }
    }

    private async Task TrySendFileTransferCancelAsync(
        string transferId,
        string reason,
        CancellationTokenSource ownerConnection,
        bool sendCancel)
    {
        if (!sendCancel)
        {
            return;
        }

        try
        {
            await SendControlAsync(
                RemoteMessageCodec.EncodeFileTransferCancel(transferId, reason),
                ownerConnection);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task TryCancelRemoteFileDropPasteAsync(
        CancellationTokenSource? ownerConnection)
    {
        try
        {
            if (ownerConnection is not null)
            {
                await SendControlAsync(
                    RemoteMessageCodec.EncodeFileDropPasteCancel(),
                    ownerConnection);
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private CancellationTokenSource RequireActiveFileTransferConnection()
    {
        CancellationTokenSource? ownerConnection = _cancellationTokenSource;
        if (_stream is null || _tcpClient is null || ownerConnection is null ||
            ownerConnection.IsCancellationRequested)
        {
            throw new IOException("连接已断开，无法传输文件。");
        }

        return ownerConnection;
    }

    private void ReleaseFileTransferLock()
    {
        try
        {
            _fileTransferLock.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal static RemoteFilePastePlan CreateFilePastePlan(
        IEnumerable<string> paths,
        int maxFiles,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        bool includeDirectories = false)
    {
        return RemoteFileTransfer.CreatePastePlan(paths, maxFiles, fileExists, directoryExists, includeDirectories);
    }

    private Task<bool> SendControlAsync(ReadOnlyMemory<byte> payload)
    {
        return SendControlAsync(payload, _cancellationTokenSource);
    }

    private async Task<bool> SendControlAsync(
        ReadOnlyMemory<byte> payload,
        CancellationTokenSource? expectedConnection,
        MessageType messageType = MessageType.Control)
    {
        TcpClient? tcpClient = _tcpClient;
        NetworkStream? stream = _stream;
        SecureSession? session = _session;
        CancellationTokenSource? cancellationTokenSource = _cancellationTokenSource;

        if (tcpClient is null || stream is null || session is null || cancellationTokenSource is null ||
            cancellationTokenSource.IsCancellationRequested ||
            !ReferenceEquals(cancellationTokenSource, expectedConnection) ||
            !ReferenceEquals(_tcpClient, tcpClient) ||
            !ReferenceEquals(_stream, stream) ||
            !ReferenceEquals(_session, session) ||
            !ReferenceEquals(_cancellationTokenSource, cancellationTokenSource))
        {
            return false;
        }

        try
        {
            await Protocol.WriteMessageAsync(stream, messageType, payload, session, _writeLock, cancellationTokenSource.Token);
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or CryptographicException)
        {
            TryCancel(cancellationTokenSource);
            tcpClient.Close();
            return false;
        }
    }

    public async Task SelectCaptureTargetAsync(string targetId)
    {
        InvalidateNativeDetails();
        NetworkStream? stream = _stream;
        SecureSession? session = _session;
        CancellationTokenSource? cancellationTokenSource = _cancellationTokenSource;

        if (stream is null || session is null || cancellationTokenSource is null || cancellationTokenSource.IsCancellationRequested)
        {
            return;
        }

        byte[] payload = RemoteMessageCodec.EncodeSelectCaptureTarget(targetId);
        await SendControlAsync(payload);
    }

    public void Dispose()
    {
        bool disconnected = false;
        try
        {
            Task disconnectTask = DisconnectAsync();
            disconnected = disconnectTask.Wait(DisposeDisconnectTimeout);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(IsDisconnectException))
        {
            disconnected = true;
        }
        catch (Exception ex) when (IsDisconnectException(ex))
        {
            disconnected = true;
        }

        _incomingFileReceiver.Dispose();
        NativeDetails.Dispose();
        if (disconnected)
        {
            _writeLock.Dispose();
            _inputSignal.Dispose();
            _fileTransferLock.Dispose();
            _disconnectLock.Dispose();
        }
    }

    private async Task ReceiveLoopAsync(
        TcpClient ownerTcpClient,
        NetworkStream stream,
        SecureSession ownerSession,
        CancellationTokenSource ownerCancellationTokenSource,
        long inputConnectionGeneration,
        TaskCompletionSource<bool> remoteCapabilitiesReady)
    {
        try
        {
            while (!ownerCancellationTokenSource.IsCancellationRequested)
            {
                ProtocolMessage message = await Protocol.ReadMessageAsync(stream, ownerSession, ownerCancellationTokenSource.Token);
                MarkMessageReceived();

                switch (message.Type)
                {
                    case MessageType.NativeDetailUdpResume:
                        try { ReceiveNativeResume(ownerCancellationTokenSource, message.PayloadSpan); }
                        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
                        { Log?.Invoke($"已忽略无效视频恢复边界：{ex.Message}"); }
                        break;
                    case MessageType.NativeDetailOffer:
                        try { ReceiveNativeOffer(ownerCancellationTokenSource, message.PayloadSpan); }
                        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
                        { Log?.Invoke($"已忽略无效原生补清能力：{ex.Message}"); }
                        break;
                    case MessageType.NativeVideoFrame:
                        try { ReceiveNativeBase(ownerCancellationTokenSource, message.PayloadMemory); }
                        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or ObjectDisposedException)
                        { Log?.Invoke($"已忽略无效原生补清底图：{ex.Message}"); }
                        break;
                    case MessageType.NativeDetailChunk:
                        ReceiveNativeChunk(ownerCancellationTokenSource, message.PayloadSpan);
                        break;
                    case MessageType.Frame:
                        try
                        {
                            PublishTcpVideoFrame(
                                ownerCancellationTokenSource,
                                RemoteMessageCodec.DecodeFrame(message.PayloadMemory));
                        }
                        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or ObjectDisposedException)
                        {
                            Log?.Invoke($"已忽略无效画面帧：{ex.Message}");
                        }

                        break;
                    case MessageType.VideoFrame:
                        try
                        {
                            PublishTcpVideoFrame(
                                ownerCancellationTokenSource,
                                RemoteMessageCodec.DecodeVideoFrame(message.PayloadMemory));
                        }
                        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or ObjectDisposedException)
                        {
                            Log?.Invoke($"已忽略无效视频帧：{ex.Message}");
                        }

                        break;
                    case MessageType.Control:
                        bool containsLowLatencyVideoSecrets =
                            message.PayloadLength > 0 &&
                            message.Buffer[message.PayloadOffset] ==
                                (byte)RemoteControlKind.LowLatencyVideoOffer;
                        try
                        {
                            await HandleControlMessageAsync(
                                message.PayloadMemory,
                                ownerCancellationTokenSource,
                                inputConnectionGeneration,
                                remoteCapabilitiesReady);
                        }
                        catch (Exception ex) when (
                            ex is not RemoteSessionRejectedException &&
                            ex is InvalidDataException or
                                EndOfStreamException or
                                IOException or
                                ArgumentException or
                                System.Text.DecoderFallbackException)
                        {
                            Log?.Invoke($"已忽略无效控制消息：{ex.Message}");
                        }
                        finally
                        {
                            if (containsLowLatencyVideoSecrets)
                            {
                                CryptographicOperations.ZeroMemory(
                                    message.Buffer.AsSpan(
                                        message.PayloadOffset,
                                        message.PayloadLength));
                            }
                        }

                        break;
                    case MessageType.Pong:
                        MarkPongReceived();
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or EndOfStreamException or ObjectDisposedException or CryptographicException or InvalidDataException)
        {
            if (ex is RemoteSessionRejectedException sessionRejection)
            {
                // Preserve the authenticated host's terminal reason across
                // the receive-loop teardown. ConnectAsync or its device-info
                // qualification can otherwise observe only the linked
                // cancellation and replace the useful reason with
                // "连接已取消".
                remoteCapabilitiesReady.TrySetException(
                    sessionRejection);
                Interlocked.Exchange(
                    ref _lastSessionRejection,
                    sessionRejection);
            }

            if (!ownerCancellationTokenSource.IsCancellationRequested)
            {
                Log?.Invoke($"连接中断：{ex.Message}");
            }
        }
        finally
        {
            remoteCapabilitiesReady.TrySetResult(false);
            ownerTcpClient.Close();

            Task? inputLoopTask = null;
            Task? heartbeatLoopTask = null;
            LowLatencyVideoViewerTransport? lowLatencyVideoTransport = null;
            ReturnedClipboardFileRequestContext? disconnectedRequest = null;
            bool ownsPublishedConnection = false;
            bool detachedOwner = false;
            lock (_connectionStateLock)
            {
                if (ReferenceEquals(_cancellationTokenSource, ownerCancellationTokenSource))
                {
                    inputLoopTask = _inputLoopTask;
                    heartbeatLoopTask = _heartbeatLoopTask;
                    lowLatencyVideoTransport = _lowLatencyVideoTransport;
                    _lowLatencyVideoTransport = null;
                    ownsPublishedConnection = true;
                }
            }

            if (ownsPublishedConnection)
            {
                // Keep the old owner published until both background loops have stopped. Otherwise
                // an immediate reconnect can publish a new generation while the old input loop is
                // still waiting on the shared signal and let that old waiter consume the new
                // generation's only wake-up.
                TryCancel(ownerCancellationTokenSource);
                ReleaseInputLoop();
                Task? lowLatencyVideoDisposeTask =
                    lowLatencyVideoTransport?.DisposeAsync().AsTask();
                if (inputLoopTask is not null)
                {
                    await IgnoreDisconnectExceptionAsync(inputLoopTask).ConfigureAwait(false);
                }

                if (heartbeatLoopTask is not null)
                {
                    await IgnoreDisconnectExceptionAsync(heartbeatLoopTask).ConfigureAwait(false);
                }

                if (lowLatencyVideoDisposeTask is not null)
                {
                    await IgnoreDisconnectExceptionAsync(lowLatencyVideoDisposeTask).ConfigureAwait(false);
                }

                lock (_connectionStateLock)
                {
                    if (ReferenceEquals(_cancellationTokenSource, ownerCancellationTokenSource))
                    {
                        _tcpClient = null;
                        _stream = null;
                        _session = null;
                        _cancellationTokenSource = null;
                        _receiveLoopTask = null;
                        _inputLoopTask = null;
                        _heartbeatLoopTask = null;
                        _lowLatencyVideoTransport = null;
                        _allowVideoFallback = true;
                        ResetRemotePeerState();
                        if (ReferenceEquals(_connectedEventOwner, ownerCancellationTokenSource))
                        {
                            _connectedEventOwner = null;
                            EnqueueConnectedChanged(false);
                        }
                        ClearInputQueueAndFailFlushWaiters(
                            "连接已中断，输入命令未能全部发送。",
                            inputConnectionGeneration);

                        disconnectedRequest = DetachActiveReturnedClipboardFileRequest(ownerCancellationTokenSource);
                        CancelReturnedClipboardFileBatchIfNoPendingCommit();
                        _incomingFileReceiver.AbortActiveTransfer();
                        detachedOwner = true;
                    }
                }
            }

            if (detachedOwner)
            {
                if (disconnectedRequest is not null)
                {
                    PublishDetachedReturnedClipboardFileRequest(
                        disconnectedRequest,
                        CreateReturnedClipboardFileBatchResult(
                            ReturnedClipboardFileBatchOutcome.Disconnected,
                            "连接已中断，远端文件回传未完成。"));
                }

                ownerCancellationTokenSource.Dispose();
            }

            ownerSession.Dispose();
        }
    }

    private async Task InputLoopAsync(
        TcpClient ownerTcpClient,
        NetworkStream stream,
        SecureSession ownerSession,
        CancellationTokenSource ownerCancellationTokenSource,
        long inputConnectionGeneration)
    {
        var batch = new List<RemoteInputCommand>(MaxInputBatchSize);
        try
        {
            while (!ownerCancellationTokenSource.IsCancellationRequested)
            {
                await _inputSignal.WaitAsync(ownerCancellationTokenSource.Token);

                while (TryDequeueInputBatch(
                    batch,
                    inputConnectionGeneration,
                    out long mouseRouteGeneration))
                {
                    await Protocol.WriteInputMessagesAsync(
                        stream,
                        batch,
                        ownerSession,
                        _writeLock,
                        ownerCancellationTokenSource.Token,
                        command =>
                            command.Kind !=
                                RemoteInputKind.MouseMove ||
                            mouseRouteGeneration ==
                                Interlocked.Read(
                                    ref _mouseRouteGeneration));
                    CompleteInputFlushWaitersAfterWrite(
                        inputConnectionGeneration,
                        batch);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or CryptographicException)
        {
            if (!ownerCancellationTokenSource.IsCancellationRequested &&
                IsCurrentInputConnectionGeneration(inputConnectionGeneration))
            {
                Log?.Invoke($"输入发送中断：{ex.Message}");
                TryCancel(ownerCancellationTokenSource);
                ownerTcpClient.Close();
            }
        }
        finally
        {
            ClearInputQueueAndFailFlushWaiters(
                "输入发送已停止，仍有命令未确认发送。",
                inputConnectionGeneration);
        }
    }

    private async Task HeartbeatLoopAsync(
        TcpClient ownerTcpClient,
        NetworkStream stream,
        SecureSession ownerSession,
        CancellationTokenSource ownerCancellationTokenSource)
    {
        Task pingTask = SendPingLoopAsync(
            stream,
            ownerSession,
            ownerCancellationTokenSource);
        Task watchdogTask = HeartbeatWatchdogLoopAsync(
            ownerTcpClient,
            ownerCancellationTokenSource);
        try
        {
            Task completed = await Task.WhenAny(
                    pingTask,
                    watchdogTask)
                .ConfigureAwait(false);
            if (ReferenceEquals(completed, pingTask) &&
                !ownerCancellationTokenSource.IsCancellationRequested)
            {
                if (pingTask.Exception?.GetBaseException() is { } failure)
                {
                    Log?.Invoke($"心跳中断：{failure.Message}");
                }

                TryCancel(ownerCancellationTokenSource);
                ownerTcpClient.Close();
            }

            await Task.WhenAll(
                    pingTask,
                    watchdogTask)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or CryptographicException)
        {
            if (!ownerCancellationTokenSource.IsCancellationRequested)
            {
                Log?.Invoke($"心跳中断：{ex.Message}");
                TryCancel(ownerCancellationTokenSource);
                ownerTcpClient.Close();
            }
        }
    }

    private async Task SendPingLoopAsync(
        NetworkStream stream,
        SecureSession ownerSession,
        CancellationTokenSource ownerCancellationTokenSource)
    {
        while (!ownerCancellationTokenSource.IsCancellationRequested)
        {
            await Task.Delay(
                    HeartbeatInterval,
                    ownerCancellationTokenSource.Token)
                .ConfigureAwait(false);
            MarkPingSent();
            await Protocol.WriteMessageAsync(
                    stream,
                    MessageType.Ping,
                    ReadOnlyMemory<byte>.Empty,
                    ownerSession,
                    _writeLock,
                    ownerCancellationTokenSource.Token)
                .ConfigureAwait(false);
        }
    }

    private async Task HeartbeatWatchdogLoopAsync(
        TcpClient ownerTcpClient,
        CancellationTokenSource ownerCancellationTokenSource)
    {
        while (!ownerCancellationTokenSource.IsCancellationRequested)
        {
            await Task.Delay(
                    HeartbeatWatchdogInterval,
                    ownerCancellationTokenSource.Token)
                .ConfigureAwait(false);
            if (!ShouldDisconnectForHeartbeat(
                    GetLastMessageAge(),
                    IsHeartbeatTimeoutSuppressed(
                        IsWaitingForFileTransferConfirmation(),
                        IsRemoteClipboardFileRequestPending,
                        IsOutgoingFileTransferPending(ownerCancellationTokenSource))))
            {
                continue;
            }

            Log?.Invoke("连接超时，已断开。");
            TryCancel(ownerCancellationTokenSource);
            ownerTcpClient.Close();
            return;
        }
    }

    private async Task HandleControlMessageAsync(
        ReadOnlyMemory<byte> payload,
        CancellationTokenSource ownerConnection,
        long inputConnectionGeneration,
        TaskCompletionSource<bool> remoteCapabilitiesReady)
    {
        if (!IsCurrentConnection(ownerConnection))
        {
            return;
        }

        RemoteControlMessage control = RemoteMessageCodec.DecodeControl(payload);
        TouchReturnedClipboardFileRequestForControl(control.Kind);
        switch (control.Kind)
        {
            case RemoteControlKind.FileReceiveLocation:
                if (IsCurrentConnection(ownerConnection) &&
                    _fileLocationRequests.TryGetValue(control.TransferId ?? string.Empty, out var locationRequest))
                    locationRequest.TrySetResult(control);
                break;
            case RemoteControlKind.HostVideoDiagnostics:
                HostVideoDiagnosticsReceived?.Invoke(control.Text ?? string.Empty);
                break;
            case RemoteControlKind.CaptureTargetList:
                if (IsCurrentConnection(ownerConnection) &&
                    IsCurrentInputConnectionGeneration(
                        inputConnectionGeneration))
                {
                    CaptureTargetsUpdated?.Invoke(
                        new CaptureTargetsUpdate(
                            inputConnectionGeneration,
                            control.Targets));
                }

                CaptureTargetsReceived?.Invoke(control.Targets);
                break;
            case RemoteControlKind.DeviceInfo:
                if (!string.IsNullOrWhiteSpace(control.MachineName))
                {
                    var device = new RemoteDeviceDescriptor(
                        control.MachineName,
                        RemoteDevicePlatforms.Normalize(control.Platform),
                        control.Capabilities,
                        control.BuildStamp ?? _remoteBuildStamp,
                        _remoteDeviceInfo?.DeviceId);
                    if (IsCurrentConnection(ownerConnection) &&
                        IsCurrentInputConnectionGeneration(
                            inputConnectionGeneration))
                    {
                        _remoteCapabilities = control.Capabilities;
                        _remoteBuildStamp = device.BuildStamp;
                        _remoteDeviceInfo = device;
                        _incomingFileReceiver.RequireChecksum =
                            control.Capabilities.HasFlag(
                                RemoteDeviceCapabilities
                                    .FileChecksum);
                        // ConnectAsync has already written our viewer
                        // safeguards before starting this receive loop.
                        Volatile.Write(
                            ref _remoteCapabilitiesInitialized,
                            1);
                        remoteCapabilitiesReady.TrySetResult(true);
                        DeviceInfoUpdated?.Invoke(
                            new RemoteDeviceInfoUpdate(
                                inputConnectionGeneration,
                                device));
                    }

                    // Preserve the original event for API compatibility.
                    DeviceInfoReceived?.Invoke(device);
                    if (!_deviceIdentityRequested && control.Capabilities.HasFlag(RemoteDeviceCapabilities.DeviceIdentity) &&
                        IsCurrentConnection(ownerConnection) && IsCurrentInputConnectionGeneration(inputConnectionGeneration))
                    {
                        _deviceIdentityRequested = true;
                        await SendControlAsync(RemoteMessageCodec.EncodeDeviceIdentityRequest(), ownerConnection);
                    }
                }

                break;
            case RemoteControlKind.DeviceBuildInfo:
                _remoteBuildStamp = control.BuildStamp;
                break;
            case RemoteControlKind.DeviceIdentity:
                if (_deviceIdentityRequested && _remoteDeviceInfo is not null && _remoteDeviceInfo.DeviceId is null && IsCurrentConnection(ownerConnection) &&
                    IsCurrentInputConnectionGeneration(inputConnectionGeneration))
                {
                    _remoteDeviceInfo = _remoteDeviceInfo with { DeviceId = control.Text };
                    DeviceInfoUpdated?.Invoke(new RemoteDeviceInfoUpdate(inputConnectionGeneration, _remoteDeviceInfo));
                    DeviceInfoReceived?.Invoke(_remoteDeviceInfo);
                }
                break;
            case RemoteControlKind.CaptureTargetChanged:
                InvalidateNativeDetails();
                if (!string.IsNullOrWhiteSpace(control.TargetId) && !string.IsNullOrWhiteSpace(control.DisplayName))
                {
                    var target = new CaptureTargetInfo(
                        control.TargetId,
                        control.DisplayName);
                    CaptureTargetSelectionChanged?.Invoke(
                        new CaptureTargetChangedUpdate(
                            inputConnectionGeneration,
                            target));
                    CaptureTargetChanged?.Invoke(target);
                }

                break;
            case RemoteControlKind.ClipboardSnapshot:
                CompleteClipboardSnapshot(control, ownerConnection, inputConnectionGeneration);
                break;
            case RemoteControlKind.ClipboardText:
                var clipboardRequest = _clipboardRequests.Take(inputConnectionGeneration, textReply: true, success: true);
                if (clipboardRequest is null) break;
                try
                {
                    if (string.IsNullOrEmpty(control.Text))
                    {
                        ClipboardStatusReceived?.Invoke("远端没有可读取的文本，本机剪贴板保持不变。");
                        break;
                    }
                    uint appliedSequence = await ClipboardTextService.SetTextAndGetSequenceAsync(control.Text, () =>
                        IsCurrentConnection(ownerConnection) && !ownerConnection.IsCancellationRequested &&
                        _clipboardRequests.CanApply(clipboardRequest, ClipboardTextService.ReadClipboardSequenceNumber()));
                    LocalClipboardTextApplied?.Invoke(inputConnectionGeneration, appliedSequence, control.Text);
                    ClipboardStatusReceived?.Invoke("已读取远程剪贴板到本机。");
                    clipboardRequest.Completion.TrySetResult(true);
                }
                catch (OperationCanceledException)
                {
                    ClipboardStatusReceived?.Invoke("剪贴板请求已过期，或本机已复制新内容；未覆盖本机剪贴板。");
                }
                catch (Exception ex)
                {
                    ClipboardStatusReceived?.Invoke($"写入本机剪贴板失败：{ex.Message}");
                }
                finally { clipboardRequest.Completion.TrySetResult(false); }

                break;
            case RemoteControlKind.ClipboardStatus:
                string clipboardStatus =
                    control.StatusMessage ??
                    "剪贴板操作已完成。";
                bool isCaptureTargetStatus = CaptureTargetAvailabilityStatusCodec.TryParse(
                        clipboardStatus,
                        out CaptureTargetAvailabilityStatusData
                            captureTargetStatus);
                if (isCaptureTargetStatus &&
                    IsCurrentConnection(ownerConnection) &&
                    IsCurrentInputConnectionGeneration(
                        inputConnectionGeneration))
                {
                    var update =
                        new CaptureTargetAvailabilityUpdate(
                            inputConnectionGeneration,
                            captureTargetStatus.IsAvailable,
                            captureTargetStatus.Target,
                            captureTargetStatus.TargetGeneration,
                            captureTargetStatus.DisplayMessage);
                    Volatile.Write(
                        ref _latestCaptureTargetAvailability,
                        update);
                    CaptureTargetAvailabilityChanged?.Invoke(
                        update);
                }
                if (!isCaptureTargetStatus)
                {
                    var reply = _clipboardRequests.Take(inputConnectionGeneration, textReply: false, control.Success);
                    reply?.Completion.TrySetResult(control.Success && !reply.Expired);
                    if (reply is not null && control.Success) break;
                }

                // Preserve the original compatibility event even when a
                // modern Windows UI also consumes the dedicated target event.
                ClipboardStatusReceived?.Invoke(clipboardStatus);
                break;
            case RemoteControlKind.FileTransferReceipt:
                if (control.TransferId is { } receiptId && _fileReceipts.TryGetValue(receiptId, out var pendingReceipt))
                    pendingReceipt.TrySetResult(control);
                break;
            case RemoteControlKind.FileTransferStatus:
                string transferStatus = control.StatusMessage ?? "文件传输状态已更新。";
                bool transferSuccess = control.Success;
                bool returnedClipboardTerminalStatus =
                    IsReturnedClipboardFileBatchCompleteStatus(transferStatus) ||
                    IsReturnedClipboardFileBatchEmptyStatus(transferStatus) ||
                    IsReturnedClipboardFileRequestTerminalFailureStatus(
                        transferSuccess,
                        transferStatus);
                bool rejectedClipboardRequestDrained = false;
                if (returnedClipboardTerminalStatus &&
                    TryGetActiveReturnedClipboardFileRequest(
                        out ReturnedClipboardFileRequestContext drainingRequest) &&
                    drainingRequest.IsRejectRequested)
                {
                    // Do not open the request slot until the old reject is on the wire. Otherwise
                    // a fast retry can overtake it and the host may apply that stale reject to the
                    // new preview (the legacy protocol has no batch id).
                    await TryRejectReturnedClipboardFilePreviewAsync(drainingRequest);
                    _incomingFileReceiver.AbortActiveTransfer();
                    _incomingFileReceiver.CancelReturnedClipboardFileBatch();
                    ReturnedClipboardFileBatchResult drainResult =
                        drainingRequest.TryGetRejectDrainResult(out ReturnedClipboardFileBatchResult requestedResult)
                            ? requestedResult
                            : CreateReturnedClipboardFileBatchResult(
                                drainingRequest.IsWaitCancelled
                                    ? ReturnedClipboardFileBatchOutcome.Cancelled
                                    : ReturnedClipboardFileBatchOutcome.Failed,
                                drainingRequest.IsWaitCancelled
                                    ? "已取消从远程画面拖出文件。"
                                    : transferStatus);
                    CompleteReturnedClipboardFileRequest(drainingRequest, drainResult);
                    rejectedClipboardRequestDrained = true;
                    if (transferSuccess)
                    {
                        transferStatus = $"{transferStatus}；本机已拒绝本批，已接收文件不会用于剪贴板或拖放。";
                    }

                    transferSuccess = false;
                }

                if (!rejectedClipboardRequestDrained &&
                    IsReturnedClipboardFileBatchCompleteStatus(transferStatus) &&
                    TryGetActiveReturnedClipboardFileRequest(out ReturnedClipboardFileRequestContext completedRequest))
                {
                    if (completedRequest.IsWaitCancelled)
                    {
                        _incomingFileReceiver.AbortActiveTransfer();
                        _incomingFileReceiver.CancelReturnedClipboardFileBatch();
                        transferSuccess = false;
                        transferStatus = $"{transferStatus}；拖出操作已取消，已接收文件不会用于本机拖放。";
                        CompleteReturnedClipboardFileRequest(
                            completedRequest,
                            CreateReturnedClipboardFileBatchResult(
                                ReturnedClipboardFileBatchOutcome.Cancelled,
                                transferStatus));
                    }
                    else if (!transferSuccess)
                    {
                        int receivedFileCount = _incomingFileReceiver.CompletedReturnedClipboardFileCount;
                        _incomingFileReceiver.AbortActiveTransfer();
                        _incomingFileReceiver.CancelReturnedClipboardFileBatch();
                        if (receivedFileCount > 0)
                        {
                            transferStatus = $"{transferStatus}；已保存的 {receivedFileCount} 个部分文件仅保留在接收目录，不会进入本机剪贴板或拖放。";
                        }

                        CompleteReturnedClipboardFileRequest(
                            completedRequest,
                            CreateReturnedClipboardFileBatchResult(
                                ReturnedClipboardFileBatchOutcome.Failed,
                                transferStatus));
                    }
                    else if (!IsReturnedClipboardFileBatchConsistent(completedRequest))
                    {
                        _incomingFileReceiver.AbortActiveTransfer();
                        _incomingFileReceiver.CancelReturnedClipboardFileBatch();
                        transferSuccess = false;
                        transferStatus = $"{transferStatus}；实际收到的文件与已确认清单不一致，已阻止本机剪贴板和拖放交付。";
                        CompleteReturnedClipboardFileRequest(
                            completedRequest,
                            CreateReturnedClipboardFileBatchResult(
                                ReturnedClipboardFileBatchOutcome.Failed,
                                transferStatus));
                    }
                    else
                    {
                        ReturnedClipboardFinalizeResult finalizeResult =
                            await FinalizeReturnedClipboardFileBatchAsync(
                                transferStatus,
                                allowClipboardFailure:
                                    completedRequest.Mode == ReturnedClipboardFileRequestMode.DragOut);
                        if (completedRequest.IsWaitCancelled)
                        {
                            transferSuccess = false;
                            transferStatus = $"{transferStatus}；拖出操作已取消，已接收文件不会用于本机拖放。";
                            CompleteReturnedClipboardFileRequest(
                                completedRequest,
                                CreateReturnedClipboardFileBatchResult(
                                    ReturnedClipboardFileBatchOutcome.Cancelled,
                                    transferStatus));
                        }
                        else
                        {
                            transferStatus = finalizeResult.Message;
                            transferSuccess = transferSuccess && finalizeResult.Success;
                            CompleteReturnedClipboardFileRequest(
                                completedRequest,
                                CreateReturnedClipboardFileBatchResult(finalizeResult));
                        }
                    }
                }
                else if (!rejectedClipboardRequestDrained &&
                    (IsReturnedClipboardFileBatchEmptyStatus(transferStatus) ||
                    IsReturnedClipboardFileRequestTerminalFailureStatus(transferSuccess, transferStatus)) &&
                    TryGetActiveReturnedClipboardFileRequest(out ReturnedClipboardFileRequestContext failedRequest))
                {
                    _incomingFileReceiver.AbortActiveTransfer();
                    _incomingFileReceiver.CancelReturnedClipboardFileBatch();
                    CompleteReturnedClipboardFileRequest(
                        failedRequest,
                        CreateReturnedClipboardFileBatchResult(
                            ReturnedClipboardFileBatchOutcome.Failed,
                            transferStatus));
                }

                if (!transferSuccess)
                {
                    ClearPendingSelfUpdateState();
                }

                FileTransferStatusReceived?.Invoke(transferSuccess, transferStatus);
                break;
            case RemoteControlKind.FileTransferStart:
                await HandleIncomingFileTransferOperationAsync(
                    () => Task.FromResult<string?>(BeginRegularIncomingFileTransfer(control)),
                    ownerConnection, control);
                break;
            case RemoteControlKind.RemoteUpdateStart:
                await HandleIncomingFileTransferOperationAsync(
                    () => Task.FromResult<string?>(BeginSelfUpdateTransfer(control)),
                    ownerConnection);
                break;
            case RemoteControlKind.FileTransferChunk:
                await HandleIncomingFileTransferOperationAsync(
                    async () =>
                    {
                        string? progressMessage = await _incomingFileReceiver.WriteChunkAsync(
                            control,
                            ownerConnection.Token);
                        if (!string.IsNullOrWhiteSpace(progressMessage))
                        {
                            FileTransferStatusReceived?.Invoke(true, progressMessage);
                        }

                        return null;
                    },
                    ownerConnection, control);
                break;
            case RemoteControlKind.FileTransferChecksum:
                await HandleIncomingFileTransferOperationAsync(
                    () =>
                    {
                        _incomingFileReceiver.SetExpectedChecksum(control);
                        return Task.FromResult<string?>(null);
                    },
                    ownerConnection, control);
                break;
            case RemoteControlKind.FileTransferCancel:
                ClearPendingSelfUpdateTransferIfMatches(control.TransferId);
                await HandleIncomingFileTransferOperationAsync(
                    () => Task.FromResult<string?>(_incomingFileReceiver.Cancel(control)),
                    ownerConnection, control);
                break;
            case RemoteControlKind.FileTransferComplete:
                await HandleIncomingFileTransferOperationAsync(
                    async () => await CompleteIncomingFileTransferOrSelfUpdateAsync(
                        control,
                        ownerConnection.Token),
                    ownerConnection, control);
                break;
            case RemoteControlKind.FileTransferClipboardFilesPreview:
                await HandleRemoteClipboardFilePreviewAsync(control, ownerConnection);
                break;
            case RemoteControlKind.LowLatencyVideoOffer:
                if (control.LowLatencyVideoOffer is { } offer)
                {
                    await TryStartLowLatencyVideoAsync(offer, ownerConnection);
                }

                break;
            case RemoteControlKind.LowLatencyVideoStopped:
                LowLatencyVideoViewerTransport? lowLatencyVideoTransport;
                lock (_connectionStateLock)
                {
                    lowLatencyVideoTransport =
                        ReferenceEquals(_cancellationTokenSource, ownerConnection)
                            ? _lowLatencyVideoTransport
                            : null;
                }

                lock (_framePublishLock)
                {
                    lowLatencyVideoTransport?.AcknowledgeStopped(
                        control.LowLatencyVideoChannelId,
                        control.LowLatencyVideoEpoch,
                        control.LowLatencyVideoStopReason,
                        allowAuthenticatedHostInitiatedPreserve:
                            true);
                }

                break;
            case RemoteControlKind.SessionRejected:
                throw new RemoteSessionRejectedException(
                    string.IsNullOrWhiteSpace(
                        control.StatusMessage)
                            ? "被控端当前无法接受新的查看连接。"
                            : control.StatusMessage.Trim());
            case RemoteControlKind.ViewerInfo:
                break;
        }
    }

    private bool ShouldIgnoreTcpVideoFrames(CancellationTokenSource ownerConnection)
    {
        lock (_connectionStateLock)
        {
            return !ReferenceEquals(_cancellationTokenSource, ownerConnection) ||
                ownerConnection.IsCancellationRequested ||
                _lowLatencyVideoTransport?.ShouldIgnoreTcpFrames == true;
        }
    }

    private void PublishTcpVideoFrame(
        CancellationTokenSource ownerConnection,
        RemoteFrame frame,
        bool nativeEnvelope = false)
    {
        lock (_framePublishLock)
        {
            if (!ShouldIgnoreTcpVideoFrames(ownerConnection))
            {
                if (!nativeEnvelope) NativeDetails.ObserveLegacyBase();
                FrameReceived?.Invoke(frame);
            }
        }
    }

    private async Task TryStartLowLatencyVideoAsync(
        LowLatencyVideoOffer offer,
        CancellationTokenSource ownerConnection)
    {
        LowLatencyVideoViewerTransport? transport = null;
        try
        {
            IPAddress? hostAddress;
            IPAddress? localAddress;
            LowLatencyVideoFeatures features;
            lock (_connectionStateLock)
            {
                TcpClient? activeTcpClient =
                    ReferenceEquals(_cancellationTokenSource, ownerConnection) &&
                    _remoteCapabilities.HasFlag(RemoteDeviceCapabilities.LowLatencyUdpVideo) &&
                    _lowLatencyVideoTransport is null
                        ? _tcpClient
                        : null;
                hostAddress = null;
                localAddress = null;
                if (activeTcpClient is not null)
                {
                    try
                    {
                        hostAddress =
                            (activeTcpClient.Client.RemoteEndPoint as
                                IPEndPoint)?.Address;
                    }
                    catch (ObjectDisposedException)
                    {
                        hostAddress = null;
                    }
                    catch (SocketException)
                    {
                        hostAddress = null;
                    }

                    if (hostAddress is not null)
                    {
                        try
                        {
                            localAddress =
                                (activeTcpClient.Client.LocalEndPoint as
                                    IPEndPoint)?.Address;
                        }
                        catch (ObjectDisposedException)
                        {
                            localAddress = null;
                        }
                        catch (SocketException)
                        {
                            localAddress = null;
                        }
                    }
                }

                features =
                    ResolveNegotiatedLowLatencyVideoFeatures(
                        _remoteCapabilities);
            }

            if (hostAddress is null)
            {
                return;
            }

            transport = new LowLatencyVideoViewerTransport(
                offer,
                hostAddress,
                payload => TrySendControlFromReceiveLoopAsync(payload, ownerConnection),
                frame => PublishLowLatencyVideoFrame(
                    ownerConnection,
                    offer.ChannelId,
                    offer.Epoch,
                    frame),
                message => Log?.Invoke(message),
                ownerConnection.Token,
                features,
                localAddress: localAddress,
                abortConnection: () =>
                    AbortConnectionOwner(ownerConnection),
                framePublicationGate: _framePublishLock);

            bool installed;
            lock (_connectionStateLock)
            {
                installed =
                    ReferenceEquals(_cancellationTokenSource, ownerConnection) &&
                    _lowLatencyVideoTransport is null;
                if (installed)
                {
                    _lowLatencyVideoTransport = transport;
                }
            }

            if (!installed)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
                transport = null;
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            if (transport is not null)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }

            Log?.Invoke($"低延迟 UDP 画面通道不可用，继续使用 TCP：{ex.Message}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(offer.HostToViewerKey);
            CryptographicOperations.ZeroMemory(offer.ViewerToHostKey);
            CryptographicOperations.ZeroMemory(offer.HostNoncePrefix);
            CryptographicOperations.ZeroMemory(offer.ViewerNoncePrefix);
        }
    }

    private void AbortConnectionOwner(
        CancellationTokenSource ownerConnection)
    {
        TcpClient? tcpClient;
        lock (_connectionStateLock)
        {
            if (!ReferenceEquals(
                    _cancellationTokenSource,
                    ownerConnection) ||
                ownerConnection.IsCancellationRequested)
            {
                return;
            }

            tcpClient = _tcpClient;
        }

        TryCancel(ownerConnection);
        try
        {
            tcpClient?.Close();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void PublishLowLatencyVideoFrame(
        CancellationTokenSource ownerConnection,
        ulong channelId,
        uint epoch,
        RemoteFrame frame)
    {
        lock (_framePublishLock)
        {
            bool publish;
            lock (_connectionStateLock)
            {
                publish =
                    ReferenceEquals(_cancellationTokenSource, ownerConnection) &&
                    _lowLatencyVideoTransport is { } transport &&
                    transport.Matches(channelId, epoch) &&
                    transport.ShouldIgnoreTcpFrames;
            }

            if (publish)
            {
                FrameReceived?.Invoke(frame);
            }
        }
    }

    private async Task HandleRemoteClipboardFilePreviewAsync(
        RemoteControlMessage control,
        CancellationTokenSource ownerConnection)
    {
        if (!TryGetActiveReturnedClipboardFileRequest(out ReturnedClipboardFileRequestContext request))
        {
            FileTransferStatusReceived?.Invoke(false, "未请求远端文件，已拒绝过期的回传清单。");
            await TrySendControlFromReceiveLoopAsync(
                RemoteMessageCodec.EncodeFileTransferRejectClipboardFiles(),
                ownerConnection);
            return;
        }

        IReadOnlyList<FileTransferConfirmationItem> items =
            (control.FileTransferPreviewItems ?? Array.Empty<FileTransferConfirmationItem>())
            .Select(item =>
            {
                string transferName = FileTransferReceiver.SanitizeFileName(item.TransferName);
                return item with
                {
                    TransferName = transferName,
                    DestinationPath = FileTransferConfirmation.FormatLocalReceiveDestination(transferName)
                };
            })
            .ToArray();
        if (items.Count == 0)
        {
            _incomingFileReceiver.CancelReturnedClipboardFileBatch();
            const string emptyMessage = "远端文件清单为空，已取消回传。";
            FileTransferStatusReceived?.Invoke(false, emptyMessage);
            BeginReturnedClipboardFileRejectDrain(
                request,
                ReturnedClipboardFileBatchOutcome.Failed,
                emptyMessage);
            await TryRejectReturnedClipboardFilePreviewAsync(request);
            return;
        }

        if (request.Mode == ReturnedClipboardFileRequestMode.DragOut)
        {
            if (request.IsWaitCancelled)
            {
                _incomingFileReceiver.CancelReturnedClipboardFileBatch();
                const string cancelledMessage = "拖出操作已取消，已拒绝远端文件回传。";
                FileTransferStatusReceived?.Invoke(false, cancelledMessage);
                BeginReturnedClipboardFileRejectDrain(
                    request,
                    ReturnedClipboardFileBatchOutcome.Cancelled,
                    cancelledMessage);
                await TryRejectReturnedClipboardFilePreviewAsync(request);
                return;
            }

            if (!TryBindReturnedClipboardFilePreview(request, items, out string? failureMessage))
            {
                BeginReturnedClipboardFileRejectDrain(
                    request,
                    ReturnedClipboardFileBatchOutcome.Failed,
                    failureMessage!);
                await TryRejectReturnedClipboardFilePreviewAsync(request);
                return;
            }

            if (request.OwnerConnection is { } dragOwnerConnection)
            {
                bool confirmationSent = await request.TrySendPreviewConfirmationAsync(
                    () => TrySendControlFromReceiveLoopAsync(
                        RemoteMessageCodec.EncodeFileTransferConfirmClipboardFiles(),
                        dragOwnerConnection));
                if (!confirmationSent)
                {
                    BeginReturnedClipboardFileRejectDrain(
                        request,
                        ReturnedClipboardFileBatchOutcome.Cancelled,
                        "已取消从远程画面拖出文件。");
                    await TryRejectReturnedClipboardFilePreviewAsync(request);
                    return;
                }
            }

            // Publish the acknowledgement only after Confirm is ordered on the wire. A drag
            // cancellation raised synchronously by this event will then be serialized as
            // Confirm -> Reject, never Reject -> Confirm (which would create two untagged host
            // terminal statuses and could poison an immediate retry).
            FileTransferStatusReceived?.Invoke(true, $"拖出手势已确认，远端将回传 {items.Count} 项文件。");
            return;
        }

        BeginFileTransferConfirmationWait();
        request.PauseIdleTimeout();
        bool confirmed;
        try
        {
            FileTransferStatusReceived?.Invoke(true, $"远端准备回传 {items.Count} 项文件，等待确认。");
            try
            {
                Func<IReadOnlyList<FileTransferConfirmationItem>, string?, bool>? confirm =
                    ConfirmRemoteClipboardFileTransfer;
                confirmed = confirm is null
                    ? FileTransferConfirmation.Confirm(
                        owner: null,
                        title: "确认拉取远端文件",
                        actionText: "被控端准备回传以下文件到本机。",
                        items: items,
                        note: control.StatusMessage)
                    : confirm(items, control.StatusMessage);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                _incomingFileReceiver.CancelReturnedClipboardFileBatch();
                string failureMessage = $"无法显示远端文件确认窗口，已取消回传：{ex.Message}";
                FileTransferStatusReceived?.Invoke(false, failureMessage);
                BeginReturnedClipboardFileRejectDrain(
                    request,
                    ReturnedClipboardFileBatchOutcome.Failed,
                    failureMessage);
                await TryRejectReturnedClipboardFilePreviewAsync(request);
                return;
            }
        }
        finally
        {
            request.ResumeIdleTimeout();
            EndFileTransferConfirmationWait();
        }

        if (!IsActiveReturnedClipboardFileRequest(request) || request.IsWaitCancelled)
        {
            if (request.IsWaitCancelled)
            {
                BeginReturnedClipboardFileRejectDrain(
                    request,
                    ReturnedClipboardFileBatchOutcome.Cancelled,
                    "已取消从远程画面拖出文件。");
            }

            await TryRejectReturnedClipboardFilePreviewAsync(request);
            return;
        }

        if (confirmed)
        {
            if (!TryBindReturnedClipboardFilePreview(request, items, out string? failureMessage))
            {
                BeginReturnedClipboardFileRejectDrain(
                    request,
                    ReturnedClipboardFileBatchOutcome.Failed,
                    failureMessage!);
                await TryRejectReturnedClipboardFilePreviewAsync(request);
                return;
            }

            FileTransferStatusReceived?.Invoke(true, "已确认拉取远端文件，等待接收。");
            if (request.OwnerConnection is { } standardOwnerConnection)
            {
                await TrySendControlFromReceiveLoopAsync(
                    RemoteMessageCodec.EncodeFileTransferConfirmClipboardFiles(),
                    standardOwnerConnection);
            }
            return;
        }

        _incomingFileReceiver.CancelReturnedClipboardFileBatch();
        const string cancelledByUserMessage = "已取消拉取远端文件。";
        FileTransferStatusReceived?.Invoke(false, cancelledByUserMessage);
        BeginReturnedClipboardFileRejectDrain(
            request,
            ReturnedClipboardFileBatchOutcome.Cancelled,
            cancelledByUserMessage);
        await TryRejectReturnedClipboardFilePreviewAsync(request);
    }

    private async Task HandleIncomingFileTransferOperationAsync(
        Func<Task<string?>> operation,
        CancellationTokenSource ownerConnection,
        RemoteControlMessage? receiptControl = null)
    {
        if (!IsCurrentConnection(ownerConnection))
        {
            return;
        }

        if (!_remoteCapabilities.HasFlag(RemoteDeviceCapabilities.FileTransferReceipt)) receiptControl = null;

        try
        {
            string? message = await operation();
            if (!IsCurrentConnection(ownerConnection))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(message))
            {
                FileTransferStatusReceived?.Invoke(true, message);
                await TrySendControlFromReceiveLoopAsync(
                    receiptControl is { Kind: RemoteControlKind.FileTransferComplete or RemoteControlKind.FileTransferCancel, TransferId: { } id }
                        ? RemoteMessageCodec.EncodeFileTransferReceipt(id, receiptControl.Kind == RemoteControlKind.FileTransferComplete, message)
                        : RemoteMessageCodec.EncodeFileTransferStatus(true, message),
                    ownerConnection);
            }
        }
        catch (Exception ex) when (RemoteFileTransfer.IsRecoverableTransferException(ex))
        {
            if (!IsCurrentConnection(ownerConnection))
            {
                return;
            }

            if (!_incomingFileReceiver.HasActiveTransfer)
            {
                ClearPendingSelfUpdateTransfer();
            }

            string message = $"接收远端文件失败：{ex.Message}";
            FileTransferStatusReceived?.Invoke(false, message);
            await TrySendControlFromReceiveLoopAsync(
                receiptControl?.TransferId is { } id
                    ? RemoteMessageCodec.EncodeFileTransferReceipt(id, false, message)
                    : RemoteMessageCodec.EncodeFileTransferStatus(false, message),
                ownerConnection);
        }
    }

    private string BeginSelfUpdateTransfer(RemoteControlMessage control)
    {
        if (!RemoteUpdater.CanApplyRemoteUpdate)
        {
            throw new InvalidOperationException("当前控制端不是可自更新的 RemoteDesk.exe。");
        }

        return BeginSelfUpdateTransferCore(control);
    }

    internal string BeginSelfUpdateTransferCore(RemoteControlMessage control)
    {
        lock (_selfUpdateLock)
        {
            if (!_selfUpdatePackageRequested)
            {
                throw new InvalidOperationException("未请求远端更新包，已拒绝本机自更新传输。");
            }

            _selfUpdatePackageRequested = false;
            RemoteUpdater.ValidateRemoteUpdatePackageName(control.FileName);
            RemoteUpdater.ValidateRemoteUpdatePackageLength(control.FileLength);
            string message = _incomingFileReceiver.Start(control);
            _pendingSelfUpdateTransferId = control.TransferId;
            return $"{message}；接收完成后将自动更新并重启本机 RemoteDesk。";
        }
    }

    internal bool TryReserveSelfUpdatePackageRequest()
    {
        lock (_selfUpdateLock)
        {
            lock (_returnedClipboardFileRequestLock)
            {
                if (_selfUpdatePackageRequested ||
                    _pendingSelfUpdateTransferId is not null ||
                    _returnedClipboardProtocolDesynchronized ||
                    _returnedClipboardFileRequest is not null ||
                    _returnedClipboardFileRequestPending != 0)
                {
                    return false;
                }

                _selfUpdatePackageRequested = true;
                return true;
            }
        }
    }

    private string BeginRegularIncomingFileTransfer(RemoteControlMessage control)
    {
        string normalizedFileName = string.IsNullOrWhiteSpace(control.FileName)
            ? string.Empty
            : FileTransferReceiver.SanitizeFileName(control.FileName);
        if (TryGetActiveReturnedClipboardFileRequest(out ReturnedClipboardFileRequestContext request) &&
            !request.TryReserveExpectedTransfer(
                normalizedFileName,
                control.FileLength,
                out string? rejectionReason))
        {
            throw new InvalidDataException(rejectionReason);
        }

        string message = _incomingFileReceiver.Start(control);
        ClearPendingSelfUpdateState();
        return message;
    }

    private bool TryBindReturnedClipboardFilePreview(
        ReturnedClipboardFileRequestContext request,
        IReadOnlyList<FileTransferConfirmationItem> items,
        out string? failureMessage)
    {
        if (request.TryBindAcceptedPreview(items, out failureMessage))
        {
            return true;
        }

        _incomingFileReceiver.CancelReturnedClipboardFileBatch();
        failureMessage ??= "远端文件清单无效，已取消回传。";
        FileTransferStatusReceived?.Invoke(false, failureMessage);
        return false;
    }

    private bool IsReturnedClipboardFileBatchConsistent(
        ReturnedClipboardFileRequestContext request)
    {
        if (_incomingFileReceiver.HasActiveTransfer)
        {
            return false;
        }

        if (!request.HasAcceptedPreview)
        {
            return !request.RequiresPreview;
        }

        return request.AllExpectedTransfersStarted &&
            _incomingFileReceiver.CompletedReturnedClipboardFileCount == request.ExpectedTransferCount;
    }

    private async Task<string?> CompleteIncomingFileTransferOrSelfUpdateAsync(
        RemoteControlMessage control,
        CancellationToken cancellationToken)
    {
        string message = await _incomingFileReceiver.CompleteAsync(control, cancellationToken);
        if (!IsPendingSelfUpdateTransfer(control.TransferId))
        {
            return message;
        }

        ClearPendingSelfUpdateState();
        string packagePath = _incomingFileReceiver.LastCompletedFilePath
            ?? throw new InvalidOperationException("本机更新包保存路径不可用。");
        try
        {
            RemoteUpdater.ScheduleApplyAndRestart(packagePath, message => Log?.Invoke(message));
        }
        catch
        {
            RemoteUpdater.DeleteRejectedReceivedPackage(packagePath);
            throw;
        }
        return $"{message}；正在应用来自远端的更新，RemoteDesk 会自动重启。";
    }

    private bool IsPendingSelfUpdateTransfer(string? transferId)
    {
        lock (_selfUpdateLock)
        {
            return !string.IsNullOrWhiteSpace(transferId) &&
                string.Equals(_pendingSelfUpdateTransferId, transferId, StringComparison.Ordinal);
        }
    }

    private void ClearPendingSelfUpdateTransferIfMatches(string? transferId)
    {
        lock (_selfUpdateLock)
        {
            if (!string.IsNullOrWhiteSpace(transferId) &&
                string.Equals(_pendingSelfUpdateTransferId, transferId, StringComparison.Ordinal))
            {
                _pendingSelfUpdateTransferId = null;
            }
        }
    }

    private void ClearPendingSelfUpdateState()
    {
        lock (_selfUpdateLock)
        {
            _selfUpdatePackageRequested = false;
            _pendingSelfUpdateTransferId = null;
        }
    }

    private void ClearPendingSelfUpdateTransfer()
    {
        lock (_selfUpdateLock)
        {
            _pendingSelfUpdateTransferId = null;
        }
    }

    private async Task<ReturnedClipboardFinalizeResult> FinalizeReturnedClipboardFileBatchAsync(
        string statusMessage,
        bool allowClipboardFailure)
    {
        IReadOnlyList<string> savedPaths = Array.Empty<string>();
        try
        {
            ReturnedClipboardFileCommitResult commitResult =
                await _incomingFileReceiver.CommitReturnedClipboardFileBatchWithResultAsync(
                    allowClipboardFailure, paths => savedPaths = paths);
            bool success = commitResult.LocalPaths.Count > 0;
            return new ReturnedClipboardFinalizeResult(
                success,
                $"{statusMessage}；{commitResult.Message}",
                commitResult.LocalPaths,
                commitResult.ClipboardUpdated);
        }
        catch (Exception ex) when (IsReturnedClipboardCommitException(ex))
        {
            return new ReturnedClipboardFinalizeResult(
                false,
                $"{statusMessage}；写入本机文件剪贴板失败：{ex.Message}。已保留已接收文件，再次点击拉取文件可重试。",
                savedPaths,
                ClipboardUpdated: false);
        }
    }

    private async Task<ReturnedClipboardFinalizeResult> TryCommitPendingReturnedClipboardFilesAsync(bool notifyRequest)
    {
        IReadOnlyList<string> savedPaths = Array.Empty<string>();
        try
        {
            ReturnedClipboardFileCommitResult commitResult =
                await _incomingFileReceiver.CommitReturnedClipboardFileBatchWithResultAsync(
                    onClipboardFailure: paths => savedPaths = paths);
            bool success = commitResult.LocalPaths.Count > 0;
            if (notifyRequest)
            {
                FileTransferStatusReceived?.Invoke(success, commitResult.Message);
            }

            return new ReturnedClipboardFinalizeResult(
                success,
                commitResult.Message,
                commitResult.LocalPaths,
                commitResult.ClipboardUpdated);
        }
        catch (Exception ex) when (IsReturnedClipboardCommitException(ex))
        {
            string message =
                $"写入本机文件剪贴板失败：{ex.Message}。已保留上次回传文件，再次点击拉取文件可重试。";
            if (notifyRequest)
            {
                FileTransferStatusReceived?.Invoke(false, message);
            }

            return new ReturnedClipboardFinalizeResult(
                false,
                message,
                savedPaths,
                ClipboardUpdated: false);
        }
    }

    private static bool IsReturnedClipboardCommitException(Exception ex)
    {
        return ex is InvalidOperationException or
            TimeoutException or
            System.Runtime.InteropServices.ExternalException;
    }

    private bool TryBeginReturnedClipboardFileRequest(
        ReturnedClipboardFileRequestMode mode,
        out ReturnedClipboardFileRequestContext request)
    {
        CancellationTokenSource? ownerConnection = _cancellationTokenSource;
        bool localCommitRetry = _incomingFileReceiver.HasPendingReturnedClipboardFileCommit;
        if (!localCommitRetry &&
            (ownerConnection is null || ownerConnection.IsCancellationRequested))
        {
            request = null!;
            return false;
        }

        lock (_selfUpdateLock)
        {
            if (_selfUpdatePackageRequested || _pendingSelfUpdateTransferId is not null)
            {
                request = null!;
                return false;
            }

            lock (_returnedClipboardFileRequestLock)
            {
                if (_returnedClipboardProtocolDesynchronized ||
                    _returnedClipboardFileRequest is not null ||
                    _returnedClipboardFileRequestPending != 0 ||
                    (!localCommitRetry &&
                        !ReferenceEquals(_cancellationTokenSource, ownerConnection)))
                {
                    request = null!;
                    return false;
                }

                request = new ReturnedClipboardFileRequestContext(
                    Interlocked.Increment(ref _returnedClipboardFileRequestSequence),
                    mode,
                    ownerConnection,
                requiresPreview: ownerConnection is not null &&
                    (Volatile.Read(ref _remoteCapabilitiesInitialized) == 0 ||
                        _remoteCapabilities.HasFlag(RemoteDeviceCapabilities.FileTransferPreview)));
                _returnedClipboardFileRequest = request;
                Volatile.Write(ref _returnedClipboardFileRequestPending, 1);
                Volatile.Write(ref _suppressInputUntilReturnedClipboardRequestDrained, 0);
            }
        }

        request.StartIdleTimeout(
            _returnedClipboardFileRequestIdleTimeout,
            HandleReturnedClipboardFileRequestIdleTimeout);
        NotifyRemoteClipboardFileRequestPendingChanged(true);
        return true;
    }

    private bool TryValidateRemoteClipboardFileRequest(
        out ReturnedClipboardFileBatchOutcome unavailableOutcome,
        out string unavailableMessage)
    {
        CancellationTokenSource? connection = _cancellationTokenSource;
        if (_stream is null || _tcpClient is null || connection is null ||
            connection.IsCancellationRequested)
        {
            unavailableOutcome = ReturnedClipboardFileBatchOutcome.Disconnected;
            unavailableMessage = "连接已断开，无法拉取远端文件。";
            return false;
        }

        if (Volatile.Read(ref _remoteCapabilitiesInitialized) == 0)
        {
            unavailableOutcome = ReturnedClipboardFileBatchOutcome.Busy;
            unavailableMessage = "正在协商远端文件能力，请稍后重试。";
            return false;
        }

        if (!_remoteCapabilities.HasFlag(RemoteDeviceCapabilities.FileSend))
        {
            unavailableOutcome = ReturnedClipboardFileBatchOutcome.Failed;
            unavailableMessage = "当前远端版本不支持文件回传。";
            return false;
        }

        unavailableOutcome = default;
        unavailableMessage = string.Empty;
        return true;
    }

    private async Task WaitForRemoteCapabilityNegotiationAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _remoteCapabilitiesInitialized) != 0)
        {
            return;
        }

        TaskCompletionSource<bool>? readiness = Volatile.Read(ref _remoteCapabilitiesReady);
        if (readiness is null || readiness.Task.IsCompleted)
        {
            return;
        }

        try
        {
            await readiness.Task.WaitAsync(
                RemoteCapabilityRequestGracePeriod,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Keep legacy/slow peers responsive: the caller will report capability negotiation as busy.
        }
    }

    private bool TryPrepareReturnedClipboardFileBatch(
        ReturnedClipboardFileRequestContext request)
    {
        lock (_returnedClipboardFileRequestLock)
        {
            if (!ReferenceEquals(_returnedClipboardFileRequest, request) ||
                request.OwnerConnection is null ||
                !ReferenceEquals(_cancellationTokenSource, request.OwnerConnection))
            {
                return false;
            }

            _incomingFileReceiver.BeginReturnedClipboardFileBatch();
            return true;
        }
    }

    private void CancelReturnedClipboardFileBatchIfActive(
        ReturnedClipboardFileRequestContext request)
    {
        lock (_returnedClipboardFileRequestLock)
        {
            if (ReferenceEquals(_returnedClipboardFileRequest, request))
            {
                _incomingFileReceiver.CancelReturnedClipboardFileBatch();
            }
        }
    }

    private void CancelReturnedClipboardFileWait(ReturnedClipboardFileRequestContext request)
    {
        bool requestIsActive = false;
        lock (_returnedClipboardFileRequestLock)
        {
            request.MarkWaitCancelled();
            if (!ReferenceEquals(_returnedClipboardFileRequest, request))
            {
                return;
            }

            _incomingFileReceiver.CancelReturnedClipboardFileBatch();
            _incomingFileReceiver.AbortActiveTransfer();
            Volatile.Write(ref _suppressInputUntilReturnedClipboardRequestDrained, 1);
            request.MarkRejectRequested();
            requestIsActive = true;
        }

        if (requestIsActive)
        {
            BeginReturnedClipboardFileRejectDrain(
                request,
                ReturnedClipboardFileBatchOutcome.Cancelled,
                "已取消从远程画面拖出文件。");
            _ = TryRejectReturnedClipboardFilePreviewAsync(request);
        }
    }

    private void TouchReturnedClipboardFileRequestForControl(RemoteControlKind kind)
    {
        if (kind is not (RemoteControlKind.FileTransferStatus or
            RemoteControlKind.FileTransferStart or
            RemoteControlKind.FileTransferChunk or
            RemoteControlKind.FileTransferChecksum or
            RemoteControlKind.FileTransferCancel or
            RemoteControlKind.FileTransferComplete or
            RemoteControlKind.FileTransferClipboardFilesPreview))
        {
            return;
        }

        if (TryGetActiveReturnedClipboardFileRequest(out ReturnedClipboardFileRequestContext request))
        {
            // A reject drain is a strict protocol barrier. Do not let unrelated or stale transfer
            // progress keep extending its short deadline; if the terminal acknowledgement never
            // arrives, the connection must close before another untagged request can be started.
            if (!request.IsRejectRequested)
            {
                request.TouchIdleTimeout();
            }
        }
    }

    private void HandleReturnedClipboardFileRequestIdleTimeout(
        ReturnedClipboardFileRequestContext request)
    {
        CancellationTokenSource? ownerConnection;
        TcpClient? ownerTcpClient;
        lock (_returnedClipboardFileRequestLock)
        {
            if (!ReferenceEquals(_returnedClipboardFileRequest, request))
            {
                return;
            }

            // The protocol has no request id. Once a request times out, close this connection so
            // a delayed preview/status can never be mistaken for a later request.
            _returnedClipboardProtocolDesynchronized = true;
            ownerConnection = request.OwnerConnection;
            ownerTcpClient = ownerConnection is not null &&
                ReferenceEquals(_cancellationTokenSource, ownerConnection)
                ? _tcpClient
                : null;
            _incomingFileReceiver.CancelReturnedClipboardFileBatch();
            _incomingFileReceiver.AbortActiveTransfer();
        }

        string message = request.IsRejectRequested
            ? "等待远端确认取消文件回传超时，已断开连接以防旧批次串入下一次拖放；请重新连接后重试。"
            : "等待远端文件回传超时，已断开连接以防迟到清单串入下一次拖放；请重新连接后重试。";
        CompleteReturnedClipboardFileRequest(
            request,
            CreateReturnedClipboardFileBatchResult(
                ReturnedClipboardFileBatchOutcome.TimedOut,
                message));

        if (ownerConnection is not null && ownerTcpClient is not null)
        {
            TryCancel(ownerConnection);
            ownerTcpClient.Close();
        }

        try
        {
            FileTransferStatusReceived?.Invoke(false, message);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"远端文件超时状态通知失败：{ex.Message}");
        }
    }

    private async Task TryRejectReturnedClipboardFilePreviewAsync(
        ReturnedClipboardFileRequestContext request)
    {
        CancellationTokenSource? ownerConnection = request.OwnerConnection;
        if (ownerConnection is null)
        {
            return;
        }

        await request.EnsureRejectSentAsync(
            () => TrySendControlFromReceiveLoopAsync(
                RemoteMessageCodec.EncodeFileTransferRejectClipboardFiles(),
                ownerConnection));
    }

    private static void BeginReturnedClipboardFileRejectDrain(
        ReturnedClipboardFileRequestContext request,
        ReturnedClipboardFileBatchOutcome outcome,
        string message)
    {
        request.BeginRejectDrain(
            CreateReturnedClipboardFileBatchResult(outcome, message),
            ReturnedClipboardFileRejectDrainTimeout);
    }

    private bool TryGetActiveReturnedClipboardFileRequest(
        out ReturnedClipboardFileRequestContext request)
    {
        lock (_returnedClipboardFileRequestLock)
        {
            request = _returnedClipboardFileRequest!;
            return request is not null;
        }
    }

    private bool IsActiveReturnedClipboardFileRequest(
        ReturnedClipboardFileRequestContext request)
    {
        lock (_returnedClipboardFileRequestLock)
        {
            return ReferenceEquals(_returnedClipboardFileRequest, request);
        }
    }

    private void CompleteActiveReturnedClipboardFileRequest(ReturnedClipboardFileBatchResult result)
    {
        if (TryGetActiveReturnedClipboardFileRequest(out ReturnedClipboardFileRequestContext request))
        {
            CompleteReturnedClipboardFileRequest(request, result);
        }
    }

    private void CompleteReturnedClipboardFileRequest(
        ReturnedClipboardFileRequestContext request,
        ReturnedClipboardFileBatchResult result)
    {
        if (!TryDetachReturnedClipboardFileRequest(request))
        {
            return;
        }

        PublishDetachedReturnedClipboardFileRequest(request, result);
    }

    private ReturnedClipboardFileRequestContext? DetachActiveReturnedClipboardFileRequest(
        CancellationTokenSource expectedConnection)
    {
        lock (_returnedClipboardFileRequestLock)
        {
            ReturnedClipboardFileRequestContext? request = _returnedClipboardFileRequest;
            if (request is null || !ReferenceEquals(request.OwnerConnection, expectedConnection))
            {
                return null;
            }

            _returnedClipboardFileRequest = null;
            Volatile.Write(ref _returnedClipboardFileRequestPending, 0);
            Volatile.Write(ref _suppressInputUntilReturnedClipboardRequestDrained, 0);
            return request;
        }
    }

    private bool TryDetachReturnedClipboardFileRequest(ReturnedClipboardFileRequestContext request)
    {
        lock (_returnedClipboardFileRequestLock)
        {
            if (!ReferenceEquals(_returnedClipboardFileRequest, request))
            {
                return false;
            }

            _returnedClipboardFileRequest = null;
            Volatile.Write(ref _returnedClipboardFileRequestPending, 0);
            Volatile.Write(ref _suppressInputUntilReturnedClipboardRequestDrained, 0);
            return true;
        }
    }

    private void PublishDetachedReturnedClipboardFileRequest(
        ReturnedClipboardFileRequestContext request,
        ReturnedClipboardFileBatchResult result)
    {
        request.StopIdleTimeout();
        request.Completion.TrySetResult(result);
        if (!IsRemoteClipboardFileRequestPending)
        {
            NotifyRemoteClipboardFileRequestPendingChanged(false);
        }

        NotifyRemoteClipboardFileBatchCompleted(result);
        if (request.Mode == ReturnedClipboardFileRequestMode.Standard && result.FilesForDisplay.Count > 0)
            RemoteClipboardFileResultReady?.Invoke(result);
    }

    private void NotifyRemoteClipboardFileRequestPendingChanged(bool pending)
    {
        try
        {
            RemoteClipboardFileRequestPendingChanged?.Invoke(pending);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"远端文件请求状态通知失败：{ex.Message}");
        }
    }

    private void NotifyRemoteClipboardFileBatchCompleted(ReturnedClipboardFileBatchResult result)
    {
        try
        {
            RemoteClipboardFileBatchCompleted?.Invoke(result);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"远端文件批次完成通知失败：{ex.Message}");
        }
    }

    private static ReturnedClipboardFileBatchResult CreateReturnedClipboardFileBatchResult(
        ReturnedClipboardFinalizeResult result)
    {
        ReturnedClipboardFileBatchResult batch = CreateReturnedClipboardFileBatchResult(
            result.Success
                ? ReturnedClipboardFileBatchOutcome.Succeeded
                : ReturnedClipboardFileBatchOutcome.Failed,
            result.Message,
            result.LocalPaths,
            result.ClipboardUpdated);
        return !result.Success && result.LocalPaths.Count > 0
            ? batch with { SavedPaths = Array.AsReadOnly(result.LocalPaths.ToArray()) }
            : batch;
    }

    private static ReturnedClipboardFileBatchResult CreateReturnedClipboardFileBatchResult(
        ReturnedClipboardFileBatchOutcome outcome,
        string message,
        IReadOnlyList<string>? localPaths = null,
        bool clipboardUpdated = false)
    {
        IReadOnlyList<string> safePaths = outcome == ReturnedClipboardFileBatchOutcome.Succeeded &&
            localPaths is { Count: > 0 }
            ? Array.AsReadOnly(localPaths.ToArray())
            : Array.Empty<string>();

        return new ReturnedClipboardFileBatchResult(
            outcome,
            safePaths,
            message,
            outcome == ReturnedClipboardFileBatchOutcome.Succeeded && clipboardUpdated);
    }

    private void CancelReturnedClipboardFileBatchIfNoPendingCommit()
    {
        _incomingFileReceiver.CancelReturnedClipboardFileBatchUnlessCommitRetryPending();
    }

    internal static bool IsReturnedClipboardFileBatchCompleteStatus(string message)
    {
        return message.StartsWith("远端文件回传完成", StringComparison.Ordinal) ||
            message.StartsWith("远端文件回传失败", StringComparison.Ordinal);
    }

    internal static bool IsReturnedClipboardFileBatchEmptyStatus(string message)
    {
        return message.StartsWith("远端剪贴板没有可回传", StringComparison.Ordinal) ||
            message.StartsWith("远端剪贴板文件不存在", StringComparison.Ordinal) ||
            message.StartsWith("远端剪贴板包含文件夹", StringComparison.Ordinal);
    }

    internal static bool IsReturnedClipboardFileRequestTerminalFailureStatus(bool success, string message)
    {
        if (success)
        {
            return false;
        }

        return message.StartsWith("读取远端文件剪贴板失败", StringComparison.Ordinal) ||
            message.StartsWith("等待远程复制更新剪贴板超时", StringComparison.Ordinal) ||
            message.StartsWith("远端文件回传已拒绝", StringComparison.Ordinal) ||
            message.StartsWith("远端文件回传已取消", StringComparison.Ordinal) ||
            message.StartsWith("远端文件回传进行中", StringComparison.Ordinal) ||
            message.StartsWith("已有远端文件回传清单等待确认", StringComparison.Ordinal) ||
            message.StartsWith("没有等待确认的远端文件回传", StringComparison.Ordinal) ||
            message.StartsWith("Linux has no return files", StringComparison.Ordinal) ||
            message.StartsWith("Linux could not prepare return files", StringComparison.Ordinal) ||
            message.StartsWith("Linux could not return files", StringComparison.Ordinal) ||
            message.StartsWith("Linux return file transfer cancelled", StringComparison.Ordinal) ||
            message.StartsWith("Linux return file transfer failed", StringComparison.Ordinal) ||
            message.StartsWith("Linux return file list contains no readable files", StringComparison.Ordinal) ||
            message.StartsWith("Linux has no pending return files", StringComparison.Ordinal);
    }

    private async Task<bool> TrySendControlFromReceiveLoopAsync(
        byte[] payload,
        CancellationTokenSource expectedConnection)
    {
        NetworkStream? stream = _stream;
        SecureSession? session = _session;
        CancellationTokenSource? cancellationTokenSource = _cancellationTokenSource;
        if (stream is null || session is null || cancellationTokenSource is null ||
            cancellationTokenSource.IsCancellationRequested ||
            !ReferenceEquals(cancellationTokenSource, expectedConnection))
        {
            return false;
        }

        try
        {
            await Protocol.WriteMessageAsync(stream, MessageType.Control, payload, session, _writeLock, cancellationTokenSource.Token);
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or CryptographicException)
        {
            // This send runs inside the sole receive loop. The peer may have half-closed its
            // receive side after already placing a terminal transfer status in our socket buffer.
            // Cancelling the owner here would skip that buffered terminal message and incorrectly
            // turn a fully received batch into "disconnected". Let the read/heartbeat path own
            // transport teardown; it can still drain any authenticated messages already received.
            if (IsCurrentConnection(expectedConnection) &&
                !expectedConnection.IsCancellationRequested)
            {
                Log?.Invoke($"回复远端控制状态失败，等待接收通道确认断线：{ex.Message}");
            }

            return false;
        }
    }

    private long BeginInputConnectionGeneration()
    {
        TaskCompletionSource<bool>[] staleFlushWaiters;
        long generation;
        lock (_inputLock)
        {
            generation = ++_inputConnectionGeneration;
            _inputQueue.Clear();
            _inputWriteInProgress = false;
            _pendingReliablePointerInputs = 0;
            _udpMouseRouteObserved = 0;
            _reliablePointerInputGateUntil = 0;
            Interlocked.Increment(
                ref _mouseRouteGeneration);
            staleFlushWaiters = _inputFlushWaiters.ToArray();
            _inputFlushWaiters.Clear();
        }

        FailInputFlushWaiters(staleFlushWaiters, "连接已切换，旧连接的输入命令未能全部发送。");
        return generation;
    }

    private bool IsCurrentInputConnectionGeneration(long generation)
    {
        lock (_inputLock)
        {
            return _inputConnectionGeneration == generation;
        }
    }

    private bool TryDequeueInputBatch(
        List<RemoteInputCommand> batch,
        long generation,
        out long mouseRouteGeneration)
    {
        batch.Clear();
        lock (_inputLock)
        {
            mouseRouteGeneration =
                Interlocked.Read(
                    ref _mouseRouteGeneration);
            if (_inputConnectionGeneration != generation)
            {
                return false;
            }

            while (batch.Count < MaxInputBatchSize &&
                _inputQueue.TryDequeue(out RemoteInputCommand command))
            {
                batch.Add(command);
            }

            if (batch.Count > 0)
            {
                _inputWriteInProgress = true;
            }
        }

        return batch.Count > 0;
    }

    private void CompleteInputFlushWaitersAfterWrite(
        long generation,
        IReadOnlyList<RemoteInputCommand> writtenBatch)
    {
        TaskCompletionSource<bool>[] completions = [];
        lock (_inputLock)
        {
            if (_inputConnectionGeneration != generation)
            {
                return;
            }

            int writtenReliablePointerInputs = 0;
            foreach (RemoteInputCommand command in
                writtenBatch)
            {
                if (IsReliablePointerInput(command))
                {
                    writtenReliablePointerInputs++;
                }
            }

            _pendingReliablePointerInputs =
                Math.Max(
                    0,
                    _pendingReliablePointerInputs -
                        writtenReliablePointerInputs);
            if (writtenReliablePointerInputs > 0)
            {
                _reliablePointerInputGateUntil =
                    Environment.TickCount64 +
                    ReliablePointerUdpResumeDelayMilliseconds;
            }

            _inputWriteInProgress = false;
            if (_inputQueue.Count == 0 && _inputFlushWaiters.Count > 0)
            {
                completions = _inputFlushWaiters.ToArray();
                _inputFlushWaiters.Clear();
            }
        }

        foreach (TaskCompletionSource<bool> completion in completions)
        {
            completion.TrySetResult(true);
        }
    }

    private void ClearInputQueueAndFailFlushWaiters(string message, long? expectedGeneration = null)
    {
        TaskCompletionSource<bool>[] completions;
        lock (_inputLock)
        {
            if (expectedGeneration is { } generation &&
                _inputConnectionGeneration != generation)
            {
                return;
            }

            _inputConnectionGeneration++;
            _inputQueue.Clear();
            _inputWriteInProgress = false;
            _pendingReliablePointerInputs = 0;
            _udpMouseRouteObserved = 0;
            _reliablePointerInputGateUntil = 0;
            Interlocked.Increment(
                ref _mouseRouteGeneration);
            completions = _inputFlushWaiters.ToArray();
            _inputFlushWaiters.Clear();
        }

        FailInputFlushWaiters(completions, message);
    }

    internal void ClearInputStateForConnectionGeneration(long generation)
    {
        ClearInputQueueAndFailFlushWaiters(
            "指定连接的输入发送已停止，仍有命令未确认发送。",
            generation);
    }

    private static void FailInputFlushWaiters(
        IEnumerable<TaskCompletionSource<bool>> completions,
        string message)
    {
        foreach (TaskCompletionSource<bool> completion in completions)
        {
            completion.TrySetException(new IOException(message));
        }
    }

    private bool CanQueueInput()
    {
        return CanQueueInput(_cancellationTokenSource);
    }

    private bool CanQueueInput(CancellationTokenSource? expectedConnection)
    {
        CancellationTokenSource? cancellationTokenSource = _cancellationTokenSource;
        return _stream is not null &&
            _session is not null &&
            cancellationTokenSource is not null &&
            ReferenceEquals(cancellationTokenSource, expectedConnection) &&
            Volatile.Read(ref _suppressInputUntilReturnedClipboardRequestDrained) == 0 &&
            !cancellationTokenSource.IsCancellationRequested;
    }

    internal static int FindInputDropIndex(IReadOnlyList<RemoteInputCommand> inputQueue)
    {
        return RemoteInputQueue.FindDropIndex(inputQueue);
    }

    private static IEnumerable<int> EnumerateTextInputCodePoints(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];
            int codePoint;
            if (current == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                codePoint = '\n';
            }
            else if (char.IsHighSurrogate(current) &&
                index + 1 < text.Length &&
                char.IsLowSurrogate(text[index + 1]))
            {
                codePoint = char.ConvertToUtf32(current, text[index + 1]);
                index++;
            }
            else if (char.IsSurrogate(current))
            {
                continue;
            }
            else
            {
                codePoint = current;
            }

            if (IsSupportedTextInputCodePoint(codePoint))
            {
                yield return codePoint;
            }
        }
    }

    private static bool IsSupportedTextInputCodePoint(int codePoint)
    {
        return InputInjector.IsSupportedTextCodePoint(codePoint);
    }

    private void ReleaseInputLoop()
    {
        try
        {
            _inputSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void TryCancel(CancellationTokenSource? cancellationTokenSource)
    {
        if (cancellationTokenSource is null)
        {
            return;
        }

        try
        {
            cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task IgnoreDisconnectExceptionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or CryptographicException)
        {
        }
    }

    private static bool IsDisconnectException(Exception ex)
    {
        return ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or CryptographicException;
    }

    private void MarkMessageReceived()
    {
        Interlocked.Exchange(
            ref _lastMessageTicks,
            Stopwatch.GetTimestamp());
    }

    private void ResetHeartbeatMetrics()
    {
        Interlocked.Exchange(ref _lastPingSentTicks, 0);
        Interlocked.Exchange(ref _pingAwaitingPong, 0);
    }

    private void MarkPingSent()
    {
        Interlocked.Exchange(ref _lastPingSentTicks, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _pingAwaitingPong, 1);
    }

    private void MarkPongReceived()
    {
        if (Interlocked.Exchange(ref _pingAwaitingPong, 0) != 1)
        {
            return;
        }

        long sentTicks = Interlocked.Read(ref _lastPingSentTicks);
        if (sentTicks <= 0)
        {
            return;
        }

        RoundTripUpdated?.Invoke(Stopwatch.GetElapsedTime(sentTicks));
    }

    private TimeSpan GetLastMessageAge()
    {
        long ticks = Interlocked.Read(ref _lastMessageTicks);
        return ticks <= 0
            ? TimeSpan.MaxValue
            : Stopwatch.GetElapsedTime(ticks);
    }

    private void BeginFileTransferConfirmationWait()
    {
        Interlocked.Increment(ref _fileTransferConfirmationWaiters);
    }

    private void EndFileTransferConfirmationWait()
    {
        MarkMessageReceived();
        int current = Interlocked.Decrement(ref _fileTransferConfirmationWaiters);
        if (current < 0)
        {
            Interlocked.Exchange(ref _fileTransferConfirmationWaiters, 0);
        }
    }

    private bool IsWaitingForFileTransferConfirmation()
    {
        return Interlocked.CompareExchange(ref _fileTransferConfirmationWaiters, 0, 0) > 0;
    }

    internal static bool ShouldDisconnectForHeartbeat(
        DateTimeOffset now,
        DateTimeOffset lastMessageAt,
        bool longRunningControlOperationPending)
    {
        return ShouldDisconnectForHeartbeat(
            now - lastMessageAt,
            longRunningControlOperationPending);
    }

    internal static bool ShouldDisconnectForHeartbeat(
        TimeSpan lastMessageAge,
        bool longRunningControlOperationPending)
    {
        if (lastMessageAge < TimeSpan.Zero)
        {
            return false;
        }

        TimeSpan timeout = longRunningControlOperationPending
            ? SuppressedHeartbeatTimeout
            : HeartbeatTimeout;
        return lastMessageAge > timeout;
    }

    internal static bool IsHeartbeatTimeoutSuppressed(
        bool fileTransferConfirmationPending,
        bool returnedClipboardFileRequestPending,
        bool outgoingFileTransferPending = false)
    {
        return fileTransferConfirmationPending || returnedClipboardFileRequestPending || outgoingFileTransferPending;
    }

    internal void BeginOutgoingFileTransfer(CancellationTokenSource ownerConnection) =>
        Volatile.Write(ref _outgoingFileTransferHeartbeat,
            new OutgoingFileTransferHeartbeatState(ownerConnection));

    internal void EndOutgoingFileTransfer(CancellationTokenSource ownerConnection, bool pendingDelivery = false)
    {
        while (Volatile.Read(ref _outgoingFileTransferHeartbeat) is { } current &&
            ReferenceEquals(current.OwnerConnection, ownerConnection))
        {
            OutgoingFileTransferHeartbeatState? next = pendingDelivery
                ? new OutgoingFileTransferHeartbeatState(ownerConnection, Stopwatch.GetTimestamp())
                : null;
            if (ReferenceEquals(Interlocked.CompareExchange(ref _outgoingFileTransferHeartbeat, next, current), current))
                return;
        }
    }

    internal bool IsOutgoingFileTransferPending(CancellationTokenSource ownerConnection)
    {
        // Uploads (including updates) are serialized by _fileTransferLock. Ping
        // shares their TCP stream and can be queued behind slow file traffic.
        // Only that connection gets the bounded grace, through its save receipt.
        // Local send progress must never reset authenticated inbound liveness.
        OutgoingFileTransferHeartbeatState? pending = Volatile.Read(ref _outgoingFileTransferHeartbeat);
        return !ownerConnection.IsCancellationRequested &&
            pending is not null && ReferenceEquals(pending.OwnerConnection, ownerConnection) &&
            (pending.DrainStartedTicks is not { } started ||
                IsOutgoingFileDeliveryDrainPending(Stopwatch.GetElapsedTime(started)));
    }

    internal static bool IsOutgoingFileDeliveryDrainPending(TimeSpan elapsed) =>
        elapsed >= TimeSpan.Zero && elapsed < SuppressedHeartbeatTimeout;

    private static string FormatVideoCodecs(RemoteVideoCodecs codecs)
    {
        List<string> names = [];
        if (codecs.HasFlag(RemoteVideoCodecs.Jpeg))
        {
            names.Add("JPEG");
        }

        if (codecs.HasFlag(RemoteVideoCodecs.H264AnnexB))
        {
            names.Add("H.264");
        }

        return names.Count == 0 ? "无" : string.Join("/", names);
    }

    internal static RemoteVideoCodecs ResolveSupportedVideoCodecs(
        ViewerVideoMode videoMode,
        string? ffmpegPath,
        bool hasNativeHardwareDecoder)
    {
        if (!Enum.IsDefined(videoMode))
        {
            videoMode = ViewerVideoMode.Automatic;
        }

        bool hasFfmpeg =
            !string.IsNullOrWhiteSpace(ffmpegPath);
        bool hasH264Decoder =
            hasNativeHardwareDecoder ||
            hasFfmpeg;
        return videoMode switch
        {
            ViewerVideoMode.StableJpeg => RemoteVideoCodecs.Jpeg,
            // The legacy H.264-only setting stranded locked Windows hosts,
            // whose privileged capture bridge produces JPEG. Codec preference
            // must never remove that recovery path or reject a JPEG-only peer.
            _ => hasH264Decoder
                ? RemoteVideoCodecs.Jpeg | RemoteVideoCodecs.H264AnnexB
                : RemoteVideoCodecs.Jpeg
        };
    }

    internal static RemoteDeviceCapabilities ResolveLocalViewerCapabilities(
        RemoteDeviceCapabilities baselineCapabilities,
        bool enableShortGopH264)
    {
        RemoteDeviceCapabilities capabilities =
            baselineCapabilities &
            ~RemoteDeviceCapabilities.ShortGopH264;
        if (!enableShortGopH264 ||
            (capabilities &
                ShortGopH264PrerequisiteCapabilities) !=
            ShortGopH264PrerequisiteCapabilities)
        {
            return capabilities;
        }

        return capabilities |
            RemoteDeviceCapabilities.ShortGopH264;
    }

    internal static LowLatencyVideoFeatures
        ResolveNegotiatedLowLatencyVideoFeatures(
            RemoteDeviceCapabilities remoteCapabilities) =>
            LowLatencyVideoFeatureNegotiation.FromCapabilities(
                remoteCapabilities &
                    LocalViewerCapabilities);

    private static string FormatViewerVideoCapabilityLog(
        RemoteVideoCodecs supportedVideoCodecs,
        ViewerVideoMode videoMode,
        string? ffmpegPath,
        bool hasNativeHardwareDecoder)
    {
        string formattedCodecs = FormatVideoCodecs(supportedVideoCodecs);
        string decoderDetail =
            FormatLocalH264DecoderAvailability(
                hasNativeHardwareDecoder,
                ffmpegPath);
        return videoMode switch
        {
            ViewerVideoMode.StableJpeg => $"查看端视频能力：{formattedCodecs}，稳定 JPEG 模式",
            ViewerVideoMode.ForceH264 =>
                $"查看端视频能力：{formattedCodecs}，旧 H.264 选项已兼容自动回退，{decoderDetail}",
            _ => supportedVideoCodecs.HasFlag(
                    RemoteVideoCodecs.H264AnnexB)
                ? $"查看端视频能力：{formattedCodecs}，自动低延迟" +
                    $"模式，{decoderDetail}"
                : $"查看端视频能力：{formattedCodecs}，自动低延迟" +
                    "模式，本机无 H.264 解码器，使用 JPEG"
        };
    }

    private static string FormatLocalH264DecoderAvailability(
        bool hasNativeHardwareDecoder,
        string? ffmpegPath)
    {
        string native = hasNativeHardwareDecoder
            ? "MF/D3D11 硬解将在首个恢复帧实测"
            : "MF/D3D11 硬解不可用";
        string ffmpeg = string.IsNullOrWhiteSpace(
            ffmpegPath)
                ? "ffmpeg 回退不可用"
                : $"ffmpeg 回退：{ffmpegPath}";
        return $"{native}，{ffmpeg}";
    }

    private enum ReturnedClipboardFileRequestMode
    {
        Standard,
        DragOut
    }

    private sealed class ReturnedClipboardFileRequestContext
    {
        private readonly object _previewLock = new();
        private readonly object _idleTimeoutLock = new();
        private readonly object _rejectLock = new();
        private readonly TaskCompletionSource<bool> _requestSendCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ExpectedTransfer[]? _expectedTransfers;
        private int _expectedTransferIndex;
        private System.Threading.Timer? _idleTimeoutTimer;
        private TimeSpan _idleTimeout;
        private bool _idleTimeoutPaused;
        private long _idleTimeoutLastActivityTimestamp;
        private Action<ReturnedClipboardFileRequestContext>? _idleTimeoutHandler;
        private int _rejectRequested;
        private int _waitCancelled;
        private Task? _rejectSendTask;
        private Task? _previewConfirmationSendTask;
        private ReturnedClipboardFileBatchResult? _rejectDrainResult;

        public ReturnedClipboardFileRequestContext(
            long operationId,
            ReturnedClipboardFileRequestMode mode,
            CancellationTokenSource? ownerConnection,
            bool requiresPreview)
        {
            OperationId = operationId;
            Mode = mode;
            OwnerConnection = ownerConnection;
            RequiresPreview = requiresPreview;
            Completion = new TaskCompletionSource<ReturnedClipboardFileBatchResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public long OperationId { get; }

        public ReturnedClipboardFileRequestMode Mode { get; }

        public CancellationTokenSource? OwnerConnection { get; }

        public bool RequiresPreview { get; }

        public TaskCompletionSource<ReturnedClipboardFileBatchResult> Completion { get; }

        public bool IsWaitCancelled => Volatile.Read(ref _waitCancelled) != 0;

        public bool IsRejectRequested => Volatile.Read(ref _rejectRequested) != 0;

        public bool HasAcceptedPreview
        {
            get
            {
                lock (_previewLock)
                {
                    return _expectedTransfers is not null;
                }
            }
        }

        public int ExpectedTransferCount
        {
            get
            {
                lock (_previewLock)
                {
                    return _expectedTransfers?.Length ?? 0;
                }
            }
        }

        public bool AllExpectedTransfersStarted
        {
            get
            {
                lock (_previewLock)
                {
                    return _expectedTransfers is not null &&
                        _expectedTransferIndex == _expectedTransfers.Length;
                }
            }
        }

        public bool TryBindAcceptedPreview(
            IReadOnlyList<FileTransferConfirmationItem> items,
            out string? failureMessage)
        {
            ExpectedTransfer[] transfers = items
                .Select(item => new ExpectedTransfer(
                    item.TransferName?.Trim() ?? string.Empty,
                    item.Kind?.Trim() ?? string.Empty,
                    item.SizeBytes))
                .ToArray();
            if (transfers.Length == 0 || transfers.Any(item => string.IsNullOrWhiteSpace(item.Name)))
            {
                failureMessage = "远端文件清单包含无效文件名，已取消回传。";
                return false;
            }

            if (transfers.Any(item => item.Kind is not ("文件" or "文件夹")))
            {
                failureMessage = "远端文件清单包含未知项目类型，已取消回传。";
                return false;
            }

            if (transfers.Any(item => item.Kind == "文件夹" &&
                !item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
            {
                failureMessage = "远端文件夹清单包含无效的 zip 传输名称，已取消回传。";
                return false;
            }

            if (transfers.Any(item => item.SizeBytes < 0 ||
                item.SizeBytes > RemoteMessageCodec.MaxFileTransferBytes))
            {
                failureMessage = "远端文件清单包含超出接收上限的项目，已取消回传。";
                return false;
            }

            lock (_previewLock)
            {
                if (_expectedTransfers is not null)
                {
                    failureMessage = "远端重复发送文件清单，已取消本次回传。";
                    return false;
                }

                _expectedTransfers = transfers;
                _expectedTransferIndex = 0;
            }

            failureMessage = null;
            return true;
        }

        public bool TryReserveExpectedTransfer(
            string? fileName,
            long fileLength,
            out string? rejectionReason)
        {
            lock (_previewLock)
            {
                if (_expectedTransfers is null)
                {
                    if (RequiresPreview)
                    {
                        rejectionReason = "远端尚未发送并确认文件清单，已拒绝提前开始的文件回传。";
                        return false;
                    }

                    rejectionReason = null;
                    return true;
                }

                if (_expectedTransferIndex >= _expectedTransfers.Length)
                {
                    rejectionReason = "远端发送了未列入确认清单的额外文件，已拒绝接收。";
                    return false;
                }

                ExpectedTransfer expected = _expectedTransfers[_expectedTransferIndex];
                if (!string.Equals(expected.Name, fileName, StringComparison.Ordinal))
                {
                    rejectionReason = $"远端发送的文件与确认清单不一致（应为 {expected.Name}），已拒绝接收。";
                    return false;
                }

                if (expected.Kind == "文件" && expected.SizeBytes != fileLength)
                {
                    rejectionReason = $"远端发送的文件大小与确认清单不一致（应为 {expected.SizeBytes} 字节），已拒绝接收。";
                    return false;
                }

                // Windows reports zero when it deliberately defers the directory walk until
                // after confirmation. Zero is therefore "unknown" and remains protected by the
                // protocol-wide 1 GiB limit; bind peers that supplied an actual source size.
                if (expected.Kind == "文件夹" && expected.SizeBytes > 0 &&
                    fileLength > FileTransferConfirmation.GetMaximumDirectoryArchiveSize(expected.SizeBytes))
                {
                    rejectionReason = "远端发送的文件夹压缩包明显大于确认清单，已拒绝接收。";
                    return false;
                }

                _expectedTransferIndex++;
                rejectionReason = null;
                return true;
            }
        }

        private readonly record struct ExpectedTransfer(
            string Name,
            string Kind,
            long SizeBytes);

        public void StartIdleTimeout(
            TimeSpan timeout,
            Action<ReturnedClipboardFileRequestContext> timeoutHandler)
        {
            ArgumentNullException.ThrowIfNull(timeoutHandler);
            lock (_idleTimeoutLock)
            {
                if (Completion.Task.IsCompleted || _idleTimeoutTimer is not null)
                {
                    return;
                }

                _idleTimeout = timeout;
                _idleTimeoutHandler = timeoutHandler;
                Volatile.Write(ref _idleTimeoutLastActivityTimestamp, Stopwatch.GetTimestamp());
                _idleTimeoutTimer = new System.Threading.Timer(
                    _ => OnIdleTimeoutTimer(),
                    state: null,
                    timeout,
                    Timeout.InfiniteTimeSpan);
            }
        }

        public void TouchIdleTimeout()
        {
            // File chunks can arrive several times per millisecond. Updating one atomic timestamp
            // keeps this hot path allocation-free and avoids a kernel timer re-registration for
            // every chunk. The timer callback lazily schedules only the remaining idle interval.
            Interlocked.Exchange(ref _idleTimeoutLastActivityTimestamp, Stopwatch.GetTimestamp());
        }

        public void PauseIdleTimeout()
        {
            lock (_idleTimeoutLock)
            {
                _idleTimeoutPaused = true;
                try
                {
                    _idleTimeoutTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        public void ResumeIdleTimeout()
        {
            lock (_idleTimeoutLock)
            {
                _idleTimeoutPaused = false;
                Volatile.Write(ref _idleTimeoutLastActivityTimestamp, Stopwatch.GetTimestamp());
                try
                {
                    _idleTimeoutTimer?.Change(_idleTimeout, Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        public void StopIdleTimeout()
        {
            System.Threading.Timer? timer;
            lock (_idleTimeoutLock)
            {
                timer = _idleTimeoutTimer;
                _idleTimeoutTimer = null;
                _idleTimeoutPaused = false;
                _idleTimeoutHandler = null;
            }

            timer?.Dispose();
        }

        public void BeginRejectDrain(
            ReturnedClipboardFileBatchResult result,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(result);
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            lock (_rejectLock)
            {
                _rejectDrainResult ??= result;
            }

            lock (_idleTimeoutLock)
            {
                if (_idleTimeoutTimer is null || _idleTimeoutHandler is null)
                {
                    return;
                }

                _idleTimeout = timeout;
                Volatile.Write(ref _idleTimeoutLastActivityTimestamp, Stopwatch.GetTimestamp());
                if (!_idleTimeoutPaused)
                {
                    try
                    {
                        _idleTimeoutTimer.Change(timeout, Timeout.InfiniteTimeSpan);
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }
        }

        public bool TryGetRejectDrainResult(out ReturnedClipboardFileBatchResult result)
        {
            lock (_rejectLock)
            {
                result = _rejectDrainResult!;
                return result is not null;
            }
        }

        private void OnIdleTimeoutTimer()
        {
            Action<ReturnedClipboardFileRequestContext>? timeoutHandler;
            lock (_idleTimeoutLock)
            {
                if (_idleTimeoutTimer is null || _idleTimeoutPaused ||
                    _idleTimeoutHandler is null)
                {
                    return;
                }

                while (true)
                {
                    long lastActivityTimestamp = Volatile.Read(ref _idleTimeoutLastActivityTimestamp);
                    TimeSpan remaining =
                        _idleTimeout - Stopwatch.GetElapsedTime(lastActivityTimestamp);
                    if (remaining > TimeSpan.Zero)
                    {
                        try
                        {
                            _idleTimeoutTimer.Change(remaining, Timeout.InfiniteTimeSpan);
                        }
                        catch (ObjectDisposedException)
                        {
                        }

                        return;
                    }

                    // Claim this observed deadline atomically. If a chunk refreshed the timestamp
                    // while it was being checked, retry and schedule from that newer activity.
                    if (Interlocked.CompareExchange(
                            ref _idleTimeoutLastActivityTimestamp,
                            long.MinValue,
                            lastActivityTimestamp) == lastActivityTimestamp)
                    {
                        break;
                    }
                }

                timeoutHandler = _idleTimeoutHandler;
                _idleTimeoutHandler = null;
            }

            timeoutHandler(this);
        }

        public void CompleteRequestSend(bool sent)
        {
            _requestSendCompletion.TrySetResult(sent);
        }

        public void MarkRejectRequested()
        {
            Interlocked.Exchange(ref _rejectRequested, 1);
        }

        public Task EnsureRejectSentAsync(Func<Task> sendAsync)
        {
            ArgumentNullException.ThrowIfNull(sendAsync);
            MarkRejectRequested();
            lock (_rejectLock)
            {
                return _rejectSendTask ??= SendRejectAfterRequestAsync(
                    _previewConfirmationSendTask,
                    sendAsync);
            }
        }

        public async Task<bool> TrySendPreviewConfirmationAsync(Func<Task> sendAsync)
        {
            ArgumentNullException.ThrowIfNull(sendAsync);
            Task confirmationSendTask;
            lock (_rejectLock)
            {
                if (IsRejectRequested)
                {
                    return false;
                }

                confirmationSendTask = _previewConfirmationSendTask ??= sendAsync();
            }

            await confirmationSendTask.ConfigureAwait(false);
            return true;
        }

        private async Task SendRejectAfterRequestAsync(
            Task? previewConfirmationSendTask,
            Func<Task> sendAsync)
        {
            if (await _requestSendCompletion.Task.ConfigureAwait(false))
            {
                if (previewConfirmationSendTask is not null)
                {
                    await previewConfirmationSendTask.ConfigureAwait(false);
                }

                await sendAsync().ConfigureAwait(false);
            }
        }

        public void MarkWaitCancelled()
        {
            Interlocked.Exchange(ref _waitCancelled, 1);
        }
    }
}
