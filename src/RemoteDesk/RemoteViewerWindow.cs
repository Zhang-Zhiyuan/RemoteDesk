using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace RemoteDesk;

internal enum RemoteDragOutStage
{
    None,
    Armed,
    Pulling,
    LocalDragging
}

internal enum RemoteDragOutCaptureLossAction
{
    None,
    ReleaseRemoteButton,
    CancelRemoteGesture,
    MarkMouseReleased
}

internal readonly record struct RemoteViewerRenderTelemetrySnapshot(
    long DirectHardwarePresentedFrames,
    long DirectHardwareOccludedFrames,
    long DirectHardwareSkippedFrames,
    long DirectHardwarePresentationFailures,
    long DirectPresentationQualificationAttempts,
    long DirectPresentationQualificationSuccesses,
    long DirectPresentationQualificationFailures,
    bool JpegFallbackRequested,
    double TotalDirectHardwareDecodeMilliseconds,
    double TotalDirectHardwareDecodeToPresentMilliseconds,
    double TotalDirectHardwareReceiveToPresentMilliseconds,
    double MaximumDirectHardwareReceiveToPresentMilliseconds,
    FixedLatencyHistogramSnapshot DirectHardwareDecodeLatency,
    FixedLatencyHistogramSnapshot DirectHardwareDecodeToPresentLatency,
    FixedLatencyHistogramSnapshot DirectHardwareReceiveToPresentLatency,
    bool DirectHardwarePathDisabled,
    string LastDirectHardwareFailureDetail,
    string LastRenderedDecoderBackend);

internal readonly record struct RemoteViewerFullScreenRestoreState(
    FormBorderStyle BorderStyle,
    FormWindowState WindowState,
    Rectangle WindowedBounds,
    bool StatusFooterVisible,
    bool ProgressBarVisible);

internal sealed class RemoteViewerWindow : Form
{
    private const int H264DecodeMissFallbackThreshold = 8;
    private const int H264KeyFrameRequestInterval = 3;
    private const int DirectH264FramesPerSecond = 60;
    internal const int DirectPresentationQualificationFrames = 3;
    internal const int H264KeyFrameRequestMinIntervalMs = 550;
    internal const int MaxQueuedH264Frames = 4;
    private const int MaxPendingMediaFoundationInputs = 8;
    private const int MaxClipboardFilePasteCount = 32;
    private const int ClipboardPullDelayMs = 550;
    private const int AutomaticRemoteFilePullStatusSuppressMs = 5000;
    private const int DragDropEdgeTolerancePixels = 8;
    private const int RemoteDragOutEdgeExitPixels = 12;
    private const int RemoteDragOutCancelSettleDelayMs = 120;
    private const int RemoteDragOutClipboardSettleDelayMs = 550;
    private const int DragStatusUpdateMinIntervalMs = 120;
    private const int RemoteDropMouseMoveFreshMs = 150;
    private const int RemoteDropFocusDelayMs = 80;
    private const int WmDisplayChange = 0x007E;
    private const int WmSettingChange = 0x001A;
    internal const int WmKeyDown = 0x0100;
    internal const int WmKeyUp = 0x0101;
    internal const int WmSysKeyDown = 0x0104;
    internal const int WmSysKeyUp = 0x0105;
    private const int WhKeyboardLowLevel = 13;
    private const uint LowLevelKeyboardExtended = 0x01;
    private const uint LowLevelKeyboardInjected = 0x10;
    private const long SpiSetWorkArea = 0x002F;
    internal const string RemoteDragOutDataFormat = "RemoteDesk.RemoteDragOut.v1";
    private static readonly TimeSpan FrameStatsInterval = TimeSpan.FromSeconds(2);
    private static readonly Color ViewerBackColor = Color.FromArgb(2, 6, 23);
    private static readonly Color StatusBackColor = Color.FromArgb(11, 18, 32);
    private static readonly Color SuccessTextColor = Color.FromArgb(134, 239, 172);
    private static readonly Color DangerTextColor = Color.FromArgb(252, 165, 165);
    private static readonly Color MutedTextColor = Color.FromArgb(203, 213, 225);
    private static readonly WindowsDiagnosticLog DiagnosticLog =
        WindowsDiagnosticLog.CreateDefault();

    private readonly RemoteViewerClient _client;
    private readonly BufferedPictureBox _pictureBox;
    private readonly Panel _statusFooterPanel;
    private readonly StatusBarControl _statusBar;
    private readonly FlowLayoutPanel _fileTransferActionsPanel;
    private readonly Button _pullRemoteFilesButton;
    private readonly Button _openReceivedFilesButton;
    private readonly Button _remoteInputMethodButton;
    private readonly Button _switchCaptureTargetButton;
    private readonly Button _displayScaleButton;
    private readonly Button _fullScreenButton;
    private readonly ProgressBar _remoteFilePullProgressBar;
    private readonly ToolTip _toolTip;
    private readonly ContextMenuStrip _statusMenu;
    private readonly object _pendingFrameLock = new();
    private readonly object _h264DecoderLock = new();
    private readonly object _h264SubmissionLock = new();
    private readonly object _d3d11PresenterLock = new();
    private readonly object _frameStatsLock = new();
    private readonly CapturePresentationTransitionGate
        _capturePresentationTransitionGate = new();
    private readonly object _clipboardPullLock = new();
    private readonly Queue<PooledRemoteFrame> _h264Frames = new();
    private readonly Dictionary<long, PendingH264Submission> _h264Submissions = new();
    private readonly Dictionary<long, PendingMediaFoundationInput>
        _pendingMediaFoundationInputs = new();
    private readonly LatestFrameMailbox<DecodedRemoteFrame> _decodedFrameMailbox = new();
    private readonly ArrayPool<byte> _encodedFramePool;
    private readonly RemoteInputOwnershipTracker
        _remoteInputOwnership = new();
    private readonly List<CaptureTargetInfo> _captureTargets = [];
    private readonly LowLevelKeyboardProcedure
        _lowLevelKeyboardProcedure;

    private Image? _currentImage;
    private FfmpegH264Decoder? _h264Decoder;
    private MediaFoundationD3D11H264Decoder? _mediaFoundationH264Decoder;
    private D3D11HwndVideoPresenter? _d3d11VideoPresenter;
    private Size _h264DecoderSize = Size.Empty;
    private Size _mediaFoundationH264DecoderSize = Size.Empty;
    private Size _d3d11VideoPresenterSourceSize = Size.Empty;
    private nint _d3d11VideoPresenterDevicePointer;
    private long _d3d11VideoPresenterDecoderGeneration;
    private long _d3d11VideoPresenterHandleGeneration;
    private CancellationTokenSource? _clipboardPullCancellation;
    private PooledRemoteFrame? _pendingFrame;
    private bool _isRenderingFrame;
    private bool _inputEnabled = true;
    private int _captureTargetAvailable = 1;
    private int _capturePresentationTransitionPending;
    private bool _clipboardTextEnabled;
    private bool _filePasteEnabled;
    private bool _fileDropPasteEnabled;
    private bool _remoteFilePullEnabled;
    private volatile bool _isClosing;
    private bool _closeFromDisconnect;
    private bool _h264FallbackRequested;
    private bool _waitingForH264RecoveryFrame;
    private bool _waitingForViewerHandle;
    private bool _dragFileTransferInProgress;
    private bool _remoteFilePullInProgress;
    private bool _remoteDragOutMouseReleased;
    private bool _suppressRemoteFilePullShortcutKeyUp;
    private bool _suppressFullScreenShortcutKeyUp;
    private bool _fileDropRegistrationUnavailable;
    private bool _isAndroidRemote;
    private bool _isWindowsRemote;
    private bool _remoteInputMethodSwitchInProgress;
    private bool _captureTargetSelectionEnabled;
    private bool _captureTargetSwitchInProgress;
    private bool _allowInjectedKeyboardCaptureForEntityTests;
    private IDataObject? _activeFileDragData;
    private CancellationTokenSource? _remoteDragOutCancellation;
    private bool? _activeFileDragHasFiles;
    private long _remoteImageSizePacked;
    private long _directFrameUiSignature = long.MinValue;
    private RemoteFrameEncoding? _lastRenderedEncoding;
    private string _lastRenderedDecoderBackend = string.Empty;
    private int _h264DecodeMisses;
    private long _lastMouseMoveAt;
    private long _lastFileDragStatusAt;
    private long _lastH264KeyFrameRequestAt;
    private long _nextH264SubmissionId;
    private long _nextMediaFoundationDecoderGeneration;
    private long _activeMediaFoundationDecoderGeneration;
    private int _resetH264DecoderOnRecovery;
    private int _mediaFoundationDirectPathDisabled;
    private int _d3d11PresenterMatchesCurrentDecoder;
    private int _decodedDispatchRetryPending;
    private int _deferredRendererRetryPending;
    private int _d3d11TargetRetryCount;
    private int _directPresentationQualified;
    private int _directPresentationQualificationCount;
    // Fit large desktops, but do not stretch small desktops' text by default.
    private int _allowDisplayUpscaling;
    private int _pictureBoxHandleAvailable;
    private int _pictureBoxClientWidth;
    private int _pictureBoxClientHeight;
    private long _nextMediaFoundationSampleTime100Nanoseconds;
    private long _nextDecodedFrameDispatchToken;
    private long _postedDecodedFrameDispatchToken;
    private long _pictureBoxHandleGeneration;
    private long _lastAutomaticRemoteFilePullAt;
    private long _capturePresentationGeneration;
    private string? _captureTargetId;
    private string? _selectedCaptureTargetId;
    private long _captureTargetStatusConnectionGeneration =
        long.MinValue;
    private int _captureTargetHostGeneration = int.MinValue;
    private int _framesInWindow;
    private long _bytesInWindow;
    private long _roundTripMilliseconds = -1;
    private double _captureMillisecondsInWindow;
    private double _encodeMillisecondsInWindow;
    private double _decodeMillisecondsInWindow;
    private double _receiveToDecodeMillisecondsInWindow;
    private double _decodeToPresentMillisecondsInWindow;
    private double _receiveToPresentMillisecondsInWindow;
    private int _captureTimingSamplesInWindow;
    private int _encodeTimingSamplesInWindow;
    private int _viewerPipelineTimingSamplesInWindow;
    private int _frameStatsReportsSinceDiagnostic;
    private long _frameWindowStartedAt = Stopwatch.GetTimestamp();
    private long _directHardwarePresentedFrameCount;
    private long _directHardwareOccludedFrameCount;
    private long _directHardwareSkippedFrameCount;
    private long _directHardwarePresentationFailureCount;
    private long _directPresentationQualificationAttemptCount;
    private long _directPresentationQualificationSuccessCount;
    private long _directPresentationQualificationFailureCount;
    private int _jpegFallbackRequested;
    private long _directHardwareDecodeStopwatchTicks;
    private long _directHardwareDecodeToPresentStopwatchTicks;
    private long _directHardwareReceiveToPresentStopwatchTicks;
    private long _directHardwareMaximumReceiveToPresentStopwatchTicks;
    private readonly FixedLatencyHistogram
        _directHardwareDecodeLatency = new();
    private readonly FixedLatencyHistogram
        _directHardwareDecodeToPresentLatency = new();
    private readonly FixedLatencyHistogram
        _directHardwareReceiveToPresentLatency = new();
    private string _lastDirectHardwareFailureDetail =
        string.Empty;
    private string _lastFileDragStatusText = string.Empty;
    private RemoteFilePullStatusStage _remoteFilePullStatusStage;
    private Point? _lastMouseMovePoint;
    private Point? _latestFileDragRemotePoint;
    private Point? _remoteDragOutPressPicturePoint;
    private Point? _remoteDragOutLastRemotePoint;
    private RemoteDragOutStage _remoteDragOutStage;
    private long _remoteDragOutOperationGeneration;
    private bool _responsiveLayoutReady;
    private bool _updatingStatusFooterLayout;
    private int _responsiveRefreshPending;
    private string _responsiveScreenDeviceName = string.Empty;
    private Rectangle _responsiveScreenWorkingArea = Rectangle.Empty;
    private RemoteViewerFullScreenRestoreState? _fullScreenRestoreState;
    private nint _lowLevelKeyboardHook;

    public RemoteViewerWindow(
        RemoteViewerClient client,
        string title,
        bool inputEnabled,
        bool clipboardTextEnabled,
        bool filePasteEnabled,
        bool fileDropPasteEnabled,
        bool remoteFilePullEnabled,
        bool isAndroidRemote,
        ArrayPool<byte>? encodedFramePool = null)
    {
        _client = client;
        _lowLevelKeyboardProcedure =
            LowLevelKeyboardCallback;
        _encodedFramePool =
            encodedFramePool ?? ArrayPool<byte>.Shared;
        _inputEnabled = inputEnabled;
        _clipboardTextEnabled = clipboardTextEnabled;
        _filePasteEnabled = filePasteEnabled;
        _fileDropPasteEnabled = fileDropPasteEnabled;
        _remoteFilePullEnabled = remoteFilePullEnabled;
        _isAndroidRemote = isAndroidRemote;
        _isWindowsRemote = false;
        SuspendLayout();
        Text = string.IsNullOrWhiteSpace(title) ? "RemoteDesk 远程桌面" : title;
        MinimumSize = Size.Empty;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(ResponsiveWindowLayout.DesignDpi, ResponsiveWindowLayout.DesignDpi);
        KeyPreview = true;
        BackColor = ViewerBackColor;
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        _pictureBox = new BufferedPictureBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            SizeMode = PictureBoxSizeMode.Zoom,
            TabStop = true,
            Cursor = inputEnabled ? Cursors.Default : Cursors.No,
            LocalImeEnabled = isAndroidRemote && inputEnabled,
            ReadInputGeneration = () => _client.InputConnectionGeneration,
            TextCommitted = SendCommittedAndroidText
        };

        _statusBar = new StatusBarControl
        {
            Dock = DockStyle.Fill,
            Height = 30,
            BackColor = StatusBackColor,
            Padding = new Padding(10, 0, 10, 0),
            StatusColor = MutedTextColor,
            StatusText = "正在等待远程画面...",
            ManageOwnHeight = false
        };

        _pullRemoteFilesButton = CreateStatusActionButton("取回文件");
        _pullRemoteFilesButton.Enabled = remoteFilePullEnabled;
        _openReceivedFilesButton = CreateStatusActionButton("接收目录");
        _remoteInputMethodButton =
            CreateStatusActionButton("远端输入法");
        _switchCaptureTargetButton =
            CreateStatusActionButton("切换屏幕");
        _switchCaptureTargetButton.Visible = false;
        _displayScaleButton =
            CreateStatusActionButton("允许放大");
        _fullScreenButton = CreateStatusActionButton("全屏");
        _fileTransferActionsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = StatusBackColor,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        _fileTransferActionsPanel.Controls.Add(_pullRemoteFilesButton);
        _fileTransferActionsPanel.Controls.Add(_openReceivedFilesButton);
        _fileTransferActionsPanel.Controls.Add(
            _remoteInputMethodButton);
        _fileTransferActionsPanel.Controls.Add(
            _switchCaptureTargetButton);
        _fileTransferActionsPanel.Controls.Add(
            _displayScaleButton);
        _fileTransferActionsPanel.Controls.Add(_fullScreenButton);
        _statusFooterPanel = new ViewerFooterPanel
        {
            Dock = DockStyle.Bottom,
            Height = 30,
            BackColor = StatusBackColor,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        ConfigureStatusFooterChildren(
            _statusFooterPanel,
            _statusBar,
            _fileTransferActionsPanel);
        _fileTransferActionsPanel.SizeChanged += (_, _) => UpdateStatusFooterLayout();
        _statusFooterPanel.ClientSizeChanged += (_, _) => UpdateStatusFooterLayout();

        _remoteFilePullProgressBar = new ProgressBar
        {
            Dock = DockStyle.Bottom,
            Height = 3,
            Margin = new Padding(0),
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 0,
            Visible = false
        };
        _toolTip = new ToolTip
        {
            AutoPopDelay = 12000,
            InitialDelay = 350,
            ReshowDelay = 100,
            ShowAlways = true
        };
        _statusMenu = new ContextMenuStrip();
        _statusMenu.Items.Add("复制当前状态", null, async (_, _) => await CopyStatusAsync());
        _statusMenu.Items.Add("打开本机接收目录", null, (_, _) => OpenReceivedFilesDirectory());
        _statusBar.ContextMenuStrip = _statusMenu;
        ConfigureFilePullToolTips();

        Controls.Add(_pictureBox);
        Controls.Add(_remoteFilePullProgressBar);
        Controls.Add(_statusFooterPanel);

        _client.FrameReceived += OnFrameReceived;
        _client.Log += OnClientLog;
        _client.RoundTripUpdated += OnRoundTripUpdated;
        _client.CaptureTargetSelectionChanged +=
            OnCaptureTargetSelectionChanged;
        _client.CaptureTargetAvailabilityChanged +=
            OnCaptureTargetAvailabilityChanged;
        _client.ClipboardStatusReceived += OnClipboardStatusReceived;
        _client.FileTransferStatusReceived += OnFileTransferStatusReceived;
        _client.RemoteClipboardFileRequestPendingChanged += OnRemoteClipboardFileRequestPendingChanged;

        if (_client.LatestCaptureTargetAvailability is { } latestAvailability &&
            _client.IsCurrentCaptureTargetAvailabilityUpdate(
                latestAvailability))
        {
            OnCaptureTargetAvailabilityChanged(
                latestAvailability);
        }

        _pictureBox.MouseDown += PictureBox_MouseDown;
        _pictureBox.MouseUp += PictureBox_MouseUp;
        _pictureBox.MouseMove += PictureBox_MouseMove;
        _pictureBox.MouseWheel += PictureBox_MouseWheel;
        _pictureBox.MouseCaptureChanged += PictureBox_MouseCaptureChanged;
        _pictureBox.KeyDown += PictureBox_KeyDown;
        _pictureBox.KeyPress += PictureBox_KeyPress;
        _pictureBox.KeyUp += PictureBox_KeyUp;
        _pictureBox.LostFocus += (_, _) => ReleaseAllRemoteInputs();
        _pictureBox.DragEnter += RemoteViewerFileDragEnter;
        _pictureBox.DragOver += RemoteViewerFileDragOver;
        _pictureBox.DragDrop += RemoteViewerFileDragDrop;
        _pictureBox.DragLeave += RemoteViewerFileDragLeave;
        _pictureBox.ClientSizeChanged += PictureBox_ClientSizeChanged;
        _pictureBox.HandleCreated +=
            PictureBox_HandleCreated;
        _pictureBox.HandleDestroyed +=
            PictureBox_HandleDestroyed;
        _pullRemoteFilesButton.Click += async (_, _) => await PullRemoteClipboardFilesAsync();
        _openReceivedFilesButton.Click += (_, _) => OpenReceivedFilesDirectory();
        _remoteInputMethodButton.Click +=
            async (_, _) =>
                await SwitchRemoteInputMethodAsync();
        _switchCaptureTargetButton.Click +=
            async (_, _) =>
                await SwitchCaptureTargetAsync();
        _displayScaleButton.Click +=
            (_, _) => ToggleDisplayScaleMode();
        _fullScreenButton.Click += (_, _) => ToggleFullScreen();
        UpdateRemoteInputMethodControls();
        ResumeLayout(performLayout: false);
        PerformLayout();
        CachePictureBoxClientSize();
        if (_pictureBox.IsHandleCreated)
        {
            UpdatePictureBoxFileDropRegistration();
            Volatile.Write(
                ref _pictureBoxHandleAvailable,
                1);
        }

        Shown +=
            (_, _) =>
            {
                TryInstallSystemKeyboardCapture();
                _pictureBox.Focus();
            };
    }

    protected override void OnLoad(EventArgs args)
    {
        base.OnLoad(args);
        ApplyDpiMetrics(DeviceDpi);
        ResponsiveWindowLayout.ApplyTo(
            this,
            logicalPreferredSize: new Size(1180, 760),
            logicalMinimumSize: new Size(480, 320),
            applyPreferredBounds: true);
        _responsiveLayoutReady = true;
        RememberResponsiveScreen();
    }

    protected override void OnHandleCreated(EventArgs args)
    {
        base.OnHandleCreated(args);
        if (_decodedFrameMailbox.HasPendingDispatch)
        {
            PostLatestRemoteImage();
        }

        ResumeDeferredFrameRenderer();
    }

    protected override void OnHandleDestroyed(EventArgs args)
    {
        Interlocked.Exchange(ref _postedDecodedFrameDispatchToken, 0);
        base.OnHandleDestroyed(args);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs args)
    {
        base.OnDpiChanged(args);
        ApplyDpiMetrics(args.DeviceDpiNew);
        if (_fullScreenRestoreState is not null)
        {
            Screen targetScreen =
                Screen.FromRectangle(args.SuggestedRectangle);
            Bounds = targetScreen.Bounds;
            RememberResponsiveScreen(targetScreen);
            return;
        }

        ResponsiveWindowLayout.ApplyTo(
            this,
            logicalPreferredSize: new Size(1180, 760),
            logicalMinimumSize: new Size(480, 320),
            applyPreferredBounds: false,
            suggestedBounds: args.SuggestedRectangle);
        RememberResponsiveScreen();
    }

    protected override void OnLocationChanged(EventArgs args)
    {
        base.OnLocationChanged(args);
        if (_responsiveLayoutReady &&
            !_isClosing &&
            _fullScreenRestoreState is null &&
            WindowState == FormWindowState.Normal)
        {
            RefreshResponsiveMinimumSizeIfScreenChanged();
        }
    }

    protected override void OnResizeEnd(EventArgs args)
    {
        base.OnResizeEnd(args);
        RefreshResponsiveWindowBounds();
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        bool workAreaChanged = message.Msg == WmSettingChange &&
            message.WParam.ToInt64() == SpiSetWorkArea;
        if (!_responsiveLayoutReady || _isClosing ||
            (message.Msg != WmDisplayChange && !workAreaChanged))
        {
            return;
        }

        QueueResponsiveWindowRefresh();
    }

    protected override bool ProcessKeyPreview(ref Message message)
    {
        Keys keyData =
            (Keys)(int)message.WParam |
            ModifierKeys;
        if (TryHandleReservedKeyboardMessage(
                message.Msg,
                keyData))
        {
            return true;
        }

        if (TryForwardNativeKeyboardMessage(
                ref message,
                keyData))
        {
            return true;
        }

        return base.ProcessKeyPreview(ref message);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (TryHandleReservedKeyboardMessage(
                message.Msg,
                keyData))
        {
            return true;
        }

        if (TryForwardNativeKeyboardMessage(
                ref message,
                keyData))
        {
            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    private bool TryHandleReservedKeyboardMessage(
        int messageId,
        Keys keyData)
    {
        bool keyDown =
            messageId is WmKeyDown or
                WmSysKeyDown;
        bool keyUp =
            messageId is WmKeyUp or
                WmSysKeyUp;
        Keys keyCode =
            keyData & Keys.KeyCode;
        if (keyUp &&
            _suppressFullScreenShortcutKeyUp &&
            keyCode == Keys.F11)
        {
            _suppressFullScreenShortcutKeyUp =
                false;
            return true;
        }

        if (keyUp &&
            _suppressRemoteFilePullShortcutKeyUp &&
            keyCode == Keys.R)
        {
            _suppressRemoteFilePullShortcutKeyUp =
                false;
            return true;
        }

        if (!keyDown)
        {
            return false;
        }

        if (IsFullScreenShortcut(keyData))
        {
            _suppressFullScreenShortcutKeyUp =
                _pictureBox.ContainsFocus;
            ReleaseAllRemoteInputs();
            ToggleFullScreen();
            return true;
        }

        if (ShouldCancelRemoteDragOutWithEscape(
                keyData,
                _remoteDragOutStage))
        {
            ReleaseAllRemoteInputs();
            CancelRemoteDragOut(
                "已取消从远程画面拖出文件。",
                cancelTransfer: true);
            return true;
        }

        if (!ShouldConsumeRemoteFilePullShortcut(
                _client.IsConnected,
                new KeyEventArgs(keyData)))
        {
            return false;
        }

        _suppressRemoteFilePullShortcutKeyUp =
            _pictureBox.ContainsFocus;
        ReleaseAllRemoteInputs();
        _ = PullRemoteClipboardFilesAsync();
        return true;
    }

    internal static bool IsFullScreenShortcut(Keys keyData) =>
        keyData == Keys.F11;

    internal static bool ShouldConsumeRemoteFilePullShortcut(bool connected, KeyEventArgs args)
    {
        return connected && RemoteFilePullUi.IsShortcut(args);
    }

    internal static bool TryCreateRawKeyboardCommand(
        int messageId,
        int virtualKey,
        nint lParam,
        out RemoteInputCommand command)
    {
        RemoteInputKind kind = messageId switch
        {
            WmKeyDown or WmSysKeyDown =>
                RemoteInputKind.KeyDown,
            WmKeyUp or WmSysKeyUp =>
                RemoteInputKind.KeyUp,
            _ => 0
        };
        if (kind == 0 ||
            virtualKey is <= 0 or > 0xFE)
        {
            command = default;
            return false;
        }

        long nativeFlags = lParam.ToInt64();
        int scanCode =
            (int)((nativeFlags >> 16) & 0xFF);
        var keyboardFlags = scanCode > 0
            ? RemoteKeyboardFlags.HasScanCode
            : RemoteKeyboardFlags.None;
        if (scanCode > 0 &&
            (nativeFlags & (1L << 24)) != 0)
        {
            keyboardFlags |= RemoteKeyboardFlags.Extended;
        }

        command = kind == RemoteInputKind.KeyDown
            ? RemoteInputCommand.KeyDown(
                virtualKey,
                scanCode,
                keyboardFlags)
            : RemoteInputCommand.KeyUp(
                virtualKey,
                scanCode,
                keyboardFlags);
        return true;
    }

    internal static bool TryCreateLowLevelKeyboardCommand(
        int messageId,
        int virtualKey,
        int scanCode,
        uint nativeFlags,
        out RemoteInputCommand command)
    {
        RemoteInputKind kind = messageId switch
        {
            WmKeyDown or WmSysKeyDown =>
                RemoteInputKind.KeyDown,
            WmKeyUp or WmSysKeyUp =>
                RemoteInputKind.KeyUp,
            _ => 0
        };
        if (kind == 0 ||
            virtualKey is <= 0 or > 0xFE ||
            scanCode is < 0 or > byte.MaxValue)
        {
            command = default;
            return false;
        }

        var keyboardFlags = scanCode > 0
            ? RemoteKeyboardFlags.HasScanCode
            : RemoteKeyboardFlags.None;
        if (scanCode > 0 &&
            (nativeFlags &
                LowLevelKeyboardExtended) != 0)
        {
            keyboardFlags |=
                RemoteKeyboardFlags.Extended;
        }

        command = kind == RemoteInputKind.KeyDown
            ? RemoteInputCommand.KeyDown(
                virtualKey,
                scanCode,
                keyboardFlags)
            : RemoteInputCommand.KeyUp(
                virtualKey,
                scanCode,
                keyboardFlags);
        return true;
    }

    private bool TryForwardNativeKeyboardMessage(
        ref Message message,
        Keys keyData)
    {
        if (!TryCreateRawKeyboardCommand(
                message.Msg,
                unchecked((int)message.WParam.ToInt64()),
                message.LParam,
                out RemoteInputCommand command))
        {
            return false;
        }

        return TryForwardKeyboardCommand(
            command,
            keyData);
    }

    private bool TryForwardKeyboardCommand(
        RemoteInputCommand command,
        Keys keyData)
    {
        if (!_client.IsConnected ||
            !_inputEnabled ||
            _isAndroidRemote ||
            !_pictureBox.ContainsFocus ||
            _remoteDragOutStage is
                RemoteDragOutStage.Pulling or
                RemoteDragOutStage.LocalDragging)
        {
            return false;
        }

        if (command.Kind == RemoteInputKind.KeyDown &&
            !IsModifierVirtualKey(command.Data))
        {
            EnsureRemoteModifiersDown(keyData);
        }

        QueueRemotePhysicalKey(command);
        if (command.Kind == RemoteInputKind.KeyDown &&
            (_clipboardTextEnabled || _remoteFilePullEnabled) &&
            TryGetClipboardPullReason(
                new KeyEventArgs(keyData),
                out string clipboardPullReason))
        {
            ScheduleRemoteClipboardPull(clipboardPullReason);
        }

        return true;
    }

    private static bool IsModifierVirtualKey(
        int virtualKey)
    {
        return (Keys)virtualKey is
            Keys.ShiftKey or Keys.LShiftKey or
            Keys.RShiftKey or Keys.ControlKey or
            Keys.LControlKey or Keys.RControlKey or
            Keys.Menu or Keys.LMenu or Keys.RMenu;
    }

    private Keys CreateLowLevelKeyData(
        RemoteInputCommand command)
    {
        Keys keyData =
            (Keys)command.Data;
        if (IsControlVirtualKey(command.Data) ||
            _remoteInputOwnership.AnyPressedKey(
                _client.InputConnectionGeneration,
                key =>
                    IsControlVirtualKey(
                        key.VirtualKey)))
        {
            keyData |= Keys.Control;
        }

        if (IsShiftVirtualKey(command.Data) ||
            _remoteInputOwnership.AnyPressedKey(
                _client.InputConnectionGeneration,
                key =>
                    IsShiftVirtualKey(
                        key.VirtualKey)))
        {
            keyData |= Keys.Shift;
        }

        if (IsAltVirtualKey(command.Data) ||
            _remoteInputOwnership.AnyPressedKey(
                _client.InputConnectionGeneration,
                key =>
                    IsAltVirtualKey(
                        key.VirtualKey)))
        {
            keyData |= Keys.Alt;
        }

        return keyData;
    }

    private static bool IsControlVirtualKey(
        int virtualKey) =>
        (Keys)virtualKey is
            Keys.ControlKey or
            Keys.LControlKey or
            Keys.RControlKey;

    private static bool IsShiftVirtualKey(
        int virtualKey) =>
        (Keys)virtualKey is
            Keys.ShiftKey or
            Keys.LShiftKey or
            Keys.RShiftKey;

    private static bool IsAltVirtualKey(
        int virtualKey) =>
        (Keys)virtualKey is
            Keys.Menu or
            Keys.LMenu or
            Keys.RMenu;

    private nint LowLevelKeyboardCallback(
        int code,
        nint message,
        nint dataPointer)
    {
        if (code >= 0 &&
            !_isClosing &&
            _pictureBox.ContainsFocus)
        {
            try
            {
                LowLevelKeyboardData data =
                    Marshal.PtrToStructure<
                        LowLevelKeyboardData>(
                        dataPointer);
                if (ShouldConsumeOwnInjectedKeyboardEvent(
                        data.Flags,
                        data.ExtraInfo,
                        _allowInjectedKeyboardCaptureForEntityTests))
                {
                    // Suppress only RemoteDesk-tagged SendInput events to
                    // prevent a same-machine feedback loop. Third-party
                    // remote-control and accessibility input still follows
                    // the normal capture path.
                    return (nint)1;
                }

                int messageId =
                    unchecked((int)message.ToInt64());
                if (TryCreateLowLevelKeyboardCommand(
                        messageId,
                        unchecked((int)data.VirtualKey),
                        unchecked((int)data.ScanCode),
                        data.Flags,
                        out RemoteInputCommand command))
                {
                    Keys keyData =
                        CreateLowLevelKeyData(command);
                    if (TryHandleReservedKeyboardMessage(
                            messageId,
                            keyData) ||
                        TryForwardKeyboardCommand(
                            command,
                            keyData))
                    {
                        // Consume the local event while the remote surface
                        // owns keyboard focus. This also captures Windows
                        // shell shortcuts such as Win+Space and Alt+Tab,
                        // which ordinary WinForms key messages cannot
                        // forward reliably.
                        return (nint)1;
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Append(
                    "VIEWER",
                    $"系统级键盘捕获忽略了无效事件：{ex.Message}");
            }
        }

        return CallNextHookEx(
            _lowLevelKeyboardHook,
            code,
            message,
            dataPointer);
    }

    internal static bool ShouldConsumeOwnInjectedKeyboardEvent(
        uint flags,
        nuint extraInfo,
        bool allowOwnInjectedEventsForTests = false) =>
        !allowOwnInjectedEventsForTests &&
        (flags & LowLevelKeyboardInjected) != 0 &&
        extraInfo == InputInjector.InjectedInputMarker;

    private void TryInstallSystemKeyboardCapture()
    {
        if (!OperatingSystem.IsWindows() ||
            _lowLevelKeyboardHook != 0 ||
            _isClosing)
        {
            return;
        }

        nint module = GetModuleHandle(null);
        _lowLevelKeyboardHook =
            SetWindowsHookEx(
                WhKeyboardLowLevel,
                _lowLevelKeyboardProcedure,
                module,
                0);
        if (_lowLevelKeyboardHook != 0)
        {
            return;
        }

        int error =
            Marshal.GetLastWin32Error();
        DiagnosticLog.Append(
            "VIEWER",
            "系统级键盘捕获不可用，已回退窗口键盘消息；" +
            $"Windows 错误 {error}。");
    }

    private void UninstallSystemKeyboardCapture()
    {
        nint hook =
            _lowLevelKeyboardHook;
        _lowLevelKeyboardHook = 0;
        if (hook == 0 ||
            UnhookWindowsHookEx(hook))
        {
            return;
        }

        int error =
            Marshal.GetLastWin32Error();
        DiagnosticLog.Append(
            "VIEWER",
            "释放系统级键盘捕获失败；" +
            $"Windows 错误 {error}。");
    }

    protected override void OnDeactivate(EventArgs args)
    {
        ReleaseAllRemoteInputs();
        base.OnDeactivate(args);
    }

    internal static void ConfigureStatusFooterChildren(
        Panel footer,
        Control status,
        Control actions)
    {
        ArgumentNullException.ThrowIfNull(footer);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(actions);

        footer.Controls.Add(status);
        footer.Controls.Add(actions);
        actions.SendToBack();
    }

    private void QueueResponsiveWindowRefresh()
    {
        if (!IsHandleCreated || Interlocked.Exchange(ref _responsiveRefreshPending, 1) != 0)
        {
            return;
        }

        try
        {
            BeginInvoke((Action)(() =>
            {
                Interlocked.Exchange(ref _responsiveRefreshPending, 0);
                RefreshResponsiveWindowBounds();
            }));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            Interlocked.Exchange(ref _responsiveRefreshPending, 0);
        }
    }

    private void RefreshResponsiveMinimumSizeIfScreenChanged()
    {
        Screen screen = Screen.FromControl(this);
        if (string.Equals(_responsiveScreenDeviceName, screen.DeviceName, StringComparison.Ordinal) &&
            _responsiveScreenWorkingArea == screen.WorkingArea)
        {
            return;
        }

        ResponsiveWindowLayout.ApplyMinimumSizeTo(this, new Size(480, 320));
        RememberResponsiveScreen(screen);
    }

    private void RememberResponsiveScreen(Screen? screen = null)
    {
        Screen current = screen ?? Screen.FromControl(this);
        _responsiveScreenDeviceName = current.DeviceName;
        _responsiveScreenWorkingArea = current.WorkingArea;
    }

    private void RefreshResponsiveWindowBounds()
    {
        if (!_responsiveLayoutReady ||
            _isClosing ||
            IsDisposed ||
            _fullScreenRestoreState is not null)
        {
            return;
        }

        ResponsiveWindowLayout.ApplyTo(
            this,
            logicalPreferredSize: new Size(1180, 760),
            logicalMinimumSize: new Size(480, 320),
            applyPreferredBounds: false);
        RememberResponsiveScreen();
    }

    internal void ToggleFullScreen()
    {
        if (_isClosing || IsDisposed)
        {
            return;
        }

        if (_fullScreenRestoreState is null)
        {
            EnterFullScreen();
        }
        else
        {
            ExitFullScreen();
        }
    }

    internal void ToggleDisplayScaleMode()
    {
        if (_isClosing || IsDisposed)
        {
            return;
        }

        bool allowUpscaling =
            Volatile.Read(
                ref _allowDisplayUpscaling) == 0;
        Volatile.Write(
            ref _allowDisplayUpscaling,
            allowUpscaling ? 1 : 0);
        _displayScaleButton.Text = allowUpscaling
            ? "禁止放大"
            : "允许放大";
        UpdatePictureBoxDisplayMode(
            GetRemoteImageSize());
        ConfigureFilePullToolTips();

        D3D11HwndVideoPresenterResult? result = null;
        lock (_d3d11PresenterLock)
        {
            if (_d3d11VideoPresenter is not null)
            {
                result =
                    _d3d11VideoPresenter.SetScaleMode(
                        GetD3D11VideoScaleMode());
            }
        }

        if (result is { } scaleResult &&
            scaleResult.Status is not
                D3D11HwndVideoPresenterStatus.Resized and not
                D3D11HwndVideoPresenterStatus.Disposed)
        {
            DiagnosticLog.Append(
                "VIEWER",
                "切换查看缩放模式失败：" +
                    scaleResult.Detail);
        }

        _pictureBox.Invalidate();
        RequestH264KeyFrameIfDue();
    }

    private D3D11HwndVideoScaleMode
        GetD3D11VideoScaleMode() =>
        Volatile.Read(
            ref _allowDisplayUpscaling) != 0
            ? D3D11HwndVideoScaleMode.Fit
            : D3D11HwndVideoScaleMode
                .FitWithoutUpscaling;

    private void UpdatePictureBoxDisplayMode(
        Size remoteImageSize)
    {
        if (_pictureBox.IsDisposed)
        {
            return;
        }

        bool requiresDownscaling =
            remoteImageSize.Width >
                _pictureBox.ClientSize.Width ||
            remoteImageSize.Height >
                _pictureBox.ClientSize.Height;
        PictureBoxSizeMode sizeMode =
            Volatile.Read(
                ref _allowDisplayUpscaling) != 0 ||
            requiresDownscaling
                ? PictureBoxSizeMode.Zoom
                : PictureBoxSizeMode.CenterImage;
        if (_pictureBox.SizeMode != sizeMode)
        {
            _pictureBox.SizeMode = sizeMode;
        }
    }

    private void EnterFullScreen()
    {
        Screen targetScreen = Screen.FromControl(this);
        _fullScreenRestoreState =
            new RemoteViewerFullScreenRestoreState(
                FormBorderStyle,
                WindowState,
                SelectWindowedRestoreBounds(
                    WindowState,
                    Bounds,
                    RestoreBounds),
                _statusFooterPanel.Visible,
                _remoteFilePullProgressBar.Visible);

        SuspendLayout();
        try
        {
            _statusFooterPanel.Visible = false;
            _remoteFilePullProgressBar.Visible = false;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = targetScreen.Bounds;
            _fullScreenButton.Text = "退出全屏";
        }
        finally
        {
            ResumeLayout(performLayout: true);
        }

        RememberResponsiveScreen(targetScreen);
        CachePictureBoxClientSize();
        _pictureBox.Focus();
    }

    private void ExitFullScreen()
    {
        if (_fullScreenRestoreState is not
                RemoteViewerFullScreenRestoreState restore)
        {
            return;
        }

        SuspendLayout();
        try
        {
            WindowState = FormWindowState.Normal;
            FormBorderStyle = restore.BorderStyle;
            Bounds = restore.WindowedBounds;
            _statusFooterPanel.Visible =
                restore.StatusFooterVisible;
            _remoteFilePullProgressBar.Visible =
                restore.ProgressBarVisible;
            _fullScreenButton.Text = "全屏";
            WindowState = restore.WindowState;
        }
        finally
        {
            _fullScreenRestoreState = null;
            ResumeLayout(performLayout: true);
        }

        RememberResponsiveScreen();
        CachePictureBoxClientSize();
        _pictureBox.Focus();
    }

    internal static Rectangle SelectWindowedRestoreBounds(
        FormWindowState windowState,
        Rectangle currentBounds,
        Rectangle restoreBounds)
    {
        if (windowState != FormWindowState.Normal &&
            restoreBounds.Width > 0 &&
            restoreBounds.Height > 0)
        {
            return restoreBounds;
        }

        return currentBounds;
    }

    private void ApplyDpiMetrics(int dpi)
    {
        int actionHeight = ResponsiveWindowLayout.ScaleLogical(26, dpi);
        int actionWidth = ResponsiveWindowLayout.ScaleLogical(64, dpi);
        int actionGap = ResponsiveWindowLayout.ScaleLogical(2, dpi);
        int actionVerticalPadding = ResponsiveWindowLayout.ScaleLogical(2, dpi);
        Button[] actionButtons =
            _fileTransferActionsPanel.Controls
                .OfType<Button>()
                .ToArray();
        foreach (Button button in actionButtons)
        {
            button.MinimumSize = new Size(actionWidth, actionHeight);
            button.Padding = new Padding(
                ResponsiveWindowLayout.ScaleLogical(4, dpi),
                0,
                ResponsiveWindowLayout.ScaleLogical(4, dpi),
                0);
            button.Margin = new Padding(0, 0, actionGap, 0);
        }

        int uniformActionHeight = actionButtons
            .Select(button =>
                button.GetPreferredSize(
                    Size.Empty).Height)
            .DefaultIfEmpty(actionHeight)
            .Max();
        uniformActionHeight = Math.Max(
            actionHeight,
            uniformActionHeight);
        foreach (Button button in actionButtons)
        {
            button.MinimumSize = new Size(
                actionWidth,
                uniformActionHeight);
        }

        _fileTransferActionsPanel.Padding = new Padding(0, actionVerticalPadding, 0, actionVerticalPadding);
        _remoteFilePullProgressBar.Height = ResponsiveWindowLayout.ScaleLogical(3, dpi);
        int horizontalPadding = ResponsiveWindowLayout.ScaleLogical(10, dpi);
        _statusBar.Padding = new Padding(horizontalPadding, 0, horizontalPadding, 0);
        UpdateStatusFooterLayout(dpi);
        _statusBar.Invalidate();
    }

    private void UpdateStatusFooterLayout(int? requestedDpi = null)
    {
        if (_updatingStatusFooterLayout || _statusFooterPanel.IsDisposed)
        {
            return;
        }

        int dpi = requestedDpi ?? _statusBar.DeviceDpi;
        int actionWidth = Math.Max(
            _fileTransferActionsPanel.Width,
            _fileTransferActionsPanel.PreferredSize.Width);
        int statusWidth = Math.Max(0, _statusFooterPanel.ClientSize.Width - actionWidth);
        int preferredHeight = CalculateStatusFooterPreferredHeight(
            statusWidth,
            _statusBar.Padding,
            _statusBar.HasDetails,
            _fileTransferActionsPanel.PreferredSize.Height,
            dpi);
        if (preferredHeight <= 0 || _statusFooterPanel.Height == preferredHeight)
        {
            return;
        }

        _updatingStatusFooterLayout = true;
        try
        {
            _statusFooterPanel.Height = preferredHeight;
        }
        finally
        {
            _updatingStatusFooterLayout = false;
        }
    }

    public void CloseAfterDisconnect()
    {
        _closeFromDisconnect = true;
        if (!IsDisposed)
        {
            Close();
        }
    }

    public bool ClosedFromDisconnect => _closeFromDisconnect;

    public void PrepareForReconnect()
    {
        if (_isClosing || IsDisposed)
        {
            return;
        }

        ReleaseAllRemoteInputs();
        CancelPendingClipboardPull();
        CancelRemoteDragOut(status: null, cancelTransfer: true);
        SetRemoteFilePullPending(false);
        SetInputEnabled(false);
        ClearPendingVideoFramesForShutdown();
        ResetH264Decoder();
        SetStatus("连接中断，正在等待自动重连…", MutedTextColor);
    }

    internal Button RemoteInputMethodButtonForEntityTests =>
        _remoteInputMethodButton;

    internal Button CaptureTargetSwitchButtonForEntityTests =>
        _switchCaptureTargetButton;

    internal Button DisplayScaleButtonForEntityTests =>
        _displayScaleButton;

    internal bool AllowDisplayUpscalingForEntityTests =>
        Volatile.Read(
            ref _allowDisplayUpscaling) != 0;

    internal bool SystemKeyboardCaptureActiveForEntityTests =>
        _lowLevelKeyboardHook != 0;

    internal void
        EnableInjectedKeyboardCaptureForEntityTests()
    {
        _allowInjectedKeyboardCaptureForEntityTests =
            true;
    }

    public void SetInputEnabled(bool enabled)
    {
        if (_inputEnabled && !enabled)
        {
            ReleaseAllRemoteInputs();
        }

        _inputEnabled = enabled;
        if (!enabled)
        {
            CancelRemoteDragOut(status: null, cancelTransfer: true);
        }

        if (!_pictureBox.IsDisposed)
        {
            _pictureBox.Cursor = enabled ? Cursors.Default : Cursors.No;
            _pictureBox.LocalImeEnabled = _isAndroidRemote && enabled;
        }

        UpdateRemoteInputMethodControls();
        ConfigureFilePullToolTips();
    }

    internal static bool ShouldShowRemoteInputMethodButton(
        string? platform,
        bool inputEnabled) =>
        inputEnabled &&
        string.Equals(
            platform,
            RemoteDevicePlatforms.Windows,
            StringComparison.OrdinalIgnoreCase);

    internal static RemoteInputCommand[]
        CreateRemoteInputMethodSwitchCommands()
    {
        const RemoteKeyboardFlags windowsKeyFlags =
            RemoteKeyboardFlags.HasScanCode |
            RemoteKeyboardFlags.Extended;
        const RemoteKeyboardFlags spaceKeyFlags =
            RemoteKeyboardFlags.HasScanCode;
        return
        [
            RemoteInputCommand.KeyDown(
                (int)Keys.LWin,
                scanCode: 0x5B,
                flags: windowsKeyFlags),
            RemoteInputCommand.KeyDown(
                (int)Keys.Space,
                scanCode: 0x39,
                flags: spaceKeyFlags),
            RemoteInputCommand.KeyUp(
                (int)Keys.Space,
                scanCode: 0x39,
                flags: spaceKeyFlags),
            RemoteInputCommand.KeyUp(
                (int)Keys.LWin,
                scanCode: 0x5B,
                flags: windowsKeyFlags)
        ];
    }

    private void UpdateRemoteInputMethodControls()
    {
        if (_remoteInputMethodButton.IsDisposed)
        {
            return;
        }

        string platform = _isWindowsRemote
            ? RemoteDevicePlatforms.Windows
            : _isAndroidRemote
                ? RemoteDevicePlatforms.Android
                : RemoteDevicePlatforms.Unknown;
        bool visible =
            ShouldShowRemoteInputMethodButton(
                platform,
                _inputEnabled);
        _remoteInputMethodButton.Visible = visible;
        _remoteInputMethodButton.Enabled =
            visible &&
            _client.IsConnected &&
            !_remoteInputMethodSwitchInProgress;
        UpdateStatusFooterLayout();
    }

    private void UpdateCaptureTargetSwitchControls()
    {
        if (_switchCaptureTargetButton.IsDisposed)
        {
            return;
        }

        bool visible =
            _captureTargetSelectionEnabled;
        CaptureTargetInfo? nextTarget =
            FindNextCaptureTarget(
                _captureTargets,
                _selectedCaptureTargetId);
        _switchCaptureTargetButton.Visible = visible;
        _switchCaptureTargetButton.Enabled =
            visible &&
            _client.IsConnected &&
            !_captureTargetSwitchInProgress &&
            nextTarget is not null;
        _switchCaptureTargetButton.Text =
            _captureTargetSwitchInProgress
                ? "切换中..."
                : "切换屏幕";
        _toolTip.SetToolTip(
            _switchCaptureTargetButton,
            !visible
                ? "当前远程设备不支持切换屏幕。"
                : nextTarget is null
                    ? "远端当前没有其他可切换的屏幕。"
                    : $"切换到下一项：{nextTarget.DisplayName}。");
        UpdateStatusFooterLayout();
    }

    private async Task SwitchCaptureTargetAsync()
    {
        if (_captureTargetSwitchInProgress)
        {
            return;
        }

        CaptureTargetInfo? nextTarget =
            FindNextCaptureTarget(
                _captureTargets,
                _selectedCaptureTargetId);
        if (!_captureTargetSelectionEnabled ||
            !_client.IsConnected ||
            nextTarget is null)
        {
            SetStatus(
                "远端当前没有其他可切换的屏幕。",
                MutedTextColor);
            _pictureBox.Focus();
            return;
        }

        _captureTargetSwitchInProgress = true;
        ReleaseAllRemoteInputs();
        UpdateCaptureTargetSwitchControls();
        SetStatus(
            $"正在切换到 {nextTarget.DisplayName}...",
            MutedTextColor);
        try
        {
            await _client.SelectCaptureTargetAsync(
                nextTarget.Id);
        }
        catch (Exception ex)
        {
            SetStatus(
                $"切换屏幕失败：{ex.Message}",
                DangerTextColor);
        }
        finally
        {
            _captureTargetSwitchInProgress = false;
            UpdateCaptureTargetSwitchControls();
            _pictureBox.Focus();
        }
    }

    private async Task SwitchRemoteInputMethodAsync()
    {
        if (_remoteInputMethodSwitchInProgress)
        {
            return;
        }

        _remoteInputMethodSwitchInProgress = true;
        ReleaseAllRemoteInputs();
        UpdateRemoteInputMethodControls();
        try
        {
            if (!_client.IsConnected)
            {
                throw new IOException(
                    "远程连接已断开，未发送 Win+Space。");
            }

            if (!ShouldShowRemoteInputMethodButton(
                    _isWindowsRemote
                        ? RemoteDevicePlatforms.Windows
                        : RemoteDevicePlatforms.Unknown,
                    _inputEnabled))
            {
                throw new InvalidOperationException(
                    "仅已启用输入控制的 Windows 远端支持此操作。");
            }

            await _client.SendInputsAsync(
                CreateRemoteInputMethodSwitchCommands());
            using var timeout =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(3));
            await _client.FlushInputAsync(
                timeout.Token);
            if (!_isClosing && !IsDisposed)
            {
                SetStatus(
                    "已向远端发送 Win+Space；切换的是远端输入法，不会切换本机。",
                    SuccessTextColor);
            }
        }
        catch (Exception ex) when (
            ex is IOException or
                InvalidOperationException or
                ObjectDisposedException or
                OperationCanceledException or
                System.Net.Sockets.SocketException)
        {
            string detail =
                ex is OperationCanceledException
                    ? "发送确认超时，请检查远程连接后重试。"
                    : ex.Message;
            string status =
                $"切换远端输入法失败：{detail}";
            DiagnosticLog.Append(
                "VIEWER",
                status);
            if (!_isClosing && !IsDisposed)
            {
                SetStatus(
                    status,
                    DangerTextColor);
            }
        }
        finally
        {
            _remoteInputMethodSwitchInProgress = false;
            UpdateRemoteInputMethodControls();
            if (!_isClosing &&
                !_pictureBox.IsDisposed &&
                _pictureBox.CanFocus)
            {
                _pictureBox.Focus();
            }
        }
    }

    public void SetClipboardTextEnabled(bool enabled)
    {
        _clipboardTextEnabled = enabled;
    }

    public void SetFilePasteEnabled(bool enabled)
    {
        _filePasteEnabled = enabled;
    }

    public void SetFileDropPasteEnabled(bool enabled)
    {
        _fileDropPasteEnabled =
            enabled &&
            !_fileDropRegistrationUnavailable;
        UpdatePictureBoxFileDropRegistration();
    }

    private void UpdatePictureBoxFileDropRegistration()
    {
        if (_pictureBox.IsDisposed ||
            !_pictureBox.IsHandleCreated)
        {
            return;
        }

        bool shouldEnable =
            _fileDropPasteEnabled &&
            !_fileDropRegistrationUnavailable;
        if (_pictureBox.AllowDrop == shouldEnable)
        {
            return;
        }

        if (TrySetFileDropRegistration(
                _pictureBox,
                shouldEnable,
                setAllowDrop: null,
                out string failure))
        {
            return;
        }

        _fileDropPasteEnabled = false;
        _fileDropRegistrationUnavailable = true;
        string status =
            "本机拖放注册不可用，已关闭向远端拖入文件" +
            (string.IsNullOrWhiteSpace(failure)
                ? "。"
                : $"：{failure}");
        DiagnosticLog.Append("VIEWER", status);
        SetStatus(status, DangerTextColor);
        ConfigureFilePullToolTips();
    }

    internal static bool TrySetFileDropRegistration(
        Control target,
        bool enabled,
        Action<Control, bool>? setAllowDrop,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(target);
        try
        {
            Action<Control, bool> setter =
                setAllowDrop ??
                SetControlAllowDrop;
            setter(target, enabled);
            failure = string.Empty;
            return true;
        }
        catch (Exception ex)
            when (ex is InvalidOperationException or
                ExternalException)
        {
            failure = ex.Message;
            return false;
        }
    }

    private static void SetControlAllowDrop(
        Control control,
        bool enabled)
    {
        control.AllowDrop = enabled;
    }

    public void SetRemoteFilePullEnabled(bool enabled)
    {
        _remoteFilePullEnabled = enabled;
        if (!enabled)
        {
            CancelRemoteDragOut(status: null, cancelTransfer: true);
        }

        ConfigureFilePullToolTips();
        UpdateRemoteFilePullControls();
    }

    private void ConfigureFilePullToolTips()
    {
        string pullTip = _remoteFilePullEnabled
            ? RemoteFilePullUi.BuildAvailableToolTip(checksumEnabled: false)
            : "当前远程设备不支持文件回传。";
        bool canDragOut = _remoteFilePullEnabled && _inputEnabled && !_isAndroidRemote;
        if (canDragOut)
        {
            pullTip += Environment.NewLine + "也可在远端资源管理器中按住文件，直接拖出远程窗口到本机。";
        }

        _toolTip.SetToolTip(_pullRemoteFilesButton, pullTip);
        _toolTip.SetToolTip(_openReceivedFilesButton, "打开本机 RemoteDesk 文件接收目录。");
        _toolTip.SetToolTip(
            _remoteInputMethodButton,
            "切换被控 Windows 的输入法（相当于在远端按 " +
                "Win+Space），不会切换本机输入法。");
        bool allowDisplayUpscaling =
            Volatile.Read(
                ref _allowDisplayUpscaling) != 0;
        _toolTip.SetToolTip(
            _displayScaleButton,
            allowDisplayUpscaling
                ? "当前允许放大以适应窗口。点击后，较小画面按 1:1 原始像素居中显示，" +
                    "避免被放大后发虚；大画面仍会等比缩小。"
                : "当前禁止放大：较小画面按原始像素居中，大画面等比缩小。" +
                    "点击后允许放大以适应窗口；不会改变远端分辨率或编码画质。");
        _toolTip.SetToolTip(
            _fullScreenButton,
            "切换无边框全屏（F11）。4K 在默认窗口会被大幅缩小，" +
                "细字与细线会变软；全屏可尽量按原始像素显示。");
        _toolTip.SetToolTip(
            _pictureBox,
            _fileDropRegistrationUnavailable
                ? "操作远端桌面；本机系统拒绝拖放注册，向远端拖入文件已关闭。可切换是否放大画面，按 F11 可切换全屏。"
                : canDragOut
                ? "操作远端桌面；在远端资源管理器中把文件拖出此窗口，可接续拖放到本机。可切换是否放大画面，按 F11 可切换全屏。"
                : "操作远端桌面；默认不放大小画面，按 F11 可切换全屏。");
        UpdateStatusToolTip(_statusBar.StatusText);
    }

    private void UpdateStatusToolTip(string status)
    {
        string copyableStatus =
            BuildCopyableStatus(
                status,
                _statusBar.DetailsText);
        string dragOutTip = _remoteFilePullEnabled && _inputEnabled && !_isAndroidRemote
            ? "；也可把远端文件拖出窗口"
            : string.Empty;
        _toolTip.SetToolTip(
            _statusBar,
            $"{copyableStatus}{Environment.NewLine}{Environment.NewLine}" +
            $"显示：可切换是否放大小画面，大画面始终等比缩小；全屏：F11" +
                "（4K 缩小显示会损失细节）；取回远端文件：" +
                $"{RemoteFilePullUi.ShortcutText}{dragOutTip}；右键可复制状态或打开接收目录。");
    }

    internal static string BuildCopyableStatus(
        string? status,
        string? details)
    {
        string normalizedStatus =
            status?.Trim() ??
            string.Empty;
        string normalizedDetails =
            details?.Trim() ??
            string.Empty;
        if (normalizedStatus.Length == 0)
        {
            return normalizedDetails;
        }

        if (normalizedDetails.Length == 0)
        {
            return normalizedStatus;
        }

        return normalizedStatus +
            Environment.NewLine +
            normalizedDetails;
    }

    private void SetRemoteFilePullPending(bool pending)
    {
        _remoteFilePullInProgress = pending;
        if (pending && _remoteFilePullStatusStage == RemoteFilePullStatusStage.Other)
        {
            _remoteFilePullStatusStage = RemoteFilePullStatusStage.Waiting;
        }
        else if (!pending)
        {
            _remoteFilePullStatusStage = RemoteFilePullStatusStage.Other;
        }

        UpdateRemoteFilePullControls();
    }

    private void UpdateRemoteFilePullControls()
    {
        _pullRemoteFilesButton.Enabled = _client.IsConnected &&
            _remoteFilePullEnabled &&
            !_remoteFilePullInProgress;
        _pullRemoteFilesButton.Text = _remoteFilePullInProgress
            ? RemoteFilePullUi.GetButtonText(_remoteFilePullStatusStage)
            : "取回文件";
        _remoteFilePullProgressBar.MarqueeAnimationSpeed = _remoteFilePullInProgress ? 24 : 0;
        _remoteFilePullProgressBar.Visible = _remoteFilePullInProgress;
        UpdateStatusFooterLayout();
    }

    private async Task PullRemoteClipboardFilesAsync()
    {
        if (_remoteFilePullInProgress || _client.IsRemoteClipboardFileRequestPending)
        {
            SetStatus("远端文件正在取回，请等待当前操作完成。", MutedTextColor);
            _pictureBox.Focus();
            return;
        }

        if (!_client.IsConnected || !_remoteFilePullEnabled)
        {
            SetStatus("当前远程设备不可取回文件。", DangerTextColor);
            _pictureBox.Focus();
            return;
        }

        _remoteFilePullStatusStage = RemoteFilePullStatusStage.Waiting;
        SetRemoteFilePullPending(true);
        SetStatus("正在请求远端剪贴板中的文件…", MutedTextColor);
        try
        {
            bool requested = await _client.RequestRemoteClipboardFilesAsync();
            if (!requested && !_client.IsRemoteClipboardFileRequestPending)
            {
                SetRemoteFilePullPending(false);
            }
        }
        catch (Exception ex) when (ex is IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            ObjectDisposedException)
        {
            if (!_isClosing && !IsDisposed)
            {
                SetRemoteFilePullPending(false);
                SetStatus($"取回远端文件失败：{ex.Message}", DangerTextColor);
            }
        }
        finally
        {
            if (!_isClosing &&
                !IsDisposed &&
                !_client.IsRemoteClipboardFileRequestPending)
            {
                SetRemoteFilePullPending(false);
            }

            if (!_isClosing && !IsDisposed && !_pictureBox.IsDisposed)
            {
                _pictureBox.Focus();
            }
        }
    }

    private async Task CopyStatusAsync()
    {
        try
        {
            await ClipboardTextService.SetTextAsync(
                BuildCopyableStatus(
                    _statusBar.StatusText,
                    _statusBar.DetailsText));
            if (!_isClosing && !IsDisposed)
            {
                SetStatus("当前状态已复制到本机剪贴板。", SuccessTextColor);
            }
        }
        catch (Exception ex) when (ex is TimeoutException or ExternalException or InvalidOperationException)
        {
            if (!_isClosing && !IsDisposed)
            {
                SetStatus($"复制状态失败：{ex.Message}", DangerTextColor);
            }
        }
    }

    private void OpenReceivedFilesDirectory()
    {
        try
        {
            string receiveDirectory = FileTransferReceiver.GetReceiveDirectory();
            Directory.CreateDirectory(receiveDirectory);
            Process.Start(RemoteFilePullUi.CreateOpenReceiveDirectoryStartInfo(receiveDirectory));
            SetStatus($"已打开本机接收目录：{receiveDirectory}", SuccessTextColor);
        }
        catch (Exception ex) when (ex is IOException or
            UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or
            InvalidOperationException or
            ArgumentException)
        {
            SetStatus($"打开本机接收目录失败：{ex.Message}", DangerTextColor);
        }
    }

    public void SetRemotePlatform(string? platform)
    {
        string normalizedPlatform =
            RemoteDevicePlatforms.Normalize(platform);
        _isAndroidRemote = string.Equals(
            normalizedPlatform,
            RemoteDevicePlatforms.Android,
            StringComparison.OrdinalIgnoreCase);
        _isWindowsRemote = string.Equals(
            normalizedPlatform,
            RemoteDevicePlatforms.Windows,
            StringComparison.OrdinalIgnoreCase);
        _pictureBox.LocalImeEnabled = _isAndroidRemote && _inputEnabled;
        if (_isAndroidRemote)
        {
            CancelRemoteDragOut(status: null, cancelTransfer: true);
        }

        UpdateRemoteInputMethodControls();
        ConfigureFilePullToolTips();
    }

    public void SetCaptureTargets(
        IReadOnlyList<CaptureTargetInfo> targets,
        string? selectedTargetId)
    {
        ArgumentNullException.ThrowIfNull(targets);

        _captureTargets.Clear();
        var knownTargetIds =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
        foreach (CaptureTargetInfo target in targets)
        {
            if (target is null ||
                string.IsNullOrWhiteSpace(target.Id) ||
                !knownTargetIds.Add(target.Id))
            {
                continue;
            }

            _captureTargets.Add(target);
        }

        _selectedCaptureTargetId =
            string.IsNullOrWhiteSpace(selectedTargetId)
                ? null
                : selectedTargetId;
        UpdateCaptureTargetSwitchControls();
    }

    public void SetCaptureTargetSelectionEnabled(bool enabled)
    {
        _captureTargetSelectionEnabled = enabled;
        UpdateCaptureTargetSwitchControls();
    }

    protected override void OnFormClosing(FormClosingEventArgs args)
    {
        ReleaseAllRemoteInputs();
        _isClosing = true;
        ClearPendingVideoFramesForShutdown();
        CancelRemoteDragOut(status: null, cancelTransfer: true);
        base.OnFormClosing(args);
        if (args.Cancel)
        {
            _isClosing = false;
            RequestH264KeyFrameIfDue();
            TryInstallSystemKeyboardCapture();
            return;
        }

        UninstallSystemKeyboardCapture();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReleaseAllRemoteInputs();
            UninstallSystemKeyboardCapture();
            _isClosing = true;
            ClearPendingVideoFramesForShutdown();
            CancelRemoteDragOut(status: null, cancelTransfer: true);
            CancelPendingClipboardPull();
            _client.FrameReceived -= OnFrameReceived;
            _client.Log -= OnClientLog;
            _client.RoundTripUpdated -= OnRoundTripUpdated;
            _client.CaptureTargetSelectionChanged -=
                OnCaptureTargetSelectionChanged;
            _client.CaptureTargetAvailabilityChanged -=
                OnCaptureTargetAvailabilityChanged;
            _client.ClipboardStatusReceived -= OnClipboardStatusReceived;
            _client.FileTransferStatusReceived -= OnFileTransferStatusReceived;
            _client.RemoteClipboardFileRequestPendingChanged -= OnRemoteClipboardFileRequestPendingChanged;
            _pictureBox.ClientSizeChanged -= PictureBox_ClientSizeChanged;
            _pictureBox.HandleCreated -=
                PictureBox_HandleCreated;
            _pictureBox.HandleDestroyed -=
                PictureBox_HandleDestroyed;
            _pictureBox.Image = null;
            _decodedFrameMailbox.Close()?.Dispose();
            _currentImage?.Dispose();
            _pictureBox.DirectPresentationActive = false;
            DisposeD3D11VideoPresenter();
            ResetH264Decoder();
            _statusMenu.Dispose();
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnClientLog(string message)
    {
        OnUi(() => SetStatus(message, MutedTextColor));
    }

    private void OnClipboardStatusReceived(string message)
    {
        if (CaptureTargetAvailabilityStatusCodec.TryParse(
                message,
                out _))
        {
            return;
        }

        Color color = message.Contains("失败", StringComparison.Ordinal) ||
            message.Contains("无法", StringComparison.Ordinal)
            ? DangerTextColor
            : MutedTextColor;
        OnUi(() => SetStatus(message, color));
    }

    private void OnCaptureTargetAvailabilityChanged(
        CaptureTargetAvailabilityUpdate update)
    {
        if (!_client.IsCurrentCaptureTargetAvailabilityUpdate(
                update))
        {
            return;
        }

        long presentationGeneration = 0;
        bool presentationChanged = false;
        _capturePresentationTransitionGate.Run(() =>
        {
            if (!ShouldAdvanceCapturePresentationForStatus(
                    _captureTargetStatusConnectionGeneration,
                    _captureTargetHostGeneration,
                    _captureTargetId,
                    Volatile.Read(
                        ref _captureTargetAvailable) != 0,
                    update))
            {
                return;
            }

            _captureTargetStatusConnectionGeneration =
                update.ConnectionGeneration;
            _captureTargetHostGeneration =
                update.TargetGeneration;
            _captureTargetId = update.Target.Id;
            Volatile.Write(
                ref _captureTargetAvailable,
                update.IsAvailable ? 1 : 0);
            presentationGeneration =
                Interlocked.Increment(
                    ref _capturePresentationGeneration);
            Volatile.Write(
                ref _capturePresentationTransitionPending,
                1);
            presentationChanged = true;
        });
        if (!presentationChanged)
        {
            return;
        }

        try
        {
            OnUiSynchronous(() =>
            {
                if (presentationGeneration !=
                        Interlocked.Read(
                            ref _capturePresentationGeneration) ||
                    !_client
                        .IsCurrentCaptureTargetAvailabilityUpdate(
                            update))
                {
                    return;
                }

                _selectedCaptureTargetId =
                    update.Target.Id;
                UpdateCaptureTargetSwitchControls();
                ReleaseAllRemoteInputs();
                CancelRemoteDragOut(
                    status: null,
                    cancelTransfer: true);
                if (!update.IsAvailable)
                {
                    ClearUnavailableCaptureTargetPresentation();
                }
                else if (!_pictureBox.IsDisposed)
                {
                    ClearCaptureTargetPresentation();
                    _pictureBox.Cursor = _inputEnabled
                        ? Cursors.Default
                        : Cursors.No;
                    SetStatus(
                        update.DisplayMessage,
                        SuccessTextColor);
                    RequestH264KeyFrameIfDue();
                }
            });
        }
        finally
        {
            CompleteCapturePresentationTransition(
                presentationGeneration);
        }
    }

    private void OnCaptureTargetSelectionChanged(
        CaptureTargetChangedUpdate update)
    {
        if (!_client.IsCurrentCaptureTargetChangedUpdate(
                update))
        {
            return;
        }

        bool targetChanged = false;
        long presentationGeneration = 0;
        _capturePresentationTransitionGate.Run(() =>
        {
            bool connectionChanged =
                _captureTargetStatusConnectionGeneration !=
                    update.ConnectionGeneration;
            if (!connectionChanged &&
                !ShouldAdvanceCapturePresentationForTarget(
                    _captureTargetId,
                    update.Target.Id))
            {
                return;
            }

            _captureTargetStatusConnectionGeneration =
                update.ConnectionGeneration;
            if (connectionChanged)
            {
                _captureTargetHostGeneration = int.MinValue;
            }
            _captureTargetId = update.Target.Id;
            Volatile.Write(
                ref _captureTargetAvailable,
                1);
            presentationGeneration =
                Interlocked.Increment(
                    ref _capturePresentationGeneration);
            Volatile.Write(
                ref _capturePresentationTransitionPending,
                1);
            targetChanged = true;
        });
        if (!targetChanged)
        {
            return;
        }

        try
        {
            OnUiSynchronous(() =>
            {
                if (presentationGeneration !=
                        Interlocked.Read(
                            ref _capturePresentationGeneration) ||
                    !_client.IsCurrentCaptureTargetChangedUpdate(
                        update))
                {
                    return;
                }

                _selectedCaptureTargetId =
                    update.Target.Id;
                UpdateCaptureTargetSwitchControls();
                ReleaseAllRemoteInputs();
                CancelRemoteDragOut(
                    status: null,
                    cancelTransfer: true);
                ClearCaptureTargetPresentation();
                if (!_pictureBox.IsDisposed)
                {
                    _pictureBox.Cursor = _inputEnabled
                        ? Cursors.Default
                        : Cursors.No;
                }

                SetStatus(
                    $"已切换到 {update.Target.DisplayName}，等待新画面。",
                    MutedTextColor);
                RequestH264KeyFrameIfDue();
            });
        }
        finally
        {
            CompleteCapturePresentationTransition(
                presentationGeneration);
        }
    }

    internal static bool ShouldAdvanceCapturePresentationForTarget(
        string? currentTargetId,
        string nextTargetId) =>
        !string.Equals(
            currentTargetId,
            nextTargetId,
            StringComparison.OrdinalIgnoreCase);

    internal static CaptureTargetInfo? FindNextCaptureTarget(
        IReadOnlyList<CaptureTargetInfo> targets,
        string? currentTargetId)
    {
        ArgumentNullException.ThrowIfNull(targets);
        CaptureTargetInfo[] screens =
            targets
                .Where(target =>
                    !string.Equals(
                        target.Id,
                        ScreenCaptureTarget.AllScreensId,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
        if (screens.Length <= 1)
        {
            return null;
        }

        for (int index = 0; index < screens.Length; index++)
        {
            if (string.Equals(
                    screens[index].Id,
                    currentTargetId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return screens[(index + 1) % screens.Length];
            }
        }

        return screens[0];
    }

    internal static bool ShouldAdvanceCapturePresentationForStatus(
        long currentConnectionGeneration,
        int currentTargetGeneration,
        string? currentTargetId,
        bool currentAvailability,
        CaptureTargetAvailabilityUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return currentConnectionGeneration !=
                update.ConnectionGeneration ||
            currentTargetGeneration !=
                update.TargetGeneration ||
            currentAvailability != update.IsAvailable ||
            ShouldAdvanceCapturePresentationForTarget(
                currentTargetId,
                update.Target.Id);
    }

    private void ClearUnavailableCaptureTargetPresentation()
    {
        ClearCaptureTargetPresentation();
        _pictureBox.Cursor = Cursors.No;

        CaptureTargetAvailabilityUpdate? current =
            _client.LatestCaptureTargetAvailability;
        SetStatus(
            current is not null &&
                !current.IsAvailable
                    ? current.DisplayMessage
                    : "捕获目标暂不可用；画面和指针输入已暂停，正在等待恢复。",
            DangerTextColor);
        _pictureBox.Invalidate();
    }

    private void ClearCaptureTargetPresentation()
    {
        _capturePresentationTransitionGate.Run(() =>
        {
            ClearPendingVideoFramesForShutdown();
            _decodedFrameMailbox.TakeLatest()?.Dispose();
            ResetH264Decoder();
            DeactivateD3D11Presentation(
                disposePresenter: true);
            _pictureBox.Image = null;
            Image? oldImage = _currentImage;
            _currentImage = null;
            oldImage?.Dispose();
            Interlocked.Exchange(
                ref _remoteImageSizePacked,
                0);
            Interlocked.Exchange(
                ref _directFrameUiSignature,
                long.MinValue);
            _lastRenderedEncoding = null;
            _lastRenderedDecoderBackend = string.Empty;
            ResetFrameStatsAfterCaptureTransition();
            SetPerformanceStatus(string.Empty);
            _pictureBox.Invalidate();
        });
    }

    private void ResetFrameStatsAfterCaptureTransition()
    {
        lock (_frameStatsLock)
        {
            ResetFrameStatsLocked();
            _frameStatsReportsSinceDiagnostic = 0;
        }
    }

    private void ResetFrameStatsLocked()
    {
        _framesInWindow = 0;
        _bytesInWindow = 0;
        _captureMillisecondsInWindow = 0;
        _encodeMillisecondsInWindow = 0;
        _decodeMillisecondsInWindow = 0;
        _receiveToDecodeMillisecondsInWindow = 0;
        _decodeToPresentMillisecondsInWindow = 0;
        _receiveToPresentMillisecondsInWindow = 0;
        _captureTimingSamplesInWindow = 0;
        _encodeTimingSamplesInWindow = 0;
        _viewerPipelineTimingSamplesInWindow = 0;
        _frameWindowStartedAt = Stopwatch.GetTimestamp();
    }

    private void OnFileTransferStatusReceived(bool success, string message)
    {
        if (ShouldSuppressAutomaticEmptyRemoteFileStatus(
            success,
            message,
            Environment.TickCount64,
            Interlocked.Read(ref _lastAutomaticRemoteFilePullAt)))
        {
            return;
        }

        OnUi(() =>
        {
            bool pending = _client.IsRemoteClipboardFileRequestPending;
            bool showPending = ShouldShowRemoteFilePullPending(pending, _remoteDragOutStage);
            RemoteFilePullStatusStage stage = RemoteFilePullUi.ClassifyStatus(
                success,
                message,
                showPending || _remoteFilePullInProgress);
            if (stage is RemoteFilePullStatusStage.Waiting or RemoteFilePullStatusStage.Receiving)
            {
                _remoteFilePullStatusStage = stage;
            }

            SetRemoteFilePullPending(showPending);
            if (_remoteDragOutStage == RemoteDragOutStage.LocalDragging)
            {
                return;
            }

            string displayMessage = RemoteFilePullUi.FormatCompletedStatus(
                    success,
                    message,
                    FileTransferReceiver.GetReceiveDirectory())
                .Replace(Environment.NewLine, "；", StringComparison.Ordinal);
            SetStatus(
                displayMessage,
                success || IsCancelledFileTransferStatus(message)
                    ? MutedTextColor
                    : DangerTextColor);
            UpdateStatusToolTip(displayMessage);
        });
    }

    internal static bool IsCancelledFileTransferStatus(string message)
    {
        return !string.IsNullOrWhiteSpace(message) &&
            (message.Contains("已取消", StringComparison.Ordinal) ||
                message.Contains("操作取消", StringComparison.Ordinal));
    }

    private void OnRemoteClipboardFileRequestPendingChanged(bool _)
    {
        OnUi(() =>
        {
            // Event callbacks can be queued behind OLE messages. Read the current client state
            // instead of letting a stale true/false notification hide or resurrect the spinner.
            bool currentPending = _client.IsRemoteClipboardFileRequestPending;
            SetRemoteFilePullPending(
                ShouldShowRemoteFilePullPending(currentPending, _remoteDragOutStage));
        });
    }

    private void OnRoundTripUpdated(TimeSpan roundTrip)
    {
        Interlocked.Exchange(ref _roundTripMilliseconds, Math.Max(0, (long)Math.Round(roundTrip.TotalMilliseconds)));
    }

    private void OnFrameReceived(RemoteFrame frame)
    {
        if (_isClosing ||
            IsDisposed ||
            Disposing ||
            Volatile.Read(
                ref _captureTargetAvailable) == 0)
        {
            return;
        }

        long presentationGeneration =
            Interlocked.Read(
                ref _capturePresentationGeneration);
        PooledRemoteFrame ownedFrame =
            PooledRemoteFrame.CopyFrom(
                frame,
                _encodedFramePool,
                presentationGeneration);
        bool accepted = false;
        bool shouldStartRenderer = false;
        try
        {
            lock (_pendingFrameLock)
            {
                if (_isClosing ||
                    IsDisposed ||
                    Disposing ||
                    !CanPresentCaptureGeneration(
                        presentationGeneration))
                {
                    return;
                }

                if (frame.Encoding == RemoteFrameEncoding.H264AnnexB)
                {
                    accepted =
                        EnqueueH264Frame(ownedFrame);
                }
                else
                {
                    ClearH264FramesLocked();
                    _waitingForH264RecoveryFrame = false;
                    Interlocked.Exchange(ref _resetH264DecoderOnRecovery, 0);
                    _pendingFrame?.Dispose();
                    _pendingFrame = ownedFrame;
                    accepted = true;
                }

                if (_waitingForViewerHandle)
                {
                    return;
                }

                if (!_isRenderingFrame &&
                    accepted)
                {
                    _isRenderingFrame = true;
                    shouldStartRenderer = true;
                }
            }
        }
        finally
        {
            if (!accepted)
            {
                ownedFrame.Dispose();
            }
        }

        if (shouldStartRenderer)
        {
            _ = Task.Run(RenderLatestFramesAsync);
        }
    }

    internal static bool CanPresentCaptureFrame(
        bool targetAvailable,
        long framePresentationGeneration,
        long currentPresentationGeneration) =>
        CanPresentCaptureFrame(
            targetAvailable,
            transitionPending: false,
            framePresentationGeneration,
            currentPresentationGeneration);

    internal static bool CanPresentCaptureFrame(
        bool targetAvailable,
        bool transitionPending,
        long framePresentationGeneration,
        long currentPresentationGeneration) =>
        targetAvailable &&
        !transitionPending &&
        framePresentationGeneration ==
            currentPresentationGeneration;

    private bool CanPresentCaptureGeneration(
        long presentationGeneration) =>
        CanPresentCaptureFrame(
            Volatile.Read(
                ref _captureTargetAvailable) != 0,
            Volatile.Read(
                ref _capturePresentationTransitionPending) != 0,
            presentationGeneration,
            Interlocked.Read(
                ref _capturePresentationGeneration));

    private void CompleteCapturePresentationTransition(
        long presentationGeneration)
    {
        _capturePresentationTransitionGate.Run(() =>
        {
            if (presentationGeneration ==
                Interlocked.Read(
                    ref _capturePresentationGeneration))
            {
                Volatile.Write(
                    ref _capturePresentationTransitionPending,
                    0);
            }
        });
    }

    private void ClearPendingVideoFramesForShutdown()
    {
        lock (_pendingFrameLock)
        {
            _pendingFrame?.Dispose();
            _pendingFrame = null;
            ClearH264FramesLocked();
            _waitingForH264RecoveryFrame = false;
            _waitingForViewerHandle = false;
            Interlocked.Exchange(
                ref _resetH264DecoderOnRecovery,
                0);
        }
    }

    private bool DeferFrameUntilViewerHandle(
        PooledRemoteFrame frame)
    {
        bool scheduleRetry;
        bool accepted = false;
        lock (_pendingFrameLock)
        {
            if (_isClosing ||
                IsDisposed ||
                Disposing)
            {
                _isRenderingFrame = false;
                return false;
            }

            if (_pendingFrame is null &&
                !_h264Frames.Any(
                    queuedFrame =>
                        IsH264RecoveryFrame(
                            queuedFrame.Frame)))
            {
                PooledRemoteFrame[] queued =
                    _h264Frames.ToArray();
                _h264Frames.Clear();
                _h264Frames.Enqueue(frame);
                accepted = true;
                foreach (PooledRemoteFrame queuedFrame in
                    queued)
                {
                    _h264Frames.Enqueue(
                        queuedFrame);
                }
            }

            _waitingForViewerHandle = true;
            _isRenderingFrame = false;
            scheduleRetry =
                IsD3DPresentationTargetReady();
        }

        if (scheduleRetry)
        {
            ScheduleDeferredRendererRetry();
        }

        return accepted;
    }

    private void ResumeDeferredFrameRenderer()
    {
        bool shouldStartRenderer = false;
        lock (_pendingFrameLock)
        {
            _waitingForViewerHandle = false;
            if (!_isClosing &&
                !IsDisposed &&
                !Disposing &&
                !_isRenderingFrame &&
                (_h264Frames.Count > 0 ||
                    _pendingFrame is not null))
            {
                _isRenderingFrame = true;
                shouldStartRenderer = true;
            }
        }

        if (shouldStartRenderer)
        {
            _ = Task.Run(
                RenderLatestFramesAsync);
        }
    }

    private void ScheduleDeferredRendererRetry()
    {
        if (Interlocked.CompareExchange(
                ref _deferredRendererRetryPending,
                1,
                0) != 0)
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(
                            16)
                        .ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Exchange(
                        ref _deferredRendererRetryPending,
                        0);
                }

                if (!_isClosing &&
                    !IsDisposed &&
                    !Disposing)
                {
                    ResumeDeferredFrameRenderer();
                }
            });
    }

    private bool IsD3DPresentationTargetReady()
    {
        if (Volatile.Read(
                ref _pictureBoxHandleAvailable) == 0)
        {
            return false;
        }

        return IsD3DClientAreaReady(
            new Size(
                Volatile.Read(
                    ref _pictureBoxClientWidth),
                Volatile.Read(
                    ref _pictureBoxClientHeight)));
    }

    internal static bool IsD3DClientAreaReady(
        Size clientSize) =>
        clientSize.Width > 0 &&
        clientSize.Height > 0;

    internal RemoteViewerRenderTelemetrySnapshot
        CollectRenderTelemetrySnapshot(
            bool includeLatencyDistributions = false)
    {
        double millisecondsPerTimestampTick =
            1_000d / Stopwatch.Frequency;
        return new(
            DirectHardwarePresentedFrames:
                Interlocked.Read(
                    ref _directHardwarePresentedFrameCount),
            DirectHardwareOccludedFrames:
                Interlocked.Read(
                    ref _directHardwareOccludedFrameCount),
            DirectHardwareSkippedFrames:
                Interlocked.Read(
                    ref _directHardwareSkippedFrameCount),
            DirectHardwarePresentationFailures:
                Interlocked.Read(
                    ref _directHardwarePresentationFailureCount),
            DirectPresentationQualificationAttempts:
                Interlocked.Read(
                    ref _directPresentationQualificationAttemptCount),
            DirectPresentationQualificationSuccesses:
                Interlocked.Read(
                    ref _directPresentationQualificationSuccessCount),
            DirectPresentationQualificationFailures:
                Interlocked.Read(
                    ref _directPresentationQualificationFailureCount),
            JpegFallbackRequested:
                Volatile.Read(
                    ref _jpegFallbackRequested) != 0,
            TotalDirectHardwareDecodeMilliseconds:
                Interlocked.Read(
                    ref _directHardwareDecodeStopwatchTicks) *
                    millisecondsPerTimestampTick,
            TotalDirectHardwareDecodeToPresentMilliseconds:
                Interlocked.Read(
                    ref _directHardwareDecodeToPresentStopwatchTicks) *
                    millisecondsPerTimestampTick,
            TotalDirectHardwareReceiveToPresentMilliseconds:
                Interlocked.Read(
                    ref _directHardwareReceiveToPresentStopwatchTicks) *
                    millisecondsPerTimestampTick,
            MaximumDirectHardwareReceiveToPresentMilliseconds:
                Interlocked.Read(
                    ref _directHardwareMaximumReceiveToPresentStopwatchTicks) *
                    millisecondsPerTimestampTick,
            DirectHardwareDecodeLatency:
                includeLatencyDistributions
                    ? _directHardwareDecodeLatency.Snapshot()
                    : default,
            DirectHardwareDecodeToPresentLatency:
                includeLatencyDistributions
                    ? _directHardwareDecodeToPresentLatency.Snapshot()
                    : default,
            DirectHardwareReceiveToPresentLatency:
                includeLatencyDistributions
                    ? _directHardwareReceiveToPresentLatency.Snapshot()
                    : default,
            DirectHardwarePathDisabled:
                Volatile.Read(
                    ref _mediaFoundationDirectPathDisabled) != 0,
            LastDirectHardwareFailureDetail:
                Volatile.Read(
                    ref _lastDirectHardwareFailureDetail),
            LastRenderedDecoderBackend:
                Volatile.Read(
                    ref _lastRenderedDecoderBackend));
    }

    private void RecordDirectHardwareFailure(
        string? detail)
    {
        if (!string.IsNullOrWhiteSpace(detail))
        {
            Volatile.Write(
                ref _lastDirectHardwareFailureDetail,
                detail.Trim());
        }
    }

    private void RecordDirectHardwarePresentation(
        RemoteFrameMetadata frame,
        long decodeStartedAt,
        long decodedAt,
        long presentedAt,
        D3D11HwndVideoPresenterStatus status)
    {
        if (status == D3D11HwndVideoPresenterStatus.Occluded)
        {
            Interlocked.Increment(
                ref _directHardwareOccludedFrameCount);
            return;
        }

        if (status != D3D11HwndVideoPresenterStatus.Presented)
        {
            Interlocked.Increment(
                ref _directHardwareSkippedFrameCount);
            return;
        }

        Interlocked.Increment(
            ref _directHardwarePresentedFrameCount);
        if (frame.ReceivedAtTimestamp <= 0 ||
            decodeStartedAt < frame.ReceivedAtTimestamp ||
            decodedAt < decodeStartedAt ||
            presentedAt < decodedAt)
        {
            return;
        }

        long decodeTicks =
            decodedAt - decodeStartedAt;
        long decodeToPresentTicks =
            presentedAt - decodedAt;
        long receiveToPresentTicks =
            presentedAt - frame.ReceivedAtTimestamp;
        Interlocked.Add(
            ref _directHardwareDecodeStopwatchTicks,
            decodeTicks);
        Interlocked.Add(
            ref _directHardwareDecodeToPresentStopwatchTicks,
            decodeToPresentTicks);
        Interlocked.Add(
            ref _directHardwareReceiveToPresentStopwatchTicks,
            receiveToPresentTicks);
        _directHardwareDecodeLatency.RecordStopwatchTicks(
            decodeTicks);
        _directHardwareDecodeToPresentLatency.RecordStopwatchTicks(
            decodeToPresentTicks);
        _directHardwareReceiveToPresentLatency.RecordStopwatchTicks(
            receiveToPresentTicks);
        UpdateMaximum(
            ref _directHardwareMaximumReceiveToPresentStopwatchTicks,
            receiveToPresentTicks);
    }

    private static void UpdateMaximum(
        ref long target,
        long candidate)
    {
        long current = Volatile.Read(ref target);
        while (candidate > current)
        {
            long observed = Interlocked.CompareExchange(
                ref target,
                candidate,
                current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private async Task RenderLatestFramesAsync()
    {
        while (true)
        {
            PooledRemoteFrame ownedFrame;
            lock (_pendingFrameLock)
            {
                if (!TryDequeueFrame(out ownedFrame))
                {
                    _isRenderingFrame = false;
                    return;
                }
            }

            RemoteFrame frame = ownedFrame.Frame;
            RemoteFrameMetadata frameMetadata =
                ownedFrame.Metadata;
            long presentationGeneration =
                ownedFrame.PresentationGeneration;
            bool frameOwnershipTransferred = false;
            try
            {
                if (!CanPresentCaptureGeneration(
                        presentationGeneration))
                {
                    continue;
                }

                long decodeStartedAt = Stopwatch.GetTimestamp();
                Bitmap? bitmap = null;
                string? decoderBackendName = null;
                bool decoderUsesHardwareAcceleration = false;
                if (frame.Encoding == RemoteFrameEncoding.Jpeg)
                {
                    ResetH264Decoder();
                    _h264DecodeMisses = 0;
                    bitmap = DetachedBitmapLoader.Load(
                        frame.EncodedBuffer,
                        frame.EncodedOffset,
                        frame.EncodedLength,
                        frame.Width,
                        frame.Height);
                }
                else if (frame.Encoding == RemoteFrameEncoding.H264AnnexB)
                {
                    bool forceDecoderReset =
                        IsH264RecoveryFrame(frame) &&
                        Interlocked.Exchange(ref _resetH264DecoderOnRecovery, 0) != 0;
                    bool dimensionChanged =
                        ShouldResetH264Decoder(frame);
                    if (dimensionChanged ||
                        (forceDecoderReset &&
                         !TryResetMediaFoundationDecoderForRecovery(
                             frame)))
                    {
                        ResetH264Decoder();
                    }

                    if (forceDecoderReset ||
                        dimensionChanged)
                    {
                        _h264DecodeMisses = 0;
                    }

                    MediaFoundationDirectDecodeAttempt directDecode =
                        TryDecodeWithMediaFoundationD3D11(
                            frame,
                            decodeStartedAt);
                    if (directDecode.Disposition ==
                            MediaFoundationDirectDecodeDisposition
                                .FrameReady &&
                        directDecode.Frame is not null)
                    {
                        _h264DecodeMisses = 0;
                        RemoteFrameMetadata hardwareSourceFrame =
                            directDecode.SourceFrame ??
                            frameMetadata;
                        long hardwareDecodeStartedAt =
                            directDecode.DecodeStartedAtTimestamp > 0
                                ? directDecode
                                    .DecodeStartedAtTimestamp
                                : decodeStartedAt;
                        long hardwareDecodedAt =
                            Stopwatch.GetTimestamp();
                        double hardwareDecodeMilliseconds =
                            Stopwatch.GetElapsedTime(
                                hardwareDecodeStartedAt)
                                .TotalMilliseconds;
                        using MediaFoundationD3D11DecodedFrame
                            hardwareFrame =
                                directDecode.Frame;
                        long presentationHandleGeneration =
                            Interlocked.Read(
                                ref _pictureBoxHandleGeneration);
                        D3D11HwndVideoPresenterResult
                            presentResult =
                                CanPresentCaptureGeneration(
                                    presentationGeneration)
                                    ? PresentHardwareFrameFromRenderThread(
                                        hardwareFrame,
                                        directDecode.DecoderGeneration,
                                        new Size(
                                            hardwareSourceFrame.Width,
                                            hardwareSourceFrame.Height),
                                        presentationGeneration)
                                    : new(
                                        D3D11HwndVideoPresenterStatus.Disposed,
                                        HResult: 0,
                                        DeviceRemovedReason: null,
                                        "The capture-target presentation generation changed.");
                        long hardwarePresentedAt =
                            Stopwatch.GetTimestamp();
                        long currentHandleGeneration =
                            Interlocked.Read(
                                ref _pictureBoxHandleGeneration);
                        if (!CanPresentCaptureGeneration(
                                presentationGeneration))
                        {
                            // An availability transition owns the blanking
                            // path. Do not misclassify its deliberate stale
                            // result as a D3D device failure or overwrite the
                            // capture-target status from a queued UI callback.
                            continue;
                        }

                        if (!IsNonFatalRenderThreadPresentationResult(
                                presentResult.Status,
                                expectedHandleGeneration:
                                    presentationHandleGeneration,
                                currentHandleGeneration,
                                IsD3DPresentationTargetReady()))
                        {
                            Interlocked.Increment(
                                ref _directHardwarePresentationFailureCount);
                            RecordDirectHardwareFailure(
                                presentResult.Detail);
                            OnUi(
                                () =>
                                    HandleDirectPresentationFailure(
                                        presentResult,
                                        directDecode
                                            .DecoderGeneration));
                            continue;
                        }

                        DirectPresentationQualificationDecision
                            qualification =
                                QualifyDirectPresentation(
                                    presentResult.Status,
                                    directDecode.DecoderGeneration);
                        if (qualification ==
                            DirectPresentationQualificationDecision
                                .FallbackToSoftware)
                        {
                            // Qualification runs only on an independently
                            // decodable output. Reuse the current AU immediately
                            // instead of waiting for another IDR.
                            if (!CanFallbackSameAccessUnitToSoftware(
                                    IsH264RecoveryFrame(frame)))
                            {
                                PrepareForH264DecoderRecovery();
                                RequestH264KeyFrameIfDue();
                                continue;
                            }
                        }
                        else
                        {
                            RecordDirectHardwarePresentation(
                                hardwareSourceFrame,
                                hardwareDecodeStartedAt,
                                hardwareDecodedAt,
                                hardwarePresentedAt,
                                presentResult.Status);
                            RecordDirectlyPresentedFrame(
                                hardwareSourceFrame,
                                hardwareDecodeMilliseconds,
                                decoderBackendName: "MF/D3D11",
                                decoderUsesHardwareAcceleration: true,
                                hardwareDecodeStartedAt,
                                hardwareDecodedAt,
                                hardwarePresentedAt,
                                presentationGeneration);
                            continue;
                        }

                    }

                    if (directDecode.Disposition ==
                        MediaFoundationDirectDecodeDisposition
                            .AwaitOutput)
                    {
                        continue;
                    }

                    if (directDecode.Disposition ==
                        MediaFoundationDirectDecodeDisposition
                            .WaitForWindow)
                    {
                        if (DeferFrameUntilViewerHandle(
                                ownedFrame))
                        {
                            frameOwnershipTransferred = true;
                        }

                        return;
                    }

                    if (directDecode.Disposition ==
                        MediaFoundationDirectDecodeDisposition
                            .AwaitRecovery)
                    {
                        PrepareForH264DecoderRecovery();
                        RequestH264KeyFrameIfDue();
                        string detail = string.IsNullOrWhiteSpace(
                            directDecode.FailureDetail)
                                ? string.Empty
                                : $"（{TrimDiagnosticDetail(
                                    directDecode.FailureDetail)}）";
                        OnUi(() => SetStatus(
                            $"MF/D3D11 硬解失步，正在等待关键帧{detail}",
                            DangerTextColor));
                        continue;
                    }

                    if (directDecode.Disposition ==
                            MediaFoundationDirectDecodeDisposition
                                .FallbackToSoftware &&
                        !CanFallbackSameAccessUnitToSoftware(
                            IsH264RecoveryFrame(frame)))
                    {
                        DisableMediaFoundationDirectPath();
                        PrepareForH264DecoderRecovery();
                        RequestH264KeyFrameIfDue();
                        OnUi(() => SetStatus(
                            "硬解失效，等待恢复帧后切换软件解码",
                            DangerTextColor));
                        continue;
                    }

                    if (!TryGetOrCreateH264Decoder(
                            frame,
                            out FfmpegH264Decoder decoder))
                    {
                        if (_isClosing || IsDisposed || Disposing)
                        {
                            return;
                        }

                        if (_client.AllowVideoFallback)
                        {
                            if (!_h264FallbackRequested)
                            {
                                _h264FallbackRequested = true;
                                Volatile.Write(
                                    ref _jpegFallbackRequested,
                                    1);
                                DiagnosticLog.Append(
                                    "VIEWER",
                                    "MF/D3D11 与 ffmpeg 均不可用，已请求 JPEG 回退。");
                                _ = _client.UpdateViewerVideoCodecsAsync(RemoteVideoCodecs.Jpeg);
                            }

                            OnUi(() => SetStatus(
                                "MF/D3D11 与 ffmpeg 均不可用，" +
                                    "正在请求 JPEG 通道",
                                DangerTextColor));
                        }
                        else
                        {
                            OnUi(() => SetStatus(
                                "强制 H.264 模式下 MF/D3D11 与 " +
                                    "ffmpeg 均不可用，且不会回退 JPEG",
                                DangerTextColor));
                        }

                        continue;
                    }

                    long submissionId = Interlocked.Increment(
                        ref _nextH264SubmissionId);
                    TrackH264Submission(
                        submissionId,
                        frameMetadata,
                        decodeStartedAt);
                    FfmpegH264DecodeResult decodeResult =
                        await decoder.DecodeAsync(
                            submissionId,
                            GetFramePayload(frame));
                    if (decodeResult.Status == FfmpegH264DecodeStatus.Failed)
                    {
                        RemoveH264Submission(
                            submissionId);
                        HandleH264DecodeFailure(decoder);
                        continue;
                    }

                    bitmap = decodeResult.Bitmap;
                    if (bitmap is null ||
                        !TryTakeH264Submission(
                            decodeResult.SubmissionId,
                            out PendingH264Submission decodedSubmission))
                    {
                        bitmap?.Dispose();
                        RemoveH264Submission(
                            submissionId);
                        HandleH264DecodeFailure(
                            decoder,
                            "，ffmpeg 输出与输入帧关联失效");
                        continue;
                    }

                    frameMetadata =
                        decodedSubmission.Frame;
                    decodeStartedAt = decodedSubmission.DecodeStartedAt;
                    if (bitmap.Width != frameMetadata.Width ||
                        bitmap.Height != frameMetadata.Height)
                    {
                        string mismatch =
                            $"，ffmpeg 输出尺寸 {bitmap.Width}x{bitmap.Height}" +
                            $" 与帧元数据 {frameMetadata.Width}x{frameMetadata.Height} 不一致";
                        bitmap.Dispose();
                        bitmap = null;
                        HandleH264DecodeFailure(
                            decoder,
                            mismatch);
                        continue;
                    }

                    _h264DecodeMisses = 0;
                    decoderBackendName = decoder.BackendName;
                    decoderUsesHardwareAcceleration =
                        decoder.UsesHardwareAcceleration;
                }
                else
                {
                    OnUi(() => SetStatus($"暂不支持的画面编码：{FormatFrameEncoding(frame.Encoding)}", DangerTextColor));
                    continue;
                }

                double decodeMilliseconds = Stopwatch.GetElapsedTime(decodeStartedAt).TotalMilliseconds;
                long decodedAt = Stopwatch.GetTimestamp();
                QueueRemoteImage(
                    frameMetadata,
                    bitmap,
                    hardwareFrame: null,
                    hardwareDecoderGeneration: 0,
                    decodeMilliseconds,
                    decoderBackendName,
                    decoderUsesHardwareAcceleration,
                    decodeStartedAt,
                    decodedAt,
                    presentationGeneration);
            }
            catch (Exception ex)
            {
                if (frame.Encoding == RemoteFrameEncoding.H264AnnexB)
                {
                    ResetH264Decoder();
                }

                if (_isClosing || IsDisposed)
                {
                    return;
                }

                OnUi(() => SetStatus($"画面渲染失败，等待下一帧：{ex.Message}", DangerTextColor));
            }
            finally
            {
                if (!frameOwnershipTransferred)
                {
                    ownedFrame.Dispose();
                }
            }
        }
    }

    private void HandleH264DecodeFailure(
        FfmpegH264Decoder decoder,
        string? failureOverride = null)
    {
        _h264DecodeMisses++;
        bool decoderStopped = !decoder.IsRunning ||
            !string.IsNullOrEmpty(failureOverride);
        string decoderFailure = failureOverride ??
            (decoderStopped
                ? FormatDecoderFailure(decoder)
                : string.Empty);
        bool fallbackThresholdReached =
            _h264DecodeMisses >= H264DecodeMissFallbackThreshold;
        if (!_h264FallbackRequested &&
            fallbackThresholdReached &&
            _client.AllowVideoFallback)
        {
            string fallbackStatus = decoderStopped
                ? $"H.264 解码反复失步，正在请求回退 JPEG{decoderFailure}"
                : "H.264 解码连续无画面，正在请求回退 JPEG";
            _h264FallbackRequested = true;
            Volatile.Write(
                ref _jpegFallbackRequested,
                1);
            DiagnosticLog.Append(
                "VIEWER",
                "H.264 解码连续无画面，已请求 JPEG 回退。");
            ResetH264Decoder();
            OnUi(() => SetStatus(fallbackStatus, DangerTextColor));
            _ = _client.UpdateViewerVideoCodecsAsync(
                RemoteVideoCodecs.Jpeg);
            return;
        }

        if (decoderStopped)
        {
            ResetH264Decoder();
            PrepareForH264DecoderRecovery();
            if (!_h264FallbackRequested)
            {
                RequestH264KeyFrameIfDue();
            }

            string status = _client.AllowVideoFallback
                ? $"H.264 解码失步，正在重建并等待关键帧{decoderFailure}"
                : $"强制 H.264 模式下解码失步，正在重建并等待关键帧{decoderFailure}";
            OnUi(() => SetStatus(status, DangerTextColor));
            return;
        }

        if (!_h264FallbackRequested &&
            (_h264DecodeMisses % H264KeyFrameRequestInterval == 0 ||
             fallbackThresholdReached))
        {
            RequestH264KeyFrameIfDue();
            if (fallbackThresholdReached && !_client.AllowVideoFallback)
            {
                OnUi(() => SetStatus(
                    "强制 H.264 模式下连续无画面，已请求关键帧",
                    DangerTextColor));
            }
        }
    }

    private void TrackH264Submission(
        long submissionId,
        RemoteFrameMetadata frame,
        long decodeStartedAt)
    {
        lock (_h264SubmissionLock)
        {
            _h264Submissions.Add(
                submissionId,
                new PendingH264Submission(
                    frame,
                    decodeStartedAt));
        }
    }

    private bool TryTakeH264Submission(
        long submissionId,
        out PendingH264Submission submission)
    {
        lock (_h264SubmissionLock)
        {
            if (!_h264Submissions.Remove(
                    submissionId,
                    out PendingH264Submission? pending))
            {
                submission = default!;
                return false;
            }

            submission = pending;
            return true;
        }
    }

    private void RemoveH264Submission(
        long submissionId)
    {
        lock (_h264SubmissionLock)
        {
            _h264Submissions.Remove(submissionId);
        }
    }

    private void TrackPendingMediaFoundationInputLocked(
        long sampleTime100Nanoseconds,
        RemoteFrameMetadata frame,
        long decodeStartedAtTimestamp)
    {
        _pendingMediaFoundationInputs[sampleTime100Nanoseconds] =
            new(
                frame,
                decodeStartedAtTimestamp);
        while (_pendingMediaFoundationInputs.Count >
            MaxPendingMediaFoundationInputs)
        {
            long oldestSampleTime =
                _pendingMediaFoundationInputs.Keys.Min();
            _pendingMediaFoundationInputs.Remove(
                oldestSampleTime);
        }
    }

    private PendingMediaFoundationInput
        ResolvePendingMediaFoundationInput(
            MediaFoundationD3D11DecodedFrame decodedFrame,
            long currentSampleTime100Nanoseconds,
            RemoteFrameMetadata currentFrame,
            long currentDecodeStartedAtTimestamp)
    {
        lock (_h264DecoderLock)
        {
            long? resolvedSampleTime = null;
            if (decodedFrame.HasExplicitSampleTime &&
                _pendingMediaFoundationInputs.ContainsKey(
                    decodedFrame.SampleTime100Nanoseconds))
            {
                resolvedSampleTime =
                    decodedFrame.SampleTime100Nanoseconds;
            }
            else if (_pendingMediaFoundationInputs.Count > 0)
            {
                if (decodedFrame.HasExplicitSampleTime)
                {
                    long[] notNewerSampleTimes =
                        _pendingMediaFoundationInputs.Keys
                            .Where(
                                sampleTime =>
                                    sampleTime <=
                                    decodedFrame
                                        .SampleTime100Nanoseconds)
                            .ToArray();
                    resolvedSampleTime =
                        notNewerSampleTimes.Length > 0
                            ? notNewerSampleTimes.Max()
                            : _pendingMediaFoundationInputs
                                .Keys.Min();
                }
                else
                {
                    resolvedSampleTime =
                        _pendingMediaFoundationInputs.Keys.Min();
                }
            }

            if (resolvedSampleTime.HasValue &&
                _pendingMediaFoundationInputs.TryGetValue(
                    resolvedSampleTime.Value,
                    out PendingMediaFoundationInput? resolved))
            {
                foreach (long staleSampleTime in
                    _pendingMediaFoundationInputs.Keys
                        .Where(
                            sampleTime =>
                                sampleTime <=
                                resolvedSampleTime.Value)
                        .ToArray())
                {
                    _pendingMediaFoundationInputs.Remove(
                        staleSampleTime);
                }

                return resolved;
            }

            _pendingMediaFoundationInputs.Remove(
                currentSampleTime100Nanoseconds);
            return new(
                currentFrame,
                currentDecodeStartedAtTimestamp);
        }
    }

    private void ClearPendingMediaFoundationInputs()
    {
        lock (_h264DecoderLock)
        {
            _pendingMediaFoundationInputs.Clear();
        }
    }

    private MediaFoundationDirectDecodeAttempt
        TryDecodeWithMediaFoundationD3D11(
            RemoteFrame frame,
            long decodeStartedAt)
    {
        bool hardwareDecoderExists;
        bool ffmpegDecoderExists;
        lock (_h264DecoderLock)
        {
            hardwareDecoderExists =
                _mediaFoundationH264Decoder is not null;
            ffmpegDecoderExists =
                _h264Decoder is not null;
        }

        if (!ShouldAttemptMediaFoundationDirectDecode(
                Volatile.Read(
                    ref _mediaFoundationDirectPathDisabled) != 0,
                hardwareDecoderExists,
                ffmpegDecoderExists,
                IsH264RecoveryFrame(frame)))
        {
            return MediaFoundationDirectDecodeAttempt
                .NotAttempted;
        }

        if (!TryGetOrCreateMediaFoundationH264Decoder(
                frame,
                out MediaFoundationD3D11H264Decoder decoder,
                out string? creationFailure))
        {
            if (!string.IsNullOrWhiteSpace(creationFailure))
            {
                RecordDirectHardwareFailure(
                    creationFailure);
                Interlocked.Exchange(
                    ref _mediaFoundationDirectPathDisabled,
                    1);
                return new(
                    MediaFoundationDirectDecodeDisposition
                        .FallbackToSoftware,
                    Frame: null,
                    creationFailure);
            }

            return MediaFoundationDirectDecodeAttempt
                .NotAttempted;
        }

        D3D11PresenterEnsureResult presenterResult =
            EnsureD3D11VideoPresenter(
                decoder,
                new Size(frame.Width, frame.Height));
        if (!presenterResult.Success)
        {
            if (presenterResult.Retryable)
            {
                int retryCount =
                    Interlocked.Increment(
                        ref _d3d11TargetRetryCount);
                if (retryCount <= 3)
                {
                    return new(
                        MediaFoundationDirectDecodeDisposition
                            .WaitForWindow,
                        Frame: null,
                        presenterResult.Detail);
                }
            }

            DisableMediaFoundationDirectPath();
            RecordDirectHardwareFailure(
                presenterResult.Detail);
            return new(
                MediaFoundationDirectDecodeDisposition
                    .FallbackToSoftware,
                Frame: null,
                presenterResult.Detail);
        }

        Interlocked.Exchange(
            ref _d3d11TargetRetryCount,
            0);
        long sampleTime100Nanoseconds;
        long decoderGeneration;
        lock (_h264DecoderLock)
        {
            if (!ReferenceEquals(
                    decoder,
                    _mediaFoundationH264Decoder))
            {
                RecordDirectHardwareFailure(
                    "The hardware decoder changed before input submission.");
                return new(
                    MediaFoundationDirectDecodeDisposition
                        .FallbackToSoftware,
                    Frame: null,
                    "The hardware decoder changed before input submission.");
            }

            sampleTime100Nanoseconds =
                _nextMediaFoundationSampleTime100Nanoseconds;
            decoderGeneration =
                _activeMediaFoundationDecoderGeneration;
            _nextMediaFoundationSampleTime100Nanoseconds =
                sampleTime100Nanoseconds +
                (10_000_000L / DirectH264FramesPerSecond);
            TrackPendingMediaFoundationInputLocked(
                sampleTime100Nanoseconds,
                RemoteFrameMetadata.FromFrame(frame),
                decodeStartedAt);
        }

        MediaFoundationD3D11DecodeResult decodeResult;
        try
        {
            decodeResult = decoder.DecodeAccessUnit(
                GetFramePayload(frame),
                sampleTime100Nanoseconds);
        }
        catch (Exception ex)
        {
            decodeResult = new(
                MediaFoundationD3D11DecodeStatus.Failed,
                Frame: null,
                ex.Message);
        }

        if (decodeResult.Status ==
                MediaFoundationD3D11DecodeStatus.FrameReady &&
            decodeResult.Frame is not null)
        {
            PendingMediaFoundationInput decodedInput =
                ResolvePendingMediaFoundationInput(
                    decodeResult.Frame,
                    sampleTime100Nanoseconds,
                    RemoteFrameMetadata.FromFrame(frame),
                    decodeStartedAt);
            return new(
                MediaFoundationDirectDecodeDisposition
                    .FrameReady,
                decodeResult.Frame,
                FailureDetail: null,
                decoderGeneration,
                decodedInput.Frame,
                decodedInput.DecodeStartedAtTimestamp);
        }

        decodeResult.Frame?.Dispose();
        if (decodeResult.Status ==
            MediaFoundationD3D11DecodeStatus
                .AcceptedAwaitingOutput)
        {
            return new(
                MediaFoundationDirectDecodeDisposition
                    .AwaitOutput,
                Frame: null,
                decodeResult.Detail,
                decoderGeneration);
        }

        ClearPendingMediaFoundationInputs();
        bool decoderCanRecover =
            decoder.State ==
                MediaFoundationD3D11DecoderState.Streaming &&
            decoder.ReferenceState ==
                MediaFoundationD3D11ReferenceState
                    .NeedsIndependentFrame;
        if (!CanFallbackSameAccessUnitToSoftware(
                IsH264RecoveryFrame(frame)))
        {
            RecordDirectHardwareFailure(
                decodeResult.Detail);
            if (!decoderCanRecover)
            {
                DisableMediaFoundationDirectPath();
            }

            return new(
                MediaFoundationDirectDecodeDisposition
                    .AwaitRecovery,
                Frame: null,
                decodeResult.Detail);
        }

        RecordDirectHardwareFailure(
            decodeResult.Detail ??
                "MF/D3D11 produced no frame for the recovery access unit.");
        DisableMediaFoundationDirectPath();
        return new(
            MediaFoundationDirectDecodeDisposition
                .FallbackToSoftware,
            Frame: null,
            decodeResult.Detail ??
                "MF/D3D11 produced no frame for the recovery access unit.");
    }

    internal static bool CanFallbackSameAccessUnitToSoftware(
        bool isRecoveryFrame) =>
        isRecoveryFrame;

    internal static bool ShouldAttemptMediaFoundationDirectDecode(
        bool directPathDisabled,
        bool hardwareDecoderExists,
        bool ffmpegDecoderExists,
        bool isRecoveryFrame)
    {
        if (directPathDisabled)
        {
            return false;
        }

        if (hardwareDecoderExists)
        {
            return true;
        }

        return !ffmpegDecoderExists &&
            isRecoveryFrame;
    }

    private bool TryGetOrCreateMediaFoundationH264Decoder(
        RemoteFrame frame,
        out MediaFoundationD3D11H264Decoder decoder,
        out string? failureDetail)
    {
        if (frame.Width <
                MediaFoundationD3D11H264DecoderOptions
                    .MinimumDimension ||
            frame.Width >
                MediaFoundationD3D11H264DecoderOptions
                    .MaximumWidth ||
            frame.Height <
                MediaFoundationD3D11H264DecoderOptions
                    .MinimumDimension ||
            frame.Height >
                MediaFoundationD3D11H264DecoderOptions
                    .MaximumHeight ||
            (frame.Width & 1) != 0 ||
            (frame.Height & 1) != 0)
        {
            decoder = null!;
            failureDetail =
                $"MF/D3D11 不支持 {frame.Width}x{frame.Height} 的 H.264/NV12 尺寸";
            return false;
        }

        lock (_h264DecoderLock)
        {
            if (_isClosing ||
                IsDisposed ||
                Disposing ||
                Volatile.Read(
                    ref _mediaFoundationDirectPathDisabled) != 0)
            {
                decoder = null!;
                failureDetail = null;
                return false;
            }

            if (_mediaFoundationH264Decoder is null)
            {
                if (_h264Decoder is not null ||
                    !IsH264RecoveryFrame(frame))
                {
                    decoder = null!;
                    failureDetail = null;
                    return false;
                }

                var options =
                    new MediaFoundationD3D11H264DecoderOptions(
                        frame.Width,
                        frame.Height,
                        DirectH264FramesPerSecond,
                        MaxAccessUnitBytes:
                            32 * 1024 * 1024,
                        // When ShortGopH264 is not advertised, every received
                        // GOP1 sample carries SPS/PPS/IDR and is independently
                        // decodable. Tell the inbox MFT this explicitly so it
                        // need not retain one frame waiting for a future
                        // reference sample.
                        AllSamplesIndependent:
                            RemoteViewerClient
                                .LocalH264SamplesAreAllIndependent);
                if (!MediaFoundationD3D11H264Decoder.TryCreate(
                        options,
                        out MediaFoundationD3D11H264Decoder? created,
                        out MediaFoundationD3D11Capability capability))
                {
                    decoder = null!;
                    failureDetail = capability.Detail;
                    return false;
                }

                if (_isClosing ||
                    IsDisposed ||
                    Disposing)
                {
                    created.Dispose();
                    decoder = null!;
                    failureDetail = null;
                    return false;
                }

                _mediaFoundationH264Decoder = created;
                _mediaFoundationH264DecoderSize =
                    new Size(frame.Width, frame.Height);
                ResetDirectPresentationQualification();
                _activeMediaFoundationDecoderGeneration =
                    ++_nextMediaFoundationDecoderGeneration;
                _nextMediaFoundationSampleTime100Nanoseconds = 0;
                _pendingMediaFoundationInputs.Clear();
                Volatile.Write(
                    ref _d3d11PresenterMatchesCurrentDecoder,
                    0);
            }

            decoder = _mediaFoundationH264Decoder;
            failureDetail = null;
            return true;
        }
    }

    private D3D11PresenterEnsureResult EnsureD3D11VideoPresenter(
        MediaFoundationD3D11H264Decoder decoder,
        Size sourceSize)
    {
        if (_isClosing ||
            IsDisposed ||
            Disposing ||
            !IsD3DPresentationTargetReady())
        {
            return new(
                Success: false,
                Retryable: true,
                "The viewer HWND is not ready for direct presentation.");
        }

        if (!TryGetCurrentMediaFoundationDecoderGeneration(
                decoder,
                out long decoderGeneration))
        {
            return new(
                Success: false,
                Retryable: false,
                "The hardware decoder changed before presenter creation.");
        }

        long handleGeneration =
            Interlocked.Read(
                ref _pictureBoxHandleGeneration);
        if (Volatile.Read(
                ref _d3d11PresenterMatchesCurrentDecoder) != 0)
        {
            lock (_d3d11PresenterLock)
            {
                if (_d3d11VideoPresenter is not null &&
                    _d3d11VideoPresenterDecoderGeneration ==
                        decoderGeneration &&
                    _d3d11VideoPresenterHandleGeneration ==
                        handleGeneration &&
                    _d3d11VideoPresenterSourceSize ==
                        sourceSize)
                {
                    return D3D11PresenterEnsureResult.Ready;
                }
            }
        }

        try
        {
            using var deviceLease =
                decoder.AcquireDeviceLease();
            nint devicePointer =
                deviceLease.NativePointer;
            D3D11PresenterEnsureResult CreateOnUiThread()
            {
                if (!TryGetCurrentMediaFoundationDecoderGeneration(
                        decoder,
                        out long currentDecoderGeneration) ||
                    currentDecoderGeneration !=
                        decoderGeneration)
                {
                    return new(
                        Success: false,
                        Retryable: false,
                        "The hardware decoder changed during presenter creation.");
                }

                long currentHandleGeneration =
                    Interlocked.Read(
                        ref _pictureBoxHandleGeneration);
                lock (_d3d11PresenterLock)
                {
                    if (_isClosing ||
                        IsDisposed ||
                        Disposing ||
                        !_pictureBox.IsHandleCreated)
                    {
                        return new(
                            Success: false,
                            Retryable: true,
                            "The viewer HWND is closing.");
                    }

                    if (_d3d11VideoPresenter is not null &&
                        _d3d11VideoPresenterDevicePointer ==
                            devicePointer &&
                        _d3d11VideoPresenterSourceSize ==
                            sourceSize &&
                        _d3d11VideoPresenterDecoderGeneration ==
                            decoderGeneration &&
                        _d3d11VideoPresenterHandleGeneration ==
                            currentHandleGeneration)
                    {
                        Volatile.Write(
                            ref _d3d11PresenterMatchesCurrentDecoder,
                            1);
                        return D3D11PresenterEnsureResult.Ready;
                    }

                    DisposeD3D11VideoPresenterLocked();
                    var options =
                        new D3D11HwndVideoPresenterOptions(
                            _pictureBox.Handle,
                            sourceSize.Width,
                            sourceSize.Height,
                            DirectH264FramesPerSecond,
                            GetD3D11VideoScaleMode());
                    if (!D3D11HwndVideoPresenter.TryCreate(
                            deviceLease,
                            options,
                            out D3D11HwndVideoPresenter? presenter,
                            out D3D11HwndVideoPresenterCapability
                                capability))
                    {
                        bool retryable =
                            capability.Status ==
                                D3D11HwndVideoPresenterCapabilityStatus
                                    .InvalidWindow;
                        return new(
                            Success: false,
                            retryable,
                            capability.Detail);
                    }

                    _d3d11VideoPresenter = presenter;
                    _d3d11VideoPresenterDevicePointer =
                        devicePointer;
                    _d3d11VideoPresenterSourceSize =
                        sourceSize;
                    _d3d11VideoPresenterDecoderGeneration =
                        decoderGeneration;
                    _d3d11VideoPresenterHandleGeneration =
                        currentHandleGeneration;
                    Volatile.Write(
                        ref _d3d11PresenterMatchesCurrentDecoder,
                        1);
                    return D3D11PresenterEnsureResult.Ready;
                }
            }

            if (InvokeRequired)
            {
                return (D3D11PresenterEnsureResult)Invoke(
                    CreateOnUiThread);
            }

            return CreateOnUiThread();
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or
                ObjectDisposedException or
                ExternalException)
        {
            return new(
                Success: false,
                Retryable:
                    !_isClosing &&
                    !IsDisposed &&
                    !Disposing,
                ex.Message);
        }
    }

    private void DisableMediaFoundationDirectPath()
    {
        Interlocked.Exchange(
            ref _mediaFoundationDirectPathDisabled,
            1);
        ResetDirectPresentationQualification();
        ResetMediaFoundationH264Decoder();
    }

    private void ResetMediaFoundationH264Decoder()
    {
        MediaFoundationD3D11H264Decoder? decoder;
        lock (_h264DecoderLock)
        {
            decoder = _mediaFoundationH264Decoder;
            _mediaFoundationH264Decoder = null;
            _mediaFoundationH264DecoderSize = Size.Empty;
            _activeMediaFoundationDecoderGeneration = 0;
            _nextMediaFoundationSampleTime100Nanoseconds = 0;
            _pendingMediaFoundationInputs.Clear();
            Volatile.Write(
                ref _d3d11PresenterMatchesCurrentDecoder,
                0);
        }

        Interlocked.Exchange(
            ref _d3d11TargetRetryCount,
            0);
        ResetDirectPresentationQualification();
        decoder?.Dispose();
    }

    internal static ReadOnlyMemory<byte> GetFramePayload(
        RemoteFrame frame) =>
        frame.EncodedBuffer.AsMemory(
            frame.EncodedOffset,
            frame.EncodedLength);

    private void ResetH264Decoder()
    {
        FfmpegH264Decoder? decoder;
        MediaFoundationD3D11H264Decoder? hardwareDecoder;
        lock (_h264DecoderLock)
        {
            decoder = _h264Decoder;
            _h264Decoder = null;
            _h264DecoderSize = Size.Empty;
            hardwareDecoder = _mediaFoundationH264Decoder;
            _mediaFoundationH264Decoder = null;
            _mediaFoundationH264DecoderSize = Size.Empty;
            _activeMediaFoundationDecoderGeneration = 0;
            _nextMediaFoundationSampleTime100Nanoseconds = 0;
            _pendingMediaFoundationInputs.Clear();
            Volatile.Write(
                ref _d3d11PresenterMatchesCurrentDecoder,
                0);
        }

        lock (_h264SubmissionLock)
        {
            _h264Submissions.Clear();
        }

        decoder?.Dispose();
        hardwareDecoder?.Dispose();
        Interlocked.Exchange(
            ref _d3d11TargetRetryCount,
            0);
        ResetDirectPresentationQualification();
    }

    private bool TryResetMediaFoundationDecoderForRecovery(
        RemoteFrame recoveryFrame)
    {
        MediaFoundationD3D11H264Decoder? decoder;
        lock (_h264DecoderLock)
        {
            if (_isClosing ||
                _h264Decoder is not null ||
                _mediaFoundationH264Decoder is null ||
                _mediaFoundationH264DecoderSize !=
                    new Size(
                        recoveryFrame.Width,
                        recoveryFrame.Height))
            {
                return false;
            }

            decoder =
                _mediaFoundationH264Decoder;
        }

        try
        {
            if (!decoder.ResetForDiscontinuity(
                    out _))
            {
                return false;
            }

            long previousGeneration;
            long nextGeneration;
            lock (_h264DecoderLock)
            {
                if (!ReferenceEquals(
                        decoder,
                        _mediaFoundationH264Decoder))
                {
                    return false;
                }

                previousGeneration =
                    _activeMediaFoundationDecoderGeneration;
                nextGeneration =
                    ++_nextMediaFoundationDecoderGeneration;
                _activeMediaFoundationDecoderGeneration =
                    nextGeneration;
                _pendingMediaFoundationInputs.Clear();
            }

            lock (_d3d11PresenterLock)
            {
                if (_d3d11VideoPresenter is not null &&
                    _d3d11VideoPresenterDecoderGeneration ==
                        previousGeneration)
                {
                    _d3d11VideoPresenterDecoderGeneration =
                        nextGeneration;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryGetOrCreateH264Decoder(
        RemoteFrame frame,
        out FfmpegH264Decoder decoder)
    {
        lock (_h264DecoderLock)
        {
            if (_isClosing || IsDisposed || Disposing)
            {
                decoder = null!;
                return false;
            }

            if (_h264Decoder is null)
            {
                FfmpegH264Decoder? created =
                    FfmpegH264Decoder.TryCreate(
                        new Size(frame.Width, frame.Height));
                if (created is null)
                {
                    decoder = null!;
                    return false;
                }

                if (_isClosing || IsDisposed || Disposing)
                {
                    created.Dispose();
                    decoder = null!;
                    return false;
                }

                _h264Decoder = created;
                _h264DecoderSize =
                    new Size(frame.Width, frame.Height);
            }

            decoder = _h264Decoder!;
            return true;
        }
    }

    private void PrepareForH264DecoderRecovery()
    {
        lock (_pendingFrameLock)
        {
            PooledRemoteFrame[] queuedFrames =
                _h264Frames.ToArray();
            int recoveryIndex = Array.FindLastIndex(
                queuedFrames,
                queuedFrame =>
                    IsH264RecoveryFrame(
                        queuedFrame.Frame));
            _h264Frames.Clear();
            Interlocked.Exchange(ref _resetH264DecoderOnRecovery, 0);
            if (recoveryIndex < 0)
            {
                foreach (PooledRemoteFrame queuedFrame in
                    queuedFrames)
                {
                    queuedFrame.Dispose();
                }

                _waitingForH264RecoveryFrame = true;
                return;
            }

            for (int index = 0;
                index < recoveryIndex;
                index++)
            {
                queuedFrames[index].Dispose();
            }

            _waitingForH264RecoveryFrame = false;
            for (int index = recoveryIndex; index < queuedFrames.Length; index++)
            {
                _h264Frames.Enqueue(queuedFrames[index]);
            }
        }
    }

    private bool EnqueueH264Frame(
        PooledRemoteFrame ownedFrame)
    {
        RemoteFrame frame = ownedFrame.Frame;
        _pendingFrame?.Dispose();
        _pendingFrame = null;
        bool recoveryFrame = IsH264RecoveryFrame(frame);
        if (_waitingForH264RecoveryFrame)
        {
            if (!recoveryFrame)
            {
                return false;
            }

            _waitingForH264RecoveryFrame = false;
            ClearH264FramesLocked();
        }
        else if (ShouldClearH264QueueForIncomingFrame(
            frame,
            _h264Frames.Count,
            RemoteViewerClient
                .LocalH264SamplesAreAllIndependent))
        {
            ClearH264FramesLocked();
        }

        _h264Frames.Enqueue(ownedFrame);
        if (_h264Frames.Count <= MaxQueuedH264Frames)
        {
            return true;
        }

        PooledRemoteFrame[] frames =
            _h264Frames.ToArray();
        RemoteFrame[] frameDescriptors =
            frames
                .Select(queuedFrame => queuedFrame.Frame)
                .ToArray();
        int startIndex = FindH264QueueStartIndex(
            frameDescriptors,
            MaxQueuedH264Frames,
            out bool startsAtRecoveryFrame);
        _h264Frames.Clear();
        for (int index = 0; index < startIndex; index++)
        {
            frames[index].Dispose();
        }

        for (int index = startIndex; index < frames.Length; index++)
        {
            _h264Frames.Enqueue(frames[index]);
        }

        if (!startsAtRecoveryFrame)
        {
            ClearH264FramesLocked();
            _waitingForH264RecoveryFrame = true;
            Interlocked.Exchange(ref _resetH264DecoderOnRecovery, 1);
            RequestH264KeyFrameIfDue();
        }

        return true;
    }

    private bool ShouldResetH264Decoder(RemoteFrame frame)
    {
        lock (_h264DecoderLock)
        {
            Size decoderSize =
                !_mediaFoundationH264DecoderSize.IsEmpty
                    ? _mediaFoundationH264DecoderSize
                    : _h264DecoderSize;
            return ShouldResetH264DecoderForFrame(
                decoderSize,
                frame);
        }
    }

    internal static bool ShouldResetH264DecoderForFrame(
        Size decoderSize,
        RemoteFrame frame)
    {
        return IsH264RecoveryFrame(frame) &&
            !decoderSize.IsEmpty &&
            (decoderSize.Width != frame.Width ||
                decoderSize.Height != frame.Height);
    }

    internal static int FindH264QueueStartIndex(
        IReadOnlyList<RemoteFrame> frames,
        int maxQueuedFrames,
        out bool startsAtRecoveryFrame)
    {
        startsAtRecoveryFrame = false;
        if (frames.Count == 0)
        {
            return 0;
        }

        int normalizedMaxQueuedFrames = Math.Max(1, maxQueuedFrames);
        int searchStart = Math.Max(0, frames.Count - normalizedMaxQueuedFrames);
        for (int index = frames.Count - 1; index >= searchStart; index--)
        {
            if (frames[index].Flags.HasFlag(RemoteFrameFlags.KeyFrame) &&
                frames[index].Flags.HasFlag(RemoteFrameFlags.CodecConfig))
            {
                startsAtRecoveryFrame = true;
                return index;
            }
        }

        for (int index = frames.Count - 1; index >= searchStart; index--)
        {
            if (frames[index].Flags.HasFlag(RemoteFrameFlags.KeyFrame))
            {
                startsAtRecoveryFrame = true;
                return index;
            }
        }

        return frames.Count;
    }

    internal static bool ShouldClearH264QueueForIncomingFrame(
        RemoteFrame frame,
        int queuedFrameCount,
        bool samplesAreAllIndependent)
    {
        if (!IsH264RecoveryFrame(frame))
        {
            return false;
        }

        // Negotiated GOP1 makes every sample independently decodable. When
        // rendering is briefly delayed, retain only the newest sample instead
        // of presenting up to four stale 30 Hz frames in FIFO order.
        if (samplesAreAllIndependent)
        {
            return queuedFrameCount > 0;
        }

        // A GOP=2 stream alternates IDR/P. Clearing a healthy one-frame
        // queue whenever the next IDR arrives systematically discards every
        // P frame when the render worker is waiting to be scheduled, cutting
        // an otherwise 60 FPS hardware path to 30 FPS. Preserve the bounded
        // queue during normal operation; only let a fresh recovery point
        // replace a queue that is already at the latency cap.
        return queuedFrameCount >= MaxQueuedH264Frames;
    }

    private bool TryDequeueFrame(
        out PooledRemoteFrame frame)
    {
        if (_h264Frames.Count > 0)
        {
            frame = _h264Frames.Dequeue();
            return true;
        }

        if (_pendingFrame is { } pendingFrame)
        {
            frame = pendingFrame;
            _pendingFrame = null;
            return true;
        }

        frame = null!;
        return false;
    }

    private void ClearH264FramesLocked()
    {
        while (_h264Frames.TryDequeue(
            out PooledRemoteFrame? frame))
        {
            frame.Dispose();
        }
    }

    private static bool IsH264RecoveryFrame(RemoteFrame frame)
    {
        return frame.Encoding == RemoteFrameEncoding.H264AnnexB &&
            frame.Flags.HasFlag(RemoteFrameFlags.KeyFrame) &&
            frame.Flags.HasFlag(RemoteFrameFlags.CodecConfig);
    }

    private void RequestH264KeyFrameIfDue()
    {
        long now = Environment.TickCount64;
        long previous = Interlocked.Read(ref _lastH264KeyFrameRequestAt);
        if (previous != 0 && now - previous < H264KeyFrameRequestMinIntervalMs)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastH264KeyFrameRequestAt, now, previous) == previous)
        {
            _ = _client.RequestVideoKeyFrameAsync();
        }
    }

    private void QueueRemoteImage(
        RemoteFrameMetadata frame,
        Bitmap? bitmap,
        MediaFoundationD3D11DecodedFrame? hardwareFrame,
        long hardwareDecoderGeneration,
        double decodeMilliseconds,
        string? decoderBackendName,
        bool decoderUsesHardwareAcceleration,
        long decodeStartedAtTimestamp,
        long decodedAtTimestamp,
        long presentationGeneration)
    {
        var decodedFrame = new DecodedRemoteFrame(
            frame,
            bitmap,
            hardwareFrame,
            hardwareDecoderGeneration,
            decodeMilliseconds,
            decoderBackendName,
            decoderUsesHardwareAcceleration,
            decodeStartedAtTimestamp,
            decodedAtTimestamp,
            alreadyPresented: false,
            presentedAtTimestamp: 0,
            presentationGeneration);
        if (_isClosing ||
            IsDisposed ||
            !CanPresentCaptureGeneration(
                presentationGeneration))
        {
            decodedFrame.Dispose();
            return;
        }

        LatestFrameOffer<DecodedRemoteFrame> offer =
            _decodedFrameMailbox.Offer(decodedFrame);
        offer.Replaced?.Dispose();
        if (!offer.Accepted)
        {
            decodedFrame.Dispose();
            return;
        }

        if (offer.ShouldSchedule)
        {
            PostLatestRemoteImage();
        }
    }

    private void PostLatestRemoteImage()
    {
        if (_isClosing || IsDisposed || Disposing)
        {
            _decodedFrameMailbox.Close()?.Dispose();
            return;
        }

        // Frames can arrive after the client event is subscribed but before Show()
        // creates the form handle. Keep the pending frame; OnHandleCreated posts it.
        if (!IsHandleCreated)
        {
            return;
        }

        long dispatchToken = Interlocked.Increment(ref _nextDecodedFrameDispatchToken);
        if (Interlocked.CompareExchange(
                ref _postedDecodedFrameDispatchToken,
                dispatchToken,
                0) != 0)
        {
            return;
        }

        try
        {
            BeginInvoke(() => ProcessLatestRemoteImage(dispatchToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            Interlocked.CompareExchange(
                ref _postedDecodedFrameDispatchToken,
                0,
                dispatchToken);
            if (_isClosing || IsDisposed || Disposing)
            {
                _decodedFrameMailbox.Close()?.Dispose();
            }
            else if (_decodedFrameMailbox.HasPendingDispatch)
            {
                ScheduleDecodedFrameDispatchRetry();
            }
        }
    }

    private void ScheduleDecodedFrameDispatchRetry()
    {
        if (Interlocked.CompareExchange(
                ref _decodedDispatchRetryPending,
                1,
                0) != 0)
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(
                            10)
                        .ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Exchange(
                        ref _decodedDispatchRetryPending,
                        0);
                }

                if (!_isClosing &&
                    !IsDisposed &&
                    !Disposing &&
                    _decodedFrameMailbox
                        .HasPendingDispatch)
                {
                    PostLatestRemoteImage();
                }
            });
    }

    private void ProcessLatestRemoteImage(long dispatchToken)
    {
        DecodedRemoteFrame? decodedFrame = null;
        try
        {
            decodedFrame = _decodedFrameMailbox.TakeLatest();
            if (decodedFrame is null)
            {
                return;
            }

            if (_isClosing || IsDisposed)
            {
                return;
            }

            if (!CanPresentCaptureGeneration(
                    decodedFrame.PresentationGeneration))
            {
                return;
            }

            RemoteFrameMetadata frame =
                decodedFrame.Frame;
            if (!decodedFrame.AlreadyPresented)
            {
                MediaFoundationD3D11DecodedFrame? hardwareFrame =
                    decodedFrame.DetachHardwareFrame();
                if (hardwareFrame is not null)
                {
                    if (!IsCurrentMediaFoundationDecoderGeneration(
                            decodedFrame
                                .HardwareDecoderGeneration))
                    {
                        hardwareFrame.Dispose();
                        return;
                    }

                    using (hardwareFrame)
                    {
                        D3D11HwndVideoPresenterResult presentResult =
                            PresentHardwareFrame(
                                hardwareFrame,
                                decodedFrame.HardwareDecoderGeneration,
                                new Size(
                                    frame.Width,
                                    frame.Height));
                        if (!IsNonFatalPresentationResult(
                                presentResult.Status))
                        {
                            HandleDirectPresentationFailure(
                                presentResult,
                                decodedFrame
                                    .HardwareDecoderGeneration);
                            return;
                        }
                    }
                }
                else
                {
                    Bitmap bitmap =
                        decodedFrame.DetachBitmap() ??
                        throw new InvalidOperationException(
                            "The decoded frame has no display payload.");
                    bool imageAssigned = false;
                    try
                    {
                        DeactivateD3D11Presentation(
                            disposePresenter: true);
                        UpdatePictureBoxDisplayMode(
                            new Size(
                                frame.Width,
                                frame.Height));
                        _pictureBox.Image = bitmap;
                        Image? oldImage = _currentImage;
                        _currentImage = bitmap;
                        imageAssigned = true;
                        oldImage?.Dispose();
                    }
                    finally
                    {
                        if (!imageAssigned)
                        {
                            bitmap.Dispose();
                        }
                    }
                }

                decodedFrame.MarkPresented(
                    Stopwatch.GetTimestamp());
            }

            Interlocked.Exchange(
                ref _remoteImageSizePacked,
                PackRemoteImageSize(
                    frame.Width,
                    frame.Height));
            Interlocked.Exchange(
                ref _directFrameUiSignature,
                long.MinValue);
            string decoderBackend =
                decodedFrame.DecoderBackendName ??
                string.Empty;
            UpdateRenderedVideoStatus(
                frame.Encoding,
                decoderBackend,
                FormatDecodedVideoName(decodedFrame));

            UpdateFrameStats(decodedFrame);
        }
        catch (Exception ex)
        {
            if (!_isClosing && !IsDisposed)
            {
                try
                {
                    SetStatus($"画面显示失败，等待下一帧：{ex.Message}", DangerTextColor);
                }
                catch (Exception statusException)
                    when (statusException is InvalidOperationException or ObjectDisposedException or ArgumentException)
                {
                }
            }
        }
        finally
        {
            decodedFrame?.Dispose();
            bool needsFollowUp = _decodedFrameMailbox.CompleteDispatch();
            Interlocked.CompareExchange(
                ref _postedDecodedFrameDispatchToken,
                0,
                dispatchToken);
            if (needsFollowUp || _decodedFrameMailbox.HasPendingDispatch)
            {
                PostLatestRemoteImage();
            }
        }
    }

    /// <summary>
    /// Submits an already-decoded GPU frame immediately on the serial decode
    /// worker. The presenter is created on the UI thread and activated only
    /// after pixel qualification; its own lock serializes this
    /// call with UI-driven resize/disposal. Avoiding a per-frame BeginInvoke
    /// keeps mouse and window messages from becoming an extra display queue.
    /// </summary>
    private D3D11HwndVideoPresenterResult
        PresentHardwareFrameFromRenderThread(
            MediaFoundationD3D11DecodedFrame frame,
            long decoderGeneration,
            Size sourceSize,
            long presentationGeneration)
    {
        return _capturePresentationTransitionGate.Run(() =>
        {
            if (!CanPresentCaptureGeneration(
                    presentationGeneration) ||
                !IsCurrentMediaFoundationDecoderGeneration(
                    decoderGeneration))
            {
                return new(
                    D3D11HwndVideoPresenterStatus.Disposed,
                    HResult: 0,
                    DeviceRemovedReason: null,
                    "The decoded GPU frame belongs to an obsolete " +
                    "capture-target presentation generation.");
            }

            Size outputSize =
                new(
                    Volatile.Read(
                        ref _pictureBoxClientWidth),
                    Volatile.Read(
                        ref _pictureBoxClientHeight));
            if (!IsD3DClientAreaReady(outputSize))
            {
                return new(
                    D3D11HwndVideoPresenterStatus.SkippedMinimized,
                    HResult: 0,
                    DeviceRemovedReason: null,
                    "The viewer is minimized.");
            }

            D3D11HwndVideoPresenter? presenter;
            long handleGeneration =
                Interlocked.Read(
                    ref _pictureBoxHandleGeneration);
            lock (_d3d11PresenterLock)
            {
                presenter = _d3d11VideoPresenter;
                if (presenter is null ||
                    _d3d11VideoPresenterDecoderGeneration !=
                        decoderGeneration ||
                    _d3d11VideoPresenterHandleGeneration !=
                        handleGeneration ||
                    _d3d11VideoPresenterSourceSize !=
                        sourceSize)
                {
                    return new(
                        D3D11HwndVideoPresenterStatus.Disposed,
                        HResult: 0,
                        DeviceRemovedReason: null,
                        "The presenter changed before the decoded frame " +
                        "could be submitted.");
                }
            }

            if (presenter.OutputSize != outputSize)
            {
                D3D11HwndVideoPresenterResult resizeResult =
                    presenter.Resize(
                        outputSize.Width,
                        outputSize.Height);
                if (!resizeResult.IsSuccess)
                {
                    return resizeResult;
                }
            }

            D3D11HwndVideoPresenterResult result =
                presenter.Present(
                    frame,
                    new Rectangle(
                        Point.Empty,
                        new Size(
                            frame.VisibleWidth,
                            frame.VisibleHeight)),
                    captureValidation: true);
            return CanPresentCaptureGeneration(
                    presentationGeneration)
                ? result
                : new(
                    D3D11HwndVideoPresenterStatus.Disposed,
                    HResult: 0,
                    DeviceRemovedReason: null,
                    "The capture-target presentation generation changed " +
                    "during direct presentation.");
        });
    }

    private D3D11HwndVideoPresenterResult PresentHardwareFrame(
        MediaFoundationD3D11DecodedFrame frame,
        long decoderGeneration,
        Size sourceSize)
    {
        if (!IsCurrentMediaFoundationDecoderGeneration(
                decoderGeneration))
        {
            return new(
                D3D11HwndVideoPresenterStatus.Disposed,
                HResult: 0,
                DeviceRemovedReason: null,
                "The decoded GPU frame belongs to an obsolete decoder.");
        }

        Size outputSize = _pictureBox.ClientSize;
        if (outputSize.Width <= 0 ||
            outputSize.Height <= 0)
        {
            return new(
                D3D11HwndVideoPresenterStatus.SkippedMinimized,
                HResult: 0,
                DeviceRemovedReason: null,
                "The viewer is minimized.");
        }

        D3D11HwndVideoPresenter? presenter;
        long handleGeneration =
            Interlocked.Read(
                ref _pictureBoxHandleGeneration);
        bool presenterMatches;
        lock (_d3d11PresenterLock)
        {
            presenter = _d3d11VideoPresenter;
            presenterMatches =
                presenter is not null &&
                _d3d11VideoPresenterDecoderGeneration ==
                    decoderGeneration &&
                _d3d11VideoPresenterHandleGeneration ==
                    handleGeneration &&
                _d3d11VideoPresenterSourceSize ==
                    sourceSize;
        }

        if (!presenterMatches)
        {
            if (!_pictureBox.IsHandleCreated)
            {
                return new(
                    D3D11HwndVideoPresenterStatus
                        .SkippedMinimized,
                    HResult: 0,
                    DeviceRemovedReason: null,
                    "The viewer HWND is being recreated.");
            }

            if (!TryRebuildD3D11PresenterForFrame(
                    frame,
                    decoderGeneration,
                    sourceSize,
                    out D3D11HwndVideoPresenterResult
                        initialRebuildFailure))
            {
                return initialRebuildFailure;
            }

            lock (_d3d11PresenterLock)
            {
                presenter = _d3d11VideoPresenter;
            }
        }

        if (presenter is null)
        {
            return new(
                D3D11HwndVideoPresenterStatus.Failed,
                HResult: 0,
                DeviceRemovedReason: null,
                "The rebuilt D3D11 presenter is unavailable.");
        }

        if (presenter.OutputSize != outputSize)
        {
            D3D11HwndVideoPresenterResult resizeResult =
                presenter.Resize(
                    outputSize.Width,
                    outputSize.Height);
            if (!resizeResult.IsSuccess)
            {
                return resizeResult;
            }
        }

        var visibleSource =
            new Rectangle(
                Point.Empty,
                new Size(
                    frame.VisibleWidth,
                    frame.VisibleHeight));
        D3D11HwndVideoPresenterResult result =
            presenter.Present(
                frame,
                visibleSource);
        if (result.Status !=
            D3D11HwndVideoPresenterStatus.WrongDevice)
        {
            return result;
        }

        if (!TryRebuildD3D11PresenterForFrame(
                frame,
                decoderGeneration,
                sourceSize,
                out D3D11HwndVideoPresenterResult
                    rebuildFailure))
        {
            return rebuildFailure;
        }

        lock (_d3d11PresenterLock)
        {
            presenter = _d3d11VideoPresenter;
        }

        return presenter?.Present(
                frame,
                visibleSource) ??
            new(
                D3D11HwndVideoPresenterStatus.Failed,
                HResult: 0,
                DeviceRemovedReason: null,
                "The rebuilt D3D11 presenter is unavailable.");
    }

    private bool TryRebuildD3D11PresenterForFrame(
        MediaFoundationD3D11DecodedFrame frame,
        long decoderGeneration,
        Size sourceSize,
        out D3D11HwndVideoPresenterResult failure)
    {
        if (!IsCurrentMediaFoundationDecoderGeneration(
                decoderGeneration))
        {
            failure = new(
                D3D11HwndVideoPresenterStatus.Disposed,
                HResult: 0,
                DeviceRemovedReason: null,
                "The decoded GPU frame is stale.");
            return false;
        }

        try
        {
            if (!_pictureBox.IsHandleCreated)
            {
                failure = new(
                    D3D11HwndVideoPresenterStatus
                        .SkippedMinimized,
                    HResult: 0,
                    DeviceRemovedReason: null,
                    "The viewer HWND is being recreated.");
                return false;
            }

            long handleGeneration =
                Interlocked.Read(
                    ref _pictureBoxHandleGeneration);
            using var deviceLease =
                frame.AcquireDeviceLease();
            lock (_d3d11PresenterLock)
            {
                DisposeD3D11VideoPresenterLocked();
                var options =
                    new D3D11HwndVideoPresenterOptions(
                        _pictureBox.Handle,
                        sourceSize.Width,
                        sourceSize.Height,
                        DirectH264FramesPerSecond,
                        GetD3D11VideoScaleMode());
                if (!D3D11HwndVideoPresenter.TryCreate(
                        deviceLease,
                        options,
                        out D3D11HwndVideoPresenter? presenter,
                        out D3D11HwndVideoPresenterCapability
                            capability))
                {
                    failure = new(
                        D3D11HwndVideoPresenterStatus.Failed,
                        HResult: 0,
                        DeviceRemovedReason: null,
                        capability.Detail);
                    return false;
                }

                _d3d11VideoPresenter = presenter;
                _d3d11VideoPresenterDevicePointer =
                    deviceLease.NativePointer;
                _d3d11VideoPresenterSourceSize =
                    sourceSize;
                _d3d11VideoPresenterDecoderGeneration =
                    decoderGeneration;
                _d3d11VideoPresenterHandleGeneration =
                    handleGeneration;
                Volatile.Write(
                    ref _d3d11PresenterMatchesCurrentDecoder,
                    1);
            }

            failure = default;
            return true;
        }
        catch (Exception ex)
        {
            failure = new(
                D3D11HwndVideoPresenterStatus.Failed,
                ex.HResult,
                DeviceRemovedReason: null,
                ex.Message);
            return false;
        }
    }

    private bool IsCurrentMediaFoundationDecoderGeneration(
        long decoderGeneration)
    {
        lock (_h264DecoderLock)
        {
            return decoderGeneration != 0 &&
                _mediaFoundationH264Decoder is not null &&
                _activeMediaFoundationDecoderGeneration ==
                    decoderGeneration;
        }
    }

    private bool TryGetCurrentMediaFoundationDecoderGeneration(
        MediaFoundationD3D11H264Decoder decoder,
        out long decoderGeneration)
    {
        lock (_h264DecoderLock)
        {
            if (!ReferenceEquals(
                    decoder,
                    _mediaFoundationH264Decoder) ||
                _activeMediaFoundationDecoderGeneration ==
                    0)
            {
                decoderGeneration = 0;
                return false;
            }

            decoderGeneration =
                _activeMediaFoundationDecoderGeneration;
            return true;
        }
    }

    internal static bool IsNonFatalPresentationResult(
        D3D11HwndVideoPresenterStatus status) =>
        status is
            D3D11HwndVideoPresenterStatus.Presented or
            D3D11HwndVideoPresenterStatus.Occluded or
            D3D11HwndVideoPresenterStatus.SkippedMinimized;

    internal static bool IsNonFatalRenderThreadPresentationResult(
        D3D11HwndVideoPresenterStatus status,
        long expectedHandleGeneration,
        long currentHandleGeneration,
        bool presentationTargetReady)
    {
        if (IsNonFatalPresentationResult(status))
        {
            return true;
        }

        // Handle destruction disposes the old presenter. If that races an
        // in-flight worker submission, discard only this frame and let the
        // next recovery frame rebuild against the new HWND. Device loss and
        // ordinary presenter failures remain fatal and take the tested
        // software fallback path.
        return status ==
                D3D11HwndVideoPresenterStatus.Disposed &&
            (!presentationTargetReady ||
                expectedHandleGeneration !=
                    currentHandleGeneration);
    }

    private DirectPresentationQualificationDecision
        QualifyDirectPresentation(
            D3D11HwndVideoPresenterStatus status,
            long decoderGeneration)
    {
        if (Volatile.Read(
                ref _directPresentationQualified) != 0)
        {
            return DirectPresentationQualificationDecision
                .Qualified;
        }

        if (status !=
            D3D11HwndVideoPresenterStatus.Presented)
        {
            return DirectPresentationQualificationDecision
                .Pending;
        }

        Interlocked.Increment(
            ref _directPresentationQualificationAttemptCount);
        D3D11HwndVideoValidationResult validation;
        lock (_d3d11PresenterLock)
        {
            validation =
                _d3d11VideoPresenterDecoderGeneration ==
                    decoderGeneration &&
                _d3d11VideoPresenter is not null
                    ? _d3d11VideoPresenter
                        .ValidatePresentedFrame()
                    : D3D11HwndVideoValidationResult
                        .NotAttempted;
        }
        if (!validation.Attempted || !validation.IsValid)
        {
            Interlocked.Increment(
                ref _directHardwarePresentationFailureCount);
            Interlocked.Increment(
                ref _directPresentationQualificationFailureCount);
            RecordDirectHardwareFailure(
                validation.Detail);
            DeactivateD3D11PresentationFromRenderThread();
            DisableMediaFoundationDirectPath();
            DiagnosticLog.Append(
                "VIEWER",
                "MF/D3D11 直显像素资格检查失败，当前恢复帧将转入 " +
                    "ffmpeg 软件 H.264：" + validation.Detail);
            OnUi(() => SetStatus(
                "D3D11 直显未产生有效像素，已切换软件 H.264" +
                    $"（{TrimDiagnosticDetail(validation.Detail)}）",
                DangerTextColor));
            return DirectPresentationQualificationDecision
                .FallbackToSoftware;
        }

        int count = Interlocked.Increment(
            ref _directPresentationQualificationCount);
        if (!ShouldActivateDirectPresentation(
                count,
                DirectPresentationQualificationFrames))
        {
            return DirectPresentationQualificationDecision
                .Pending;
        }

        if (Interlocked.CompareExchange(
                ref _directPresentationQualified,
                1,
                0) == 0)
        {
            Interlocked.Increment(
                ref _directPresentationQualificationSuccessCount);
            DiagnosticLog.Append(
                "VIEWER",
                "MF/D3D11 直显已通过像素资格检查：" +
                    validation.Detail);
            OnUiSynchronous(ActivateD3D11Presentation);
        }

        return DirectPresentationQualificationDecision
            .Qualified;
    }

    internal static bool ShouldActivateDirectPresentation(
        int validatedPresentedFrames,
        int requiredFrames =
            DirectPresentationQualificationFrames)
    {
        if (requiredFrames <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requiredFrames));
        }

        return validatedPresentedFrames >= requiredFrames;
    }

    private void ResetDirectPresentationQualification()
    {
        Interlocked.Exchange(
            ref _directPresentationQualified,
            0);
        Interlocked.Exchange(
            ref _directPresentationQualificationCount,
            0);
    }

    private void DeactivateD3D11PresentationFromRenderThread()
    {
        // Disable painting before the disposed swap chain can be exposed by a
        // queued UI callback. UI-owned controls are updated synchronously;
        // presenter disposal itself is serialized by its own lock.
        OnUiSynchronous(
            () => DeactivateD3D11Presentation(
                disposePresenter: true));
    }

    private void HandleDirectPresentationFailure(
        D3D11HwndVideoPresenterResult result,
        long? decoderGeneration = null)
    {
        if (decoderGeneration.HasValue &&
            !IsCurrentMediaFoundationDecoderGeneration(
                decoderGeneration.Value))
        {
            return;
        }

        DeactivateD3D11Presentation(
            disposePresenter: true);
        DisableMediaFoundationDirectPath();
        PrepareForH264DecoderRecovery();
        RequestH264KeyFrameIfDue();
        string detail =
            TrimDiagnosticDetail(result.Detail);
        SetStatus(
            $"D3D11 直显失效，已切换软件解码并请求关键帧" +
                (string.IsNullOrWhiteSpace(detail)
                    ? string.Empty
                    : $"（{detail}）"),
            DangerTextColor);
    }

    private void ActivateD3D11Presentation()
    {
        if (!_pictureBox.DirectPresentationActive)
        {
            _pictureBox.DirectPresentationActive = true;
        }

        if (_pictureBox.Image is not null)
        {
            _pictureBox.Image = null;
        }

        Image? oldImage = _currentImage;
        _currentImage = null;
        oldImage?.Dispose();
    }

    private void DeactivateD3D11Presentation(
        bool disposePresenter)
    {
        _pictureBox.DirectPresentationActive = false;
        if (disposePresenter)
        {
            DisposeD3D11VideoPresenter();
        }
    }

    private void PictureBox_ClientSizeChanged(
        object? sender,
        EventArgs args)
    {
        Size clientSize =
            CachePictureBoxClientSize();
        UpdatePictureBoxDisplayMode(
            GetRemoteImageSize());
        if (IsD3DClientAreaReady(
                clientSize))
        {
            ResumeDeferredFrameRenderer();
        }

        if (!_pictureBox.DirectPresentationActive)
        {
            return;
        }

        D3D11HwndVideoPresenter? presenter;
        long presenterDecoderGeneration;
        lock (_d3d11PresenterLock)
        {
            presenter = _d3d11VideoPresenter;
            presenterDecoderGeneration =
                _d3d11VideoPresenterDecoderGeneration;
        }

        if (presenter is null)
        {
            return;
        }

        D3D11HwndVideoPresenterResult result =
            presenter.Resize(
                Math.Max(0, _pictureBox.ClientSize.Width),
                Math.Max(0, _pictureBox.ClientSize.Height));
        if (result.Status is
                D3D11HwndVideoPresenterStatus
                    .SkippedMinimized or
            D3D11HwndVideoPresenterStatus.Resized)
        {
            return;
        }

        HandleDirectPresentationFailure(
            result,
            presenterDecoderGeneration);
    }

    private void PictureBox_HandleDestroyed(
        object? sender,
        EventArgs args)
    {
        if (_pictureBox.AllowDrop)
        {
            _ = TrySetFileDropRegistration(
                _pictureBox,
                enabled: false,
                setAllowDrop: null,
                out _);
        }

        Volatile.Write(
            ref _pictureBoxHandleAvailable,
            0);
        Interlocked.Increment(
            ref _pictureBoxHandleGeneration);
        _pictureBox.DirectPresentationActive = false;
        ResetDirectPresentationQualification();
        DisposeD3D11VideoPresenter();
    }

    private void PictureBox_HandleCreated(
        object? sender,
        EventArgs args)
    {
        CachePictureBoxClientSize();
        UpdatePictureBoxFileDropRegistration();
        Volatile.Write(
            ref _pictureBoxHandleAvailable,
            1);
        Interlocked.Increment(
            ref _pictureBoxHandleGeneration);
        ResumeDeferredFrameRenderer();
    }

    private Size CachePictureBoxClientSize()
    {
        Size clientSize =
            _pictureBox.ClientSize;
        Volatile.Write(
            ref _pictureBoxClientWidth,
            clientSize.Width);
        Volatile.Write(
            ref _pictureBoxClientHeight,
            clientSize.Height);
        return clientSize;
    }

    private void DisposeD3D11VideoPresenter()
    {
        lock (_d3d11PresenterLock)
        {
            DisposeD3D11VideoPresenterLocked();
        }
    }

    private void DisposeD3D11VideoPresenterLocked()
    {
        ResetDirectPresentationQualification();
        D3D11HwndVideoPresenter? presenter =
            _d3d11VideoPresenter;
        _d3d11VideoPresenter = null;
        _d3d11VideoPresenterDevicePointer = nint.Zero;
        _d3d11VideoPresenterSourceSize = Size.Empty;
        _d3d11VideoPresenterDecoderGeneration = 0;
        _d3d11VideoPresenterHandleGeneration = 0;
        Volatile.Write(
            ref _d3d11PresenterMatchesCurrentDecoder,
            0);
        presenter?.Dispose();
    }

    private enum DirectPresentationQualificationDecision
    {
        Pending,
        Qualified,
        FallbackToSoftware
    }

    private enum MediaFoundationDirectDecodeDisposition
    {
        NotAttempted,
        FrameReady,
        AwaitOutput,
        WaitForWindow,
        AwaitRecovery,
        FallbackToSoftware
    }

    private readonly record struct
        MediaFoundationDirectDecodeAttempt(
            MediaFoundationDirectDecodeDisposition Disposition,
            MediaFoundationD3D11DecodedFrame? Frame,
            string? FailureDetail,
            long DecoderGeneration = 0,
            RemoteFrameMetadata? SourceFrame = null,
            long DecodeStartedAtTimestamp = 0)
    {
        public static MediaFoundationDirectDecodeAttempt
            NotAttempted =>
                new(
                    MediaFoundationDirectDecodeDisposition
                        .NotAttempted,
                    Frame: null,
                    FailureDetail: null);
    }

    private readonly record struct D3D11PresenterEnsureResult(
        bool Success,
        bool Retryable,
        string Detail)
    {
        public static D3D11PresenterEnsureResult Ready =>
            new(
                Success: true,
                Retryable: false,
                string.Empty);
    }

    internal sealed class DecodedRemoteFrame : IDisposable
    {
        private Bitmap? _bitmap;
        private MediaFoundationD3D11DecodedFrame? _hardwareFrame;
        private int _disposed;

        public DecodedRemoteFrame(
            RemoteFrame frame,
            Bitmap? bitmap,
            MediaFoundationD3D11DecodedFrame? hardwareFrame,
            long hardwareDecoderGeneration,
            double decodeMilliseconds,
            string? decoderBackendName,
            bool decoderUsesHardwareAcceleration,
            long decodeStartedAtTimestamp = 0,
            long decodedAtTimestamp = 0,
            bool alreadyPresented = false,
            long presentedAtTimestamp = 0,
            long presentationGeneration = 0)
            : this(
                RemoteFrameMetadata.FromFrame(frame),
                bitmap,
                hardwareFrame,
                hardwareDecoderGeneration,
                decodeMilliseconds,
                decoderBackendName,
                decoderUsesHardwareAcceleration,
                decodeStartedAtTimestamp,
                decodedAtTimestamp,
                alreadyPresented,
                presentedAtTimestamp,
                presentationGeneration)
        {
        }

        internal DecodedRemoteFrame(
            RemoteFrameMetadata frame,
            Bitmap? bitmap,
            MediaFoundationD3D11DecodedFrame? hardwareFrame,
            long hardwareDecoderGeneration,
            double decodeMilliseconds,
            string? decoderBackendName,
            bool decoderUsesHardwareAcceleration,
            long decodeStartedAtTimestamp = 0,
            long decodedAtTimestamp = 0,
            bool alreadyPresented = false,
            long presentedAtTimestamp = 0,
            long presentationGeneration = 0)
        {
            bool hasExactlyOneDisplayPayload =
                (bitmap is null) !=
                (hardwareFrame is null);
            if ((!alreadyPresented &&
                    !hasExactlyOneDisplayPayload) ||
                (alreadyPresented &&
                    (bitmap is not null ||
                        hardwareFrame is not null ||
                        presentedAtTimestamp <= 0)))
            {
                throw new ArgumentException(
                    alreadyPresented
                        ? "An already-presented frame must contain only " +
                            "presentation metadata."
                        : "Exactly one decoded display payload is required.");
            }

            Frame = frame;
            _bitmap = bitmap;
            _hardwareFrame = hardwareFrame;
            HardwareDecoderGeneration =
                hardwareDecoderGeneration;
            DecodeMilliseconds = decodeMilliseconds;
            DecoderBackendName = decoderBackendName;
            DecoderUsesHardwareAcceleration =
                decoderUsesHardwareAcceleration;
            DecodeStartedAtTimestamp =
                decodeStartedAtTimestamp;
            DecodedAtTimestamp =
                decodedAtTimestamp;
            AlreadyPresented = alreadyPresented;
            PresentationGeneration =
                presentationGeneration;
            _presentedAtTimestamp =
                presentedAtTimestamp;
        }

        public RemoteFrameMetadata Frame { get; }

        public double DecodeMilliseconds { get; }

        public long HardwareDecoderGeneration { get; }

        public string? DecoderBackendName { get; }

        public bool DecoderUsesHardwareAcceleration { get; }

        public long DecodeStartedAtTimestamp { get; }

        public long DecodedAtTimestamp { get; }

        public bool AlreadyPresented { get; }

        public long PresentationGeneration { get; }

        public long PresentedAtTimestamp =>
            Interlocked.Read(
                ref _presentedAtTimestamp);

        private long _presentedAtTimestamp;

        public void MarkPresented(long timestamp)
        {
            if (timestamp <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timestamp));
            }

            Interlocked.CompareExchange(
                ref _presentedAtTimestamp,
                timestamp,
                0);
        }

        public Bitmap? DetachBitmap() =>
            Interlocked.Exchange(
                ref _bitmap,
                null);

        public MediaFoundationD3D11DecodedFrame?
            DetachHardwareFrame() =>
                Interlocked.Exchange(
                    ref _hardwareFrame,
                    null);

        public void Dispose()
        {
            if (Interlocked.Exchange(
                    ref _disposed,
                    1) != 0)
            {
                return;
            }

            Interlocked.Exchange(
                ref _bitmap,
                null)?.Dispose();
            Interlocked.Exchange(
                ref _hardwareFrame,
                null)?.Dispose();
        }
    }

    private sealed record PendingH264Submission(
        RemoteFrameMetadata Frame,
        long DecodeStartedAt);

    private sealed record PendingMediaFoundationInput(
        RemoteFrameMetadata Frame,
        long DecodeStartedAtTimestamp);

    internal readonly record struct ViewerFramePipelineTiming(
        double ReceiveToDecodeMilliseconds,
        double DecodeToPresentMilliseconds,
        double ReceiveToPresentMilliseconds);

    internal static bool TryCalculateViewerFramePipelineTiming(
        RemoteFrame frame,
        long decodeStartedAtTimestamp,
        long decodedAtTimestamp,
        long presentedAtTimestamp,
        out ViewerFramePipelineTiming timing) =>
        TryCalculateViewerFramePipelineTiming(
            RemoteFrameMetadata.FromFrame(frame),
            decodeStartedAtTimestamp,
            decodedAtTimestamp,
            presentedAtTimestamp,
            out timing);

    private static bool TryCalculateViewerFramePipelineTiming(
        RemoteFrameMetadata frame,
        long decodeStartedAtTimestamp,
        long decodedAtTimestamp,
        long presentedAtTimestamp,
        out ViewerFramePipelineTiming timing)
    {
        if (frame.ReceivedAtTimestamp <= 0 ||
            decodeStartedAtTimestamp <
                frame.ReceivedAtTimestamp ||
            decodedAtTimestamp <
                decodeStartedAtTimestamp ||
            presentedAtTimestamp <
                decodedAtTimestamp)
        {
            timing = default;
            return false;
        }

        double receiveToDecodeMilliseconds =
            Stopwatch.GetElapsedTime(
                    frame.ReceivedAtTimestamp,
                    decodeStartedAtTimestamp)
                .TotalMilliseconds;
        double decodeToPresentMilliseconds =
            Stopwatch.GetElapsedTime(
                    decodedAtTimestamp,
                    presentedAtTimestamp)
                .TotalMilliseconds;
        double receiveToPresentMilliseconds =
            Stopwatch.GetElapsedTime(
                    frame.ReceivedAtTimestamp,
                    presentedAtTimestamp)
                .TotalMilliseconds;
        if (!double.IsFinite(
                receiveToDecodeMilliseconds) ||
            !double.IsFinite(
                decodeToPresentMilliseconds) ||
            !double.IsFinite(
                receiveToPresentMilliseconds))
        {
            timing = default;
            return false;
        }

        timing = new(
            receiveToDecodeMilliseconds,
            decodeToPresentMilliseconds,
            receiveToPresentMilliseconds);
        return true;
    }

    private void UpdateFrameStats(DecodedRemoteFrame decodedFrame)
    {
        UpdateFrameStats(
            decodedFrame.Frame,
            decodedFrame.DecodeMilliseconds,
            decodedFrame.DecoderBackendName,
            decodedFrame.DecoderUsesHardwareAcceleration,
            decodedFrame.DecodeStartedAtTimestamp,
            decodedFrame.DecodedAtTimestamp,
            decodedFrame.PresentedAtTimestamp,
            decodedFrame.PresentationGeneration);
    }

    private void UpdateFrameStats(
        RemoteFrameMetadata frame,
        double decodeMilliseconds,
        string? decoderBackendName,
        bool decoderUsesHardwareAcceleration,
        long decodeStartedAtTimestamp,
        long decodedAtTimestamp,
        long presentedAtTimestamp,
        long presentationGeneration)
    {
        if (!CanPresentCaptureGeneration(
                presentationGeneration))
        {
            return;
        }

        string? performanceStatus;
        bool appendDiagnostic;
        lock (_frameStatsLock)
        {
            _framesInWindow++;
            _bytesInWindow += frame.EncodedLength;
            if (IsKnownFrameTiming(frame.CaptureMilliseconds))
            {
                _captureMillisecondsInWindow +=
                    frame.CaptureMilliseconds;
                _captureTimingSamplesInWindow++;
            }

            if (IsKnownFrameTiming(frame.EncodeMilliseconds))
            {
                _encodeMillisecondsInWindow +=
                    frame.EncodeMilliseconds;
                _encodeTimingSamplesInWindow++;
            }

            _decodeMillisecondsInWindow +=
                decodeMilliseconds;
            if (TryCalculateViewerFramePipelineTiming(
                    frame,
                    decodeStartedAtTimestamp,
                    decodedAtTimestamp,
                    presentedAtTimestamp,
                    out ViewerFramePipelineTiming
                        viewerPipelineTiming))
            {
                _receiveToDecodeMillisecondsInWindow +=
                    viewerPipelineTiming
                        .ReceiveToDecodeMilliseconds;
                _decodeToPresentMillisecondsInWindow +=
                    viewerPipelineTiming
                        .DecodeToPresentMilliseconds;
                _receiveToPresentMillisecondsInWindow +=
                    viewerPipelineTiming
                        .ReceiveToPresentMilliseconds;
                _viewerPipelineTimingSamplesInWindow++;
            }

            TimeSpan elapsed =
                Stopwatch.GetElapsedTime(
                    _frameWindowStartedAt);
            if (elapsed < FrameStatsInterval ||
                _framesInWindow <= 0)
            {
                return;
            }

            double elapsedSeconds = elapsed.TotalSeconds;
            double fps =
                _framesInWindow / elapsedSeconds;
            double megabitsPerSecond =
                _bytesInWindow * 8d /
                elapsedSeconds /
                1_000_000d;
            string encodingName =
                FormatDecodedVideoName(
                    frame.Encoding,
                    decoderBackendName,
                    decoderUsesHardwareAcceleration);
            long roundTripMilliseconds =
                Interlocked.Read(
                    ref _roundTripMilliseconds);
            string roundTripText =
                roundTripMilliseconds >= 0
                    ? $" | RTT {roundTripMilliseconds}ms"
                    : string.Empty;
            string mouseInputLatencyText =
                FormatMouseInputAppliedLatency(
                    _client
                        .CollectUdpMouseInputLatencySnapshot());
            string captureTiming =
                FormatFrameTimingAverage(
                    _captureMillisecondsInWindow,
                    _captureTimingSamplesInWindow);
            string encodeTiming =
                FormatFrameTimingAverage(
                    _encodeMillisecondsInWindow,
                    _encodeTimingSamplesInWindow);
            string receiveToDecodeTiming =
                FormatFrameTimingAverage(
                    _receiveToDecodeMillisecondsInWindow,
                    _viewerPipelineTimingSamplesInWindow);
            string decodeToPresentTiming =
                FormatFrameTimingAverage(
                    _decodeToPresentMillisecondsInWindow,
                    _viewerPipelineTimingSamplesInWindow);
            string receiveToPresentTiming =
                FormatFrameTimingAverage(
                    _receiveToPresentMillisecondsInWindow,
                    _viewerPipelineTimingSamplesInWindow);
            string displayScaleText =
                FormatDisplayScale(
                    new Size(
                        frame.Width,
                        frame.Height),
                    new Rectangle(
                        0,
                        0,
                        Volatile.Read(
                            ref _pictureBoxClientWidth),
                        Volatile.Read(
                            ref _pictureBoxClientHeight)),
                    allowUpscaling:
                        Volatile.Read(
                            ref _allowDisplayUpscaling) != 0);
            performanceStatus =
                $"{encodingName} {frame.Width}x{frame.Height}" +
                $"{displayScaleText} | {fps:F1} FPS | " +
                $"采 {captureTiming} | 编 {encodeTiming} | " +
                $"解 {_decodeMillisecondsInWindow / _framesInWindow:F1}ms | " +
                $"收→解 {receiveToDecodeTiming} | " +
                $"解→显 {decodeToPresentTiming} | " +
                $"收→显 {receiveToPresentTiming} | " +
                $"{megabitsPerSecond:F1}Mbps" +
                $"{roundTripText}{mouseInputLatencyText}";
            appendDiagnostic =
                ++_frameStatsReportsSinceDiagnostic >= 5;
            if (appendDiagnostic)
            {
                _frameStatsReportsSinceDiagnostic = 0;
            }

            ResetFrameStatsLocked();
        }

        if (appendDiagnostic)
        {
            DiagnosticLog.Append(
                "VIEWER-PERF",
                performanceStatus);
        }

        OnUi(() =>
        {
            if (CanPresentCaptureGeneration(
                    presentationGeneration))
            {
                SetPerformanceStatus(
                    performanceStatus);
            }
        });
    }

    internal static bool IsKnownFrameTiming(double milliseconds)
    {
        return double.IsFinite(milliseconds) && milliseconds > 0;
    }

    internal static string FormatFrameTimingAverage(
        double totalMilliseconds,
        int sampleCount)
    {
        return sampleCount > 0 && double.IsFinite(totalMilliseconds)
            ? $"{totalMilliseconds / sampleCount:F1}ms"
            : "—";
    }

    internal static string FormatDisplayScale(
        Size frameSize,
        Rectangle clientRectangle,
        bool allowUpscaling = true)
    {
        Rectangle destination =
            CalculateDisplayedImageRectangle(
                clientRectangle,
                frameSize,
                allowUpscaling);
        if (destination.Width <= 0 ||
            destination.Height <= 0 ||
            frameSize.Width <= 0 ||
            frameSize.Height <= 0)
        {
            return string.Empty;
        }

        if (destination.Size == frameSize)
        {
            return " | 显示 1:1";
        }

        double scale = Math.Min(
            destination.Width /
                (double)frameSize.Width,
            destination.Height /
                (double)frameSize.Height);
        string direction =
            scale > 1d ? "放大" : "缩小";
        string clarityHint =
            scale < 0.8d &&
            (long)frameSize.Width * frameSize.Height >=
                3840L * 2160
                ? "（4K 缩小会损失细节，按 F11 全屏）"
                : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $" | 显示 {scale:F2}x {direction}{clarityHint}");
    }

    internal static string FormatMouseInputAppliedLatency(
        LowLatencyMouseInputLatencySnapshot snapshot)
    {
        return snapshot.MatchedLatencySampleCount > 0 &&
            double.IsFinite(
                snapshot.SmoothedRoundTripMilliseconds) &&
            snapshot.SmoothedRoundTripMilliseconds >= 0
                ? $" | 输入落地 {snapshot.SmoothedRoundTripMilliseconds:F1}ms"
                : string.Empty;
    }

    private void PictureBox_MouseDown(object? sender, MouseEventArgs args)
    {
        _pictureBox.Focus();
        if (_remoteDragOutStage != RemoteDragOutStage.None)
        {
            return;
        }

        if (_inputEnabled && _isAndroidRemote && TrySendAndroidMouseShortcut(args.Button))
        {
            return;
        }

        if (_inputEnabled &&
            TryMapToRemotePoint(args.Location, out Point remotePoint) &&
            TryMapMouseButton(args.Button, out RemoteMouseButton button))
        {
            bool mouseDownQueued =
                _remoteInputOwnership.TryQueueMouseDown(
                    button,
                    remotePoint,
                    _client.TryQueueOwnedInput);
            if (mouseDownQueued &&
                CanArmRemoteDragOut(
                    _client.IsConnected,
                    _inputEnabled,
                    _remoteFilePullEnabled,
                    _dragFileTransferInProgress ||
                    _remoteFilePullInProgress ||
                    _client.IsRemoteClipboardFileRequestPending,
                _isAndroidRemote,
                args.Button))
            {
                ArmRemoteDragOut(args.Location, remotePoint);
            }
        }
    }

    private void PictureBox_MouseUp(object? sender, MouseEventArgs args)
    {
        if (args.Button == MouseButtons.Left)
        {
            if (_remoteDragOutStage == RemoteDragOutStage.Armed)
            {
                SendRemoteDragOutMouseUp(args.Location);
                ResetRemoteDragOutState(cancelTransfer: false);
                return;
            }

            if (_remoteDragOutStage is RemoteDragOutStage.Pulling or RemoteDragOutStage.LocalDragging)
            {
                _remoteDragOutMouseReleased = true;
                ReleaseRemoteDragOutMouseCapture();
                return;
            }
        }

        if (_inputEnabled &&
            _isAndroidRemote &&
            IsAndroidMouseShortcut(args.Button))
        {
            return;
        }

        if (!TryMapMouseButton(
                args.Button,
                out RemoteMouseButton button))
        {
            return;
        }

        Point? remotePoint =
            _inputEnabled &&
            TryMapToRemotePoint(
                args.Location,
                out Point mappedRemotePoint)
                ? mappedRemotePoint
                : null;
        _remoteInputOwnership.TryQueueMouseUp(
            button,
            remotePoint,
            _client.InputConnectionGeneration,
            _client.TryQueueOwnedInput);
    }

    private void PictureBox_MouseMove(object? sender, MouseEventArgs args)
    {
        if (_remoteDragOutStage == RemoteDragOutStage.Armed)
        {
            bool leftButtonDown = args.Button.HasFlag(MouseButtons.Left) ||
                Control.MouseButtons.HasFlag(MouseButtons.Left);
            if (!leftButtonDown)
            {
                SendRemoteDragOutMouseUp(args.Location);
                ResetRemoteDragOutState(cancelTransfer: false);
                return;
            }

            if (TryMapToRemotePoint(args.Location, out Point armedRemotePoint))
            {
                _remoteDragOutLastRemotePoint = armedRemotePoint;
            }

            if (_remoteDragOutPressPicturePoint is { } pressPoint &&
                ShouldBeginRemoteDragOut(
                    pressPoint,
                    args.Location,
                    GetViewerClientRectangleInPictureCoordinates(),
                    SystemInformation.DragSize,
                    GetRemoteDragOutEdgeExitTolerance(_pictureBox.DeviceDpi),
                    leftButtonDown))
            {
                _ = StartRemoteDragOutAsync();
                return;
            }

            if (_remoteDragOutLastRemotePoint is { } armedLastPoint)
            {
                TrySendRemoteMouseMove(armedLastPoint);
            }

            return;
        }

        if (_remoteDragOutStage is RemoteDragOutStage.Pulling or RemoteDragOutStage.LocalDragging)
        {
            return;
        }

        if (!_inputEnabled || !_client.IsConnected || !TryMapToRemotePoint(args.Location, out Point remotePoint))
        {
            return;
        }

        TrySendRemoteMouseMove(remotePoint);
    }

    private void PictureBox_MouseCaptureChanged(object? sender, EventArgs args)
    {
        switch (GetRemoteDragOutCaptureLossAction(
            _remoteDragOutStage,
            _pictureBox.Capture,
            Control.MouseButtons.HasFlag(MouseButtons.Left)))
        {
            case RemoteDragOutCaptureLossAction.ReleaseRemoteButton:
                // WinForms can release capture as part of processing WM_LBUTTONUP before it raises
                // MouseUp. Balance the remote button without injecting Escape for an ordinary click.
                SendRemoteDragOutMouseUp(picturePoint: null);
                ResetRemoteDragOutState(cancelTransfer: false);
                break;
            case RemoteDragOutCaptureLossAction.CancelRemoteGesture:
                // Losing capture means this control can no longer observe the matching mouse-up.
                // Cancel the remote OLE gesture even when the physical button is still held.
                CancelRemoteDragOut(status: null, cancelTransfer: false);
                break;
            case RemoteDragOutCaptureLossAction.MarkMouseReleased:
                // Continue receiving the files, but do not start a local OLE drag from a gesture
                // now owned by another window/control.
                _remoteDragOutMouseReleased = true;
                break;
        }
    }

    private void ArmRemoteDragOut(Point picturePoint, Point remotePoint)
    {
        _remoteDragOutStage = RemoteDragOutStage.Armed;
        _remoteDragOutPressPicturePoint = picturePoint;
        _remoteDragOutLastRemotePoint = remotePoint;
        _remoteDragOutMouseReleased = false;
        if (!_pictureBox.IsDisposed)
        {
            _pictureBox.Capture = true;
        }
    }

    private async Task StartRemoteDragOutAsync()
    {
        if (_remoteDragOutStage != RemoteDragOutStage.Armed ||
            _remoteDragOutLastRemotePoint is not { } remotePoint ||
            !CanArmRemoteDragOut(
                _client.IsConnected,
                _inputEnabled,
                _remoteFilePullEnabled,
                _dragFileTransferInProgress ||
                    _remoteFilePullInProgress ||
                    _client.IsRemoteClipboardFileRequestPending,
                _isAndroidRemote,
                MouseButtons.Left) ||
            !Control.MouseButtons.HasFlag(MouseButtons.Left))
        {
            if (_remoteDragOutStage == RemoteDragOutStage.Armed)
            {
                CancelRemoteDragOut(status: null, cancelTransfer: false);
            }

            return;
        }

        // A delayed automatic pull from an earlier Ctrl+C must not race this explicit drag batch.
        CancelPendingClipboardPull();
        var owner = new CancellationTokenSource();
        long operationGeneration = Interlocked.Increment(ref _remoteDragOutOperationGeneration);
        _remoteDragOutCancellation = owner;
        _remoteDragOutStage = RemoteDragOutStage.Pulling;
        _remoteDragOutMouseReleased = !Control.MouseButtons.HasFlag(MouseButtons.Left);
        _remoteFilePullStatusStage = RemoteFilePullStatusStage.Waiting;
        SetRemoteFilePullPending(true);
        if (!_pictureBox.IsDisposed)
        {
            _pictureBox.Cursor = Cursors.AppStarting;
        }

        try
        {
            SetStatus("正在取消远端拖动并取回所选文件，请继续按住鼠标…", MutedTextColor);
            QueueRemoteDragOutCancel(remotePoint);

            await _client.FlushInputAsync(owner.Token);

            await Task.Delay(RemoteDragOutCancelSettleDelayMs, owner.Token);
            await _client.SendInputsAsync(CreateRemoteDragOutCopyCommands());

            await _client.FlushInputAsync(owner.Token);

            SetStatus("正在复制并取回远端所选文件，请继续按住鼠标…", MutedTextColor);
            int clipboardSettleDelay = GetClipboardSynchronizationDelayMs(
                _client.SupportsRemoteClipboardSequenceTracking,
                RemoteDragOutClipboardSettleDelayMs);
            if (clipboardSettleDelay > 0)
            {
                await Task.Delay(clipboardSettleDelay, owner.Token);
            }

            ReturnedClipboardFileBatchResult result =
                await _client.RequestRemoteClipboardFilesForDragOutAsync(owner.Token);
            if (!IsCurrentRemoteDragOut(owner, operationGeneration))
            {
                return;
            }

            string[] localPaths = GetUsableRemoteDragOutPaths(result.LocalPaths);
            if (!result.Success || localPaths.Length == 0)
            {
                string failureStatus = result.Busy
                    ? "另一个远端文件回传正在进行，请稍后重新拖出。"
                    : result.Success
                        ? "远端文件已接收，但本机接收文件已不存在，无法继续拖放。"
                        : string.IsNullOrWhiteSpace(result.Message)
                            ? "没有取回可拖放到本机的远端文件。"
                            : result.Message;
                SetStatus(
                    failureStatus,
                    result.Cancelled || result.Busy ? MutedTextColor : DangerTextColor);
                return;
            }

            if (!ShouldStartRemoteDragOutFileDrop(
                IsCurrentRemoteDragOut(owner, operationGeneration),
                _remoteDragOutMouseReleased,
                Control.MouseButtons.HasFlag(MouseButtons.Left),
                localPaths.Length))
            {
                SetStatus(
                    FormatReleasedRemoteDragOutStatus(localPaths.Length, result.ClipboardUpdated),
                    result.ClipboardUpdated ? SuccessTextColor : MutedTextColor);
                return;
            }

            _remoteDragOutStage = RemoteDragOutStage.LocalDragging;
            ReleaseRemoteDragOutMouseCapture();
            SetRemoteFilePullPending(false);
            SetStatus($"已取回 {localPaths.Length} 个文件，请拖到本机目标位置后松开鼠标。", SuccessTextColor);
            DragDropEffects effect;
            try
            {
                var dataObject = CreateRemoteDragOutDataObject(localPaths);
                effect = _pictureBox.DoDragDrop(dataObject, DragDropEffects.Copy);
            }
            catch (Exception ex) when (IsRemoteDragOutOleException(ex))
            {
                if (IsCurrentRemoteDragOut(owner, operationGeneration))
                {
                    SetStatus(
                        FormatRemoteDragOutOleFailureStatus(
                            localPaths.Length,
                            result.ClipboardUpdated,
                            ex.Message),
                        DangerTextColor);
                }

                return;
            }

            if (!IsCurrentRemoteDragOut(owner, operationGeneration))
            {
                return;
            }

            SetStatus(
                FormatRemoteDragOutCompletionStatus(
                    localPaths.Length,
                    effect,
                    result.ClipboardUpdated),
                effect.HasFlag(DragDropEffects.Copy) ? SuccessTextColor : MutedTextColor);
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentRemoteDragOut(owner, operationGeneration) && !_isClosing)
            {
                SetStatus("已取消从远程画面拖出文件。", MutedTextColor);
            }
        }
        catch (Exception ex) when (ex is IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            ObjectDisposedException or
            ExternalException or
            ThreadStateException or
            TimeoutException or
            ArgumentException or
            NotSupportedException or
            System.ComponentModel.Win32Exception)
        {
            if (IsCurrentRemoteDragOut(owner, operationGeneration) && !_isClosing && !IsDisposed)
            {
                SetStatus($"从远程画面拖出文件失败：{ex.Message}", DangerTextColor);
            }
        }
        finally
        {
            if (ReferenceEquals(_remoteDragOutCancellation, owner))
            {
                ResetRemoteDragOutState(cancelTransfer: false);
                if (!_client.IsRemoteClipboardFileRequestPending)
                {
                    SetRemoteFilePullPending(false);
                }
            }

            owner.Dispose();
        }
    }

    private bool IsCurrentRemoteDragOut(
        CancellationTokenSource owner,
        long operationGeneration)
    {
        return IsRemoteDragOutOperationCurrent(
            Interlocked.Read(ref _remoteDragOutOperationGeneration),
            operationGeneration,
            ReferenceEquals(_remoteDragOutCancellation, owner),
            owner.IsCancellationRequested,
            _remoteDragOutStage,
            _isClosing,
            IsDisposed);
    }

    private void CancelRemoteDragOut(string? status, bool cancelTransfer)
    {
        if (_remoteDragOutStage == RemoteDragOutStage.Armed &&
            _remoteDragOutLastRemotePoint is { } remotePoint)
        {
            QueueRemoteDragOutCancel(remotePoint);
        }

        ResetRemoteDragOutState(cancelTransfer);
        if (!string.IsNullOrWhiteSpace(status) && !_isClosing && !IsDisposed)
        {
            SetStatus(status, MutedTextColor);
        }

        if (!_client.IsRemoteClipboardFileRequestPending)
        {
            SetRemoteFilePullPending(false);
        }
    }

    private void ResetRemoteDragOutState(bool cancelTransfer)
    {
        Interlocked.Increment(ref _remoteDragOutOperationGeneration);
        CancellationTokenSource? cancellation = _remoteDragOutCancellation;
        _remoteDragOutCancellation = null;
        _remoteDragOutStage = RemoteDragOutStage.None;
        _remoteDragOutPressPicturePoint = null;
        _remoteDragOutLastRemotePoint = null;
        _remoteDragOutMouseReleased = false;
        if (cancelTransfer && cancellation is not null)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        ReleaseRemoteDragOutMouseCapture();
        if (!_pictureBox.IsDisposed)
        {
            _pictureBox.Cursor = _inputEnabled ? Cursors.Default : Cursors.No;
        }
    }

    private void ReleaseRemoteDragOutMouseCapture()
    {
        if (!_pictureBox.IsDisposed && _pictureBox.Capture)
        {
            _pictureBox.Capture = false;
        }
    }

    private void SendRemoteDragOutMouseUp(Point? picturePoint)
    {
        Point? remotePoint = picturePoint is { } point &&
            TryMapToRemotePoint(point, out Point mappedPoint)
                ? mappedPoint
                : _remoteDragOutLastRemotePoint;
        if (remotePoint is { } releasePoint)
        {
            _remoteInputOwnership.TryQueueMouseUp(
                RemoteMouseButton.Left,
                releasePoint,
                _client.InputConnectionGeneration,
                _client.TryQueueOwnedInput);
        }
    }

    private Rectangle GetViewerClientRectangleInPictureCoordinates()
    {
        Rectangle screenBounds = RectangleToScreen(ClientRectangle);
        return new Rectangle(_pictureBox.PointToClient(screenBounds.Location), screenBounds.Size);
    }

    internal static bool CanArmRemoteDragOut(
        bool connected,
        bool inputEnabled,
        bool remoteFilePullEnabled,
        bool transferInProgress,
        bool isAndroidRemote,
        MouseButtons button)
    {
        return connected &&
            inputEnabled &&
            remoteFilePullEnabled &&
            !transferInProgress &&
            !isAndroidRemote &&
            button == MouseButtons.Left;
    }

    internal static bool ShouldBeginRemoteDragOut(
        Point pressPoint,
        Point currentPoint,
        Rectangle viewerBounds,
        Size dragSize,
        int edgeExitTolerancePixels,
        bool leftButtonDown)
    {
        if (!leftButtonDown ||
            viewerBounds.Width <= 0 ||
            viewerBounds.Height <= 0 ||
            !viewerBounds.Contains(pressPoint))
        {
            return false;
        }

        long horizontalThreshold = Math.Max(1L, dragSize.Width / 2L);
        long verticalThreshold = Math.Max(1L, dragSize.Height / 2L);
        bool exceededDragThreshold = Math.Abs((long)currentPoint.X - pressPoint.X) >= horizontalThreshold ||
            Math.Abs((long)currentPoint.Y - pressPoint.Y) >= verticalThreshold;
        if (!exceededDragThreshold)
        {
            return false;
        }

        long tolerance = Math.Max(0L, edgeExitTolerancePixels);
        long left = viewerBounds.Left;
        long top = viewerBounds.Top;
        long right = left + viewerBounds.Width - 1L;
        long bottom = top + viewerBounds.Height - 1L;
        return currentPoint.X < left - tolerance ||
            currentPoint.X > right + tolerance ||
            currentPoint.Y < top - tolerance ||
            currentPoint.Y > bottom + tolerance;
    }

    internal static bool ShouldCancelRemoteDragOutWithEscape(
        Keys keyData,
        RemoteDragOutStage stage)
    {
        return keyData == Keys.Escape &&
            stage is RemoteDragOutStage.Armed or RemoteDragOutStage.Pulling;
    }

    internal static RemoteDragOutCaptureLossAction GetRemoteDragOutCaptureLossAction(
        RemoteDragOutStage stage,
        bool pictureHasCapture,
        bool leftButtonDown)
    {
        if (pictureHasCapture)
        {
            return RemoteDragOutCaptureLossAction.None;
        }

        return stage switch
        {
            RemoteDragOutStage.Armed when leftButtonDown => RemoteDragOutCaptureLossAction.CancelRemoteGesture,
            RemoteDragOutStage.Armed => RemoteDragOutCaptureLossAction.ReleaseRemoteButton,
            RemoteDragOutStage.Pulling => RemoteDragOutCaptureLossAction.MarkMouseReleased,
            _ => RemoteDragOutCaptureLossAction.None
        };
    }

    internal static bool ShouldShowRemoteFilePullPending(
        bool clientRequestPending,
        RemoteDragOutStage stage)
    {
        return clientRequestPending || stage == RemoteDragOutStage.Pulling;
    }

    internal static bool IsRemoteDragOutOperationCurrent(
        long currentGeneration,
        long operationGeneration,
        bool ownerMatches,
        bool cancellationRequested,
        RemoteDragOutStage stage,
        bool isClosing,
        bool isDisposed)
    {
        return currentGeneration == operationGeneration &&
            ownerMatches &&
            !cancellationRequested &&
            (stage is RemoteDragOutStage.Pulling or RemoteDragOutStage.LocalDragging) &&
            !isClosing &&
            !isDisposed;
    }

    internal static int GetRemoteDragOutEdgeExitTolerance(int dpi)
    {
        return ResponsiveWindowLayout.ScaleLogical(RemoteDragOutEdgeExitPixels, dpi);
    }

    internal static int GetClipboardSynchronizationDelayMs(
        bool supportsRemoteSequenceTracking,
        int compatibilityDelayMs)
    {
        return supportsRemoteSequenceTracking ? 0 : Math.Max(0, compatibilityDelayMs);
    }

    internal static bool ShouldStartRemoteDragOutFileDrop(
        bool isCurrentOperation,
        bool mouseReleased,
        bool leftButtonDown,
        int localPathCount)
    {
        return isCurrentOperation &&
            !mouseReleased &&
            leftButtonDown &&
            localPathCount > 0;
    }

    internal static string FormatReleasedRemoteDragOutStatus(int localPathCount, bool clipboardUpdated)
    {
        int count = Math.Max(0, localPathCount);
        return clipboardUpdated
            ? $"已取回 {count} 个文件并放入本机剪贴板；鼠标已松开，可在资源管理器按 Ctrl+V。"
            : $"已取回 {count} 个文件并保存到接收目录；鼠标已松开，本机剪贴板暂不可用。";
    }

    internal static string FormatRemoteDragOutCompletionStatus(
        int localPathCount,
        DragDropEffects effect,
        bool clipboardUpdated)
    {
        int count = Math.Max(0, localPathCount);
        if (effect.HasFlag(DragDropEffects.Copy))
        {
            return $"已将 {count} 个远端文件拖放到本机目标位置。";
        }

        return clipboardUpdated
            ? $"未完成本机拖放；已取回 {count} 个文件并放入本机剪贴板，可在资源管理器按 Ctrl+V。"
            : $"未完成本机拖放；已取回 {count} 个文件并保存在接收目录，本机剪贴板暂不可用。";
    }

    internal static string FormatRemoteDragOutOleFailureStatus(
        int localPathCount,
        bool clipboardUpdated,
        string? errorMessage)
    {
        int count = Math.Max(0, localPathCount);
        string reason = string.IsNullOrWhiteSpace(errorMessage)
            ? "本机拖放服务暂不可用"
            : errorMessage.Trim();
        return clipboardUpdated
            ? $"无法启动本机拖放：{reason}；已取回 {count} 个文件并放入本机剪贴板，可按 Ctrl+V。"
            : $"无法启动本机拖放：{reason}；已取回 {count} 个文件并保存在接收目录。";
    }

    internal static bool IsRemoteDragOutOleException(Exception exception)
    {
        return exception is InvalidOperationException or
            ExternalException or
            ThreadStateException or
            ArgumentException or
            NotSupportedException or
            System.ComponentModel.Win32Exception;
    }

    internal static RemoteInputCommand[] CreateRemoteDragOutCancelCommands(Point remotePoint)
    {
        return
        [
            RemoteInputCommand.KeyDown((int)Keys.Escape),
            RemoteInputCommand.KeyUp((int)Keys.Escape),
            RemoteInputCommand.MouseUp(RemoteMouseButton.Left, remotePoint.X, remotePoint.Y)
        ];
    }

    private void QueueRemoteDragOutCancel(
        Point remotePoint)
    {
        _ = _client.SendInputAsync(
            RemoteInputCommand.KeyDown(
                (int)Keys.Escape));
        _ = _client.SendInputAsync(
            RemoteInputCommand.KeyUp(
                (int)Keys.Escape));
        _remoteInputOwnership.TryQueueMouseUp(
            RemoteMouseButton.Left,
            remotePoint,
            _client.InputConnectionGeneration,
            _client.TryQueueOwnedInput);
    }

    internal static RemoteInputCommand[] CreateRemoteDragOutCopyCommands()
    {
        return
        [
            RemoteInputCommand.KeyDown((int)Keys.ControlKey),
            RemoteInputCommand.KeyDown((int)Keys.C),
            RemoteInputCommand.KeyUp((int)Keys.C),
            RemoteInputCommand.KeyUp((int)Keys.ControlKey)
        ];
    }

    internal static string[] GetUsableRemoteDragOutPaths(IEnumerable<string?> paths)
    {
        return ClipboardTextService.NormalizeFileDropPaths(paths)
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .ToArray();
    }

    internal static DataObject CreateRemoteDragOutDataObject(IEnumerable<string> paths)
    {
        DataObject dataObject = ClipboardTextService.CreateFileDropDataObject(paths);
        dataObject.SetData(RemoteDragOutDataFormat, autoConvert: false, data: true);
        return dataObject;
    }

    internal static bool IsRemoteDragOutData(IDataObject? data)
    {
        try
        {
            return data?.GetDataPresent(RemoteDragOutDataFormat, autoConvert: false) == true;
        }
        catch (Exception ex) when (IsRemoteDragOutOleException(ex))
        {
            return false;
        }
    }

    private void PictureBox_MouseWheel(object? sender, MouseEventArgs args)
    {
        if (_remoteDragOutStage != RemoteDragOutStage.None)
        {
            return;
        }

        if (_inputEnabled && TryMapToRemotePoint(args.Location, out Point remotePoint))
        {
            RemoteInputCommand command = _isAndroidRemote && ModifierKeys.HasFlag(Keys.Control)
                ? RemoteInputCommand.PinchZoom(args.Delta, remotePoint.X, remotePoint.Y)
                : RemoteInputCommand.MouseWheel(args.Delta, remotePoint.X, remotePoint.Y);
            _ = _client.SendInputAsync(command);
        }
    }

    private void RemoteViewerFileDragEnter(object? sender, DragEventArgs args)
    {
        _activeFileDragData = null;
        _activeFileDragHasFiles = null;
        _latestFileDragRemotePoint = null;
        args.Effect = UpdateFileDragState(args, forceStatus: true);
    }

    private void RemoteViewerFileDragOver(object? sender, DragEventArgs args)
    {
        args.Effect = UpdateFileDragState(args, forceStatus: false);
    }

    private async void RemoteViewerFileDragDrop(object? sender, DragEventArgs args)
    {
        args.Effect = UpdateFileDragState(args, forceStatus: true);
        if (args.Effect != DragDropEffects.Copy)
        {
            ResetFileDragState();
            return;
        }

        string[] files = GetFileDropPaths(args.Data);
        if (files.Length == 0)
        {
            SetStatus("拖放内容没有可发送的文件", MutedTextColor);
            ResetFileDragState();
            return;
        }

        Point screenPoint = new(args.X, args.Y);
        Point? remotePoint = TryGetRemoteDropPoint(screenPoint, out Point mappedPoint)
            ? mappedPoint
            : _latestFileDragRemotePoint;
        ResetFileDragState();

        await DropFilesToRemoteAsync(files, remotePoint);
    }

    private void RemoteViewerFileDragLeave(object? sender, EventArgs args)
    {
        ResetFileDragState();
    }

    private void PictureBox_KeyDown(object? sender, KeyEventArgs args)
    {
        if (!_client.IsConnected)
        {
            return;
        }

        if (_remoteDragOutStage is RemoteDragOutStage.Pulling or RemoteDragOutStage.LocalDragging)
        {
            args.SuppressKeyPress = true;
            return;
        }

        if (_isAndroidRemote && IsLocalImeKey(args.KeyCode, _pictureBox.IsImeComposing))
        {
            // The local IME owns pre-edit keys, candidate selection and Enter.
            // Only its committed result is sent to the phone.
            return;
        }

        if (_isAndroidRemote && IsPasteShortcut(args))
        {
            args.SuppressKeyPress = true;
            _ = PasteClipboardToRemoteAsync(args);
            return;
        }

        if (!_inputEnabled)
        {
            return;
        }

        string clipboardPullReason = string.Empty;
        bool shouldPullClipboard = (_clipboardTextEnabled || _remoteFilePullEnabled) &&
            TryGetClipboardPullReason(args, out clipboardPullReason);

        if (_isAndroidRemote && args.KeyCode == Keys.Enter)
        {
            _ = _client.SendInputAsync(RemoteInputCommand.TextInput('\n'));
            args.SuppressKeyPress = true;
            if (shouldPullClipboard)
            {
                ScheduleRemoteClipboardPull(clipboardPullReason);
            }

            return;
        }

        if (_isAndroidRemote && ShouldSendAsTextInput(args))
        {
            if (shouldPullClipboard)
            {
                ScheduleRemoteClipboardPull(clipboardPullReason);
            }

            return;
        }

        SendRemoteKeyDown(args.KeyCode);
        args.SuppressKeyPress = true;
        if (shouldPullClipboard)
        {
            ScheduleRemoteClipboardPull(clipboardPullReason);
        }
    }

    private void PictureBox_KeyPress(object? sender, KeyPressEventArgs args)
    {
        if (_remoteDragOutStage is RemoteDragOutStage.Pulling or RemoteDragOutStage.LocalDragging)
        {
            args.Handled = true;
            return;
        }

        if (!_isAndroidRemote ||
            !_inputEnabled ||
            !_client.IsConnected)
        {
            return;
        }

        if (!char.IsControl(args.KeyChar))
        {
            _pictureBox.CommitCharacter(args.KeyChar);
            args.Handled = true;
        }
    }

    private void PictureBox_KeyUp(object? sender, KeyEventArgs args)
    {
        if (_suppressFullScreenShortcutKeyUp &&
            args.KeyCode == Keys.F11)
        {
            _suppressFullScreenShortcutKeyUp = false;
            args.SuppressKeyPress = true;
            return;
        }

        if (_remoteDragOutStage is RemoteDragOutStage.Pulling or RemoteDragOutStage.LocalDragging)
        {
            args.SuppressKeyPress = true;
            return;
        }

        if (_suppressRemoteFilePullShortcutKeyUp && args.KeyCode == Keys.R)
        {
            _suppressRemoteFilePullShortcutKeyUp = false;
            args.SuppressKeyPress = true;
            return;
        }

        if (!_inputEnabled || !_client.IsConnected)
        {
            return;
        }

        if (_isAndroidRemote && IsLocalImeKey(args.KeyCode, _pictureBox.IsImeComposing))
        {
            return;
        }

        if (_isAndroidRemote && args.KeyCode == Keys.Enter)
        {
            args.SuppressKeyPress = true;
            return;
        }

        if (_isAndroidRemote && ShouldSendAsTextInput(args))
        {
            return;
        }

        SendRemoteKeyUp(args.KeyCode);
        args.SuppressKeyPress = true;
    }

    private void EnsureRemoteModifiersDown(Keys keyData)
    {
        if ((keyData & Keys.Control) == Keys.Control)
        {
            EnsureRemoteKeyDown(Keys.ControlKey);
        }

        if ((keyData & Keys.Shift) == Keys.Shift)
        {
            EnsureRemoteKeyDown(Keys.ShiftKey);
        }

        if ((keyData & Keys.Alt) == Keys.Alt)
        {
            EnsureRemoteKeyDown(Keys.Menu);
        }
    }

    private void EnsureRemoteKeyDown(Keys keyCode)
    {
        int virtualKey = (int)(keyCode & Keys.KeyCode);
        if (!_remoteInputOwnership.AnyPressedKey(
                _client.InputConnectionGeneration,
                key =>
                    AreEquivalentModifierVirtualKeys(
                        key.VirtualKey,
                        virtualKey)))
        {
            SendRemoteKeyDown(keyCode);
        }
    }

    internal static bool
        AreEquivalentModifierVirtualKeys(
            int left,
            int right)
    {
        if (left == right)
        {
            return true;
        }

        return
            IsControlVirtualKey(left) &&
                IsControlVirtualKey(right) ||
            IsShiftVirtualKey(left) &&
                IsShiftVirtualKey(right) ||
            IsAltVirtualKey(left) &&
                IsAltVirtualKey(right);
    }

    private void SendRemoteKeyDown(Keys keyCode)
    {
        int virtualKey =
            (int)(keyCode & Keys.KeyCode);
        if (virtualKey is <= 0 or > 0xFE)
        {
            return;
        }

        QueueRemotePhysicalKey(
            RemoteInputCommand.KeyDown(virtualKey));
    }

    private void SendRemoteKeyUp(Keys keyCode)
    {
        int virtualKey =
            (int)(keyCode & Keys.KeyCode);
        if (virtualKey is <= 0 or > 0xFE)
        {
            return;
        }

        QueueRemotePhysicalKey(
            RemoteInputCommand.KeyUp(virtualKey));
    }

    private void QueueRemotePhysicalKey(
        RemoteInputCommand command)
    {
        _remoteInputOwnership.TryQueueKey(
            command,
            _client.InputConnectionGeneration,
            _client.TryQueueOwnedInput,
            _client.TryQueueOwnedInput);
    }

    private void ReleaseAllRemoteInputs()
    {
        _remoteInputOwnership.ReleaseAll(
            _client.InputConnectionGeneration,
            _client.TryQueueOwnedInput);
    }

    private static bool ShouldSendAsTextInput(KeyEventArgs args)
    {
        if (args.Control || args.Alt)
        {
            return false;
        }

        return args.KeyCode switch
        {
            >= Keys.A and <= Keys.Z => true,
            >= Keys.D0 and <= Keys.D9 => true,
            >= Keys.NumPad0 and <= Keys.NumPad9 => true,
            Keys.Space => true,
            Keys.Oem1 or Keys.Oem2 or Keys.Oem3 or Keys.Oem4 or Keys.Oem5 or Keys.Oem6 or Keys.Oem7 or Keys.Oem8 => true,
            Keys.OemBackslash or Keys.OemClear or Keys.Oemcomma or Keys.OemMinus => true,
            Keys.OemPeriod or Keys.Oemplus => true,
            _ => false
        };
    }

    internal static bool IsLocalImeKey(Keys key, bool composing) =>
        composing || key is Keys.ProcessKey or Keys.Packet or
            Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or
            Keys.IMEConvert or Keys.IMENonconvert or Keys.IMEModeChange;

    private void SendCommittedAndroidText(string text, long generation)
    {
        if (!_isAndroidRemote || !_inputEnabled || !_client.IsConnected ||
            !_pictureBox.ContainsFocus || generation != _client.InputConnectionGeneration ||
            _remoteDragOutStage is RemoteDragOutStage.Pulling or RemoteDragOutStage.LocalDragging)
        {
            return;
        }

        RemoteTextInputResult result = _client.SendTextInput(text);
        if (result.Truncated)
        {
            SetStatus("输入文字过长或发送队列已满，请分段输入。", MutedTextColor);
        }
    }

    private async Task DropFilesToRemoteAsync(IReadOnlyList<string> files, Point? remotePoint)
    {
        long fileGeneration = _client.InputConnectionGeneration;
        if (_dragFileTransferInProgress)
        {
            return;
        }

        if (!_filePasteEnabled)
        {
            OnUi(() => SetStatus("远程设备未声明文件接收能力，无法拖放文件", MutedTextColor));
            return;
        }

        _dragFileTransferInProgress = true;
        bool pasteAtRemoteDropTarget = _fileDropPasteEnabled && !_isAndroidRemote && _inputEnabled && remotePoint.HasValue;
        bool remoteTargetPasteRequested = false;
        try
        {
            OnUi(() => SetStatus("正在读取拖放文件信息...", MutedTextColor));
            FileTransferConfirmationPreview preview = await CreateOutgoingFileTransferPreviewAsync(
                files,
                pasteAtRemoteDropTarget);
            if (!ConfirmOutgoingFileTransfer(
                fileGeneration,
                preview,
                "确认拖放文件",
                pasteAtRemoteDropTarget
                    ? "即将发送以下拖放文件，并请求被控端当前位置粘贴。"
                    : "即将发送以下拖放文件到被控端。"))
            {
                OnUi(() => SetStatus("已取消拖放文件传输", MutedTextColor));
                return;
            }

            if (_inputEnabled && remotePoint is { } point)
            {
                await FocusRemoteDropTargetAsync(point, pasteAtRemoteDropTarget);
                EnsureFileTargetCurrent(fileGeneration);
            }

            OnUi(() => SetStatus(
                pasteAtRemoteDropTarget
                    ? "正在发送拖放文件，完成后会请求远端当前位置粘贴..."
                    : "正在发送拖放文件到被控端接收目录...",
                MutedTextColor));
            RemoteFilePasteResult result;
            if (pasteAtRemoteDropTarget)
            {
                RemoteFileDropPasteResult dropPasteResult = await _client.SendFilePastePlanToRemoteDropPasteAsync(preview.Plan);
                result = dropPasteResult.TransferResult;
                remoteTargetPasteRequested = dropPasteResult.RemotePasteRequested;
            }
            else
            {
                result = await _client.SendFilePastePlanToRemoteAsync(preview.Plan);
            }

            string status = FormatDroppedFileTransferStatus(
                result,
                MaxClipboardFilePasteCount,
                remoteTargetPasteRequested);
            Color color = result.FailedFiles > 0
                ? DangerTextColor
                : result.SentFiles > 0
                    ? SuccessTextColor
                    : MutedTextColor;
            OnUi(() => SetStatus(status, color));
            if (result.FailedFiles > 0) ShowFileTransferFailure(result.FailureMessage ?? status);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
        {
            ShowFileTransferFailure(ex.Message);
        }
        finally
        {
            _dragFileTransferInProgress = false;
        }
    }

    private async Task FocusRemoteDropTargetAsync(Point point, bool clickToFocus)
    {
        if (ShouldRefreshRemoteDropPoint(
            point,
            _lastMouseMovePoint,
            Environment.TickCount64,
            _lastMouseMoveAt,
            RemoteDropMouseMoveFreshMs))
        {
            await _client.SendInputAsync(RemoteInputCommand.MouseMove(point.X, point.Y));
            _lastMouseMoveAt = Environment.TickCount64;
            _lastMouseMovePoint = point;
        }

        if (!clickToFocus)
        {
            return;
        }

        await _client.SendInputAsync(RemoteInputCommand.MouseDown(RemoteMouseButton.Left, point.X, point.Y));
        await _client.SendInputAsync(RemoteInputCommand.MouseUp(RemoteMouseButton.Left, point.X, point.Y));
        await Task.Delay(RemoteDropFocusDelayMs);
    }

    private async Task PasteClipboardToRemoteAsync(KeyEventArgs shortcutArgs)
    {
        long clipboardGeneration = _client.InputConnectionGeneration;
        try
        {
            IReadOnlyList<string> clipboardFiles = await ClipboardTextService.GetFileDropListAsync();
            if (_client.InputConnectionGeneration != clipboardGeneration) return;
            if (clipboardFiles.Count > 0)
            {
                await PasteClipboardFilesToRemoteAsync(clipboardFiles);
                return;
            }

            string text = await ClipboardTextService.GetTextAsync();
            if (_client.InputConnectionGeneration != clipboardGeneration) return;
            if (string.IsNullOrEmpty(text))
            {
                OnUi(() => SetStatus("本机剪贴板没有可输入的文本", MutedTextColor));
                return;
            }

            bool clipboardSynced = false;
            Exception? clipboardSyncError = null;
            if (_clipboardTextEnabled)
            {
                try
                {
                    clipboardSynced = await _client.SendClipboardTextToRemoteAsync(
                        text,
                        "已同步本机文本剪贴板到远程。");
                }
                catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException)
                {
                    clipboardSyncError = ex;
                }
            }

            if (_client.InputConnectionGeneration != clipboardGeneration) return;
            if (_clipboardTextEnabled && !clipboardSynced)
            {
                OnUi(() => SetStatus("远端未确认写入剪贴板，未触发粘贴；请检查连接后重试", DangerTextColor));
                return;
            }

            if (!_inputEnabled)
            {
                string syncOnlyStatus = clipboardSynced
                    ? "已同步本机文本剪贴板到远程"
                    : clipboardSyncError is not null
                        ? $"同步文本剪贴板失败：{clipboardSyncError.Message}"
                        : "远程设备未声明输入控制或文本剪贴板能力，无法粘贴文本";
                Color color = clipboardSynced
                    ? SuccessTextColor
                    : clipboardSyncError is not null
                        ? DangerTextColor
                        : MutedTextColor;
                OnUi(() => SetStatus(syncOnlyStatus, color));
                return;
            }

            if (clipboardSynced && (!_isAndroidRemote || _client.SupportsRemoteClipboardPasteShortcut) && TryCreateRemotePasteTriggerCommandSequence(
                shortcutArgs,
                out RemoteInputCommand[] pasteCommands))
            {
                await _client.SendInputsAsync(
                    pasteCommands);

                OnUi(() => SetStatus("已同步文本剪贴板并触发远程粘贴", SuccessTextColor));
                return;
            }

            RemoteTextInputResult result = _client.SendTextInput(text);
            if (result.SentCodePoints <= 0)
            {
                string noInputStatus = clipboardSynced
                    ? "已同步文本剪贴板到远程"
                    : clipboardSyncError is not null
                        ? $"同步文本剪贴板失败：{clipboardSyncError.Message}"
                        : "剪贴板文本没有可输入字符";
                Color color = clipboardSynced
                    ? SuccessTextColor
                    : clipboardSyncError is not null
                        ? DangerTextColor
                        : MutedTextColor;
                OnUi(() => SetStatus(noInputStatus, color));
                return;
            }

            string prefix = clipboardSynced ? "已同步文本剪贴板并输入" : "已输入剪贴板文本";
            string textInputStatus = result.Truncated
                ? $"{prefix}前 {result.SentCodePoints} 个字符，长文本已截断"
                : $"{prefix} {result.SentCodePoints} 个字符";
            if (!clipboardSynced && clipboardSyncError is not null)
            {
                textInputStatus += $"；远端剪贴板未同步：{clipboardSyncError.Message}";
            }

            OnUi(() => SetStatus(textInputStatus, SuccessTextColor));
        }
        catch (Exception ex) when (ex is TimeoutException or ExternalException or InvalidOperationException)
        {
            OnUi(() => SetStatus($"读取本机剪贴板失败：{ex.Message}", DangerTextColor));
        }
    }

    private async Task PasteClipboardFilesToRemoteAsync(IReadOnlyList<string> clipboardFiles)
    {
        long fileGeneration = _client.InputConnectionGeneration;
        if (!_filePasteEnabled)
        {
            OnUi(() => SetStatus("远程设备未声明文件接收能力，无法粘贴文件", MutedTextColor));
            return;
        }

        try
        {
            bool pasteAtRemoteTarget = _fileDropPasteEnabled && !_isAndroidRemote && _inputEnabled;
            OnUi(() => SetStatus("正在读取剪贴板文件信息...", MutedTextColor));
            FileTransferConfirmationPreview preview = await CreateOutgoingFileTransferPreviewAsync(
                clipboardFiles,
                pasteAtRemoteTarget);
            if (!ConfirmOutgoingFileTransfer(
                fileGeneration,
                preview,
                "确认粘贴文件",
                pasteAtRemoteTarget
                    ? "即将把本机剪贴板文件发送到被控端，并请求远端当前位置粘贴。"
                    : "即将把本机剪贴板文件发送到被控端。"))
            {
                OnUi(() => SetStatus("已取消剪贴板文件传输", MutedTextColor));
                return;
            }

            OnUi(() => SetStatus(
                pasteAtRemoteTarget
                    ? "正在发送剪贴板文件，完成后会请求远端当前位置粘贴..."
                    : "正在发送剪贴板文件...",
                MutedTextColor));
            bool remoteTargetPasteRequested = false;
            RemoteFilePasteResult result;
            if (pasteAtRemoteTarget)
            {
                RemoteFileDropPasteResult dropPasteResult = await _client.SendFilePastePlanToRemoteDropPasteAsync(preview.Plan);
                result = dropPasteResult.TransferResult;
                remoteTargetPasteRequested = dropPasteResult.RemotePasteRequested;
            }
            else
            {
                result = await _client.SendFilePastePlanToRemoteAsync(preview.Plan);
            }

            string status = FormatClipboardFilePasteStatus(
                result,
                MaxClipboardFilePasteCount,
                remoteTargetPasteRequested);
            Color color = result.FailedFiles > 0
                ? DangerTextColor
                : result.SentFiles > 0
                    ? SuccessTextColor
                    : MutedTextColor;
            OnUi(() => SetStatus(status, color));
            if (result.FailedFiles > 0) ShowFileTransferFailure(result.FailureMessage ?? status);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
        {
            ShowFileTransferFailure(ex.Message);
        }
    }

    private static Task<FileTransferConfirmationPreview> CreateOutgoingFileTransferPreviewAsync(
        IReadOnlyList<string> paths,
        bool pasteAtRemoteDropTarget)
    {
        return Task.Run(() =>
        {
            RemoteFilePastePlan plan = RemoteViewerClient.CreateFilePastePlan(
                paths,
                MaxClipboardFilePasteCount,
                File.Exists,
                Directory.Exists,
                includeDirectories: true);
            return FileTransferConfirmation.CreatePreview(
                plan,
                (_item, transferName) => pasteAtRemoteDropTarget
                    ? FileTransferConfirmation.FormatRemoteDropPasteDestination(transferName)
                    : FileTransferConfirmation.FormatRemoteReceiveDestination(transferName));
        });
    }

    private bool ConfirmOutgoingFileTransfer(
        long expectedGeneration,
        FileTransferConfirmationPreview preview,
        string title,
        string actionText)
    {
        EnsureFileTargetCurrent(expectedGeneration);
        if (preview.Plan.Files.Count == 0)
        {
            OnUi(() => SetStatus("没有可传输的文件", MutedTextColor));
            return false;
        }

        bool confirmed = FileTransferConfirmation.Confirm(
            this,
            title,
            actionText,
            preview.Items,
            preview.Note);
        EnsureFileTargetCurrent(expectedGeneration);
        return confirmed;
    }

    private void EnsureFileTargetCurrent(long generation)
    {
        if (!_client.IsConnected || _client.InputConnectionGeneration != generation)
            throw new IOException("连接已改变，原文件确认已失效，请在当前连接重新选择文件。");
    }

    private void ShowFileTransferFailure(string message)
    {
        OnUi(() => {
            if (IsDisposed || Disposing) return;
            SetStatus(message, DangerTextColor);
            MessageBox.Show(this, message, "文件传输未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        });
    }

    internal static string FormatClipboardFilePasteStatus(
        RemoteFilePasteResult result,
        int maxFiles,
        bool requestedRemoteTargetPaste = false)
    {
        var parts = new List<string>();
        if (result.SentFiles > 0)
        {
            parts.Add(requestedRemoteTargetPaste
                ? $"已从剪贴板发送 {result.SentFiles} 个文件，并请求远端当前位置粘贴"
                : $"已从剪贴板发送 {result.SentFiles} 个文件");
        }
        else if (result.SkippedDirectories > 0 && result.SkippedMissing == 0 && result.FailedFiles == 0)
        {
            parts.Add("剪贴板文件夹当前不可访问或未能打包");
        }
        else if (result.SkippedMissing > 0 && result.FailedFiles == 0)
        {
            parts.Add("剪贴板文件不存在或不可访问");
        }
        else if (result.FailedFiles > 0)
        {
            parts.Add("剪贴板文件发送失败");
        }
        else
        {
            parts.Add("剪贴板没有可发送的文件");
        }

        if (result.FailedFiles > 0)
        {
            parts.Add($"{result.FailedFiles} 个文件失败");
        }

        if (result.ArchivedDirectories > 0)
        {
            parts.Add($"已打包 {result.ArchivedDirectories} 个文件夹为 zip");
        }

        if (result.SkippedDirectories > 0 && result.SentFiles > 0)
        {
            parts.Add($"跳过 {result.SkippedDirectories} 个文件夹");
        }

        if (result.SkippedMissing > 0 && result.SentFiles > 0)
        {
            parts.Add($"跳过 {result.SkippedMissing} 个不可访问项");
        }

        if (result.Truncated)
        {
            parts.Add($"一次最多粘贴 {Math.Max(0, maxFiles)} 个文件");
        }

        return string.Join("，", parts);
    }

    internal static string FormatDroppedFileTransferStatus(
        RemoteFilePasteResult result,
        int maxFiles,
        bool requestedRemoteTargetPaste = false)
    {
        var parts = new List<string>();
        if (result.SentFiles > 0)
        {
            parts.Add(requestedRemoteTargetPaste
                ? $"已拖放发送 {result.SentFiles} 个文件，并请求远端当前位置粘贴"
                : $"已拖放发送 {result.SentFiles} 个文件到被控端接收目录");
        }
        else if (result.SkippedDirectories > 0 && result.SkippedMissing == 0 && result.FailedFiles == 0)
        {
            parts.Add("拖放文件夹当前不可访问或未能打包");
        }
        else if (result.SkippedMissing > 0 && result.FailedFiles == 0)
        {
            parts.Add("拖放文件不存在或不可访问");
        }
        else if (result.FailedFiles > 0)
        {
            parts.Add("拖放文件发送失败");
        }
        else
        {
            parts.Add("拖放内容没有可发送的文件");
        }

        if (result.FailedFiles > 0)
        {
            parts.Add($"{result.FailedFiles} 个文件失败");
        }

        if (result.ArchivedDirectories > 0)
        {
            parts.Add($"已打包 {result.ArchivedDirectories} 个文件夹为 zip");
        }

        if (result.SkippedDirectories > 0 && result.SentFiles > 0)
        {
            parts.Add($"跳过 {result.SkippedDirectories} 个文件夹");
        }

        if (result.SkippedMissing > 0 && result.SentFiles > 0)
        {
            parts.Add($"跳过 {result.SkippedMissing} 个不可访问项");
        }

        if (result.Truncated)
        {
            parts.Add($"一次最多拖放 {Math.Max(0, maxFiles)} 个文件");
        }

        return string.Join("，", parts);
    }

    internal static DragDropEffects GetFileDragDropEffect(
        bool connected,
        bool filePasteEnabled,
        bool transferInProgress,
        bool hasFileDropData)
    {
        return connected && filePasteEnabled && !transferInProgress && hasFileDropData
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    internal static bool ShouldInspectFileDragData(bool connected, bool filePasteEnabled, bool transferInProgress)
    {
        return connected && filePasteEnabled && !transferInProgress;
    }

    private DragDropEffects UpdateFileDragState(DragEventArgs args, bool forceStatus)
    {
        if (_remoteDragOutStage == RemoteDragOutStage.LocalDragging ||
            IsRemoteDragOutData(args.Data))
        {
            return DragDropEffects.None;
        }

        if (!ShouldInspectFileDragData(_client.IsConnected, _filePasteEnabled, _dragFileTransferInProgress))
        {
            return DragDropEffects.None;
        }

        bool hasFileDropData = HasCachedFileDropData(args.Data);
        DragDropEffects effect = GetFileDragDropEffect(
            _client.IsConnected,
            _filePasteEnabled,
            _dragFileTransferInProgress,
            hasFileDropData);
        if (effect != DragDropEffects.Copy)
        {
            return effect;
        }

        Point screenPoint = new(args.X, args.Y);
        Point? remotePoint = TryGetRemoteDropPoint(screenPoint, out Point mappedPoint)
            ? mappedPoint
            : null;
        if (remotePoint is { } point)
        {
            _latestFileDragRemotePoint = point;
            if (_inputEnabled && _fileDropPasteEnabled && !_isAndroidRemote)
            {
                TrySendRemoteMouseMove(point);
            }
        }

        UpdateFileDragStatus(remotePoint ?? _latestFileDragRemotePoint, forceStatus);
        return effect;
    }

    private bool HasCachedFileDropData(IDataObject? data)
    {
        if (data is null)
        {
            _activeFileDragData = null;
            _activeFileDragHasFiles = null;
            return false;
        }

        if (!ReferenceEquals(data, _activeFileDragData))
        {
            _activeFileDragData = data;
            _activeFileDragHasFiles = HasFileDropData(data);
        }

        return _activeFileDragHasFiles == true;
    }

    private void ResetFileDragState()
    {
        _activeFileDragData = null;
        _activeFileDragHasFiles = null;
        _latestFileDragRemotePoint = null;
        _lastFileDragStatusAt = 0;
        _lastFileDragStatusText = string.Empty;
    }

    private void UpdateFileDragStatus(Point? remotePoint, bool force)
    {
        long now = Environment.TickCount64;
        if (!force && now - _lastFileDragStatusAt >= 0 && now - _lastFileDragStatusAt < DragStatusUpdateMinIntervalMs)
        {
            return;
        }

        _lastFileDragStatusAt = now;
        bool canPasteAtTarget = _fileDropPasteEnabled && !_isAndroidRemote && _inputEnabled && remotePoint.HasValue;
        string status = canPasteAtTarget
            ? "松开鼠标即可发送文件，并请求远端当前指向位置粘贴"
            : "松开鼠标即可发送文件到被控端接收目录";
        if (!force && string.Equals(status, _lastFileDragStatusText, StringComparison.Ordinal))
        {
            return;
        }

        _lastFileDragStatusText = status;
        SetStatus(status, MutedTextColor);
    }

    private void TrySendRemoteMouseMove(Point remotePoint)
    {
        if (_remoteInputOwnership.PressedMouseButtonCount > 0)
        {
            _remoteInputOwnership.UpdatePressedMousePosition(
                remotePoint,
                _client.InputConnectionGeneration);
        }

        if (!ShouldSendMouseMove(remotePoint, _lastMouseMovePoint))
        {
            return;
        }

        _lastMouseMoveAt = Environment.TickCount64;
        _lastMouseMovePoint = remotePoint;
        _ = _client.SendInputAsync(RemoteInputCommand.MouseMove(remotePoint.X, remotePoint.Y));
    }

    private bool TryGetRemoteDropPoint(Point screenPoint, out Point remotePoint)
    {
        Point picturePoint = _pictureBox.PointToClient(screenPoint);
        return TryMapToRemotePoint(
            picturePoint,
            GetDragDropEdgeTolerance(_pictureBox.DeviceDpi),
            out remotePoint);
    }

    internal static int GetDragDropEdgeTolerance(int dpi)
    {
        return ResponsiveWindowLayout.ScaleLogical(DragDropEdgeTolerancePixels, dpi);
    }

    private static bool HasFileDropData(IDataObject? data)
    {
        try
        {
            return data?.GetDataPresent(DataFormats.FileDrop, autoConvert: true) == true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    private static string[] GetFileDropPaths(IDataObject? data)
    {
        try
        {
            return data?.GetData(DataFormats.FileDrop, autoConvert: true) is string[] paths
                ? ClipboardTextService.NormalizeFileDropPaths(paths)
                : Array.Empty<string>();
        }
        catch (ExternalException)
        {
            return Array.Empty<string>();
        }
    }

    private static bool IsPasteShortcut(KeyEventArgs args)
    {
        return (args.Control && args.KeyCode == Keys.V) ||
            (args.Shift && args.KeyCode == Keys.Insert);
    }

    internal static bool TryCreateRemotePasteTriggerCommands(
        KeyEventArgs args,
        out RemoteInputCommand keyDown,
        out RemoteInputCommand keyUp)
    {
        Keys key = args.Control && args.KeyCode == Keys.V
            ? Keys.V
            : args.Shift && args.KeyCode == Keys.Insert
                ? Keys.Insert
                : Keys.None;
        if (key == Keys.None)
        {
            keyDown = default;
            keyUp = default;
            return false;
        }

        keyDown = RemoteInputCommand.KeyDown((int)key);
        keyUp = RemoteInputCommand.KeyUp((int)key);
        return true;
    }

    internal static bool TryCreateRemotePasteTriggerCommandSequence(
        KeyEventArgs args,
        out RemoteInputCommand[] commands)
    {
        if (args.Control && args.KeyCode == Keys.V)
        {
            commands =
            [
                RemoteInputCommand.KeyDown((int)Keys.ControlKey),
                RemoteInputCommand.KeyDown((int)Keys.V),
                RemoteInputCommand.KeyUp((int)Keys.V),
                RemoteInputCommand.KeyUp((int)Keys.ControlKey)
            ];
            return true;
        }

        if (args.Shift && args.KeyCode == Keys.Insert)
        {
            commands =
            [
                RemoteInputCommand.KeyDown((int)Keys.ShiftKey),
                RemoteInputCommand.KeyDown((int)Keys.Insert),
                RemoteInputCommand.KeyUp((int)Keys.Insert),
                RemoteInputCommand.KeyUp((int)Keys.ShiftKey)
            ];
            return true;
        }

        commands = [];
        return false;
    }

    internal static bool TryGetClipboardPullReason(KeyEventArgs args, out string reason)
    {
        if ((args.Control && args.KeyCode == Keys.C) ||
            (args.Control && args.KeyCode == Keys.Insert))
        {
            reason = "复制";
            return true;
        }

        if ((args.Control && args.KeyCode == Keys.X) ||
            (args.Shift && args.KeyCode == Keys.Delete))
        {
            reason = "剪切";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    internal static bool ShouldSuppressAutomaticEmptyRemoteFileStatus(
        bool success,
        string message,
        long nowMilliseconds,
        long lastAutomaticRemoteFilePullAt)
    {
        if (success || lastAutomaticRemoteFilePullAt <= 0)
        {
            return false;
        }

        long elapsed = nowMilliseconds - lastAutomaticRemoteFilePullAt;
        return elapsed >= 0 &&
            elapsed <= AutomaticRemoteFilePullStatusSuppressMs &&
            RemoteViewerClient.IsReturnedClipboardFileBatchEmptyStatus(message);
    }

    private void ScheduleRemoteClipboardPull(string reason)
    {
        CancelPendingClipboardPull();
        var cancellation = new CancellationTokenSource();
        lock (_clipboardPullLock)
        {
            _clipboardPullCancellation = cancellation;
        }

        _ = PullRemoteClipboardAfterDelayAsync(cancellation, reason);
    }

    private async Task PullRemoteClipboardAfterDelayAsync(CancellationTokenSource owner, string reason)
    {
        try
        {
            bool supportsSequenceTracking = _client.SupportsRemoteClipboardSequenceTracking;
            int clipboardSettleDelay = GetClipboardSynchronizationDelayMs(
                supportsSequenceTracking,
                ClipboardPullDelayMs);
            if (clipboardSettleDelay > 0)
            {
                await Task.Delay(clipboardSettleDelay, owner.Token);
            }
            else
            {
                // Put the clipboard shortcut on the wire before the control request. The host
                // records its sequence baseline while applying that input and waits for the actual
                // clipboard change instead of relying on a fixed viewer-side delay.
                await _client.FlushInputAsync(owner.Token);
            }

            if (owner.IsCancellationRequested ||
                !_client.IsConnected ||
                _remoteDragOutStage != RemoteDragOutStage.None ||
                (!_clipboardTextEnabled && !_remoteFilePullEnabled))
            {
                return;
            }

            OnUi(() => SetStatus($"正在同步远程{reason}后的剪贴板...", MutedTextColor));
            if (_clipboardTextEnabled)
            {
                await _client.ReadRemoteClipboardAsync(notifyRequest: false);
            }

            // ReadRemoteClipboardAsync is intentionally independent of this delay token. A drag
            // can start while that network write is awaiting completion, so revalidate ownership
            // and drag state before reserving the single file-return request slot.
            if (ShouldRequestAutomaticRemoteClipboardFiles(
                IsCurrentPendingClipboardPull(owner),
                owner.IsCancellationRequested,
                _client.IsConnected,
                _remoteFilePullEnabled,
                _remoteDragOutStage))
            {
                Interlocked.Exchange(ref _lastAutomaticRemoteFilePullAt, Environment.TickCount64);
                await _client.RequestRemoteClipboardFilesAsync(notifyRequest: false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            OnUi(() => SetStatus($"同步远程剪贴板失败：{ex.Message}", DangerTextColor));
        }
        finally
        {
            lock (_clipboardPullLock)
            {
                if (ReferenceEquals(_clipboardPullCancellation, owner))
                {
                    _clipboardPullCancellation = null;
                }
            }

            owner.Dispose();
        }
    }

    private bool IsCurrentPendingClipboardPull(CancellationTokenSource owner)
    {
        lock (_clipboardPullLock)
        {
            return ReferenceEquals(_clipboardPullCancellation, owner);
        }
    }

    internal static bool ShouldRequestAutomaticRemoteClipboardFiles(
        bool ownerMatches,
        bool cancellationRequested,
        bool connected,
        bool remoteFilePullEnabled,
        RemoteDragOutStage dragOutStage)
    {
        return ownerMatches &&
            !cancellationRequested &&
            connected &&
            remoteFilePullEnabled &&
            dragOutStage == RemoteDragOutStage.None;
    }

    private void CancelPendingClipboardPull()
    {
        CancellationTokenSource? cancellation;
        lock (_clipboardPullLock)
        {
            cancellation = _clipboardPullCancellation;
            _clipboardPullCancellation = null;
        }

        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool TrySendAndroidMouseShortcut(MouseButtons button)
    {
        if (!TryCreateAndroidMouseShortcutCommands(button, out RemoteInputCommand keyDown, out RemoteInputCommand keyUp))
        {
            return false;
        }

        _ = _client.SendInputAsync(keyDown);
        _ = _client.SendInputAsync(keyUp);
        return true;
    }

    internal static bool TryCreateAndroidMouseShortcutCommands(
        MouseButtons button,
        out RemoteInputCommand keyDown,
        out RemoteInputCommand keyUp)
    {
        int? virtualKey = button switch
        {
            MouseButtons.Right => (int)Keys.Escape,
            MouseButtons.Middle => (int)Keys.Home,
            _ => null
        };

        if (virtualKey is null)
        {
            keyDown = default;
            keyUp = default;
            return false;
        }

        keyDown = RemoteInputCommand.KeyDown(virtualKey.Value);
        keyUp = RemoteInputCommand.KeyUp(virtualKey.Value);
        return true;
    }

    private static bool IsAndroidMouseShortcut(MouseButtons button)
    {
        return button is MouseButtons.Right or MouseButtons.Middle;
    }

    private bool TryMapToRemotePoint(Point picturePoint, out Point remotePoint)
    {
        return TryMapToRemotePoint(picturePoint, edgeTolerancePixels: 0, out remotePoint);
    }

    private bool TryMapToRemotePoint(Point picturePoint, int edgeTolerancePixels, out Point remotePoint)
    {
        remotePoint = Point.Empty;
        Size remoteImageSize = GetRemoteImageSize();

        if (!_client.IsConnected ||
            Volatile.Read(
                ref _captureTargetAvailable) == 0 ||
            remoteImageSize.Width <= 0 ||
            remoteImageSize.Height <= 0)
        {
            return false;
        }

        Rectangle imageRect =
            GetZoomedImageRectangle(
                remoteImageSize);
        return TryMapZoomedImagePoint(
            picturePoint,
            remoteImageSize,
            imageRect,
            edgeTolerancePixels,
            out remotePoint);
    }

    internal static bool TryMapZoomedImagePoint(
        Point picturePoint,
        Size remoteImageSize,
        Rectangle imageRect,
        int edgeTolerancePixels,
        out Point remotePoint)
    {
        remotePoint = Point.Empty;

        if (remoteImageSize.Width <= 0 ||
            remoteImageSize.Height <= 0 ||
            imageRect.Width <= 0 ||
            imageRect.Height <= 0)
        {
            return false;
        }

        int tolerance = Math.Max(0, edgeTolerancePixels);
        int imageLeft = imageRect.Left;
        int imageTop = imageRect.Top;
        int imageRight = imageRect.Left + imageRect.Width - 1;
        int imageBottom = imageRect.Top + imageRect.Height - 1;
        if (picturePoint.X < imageLeft - tolerance ||
            picturePoint.X > imageRight + tolerance ||
            picturePoint.Y < imageTop - tolerance ||
            picturePoint.Y > imageBottom + tolerance)
        {
            return false;
        }

        int clampedX = Math.Clamp(picturePoint.X, imageLeft, imageRight);
        int clampedY = Math.Clamp(picturePoint.Y, imageTop, imageBottom);
        double xRatio = imageRect.Width == 1
            ? 0
            : (clampedX - imageLeft) / (double)(imageRect.Width - 1);
        double yRatio = imageRect.Height == 1
            ? 0
            : (clampedY - imageTop) / (double)(imageRect.Height - 1);
        int x = Math.Clamp((int)Math.Round(xRatio * (remoteImageSize.Width - 1)), 0, remoteImageSize.Width - 1);
        int y = Math.Clamp((int)Math.Round(yRatio * (remoteImageSize.Height - 1)), 0, remoteImageSize.Height - 1);
        remotePoint = new Point(x, y);
        return true;
    }

    private Rectangle GetZoomedImageRectangle(
        Size remoteImageSize)
    {
        return CalculateDisplayedImageRectangle(
            _pictureBox.ClientRectangle,
            remoteImageSize,
            allowUpscaling:
                Volatile.Read(
                    ref _allowDisplayUpscaling) != 0);
    }

    private Size GetRemoteImageSize()
    {
        long packed = Interlocked.Read(
            ref _remoteImageSizePacked);
        return new Size(
            unchecked((int)(packed >> 32)),
            unchecked((int)packed));
    }

    private static long PackRemoteImageSize(
        int width,
        int height)
    {
        return ((long)(uint)Math.Max(0, width) << 32) |
            (uint)Math.Max(0, height);
    }

    internal static Rectangle CalculateZoomedImageRectangle(Rectangle client, Size imageSize)
    {
        if (imageSize.Width <= 0 || imageSize.Height <= 0 || client.Width <= 0 || client.Height <= 0)
        {
            return Rectangle.Empty;
        }

        double imageRatio = imageSize.Width / (double)imageSize.Height;
        double clientRatio = client.Width / (double)client.Height;

        if (clientRatio > imageRatio)
        {
            int height = client.Height;
            int width = (int)Math.Round(height * imageRatio);
            int left = client.Left + (client.Width - width) / 2;
            return new Rectangle(left, client.Top, width, height);
        }

        int scaledWidth = client.Width;
        int scaledHeight = (int)Math.Round(scaledWidth / imageRatio);
        int top = client.Top + (client.Height - scaledHeight) / 2;
        return new Rectangle(client.Left, top, scaledWidth, scaledHeight);
    }

    internal static Rectangle CalculateDisplayedImageRectangle(
        Rectangle client,
        Size imageSize,
        bool allowUpscaling)
    {
        if (!allowUpscaling &&
            imageSize.Width > 0 &&
            imageSize.Height > 0 &&
            imageSize.Width <= client.Width &&
            imageSize.Height <= client.Height)
        {
            return new Rectangle(
                client.Left +
                    (client.Width - imageSize.Width) / 2,
                client.Top +
                    (client.Height - imageSize.Height) / 2,
                imageSize.Width,
                imageSize.Height);
        }

        return CalculateZoomedImageRectangle(
            client,
            imageSize);
    }

    private static bool TryMapMouseButton(MouseButtons source, out RemoteMouseButton target)
    {
        target = source switch
        {
            MouseButtons.Left => RemoteMouseButton.Left,
            MouseButtons.Right => RemoteMouseButton.Right,
            MouseButtons.Middle => RemoteMouseButton.Middle,
            _ => RemoteMouseButton.None
        };

        return target != RemoteMouseButton.None;
    }

    private static string FormatFrameEncoding(RemoteFrameEncoding encoding)
    {
        return encoding switch
        {
            RemoteFrameEncoding.Jpeg => "JPEG",
            RemoteFrameEncoding.H264AnnexB => "H.264",
            _ => encoding.ToString()
        };
    }

    private static string FormatDecodedVideoName(
        DecodedRemoteFrame decodedFrame)
    {
        return FormatDecodedVideoName(
            decodedFrame.Frame.Encoding,
            decodedFrame.DecoderBackendName,
            decodedFrame.DecoderUsesHardwareAcceleration);
    }

    private static string FormatDecodedVideoName(
        RemoteFrameEncoding encoding,
        string? decoderBackendName,
        bool decoderUsesHardwareAcceleration)
    {
        string encodingName =
            FormatFrameEncoding(encoding);
        if (encoding !=
                RemoteFrameEncoding.H264AnnexB ||
            string.IsNullOrWhiteSpace(
                decoderBackendName))
        {
            return encodingName;
        }

        string acceleration =
            decoderUsesHardwareAcceleration
                ? "硬解"
                : "软件择优";
        return $"{encodingName}/{decoderBackendName}" +
            $"（{acceleration}）";
    }

    private static string FormatDecoderFailure(FfmpegH264Decoder decoder)
    {
        string detail =
            TrimDiagnosticDetail(
                decoder.FailureDetail);
        if (string.IsNullOrWhiteSpace(detail))
        {
            return string.Empty;
        }

        return $"（{detail}）";
    }

    private static string TrimDiagnosticDetail(
        string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return string.Empty;
        }

        string normalized =
            detail.Replace(
                    Environment.NewLine,
                    " ",
                    StringComparison.Ordinal)
                .Trim();
        return normalized.Length > 96
            ? normalized[..96] + "..."
            : normalized;
    }

    private void SetStatus(string text, Color color)
    {
        _statusBar.SetStatus(text, color);
        UpdateStatusToolTip(text);
    }

    private void SetPerformanceStatus(string text)
    {
        _statusBar.SetDetails(text, MutedTextColor);
        UpdateStatusToolTip(
            _statusBar.StatusText);
        UpdateStatusFooterLayout();
    }

    internal static (Rectangle StatusBounds, Rectangle DetailsBounds) CalculateStatusBarLayout(
        Size clientSize,
        Padding padding,
        bool hasDetails,
        int dpi = ResponsiveWindowLayout.DesignDpi)
    {
        Rectangle contentBounds = new(
            padding.Left,
            padding.Top,
            Math.Max(0, clientSize.Width - padding.Horizontal),
            Math.Max(0, clientSize.Height - padding.Vertical));
        Rectangle statusBounds = contentBounds;
        Rectangle detailsBounds = Rectangle.Empty;

        int minimumDetailsWidth = ResponsiveWindowLayout.ScaleLogical(360, dpi);
        int minimumStatusWidth = ResponsiveWindowLayout.ScaleLogical(180, dpi);
        int contentGap = ResponsiveWindowLayout.ScaleLogical(16, dpi);
        int sideBySideMinimumWidth = CalculateStatusBarSideBySideMinimumWidth(dpi);
        if (hasDetails && contentBounds.Width >= sideBySideMinimumWidth)
        {
            int detailsWidth = Math.Min(
                Math.Max(minimumDetailsWidth, contentBounds.Width / 2),
                Math.Max(0, contentBounds.Width - minimumStatusWidth));
            detailsBounds = new Rectangle(
                contentBounds.Right - detailsWidth,
                contentBounds.Top,
                detailsWidth,
                contentBounds.Height);
            statusBounds = new Rectangle(
                contentBounds.Left,
                contentBounds.Top,
                Math.Max(0, contentBounds.Width - detailsWidth - contentGap),
                contentBounds.Height);
        }
        else if (hasDetails)
        {
            int rowGap = Math.Min(
                ResponsiveWindowLayout.ScaleLogical(2, dpi),
                contentBounds.Height);
            int statusHeight = Math.Max(0, (contentBounds.Height - rowGap) / 2);
            int detailsHeight = Math.Max(0, contentBounds.Height - rowGap - statusHeight);
            statusBounds = new Rectangle(
                contentBounds.Left,
                contentBounds.Top,
                contentBounds.Width,
                statusHeight);
            detailsBounds = new Rectangle(
                contentBounds.Left,
                contentBounds.Top + statusHeight + rowGap,
                contentBounds.Width,
                detailsHeight);
        }

        return (statusBounds, detailsBounds);
    }

    internal static int CalculateStatusBarPreferredHeight(
        int clientWidth,
        Padding padding,
        bool hasDetails,
        int dpi = ResponsiveWindowLayout.DesignDpi)
    {
        int contentWidth = Math.Max(0, clientWidth - padding.Horizontal);
        int sideBySideMinimumWidth = CalculateStatusBarSideBySideMinimumWidth(dpi);
        int logicalHeight = hasDetails && contentWidth < sideBySideMinimumWidth ? 52 : 30;
        return ResponsiveWindowLayout.ScaleLogical(logicalHeight, dpi);
    }

    internal static int CalculateStatusFooterPreferredHeight(
        int statusWidth,
        Padding statusPadding,
        bool hasDetails,
        int actionPreferredHeight,
        int dpi = ResponsiveWindowLayout.DesignDpi)
    {
        return Math.Max(
            CalculateStatusBarPreferredHeight(statusWidth, statusPadding, hasDetails, dpi),
            Math.Max(0, actionPreferredHeight));
    }

    internal static int CalculateStatusBarSideBySideMinimumWidth(int dpi)
    {
        return ResponsiveWindowLayout.ScaleLogical(360, dpi) +
            ResponsiveWindowLayout.ScaleLogical(180, dpi) +
            ResponsiveWindowLayout.ScaleLogical(16, dpi);
    }

    internal static bool ShouldSendMouseMove(
        Point remotePoint,
        Point? lastRemotePoint)
    {
        return lastRemotePoint != remotePoint;
    }

    internal static bool ShouldRefreshRemoteDropPoint(
        Point remotePoint,
        Point? lastRemotePoint,
        long nowMilliseconds,
        long lastSentMilliseconds,
        int freshnessMilliseconds)
    {
        if (lastRemotePoint != remotePoint)
        {
            return true;
        }

        long elapsed = nowMilliseconds - lastSentMilliseconds;
        return elapsed < 0 || elapsed > Math.Max(0, freshnessMilliseconds);
    }

    private void OnUi(Action action)
    {
        if (_isClosing || IsDisposed)
        {
            return;
        }

        try
        {
            if (InvokeRequired)
            {
                if (IsHandleCreated)
                {
                    BeginInvoke((Action)(() => OnUi(action)));
                }
            }
            else
            {
                action();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Capture-target control events and TCP frames share the client's single
    /// receive loop. Completing the UI blank synchronously therefore keeps
    /// the first frame of the new generation buffered by TCP until the blank
    /// is committed instead of accepting and then clearing a static IDR. UDP
    /// video is stopped by the host's ordered barrier before this control
    /// bundle is written.
    /// </summary>
    private void OnUiSynchronous(Action action)
    {
        if (_isClosing || IsDisposed)
        {
            return;
        }

        try
        {
            if (InvokeRequired)
            {
                if (IsHandleCreated)
                {
                    Invoke(action);
                }
            }
            else
            {
                action();
            }
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or
                ObjectDisposedException)
        {
        }
    }

    private delegate nint LowLevelKeyboardProcedure(
        int code,
        nint message,
        nint dataPointer);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct LowLevelKeyboardData
    {
        public readonly uint VirtualKey;
        public readonly uint ScanCode;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly nuint ExtraInfo;
    }

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    private static extern nint SetWindowsHookEx(
        int hookId,
        LowLevelKeyboardProcedure procedure,
        nint module,
        uint threadId);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(
        nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(
        nint hook,
        int code,
        nint message,
        nint dataPointer);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern nint GetModuleHandle(
        string? moduleName);

    private static Button CreateStatusActionButton(string text)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(64, 26),
            Padding = new Padding(4, 0, 4, 0),
            Margin = new Padding(0, 0, 2, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(23, 32, 51),
            ForeColor = Color.FromArgb(226, 232, 240),
            Cursor = Cursors.Hand,
            TabStop = false,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(51, 65, 85);
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(30, 41, 59);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(51, 65, 85);
        return button;
    }

    private sealed class ViewerFooterPanel : Panel
    {
        public ViewerFooterPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            using var pen = new Pen(Color.FromArgb(51, 65, 85));
            args.Graphics.DrawLine(
                pen,
                0,
                0,
                ClientSize.Width,
                0);
        }
    }

    private void UpdateRenderedVideoStatus(
        RemoteFrameEncoding encoding,
        string decoderBackend,
        string decodedVideoName)
    {
        if (_lastRenderedEncoding == encoding &&
            string.Equals(
                _lastRenderedDecoderBackend,
                decoderBackend,
                StringComparison.Ordinal))
        {
            return;
        }

        _lastRenderedEncoding = encoding;
        Volatile.Write(
            ref _lastRenderedDecoderBackend,
            decoderBackend);
        SetStatus(
            $"已切换到 {decodedVideoName} 画面流",
            SuccessTextColor);
    }

    private void RecordDirectlyPresentedFrame(
        RemoteFrameMetadata frame,
        double decodeMilliseconds,
        string decoderBackendName,
        bool decoderUsesHardwareAcceleration,
        long decodeStartedAtTimestamp,
        long decodedAtTimestamp,
        long presentedAtTimestamp,
        long presentationGeneration)
    {
        if (_isClosing ||
            IsDisposed ||
            !CanPresentCaptureGeneration(
                presentationGeneration))
        {
            return;
        }

        long frameSignature = PackRemoteImageSize(
            frame.Width,
            frame.Height);
        Interlocked.Exchange(
            ref _remoteImageSizePacked,
            frameSignature);
        if (Interlocked.Exchange(
                ref _directFrameUiSignature,
                frameSignature) != frameSignature)
        {
            string decodedVideoName =
                FormatDecodedVideoName(
                    frame.Encoding,
                    decoderBackendName,
                    decoderUsesHardwareAcceleration);
            OnUi(() =>
            {
                if (CanPresentCaptureGeneration(
                        presentationGeneration))
                {
                    UpdateRenderedVideoStatus(
                        frame.Encoding,
                        decoderBackendName,
                        decodedVideoName);
                }
            });
        }

        UpdateFrameStats(
            frame,
            decodeMilliseconds,
            decoderBackendName,
            decoderUsesHardwareAcceleration,
            decodeStartedAtTimestamp,
            decodedAtTimestamp,
            presentedAtTimestamp,
            presentationGeneration);
    }

    internal sealed class BufferedPictureBox : PictureBox
    {
        private bool _directPresentationActive;
        private bool _localImeEnabled;
        private long _compositionGeneration;
        private readonly RemoteTextInputBuffer _textInput = new();

        public Func<long>? ReadInputGeneration { get; set; }
        public Action<string, long>? TextCommitted { get; set; }
        public bool IsImeComposing { get; private set; }

        // PictureBox normally disables IME even though this subclass is focusable.
        protected override ImeMode DefaultImeMode => ImeMode.NoControl;

        public bool LocalImeEnabled
        {
            get => _localImeEnabled;
            set
            {
                if (_localImeEnabled == value) return;
                _localImeEnabled = value;
                ResetComposition();
                ImeMode = value ? ImeMode.On : ImeMode.Disable;
            }
        }

        public BufferedPictureBox()
        {
            SetStyle(ControlStyles.Selectable, true);
            DoubleBuffered = true;
            ResizeRedraw = true;
            ImeMode = ImeMode.Disable;
        }

        protected override bool IsInputKey(Keys keyData) => true;

        public void CommitCharacter(char character)
        {
            if (!_localImeEnabled || IsImeComposing) return;
            long generation = ReadInputGeneration?.Invoke() ?? 0;
            EmitText(_textInput.Append(character, generation), generation);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            ResetComposition();
            base.OnLostFocus(e);
        }

        private void ResetComposition()
        {
            IsImeComposing = false;
            _compositionGeneration = 0;
            _textInput.Reset();
        }

        private void EmitText(string text, long generation)
        {
            if (_localImeEnabled && text.Length != 0)
            {
                TextCommitted?.Invoke(text, generation);
            }
        }

        protected override void WndProc(ref Message message)
        {
            const int wmImeStartComposition = 0x010D;
            const int wmImeEndComposition = 0x010E;
            const int wmImeComposition = 0x010F;
            const int wmImeChar = 0x0286;
            const long resultString = 0x0800;
            if (_localImeEnabled)
            {
                if (message.Msg == wmImeStartComposition)
                {
                    _textInput.Reset();
                    _compositionGeneration = ReadInputGeneration?.Invoke() ?? 0;
                    IsImeComposing = true;
                }
                else if (message.Msg == wmImeEndComposition)
                {
                    ResetComposition();
                }
                else if (message.Msg == wmImeComposition &&
                    (message.LParam.ToInt64() & resultString) != 0 &&
                    TryReadImeResult(out string text))
                {
                    long generation = IsImeComposing
                        ? _compositionGeneration : ReadInputGeneration?.Invoke() ?? 0;
                    _textInput.Reset();
                    EmitText(text, generation);
                    // DefWindowProc would otherwise emit the same result again
                    // through WM_IME_CHAR / WM_CHAR. Preserve any new pre-edit flags.
                    message.LParam = (nint)(message.LParam.ToInt64() & ~resultString);
                    if (message.LParam == 0)
                    {
                        message.Result = 0;
                        return;
                    }
                }
                else if (message.Msg == wmImeChar)
                {
                    long generation = IsImeComposing
                        ? _compositionGeneration : ReadInputGeneration?.Invoke() ?? 0;
                    EmitText(_textInput.Append((char)message.WParam.ToInt64(), generation), generation);
                    message.Result = 0;
                    return;
                }
            }

            base.WndProc(ref message);
        }

        private bool TryReadImeResult(out string text)
        {
            text = string.Empty;
            nint context = ImmGetContext(Handle);
            if (context == 0) return false;
            try
            {
                const uint resultString = 0x0800;
                int length = ImmGetCompositionStringW(context, resultString, null, 0);
                if (length < 0 || length > 16 * 1024 || (length & 1) != 0) return false;
                if (length == 0) return true;
                byte[] buffer = new byte[length];
                int copied = ImmGetCompositionStringW(context, resultString, buffer, (uint)length);
                if (copied < 0 || copied > length || (copied & 1) != 0) return false;
                text = System.Text.Encoding.Unicode.GetString(buffer, 0, copied);
                return true;
            }
            finally
            {
                _ = ImmReleaseContext(Handle, context);
            }
        }

        [DllImport("imm32.dll")]
        private static extern nint ImmGetContext(nint window);
        [DllImport("imm32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ImmReleaseContext(nint window, nint context);
        [DllImport("imm32.dll", ExactSpelling = true)]
        private static extern int ImmGetCompositionStringW(
            nint context, uint index, [Out] byte[]? buffer, uint length);

        public bool DirectPresentationActive
        {
            get => _directPresentationActive;
            set
            {
                if (_directPresentationActive == value)
                {
                    return;
                }

                _directPresentationActive = value;
                Invalidate();
            }
        }

        protected override void OnPaintBackground(
            PaintEventArgs args)
        {
            if (!_directPresentationActive)
            {
                base.OnPaintBackground(args);
            }
        }

        protected override void OnPaint(
            PaintEventArgs args)
        {
            if (!_directPresentationActive)
            {
                args.Graphics.InterpolationMode =
                    System.Drawing.Drawing2D.InterpolationMode
                        .HighQualityBicubic;
                args.Graphics.PixelOffsetMode =
                    System.Drawing.Drawing2D.PixelOffsetMode
                        .HighQuality;
                base.OnPaint(args);
            }
        }
    }

    private sealed class StatusBarControl : Control
    {
        private bool _updatingAdaptiveHeight;

        public StatusBarControl()
        {
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.Opaque |
                ControlStyles.ResizeRedraw,
                true);
        }

        public string StatusText { get; set; } = string.Empty;

        public Color StatusColor { get; set; } = MutedTextColor;

        public string DetailsText { get; set; } = string.Empty;

        public Color DetailsColor { get; set; } = MutedTextColor;

        public bool ManageOwnHeight { get; set; } = true;

        public bool HasDetails => !string.IsNullOrWhiteSpace(DetailsText);

        public void SetStatus(string text, Color color)
        {
            if (string.Equals(StatusText, text, StringComparison.Ordinal) &&
                StatusColor.ToArgb() == color.ToArgb())
            {
                return;
            }

            StatusText = text;
            StatusColor = color;
            Invalidate(GetCurrentLayout().StatusBounds);
        }

        public void SetDetails(string text, Color color)
        {
            if (string.Equals(DetailsText, text, StringComparison.Ordinal) &&
                DetailsColor.ToArgb() == color.ToArgb())
            {
                return;
            }

            (Rectangle oldStatusBounds, Rectangle oldDetailsBounds) = GetCurrentLayout();
            DetailsText = text;
            DetailsColor = color;
            bool heightChanged = UpdateAdaptiveHeight();
            (Rectangle newStatusBounds, Rectangle newDetailsBounds) = GetCurrentLayout();

            if (heightChanged || oldStatusBounds != newStatusBounds || oldDetailsBounds != newDetailsBounds)
            {
                Invalidate();
                return;
            }

            Invalidate(newDetailsBounds.IsEmpty ? ClientRectangle : newDetailsBounds);
        }

        public bool UpdateAdaptiveHeight()
        {
            if (!ManageOwnHeight || _updatingAdaptiveHeight)
            {
                return false;
            }

            int preferredHeight = CalculateStatusBarPreferredHeight(
                ClientSize.Width,
                Padding,
                !string.IsNullOrWhiteSpace(DetailsText),
                DeviceDpi);
            if (Height == preferredHeight)
            {
                return false;
            }

            _updatingAdaptiveHeight = true;
            try
            {
                Height = preferredHeight;
            }
            finally
            {
                _updatingAdaptiveHeight = false;
            }

            return true;
        }

        protected override void OnResize(EventArgs args)
        {
            base.OnResize(args);
            UpdateAdaptiveHeight();
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            using var background = new SolidBrush(BackColor);
            args.Graphics.FillRectangle(background, ClientRectangle);

            (Rectangle statusBounds, Rectangle detailsBounds) = GetCurrentLayout();

            TextRenderer.DrawText(
                args.Graphics,
                StatusText,
                Font,
                statusBounds,
                StatusColor,
                TextFormatFlags.Left |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPadding);

            if (!detailsBounds.IsEmpty)
            {
                TextRenderer.DrawText(
                    args.Graphics,
                    DetailsText,
                    Font,
                    detailsBounds,
                    DetailsColor,
                    TextFormatFlags.Right |
                    TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPadding);
            }
        }

        private (Rectangle StatusBounds, Rectangle DetailsBounds) GetCurrentLayout()
        {
            return CalculateStatusBarLayout(
                ClientSize,
                Padding,
                !string.IsNullOrWhiteSpace(DetailsText),
                DeviceDpi);
        }
    }
}
