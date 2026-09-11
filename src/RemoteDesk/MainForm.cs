using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace RemoteDesk;

public sealed partial class MainForm : Form
{
    private static readonly TimeSpan ExitWatchdogTimeout = TimeSpan.FromSeconds(6);
    private const int MaxRecentRemoteDevices = 20;
    private const int MaxDiscoveryProbeTargets = MaxRecentRemoteDevices + 1;
    private const int MaxClipboardFilePasteCount = 32;
    private const int WmDisplayChange = 0x007E;
    private const int WmSettingChange = 0x001A;
    private const long SpiSetWorkArea = 0x002F;
    private const string DefaultAccessPassword = "";
    private static readonly Color AppBackColor = Color.FromArgb(243, 246, 251);
    private static readonly Color SurfaceBackColor = Color.White;
    private static readonly Color SurfaceMutedColor = Color.FromArgb(248, 250, 252);
    private static readonly Color PanelBorderColor = Color.FromArgb(219, 228, 239);
    private static readonly Color TextColor = Color.FromArgb(15, 23, 42);
    private static readonly Color MutedTextColor = Color.FromArgb(100, 116, 139);
    private static readonly Color PrimaryColor = Color.FromArgb(37, 99, 235);
    private static readonly Color PrimarySoftColor = Color.FromArgb(239, 246, 255);
    private static readonly Color DangerColor = Color.FromArgb(220, 38, 38);
    private static readonly Color SuccessBackColor = Color.FromArgb(220, 252, 231);
    private static readonly Color SuccessTextColor = Color.FromArgb(22, 101, 52);
    private static readonly Color NeutralBadgeBackColor = Color.FromArgb(226, 232, 240);
    private static readonly Color NeutralBadgeTextColor = Color.FromArgb(71, 85, 105);

    private readonly AppSettingsService _settingsService = new();
    private readonly RemoteDeskSettings _settings;
    private readonly RemoteHostServer _hostServer = new();
    private readonly RemoteViewerClient _viewerClient = new();
    private readonly RelayHostConnector _relayHostConnector = new();
    private readonly WindowsRelayNetworkOptimizer _relayNetworkOptimizer = new();
    private readonly RelayProvisioner _relayProvisioner = new();
    private readonly CancellationTokenSource _relayOperationCancellation = new();
    private long _relayConfigurationGeneration;
    private readonly System.Windows.Forms.Timer _relayRefreshTimer = new()
    {
        Interval = 10_000
    };
    private readonly NetworkDiscoveryResponder _presenceResponder = new();
    private readonly WindowsDiagnosticLog _diagnosticLog = WindowsDiagnosticLog.CreateDefault();
    private readonly object _presenceLock = new();
    private readonly object _viewerReconnectLock = new();
    private readonly object _viewerTelemetryLock = new();
    private readonly ViewerReconnectQualification
        _viewerReconnectQualification = new();
    private readonly ToolTip _toolTip = new()
    {
        AutoPopDelay = 12000,
        InitialDelay = 350,
        ReshowDelay = 100,
        ShowAlways = true
    };

    private TabControl _tabs = null!;
    private SettingsSaveNotice _settingsSaveNotice = null!;
    private string? _lastSettingsSaveError;
    private TextBox _localIpsBox = null!;
    private NumericUpDown _hostPortBox = null!;
    private TextBox _hostPasswordBox = null!;
    private CheckBox _startWithWindowsBox = null!;
    private CheckBox _autoStartHostBox = null!;
    private CheckBox _minimizeToTrayBox = null!;
    private CheckBox _allowRemoteStartBox = null!;
    private ComboBox _captureTargetBox = null!;
    private ComboBox _captureScaleBox = null!;
    private NumericUpDown _hostFpsBox = null!;
    private TrackBar _jpegQualityTrack = null!;
    private Label _jpegQualityLabel = null!;
    private CheckBox _adaptiveQualityBox = null!;
    private Button _hostToggleButton = null!;
    private Button _restartAsAdministratorButton = null!;
    private Button _persistentStartupButton = null!;
    private Button _secureDesktopButton = null!;
    private Label _hostStatusLabel = null!;
    private TextBox _hostLogBox = null!;

    private TextBox _viewerHostBox = null!;
    private NumericUpDown _viewerPortBox = null!;
    private TextBox _viewerPasswordBox = null!;
    private ComboBox _discoveredHostsBox = null!;
    private ComboBox _viewerCaptureTargetBox = null!;
    private ComboBox _viewerVideoModeBox = null!;
    private Button _discoverHostsButton = null!;
    private Button _diagnoseConnectionButton = null!;
    private Button _sendClipboardButton = null!;
    private Button _readClipboardButton = null!;
    private Button _sendFileButton = null!;
    private Button _pasteFilesButton = null!;
    private Button _pullRemoteFilesButton = null!;
    private Button _openReceivedFilesButton = null!;
    private Button _remoteUpdateButton = null!;
    private Button _viewerToggleButton = null!;
    private Label _viewerStatusLabel = null!;
    private ProgressBar _remoteFilePullProgressBar = null!;
    private ListBox _discoveredHostsList = null!;
    private Button _editSavedDeviceRemarkButton = null!;
    private Button _deleteSavedDeviceButton = null!;
    private ContextMenuStrip _savedDeviceMenu = null!;
    private ToolStripMenuItem _editSavedDeviceRemarkMenuItem = null!;
    private ToolStripMenuItem _deleteSavedDeviceMenuItem = null!;
    private TabPage _relayPage = null!;
    private Label _relayServerSummaryLabel = null!;
    private CheckBox _relayRegisterHostBox = null!;
    private CheckBox _relayOptimizeNetworkBox = null!;
    private Label _relayRouteStatusLabel = null!;
    private TextBox _relayViewerPasswordBox = null!;
    private ComboBox _relayVideoModeBox = null!;
    private Button _relayConfigureButton = null!;
    private Button _relayRefreshButton = null!;
    private Button _relayConnectButton = null!;
    private ListView _relayDevicesList = null!;
    private Label _relayStatusLabel = null!;
    private ContextMenuStrip _viewerStatusMenu = null!;
    private NotifyIcon _notifyIcon = null!;
    private ContextMenuStrip _trayMenu = null!;
    private ToolStripMenuItem _trayHostToggleItem = null!;
    private RemoteViewerWindow? _viewerWindow;
    private RemoteViewerWindow? _viewerTelemetryWindow;
    private ViewerRenderTelemetryReport? _lastViewerRenderTelemetry;
    private long _lastAcceptedViewerConnectionGeneration = long.MinValue;
    private long _lastViewerTelemetryIntentGeneration = long.MinValue;
    private long _lastViewerTelemetryConnectionGeneration = long.MinValue;
    private bool _viewerTelemetryFinalized;
    private RemoteDeviceDescriptor? _lastConnectedDeviceInfo;
    private RemoteDeviceCapabilities _connectedViewerCapabilities = DefaultViewerCapabilities;

    private bool _updatingViewerCaptureTargets;
    private bool _updatingDiscoveredHosts;
    private bool _pendingViewerCaptureTargetRestore;
    private IReadOnlyList<DiscoveredHost> _lastDiscoveredHosts = Array.Empty<DiscoveredHost>();
    private CancellationTokenSource? _discoveryScanCancellation;
    private int _presencePort = Protocol.DefaultPort;
    private string _presenceCaptureTarget = string.Empty;
    private string _presencePassword = string.Empty;
    private bool _presenceAllowRemoteStart;
    private bool _applyingSettings;
    private bool _allowExit;
    private readonly bool _startMinimizedToTray;
    private readonly bool _resumeHostAfterUpdate;
    private readonly long _applicationStartedAt;
    private bool _isClosing;
    private bool _pastingClipboardFilesFromMain;
    private bool _pullingRemoteFilesFromMain;
    private bool _viewerActionInProgress;
    private bool _viewerActionDisconnecting;
    private bool _relayOperationInProgress;
    private bool _relayRefreshInProgress;
    private ViewerReconnectIntent? _viewerReconnectIntent;
    private Task? _viewerReconnectTask;
    private bool _viewerReconnectRequested;
    private bool _viewerReconnectConnecting;
    private long _nextViewerReconnectGeneration;
    private PendingViewerConnection? _pendingViewerConnection;
    private long _remoteUpdateRestartExpectedUntil;
    private RemoteFilePullStatusStage _remoteFilePullStatusStage;
    private int _exitWatchdogStarted;
    private bool _responsiveLayoutReady;
    private int _appliedDpi;
    private int _responsiveRefreshPending;
    private string _responsiveScreenDeviceName = string.Empty;
    private Rectangle _responsiveScreenWorkingArea = Rectangle.Empty;

    private sealed class ViewerReconnectIntent : IDisposable
    {
        public ViewerReconnectIntent(
            long generation,
            ViewerConnectionSnapshot connection)
        {
            Generation = generation;
            Connection = connection;
            Cancellation = new CancellationTokenSource();
        }

        public long Generation { get; }

        public ViewerConnectionSnapshot Connection { get; }

        public CancellationTokenSource Cancellation { get; }

        public int FailedAttemptCount { get; set; }

        public long QualifiedConnectionGeneration { get; set; } =
            long.MinValue;

        public bool QualifiedConnectionCountsAsReconnectAttempt
        {
            get;
            set;
        }

        public bool QualifiedConnectionStable { get; set; }

        public long NextReconnectAttemptOrdinal { get; set; }

        public long ActiveReconnectAttemptOrdinal { get; set; }

        public long QualifiedReconnectAttemptOrdinal { get; set; }

        public long LastFailedReconnectAttemptOrdinal { get; set; }

        public void Dispose()
        {
            Cancellation.Dispose();
        }
    }

    private sealed class PendingViewerConnection
    {
        public PendingViewerConnection(
            ViewerConnectionSnapshot connection)
        {
            Connection = connection;
        }

        public ViewerConnectionSnapshot Connection { get; }
    }

    public MainForm(
        bool startMinimizedToTray = false,
        bool resumeHostAfterUpdate = false,
        long applicationStartedAt = 0)
    {
        _applicationStartedAt =
            applicationStartedAt > 0
                ? applicationStartedAt
                : Stopwatch.GetTimestamp();
        long phaseStartedAt = Stopwatch.GetTimestamp();
        _startMinimizedToTray = startMinimizedToTray;
        _resumeHostAfterUpdate = resumeHostAfterUpdate;
        _settings = _settingsService.Load();
        _settings.Relay.DeviceId = RemoteDeviceIdentity.Normalize(_settings.Relay.DeviceId) ?? Guid.NewGuid().ToString("D");
        RemoteDeviceIdentity.LocalId = _settings.Relay.DeviceId;
        long settingsLoadedAt = Stopwatch.GetTimestamp();
        SuspendLayout();
        Text = "RemoteDesk";
        LoadWindowIcon();
        MinimumSize = Size.Empty;
        Size = new Size(1120, 720);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        BackColor = AppBackColor;
        Font = new Font("Microsoft YaHei UI", 9.25F, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(ResponsiveWindowLayout.DesignDpi, ResponsiveWindowLayout.DesignDpi);
        DoubleBuffered = true;

        BuildInterface();
        ApplyDpiMetrics(GetInitialSystemDpi());
        long interfaceBuiltAt = Stopwatch.GetTimestamp();
        ConfigureToolTips();
        InitializeTray();
        WireEvents();
        long chromeConfiguredAt = Stopwatch.GetTimestamp();
        RefreshLocalIps();
        long localIpsRefreshedAt = Stopwatch.GetTimestamp();
        RefreshCaptureTargets();
        long captureTargetsRefreshedAt = Stopwatch.GetTimestamp();
        ApplySettings();
        long settingsAppliedAt = Stopwatch.GetTimestamp();
        UpdateDiscoveryPresence();
        StartPresenceResponder();
        InitializeDeviceRefresh();
        FormClosing += MainForm_FormClosing;
        long servicesStartedAt = Stopwatch.GetTimestamp();
        ResumeLayout(performLayout: false);
        PerformLayout();
        long constructionCompletedAt = Stopwatch.GetTimestamp();
        _diagnosticLog.Append(
            "PERF",
            "主窗口构造耗时 " +
            $"{ElapsedMilliseconds(phaseStartedAt, constructionCompletedAt):F1}ms" +
            "（配置 " +
            $"{ElapsedMilliseconds(phaseStartedAt, settingsLoadedAt):F1}，" +
            "界面 " +
            $"{ElapsedMilliseconds(settingsLoadedAt, interfaceBuiltAt):F1}，" +
            "控件配置 " +
            $"{ElapsedMilliseconds(interfaceBuiltAt, chromeConfiguredAt):F1}，" +
            "本机 IP " +
            $"{ElapsedMilliseconds(chromeConfiguredAt, localIpsRefreshedAt):F1}，" +
            "屏幕枚举 " +
            $"{ElapsedMilliseconds(localIpsRefreshedAt, captureTargetsRefreshedAt):F1}，" +
            "应用配置 " +
            $"{ElapsedMilliseconds(captureTargetsRefreshedAt, settingsAppliedAt):F1}，" +
            "后台服务 " +
            $"{ElapsedMilliseconds(settingsAppliedAt, servicesStartedAt):F1}，" +
            "首次布局 " +
            $"{ElapsedMilliseconds(servicesStartedAt, constructionCompletedAt):F1}ms）。");
        Shown += async (_, _) =>
        {
            _diagnosticLog.Append(
                "PERF",
                "应用窗口已可交互，启动总耗时 " +
                $"{ElapsedMilliseconds(_applicationStartedAt, Stopwatch.GetTimestamp()):F1}ms。");
            if (_startMinimizedToTray && _settings.App.MinimizeToTray)
            {
                HideToTray(showTip: false);
            }

            await StartHostAutomaticallyAsync(
                _resumeHostAfterUpdate);
            await DiscoverHostsAsync(silent: true);
            await RefreshRelayDevicesAsync(silent: true);
            _relayRefreshTimer.Start();
        };
    }

    private static double ElapsedMilliseconds(
        long startedAt,
        long completedAt) =>
        Stopwatch.GetElapsedTime(
                startedAt,
                completedAt)
            .TotalMilliseconds;

    private static int GetInitialSystemDpi()
    {
        try
        {
            uint dpi = GetDpiForSystem();
            return dpi is >= 48 and <= 768
                ? checked((int)dpi)
                : ResponsiveWindowLayout.DesignDpi;
        }
        catch (Exception ex)
            when (ex is DllNotFoundException or
                EntryPointNotFoundException)
        {
            return ResponsiveWindowLayout.DesignDpi;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    private void LoadWindowIcon()
    {
        try
        {
            Icon? associatedIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (associatedIcon is not null)
            {
                Icon = associatedIcon;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or System.ComponentModel.Win32Exception)
        {
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _isClosing = true;
            TryShutdown(() => CancelViewerReconnectIntent());
            TryShutdown(CancelDiscoveryScan);
            TryShutdown(() => _relayOperationCancellation.Cancel());
            TryShutdown(() => _relayRefreshTimer.Stop());
            TryShutdown(SaveSettingsFromUi);
            TryShutdown(() => _notifyIcon.Visible = false);
            TryShutdown(() => _presenceResponder.Dispose());
            TryShutdown(CloseViewerWindowFromDisconnect);
            TryShutdown(() => _relayHostConnector
                .StopAsync().GetAwaiter().GetResult());
            TryShutdown(() => _hostServer.Dispose());
            TryShutdown(() => _relayHostConnector.Dispose());
            TryShutdown(() => _viewerClient.Dispose());
            TryShutdown(() => _relayNetworkOptimizer.Dispose());
            TryShutdown(_diagnosticLog.Flush);
            TryShutdown(() => _savedDeviceMenu.Dispose());
            TryShutdown(() => _secureDesktopButton.ContextMenuStrip?.Dispose());
            TryShutdown(() => _viewerStatusMenu.Dispose());
            TryShutdown(() => _toolTip.Dispose());
            TryShutdown(() => _notifyIcon.Dispose());
            TryShutdown(() => _trayMenu.Dispose());
            TryShutdown(() => _relayRefreshTimer.Dispose());
            TryShutdown(() => _relayOperationCancellation.Dispose());
        }

        base.Dispose(disposing);
    }

    protected override void OnResize(EventArgs args)
    {
        base.OnResize(args);
        if (!_isClosing &&
            _notifyIcon is not null &&
            WindowState == FormWindowState.Minimized &&
            _minimizeToTrayBox is not null &&
            _minimizeToTrayBox.Checked)
        {
            BeginInvoke((Action)(() => HideToTray(showTip: false)));
        }
    }

    protected override void OnHandleCreated(EventArgs args)
    {
        long startedAt = Stopwatch.GetTimestamp();
        base.OnHandleCreated(args);
        WindowsAppInstance.AllowActivation(Handle);
        _diagnosticLog.Append(
            "PERF",
            "主窗口句柄创建耗时 " +
            $"{ElapsedMilliseconds(startedAt, Stopwatch.GetTimestamp()):F1}ms。");
    }

    protected override void OnLoad(EventArgs args)
    {
        long startedAt = Stopwatch.GetTimestamp();
        base.OnLoad(args);
        long baseLoadedAt = Stopwatch.GetTimestamp();
        long dpiAppliedAt;
        long boundsAppliedAt;
        SuspendLayout();
        _tabs.SuspendLayout();
        try
        {
            ApplyDpiMetrics(DeviceDpi);
            dpiAppliedAt = Stopwatch.GetTimestamp();
            ResponsiveWindowLayout.ApplyTo(
                this,
                logicalPreferredSize: new Size(1120, 720),
                logicalMinimumSize: new Size(640, 440),
                applyPreferredBounds: true);
            boundsAppliedAt = Stopwatch.GetTimestamp();
        }
        finally
        {
            _tabs.ResumeLayout(performLayout: false);
            ResumeLayout(performLayout: false);
        }

        _responsiveLayoutReady = true;
        RememberResponsiveScreen();
        _diagnosticLog.Append(
            "PERF",
            "主窗口首次加载耗时 " +
            $"{ElapsedMilliseconds(startedAt, Stopwatch.GetTimestamp()):F1}ms" +
            "（基类/自动缩放 " +
            $"{ElapsedMilliseconds(startedAt, baseLoadedAt):F1}，" +
            "DPI 控件 " +
            $"{ElapsedMilliseconds(baseLoadedAt, dpiAppliedAt):F1}，" +
            "窗口边界 " +
            $"{ElapsedMilliseconds(dpiAppliedAt, boundsAppliedAt):F1}ms）。");
    }

    protected override void OnDpiChanged(DpiChangedEventArgs args)
    {
        base.OnDpiChanged(args);
        ApplyDpiMetrics(args.DeviceDpiNew);
        ResponsiveWindowLayout.ApplyTo(
            this,
            logicalPreferredSize: new Size(1120, 720),
            logicalMinimumSize: new Size(640, 440),
            applyPreferredBounds: false,
            suggestedBounds: args.SuggestedRectangle);
        RememberResponsiveScreen();
    }

    protected override void OnLocationChanged(EventArgs args)
    {
        base.OnLocationChanged(args);
        if (_responsiveLayoutReady && !_isClosing && WindowState == FormWindowState.Normal)
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
        if (WindowsAppInstance.ActivateMessage != 0 && (uint)message.Msg == WindowsAppInstance.ActivateMessage && !_isClosing)
        {
            if (IsHandleCreated) BeginInvoke((Action)ShowFromTray);
            return;
        }
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

        ResponsiveWindowLayout.ApplyMinimumSizeTo(this, new Size(640, 440));
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
        if (!_responsiveLayoutReady || _isClosing || IsDisposed)
        {
            return;
        }

        ResponsiveWindowLayout.ApplyTo(
            this,
            logicalPreferredSize: new Size(1120, 720),
            logicalMinimumSize: new Size(640, 440),
            applyPreferredBounds: false);
        RememberResponsiveScreen();
    }

    private void BuildInterface()
    {
        long phaseStartedAt = Stopwatch.GetTimestamp();
        _tabs = CreateTabs();
        long tabsCreatedAt = Stopwatch.GetTimestamp();
        TabPage hostPage = BuildHostPage();
        long hostPageBuiltAt = Stopwatch.GetTimestamp();
        TabPage viewerPage = BuildViewerPage();
        long viewerPageBuiltAt = Stopwatch.GetTimestamp();
        _relayPage = BuildRelayPage();
        _tabs.TabPages.Add(hostPage);
        _tabs.TabPages.Add(viewerPage);
        _tabs.TabPages.Add(_relayPage);

        var shell = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = AppBackColor,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _settingsSaveNotice = new SettingsSaveNotice();
        _settingsSaveNotice.RetryRequested += (_, _) =>
        {
            SaveSettingsFromUi();
            if (!_settingsSaveNotice.HasPendingChanges && !_isClosing && !IsDisposed)
            {
                SetViewerStatus("设置已成功保存。", SuccessTextColor);
                if (_tabs.SelectedTab == _relayPage)
                {
                    SetRelayStatus("本机中继配置已成功保存。", SuccessTextColor);
                }
            }
        };
        shell.Controls.Add(CreateAppHeader(), 0, 0);
        shell.Controls.Add(_settingsSaveNotice, 0, 1);
        shell.Controls.Add(_tabs, 0, 2);
        Controls.Add(shell);
        long shellBuiltAt = Stopwatch.GetTimestamp();
        _diagnosticLog.Append(
            "PERF",
            "界面创建分段：标签 " +
            $"{ElapsedMilliseconds(phaseStartedAt, tabsCreatedAt):F1}，" +
            "被控页 " +
            $"{ElapsedMilliseconds(tabsCreatedAt, hostPageBuiltAt):F1}，" +
            "连接页 " +
            $"{ElapsedMilliseconds(hostPageBuiltAt, viewerPageBuiltAt):F1}，" +
            "外壳 " +
            $"{ElapsedMilliseconds(viewerPageBuiltAt, shellBuiltAt):F1}ms。");
    }

    private Control CreateAppHeader()
    {
        var header = new BufferedPanel
        {
            Dock = DockStyle.Top,
            Height = 76,
            BackColor = SurfaceBackColor,
            Padding = new Padding(20, 12, 20, 11),
            Margin = new Padding(0)
        };
        header.Paint += (_, args) =>
        {
            using var pen = new Pen(PanelBorderColor);
            args.Graphics.DrawLine(
                pen,
                0,
                Math.Max(0, header.ClientSize.Height - 1),
                header.ClientSize.Width,
                Math.Max(0, header.ClientSize.Height - 1));
        };

        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = SurfaceBackColor,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var mark = new Label
        {
            Text = "RD",
            Dock = DockStyle.Fill,
            BackColor = PrimaryColor,
            ForeColor = Color.White,
            Font = new Font(Font.FontFamily, 11F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(0, 3, 12, 3)
        };
        var copy = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = SurfaceBackColor,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        copy.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        copy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        copy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        copy.Controls.Add(new Label
        {
            Text = "RemoteDesk",
            AutoSize = true,
            ForeColor = TextColor,
            Font = new Font(Font.FontFamily, 15F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 1)
        }, 0, 0);
        copy.Controls.Add(new Label
        {
            Text = "安全连接、远程控制与文件传输",
            AutoSize = true,
            ForeColor = MutedTextColor,
            Margin = new Padding(0)
        }, 0, 1);

        var badge = new Label
        {
            Text = "WINDOWS  ·  直连 / 私有中继",
            AutoSize = true,
            BackColor = PrimarySoftColor,
            ForeColor = Color.FromArgb(30, 64, 175),
            Font = new Font(Font.FontFamily, 8.5F, FontStyle.Bold),
            Padding = new Padding(10, 6, 10, 6),
            Margin = new Padding(12, 8, 0, 0),
            TextAlign = ContentAlignment.MiddleCenter
        };

        layout.Controls.Add(mark, 0, 0);
        layout.Controls.Add(copy, 1, 0);
        layout.Controls.Add(badge, 2, 0);
        header.Controls.Add(layout);
        header.ClientSizeChanged += (_, _) =>
        {
            badge.Visible = header.ClientSize.Width >=
                ResponsiveWindowLayout.ScaleLogical(
                    690,
                    header.DeviceDpi);
        };
        return header;
    }

    private TabControl CreateTabs()
    {
        var tabs = new BufferedTabControl
        {
            Dock = DockStyle.Fill,
            DrawMode = TabDrawMode.OwnerDrawFixed,
            ItemSize = new Size(150, 40),
            Padding = new Point(16, 4),
            SizeMode = TabSizeMode.Fixed
        };

        tabs.DrawItem += (_, args) =>
        {
            TabPage page = tabs.TabPages[args.Index];
            bool selected = args.Index == tabs.SelectedIndex;
            Rectangle bounds = args.Bounds;
            Color backColor = selected ? SurfaceBackColor : AppBackColor;
            Color foreColor = selected ? PrimaryColor : MutedTextColor;

            using var background = new SolidBrush(backColor);
            args.Graphics.FillRectangle(background, bounds);
            TextRenderer.DrawText(
                args.Graphics,
                page.Text,
                Font,
                bounds,
                foreColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if (selected)
            {
                int inset = ResponsiveWindowLayout.ScaleLogical(16, tabs.DeviceDpi);
                int thickness = Math.Max(1, ResponsiveWindowLayout.ScaleLogical(2, tabs.DeviceDpi));
                using var pen = new Pen(PrimaryColor, thickness);
                args.Graphics.DrawLine(
                    pen,
                    bounds.Left + inset,
                    bounds.Bottom - thickness,
                    bounds.Right - inset,
                    bounds.Bottom - thickness);
            }
        };

        return tabs;
    }

    private TabPage BuildHostPage()
    {
        var page = CreatePage("被控端");
        var root = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(20),
            BackColor = AppBackColor
        };

        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var settingsArea = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0, 0, 0, 16)
        };

        settingsArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        settingsArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));

        _localIpsBox = new TextBox
        {
            ReadOnly = true,
            Dock = DockStyle.Fill
        };
        _hostPortBox = CreatePortInput();
        _hostPasswordBox = CreatePasswordInput();
        _hostPasswordBox.PlaceholderText = "访问口令";
        _startWithWindowsBox = new CheckBox
        {
            Text = "开机启动应用到托盘",
            AutoSize = true
        };
        _autoStartHostBox = new CheckBox
        {
            Text = "打开应用时自动启动被控端",
            AutoSize = true,
            Checked = true
        };
        _minimizeToTrayBox = new CheckBox
        {
            Text = "关闭/最小化时收入托盘",
            AutoSize = true,
            Checked = true
        };
        _allowRemoteStartBox = new CheckBox
        {
            Text = "允许同口令远程启动被控端",
            AutoSize = true,
            Checked = false
        };
        _captureTargetBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
            Width = 340
        };
        _captureScaleBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 115
        };
        _captureScaleBox.Items.AddRange(
            new object[]
            {
                "100%（原生）",
                "最高 1440p",
                "75%",
                "50%"
            });
        _captureScaleBox.SelectedIndex = 0;
        _hostFpsBox = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 60,
            Value = 30,
            Width = 90
        };
        _jpegQualityTrack = new TrackBar
        {
            Minimum = 30,
            Maximum = 90,
            TickFrequency = 10,
            Value = 85,
            Width = 180
        };
        _jpegQualityLabel = CreateMutedLabel("JPEG 85");
        _adaptiveQualityBox = new CheckBox
        {
            Text = "清晰优先自适应",
            AutoSize = true,
            Checked = true
        };

        var qualityPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };
        qualityPanel.Controls.Add(_jpegQualityTrack);
        qualityPanel.Controls.Add(_jpegQualityLabel);

        var networkSettings = CreateSection(
            "连接",
            "端口、口令与后台行为");
        AddSettingRow(networkSettings, 0, "本机 IP", _localIpsBox);
        AddSettingRow(networkSettings, 1, "监听端口", _hostPortBox);
        AddSettingRow(networkSettings, 2, "访问口令", _hostPasswordBox);
        AddSettingRow(networkSettings, 3, "开机自启", _startWithWindowsBox);
        AddSettingRow(networkSettings, 4, "被控自启", _autoStartHostBox);
        AddSettingRow(networkSettings, 5, "托盘驻留", _minimizeToTrayBox);
        AddSettingRow(networkSettings, 6, "远程启动", _allowRemoteStartBox);

        var captureSettings = CreateSection(
            "画面",
            "屏幕、帧率与传输质量");
        AddSettingRow(captureSettings, 0, "捕获屏幕", _captureTargetBox);
        AddSettingRow(captureSettings, 1, "传输缩放", _captureScaleBox);
        AddSettingRow(captureSettings, 2, "帧率", _hostFpsBox);
        AddSettingRow(captureSettings, 3, "画质", qualityPanel);
        AddSettingRow(captureSettings, 4, "自适应", _adaptiveQualityBox);

        settingsArea.Controls.Add(networkSettings.Parent!, 0, 0);
        settingsArea.Controls.Add(captureSettings.Parent!, 1, 0);
        ConfigureResponsiveHostSettings(settingsArea, networkSettings.Parent!, captureSettings.Parent!);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 12),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 8)
        };

        _hostToggleButton = CreatePrimaryButton("启动被控端");
        bool elevated =
            WindowsProcessElevation.IsCurrentProcessElevated();
        _restartAsAdministratorButton = CreateSecondaryButton(
            elevated
                ? "已管理员运行"
                : "管理员重启");
        _restartAsAdministratorButton.Enabled = !elevated;
        _persistentStartupButton = CreateSecondaryButton("安装 / 更新常驻权限");
        SetToolTip(_persistentStartupButton,
            "一次管理员授权：安装到受保护目录，之后登录自动以管理员运行。" +
            "不保存 Windows 密码，不关闭 UAC；取消“开机自启”可停用。");
        _persistentStartupButton.Click += async (_, _) => await InstallPersistentStartupAsync();
        _secureDesktopButton = CreateSecondaryButton("锁屏控制…");
        var secureDesktopMenu = new ContextMenuStrip();
        secureDesktopMenu.Items.Add("安装 / 更新锁屏控制", null, async (_, _) => await ConfigureSecureDesktopAsync(true));
        secureDesktopMenu.Items.Add("停用锁屏控制", null, async (_, _) => await ConfigureSecureDesktopAsync(false));
        _secureDesktopButton.ContextMenuStrip = secureDesktopMenu;
        _secureDesktopButton.Click += (_, _) => secureDesktopMenu.Show(_secureDesktopButton, new Point(0, _secureDesktopButton.Height));
        SetToolTip(_secureDesktopButton,
            "一次管理员安装后，可远程操作当前用户的锁屏 / 登录界面。" +
            "无密码账户直接点击登录；有密码或 PIN 的账户按 Windows 提示输入。" +
            "不保存系统密码，不代替指纹、人脸等硬件验证。首次开机尚未登录不在支持范围内。");
        SetToolTip(
            _restartAsAdministratorButton,
            elevated
                ? "当前被控端可操作普通管理员窗口；" +
                    "锁屏 / UAC 安全桌面需另行启用“锁屏控制”。"
                : "操作任务管理器等管理员窗口需要让被控端以管理员权限运行；" +
                    "点击后确认 Windows UAC；锁屏控制需另行安装辅助服务。");
        var refreshIpButton = CreateSecondaryButton("刷新 IP/屏幕");
        refreshIpButton.Click += (_, _) =>
        {
            RefreshLocalIps();
            RefreshCaptureTargets();
            UpdateDiscoveryPresence();
        };
        var minimizeToTrayButton = CreateSecondaryButton("收入托盘");
        minimizeToTrayButton.Click += (_, _) => HideToTray(showTip: true);
        var exportLogButton = CreateSecondaryButton("导出日志");
        exportLogButton.Click += (_, _) => ExportDiagnosticLog();
        _hostStatusLabel = CreateStatusBadge("未启动", NeutralBadgeBackColor, NeutralBadgeTextColor);

        actions.Controls.Add(_hostToggleButton);
        actions.Controls.Add(_restartAsAdministratorButton);
        actions.Controls.Add(_persistentStartupButton);
        actions.Controls.Add(_secureDesktopButton);
        actions.Controls.Add(refreshIpButton);
        actions.Controls.Add(minimizeToTrayButton);
        actions.Controls.Add(exportLogButton);
        actions.Controls.Add(_hostStatusLabel);

        _hostLogBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(17, 24, 39),
            ForeColor = Color.FromArgb(209, 250, 229),
            Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point),
            Margin = new Padding(0)
        };

        var logSection = CreateSection(
            "运行日志",
            "主机状态与关键事件");
        if (logSection.Parent is TableLayoutPanel logShell)
        {
            logShell.AutoSize = false;
            logShell.Dock = DockStyle.Fill;
            logShell.MinimumSize = new Size(0, 130);
        }

        logSection.AutoSize = false;
        logSection.Dock = DockStyle.Fill;
        logSection.Controls.Add(_hostLogBox, 0, 0);
        logSection.SetColumnSpan(_hostLogBox, 2);
        logSection.RowStyles.Clear();
        logSection.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(settingsArea, 0, 0);
        root.Controls.Add(actions, 0, 1);
        root.Controls.Add(logSection.Parent!, 0, 2);
        page.Controls.Add(root);
        return page;
    }

    private TabPage BuildViewerPage()
    {
        long phaseStartedAt = Stopwatch.GetTimestamp();
        var page = CreatePage("IP 直连");
        var root = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(20),
            BackColor = AppBackColor
        };

        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        long rootCreatedAt = Stopwatch.GetTimestamp();

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 44,
            Padding = new Padding(0),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0)
        };

        _viewerHostBox = new TextBox
        {
            Width = 180,
            PlaceholderText = "192.168.1.100"
        };
        StyleInput(_viewerHostBox);
        _viewerPortBox = CreatePortInput();
        _viewerAutoPortBox = new CheckBox { Text = "自动端口", Checked = true, AutoSize = true, Margin = new Padding(6, 12, 6, 0) };
        _viewerPasswordBox = CreatePasswordInput();
        _viewerPasswordBox.PlaceholderText = "连接口令";
        _discoveredHostsBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 230,
            DropDownWidth = 360
        };
        StyleInput(_discoveredHostsBox);
        _viewerCaptureTargetBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 260,
            DropDownWidth = 360,
            Enabled = false
        };
        StyleInput(_viewerCaptureTargetBox);
        _viewerVideoModeBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 150,
            DropDownWidth = 220
        };
        PopulateViewerVideoModes();
        StyleInput(_viewerVideoModeBox);
        _discoverHostsButton = CreateSecondaryButton("扫描/探测");
        _diagnoseConnectionButton = CreateSecondaryButton("诊断");
        _editSavedDeviceRemarkButton = CreateSecondaryButton("修改备注");
        _editSavedDeviceRemarkButton.Enabled = false;
        _deleteSavedDeviceButton = CreateSecondaryButton("删除记录");
        _deleteSavedDeviceButton.Enabled = false;
        ApplyButtonStyle(
            _deleteSavedDeviceButton,
            SurfaceBackColor,
            DangerColor,
            DangerColor);
        _sendClipboardButton = CreateSecondaryButton("发送剪贴板");
        _sendClipboardButton.Enabled = false;
        _readClipboardButton = CreateSecondaryButton("读取剪贴板");
        _readClipboardButton.Enabled = false;
        _sendFileButton = CreateSecondaryButton("发送文件");
        _sendFileButton.Enabled = false;
        _pasteFilesButton = CreateSecondaryButton("粘贴文件");
        _pasteFilesButton.Enabled = false;
        _pullRemoteFilesButton = CreateSecondaryButton(RemoteFilePullUi.DefaultButtonText);
        _pullRemoteFilesButton.Enabled = false;
        _openReceivedFilesButton = CreateSecondaryButton("接收目录");
        _remoteUpdateButton = CreateSecondaryButton("同步更新");
        _remoteUpdateButton.Enabled = false;
        _viewerToggleButton = CreatePrimaryButton("连接");
        _viewerStatusLabel = CreateStatusLine("未连接");
        long controlsCreatedAt = Stopwatch.GetTimestamp();

        toolbar.Controls.Add(
            CreateNonWrappingToolbarField(
                "设备",
                _discoveredHostsBox));
        toolbar.Controls.Add(_discoverHostsButton);
        toolbar.Controls.Add(_diagnoseConnectionButton);
        _addDeviceButton = CreateSecondaryButton("新增设备");
        _addDeviceButton.Click += (_, _) => AddDevice();
        toolbar.Controls.Add(_addDeviceButton);
        toolbar.Controls.Add(
            CreateNonWrappingToolbarField(
                "IP/主机名",
                _viewerHostBox));
        toolbar.Controls.Add(
            CreateNonWrappingToolbarField(
                "端口",
                _viewerPortBox));
        toolbar.Controls.Add(_viewerAutoPortBox);
        toolbar.Controls.Add(
            CreateNonWrappingToolbarField(
                "口令",
                _viewerPasswordBox));
        toolbar.Controls.Add(
            CreateNonWrappingToolbarField(
                "远程屏幕",
                _viewerCaptureTargetBox));
        toolbar.Controls.Add(
            CreateNonWrappingToolbarField(
                "模式",
                _viewerVideoModeBox));
        toolbar.Controls.Add(_sendClipboardButton);
        toolbar.Controls.Add(_readClipboardButton);
        toolbar.Controls.Add(_sendFileButton);
        toolbar.Controls.Add(_pasteFilesButton);
        toolbar.Controls.Add(_pullRemoteFilesButton);
        toolbar.Controls.Add(_openReceivedFilesButton);
        toolbar.Controls.Add(_remoteUpdateButton);
        toolbar.Controls.Add(_viewerToggleButton);
        long toolbarPopulatedAt = Stopwatch.GetTimestamp();

        // This section lives in an AutoSize root row. It must shrink after
        // the wrapping toolbar learns its real width, otherwise the section
        // retains its initial one-control-per-line height and pushes the
        // percent-sized device/status workspace below the visible page.
        var toolbarSection = CreateSection(
            "连接",
            "选择设备并建立加密会话",
            sizeToContent: true);
        toolbarSection.Controls.Add(toolbar, 0, 0);
        toolbarSection.SetColumnSpan(toolbar, 2);
        toolbarSection.RowStyles.Clear();
        toolbarSection.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        ConfigureResponsiveViewerToolbar(
            toolbar,
            toolbarSection);
        long toolbarConfiguredAt = Stopwatch.GetTimestamp();

        var workspace = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            MinimumSize = new Size(0, 220),
            ColumnCount = 2,
            RowCount = 1,
            BackColor = AppBackColor,
            Margin = new Padding(0, 16, 0, 0)
        };
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _discoveredHostsList = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawFixed,
            IntegralHeight = false,
            ItemHeight = 52,
            BackColor = SurfaceBackColor,
            ForeColor = TextColor
        };
        ConfigureDiscoveredHostsList();
        var savedDeviceActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = SurfaceBackColor,
            Padding = new Padding(0, 8, 0, 0),
            Margin = new Padding(0)
        };
        _deleteSavedDeviceButton.Margin = new Padding(8, 0, 0, 0);
        _editSavedDeviceRemarkButton.Margin = new Padding(0);
        savedDeviceActions.Controls.Add(_deleteSavedDeviceButton);
        savedDeviceActions.Controls.Add(_editSavedDeviceRemarkButton);
        long hostsCreatedAt = Stopwatch.GetTimestamp();

        var hostsSection = CreateSection(
            "设备",
            "局域网发现与最近连接");
        if (hostsSection.Parent is TableLayoutPanel hostsShell)
        {
            hostsShell.AutoSize = false;
            hostsShell.Dock = DockStyle.Fill;
            hostsShell.Margin = new Padding(0, 0, 12, 0);
        }

        hostsSection.AutoSize = false;
        hostsSection.Dock = DockStyle.Fill;
        hostsSection.Controls.Add(_discoveredHostsList, 0, 0);
        hostsSection.SetColumnSpan(_discoveredHostsList, 2);
        hostsSection.Controls.Add(savedDeviceActions, 0, 1);
        hostsSection.SetColumnSpan(savedDeviceActions, 2);
        hostsSection.RowCount = 2;
        hostsSection.RowStyles.Clear();
        hostsSection.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        hostsSection.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var statusPanel = new BufferedPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = SurfaceBackColor,
            Padding = new Padding(12),
            Margin = new Padding(0)
        };
        _viewerStatusLabel.Dock = DockStyle.Fill;
        _viewerStatusLabel.Font = new Font(Font, FontStyle.Regular);
        _viewerStatusLabel.ContextMenuStrip = CreateViewerStatusMenu();
        _remoteFilePullProgressBar = new ProgressBar
        {
            Dock = DockStyle.Bottom,
            Height = 4,
            Margin = new Padding(0),
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 0,
            Visible = false
        };
        statusPanel.Controls.Add(_viewerStatusLabel);
        statusPanel.Controls.Add(_remoteFilePullProgressBar);
        _remoteFilePullProgressBar.BringToFront();
        void UpdateViewerStatusWrapWidth()
        {
            _viewerStatusLabel.MaximumSize = new Size(
                Math.Max(1, statusPanel.ClientSize.Width - statusPanel.Padding.Horizontal),
                0);
        }

        statusPanel.ClientSizeChanged += (_, _) => UpdateViewerStatusWrapWidth();
        statusPanel.HandleCreated += (_, _) => UpdateViewerStatusWrapWidth();
        UpdateViewerStatusWrapWidth();
        long statusCreatedAt = Stopwatch.GetTimestamp();

        var statusSection = CreateSection(
            "会话状态",
            "连接、传输与诊断反馈");
        if (statusSection.Parent is TableLayoutPanel statusShell)
        {
            statusShell.AutoSize = false;
            statusShell.Dock = DockStyle.Fill;
            statusShell.Margin = new Padding(0);
        }

        statusSection.AutoSize = false;
        statusSection.Dock = DockStyle.Fill;
        statusSection.Controls.Add(statusPanel, 0, 0);
        statusSection.SetColumnSpan(statusPanel, 2);
        statusSection.RowStyles.Clear();
        statusSection.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        workspace.Controls.Add(hostsSection.Parent!, 0, 0);
        workspace.Controls.Add(statusSection.Parent!, 1, 0);
        ConfigureResponsiveViewerWorkspace(workspace, hostsSection.Parent!, statusSection.Parent!);
        long workspaceConfiguredAt = Stopwatch.GetTimestamp();

        root.Controls.Add(toolbarSection.Parent!, 0, 0);
        root.Controls.Add(workspace, 0, 1);
        page.Controls.Add(root);
        long pageCompletedAt = Stopwatch.GetTimestamp();
        _diagnosticLog.Append(
            "PERF",
            "连接页创建分段：根容器 " +
            $"{ElapsedMilliseconds(phaseStartedAt, rootCreatedAt):F1}，" +
            "控件 " +
            $"{ElapsedMilliseconds(rootCreatedAt, controlsCreatedAt):F1}，" +
            "工具栏填充 " +
            $"{ElapsedMilliseconds(controlsCreatedAt, toolbarPopulatedAt):F1}，" +
            "工具栏布局 " +
            $"{ElapsedMilliseconds(toolbarPopulatedAt, toolbarConfiguredAt):F1}，" +
            "设备区 " +
            $"{ElapsedMilliseconds(toolbarConfiguredAt, hostsCreatedAt):F1}，" +
            "状态区 " +
            $"{ElapsedMilliseconds(hostsCreatedAt, statusCreatedAt):F1}，" +
            "工作区布局 " +
            $"{ElapsedMilliseconds(statusCreatedAt, workspaceConfiguredAt):F1}，" +
            "挂载 " +
            $"{ElapsedMilliseconds(workspaceConfiguredAt, pageCompletedAt):F1}ms。");
        return page;
    }

    private TabPage BuildRelayPage()
    {
        var page = CreatePage("公网中继");
        var root = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(20),
            BackColor = AppBackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _relayServerSummaryLabel = CreateStatusLine("尚未配置公网中继服务器");
        _relayServerSummaryLabel.Dock = DockStyle.Fill;
        _relayRegisterHostBox = new CheckBox
        {
            Text = "发布本机到在线列表，并自动更新 IP / 端口",
            AutoSize = true,
            Checked = true
        };
        _relayOptimizeNetworkBox = new CheckBox
        {
            Text = "自动优化双网卡中继线路（管理员权限）",
            AutoSize = true,
            Checked = true
        };
        _toolTip.SetToolTip(_relayOptimizeNetworkBox,
            "仅对已配置中继的公网 IPv4 地址使用 90 秒临时路由；运行中续期，退出自动撤销。" +
            "不更改默认网关或网卡优先级，已有连接时不切线路。关闭此项会让中继连接回退重连。");
        _relayRouteStatusLabel = CreateStatusLine(_relayNetworkOptimizer.Status);
        var networkOptions = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0)
        };
        networkOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        networkOptions.Controls.Add(_relayOptimizeNetworkBox, 0, 0);
        networkOptions.Controls.Add(_relayRouteStatusLabel, 0, 1);
        _relayViewerPasswordBox = CreatePasswordInput();
        _relayViewerPasswordBox.PlaceholderText = "所选远端电脑的访问口令";
        _relayVideoModeBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 240,
            DropDownWidth = 300
        };
        PopulateViewerVideoModes(_relayVideoModeBox);

        _relayConfigureButton = CreatePrimaryButton("配置/更新服务器");
        _relayRefreshButton = CreateSecondaryButton("刷新在线设备");
        _relayReportAddressButton = CreateSecondaryButton("立即上报本机 IP");
        _relayAddressButton = CreateSecondaryButton("查看 / 使用 IP");
        _relayConnectButton = CreatePrimaryButton("连接所选设备");
        var configurationActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0)
        };
        configurationActions.Controls.Add(_relayConfigureButton);
        configurationActions.Controls.Add(_relayRefreshButton);
        configurationActions.Controls.Add(_relayReportAddressButton);

        var configuration = CreateSection(
            "私有公网中继",
            "跨局域网时两端主动连接公网服务器；画面与控制仍使用端到端加密",
            sizeToContent: true);
        AddSettingRow(configuration, 0, "服务器", _relayServerSummaryLabel);
        AddSettingRow(configuration, 1, "本机上线", _relayRegisterHostBox);
        AddSettingRow(configuration, 2, "远控口令", _relayViewerPasswordBox);
        AddSettingRow(configuration, 3, "画面模式", _relayVideoModeBox);
        AddSettingRow(configuration, 4, "网络", networkOptions);
        AddSettingRow(configuration, 5, "管理", configurationActions);
        ConfigureWrappedActionRow(configurationActions);

        _relayDevicesList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            ShowItemToolTips = true,
            HideSelection = false,
            BorderStyle = BorderStyle.None,
            BackColor = SurfaceBackColor,
            ForeColor = TextColor
        };
        _relayDevicesList.Columns.Add("设备名称", 230);
        _relayDevicesList.Columns.Add("平台", 100);
        _relayDevicesList.Columns.Add("版本", 125);
        _relayDevicesList.Columns.Add("状态", 130);
        _relayDevicesList.Columns.Add("最后响应", 100);
        _relayDevicesList.Columns.Add("直连 IP / 端口", 270);
        _relayStatusLabel = CreateStatusLine(
            "配置服务器后会显示当前在线的 RemoteDesk 设备。");
        _relayStatusLabel.Dock = DockStyle.Fill;

        var deviceActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0, 10, 0, 0)
        };
        deviceActions.Controls.Add(_relayConnectButton);
        deviceActions.Controls.Add(_relayAddressButton);

        var online = CreateSection(
            "在线设备",
            "双击设备或选择后点击连接；已有会话时，新连接会替换旧连接");
        if (online.Parent is TableLayoutPanel onlineShell)
        {
            onlineShell.AutoSize = false;
            onlineShell.Dock = DockStyle.Fill;
            onlineShell.Margin = new Padding(0, 16, 0, 0);
            onlineShell.MinimumSize = new Size(0, 260);
        }

        online.AutoSize = false;
        online.Dock = DockStyle.Fill;
        online.RowCount = 3;
        online.RowStyles.Clear();
        online.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        online.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        online.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        online.Controls.Add(deviceActions, 0, 0);
        online.SetColumnSpan(deviceActions, 2);
        online.Controls.Add(_relayStatusLabel, 0, 1);
        online.SetColumnSpan(_relayStatusLabel, 2);
        online.Controls.Add(_relayDevicesList, 0, 2);
        online.SetColumnSpan(_relayDevicesList, 2);
        ConfigureWrappedActionRow(deviceActions);

        root.Controls.Add(configuration.Parent!, 0, 0);
        root.Controls.Add(online.Parent!, 0, 1);
        page.Controls.Add(root);
        return page;
    }

    private ContextMenuStrip CreateViewerStatusMenu()
    {
        _viewerStatusMenu = new ContextMenuStrip();
        _viewerStatusMenu.Items.Add("复制状态", null, async (_, _) => await CopyViewerStatusAsync());
        _viewerStatusMenu.Items.Add("打开本机接收目录", null, (_, _) => OpenReceivedFilesDirectory());
        return _viewerStatusMenu;
    }

    private async Task CopyViewerStatusAsync()
    {
        try
        {
            await ClipboardTextService.SetTextAsync(_viewerStatusLabel.Text);
            SetViewerStatus("状态信息已复制到本机剪贴板。", SuccessTextColor);
        }
        catch (Exception ex) when (ex is TimeoutException or System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            SetViewerStatus($"复制状态失败：{ex.Message}", DangerColor);
        }
    }

    private void OpenReceivedFilesDirectory()
    {
        try
        {
            string receiveDirectory = FileTransferReceiver.GetReceiveDirectory();
            Directory.CreateDirectory(receiveDirectory);
            Process.Start(RemoteFilePullUi.CreateOpenReceiveDirectoryStartInfo(receiveDirectory));
            SetViewerStatus($"已打开本机接收目录：{receiveDirectory}", SuccessTextColor);
        }
        catch (Exception ex) when (ex is IOException or
            UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or
            InvalidOperationException or
            ArgumentException)
        {
            SetViewerStatus($"打开本机接收目录失败：{ex.Message}", DangerColor);
        }
    }

    private void OnViewerFileTransferStatusReceived(bool success, string message)
    {
        OnUi(() =>
        {
            bool pending = _viewerClient.IsRemoteClipboardFileRequestPending;
            RemoteFilePullStatusStage stage = RemoteFilePullUi.ClassifyStatus(
                success,
                message,
                pending || _pullingRemoteFilesFromMain);
            if (stage is RemoteFilePullStatusStage.Waiting or RemoteFilePullStatusStage.Receiving)
            {
                _remoteFilePullStatusStage = stage;
            }

            SetRemoteFilePullPending(pending);
            string displayMessage = RemoteFilePullUi.FormatCompletedStatus(
                success,
                message,
                FileTransferReceiver.GetReceiveDirectory());
            SetViewerStatus(displayMessage, success ? SuccessTextColor : DangerColor);
        });
    }

    private void SetRemoteFilePullPending(bool pending)
    {
        _pullingRemoteFilesFromMain = pending;
        if (pending && _remoteFilePullStatusStage == RemoteFilePullStatusStage.Other)
        {
            _remoteFilePullStatusStage = RemoteFilePullStatusStage.Waiting;
        }
        else if (!pending)
        {
            _remoteFilePullStatusStage = RemoteFilePullStatusStage.Other;
        }

        _pullRemoteFilesButton.Text = pending
            ? RemoteFilePullUi.GetButtonText(_remoteFilePullStatusStage)
            : RemoteFilePullUi.DefaultButtonText;
        _remoteFilePullProgressBar.MarqueeAnimationSpeed = pending ? 24 : 0;
        _remoteFilePullProgressBar.Visible = pending;
        ApplyViewerCapabilityState(_viewerClient.IsConnected);
    }

    private void ConfigureToolTips()
    {
        SetToolTip(_localIpsBox, "本机可用于局域网连接的 IPv4 地址。");
        SetToolTip(
            _hostPortBox,
            "被控端监听端口，默认 56565；若 Windows 保留该端口，" +
            "会自动迁移并保存到 40565/40567。");
        SetToolTip(_hostPasswordBox, "控制端连接时必须填写相同口令。");
        SetToolTip(_startWithWindowsBox, "当前 Windows 用户登录后自动启动 RemoteDesk。");
        SetToolTip(_minimizeToTrayBox, "关闭或最小化窗口时驻留到右下角托盘。");
        SetToolTip(_allowRemoteStartBox, "允许同口令控制端远程启动本机被控端。");
        SetToolTip(_captureTargetBox, "选择被控端要捕获的屏幕。");
        SetToolTip(_captureScaleBox, "“100%（原生）+ 60 FPS”为 4K60；“最高 1440p + 60 FPS”为精确 1440p60。1440p 模式使用 GPU 表面缩放，低于该分辨率的屏幕保持原生。");
        SetToolTip(_hostFpsBox, "30 FPS 更节省带宽；60 FPS 仅在查看端声明高帧率能力且硬件捕获/编解码链路可用时启用。");
        SetToolTip(_jpegQualityTrack, "JPEG 画质越高越清晰，占用带宽和 CPU 也越高。");
        SetToolTip(_adaptiveQualityBox, "压力变高时先降帧率和 JPEG 画质，持续严重拥塞才降低分辨率；恢复后优先拉回分辨率。");
        SetToolTip(_hostToggleButton, "启动或停止本机被控端。");

        SetToolTip(_discoveredHostsBox, "选择扫描到的在线设备或最近连接设备。");
        SetToolTip(
            _discoveredHostsList,
            "双击设备可直接连接；已保存设备可右键修改备注或删除记录，也可按 F2/Delete。");
        SetToolTip(_discoverHostsButton, "扫描局域网、手填地址和最近连接设备。");
        SetToolTip(_diagnoseConnectionButton, "检查目标 IP、端口、RemoteDesk 握手和视频模式。");
        SetToolTip(_editSavedDeviceRemarkButton, "修改所选已保存设备的备注；留空可清除备注。");
        SetToolTip(_deleteSavedDeviceButton, "删除所选设备的保存记录，不会卸载或关闭远端软件。");
        SetToolTip(_viewerHostBox, "填写被控端内网 IP 或主机名，例如 192.168.1.100。");
        SetToolTip(
            _viewerPortBox,
            "被控端 TCP 端口，默认 56565；扫描和历史探测会同时兼容 " +
            "Windows 自动迁移端口 40565/40567。");
        SetToolTip(_viewerPasswordBox, "填写与被控端相同的连接口令。");
        SetToolTip(_viewerCaptureTargetBox, "连接后可切换远程屏幕。");
        SetToolTip(
            _viewerVideoModeBox,
            "自动低延迟会在 H.264 不可用时保持 JPEG 连接；" +
            "仅 H.264 适合编码链路已确认可用的设备，否则会断开。");
        SetToolTip(_viewerStatusLabel, "右键可复制当前状态，或打开本机文件接收目录。");
        SetToolTip(_openReceivedFilesButton, "打开本机 RemoteDesk 文件接收目录，连接断开时也可使用。");
        SetToolTip(
            _relayConfigureButton,
            "通过 SSH 自动安装或更新你的私有 Linux 中继；管理员密码仅在本次操作的内存中使用。");
        SetToolTip(
            _relayRefreshButton,
            "读取私有中继上的在线设备目录。");
        SetToolTip(
            _relayRegisterHostBox,
            "被控端启动后主动连到公网中继，不需要给本机做端口映射。");
        SetToolTip(
            _relayDevicesList,
            "双击在线设备即可通过私有中继建立端到端加密远控连接。");
        SetToolTip(
            _relayViewerPasswordBox,
            "这是目标电脑 RemoteDesk 的访问口令，不是 Linux 管理员密码。");
        UpdateViewerCapabilityToolTips(_viewerClient.IsConnected, NormalizeCapabilities(_connectedViewerCapabilities));
        UpdateViewerActionState();
    }

    private void SetToolTip(Control control, string text)
    {
        if (!control.IsDisposed)
        {
            _toolTip.SetToolTip(control, text);
        }
    }

    private static void ConfigureResponsiveHostSettings(
        TableLayoutPanel settingsArea,
        Control networkSection,
        Control captureSection)
    {
        bool applying = false;
        void Apply()
        {
            if (applying)
            {
                return;
            }

            int dpi = settingsArea.DeviceDpi;
            bool compact = ResponsiveWindowLayout.IsBelowLogicalWidth(
                settingsArea.ClientSize.Width,
                860,
                dpi);
            int sectionGap = ResponsiveWindowLayout.ScaleLogical(12, dpi);

            applying = true;
            settingsArea.SuspendLayout();
            try
            {
                settingsArea.ColumnStyles.Clear();
                settingsArea.RowStyles.Clear();
                if (compact)
                {
                    settingsArea.ColumnCount = 1;
                    settingsArea.RowCount = 2;
                    settingsArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                    settingsArea.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    settingsArea.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    settingsArea.SetColumn(networkSection, 0);
                    settingsArea.SetRow(networkSection, 0);
                    settingsArea.SetColumn(captureSection, 0);
                    settingsArea.SetRow(captureSection, 1);
                    networkSection.Margin = new Padding(0, 0, 0, sectionGap);
                    captureSection.Margin = new Padding(0);
                }
                else
                {
                    settingsArea.ColumnCount = 2;
                    settingsArea.RowCount = 1;
                    settingsArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
                    settingsArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
                    settingsArea.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    settingsArea.SetColumn(networkSection, 0);
                    settingsArea.SetRow(networkSection, 0);
                    settingsArea.SetColumn(captureSection, 1);
                    settingsArea.SetRow(captureSection, 0);
                    networkSection.Margin = new Padding(0, 0, sectionGap, 0);
                    captureSection.Margin = new Padding(0);
                }
            }
            finally
            {
                settingsArea.ResumeLayout();
                applying = false;
            }
        }

        settingsArea.Resize += (_, _) => Apply();
        settingsArea.HandleCreated += (_, _) => Apply();
        Apply();
    }

    private void ConfigureResponsiveViewerToolbar(
        FlowLayoutPanel toolbar,
        TableLayoutPanel toolbarSection)
    {
        bool applying = false;
        void Apply()
        {
            if (applying)
            {
                return;
            }

            int dpi = toolbar.DeviceDpi;
            int availableWidth = Math.Max(
                toolbar.ClientSize.Width,
                toolbar.Parent?.ClientSize.Width ?? 0);
            if (!toolbar.IsHandleCreated)
            {
                availableWidth = Math.Max(
                    availableWidth,
                    ResponsiveWindowLayout.ScaleLogical(
                        1040,
                        dpi));
            }

            if (availableWidth <= 0)
            {
                return;
            }

            applying = true;
            try
            {
                bool compact = ResponsiveWindowLayout.IsBelowLogicalWidth(availableWidth, 1100, dpi);
                bool narrow = ResponsiveWindowLayout.IsBelowLogicalWidth(availableWidth, 880, dpi);

                _discoveredHostsBox.Width = ResponsiveWindowLayout.ScaleLogical(
                    narrow ? 190 : compact ? 220 : 260,
                    dpi);
                _viewerHostBox.Width = ResponsiveWindowLayout.ScaleLogical(
                    narrow ? 128 : compact ? 150 : 180,
                    dpi);
                _viewerPasswordBox.Width = ResponsiveWindowLayout.ScaleLogical(
                    narrow ? 128 : compact ? 150 : 180,
                    dpi);
                _viewerCaptureTargetBox.Width = ResponsiveWindowLayout.ScaleLogical(
                    narrow ? 190 : compact ? 230 : 280,
                    dpi);
                _viewerVideoModeBox.Width = ResponsiveWindowLayout.ScaleLogical(
                    narrow ? 126 : compact ? 140 : 156,
                    dpi);
                _discoverHostsButton.MinimumSize = new Size(
                    ResponsiveWindowLayout.ScaleLogical(narrow ? 84 : 92, dpi),
                    ResponsiveWindowLayout.ScaleLogical(32, dpi));
                _diagnoseConnectionButton.MinimumSize = new Size(
                    ResponsiveWindowLayout.ScaleLogical(narrow ? 64 : 72, dpi),
                    ResponsiveWindowLayout.ScaleLogical(32, dpi));

                ConstrainWrappedToolbarSection(
                    toolbar,
                    toolbarSection,
                    availableWidth);
            }
            finally
            {
                applying = false;
            }
        }

        toolbar.Resize += (_, _) => Apply();
        toolbar.ParentChanged += (_, _) => Apply();
        toolbar.HandleCreated += (_, _) => Apply();
        Apply();
    }

    private static void ConfigureWrappedActionRow(FlowLayoutPanel actions)
    {
        // AutoSize measures a wrapping panel before the table has assigned its
        // width, retaining a one-button-per-row height even after reflow. Let
        // the table assign the width, then size only this row from that width.
        actions.AutoSize = false;
        actions.Dock = DockStyle.Top;
        bool applying = false;
        void Apply()
        {
            if (applying || actions.IsDisposed || !actions.Visible || actions.ClientSize.Width <= 0) return;
            applying = true;
            try { ConstrainWrappedActionRow(actions, actions.ClientSize.Width); }
            finally { applying = false; }
        }
        actions.ClientSizeChanged += (_, _) => Apply();
        actions.VisibleChanged += (_, _) => Apply();
        actions.DpiChangedAfterParent += (_, _) => Apply();
        Apply();
    }

    internal static void ConstrainWrappedActionRow(FlowLayoutPanel actions, int availableWidth)
    {
        ArgumentNullException.ThrowIfNull(actions);
        int height = CalculateWrappedToolbarHeight(actions, availableWidth);
        actions.AutoSize = false;
        actions.Dock = DockStyle.Top;
        actions.Height = height;
    }

    internal static int ConstrainWrappedToolbarSection(
        FlowLayoutPanel toolbar,
        TableLayoutPanel toolbarSection,
        int availableWidth)
    {
        ArgumentNullException.ThrowIfNull(toolbar);
        ArgumentNullException.ThrowIfNull(toolbarSection);
        if (availableWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(availableWidth));
        }

        // A wrapping AutoSize FlowLayoutPanel can be measured before its
        // parent has a real width. Without an explicit width constraint,
        // that first one-control-per-line preferred height is retained by
        // the surrounding AutoSize table even after the toolbar visibly
        // reflows into a few rows. Recompute against the actual width and
        // propagate the smaller preferred height to the root layout.
        var wrappingLimit = new Size(availableWidth, 0);
        if (toolbar.MaximumSize != wrappingLimit)
        {
            toolbar.MaximumSize = wrappingLimit;
        }

        int toolbarHeight = CalculateWrappedToolbarHeight(
            toolbar,
            availableWidth);
        toolbar.Height = toolbarHeight;
        int contentHeight = checked(
            toolbarHeight +
            toolbarSection.Padding.Vertical +
            toolbar.Margin.Vertical);
        toolbarSection.MaximumSize = new Size(
            0,
            contentHeight);

        if (toolbarSection.Parent is not TableLayoutPanel shell)
        {
            return contentHeight;
        }

        Control? header = shell.GetControlFromPosition(0, 0);
        if (header is null)
        {
            return contentHeight;
        }

        int shellHeight = checked(
            contentHeight +
            header.PreferredSize.Height +
            header.Margin.Vertical +
            shell.Padding.Vertical);
        shell.MaximumSize = new Size(0, shellHeight);
        shell.Height = shellHeight;
        return shellHeight;
    }

    internal static int CalculateWrappedToolbarHeight(
        FlowLayoutPanel toolbar,
        int availableWidth)
    {
        ArgumentNullException.ThrowIfNull(toolbar);
        if (availableWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(availableWidth));
        }

        int contentWidth = Math.Max(
            1,
            availableWidth - toolbar.Padding.Horizontal);
        int occupiedWidth = 0;
        int rowHeight = 0;
        int totalHeight = toolbar.Padding.Vertical;
        foreach (Control control in toolbar.Controls)
        {
            if (!control.Visible)
            {
                continue;
            }

            Size preferred = control.GetPreferredSize(
                Size.Empty);
            int itemWidth = Math.Max(
                    control.MinimumSize.Width,
                    preferred.Width) +
                control.Margin.Horizontal;
            int itemHeight = Math.Max(
                    control.MinimumSize.Height,
                    preferred.Height) +
                control.Margin.Vertical;
            if (occupiedWidth > 0 &&
                occupiedWidth + itemWidth > contentWidth)
            {
                totalHeight += rowHeight;
                occupiedWidth = 0;
                rowHeight = 0;
            }

            occupiedWidth += itemWidth;
            rowHeight = Math.Max(
                rowHeight,
                itemHeight);
        }

        return Math.Max(
            1,
            totalHeight + rowHeight);
    }

    private void PopulateViewerVideoModes()
    {
        PopulateViewerVideoModes(_viewerVideoModeBox);
    }

    private static void PopulateViewerVideoModes(
        ComboBox videoModeBox)
    {
        videoModeBox.Items.Clear();
        videoModeBox.Items.Add(new ViewerVideoModeItem(ViewerVideoMode.Automatic, "自动（H.264 清晰增强）"));
        videoModeBox.Items.Add(new ViewerVideoModeItem(ViewerVideoMode.StableJpeg, "文字清晰（JPEG）"));
        videoModeBox.Items.Add(
            new ViewerVideoModeItem(
                ViewerVideoMode.ForceH264,
                "仅 H.264（不可用即断开）"));
        videoModeBox.SelectedIndex = 0;
    }

    private void ConfigureResponsiveViewerWorkspace(
        TableLayoutPanel workspace,
        Control hostsSection,
        Control statusSection)
    {
        bool applying = false;
        void Apply()
        {
            if (applying)
            {
                return;
            }

            int dpi = workspace.DeviceDpi;
            bool compact = ResponsiveWindowLayout.IsBelowLogicalWidth(
                workspace.ClientSize.Width,
                760,
                dpi);
            int sectionGap = ResponsiveWindowLayout.ScaleLogical(12, dpi);

            applying = true;
            workspace.SuspendLayout();
            try
            {
                workspace.ColumnStyles.Clear();
                workspace.RowStyles.Clear();
                if (compact)
                {
                    workspace.ColumnCount = 1;
                    workspace.RowCount = 2;
                    workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                    workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
                    workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
                    workspace.SetColumn(hostsSection, 0);
                    workspace.SetRow(hostsSection, 0);
                    workspace.SetColumn(statusSection, 0);
                    workspace.SetRow(statusSection, 1);
                    hostsSection.Margin = new Padding(0, 0, 0, sectionGap);
                    statusSection.Margin = new Padding(0);
                }
                else
                {
                    workspace.ColumnCount = 2;
                    workspace.RowCount = 1;
                    workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
                    workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
                    workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                    workspace.SetColumn(hostsSection, 0);
                    workspace.SetRow(hostsSection, 0);
                    workspace.SetColumn(statusSection, 1);
                    workspace.SetRow(statusSection, 0);
                    hostsSection.Margin = new Padding(0, 0, sectionGap, 0);
                    statusSection.Margin = new Padding(0);
                }
            }
            finally
            {
                workspace.ResumeLayout();
                applying = false;
            }
        }

        workspace.Resize += (_, _) => Apply();
        workspace.HandleCreated += (_, _) => Apply();
        Apply();
    }

    private void ApplyDpiMetrics(int dpi)
    {
        if (_appliedDpi == dpi)
        {
            return;
        }

        long startedAt = Stopwatch.GetTimestamp();
        _appliedDpi = dpi;
        Size previousTabItemSize = Size.Empty;
        Size targetTabItemSize = Size.Empty;
        Point previousTabPadding = Point.Empty;
        Point targetTabPadding = Point.Empty;
        long tabItemAppliedAt = startedAt;
        long tabPaddingAppliedAt = startedAt;
        if (_tabs is not null)
        {
            Size itemSize = new(
                ResponsiveWindowLayout.ScaleLogical(150, dpi),
                ResponsiveWindowLayout.ScaleLogical(40, dpi));
            Point padding = new(
                ResponsiveWindowLayout.ScaleLogical(16, dpi),
                ResponsiveWindowLayout.ScaleLogical(4, dpi));
            previousTabItemSize = _tabs.ItemSize;
            targetTabItemSize = itemSize;
            previousTabPadding = _tabs.Padding;
            targetTabPadding = padding;
            bool changed = false;
            if (_tabs.ItemSize != itemSize)
            {
                _tabs.ItemSize = itemSize;
                changed = true;
            }
            tabItemAppliedAt = Stopwatch.GetTimestamp();

            if (_tabs.Padding != padding)
            {
                _tabs.Padding = padding;
                changed = true;
            }
            tabPaddingAppliedAt = Stopwatch.GetTimestamp();

            if (changed)
            {
                _tabs.Invalidate();
            }
        }
        long tabsAppliedAt = Stopwatch.GetTimestamp();

        if (_discoveredHostsList is not null)
        {
            int itemHeight =
                ResponsiveWindowLayout.ScaleLogical(
                    52,
                    dpi);
            if (_discoveredHostsList.ItemHeight != itemHeight)
            {
                _discoveredHostsList.ItemHeight = itemHeight;
                _discoveredHostsList.Invalidate();
            }
        }
        long listAppliedAt = Stopwatch.GetTimestamp();

        if (_discoveredHostsBox is not null)
        {
            int width = ResponsiveWindowLayout.ScaleLogical(
                360,
                dpi);
            if (_discoveredHostsBox.DropDownWidth != width)
            {
                _discoveredHostsBox.DropDownWidth = width;
            }
        }

        if (_viewerCaptureTargetBox is not null)
        {
            int width = ResponsiveWindowLayout.ScaleLogical(
                360,
                dpi);
            if (_viewerCaptureTargetBox.DropDownWidth != width)
            {
                _viewerCaptureTargetBox.DropDownWidth = width;
            }
        }

        if (_viewerVideoModeBox is not null)
        {
            int width = ResponsiveWindowLayout.ScaleLogical(
                280,
                dpi);
            if (_viewerVideoModeBox.DropDownWidth != width)
            {
                _viewerVideoModeBox.DropDownWidth = width;
            }
        }

        if (_remoteFilePullProgressBar is not null)
        {
            int height = ResponsiveWindowLayout.ScaleLogical(
                4,
                dpi);
            if (_remoteFilePullProgressBar.Height != height)
            {
                _remoteFilePullProgressBar.Height = height;
            }
        }

        _diagnosticLog.Append(
            "PERF",
            "DPI 控件更新分段：标签 " +
            $"{ElapsedMilliseconds(startedAt, tabsAppliedAt):F1}，" +
            $"其中尺寸 {ElapsedMilliseconds(startedAt, tabItemAppliedAt):F1}、" +
            $"内边距 {ElapsedMilliseconds(tabItemAppliedAt, tabPaddingAppliedAt):F1}，" +
            $"DPI {dpi}，尺寸 {previousTabItemSize}->{targetTabItemSize}，" +
            $"内边距 {previousTabPadding}->{targetTabPadding}；" +
            "设备列表 " +
            $"{ElapsedMilliseconds(tabsAppliedAt, listAppliedAt):F1}，" +
            "下拉框/进度条 " +
            $"{ElapsedMilliseconds(listAppliedAt, Stopwatch.GetTimestamp()):F1}ms。");
    }

    private void ConfigureDiscoveredHostsList()
    {
        _savedDeviceMenu = new ContextMenuStrip();
        _editSavedDeviceRemarkMenuItem = new ToolStripMenuItem(
            "修改备注…",
            null,
            (_, _) => EditSelectedSavedDeviceRemark());
        _deleteSavedDeviceMenuItem = new ToolStripMenuItem(
            "删除保存记录",
            null,
            (_, _) => DeleteSelectedSavedDevice());
        _savedDeviceMenu.Items.Add(_editSavedDeviceRemarkMenuItem);
        _savedDeviceMenu.Items.Add(new ToolStripSeparator());
        _savedDeviceMenu.Items.Add(_deleteSavedDeviceMenuItem);
        _savedDeviceMenu.Opening += (_, _) =>
            UpdateSavedDeviceActionState();
        _discoveredHostsList.ContextMenuStrip = _savedDeviceMenu;
        _discoveredHostsList.DrawItem += (_, args) => DrawDiscoveredHostItem(args);
        _discoveredHostsList.SelectedIndexChanged += (_, _) => DiscoveredHostListChanged();
        _discoveredHostsList.MouseDown += (_, args) =>
        {
            if (args.Button != MouseButtons.Right)
            {
                return;
            }

            int index = _discoveredHostsList.IndexFromPoint(args.Location);
            _discoveredHostsList.SelectedIndex = index;
        };
        _discoveredHostsList.DoubleClick += async (_, _) =>
        {
            if (!_viewerClient.IsConnected &&
                _discoveredHostsList.SelectedItem is RemoteDeviceListItem &&
                !ReportBlockedViewerAction())
            {
                await ToggleViewerAsync();
            }
        };
    }

    private void DrawDiscoveredHostItem(DrawItemEventArgs args)
    {
        if (args.Index < 0 || args.Index >= _discoveredHostsList.Items.Count)
        {
            return;
        }

        bool selected = (args.State & DrawItemState.Selected) == DrawItemState.Selected;
        Color backColor = selected ? Color.FromArgb(219, 234, 254) : SurfaceBackColor;
        Color primaryTextColor = selected ? Color.FromArgb(30, 64, 175) : TextColor;
        Color secondaryTextColor = selected ? Color.FromArgb(37, 99, 235) : MutedTextColor;

        using var background = new SolidBrush(backColor);
        args.Graphics.FillRectangle(background, args.Bounds);

        if (_discoveredHostsList.Items[args.Index] is not RemoteDeviceListItem device)
        {
            return;
        }

        int dpi = _discoveredHostsList.DeviceDpi;
        int horizontalInset = ResponsiveWindowLayout.ScaleLogical(12, dpi);
        int verticalInset = ResponsiveWindowLayout.ScaleLogical(7, dpi);
        int primaryLineHeight = ResponsiveWindowLayout.ScaleLogical(20, dpi);
        int secondaryOffset = ResponsiveWindowLayout.ScaleLogical(22, dpi);
        int secondaryLineHeight = ResponsiveWindowLayout.ScaleLogical(18, dpi);
        Rectangle content = new(
            args.Bounds.Left + horizontalInset,
            args.Bounds.Top + verticalInset,
            Math.Max(0, args.Bounds.Width - (horizontalInset * 2)),
            Math.Max(0, args.Bounds.Height - (verticalInset * 2)));

        string status = device.GetShortStatus();
        TextRenderer.DrawText(
            args.Graphics,
            device.DisplayName,
            Font,
            new Rectangle(content.Left, content.Top, content.Width, primaryLineHeight),
            primaryTextColor,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(
            args.Graphics,
            device.GetListSecondaryText(status),
            Font,
            new Rectangle(content.Left, content.Top + secondaryOffset, content.Width, secondaryLineHeight),
            secondaryTextColor,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        if (selected)
        {
            int lineInset = ResponsiveWindowLayout.ScaleLogical(6, dpi);
            int thickness = Math.Max(1, ResponsiveWindowLayout.ScaleLogical(2, dpi));
            using var pen = new Pen(PrimaryColor, thickness);
            args.Graphics.DrawLine(
                pen,
                args.Bounds.Left,
                args.Bounds.Top + lineInset,
                args.Bounds.Left,
                args.Bounds.Bottom - lineInset);
        }
    }

    private void WireEvents()
    {
        _jpegQualityTrack.ValueChanged += (_, _) =>
        {
            _jpegQualityLabel.Text = $"JPEG {_jpegQualityTrack.Value}";
            UpdateDiscoveryPresence();
        };
        _hostPortBox.ValueChanged += (_, _) => UpdateDiscoveryPresence();
        _hostPasswordBox.TextChanged += (_, _) => UpdateDiscoveryPresence();
        _hostPasswordBox.Leave += (_, _) => SaveSettingsFromUi();
        _viewerHostBox.TextChanged += (_, _) => UpdateViewerActionState();
        _viewerHostBox.Leave += (_, _) => NormalizeViewerHostInput();
        _viewerPortBox.ValueChanged += (_, _) => UpdateViewerActionState();
        _viewerPasswordBox.Leave += (_, _) => SaveSettingsFromUi();
        _captureTargetBox.SelectedIndexChanged += (_, _) => UpdateDiscoveryPresence();
        _startWithWindowsBox.CheckedChanged += async (_, _) => await ApplyStartWithWindowsFromUiAsync();
        _autoStartHostBox.CheckedChanged += (_, _) =>
        {
            if (!_applyingSettings)
            {
                SaveSettingsFromUi();
            }
        };
        _minimizeToTrayBox.CheckedChanged += (_, _) =>
        {
            if (!_applyingSettings)
            {
                SaveSettingsFromUi();
            }
        };
        _allowRemoteStartBox.CheckedChanged += (_, _) =>
        {
            if (!_applyingSettings)
            {
                SaveSettingsFromUi();
            }

            UpdateDiscoveryPresence();
        };
        _viewerVideoModeBox.SelectedIndexChanged += (_, _) =>
        {
            if (!_applyingSettings)
            {
                SaveSettingsFromUi();
            }
        };
        _relayViewerPasswordBox.Leave += (_, _) =>
            SaveSettingsFromUi();
        _relayVideoModeBox.SelectedIndexChanged += (_, _) =>
        {
            if (!_applyingSettings)
            {
                SaveSettingsFromUi();
            }
        };
        _relayRegisterHostBox.CheckedChanged += async (_, _) =>
        {
            if (_applyingSettings)
            {
                return;
            }

            SaveSettingsFromUi();
            await ApplyRelayHostRegistrationAsync();
        };
        _relayOptimizeNetworkBox.CheckedChanged += async (_, _) =>
        {
            if (_applyingSettings || _isClosing) return;
            SaveSettingsFromUi();
            if (IsRelayConfigured())
                await OptimizeRelayNetworkAsync(CreateRelayOptions(_settings.Relay.DeviceId));
        };
        _relayNetworkOptimizer.StatusChanged += status => OnUi(() =>
        {
            _relayRouteStatusLabel.Text = status;
            AppendHostLog(status);
        });

        _hostToggleButton.Click += async (_, _) => await ToggleHostAsync();
        _restartAsAdministratorButton.Click +=
            (_, _) => RestartAsAdministrator();
        _viewerToggleButton.Click += async (_, _) => await ToggleViewerAsync();
        _discoverHostsButton.Click += async (_, _) => await DiscoverHostsAsync(silent: false);
        _diagnoseConnectionButton.Click += async (_, _) => await DiagnoseConnectionAsync();
        _editSavedDeviceRemarkButton.Click += (_, _) => EditSelectedSavedDeviceRemark();
        _deleteSavedDeviceButton.Click += (_, _) => DeleteSelectedSavedDevice();
        _discoveredHostsBox.SelectedIndexChanged += (_, _) => DiscoveredHostChanged();
        _viewerCaptureTargetBox.SelectedIndexChanged += async (_, _) => await ViewerCaptureTargetChangedAsync();
        _sendClipboardButton.Click += async (_, _) => await SendClipboardAsync();
        _readClipboardButton.Click += async (_, _) => await ReadClipboardAsync();
        _sendFileButton.Click += async (_, _) => await SendFileAsync();
        _pasteFilesButton.Click += async (_, _) => await PasteClipboardFilesFromMainAsync();
        _pullRemoteFilesButton.Click += async (_, _) => await PullRemoteClipboardFilesAsync();
        _openReceivedFilesButton.Click += (_, _) => OpenReceivedFilesDirectory();
        _remoteUpdateButton.Click += async (_, _) => await SynchronizeRemoteUpdateAsync();
        _relayConfigureButton.Click += async (_, _) =>
            await ConfigureRelayServerAsync();
        _relayRefreshButton.Click += async (_, _) =>
            await RefreshRelayDevicesAsync(silent: false);
        _relayReportAddressButton.Click += (_, _) => SetRelayStatus(
            _relayHostConnector.RequestAddressRefresh()
                ? "已请求上报本机 IP / 端口；稍后刷新在线设备即可查看。"
                : "请先启动被控端，并启用本机上线。", MutedTextColor);
        _relayAddressButton.Click += async (_, _) => await ShowRelayAddressesAsync();
        _relayConnectButton.Click += async (_, _) =>
            await ToggleRelayViewerAsync();
        _relayDevicesList.SelectedIndexChanged += (_, _) =>
            UpdateRelayActionState();
        _relayDevicesList.DoubleClick += async (_, _) =>
        {
            if (_relayConnectButton.Enabled &&
                !_viewerClient.IsConnected)
            {
                await ToggleRelayViewerAsync();
            }
        };
        _relayRefreshTimer.Tick += async (_, _) =>
        {
            if (ShouldPollRelayDirectory(
                    Visible,
                    WindowState == FormWindowState.Minimized,
                    _tabs.SelectedTab == _relayPage,
                    _relayOperationInProgress || _relayRefreshInProgress || _isClosing,
                    IsRelayConfigured))
            {
                await RefreshRelayDevicesAsync(silent: true);
            }
        };
        _tabs.SelectedIndexChanged += async (_, _) =>
        {
            if (_tabs.SelectedTab == _relayPage)
            {
                await RefreshRelayDevicesAsync(silent: true);
            }
        };
        WireViewerKeyboardShortcuts();
        KeyDown += MainForm_KeyDown;

        _hostServer.Log += AppendHostLog;
        _hostServer.RunningChanged += running => OnUi(() =>
        {
            if (!running)
            {
                _ = _relayHostConnector.StopAsync();
            }

            _hostToggleButton.Text = running ? "停止被控端" : "启动被控端";
            ApplyButtonStyle(
                _hostToggleButton,
                running ? DangerColor : PrimaryColor,
                Color.White,
                running ? DangerColor : PrimaryColor);
            SetHostStatus(
                running &&
                    _hostServer.ListeningPort > 0
                    ? $"正在监听 0.0.0.0:" +
                        $"{_hostServer.ListeningPort}"
                    : running
                        ? "正在监听"
                        : "未启动");
            _hostPortBox.Enabled = !running;
            _hostPasswordBox.Enabled = !running;
            _captureTargetBox.Enabled = !running;
            _captureScaleBox.Enabled = !running;
            _hostFpsBox.Enabled = !running;
            _jpegQualityTrack.Enabled = !running;
            _adaptiveQualityBox.Enabled = !running;
            UpdateDiscoveryPresence();
            UpdateTrayMenu();
        });
        _hostServer.ClientStatusChanged += status => OnUi(() =>
        {
            SetHostStatus(status);
            UpdateTrayMenu();
        });
        _relayHostConnector.StatusChanged += status => OnUi(() =>
        {
            AppendHostLog(status);
            SetRelayStatus(
                status,
                status.Contains("已上线", StringComparison.Ordinal)
                    ? SuccessTextColor
                    : status.Contains("校验失败", StringComparison.Ordinal) ||
                      status.Contains("注册失败", StringComparison.Ordinal)
                        ? DangerColor
                        : MutedTextColor);
        });

        _viewerClient.Log += OnViewerClientLog;
        _viewerClient.ConnectedChanged += OnViewerConnectedChanged;
        _viewerClient.DeviceInfoUpdated += OnViewerDeviceInfoReceived;
        _viewerClient.CaptureTargetsUpdated += OnViewerCaptureTargetsReceived;
        _viewerClient.CaptureTargetSelectionChanged += OnViewerCaptureTargetChanged;
        _viewerClient.CaptureTargetAvailabilityChanged +=
            OnViewerCaptureTargetAvailabilityChanged;
        _viewerClient.ClipboardStatusReceived +=
            OnViewerClipboardStatusReceived;
        _viewerClient.FileTransferStatusReceived += OnViewerFileTransferStatusReceived;
        _viewerClient.RemoteClipboardFileRequestPendingChanged += pending => OnUi(() =>
            SetRemoteFilePullPending(pending));
        _viewerClient.ConfirmRemoteClipboardFileTransfer = ConfirmRemoteClipboardFileTransfer;
    }

    private void OnViewerClientLog(string message)
    {
        OnUi(() =>
        {
            if (IsViewerReconnecting() &&
                string.Equals(
                    message,
                    "连接已断开。",
                    StringComparison.Ordinal))
            {
                return;
            }

            SetViewerStatus(
                string.Equals(
                    message,
                    "连接已断开。",
                    StringComparison.Ordinal)
                        ? ResolveDisconnectedViewerStatus(message)
                        : message,
                MutedTextColor);
        });
    }

    private void OnViewerConnectedChanged(bool connected)
    {
        OnUi(() => ApplyViewerConnectedChanged(connected));
    }

    private void ApplyViewerConnectedChanged(bool connected)
    {
        // ConnectedChanged is serialized only until each subscriber returns.
        // OnUi can enqueue the actual work. Comparing with the live transport
        // state prevents a delayed false from an older connection from closing
        // a newer live session, without dropping a true event that was queued
        // before DeviceInfo established the reconnect intent.
        bool currentConnected = _viewerClient.IsConnected;
        bool relayConnection = IsCurrentViewerRelayConnection();
        if (!ViewerReconnectPolicy.ShouldApplyConnectionEvent(
                connected,
                currentConnected))
        {
            return;
        }

        RemoteSessionRejectedException? sessionRejection =
            connected
                ? null
                : _viewerClient.LastSessionRejection;
        if (sessionRejection is not null)
        {
            // A takeover is intentional and terminal. Keeping the qualified
            // reconnect intent would let the displaced viewer reconnect and
            // immediately steal the session back from its replacement.
            CancelViewerReconnectIntent(manualDisconnect: false);
        }

        ViewerReconnectIntent? currentIntent;
        bool reconnectActive;
        lock (_viewerReconnectLock)
        {
            currentIntent = _viewerReconnectIntent;
            if (!connected && currentIntent is not null)
            {
                RecordDisconnectedQualifiedViewerConnectionLocked(
                    currentIntent);
            }

            reconnectActive = _viewerReconnectRequested ||
                _viewerReconnectConnecting ||
                _viewerReconnectTask is { IsCompleted: false };
        }

        SetViewerConnectionInputsEnabled(
            !connected &&
            !_viewerActionInProgress &&
            !reconnectActive);
        ApplyViewerCapabilityState(connected);

        if (!connected)
        {
            SetRemoteFilePullPending(false);
            _pendingViewerCaptureTargetRestore = false;
            _lastConnectedDeviceInfo = null;
            _connectedViewerCapabilities = DefaultViewerCapabilities;
            ClearViewerCaptureTargets();
            bool expectedRemoteUpdateRestart =
                ConsumeExpectedRemoteUpdateRestart(
                    Environment.TickCount64);
            bool shouldReconnect =
                ViewerReconnectPolicy.ShouldStartReconnect(
                    connected,
                    currentConnected,
                    currentIntent is not null,
                    sessionRejection is not null);
            if (shouldReconnect &&
                currentIntent is not null &&
                !_isClosing &&
                !IsDisposed)
            {
                CaptureViewerTelemetrySnapshot(
                    _viewerWindow,
                    currentIntent);
                _viewerWindow?.PrepareForReconnect();
                RequestViewerReconnect(currentIntent);
                if (expectedRemoteUpdateRestart)
                {
                    SetViewerStatus(
                        "远程更新包已发送，等待被控端重启并自动重连。",
                        SuccessTextColor);
                }
            }
            else
            {
                CloseViewerWindowFromDisconnect();
                string previousStatus = sessionRejection is not null
                    ? $"连接已结束：{sessionRejection.Message}"
                    : expectedRemoteUpdateRestart
                        ? "远程更新包已发送，等待被控端校验并重启。"
                        : _viewerStatusLabel.Text;
                string disconnectedStatus = sessionRejection is not null
                    ? previousStatus
                    : ResolveDisconnectedViewerStatus(previousStatus);
                Color disconnectedColor =
                    sessionRejection is not null
                        ? DangerColor
                    : IsExpectedRemoteUpdateRestartStatus(previousStatus)
                        ? SuccessTextColor
                        : ShouldPreserveViewerDisconnectFailure(previousStatus)
                            ? DangerColor
                            : MutedTextColor;
                SetViewerStatus(
                    disconnectedStatus,
                    disconnectedColor);
            }
        }
        else
        {
            _pendingViewerCaptureTargetRestore =
                !string.IsNullOrWhiteSpace(
                    _settings.Viewer.CaptureTargetId);
        }

        if (relayConnection)
        {
            SetRelayStatus(
                connected
                    ? "私有中继会话已建立，远控数据为端到端加密。"
                    : sessionRejection is not null
                        ? $"中继会话已结束：{sessionRejection.Message}"
                        : reconnectActive
                            ? "中继连接中断，正在自动重连..."
                            : "中继连接已断开。",
                connected
                    ? SuccessTextColor
                    : sessionRejection is not null
                        ? DangerColor
                        : MutedTextColor);
        }

        UpdateViewerActionState();
        UpdateTrayMenu();
    }

    private void RequestViewerReconnect(
        ViewerReconnectIntent intent)
    {
        bool startLoop = false;
        lock (_viewerReconnectLock)
        {
            if (!ReferenceEquals(_viewerReconnectIntent, intent) ||
                intent.Cancellation.IsCancellationRequested)
            {
                return;
            }

            _viewerReconnectRequested = true;
            if (_viewerReconnectTask is null ||
                _viewerReconnectTask.IsCompleted)
            {
                startLoop = true;
                _viewerReconnectTask = RunViewerReconnectLoopAsync(
                    intent);
            }
        }

        if (startLoop)
        {
            SetViewerConnectionInputsEnabled(false);
            UpdateViewerActionState();
        }
    }

    private async Task RunViewerReconnectLoopAsync(
        ViewerReconnectIntent intent)
    {
        CancellationToken cancellationToken =
            intent.Cancellation.Token;
        try
        {
            while (IsCurrentViewerReconnectIntent(intent))
            {
                int failedAttemptCount;
                long reconnectAttemptOrdinal;
                lock (_viewerReconnectLock)
                {
                    if (!ReferenceEquals(
                            _viewerReconnectIntent,
                            intent))
                    {
                        return;
                    }

                    failedAttemptCount =
                        intent.FailedAttemptCount;
                }

                TimeSpan delay =
                    ViewerReconnectPolicy.GetRetryDelay(
                        failedAttemptCount);
                OnUi(() =>
                {
                    if (!IsCurrentViewerReconnectIntent(intent))
                    {
                        return;
                    }

                    SetViewerStatus(
                        $"连接中断，{FormatReconnectDelay(delay)} 后自动重连；可点击“取消重连”。",
                        MutedTextColor);
                    UpdateViewerActionState();
                });
                await Task.Delay(delay, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                lock (_viewerReconnectLock)
                {
                    if (!ReferenceEquals(
                            _viewerReconnectIntent,
                            intent))
                    {
                        return;
                    }

                    _viewerReconnectRequested = false;
                    _viewerReconnectConnecting = true;
                    reconnectAttemptOrdinal =
                        ++intent.NextReconnectAttemptOrdinal;
                    intent.ActiveReconnectAttemptOrdinal =
                        reconnectAttemptOrdinal;
                }

                OnUi(() =>
                {
                    if (!IsCurrentViewerReconnectIntent(intent))
                    {
                        return;
                    }

                    SetViewerStatus(
                        FormatViewerConnectingStatus(
                            intent.Connection.Host,
                            intent.Connection.Port)
                            .Replace(
                                "正在连接",
                                "正在自动重连",
                                StringComparison.Ordinal),
                        MutedTextColor);
                    UpdateViewerActionState();
                });

                long qualifiedConnectionGeneration = long.MinValue;
                try
                {
                    if (intent.Connection.RelayRoute is { } relayRoute)
                    {
                        await _viewerClient.ConnectViaRelayAsync(
                                relayRoute,
                                intent.Connection.Password,
                                intent.Connection.VideoMode,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await _viewerClient.ConnectAsync(
                                intent.Connection.Host,
                                intent.Connection.Port,
                                intent.Connection.Password,
                                intent.Connection.VideoMode,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    qualifiedConnectionGeneration =
                        _viewerClient.InputConnectionGeneration;
                    bool deviceInfoReceived =
                        await _viewerClient
                            .WaitForCurrentDeviceInfoAsync(
                                RemoteViewerClient
                                    .DeviceInfoHandshakeTimeout,
                                cancellationToken)
                            .ConfigureAwait(false);
                    if (!deviceInfoReceived)
                    {
                        throw new TimeoutException(
                            "重连握手后未收到当前会话的设备信息。");
                    }
                }
                catch (Exception) when (
                    !IsCurrentViewerReconnectIntent(intent))
                {
                    // The reconnect intent may be cancelled while the
                    // qualification wait is still pending. If this attempt
                    // already published a connection, tear down only its
                    // generation; a global disconnect could close a newer
                    // manual connection.
                    await _viewerClient
                        .DisconnectIfCurrentGenerationAsync(
                            qualifiedConnectionGeneration)
                        .ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (
                    ViewerReconnectPolicy
                        .IsRetryableConnectionFailure(ex))
                {
                    await _viewerClient
                        .DisconnectIfCurrentGenerationAsync(
                            qualifiedConnectionGeneration)
                        .ConfigureAwait(false);
                    lock (_viewerReconnectLock)
                    {
                        if (ReferenceEquals(
                                _viewerReconnectIntent,
                                intent))
                        {
                            _viewerReconnectConnecting = false;
                            _viewerReconnectRequested = true;
                            intent.ActiveReconnectAttemptOrdinal = 0;
                            RecordViewerReconnectAttemptFailureLocked(
                                intent,
                                reconnectAttemptOrdinal);
                        }
                    }
                    OnUi(() =>
                    {
                        if (IsCurrentViewerReconnectIntent(intent))
                        {
                            SetViewerStatus(
                                $"自动重连失败：{ex.Message}",
                                MutedTextColor);
                            UpdateViewerActionState();
                        }
                    });
                    continue;
                }
                catch (Exception ex)
                {
                    await _viewerClient
                        .DisconnectIfCurrentGenerationAsync(
                            qualifiedConnectionGeneration)
                        .ConfigureAwait(false);
                    CancelViewerReconnectIntent(
                        manualDisconnect: false);
                    OnUi(() =>
                    {
                        if (!_isClosing && !IsDisposed)
                        {
                            CloseViewerWindowFromDisconnect();
                            SetViewerConnectionInputsEnabled(true);
                            SetViewerStatus(
                                $"自动重连已停止：{ex.Message}",
                                DangerColor);
                            UpdateViewerActionState();
                        }
                    });
                    return;
                }

                if (!IsCurrentViewerReconnectIntent(intent))
                {
                    await _viewerClient
                        .DisconnectIfCurrentGenerationAsync(
                            qualifiedConnectionGeneration)
                        .ConfigureAwait(false);
                    return;
                }

                bool scheduleStabilityCheck = false;
                bool disconnectedAgain;
                lock (_viewerReconnectLock)
                {
                    if (!ReferenceEquals(
                            _viewerReconnectIntent,
                            intent))
                    {
                        return;
                    }

                    _viewerReconnectConnecting = false;
                    disconnectedAgain =
                        _viewerReconnectRequested ||
                        !_viewerClient.IsConnected;
                    _viewerReconnectRequested =
                        disconnectedAgain;
                    scheduleStabilityCheck =
                        RegisterQualifiedViewerConnectionLocked(
                            intent,
                            qualifiedConnectionGeneration,
                            reconnectAttemptOrdinal);
                    intent.ActiveReconnectAttemptOrdinal = 0;
                    if (disconnectedAgain)
                    {
                        RecordDisconnectedQualifiedViewerConnectionLocked(
                            intent);
                    }
                }

                if (scheduleStabilityCheck)
                {
                    _ = RunViewerReconnectStabilityWindowAsync(
                        intent,
                        qualifiedConnectionGeneration);
                }

                if (disconnectedAgain)
                {
                    continue;
                }

                OnUi(() =>
                {
                    if (!IsCurrentViewerReconnectIntent(intent) ||
                        !_viewerClient.IsConnected)
                    {
                        return;
                    }

                    SetViewerStatus(
                        $"已自动重连 {intent.Connection.Host}:" +
                        $"{intent.Connection.Port}",
                        SuccessTextColor);
                    SetViewerConnectionInputsEnabled(false);
                    UpdateViewerActionState();
                    UpdateTrayMenu();
                });
                return;
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            bool restartLoop = false;
            lock (_viewerReconnectLock)
            {
                if (ReferenceEquals(
                        _viewerReconnectIntent,
                        intent) &&
                    !intent.Cancellation.IsCancellationRequested)
                {
                    _viewerReconnectConnecting = false;
                    restartLoop =
                        !_isClosing &&
                        (_viewerReconnectRequested ||
                            !_viewerClient.IsConnected);
                    if (restartLoop)
                    {
                        _viewerReconnectRequested = true;
                        _viewerReconnectTask =
                            RunViewerReconnectLoopAsync(intent);
                    }
                    else
                    {
                        _viewerReconnectRequested = false;
                        _viewerReconnectTask = null;
                    }
                }
            }

            OnUi(UpdateViewerActionState);
        }
    }

    private bool RegisterQualifiedViewerConnectionLocked(
        ViewerReconnectIntent intent,
        long connectionGeneration,
        long reconnectAttemptOrdinal)
    {
        if (!ReferenceEquals(
                _viewerReconnectIntent,
                intent) ||
            intent.Cancellation.IsCancellationRequested)
        {
            return false;
        }

        if (intent.QualifiedConnectionGeneration ==
            connectionGeneration)
        {
            intent.QualifiedConnectionCountsAsReconnectAttempt |=
                reconnectAttemptOrdinal > 0;
            if (reconnectAttemptOrdinal > 0)
            {
                intent.QualifiedReconnectAttemptOrdinal =
                    reconnectAttemptOrdinal;
            }
            return false;
        }

        intent.QualifiedConnectionGeneration =
            connectionGeneration;
        intent.QualifiedConnectionCountsAsReconnectAttempt =
            reconnectAttemptOrdinal > 0;
        intent.QualifiedReconnectAttemptOrdinal =
            reconnectAttemptOrdinal;
        intent.QualifiedConnectionStable = false;
        return true;
    }

    private static void
        RecordDisconnectedQualifiedViewerConnectionLocked(
            ViewerReconnectIntent intent)
    {
        if (intent.QualifiedConnectionGeneration ==
            long.MinValue)
        {
            return;
        }

        if (intent
                .QualifiedConnectionCountsAsReconnectAttempt &&
            !intent.QualifiedConnectionStable)
        {
            RecordViewerReconnectAttemptFailureLocked(
                intent,
                intent.QualifiedReconnectAttemptOrdinal);
        }

        intent.QualifiedConnectionGeneration =
            long.MinValue;
        intent.QualifiedConnectionCountsAsReconnectAttempt =
            false;
        intent.QualifiedReconnectAttemptOrdinal = 0;
        intent.QualifiedConnectionStable = false;
    }

    private static void RecordViewerReconnectAttemptFailureLocked(
        ViewerReconnectIntent intent,
        long reconnectAttemptOrdinal)
    {
        if (reconnectAttemptOrdinal <= 0 ||
            reconnectAttemptOrdinal <=
                intent.LastFailedReconnectAttemptOrdinal)
        {
            return;
        }

        intent.LastFailedReconnectAttemptOrdinal =
            reconnectAttemptOrdinal;
        intent.FailedAttemptCount =
            ViewerReconnectPolicy.RecordAttemptResult(
                intent.FailedAttemptCount,
                stableConnectionWindowCompleted: false);
    }

    private async Task RunViewerReconnectStabilityWindowAsync(
        ViewerReconnectIntent intent,
        long connectionGeneration)
    {
        try
        {
            long stableStartedAt = Stopwatch.GetTimestamp();
            while (true)
            {
                TimeSpan remaining =
                    ViewerReconnectPolicy
                        .StableConnectionWindow -
                    Stopwatch.GetElapsedTime(stableStartedAt);
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                await Task.Delay(
                        remaining,
                        intent.Cancellation.Token)
                    .ConfigureAwait(false);
            }

            lock (_viewerReconnectLock)
            {
                if (!ReferenceEquals(
                        _viewerReconnectIntent,
                        intent) ||
                    intent.Cancellation
                        .IsCancellationRequested ||
                    intent.QualifiedConnectionGeneration !=
                        connectionGeneration ||
                    !ViewerReconnectPolicy
                        .ShouldCompleteStableConnectionWindow(
                            intent.Generation,
                            _viewerReconnectIntent.Generation,
                            connectionGeneration,
                            _viewerClient
                                .InputConnectionGeneration,
                            _viewerClient.IsConnected))
                {
                    return;
                }

                intent.QualifiedConnectionStable = true;
                intent.FailedAttemptCount =
                    ViewerReconnectPolicy.RecordAttemptResult(
                        intent.FailedAttemptCount,
                        stableConnectionWindowCompleted: true);
            }
        }
        catch (OperationCanceledException) when (
            intent.Cancellation.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
            // Manual disconnect/application shutdown owns the intent now.
        }
    }

    private static string FormatReconnectDelay(
        TimeSpan delay)
    {
        return delay.TotalSeconds < 1
            ? $"{delay.TotalMilliseconds:F0} 毫秒"
            : $"{delay.TotalSeconds:F0} 秒";
    }

    private void WireViewerKeyboardShortcuts()
    {
        _viewerHostBox.KeyDown += ViewerShortcutKeyDown;
        _viewerPasswordBox.KeyDown += ViewerShortcutKeyDown;
        _viewerPortBox.KeyDown += ViewerShortcutKeyDown;
        _discoveredHostsBox.KeyDown += ViewerShortcutKeyDown;
        _discoveredHostsList.KeyDown += ViewerShortcutKeyDown;
        _viewerCaptureTargetBox.KeyDown += ViewerShortcutKeyDown;
        _viewerVideoModeBox.KeyDown += ViewerShortcutKeyDown;
    }

    private async void ViewerShortcutKeyDown(object? sender, KeyEventArgs args)
    {
        if (_isClosing || IsDisposed)
        {
            return;
        }

        if (ReferenceEquals(sender, _discoveredHostsList) &&
            !args.Control &&
            !args.Alt &&
            !args.Shift)
        {
            if (args.KeyCode == Keys.F2)
            {
                args.SuppressKeyPress = true;
                EditSelectedSavedDeviceRemark();
                return;
            }

            if (args.KeyCode == Keys.Delete)
            {
                args.SuppressKeyPress = true;
                DeleteSelectedSavedDevice();
                return;
            }
        }

        if (IsPlainEnter(args) && CanSubmitViewerFromShortcutSender(sender))
        {
            args.SuppressKeyPress = true;
            if (_viewerToggleButton.Enabled)
            {
                NormalizeViewerHostInput();
                await ToggleViewerAsync();
            }

            return;
        }

        if (args.KeyCode == Keys.F5 && !args.Control && !args.Alt && !args.Shift)
        {
            args.SuppressKeyPress = true;
            if (_discoverHostsButton.Enabled)
            {
                await DiscoverHostsAsync(silent: false);
            }

            return;
        }

        if (args.Control && args.KeyCode == Keys.D && !args.Alt && !args.Shift)
        {
            args.SuppressKeyPress = true;
            if (_diagnoseConnectionButton.Enabled)
            {
                NormalizeViewerHostInput();
                await DiagnoseConnectionAsync();
            }
        }
    }

    private async void MainForm_KeyDown(object? sender, KeyEventArgs args)
    {
        if (_isClosing || IsDisposed)
        {
            return;
        }

        if (RemoteFilePullUi.IsShortcut(args))
        {
            args.SuppressKeyPress = true;
            await PullRemoteClipboardFilesAsync();
            return;
        }

        if (!IsMainWindowFilePasteShortcut(args) ||
            IsTextEntryControl(GetFocusedControl(this)) ||
            !CanConnectedViewer(RemoteDeviceCapabilities.FileReceive))
        {
            return;
        }

        args.SuppressKeyPress = true;
        await PasteClipboardFilesFromMainAsync();
    }

    internal static bool IsPlainEnter(KeyEventArgs args)
    {
        return args.KeyCode == Keys.Enter && !args.Control && !args.Alt && !args.Shift;
    }

    internal static bool IsMainWindowFilePasteShortcut(KeyEventArgs args)
    {
        return (args.KeyCode == Keys.V && args.Control && !args.Alt && !args.Shift) ||
            (args.KeyCode == Keys.Insert && args.Shift && !args.Control && !args.Alt);
    }

    internal static bool IsTextEntryControl(Control? control)
    {
        for (Control? current = control; current is not null; current = current.Parent)
        {
            if (current is TextBoxBase or ComboBox or NumericUpDown)
            {
                return true;
            }
        }

        return false;
    }

    private static Control? GetFocusedControl(Control root)
    {
        if (root.Focused)
        {
            return root;
        }

        foreach (Control child in root.Controls)
        {
            if (child.ContainsFocus)
            {
                return GetFocusedControl(child) ?? child;
            }
        }

        return root is ContainerControl container ? container.ActiveControl : null;
    }

    private bool CanSubmitViewerFromShortcutSender(object? sender)
    {
        if (sender is ComboBox { DroppedDown: true })
        {
            return false;
        }

        return ReferenceEquals(sender, _viewerHostBox) ||
            ReferenceEquals(sender, _viewerPasswordBox) ||
            ReferenceEquals(sender, _viewerPortBox) ||
            ReferenceEquals(sender, _discoveredHostsBox) ||
            ReferenceEquals(sender, _discoveredHostsList);
    }

    private void NormalizeViewerHostInput()
    {
        string trimmed = _viewerHostBox.Text.Trim();
        if (!string.Equals(_viewerHostBox.Text, trimmed, StringComparison.Ordinal))
        {
            _viewerHostBox.Text = trimmed;
        }
    }

    private void InitializeTray()
    {
        _trayMenu = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("打开 RemoteDesk", null, (_, _) => ShowFromTray());
        _trayHostToggleItem = new ToolStripMenuItem("启动被控端", null, async (_, _) => await ToggleHostAsync());
        var exitItem = new ToolStripMenuItem("退出", null, (_, _) => ExitApplication());
        _trayMenu.Items.Add(showItem);
        _trayMenu.Items.Add(_trayHostToggleItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Text = "RemoteDesk",
            Icon = Icon ?? SystemIcons.Application,
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowFromTray();
        UpdateTrayMenu();
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs args)
    {
        if (!_allowExit &&
            args.CloseReason == CloseReason.UserClosing &&
            _minimizeToTrayBox.Checked)
        {
            args.Cancel = true;
            HideToTray(showTip: true);
            return;
        }

        _isClosing = true;
        CaptureViewerTelemetrySnapshot(_viewerWindow);
        CancelViewerReconnectIntent();
        StartExitWatchdog();
        CancelDiscoveryScan();
        _notifyIcon.Visible = false;
    }

    private void HideToTray(bool showTip)
    {
        if (_isClosing || IsDisposed)
        {
            return;
        }

        _notifyIcon.Visible = true;
        ShowInTaskbar = false;
        Hide();
        if (showTip)
        {
            _notifyIcon.ShowBalloonTip(1500, "RemoteDesk", "已收入右下角托盘，双击图标可打开。", ToolTipIcon.Info);
        }
    }

    private void ShowFromTray()
    {
        if (_isClosing || IsDisposed)
        {
            return;
        }

        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        ResponsiveWindowLayout.ApplyTo(
            this,
            logicalPreferredSize: new Size(1120, 720),
            logicalMinimumSize: new Size(640, 440),
            applyPreferredBounds: false);
        Show();
        Activate();
    }

    private void ExitApplication()
    {
        _allowExit = true;
        _isClosing = true;
        StartExitWatchdog();
        _notifyIcon.Visible = false;
        Close();
    }

    private void StartExitWatchdog()
    {
        if (Interlocked.Exchange(ref _exitWatchdogStarted, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ExitWatchdogTimeout).ConfigureAwait(false);
                if (!Environment.HasShutdownStarted)
                {
                    Environment.Exit(0);
                }
            }
            catch
            {
            }
        });
    }

    private static void TryShutdown(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or IOException or SocketException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private void UpdateTrayMenu()
    {
        if (_trayHostToggleItem is null)
        {
            return;
        }

        _trayHostToggleItem.Text = _hostServer.IsRunning ? "停止被控端" : "启动被控端";
        string status = _hostServer.IsRunning &&
            _hostServer.ListeningPort > 0
            ? $"被控端正在监听 {_hostServer.ListeningPort}"
            : _hostServer.IsRunning
                ? "被控端正在监听"
                : "软件已运行";
        _notifyIcon.Text = status.Length > 63 ? status[..63] : status;
    }

    private void StartPresenceResponder()
    {
        try
        {
            _presenceResponder.Start(
                GetDiscoveryPresence,
                GetRemoteStartPassword,
                HandleRemoteStartAsync,
                AppendHostLog);
            int discoveryPort =
                _presenceResponder.ListeningPort;
            if (discoveryPort ==
                RemotePortPolicy.FallbackDiscoveryPort)
            {
                AppendHostLog(
                    "Windows 拒绝监听旧发现端口 " +
                    $"{RemotePortPolicy.DefaultDiscoveryPort}，" +
                    "已自动改用兼容发现端口 " +
                    $"{discoveryPort}；新版查看端会同时探测两个端口。");
            }
            else
            {
                AppendHostLog(
                    "局域网发现已启动：软件运行即可被扫描到，" +
                    $"UDP 端口 {discoveryPort}。");
            }
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        {
            AppendHostLog($"局域网发现启动失败：{ex.Message}");
        }
    }

    private DiscoveryPresence GetDiscoveryPresence()
    {
        lock (_presenceLock)
        {
            int listeningPort =
                _hostServer.ListeningPort;
            return new DiscoveryPresence(
                listeningPort > 0
                    ? listeningPort
                    : _presencePort,
                _presenceCaptureTarget,
                _hostServer.IsRunning,
                _presenceAllowRemoteStart && !string.IsNullOrWhiteSpace(_presencePassword),
                RemoteDevicePlatforms.Current,
                RemoteDeviceCapabilityInfo.LocalWindows(_presenceAllowRemoteStart && !string.IsNullOrWhiteSpace(_presencePassword)),
                RemoteDeskBuildInfo.BuildStamp);
        }
    }

    private string? GetRemoteStartPassword()
    {
        lock (_presenceLock)
        {
            return _presencePassword;
        }
    }

    private async Task<RemoteStartResult> HandleRemoteStartAsync(int requestedPort, CancellationToken cancellationToken)
    {
        if (_isClosing || IsDisposed || !IsHandleCreated)
        {
            return new RemoteStartResult(false, "远程启动失败：本机正在退出。", Protocol.DefaultPort);
        }

        var completion = new TaskCompletionSource<RemoteStartResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke((Action)(async () =>
            {
                try
                {
                    if (_isClosing || IsDisposed)
                    {
                        completion.TrySetResult(new RemoteStartResult(false, "远程启动失败：本机正在退出。", Protocol.DefaultPort));
                        return;
                    }

                    RemoteStartResult result = await StartHostFromRemoteAsync(requestedPort);
                    completion.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    completion.TrySetResult(new RemoteStartResult(false, $"远程启动失败：{ex.Message}", (int)_hostPortBox.Value));
                }
            }));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return new RemoteStartResult(false, "远程启动失败：窗口不可用。", Protocol.DefaultPort);
        }

        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
    }

    private async Task<RemoteStartResult> StartHostFromRemoteAsync(int requestedPort)
    {
        if (_hostServer.IsRunning)
        {
            return new RemoteStartResult(true, "被控端已在运行。", (int)_hostPortBox.Value);
        }

        if (!_allowRemoteStartBox.Checked)
        {
            return new RemoteStartResult(false, "本机未启用远程启动。", (int)_hostPortBox.Value);
        }

        if (string.IsNullOrWhiteSpace(_hostPasswordBox.Text))
        {
            return new RemoteStartResult(false, "本机未设置被控端口令。", (int)_hostPortBox.Value);
        }

        ScreenCaptureTarget captureTarget =
            GetLowLatencyStartupCaptureTarget();
        SaveSettingsFromUi();
        HostPortStartResult portResult =
            await StartConfiguredHostAsync(
                captureTarget);

        AppendHostLog(
            "已通过局域网远程启动请求启动被控端，" +
            $"请求端口：{requestedPort}，实际监听：" +
            $"{portResult.ListeningPort}。");
        UpdateDiscoveryPresence();
        UpdateTrayMenu();
        return new RemoteStartResult(
            true,
            portResult.UsedAutomaticFallback
                ? "被控端已远程启动；旧端口被 Windows 保留，" +
                    $"已自动改用 {portResult.ListeningPort}。"
                : "被控端已远程启动。",
            portResult.ListeningPort);
    }

    private void UpdateDiscoveryPresence()
    {
        if (_hostPortBox is null || _hostPortBox.IsDisposed)
        {
            return;
        }

        lock (_presenceLock)
        {
            _presencePort = (int)_hostPortBox.Value;
            _presenceCaptureTarget = (_captureTargetBox.SelectedItem as ScreenCaptureTarget)?.DisplayName ?? string.Empty;
            _presencePassword = _hostPasswordBox.Text;
            _presenceAllowRemoteStart = _allowRemoteStartBox.Checked;
        }
    }

    private async Task ApplyStartWithWindowsFromUiAsync()
    {
        if (_applyingSettings)
        {
            return;
        }

        try
        {
            _startWithWindowsBox.Enabled = false;
            if (WindowsPersistentStartup.GetStatus().IsInstalled && !WindowsProcessElevation.IsCurrentProcessElevated())
                await WindowsPersistentStartup.ChangeAsync(_startWithWindowsBox.Checked
                    ? WindowsPersistentStartup.EnableArgument : WindowsPersistentStartup.DisableArgument);
            else
                StartupService.SetEnabled(_startWithWindowsBox.Checked);
            if (_isClosing || IsDisposed) return;
            StartupRegistrationStatus status = StartupService.GetStatus();
            _settings.App.StartWithWindows = status.IsRegistered;
            UpdateStartupRegistrationToolTip(status);
            SaveSettingsFromUi();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            System.Security.SecurityException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            if (_isClosing || IsDisposed) return;
            _applyingSettings = true;
            try
            {
                ApplyStartupRegistrationStatus(StartupService.GetStatus());
            }
            finally
            {
                _applyingSettings = false;
            }

            MessageBox.Show(this, ex.Message, "开机自启", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (!_isClosing && !IsDisposed) _startWithWindowsBox.Enabled = true;
        }
    }

    private async Task InstallPersistentStartupAsync()
    {
        _persistentStartupButton.Enabled = false;
        try
        {
            SaveSettingsFromUi();
            await WindowsPersistentStartup.ChangeAsync(WindowsPersistentStartup.InstallArgument);
            if (_isClosing || IsDisposed) return;
            _applyingSettings = true;
            try { ApplyStartupRegistrationStatus(StartupService.GetStatus()); }
            finally { _applyingSettings = false; }
            SaveSettingsFromUi();
            AppendHostLog("常驻权限已安装：下次登录自动以管理员运行。当前连接未中断；如需操作锁屏 / UAC，请另行启用“锁屏控制”。");
        }
        catch (System.ComponentModel.Win32Exception ex) when (WindowsProcessElevation.IsUserCancellation(ex))
        {
            if (!_isClosing && !IsDisposed) AppendHostLog("已取消常驻权限安装，现有被控服务保持不变。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (!_isClosing && !IsDisposed) MessageBox.Show(this, ex.Message, "常驻权限", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (!_isClosing && !IsDisposed) _persistentStartupButton.Enabled = true;
        }
    }

    private async Task ConfigureSecureDesktopAsync(bool enable)
    {
        _secureDesktopButton.Enabled = false;
        try
        {
            SaveSettingsFromUi();
            await WindowsSecureDesktopInstallation.ChangeAsync(enable);
            if (_isClosing || IsDisposed) return;
            _applyingSettings = true;
            try { ApplyStartupRegistrationStatus(StartupService.GetStatus()); }
            finally { _applyingSettings = false; }
            SaveSettingsFromUi();
            _secureDesktopButton.Text = enable ? "锁屏控制：已启用" : "锁屏控制：已停用";
            AppendHostLog(enable
                ? "锁屏控制已安装。无密码账户可直接点击登录；有密码 / PIN 时按 Windows 提示输入，不保存系统密码。" +
                  (WindowsProcessElevation.IsCurrentProcessElevated() ? "当前连接可直接使用。" : "请点击“管理员重启”使当前被控端生效。")
                : "锁屏控制已停用，普通桌面远控不受影响。");
        }
        catch (System.ComponentModel.Win32Exception ex) when (WindowsProcessElevation.IsUserCancellation(ex))
        {
            if (!_isClosing && !IsDisposed) AppendHostLog("已取消锁屏控制配置，现有连接保持不变。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (!_isClosing && !IsDisposed) MessageBox.Show(this, ex.Message, "锁屏控制", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { if (!_isClosing && !IsDisposed) _secureDesktopButton.Enabled = true; }
    }

    private async Task ToggleHostAsync()
    {
        _hostToggleButton.Enabled = false;
        try
        {
            if (_hostServer.IsRunning)
            {
                // An explicit Stop also disarms automatic listening at the next login.
                // Closing the window or updating the app does not pass through this branch.
                _autoStartHostBox.Checked = false;
                SaveSettingsFromUi();
                await _relayHostConnector.StopAsync();
                await _hostServer.StopAsync();
            }
            else
            {
                await StartHostCoreAsync();
                _autoStartHostBox.Checked = true;
                SaveSettingsFromUi();
            }
        }
        catch (Exception ex)
        {
            if (!_isClosing && !IsDisposed)
            {
                MessageBox.Show(this, ex.Message, "被控端", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            if (!_isClosing && !IsDisposed)
            {
                _hostToggleButton.Enabled = true;
            }
        }
    }

    private void RestartAsAdministrator()
    {
        if (WindowsProcessElevation
                .IsCurrentProcessElevated())
        {
            _restartAsAdministratorButton.Text =
                "已管理员运行";
            _restartAsAdministratorButton.Enabled = false;
            return;
        }

        _restartAsAdministratorButton.Enabled = false;
        try
        {
            SaveSettingsFromUi();
            using Process replacement =
                WindowsProcessElevation
                    .StartElevatedReplacement(
                        Application.ExecutablePath,
                        Environment.ProcessId);
            AppendHostLog(
                "管理员副本已启动；当前副本正在退出，" +
                "被控端会自动恢复到原监听端口。");
            ExitApplication();
        }
        catch (System.ComponentModel.Win32Exception ex)
            when (WindowsProcessElevation
                .IsUserCancellation(ex))
        {
            AppendHostLog(
                "已取消管理员重启；普通窗口仍可操作，" +
                "管理员窗口的点击和键盘会被 Windows 拦截。");
            SetHostStatus("管理员重启已取消");
            _restartAsAdministratorButton.Enabled = true;
        }
        catch (Exception ex) when (
            ex is System.ComponentModel.Win32Exception or
                IOException or
                InvalidOperationException or
                ArgumentException)
        {
            AppendHostLog(
                $"管理员重启失败：{ex.Message}");
            SetHostStatus("管理员重启失败");
            _restartAsAdministratorButton.Enabled = true;
            MessageBox.Show(
                this,
                ex.Message,
                "管理员重启",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private async Task StartHostAutomaticallyAsync(
        bool forceForUpdateRestart)
    {
        if ((!forceForUpdateRestart &&
                !_autoStartHostBox.Checked) ||
            string.IsNullOrWhiteSpace(_hostPasswordBox.Text) ||
            _hostServer.IsRunning ||
            _isClosing ||
            IsDisposed)
        {
            return;
        }

        _hostToggleButton.Enabled = false;
        try
        {
            await StartHostCoreAsync();
            AppendHostLog(
                forceForUpdateRestart
                    ? "被控端已在更新重启后恢复运行。\r\n"
                    : "被控端已自动启动。\r\n");
        }
        catch (Exception ex)
        {
            AppendHostLog($"自动启动被控端失败：{ex.Message}\r\n");
            SetHostStatus("自动启动失败");
        }
        finally
        {
            if (!_isClosing && !IsDisposed)
            {
                _hostToggleButton.Enabled = true;
            }
        }
    }

    private async Task StartHostCoreAsync()
    {
        ScreenCaptureTarget captureTarget =
            GetLowLatencyStartupCaptureTarget();
        SaveSettingsFromUi();
        await StartConfiguredHostAsync(
            captureTarget);
    }

    private async Task<HostPortStartResult>
        StartConfiguredHostAsync(
            ScreenCaptureTarget captureTarget)
    {
        int requestedPort =
            (int)_hostPortBox.Value;
        HostPortStartResult result =
            await RemotePortPolicy
                .StartHostWithAccessDeniedFallbackAsync(
                    requestedPort,
                    port => _hostServer.StartAsync(
                        port,
                        _hostPasswordBox.Text,
                        (int)_hostFpsBox.Value,
                        _jpegQualityTrack.Value,
                        GetSelectedScalePercent(),
                        captureTarget,
                        _adaptiveQualityBox.Checked));
        if (result.UsedAutomaticFallback)
        {
            _hostPortBox.Value =
                ClampToRange(
                    result.ListeningPort,
                    _hostPortBox);
            _settings.Host.Port =
                result.ListeningPort;
            SaveSettingsFromUi();
            AppendHostLog(
                $"Windows 拒绝监听已保存端口 {result.RequestedPort} " +
                "（通常由 Hyper-V/WinNAT 动态排除范围导致），" +
                $"已自动迁移到安全端口 {result.ListeningPort}；" +
                "配置、局域网发现和远程启动响应已同步更新。");
            SetHostStatus(
                $"正在监听 0.0.0.0:{result.ListeningPort}");
            UpdateDiscoveryPresence();
            UpdateTrayMenu();
        }

        await EnsureRelayHostConnectorAsync(
            result.ListeningPort);
        return result;
    }

    private async Task ConfigureRelayServerAsync()
    {
        if (_relayOperationInProgress ||
            _viewerActionInProgress || IsViewerReconnecting() || _viewerClient.IsConnected)
        {
            return;
        }

        using var dialog = new RelaySetupDialog(
            _settings.Relay);
        string? expectedFingerprint = null;
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        RelayProvisionRequest unpinnedRequest =
            dialog.CreateRequest(
                expectedSshHostKeySha256: null);
        if (string.Equals(
                unpinnedRequest.ServerAddress,
                _settings.Relay.ServerAddress,
                StringComparison.OrdinalIgnoreCase) &&
            unpinnedRequest.SshPort == _settings.Relay.SshPort)
        {
            expectedFingerprint =
                _settings.Relay.SshHostKeySha256;
        }

        RelayProvisionRequest request =
            unpinnedRequest with
            {
                ExpectedSshHostKeySha256 = expectedFingerprint
            };
        _relayOperationInProgress = true;
        _relayConfigurationGeneration++;
        _relayDevicesList.Items.Clear();
        UseWaitCursor = true;
        UpdateRelayActionState();
        SetRelayStatus(
            $"正在通过 SSH 配置 {request.ServerAddress}:{request.SshPort}，" +
            "首次安装依赖可能需要几分钟...",
            MutedTextColor);
        try
        {
            RelayProvisionResult result =
                await _relayProvisioner.ProvisionAsync(
                    request,
                    _relayOperationCancellation.Token);
            if (_isClosing || IsDisposed)
            {
                return;
            }
            _settings.Relay.ServerAddress =
                result.ServerAddress;
            _settings.Relay.RelayPort = result.RelayPort;
            _settings.Relay.SshPort = request.SshPort;
            _settings.Relay.AdminUsername =
                request.AdminUsername;
            _settings.Relay.ProtectedAccessToken =
                AppSettingsService.ProtectSecret(
                    result.AccessToken);
            _settings.Relay.TlsCertificateSha256 =
                result.TlsCertificateSha256;
            _settings.Relay.SshHostKeySha256 =
                result.SshHostKeySha256;
            _relayConfigurationGeneration++;
            _relayDevicesList.Items.Clear();
            bool settingsSaved = TrySaveSettings();
            UpdateRelayServerSummary();
            await ApplyRelayHostRegistrationAsync();
            SetRelayStatus(
                !settingsSaved
                    ? "中继服务器已配置，但本机配置未保存；请点击顶部“重试保存”。"
                    : result.Installed
                    ? "私有中继已安装并启动；已固定 SSH 与 TLS 指纹。"
                    : "私有中继配置已保存；原访问密钥继续有效。",
                settingsSaved ? SuccessTextColor : DangerColor);
            await RefreshRelayDevicesAsync(silent: true);
            if (_settingsSaveNotice.HasPendingChanges && !_isClosing && !IsDisposed)
            {
                SetRelayStatus(
                    "中继服务器已配置，但本机配置未保存；请点击顶部“重试保存”。",
                    DangerColor);
            }
        }
        catch (OperationCanceledException)
            when (_relayOperationCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or TimeoutException or
                System.Security.Authentication.AuthenticationException or
                SocketException)
        {
            SetRelayStatus(
                $"配置失败：{ex.Message}",
                DangerColor);
            if (!_isClosing && !IsDisposed)
            {
                MessageBox.Show(
                    this,
                    ex.Message,
                    "配置私有中继",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        finally
        {
            _relayOperationInProgress = false;
            if (!_isClosing && !IsDisposed)
            {
                UseWaitCursor = false;
                UpdateRelayActionState();
            }
        }
    }

    // Directory polling serves the visible list only. Host registration and its
    // heartbeat have their own lifetime and must keep running in the tray.
    internal static bool ShouldPollRelayDirectory(
        bool windowVisible,
        bool windowMinimized,
        bool relayPageSelected,
        bool operationInProgress,
        Func<bool> isRelayConfigured) =>
        windowVisible && !windowMinimized && relayPageSelected &&
        !operationInProgress && isRelayConfigured();

    private async Task RefreshRelayDevicesAsync(
        bool silent)
    {
        if (_relayRefreshInProgress ||
            _isClosing ||
            IsDisposed)
        {
            return;
        }

        RelayConnectionOptions options;
        try
        {
            options = CreateRelayOptions(
                _settings.Relay.DeviceId);
        }
        catch (InvalidOperationException)
        {
            _relayDevicesList.Items.Clear();
            UpdateRelayServerSummary();
            SetRelayStatus(
                "尚未配置公网中继服务器。",
                MutedTextColor);
            UpdateRelayActionState();
            return;
        }

        _relayRefreshInProgress = true;
        long configurationGeneration = _relayConfigurationGeneration;
        UpdateRelayActionState();
        if (!silent)
        {
            SetRelayStatus("正在读取在线设备...", MutedTextColor);
        }

        try
        {
            await OptimizeRelayNetworkAsync(options);
            if (_isClosing || IsDisposed || configurationGeneration != _relayConfigurationGeneration) return;
            IReadOnlyList<RelayOnlineDevice> devices =
                await RelayTunnelClient.ListDevicesAsync(
                    options,
                    _relayOperationCancellation.Token);
            if (_isClosing || IsDisposed || configurationGeneration != _relayConfigurationGeneration)
            {
                return;
            }
            string? selectedId = GetSelectedRelayDevice()?.DeviceId;
            _relayDevicesList.BeginUpdate();
            try
            {
                _relayDevicesList.Items.Clear();
                foreach (RelayOnlineDevice device in devices)
                {
                    ListViewItem item = CreateRelayDeviceListItem(device, _settings.Relay.DeviceId);
                    _relayDevicesList.Items.Add(item);
                    if (string.Equals(
                            device.DeviceId,
                            selectedId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        item.Selected = true;
                    }
                }
            }
            finally
            {
                _relayDevicesList.EndUpdate();
            }

            SetRelayStatus(
                devices.Count == 0
                    ? "中继可用，当前没有在线设备。请在目标电脑勾选“将这台电脑发布到在线设备列表”。"
                    : $"中继可用，共 {devices.Count} 台设备在线。",
                devices.Count == 0
                    ? MutedTextColor
                    : SuccessTextColor);
        }
        catch (OperationCanceledException)
            when (_relayOperationCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (
            ex is IOException or SocketException or
                TimeoutException or
                System.Security.Authentication.AuthenticationException)
        {
            if (_isClosing || IsDisposed || configurationGeneration != _relayConfigurationGeneration)
            {
                return;
            }
            _relayDevicesList.Items.Clear();
            SetRelayStatus(
                $"读取在线设备失败：{ex.Message}",
                DangerColor);
            if (!silent && !_isClosing && !IsDisposed)
            {
                MessageBox.Show(
                    this,
                    ex.Message,
                    "公网中继",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        finally
        {
            _relayRefreshInProgress = false;
            UpdateRelayActionState();
        }
    }

    private async Task ApplyRelayHostRegistrationAsync()
    {
        if (!_relayRegisterHostBox.Checked ||
            !_hostServer.IsRunning ||
            _hostServer.ListeningPort <= 0)
        {
            await _relayHostConnector.StopAsync();
            if (!_relayRegisterHostBox.Checked)
            {
                SetRelayStatus(
                    "本机已从中继在线列表下线。",
                    MutedTextColor);
            }
            return;
        }

        await EnsureRelayHostConnectorAsync(
            _hostServer.ListeningPort);
    }

    private async Task EnsureRelayHostConnectorAsync(
        int localHostPort)
    {
        if (!_relayRegisterHostBox.Checked)
        {
            await _relayHostConnector.StopAsync();
            return;
        }
        if (!IsRelayConfigured())
        {
            await _relayHostConnector.StopAsync();
            return;
        }

        try
        {
            RelayConnectionOptions options =
                CreateRelayOptions(
                    _settings.Relay.DeviceId);
            await OptimizeRelayNetworkAsync(options);
            if (_isClosing || IsDisposed) return;
            await _relayHostConnector.StartAsync(
                options,
                localHostPort,
                _relayOperationCancellation.Token);
        }
        catch (OperationCanceledException)
            when (_relayOperationCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or
                SocketException or
                System.Security.Authentication.AuthenticationException)
        {
            AppendHostLog(
                $"公网中继注册未启动：{ex.Message}");
            SetRelayStatus(
                $"本机中继注册失败：{ex.Message}",
                DangerColor);
        }
    }

    private async Task ToggleRelayViewerAsync()
    {
        if (IsViewerReconnecting())
        {
            await CancelViewerReconnectAsync(
                disconnectClient: true,
                "已取消自动重连。");
            SetRelayStatus("已取消自动重连。", MutedTextColor);
            return;
        }

        if (_viewerActionInProgress ||
            _relayOperationInProgress)
        {
            return;
        }

        bool disconnecting = _viewerClient.IsConnected;
        RelayOnlineDevice? selected = GetSelectedRelayDevice();
        string? blockedReason = RelayDeviceSelectionPolicy.GetConnectionBlockReason(
            selected, _settings.Relay.DeviceId);
        if (!disconnecting && blockedReason is not null)
        {
            SetRelayStatus(blockedReason, DangerColor);
            return;
        }

        _viewerActionInProgress = true;
        _viewerActionDisconnecting = disconnecting;
        SetViewerConnectionInputsEnabled(false);
        UpdateViewerActionState();
        try
        {
            if (disconnecting)
            {
                CaptureViewerTelemetrySnapshot(_viewerWindow);
                CancelViewerReconnectIntent();
                SetRelayStatus("正在断开远程连接...", MutedTextColor);
                await _viewerClient.DisconnectAsync();
                return;
            }

            SaveSettingsFromUi();
            RelayConnectionOptions route =
                CreateRelayOptions(selected!.DeviceId);
            await OptimizeRelayNetworkAsync(route);
            if (_isClosing || IsDisposed) return;
            var snapshot = new ViewerConnectionSnapshot(
                route.ServerAddress,
                route.Port,
                _relayViewerPasswordBox.Text,
                GetSelectedViewerVideoMode(
                    _relayVideoModeBox),
                route);
            if (_viewerWindow is null ||
                _viewerWindow.IsDisposed)
            {
                BeginViewerTelemetrySession();
            }

            _pendingViewerConnection =
                new PendingViewerConnection(snapshot);
            _viewerReconnectQualification.BeginManualConnection(
                snapshot);
            SetRelayStatus(
                $"正在通过中继连接 {selected!.MachineName}（端到端加密握手）...",
                MutedTextColor);
            SetViewerStatus(
                $"正在通过中继连接 {selected.MachineName}...",
                MutedTextColor);
            await _viewerClient.ConnectViaRelayAsync(
                route,
                snapshot.Password,
                snapshot.VideoMode,
                _relayOperationCancellation.Token);
            bool deviceInfoReceived =
                await _viewerClient.WaitForCurrentDeviceInfoAsync(
                    RemoteViewerClient.DeviceInfoHandshakeTimeout,
                    _relayOperationCancellation.Token);
            if (!deviceInfoReceived)
            {
                await _viewerClient.DisconnectAsync();
                throw new TimeoutException(
                    "连接握手后未收到被控端设备信息。");
            }

            ShowViewerWindow();
            SetRelayStatus(
                $"已通过私有中继连接 {selected.MachineName}。",
                SuccessTextColor);
        }
        catch (OperationCanceledException)
            when (_relayOperationCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!disconnecting)
            {
                _pendingViewerConnection = null;
            }

            if (!_isClosing && !IsDisposed)
            {
                SetRelayStatus(
                    $"连接失败：{ex.Message}",
                    DangerColor);
                SetViewerStatus(
                    $"中继连接失败：{ex.Message}",
                    DangerColor);
                MessageBox.Show(
                    this,
                    ex.Message,
                    "中继连接",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        finally
        {
            if (!_isClosing && !IsDisposed)
            {
                _viewerActionInProgress = false;
                _viewerActionDisconnecting = false;
                if (!_viewerClient.IsConnected)
                {
                    _pendingViewerConnection = null;
                }
                SetViewerConnectionInputsEnabled(
                    !_viewerClient.IsConnected);
                UpdateViewerActionState();
            }
        }
    }

    private async Task OptimizeRelayNetworkAsync(RelayConnectionOptions options)
    {
        try
        {
            await _relayNetworkOptimizer.OptimizeAsync(options,
                _relayOptimizeNetworkBox.Checked, _relayOperationCancellation.Token);
        }
        catch (OperationCanceledException) when (_relayOperationCancellation.IsCancellationRequested) { }
    }

    private RelayConnectionOptions CreateRelayOptions(
        string deviceId)
    {
        return new RelayConnectionOptions(
                _settings.Relay.ServerAddress ?? string.Empty,
                _settings.Relay.RelayPort,
                AppSettingsService.UnprotectSecret(
                    _settings.Relay.ProtectedAccessToken),
                _settings.Relay.TlsCertificateSha256 ?? string.Empty,
                deviceId)
            .Validate();
    }

    private RelayOnlineDevice? GetSelectedRelayDevice() =>
        _relayDevicesList.SelectedItems.Count == 1
            ? _relayDevicesList.SelectedItems[0].Tag
                as RelayOnlineDevice
            : null;

    private bool IsRelayConfigured()
    {
        try
        {
            _ = CreateRelayOptions(
                _settings.Relay.DeviceId);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool IsCurrentViewerRelayConnection()
    {
        lock (_viewerReconnectLock)
        {
            return _viewerReconnectIntent?.Connection.RelayRoute is not null ||
                _pendingViewerConnection?.Connection.RelayRoute is not null;
        }
    }

    private void UpdateRelayServerSummary()
    {
        if (_relayServerSummaryLabel is null ||
            _relayServerSummaryLabel.IsDisposed)
        {
            return;
        }

        if (!IsRelayConfigured())
        {
            _relayServerSummaryLabel.Text =
                "尚未配置（点击“配置/更新服务器”）";
            _relayServerSummaryLabel.ForeColor = MutedTextColor;
            UpdateRelayActionState();
            return;
        }

        _relayServerSummaryLabel.Text =
            $"{_settings.Relay.ServerAddress}:{_settings.Relay.RelayPort}  ·  TLS 已固定  ·  " +
            $"本机 ID {_settings.Relay.DeviceId[..8]}";
        _relayServerSummaryLabel.ForeColor = SuccessTextColor;
        UpdateRelayActionState();
    }

    private void SetRelayStatus(
        string status,
        Color foreColor)
    {
        _diagnosticLog.Append("RELAY", status);
        if (_relayStatusLabel is null ||
            _relayStatusLabel.IsDisposed)
        {
            return;
        }

        _relayStatusLabel.Text = status;
        _relayStatusLabel.ForeColor = foreColor;
        SetToolTip(_relayStatusLabel, status);
    }

    private async Task ToggleViewerAsync()
    {
        if (IsViewerReconnecting())
        {
            await CancelViewerReconnectAsync(
                disconnectClient: true,
                "已取消自动重连。");
            return;
        }

        if (_viewerActionInProgress)
        {
            return;
        }

        bool disconnecting =
            _viewerClient.IsConnected;
        _viewerActionInProgress = true;
        _viewerActionDisconnecting =
            disconnecting;
        SetViewerConnectionInputsEnabled(false);
        UpdateViewerActionState();
        try
        {
            if (disconnecting)
            {
                CaptureViewerTelemetrySnapshot(_viewerWindow);
                CancelViewerReconnectIntent();
                SetViewerStatus(
                    "正在断开远程连接...",
                    MutedTextColor);
                await _viewerClient.DisconnectAsync();
            }
            else
            {
                if (string.IsNullOrWhiteSpace(_viewerHostBox.Text))
                {
                    await DiscoverHostsAsync(silent: false);
                }

                SaveSettingsFromUi();
                if (!await ResolveViewerEndpointAsync()) return;
                bool remoteStartAttempted = await EnsureRemoteHostReadyAsync();
                PrepareViewerCapabilitiesForConnection();
                if (_viewerWindow is null || _viewerWindow.IsDisposed)
                {
                    BeginViewerTelemetrySession();
                }

                _pendingViewerConnection =
                    new PendingViewerConnection(
                        CaptureViewerConnectionSnapshot());
                _viewerReconnectQualification.BeginManualConnection(
                    _pendingViewerConnection.Connection);
                SetViewerStatus(
                    FormatViewerConnectingStatus(
                        _viewerHostBox.Text,
                        (int)_viewerPortBox.Value),
                    MutedTextColor);
                try
                {
                    await ConnectViewerAsync();
                }
                catch (Exception ex) when (!remoteStartAttempted && IsConnectionFailureForRemoteStartRetry(ex))
                {
                    if (!await TryDirectRemoteStartAsync())
                    {
                        throw;
                    }

                    SetViewerStatus(
                        FormatViewerConnectingStatus(
                            _viewerHostBox.Text,
                            (int)_viewerPortBox.Value),
                        MutedTextColor);
                    await ConnectViewerAsync();
                }

                ShowViewerWindow();
                RememberCurrentViewerDevice();
            }
        }
        catch (Exception ex)
        {
            if (!disconnecting)
            {
                _pendingViewerConnection = null;
            }

            if (!_isClosing && !IsDisposed)
            {
                string actionName =
                    disconnecting
                        ? "断开远程"
                        : "连接远程";
                SetViewerStatus(
                    $"{(disconnecting ? "断开" : "连接")}失败：{ex.Message}",
                    DangerColor);
                MessageBox.Show(
                    this,
                    ex.Message,
                    actionName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        finally
        {
            if (!_isClosing && !IsDisposed)
            {
                _viewerActionInProgress = false;
                _viewerActionDisconnecting =
                    false;
                if (!_viewerClient.IsConnected)
                {
                    _pendingViewerConnection = null;
                }
                SetViewerConnectionInputsEnabled(
                    !_viewerClient.IsConnected);
                UpdateViewerActionState();
            }
        }
    }

    private async Task ConnectViewerAsync()
    {
        await _viewerClient.ConnectAsync(
            _viewerHostBox.Text.Trim(),
            (int)_viewerPortBox.Value,
            _viewerPasswordBox.Text,
            GetSelectedViewerVideoMode());
        bool deviceInfoReceived =
            await _viewerClient.WaitForCurrentDeviceInfoAsync(
                RemoteViewerClient.DeviceInfoHandshakeTimeout,
                CancellationToken.None);
        if (!deviceInfoReceived)
        {
            await _viewerClient.DisconnectAsync();
            throw new TimeoutException(
                "连接握手后未收到被控端设备信息。");
        }
    }

    private ViewerConnectionSnapshot
        CaptureViewerConnectionSnapshot()
    {
        return new ViewerConnectionSnapshot(
            _viewerHostBox.Text.Trim(),
            (int)_viewerPortBox.Value,
            _viewerPasswordBox.Text,
            GetSelectedViewerVideoMode());
    }

    private void BeginViewerTelemetrySession()
    {
        lock (_viewerTelemetryLock)
        {
            _viewerTelemetryWindow = null;
            _lastViewerRenderTelemetry = null;
            _lastViewerTelemetryIntentGeneration = long.MinValue;
            _lastViewerTelemetryConnectionGeneration = long.MinValue;
            _viewerTelemetryFinalized = false;
        }

        Interlocked.Exchange(
            ref _lastAcceptedViewerConnectionGeneration,
            long.MinValue);
    }

    private long GetCurrentViewerTelemetryIntentGeneration()
    {
        lock (_viewerReconnectLock)
        {
            return _viewerReconnectIntent?.Generation ?? long.MinValue;
        }
    }

    private void RegisterViewerTelemetryConnection(
        long connectionGeneration,
        long intentGeneration)
    {
        if (connectionGeneration == long.MinValue)
        {
            return;
        }

        // DeviceInfoUpdated is raised by the client's receive loop. Keep all
        // WinForms window access on the UI thread; a queued registration is
        // still fenced by the accepted transport generation below.
        if (InvokeRequired)
        {
            if (_isClosing || IsDisposed || !IsHandleCreated)
            {
                return;
            }

            try
            {
                BeginInvoke((Action)(() =>
                    RegisterViewerTelemetryConnection(
                        connectionGeneration,
                        intentGeneration)));
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                    ObjectDisposedException)
            {
            }

            return;
        }

        if (_isClosing || IsDisposed ||
            Volatile.Read(
                ref _lastAcceptedViewerConnectionGeneration) !=
                connectionGeneration)
        {
            return;
        }

        RemoteViewerWindow? window = _viewerWindow;
        if (window is null || window.IsDisposed)
        {
            return;
        }

        long currentIntentGeneration =
            GetCurrentViewerTelemetryIntentGeneration();
        if (intentGeneration == long.MinValue)
        {
            intentGeneration = currentIntentGeneration;
        }
        else if (currentIntentGeneration != long.MinValue &&
            intentGeneration != currentIntentGeneration)
        {
            // A delayed DeviceInfo registration must not attach an old
            // reconnect intent to a newer UI lifecycle.
            return;
        }

        lock (_viewerTelemetryLock)
        {
            if (_viewerTelemetryWindow is not null &&
                !ReferenceEquals(_viewerTelemetryWindow, window))
            {
                _lastViewerRenderTelemetry = null;
                _lastViewerTelemetryIntentGeneration =
                    long.MinValue;
                _lastViewerTelemetryConnectionGeneration =
                    long.MinValue;
                _viewerTelemetryFinalized = false;
            }

            _viewerTelemetryWindow = window;
            if (intentGeneration == long.MinValue &&
                _lastViewerTelemetryIntentGeneration !=
                    long.MinValue)
            {
                intentGeneration =
                    _lastViewerTelemetryIntentGeneration;
            }

            if (_lastViewerTelemetryIntentGeneration !=
                    long.MinValue &&
                intentGeneration != long.MinValue &&
                intentGeneration <
                    _lastViewerTelemetryIntentGeneration)
            {
                return;
            }

            if (_lastViewerTelemetryConnectionGeneration !=
                    long.MinValue &&
                connectionGeneration <
                    _lastViewerTelemetryConnectionGeneration)
            {
                return;
            }

            if (_lastViewerTelemetryConnectionGeneration !=
                    connectionGeneration ||
                _lastViewerTelemetryIntentGeneration !=
                    intentGeneration)
            {
                _lastViewerTelemetryConnectionGeneration =
                    connectionGeneration;
                _lastViewerTelemetryIntentGeneration =
                    intentGeneration;
                // A new transport generation on the same window starts a
                // fresh lifecycle segment, while the cumulative snapshot is
                // retained until the next diagnostic capture.
                _viewerTelemetryFinalized = false;
            }
        }
    }

    private void CaptureViewerTelemetrySnapshot(
        RemoteViewerWindow? window,
        ViewerReconnectIntent? expectedIntent = null)
    {
        // Only the window currently owned by MainForm may publish telemetry.
        // A delayed FormClosed callback for an older window must not be tagged
        // with the newer connection's live generations.
        if (window is null ||
            !ReferenceEquals(_viewerWindow, window) ||
            window.IsDisposed)
        {
            return;
        }

        long liveIntentGeneration = expectedIntent?.Generation ??
            GetCurrentViewerTelemetryIntentGeneration();
        ViewerConnectionSnapshot? sourceConnection =
            GetViewerTelemetrySourceConnection(
                expectedIntent,
                liveIntentGeneration);
        if (sourceConnection is null)
        {
            return;
        }

        long liveConnectionGeneration = Volatile.Read(
            ref _lastAcceptedViewerConnectionGeneration);

        lock (_viewerTelemetryLock)
        {
            if (_viewerTelemetryWindow is not null &&
                !ReferenceEquals(_viewerTelemetryWindow, window))
            {
                // A new window is a new telemetry lifetime. Never merge a
                // disposed/previous window's counters into it.
                _lastViewerRenderTelemetry = null;
                _lastViewerTelemetryIntentGeneration =
                    long.MinValue;
                _lastViewerTelemetryConnectionGeneration =
                    long.MinValue;
                _viewerTelemetryFinalized = false;
            }

            _viewerTelemetryWindow = window;
            long intentGeneration = liveIntentGeneration;
            long connectionGeneration = liveConnectionGeneration;
            if (intentGeneration == long.MinValue)
            {
                intentGeneration =
                    _lastViewerTelemetryIntentGeneration;
            }

            if (connectionGeneration == long.MinValue)
            {
                connectionGeneration =
                    _lastViewerTelemetryConnectionGeneration;
            }

            // An explicitly supplied intent identifies the callback that is
            // allowed to finalize this segment. Do not let a delayed callback
            // from an older reconnect intent capture the newer window.
            if (expectedIntent is not null &&
                _lastViewerTelemetryIntentGeneration !=
                    long.MinValue &&
                _lastViewerTelemetryIntentGeneration !=
                    expectedIntent.Generation)
            {
                return;
            }

            // A window that has not yet passed the authenticated DeviceInfo
            // fence has no reliable session identity. Do not publish its
            // counters as if they belonged to a completed viewer session.
            if (connectionGeneration == long.MinValue ||
                intentGeneration == long.MinValue)
            {
                return;
            }

            bool sameIdentity =
                _lastViewerTelemetryIntentGeneration ==
                    intentGeneration &&
                _lastViewerTelemetryConnectionGeneration ==
                    connectionGeneration;
            if (_viewerTelemetryFinalized && sameIdentity)
            {
                return;
            }

            if (_lastViewerTelemetryIntentGeneration !=
                    long.MinValue &&
                intentGeneration != long.MinValue &&
                intentGeneration <
                    _lastViewerTelemetryIntentGeneration)
            {
                return;
            }

            if (_lastViewerTelemetryConnectionGeneration !=
                    long.MinValue &&
                connectionGeneration != long.MinValue &&
                connectionGeneration <
                    _lastViewerTelemetryConnectionGeneration)
            {
                return;
            }

            try
            {
                RemoteViewerRenderTelemetrySnapshot snapshot =
                    window.CollectRenderTelemetrySnapshot(
                        includeLatencyDistributions: true);
                _lastViewerRenderTelemetry = new(
                    sourceConnection.Host,
                    sourceConnection.Port,
                    sourceConnection.VideoMode,
                    intentGeneration,
                    connectionGeneration,
                    DateTimeOffset.UtcNow,
                    CloneViewerTelemetrySnapshot(snapshot),
                    IsWindowLifetimeCumulative: true);
                _lastViewerTelemetryIntentGeneration =
                    intentGeneration;
                _lastViewerTelemetryConnectionGeneration =
                    connectionGeneration;
                _viewerTelemetryFinalized = true;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                    ObjectDisposedException)
            {
                // The window may be tearing down on the UI thread. A later
                // FormClosed/Dispose callback can retry while it is alive.
            }
        }
    }

    private ViewerConnectionSnapshot?
        GetViewerTelemetrySourceConnection(
            ViewerReconnectIntent? expectedIntent,
            long intentGeneration)
    {
        if (expectedIntent is not null)
        {
            return expectedIntent.Generation == intentGeneration
                ? expectedIntent.Connection
                : null;
        }

        lock (_viewerReconnectLock)
        {
            return _viewerReconnectIntent is { } currentIntent &&
                currentIntent.Generation == intentGeneration
                    ? currentIntent.Connection
                    : null;
        }
    }

    private ViewerRenderTelemetryReport?
        GetViewerTelemetryForDiagnostics()
    {
        lock (_viewerTelemetryLock)
        {
            if (_lastViewerRenderTelemetry is not { } report)
            {
                return null;
            }

            return report with
            {
                Snapshot = CloneViewerTelemetrySnapshot(
                    report.Snapshot)
            };
        }
    }

    private static RemoteViewerRenderTelemetrySnapshot
        CloneViewerTelemetrySnapshot(
            RemoteViewerRenderTelemetrySnapshot value)
    {
        return new(
            value.DirectHardwarePresentedFrames,
            value.DirectHardwareOccludedFrames,
            value.DirectHardwareSkippedFrames,
            value.DirectHardwarePresentationFailures,
            value.DirectPresentationQualificationAttempts,
            value.DirectPresentationQualificationSuccesses,
            value.DirectPresentationQualificationFailures,
            value.JpegFallbackRequested,
            value.TotalDirectHardwareDecodeMilliseconds,
            value.TotalDirectHardwareDecodeToPresentMilliseconds,
            value.TotalDirectHardwareReceiveToPresentMilliseconds,
            value.MaximumDirectHardwareReceiveToPresentMilliseconds,
            CloneLatencyHistogram(value.DirectHardwareDecodeLatency),
            CloneLatencyHistogram(
                value.DirectHardwareDecodeToPresentLatency),
            CloneLatencyHistogram(
                value.DirectHardwareReceiveToPresentLatency),
            value.DirectHardwarePathDisabled,
            value.LastDirectHardwareFailureDetail,
            value.LastRenderedDecoderBackend);
    }

    private static FixedLatencyHistogramSnapshot CloneLatencyHistogram(
        FixedLatencyHistogramSnapshot value)
    {
        long[]? buckets = value.Buckets is { } source
            ? source.ToArray()
            : null;
        return new(
            buckets,
            value.BucketWidthMilliseconds,
            value.CumulativeMaximumMilliseconds);
    }

    private void CancelViewerReconnectIntent(
        bool manualDisconnect = true)
    {
        ViewerReconnectIntent? cancelled;
        lock (_viewerReconnectLock)
        {
            cancelled = _viewerReconnectIntent;
            _viewerReconnectIntent = null;
            _viewerReconnectTask = null;
            _viewerReconnectRequested = false;
            _viewerReconnectConnecting = false;
            ++_nextViewerReconnectGeneration;
        }

        if (manualDisconnect)
        {
            _viewerReconnectQualification
                .CancelForManualDisconnect();
        }
        else if (cancelled is not null)
        {
            _viewerReconnectQualification
                .ClearActiveIntent(
                    cancelled.Generation);
        }
        _pendingViewerConnection = null;

        TryCancelViewerReconnectIntent(cancelled);
    }

    private async Task CancelViewerReconnectAsync(
        bool disconnectClient,
        string? status)
    {
        CancelViewerReconnectIntent();
        if (disconnectClient)
        {
            try
            {
                await _viewerClient.DisconnectAsync();
            }
            catch (Exception ex) when (
                ex is IOException or
                    SocketException or
                    ObjectDisposedException or
                    InvalidOperationException)
            {
            }
        }

        if (_isClosing || IsDisposed)
        {
            return;
        }

        _viewerActionInProgress = false;
        _viewerActionDisconnecting = false;
        SetViewerConnectionInputsEnabled(true);
        ApplyViewerCapabilityState(connected: false);
        UpdateViewerActionState();
        if (!string.IsNullOrWhiteSpace(status))
        {
            SetViewerStatus(status, MutedTextColor);
        }
    }

    private bool IsViewerReconnecting()
    {
        lock (_viewerReconnectLock)
        {
            return _viewerReconnectIntent is not null &&
                (_viewerReconnectRequested ||
                    _viewerReconnectConnecting ||
                    _viewerReconnectTask is { IsCompleted: false });
        }
    }

    private bool IsCurrentViewerReconnectIntent(
        ViewerReconnectIntent intent)
    {
        lock (_viewerReconnectLock)
        {
            return ReferenceEquals(
                    _viewerReconnectIntent,
                    intent) &&
                !intent.Cancellation.IsCancellationRequested;
        }
    }

    private static void TryCancelViewerReconnectIntent(
        ViewerReconnectIntent? intent)
    {
        if (intent is null)
        {
            return;
        }

        try
        {
            intent.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void CancelAndDisposeViewerReconnectIntent(
        ViewerReconnectIntent? intent)
    {
        if (intent is null)
        {
            return;
        }

        TryCancelViewerReconnectIntent(intent);
        intent.Dispose();
    }

    private async Task<bool> EnsureRemoteHostReadyAsync()
    {
        RemoteDeviceListItem? device = GetSelectedRemoteDevice();
        if (device is null ||
            device.IsSavedOnly ||
            device.IsHostRunning ||
            !string.Equals(device.Address, _viewerHostBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!device.CanRemoteStart)
        {
            if (string.Equals(device.Platform, RemoteDevicePlatforms.Android, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("已扫描到 Android App，但手机端尚未启动被控端。请在手机上设置口令、点击“启动被控端”并授权屏幕录制后再连接。");
            }

            throw new InvalidOperationException("对方软件已运行，但未启用远程启动被控端。");
        }

        SetViewerStatus($"正在请求 {device.MachineName} 启动被控端...", MutedTextColor);
        RemoteStartResult result = await NetworkDiscoveryService.RequestRemoteStartAsync(
            device.Address,
            (int)_viewerPortBox.Value,
            _viewerPasswordBox.Text,
            TimeSpan.FromSeconds(4),
            CancellationToken.None);

        if (!result.Success)
        {
            throw new InvalidOperationException(result.Message);
        }

        _viewerPortBox.Value = ClampToRange(result.Port, _viewerPortBox);
        SetViewerStatus(result.Message, SuccessTextColor);
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        return true;
    }

    private async Task<bool> TryDirectRemoteStartAsync()
    {
        string address = _viewerHostBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(address) ||
            string.IsNullOrWhiteSpace(_viewerPasswordBox.Text))
        {
            return false;
        }

        SetViewerStatus($"正在对 {address} 定向请求远程启动...", MutedTextColor);
        try
        {
            RemoteStartResult result = await NetworkDiscoveryService.RequestRemoteStartAsync(
                address,
                (int)_viewerPortBox.Value,
                _viewerPasswordBox.Text,
                TimeSpan.FromSeconds(3),
                CancellationToken.None);

            if (!result.Success)
            {
                SetViewerStatus(result.Message, DangerColor);
                return false;
            }

            _viewerPortBox.Value = ClampToRange(result.Port, _viewerPortBox);
            SetViewerStatus(result.Message, SuccessTextColor);
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or SocketException or ObjectDisposedException)
        {
            SetViewerStatus($"定向远程启动失败：{ex.Message}", MutedTextColor);
            return false;
        }
    }

    internal static bool IsConnectionFailureForRemoteStartRetry(Exception ex)
    {
        return ex is not RemoteSessionRejectedException &&
            ex is TimeoutException or SocketException or IOException;
    }

    private async Task RefreshManualHostStatusAsync()
    {
        string address = _viewerHostBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        int port = (int)_viewerPortBox.Value;
        RemoteDeviceListItem? selectedDevice = GetSelectedRemoteDevice();
        if (selectedDevice is not null &&
            !selectedDevice.IsSavedOnly &&
            selectedDevice.Port == port &&
            string.Equals(selectedDevice.Address, address, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SetViewerStatus($"正在探测 {address}:{port} 状态...", MutedTextColor);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            IReadOnlyList<DiscoveredHost> discoveredHosts = await NetworkDiscoveryService.DiscoverAsync(
                TimeSpan.FromMilliseconds(650),
                timeout.Token,
                [address],
                hostProbePort: port);
            IReadOnlyList<DiscoveredHost> hosts = RemoveLocalDiscoveredHosts(discoveredHosts, out _);
            if (hosts.Count == 0)
            {
                return;
            }

            UpdateDiscoveredHosts(MergeDiscoveredHosts(_lastDiscoveredHosts, hosts), silent: true);
            RemoteDeviceListItem? refreshedDevice = GetSelectedRemoteDevice();
            if (refreshedDevice is not null &&
                !refreshedDevice.CanOpenViewerNow &&
                refreshedDevice.Port == port &&
                string.Equals(refreshedDevice.Address, address, StringComparison.OrdinalIgnoreCase))
            {
                SetViewerStatus(refreshedDevice.GetBlockedActionHint(), DangerColor);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
        {
        }
    }

    private async Task DiagnoseConnectionAsync()
    {
        if (_viewerClient.IsConnected || _isClosing || IsDisposed)
        {
            return;
        }

        string address = _viewerHostBox.Text.Trim();
        int port = (int)_viewerPortBox.Value;
        IReadOnlyList<DiscoveredHost> hosts = Array.Empty<DiscoveredHost>();
        IReadOnlyList<RemoteDeskTcpProbeResult> tcpProbeResults = Array.Empty<RemoteDeskTcpProbeResult>();

        _diagnoseConnectionButton.Enabled = false;
        _discoverHostsButton.Enabled = false;
        SetViewerStatus("正在诊断连接环境...", MutedTextColor);

        try
        {
            if (!string.IsNullOrWhiteSpace(address))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                IReadOnlyList<DiscoveredHost> discoveredHosts = await NetworkDiscoveryService.DiscoverAsync(
                    TimeSpan.FromMilliseconds(900),
                    timeout.Token,
                    [address],
                    hostProbePort: port);
                hosts = RemoveLocalDiscoveredHosts(discoveredHosts, out _);
                DiscoveredHost? compatibleTarget =
                    ConnectionDiagnostics.FindTargetHost(
                        address,
                        port,
                        hosts);
                if (compatibleTarget is not null &&
                    RemotePortPolicy
                        .AreCompatibleHostPorts(
                            port,
                            compatibleTarget.Port))
                {
                    port =
                        compatibleTarget.Port;
                }

                IReadOnlyList<IPAddress> probeAddresses = await NetworkDiscoveryService.ResolveDirectProbeAddressesAsync(
                    [address],
                    timeout.Token);
                tcpProbeResults = await NetworkDiscoveryService.ProbeRemoteDeskTcpAsync(
                    probeAddresses,
                    port,
                    TimeSpan.FromMilliseconds(900),
                    timeout.Token);
                if (hosts.Count > 0)
                {
                    UpdateDiscoveredHosts(MergeDiscoveredHosts(_lastDiscoveredHosts, hosts), silent: true);
                }
            }
            else
            {
                hosts = RemoveLocalDiscoveredHosts(_lastDiscoveredHosts, out _);
            }

            string report = ConnectionDiagnostics.BuildReport(
                address,
                port,
                GetSelectedViewerVideoMode(),
                FfmpegH264Decoder.AvailablePath,
                NetworkUtils.GetLocalIPv4Addresses(),
                hosts,
                tcpProbeResults,
                hasNativeHardwareDecoder:
                    MediaFoundationD3D11H264Decoder
                        .IsPlatformPotentiallySupported,
                renderTelemetry:
                    GetViewerTelemetryForDiagnostics());
            SetViewerStatus(report, GetDiagnosticStatusColor(report));
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
        {
            string report = ConnectionDiagnostics.BuildReport(
                address,
                port,
                GetSelectedViewerVideoMode(),
                FfmpegH264Decoder.AvailablePath,
                NetworkUtils.GetLocalIPv4Addresses(),
                Array.Empty<DiscoveredHost>(),
                tcpProbeResults,
                hasNativeHardwareDecoder:
                    MediaFoundationD3D11H264Decoder
                        .IsPlatformPotentiallySupported,
                renderTelemetry:
                    GetViewerTelemetryForDiagnostics());
            SetViewerStatus($"{report}{Environment.NewLine}探测异常：{ex.Message}", DangerColor);
        }
        finally
        {
            if (!_isClosing &&
                !IsDisposed &&
                !_viewerClient.IsConnected &&
                !_viewerActionInProgress)
            {
                _diagnoseConnectionButton.Enabled = true;
                _discoverHostsButton.Enabled = true;
            }
        }
    }

    private static Color GetDiagnosticStatusColor(string report)
    {
        return report.Contains("未发现该地址", StringComparison.Ordinal) ||
            report.Contains(
                "更新显卡驱动、安装 ffmpeg",
                StringComparison.Ordinal) ||
            report.Contains("未读取到可用 IPv4", StringComparison.Ordinal) ||
            report.Contains("目标未解析到 IPv4", StringComparison.Ordinal) ||
            report.Contains("端口打开但不是 RemoteDesk", StringComparison.Ordinal) ||
            report.Contains("连接失败", StringComparison.Ordinal) ||
            report.Contains("超时", StringComparison.Ordinal)
            ? DangerColor
            : MutedTextColor;
    }

    private void ShowViewerWindow()
    {
        if (_viewerWindow is not null && !_viewerWindow.IsDisposed)
        {
            _viewerWindow.Activate();
            return;
        }

        string host = _viewerHostBox.Text.Trim();
        string title = $"RemoteDesk - {host}:{(int)_viewerPortBox.Value}";
        string remotePlatform =
            GetCurrentRemotePlatform();
        var viewerWindow = new RemoteViewerWindow(
            _viewerClient,
            title,
            CanConnectedViewer(RemoteDeviceCapabilities.InputControl),
            CanConnectedViewer(RemoteDeviceCapabilities.ClipboardText),
            CanConnectedViewer(RemoteDeviceCapabilities.FileReceive),
            CanConnectedViewer(RemoteDeviceCapabilities.FileDropPaste),
            CanConnectedViewer(RemoteDeviceCapabilities.FileSend),
            string.Equals(
                remotePlatform,
                RemoteDevicePlatforms.Android,
                StringComparison.OrdinalIgnoreCase));
        viewerWindow.SetRemotePlatform(
            remotePlatform);
        viewerWindow.SetCaptureTargets(
            _viewerCaptureTargetBox.Items
                .OfType<CaptureTargetInfo>()
                .ToArray(),
            (_viewerCaptureTargetBox.SelectedItem as
                CaptureTargetInfo)?.Id);
        viewerWindow.SetCaptureTargetSelectionEnabled(
            CanConnectedViewer(
                RemoteDeviceCapabilities
                    .CaptureTargetSelection));
        _viewerWindow = viewerWindow;
        RegisterViewerTelemetryConnection(
            Volatile.Read(
                ref _lastAcceptedViewerConnectionGeneration),
            GetCurrentViewerTelemetryIntentGeneration());
        viewerWindow.FormClosed += async (_, _) => await ViewerWindowClosedAsync(viewerWindow);
        viewerWindow.Show(this);
    }

    private async Task ViewerWindowClosedAsync(RemoteViewerWindow closedWindow)
    {
        // FormClosed can be delivered after MainForm has already replaced the
        // viewer window. Such a stale callback must not clear a newer
        // telemetry session, cancel its reconnect intent, or disconnect its
        // transport.
        if (!ReferenceEquals(_viewerWindow, closedWindow))
        {
            return;
        }

        CaptureViewerTelemetrySnapshot(closedWindow);
        _viewerWindow = null;

        if (!closedWindow.ClosedFromDisconnect &&
            !_isClosing &&
            !IsDisposed)
        {
            CancelViewerReconnectIntent();
        }

        if (!ShouldDisconnectViewerAfterWindowClosed(
            closedWindow.ClosedFromDisconnect,
            _isClosing,
            IsDisposed,
            _viewerClient.IsConnected))
        {
            return;
        }

        SetViewerStatus("远程窗口已关闭，正在断开连接...", MutedTextColor);
        UpdateViewerActionState();
        try
        {
            await _viewerClient.DisconnectAsync();
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            if (!_isClosing && !IsDisposed)
            {
                SetViewerStatus($"远程窗口关闭后断开失败：{ex.Message}", DangerColor);
            }
        }
    }

    internal static bool ShouldDisconnectViewerAfterWindowClosed(
        bool closedFromDisconnect,
        bool isClosing,
        bool isDisposed,
        bool viewerConnected)
    {
        return viewerConnected &&
            !closedFromDisconnect &&
            !isClosing &&
            !isDisposed;
    }

    private string GetCurrentRemotePlatform()
    {
        if (!string.IsNullOrWhiteSpace(
                _lastConnectedDeviceInfo?.Platform))
        {
            return RemoteDevicePlatforms.Normalize(
                _lastConnectedDeviceInfo.Platform,
                RemoteDevicePlatforms.Windows);
        }

        RemoteDeviceListItem? selectedDevice = GetSelectedRemoteDevice();
        return selectedDevice is not null &&
            string.Equals(selectedDevice.Address, _viewerHostBox.Text.Trim(), StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(selectedDevice.Platform)
                ? RemoteDevicePlatforms.Normalize(
                    selectedDevice.Platform,
                    RemoteDevicePlatforms.Windows)
                : RemoteDevicePlatforms.Windows;
    }

    private void CloseViewerWindowFromDisconnect()
    {
        RemoteViewerWindow? window = _viewerWindow;
        if (window is null || window.IsDisposed)
        {
            return;
        }

        CaptureViewerTelemetrySnapshot(window);
        _viewerWindow = null;
        try
        {
            window.CloseAfterDisconnect();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private async Task DiscoverHostsAsync(bool silent)
    {
        if (_viewerClient.IsConnected || _isClosing || IsDisposed)
        {
            return;
        }

        var scanCancellation = new CancellationTokenSource();
        CancellationTokenSource? previousScan = _discoveryScanCancellation;
        _discoveryScanCancellation = scanCancellation;
        try
        {
            previousScan?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _discoverHostsButton.Enabled = false;
        if (!silent)
        {
            SetViewerStatus("正在查找附近设备和已保存地址的实际端口...", MutedTextColor);
        }

        try
        {
            int hostProbePort = (int)_viewerPortBox.Value;
            IReadOnlyList<DiscoveredHost> discoveredHosts = await NetworkDiscoveryService.DiscoverAsync(
                TimeSpan.FromMilliseconds(900),
                scanCancellation.Token,
                hostProbePort: hostProbePort,
                directTargets: GetDiscoveryProbeTargets(),
                includeDirectedTcpProbes: false);
            IReadOnlyList<DiscoveredHost> hosts = RemoveLocalDiscoveredHosts(discoveredHosts, out int ignoredLocalHosts);

            if (scanCancellation.IsCancellationRequested || _isClosing || IsDisposed)
            {
                return;
            }

            UpdateDiscoveredHosts(hosts, silent);
            if (!silent)
            {
                SetViewerStatus(
                    GetDiscoveryStatus(hosts.Count, ignoredLocalHosts),
                    hosts.Count == 0 ? MutedTextColor : SuccessTextColor);
            }
        }
        catch (OperationCanceledException) when (scanCancellation.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (scanCancellation.IsCancellationRequested || _isClosing || IsDisposed)
        {
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                SetViewerStatus($"扫描失败：{ex.Message}", DangerColor);
            }
        }
        finally
        {
            bool isCurrentScan = ReferenceEquals(_discoveryScanCancellation, scanCancellation);
            if (isCurrentScan)
            {
                _discoveryScanCancellation = null;
            }

            scanCancellation.Dispose();
            if (isCurrentScan &&
                !_isClosing &&
                !IsDisposed &&
                !_viewerClient.IsConnected &&
                !_viewerActionInProgress)
            {
                _discoverHostsButton.Enabled = true;
            }
        }
    }

    private static IReadOnlyList<DiscoveredHost> RemoveLocalDiscoveredHosts(
        IReadOnlyList<DiscoveredHost> hosts,
        out int ignoredLocalHosts)
    {
        var localAddresses = NetworkUtils.GetLocalIPv4Addresses()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remoteHosts = new List<DiscoveredHost>(hosts.Count);
        ignoredLocalHosts = 0;

        foreach (DiscoveredHost host in hosts)
        {
            if (IsLocalDiscoveredHost(host, localAddresses))
            {
                ignoredLocalHosts++;
                continue;
            }

            remoteHosts.Add(host);
        }

        return remoteHosts;
    }

    private static bool IsLocalDiscoveredHost(DiscoveredHost host, HashSet<string> localAddresses)
    {
        return NetworkUtils.IsLikelyLocalEndpoint(host.Address, host.MachineName, localAddresses);
    }

    private static string GetDiscoveryStatus(int remoteHostCount, int ignoredLocalHostCount)
    {
        if (remoteHostCount > 0 && ignoredLocalHostCount > 0)
        {
            return $"发现 {remoteHostCount} 台远端 RemoteDesk，已忽略本机 {ignoredLocalHostCount} 条。";
        }

        if (remoteHostCount > 0)
        {
            return $"发现 {remoteHostCount} 台远端 RemoteDesk。";
        }

        if (ignoredLocalHostCount > 0)
        {
            return "只发现本机，已忽略；请确认对方软件已打开并允许防火墙访问。";
        }

        return "未发现其他 RemoteDesk，请确认对方软件已打开并允许防火墙访问。";
    }

    private IReadOnlyList<DiscoveryProbeTarget> GetDiscoveryProbeTargets()
    {
        var localAddresses = NetworkUtils.GetLocalIPv4Addresses()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return BuildDiscoveryProbeTargets(
            _viewerHostBox.Text,
            (int)_viewerPortBox.Value,
            GetRecentDevices(),
            localAddresses);
    }

    internal static IReadOnlyList<DiscoveryProbeTarget> BuildDiscoveryProbeTargets(
        string? manualAddress,
        int manualPort,
        IEnumerable<SavedRemoteDevice>? recentDevices,
        IReadOnlySet<string> localAddresses)
    {
        var targets = new List<DiscoveryProbeTarget>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddTarget(string? address, int port, string? machineName)
        {
            if (targets.Count >= MaxDiscoveryProbeTargets ||
                string.IsNullOrWhiteSpace(address) ||
                port <= 0 ||
                port > IPEndPoint.MaxPort)
            {
                return;
            }

            string trimmedAddress = address.Trim();
            if (NetworkUtils.IsLikelyLocalEndpoint(trimmedAddress, machineName ?? trimmedAddress, localAddresses))
            {
                return;
            }

            string key = MakeDeviceKey(trimmedAddress, port);
            if (!seenKeys.Add(key))
            {
                return;
            }

            targets.Add(new DiscoveryProbeTarget(trimmedAddress, port));
        }

        AddTarget(manualAddress, manualPort, null);
        if (recentDevices is null)
        {
            return targets;
        }

        foreach (SavedRemoteDevice device in recentDevices
            .Where(IsValidSavedRemoteDevice)
            .OrderByDescending(device => device.LastConnectedAt))
        {
            AddTarget(device.Address, device.Port, device.MachineName);
        }

        return targets;
    }

    private void CancelDiscoveryScan()
    {
        try
        {
            _discoveryScanCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void UpdateDiscoveredHosts(IReadOnlyList<DiscoveredHost> hosts, bool silent)
    {
        _lastDiscoveredHosts = hosts;
        IReadOnlyList<RemoteDeviceListItem> devices = BuildRemoteDeviceList(hosts);
        string currentAddress = _viewerHostBox.Text.Trim();
        int currentPort =
            (int)_viewerPortBox.Value;
        RemoteDeviceListItem? previous = GetSelectedRemoteDevice();
        string? selectedIdentity = _viewerClient.IsConnected ? _lastConnectedDeviceInfo?.DeviceId :
            previous is not null && previous.Port == currentPort &&
            string.Equals(previous.Address, currentAddress, StringComparison.OrdinalIgnoreCase) ? previous.DeviceId : null;
        RemoteDeviceListItem? selected = devices.FirstOrDefault(device => RemoteDeviceIdentity.Same(device.DeviceId, selectedIdentity)) ??
            devices.FirstOrDefault(device => device.Port == currentPort &&
            string.Equals(device.Address, currentAddress, StringComparison.OrdinalIgnoreCase)) ?? devices.FirstOrDefault(device =>
            string.Equals(device.Address, currentAddress, StringComparison.OrdinalIgnoreCase)) ?? devices.FirstOrDefault();

        _updatingDiscoveredHosts = true;
        _discoveredHostsBox.BeginUpdate();
        _discoveredHostsList.BeginUpdate();
        try
        {
            _discoveredHostsBox.Items.Clear();
            _discoveredHostsList.Items.Clear();
            foreach (RemoteDeviceListItem device in devices)
            {
                _discoveredHostsBox.Items.Add(device);
                _discoveredHostsList.Items.Add(device);
            }

            if (selected is not null)
            {
                _discoveredHostsBox.SelectedItem = selected;
                _discoveredHostsList.SelectedItem = selected;
            }
        }
        finally
        {
            _discoveredHostsList.EndUpdate();
            _discoveredHostsBox.EndUpdate();
            _updatingDiscoveredHosts = false;
        }

        if (selected is not null &&
            (string.IsNullOrWhiteSpace(currentAddress) || !silent))
        {
            ApplyRemoteDevice(selected);
        }

        UpdateSavedDeviceActionState();
    }

    internal static bool
        ShouldAdoptCompatibleDiscoveredPort(
            string? currentAddress,
            int currentPort,
            string? discoveredAddress,
            int discoveredPort,
            bool isSavedOnly)
    {
        return !isSavedOnly &&
            !string.IsNullOrWhiteSpace(
                currentAddress) &&
            string.Equals(
                currentAddress.Trim(),
                discoveredAddress?.Trim(),
                StringComparison.OrdinalIgnoreCase) &&
            RemotePortPolicy.AreCompatibleHostPorts(
                currentPort,
                discoveredPort);
    }

    internal static bool
        MigrateCompatibleRecentDevicePorts(
            IList<SavedRemoteDevice> recentDevices,
            IReadOnlyList<DiscoveredHost> discoveredHosts)
    {
        ArgumentNullException.ThrowIfNull(recentDevices);
        ArgumentNullException.ThrowIfNull(discoveredHosts);

        bool changed = false;
        foreach (SavedRemoteDevice device in
            recentDevices.ToArray())
        {
            if (string.IsNullOrWhiteSpace(
                    device.Address) ||
                discoveredHosts.Any(host =>
                    string.Equals(
                        host.Address,
                        device.Address,
                        StringComparison.OrdinalIgnoreCase) &&
                    host.Port == device.Port))
            {
                continue;
            }

            DiscoveredHost? compatibleHost =
                discoveredHosts
                    .Where(host =>
                        !RemoteDeviceIdentity.Conflicts(device.DeviceId, host.DeviceId) &&
                        string.Equals(
                            host.Address,
                            device.Address,
                            StringComparison.OrdinalIgnoreCase) &&
                        RemotePortPolicy
                            .AreCompatibleHostPorts(
                                device.Port,
                                host.Port))
                    .OrderByDescending(host =>
                        host.IsHostRunning)
                    .FirstOrDefault();
            if (compatibleHost is null)
            {
                continue;
            }

            device.Port =
                compatibleHost.Port;
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        var retainedDevices = new List<SavedRemoteDevice>();
        for (int index = 0;
             index < recentDevices.Count;)
        {
            SavedRemoteDevice device =
                recentDevices[index];
            SavedRemoteDevice? retainedDevice = retainedDevices.FirstOrDefault(item => SameSavedMachine(item, device));
            if (retainedDevice is null)
            {
                retainedDevices.Add(device);
                index++;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(
                        retainedDevice.Remark) &&
                    !string.IsNullOrWhiteSpace(
                        device.Remark))
                {
                    retainedDevice.Remark =
                        AppSettingsService
                            .NormalizeSavedDeviceRemark(
                                device.Remark);
                }

                recentDevices.RemoveAt(
                    index);
            }
        }

        return true;
    }

    private void DiscoveredHostChanged()
    {
        if (_updatingDiscoveredHosts || _discoveredHostsBox.SelectedItem is not RemoteDeviceListItem device)
        {
            return;
        }

        ApplyRemoteDevice(device);
        SelectRemoteDeviceInList(device);
        SaveSettingsFromUi();
        UpdateSavedDeviceActionState();
    }

    private void DiscoveredHostListChanged()
    {
        if (_updatingDiscoveredHosts || _discoveredHostsList.SelectedItem is not RemoteDeviceListItem device)
        {
            return;
        }

        _updatingDiscoveredHosts = true;
        try
        {
            _discoveredHostsBox.SelectedItem = device;
        }
        finally
        {
            _updatingDiscoveredHosts = false;
        }

        ApplyRemoteDevice(device);
        SaveSettingsFromUi();
        UpdateSavedDeviceActionState();
    }

    private void ApplyRemoteDevice(RemoteDeviceListItem device)
    {
        _selectedHistoryDevice = FindSavedForDiscovery(device.Address, device.Port, device.DeviceId);
        _viewerHostBox.Text = device.Address;
        _viewerPortBox.Value = ClampToRange(device.Port, _viewerPortBox);
        if (_selectedHistoryDevice is not null)
        {
            if (_selectedHistoryDevice.ProtectedPassword is not null)
                _viewerPasswordBox.Text = AppSettingsService.UnprotectSecret(_selectedHistoryDevice.ProtectedPassword);
            _viewerAutoPortBox.Checked = _selectedHistoryDevice.AutoDetectPort;
        }
        SetViewerStatus($"已选择 {device.DisplayName} ({device.Address}:{device.Port})，{device.GetLongStatus()}", MutedTextColor);
        UpdateViewerActionState();
    }

    private void SelectRemoteDeviceInList(RemoteDeviceListItem device)
    {
        _updatingDiscoveredHosts = true;
        try
        {
            _discoveredHostsList.SelectedItem = device;
        }
        finally
        {
            _updatingDiscoveredHosts = false;
        }

        UpdateSavedDeviceActionState();
    }

    private void UpdateSavedDeviceActionState()
    {
        ListBox? deviceList =
            _discoveredHostsList;
        RemoteDeviceListItem? selected =
            deviceList?.SelectedItem as
                RemoteDeviceListItem;
        bool enabled =
            selected?.IsSaved == true &&
            deviceList?.Enabled == true &&
            !_viewerClient.IsConnected &&
            !_viewerActionInProgress;

        if (_editSavedDeviceRemarkButton is not null)
        {
            _editSavedDeviceRemarkButton.Enabled = enabled;
        }

        if (_deleteSavedDeviceButton is not null)
        {
            _deleteSavedDeviceButton.Enabled = enabled;
        }

        if (_editSavedDeviceRemarkMenuItem is not null)
        {
            _editSavedDeviceRemarkMenuItem.Enabled = enabled;
        }

        if (_deleteSavedDeviceMenuItem is not null)
        {
            _deleteSavedDeviceMenuItem.Enabled = enabled;
        }
    }

    private void EditSelectedSavedDeviceRemark()
    {
        if (_viewerClient.IsConnected ||
            _viewerActionInProgress ||
            _discoveredHostsList.SelectedItem is not
                RemoteDeviceListItem { IsSaved: true } device ||
            !TryPromptForSavedDeviceRemark(
                device,
                out string? remark))
        {
            return;
        }

        if (!UpdateSavedDeviceRemark(
                GetRecentDevices(),
                device.Address,
                device.Port,
                remark,
                device.DeviceId))
        {
            SetViewerStatus(
                "未找到对应的保存记录，请重新扫描后再试。",
                DangerColor);
            UpdateDiscoveredHosts(
                _lastDiscoveredHosts,
                silent: true);
            return;
        }

        UpdateDiscoveredHosts(
            _lastDiscoveredHosts,
            silent: true);
        ReselectDisplayedRemoteDevice(
            device.Address,
            device.Port,
            device.DeviceId);
        if (!TrySaveSettings())
        {
            SetViewerStatus("备注更改尚未保存；当前仅本次运行有效，请点击顶部“重试保存”。", DangerColor);
            return;
        }
        string message = remark is null
            ? $"已清除 {device.MachineName} 的备注。"
            : $"已将 {device.MachineName} 的备注改为“{remark}”。";
        SetViewerStatus(message, SuccessTextColor);
    }

    private void DeleteSelectedSavedDevice()
    {
        if (_viewerClient.IsConnected ||
            _viewerActionInProgress ||
            _discoveredHostsList.SelectedItem is not
                RemoteDeviceListItem { IsSaved: true } device)
        {
            return;
        }

        DialogResult confirmation = MessageBox.Show(
            this,
            $"确定删除这个保存记录吗？\n\n" +
            $"{device.DisplayName}\n" +
            $"{device.Address}:{device.Port}\n\n" +
            "这不会断开或卸载远端软件；设备在线时仍可能被局域网扫描发现。",
            "删除保存的设备",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes)
        {
            return;
        }

        if (!RemoveSavedDevice(
                GetRecentDevices(),
                device.Address,
                device.Port,
                device.DeviceId))
        {
            SetViewerStatus(
                "未找到对应的保存记录，请重新扫描后再试。",
                DangerColor);
            UpdateDiscoveredHosts(
                _lastDiscoveredHosts,
                silent: true);
            return;
        }

        UpdateDiscoveredHosts(
            _lastDiscoveredHosts,
            silent: true);
        ReselectDisplayedRemoteDevice(
            device.Address,
            device.Port,
            device.DeviceId);
        if (!TrySaveSettings())
        {
            SetViewerStatus("删除记录尚未保存；当前仅本次运行有效，请点击顶部“重试保存”。", DangerColor);
            return;
        }
        SetViewerStatus(
            $"已删除 {device.DisplayName} 的保存记录。",
            SuccessTextColor);
    }

    private bool TryPromptForSavedDeviceRemark(
        RemoteDeviceListItem device,
        out string? remark)
    {
        using var dialog = new Form
        {
            Text = "修改设备备注",
            ClientSize = new Size(440, 184),
            MinimumSize = new Size(380, 210),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            BackColor = AppBackColor,
            Font = Font,
            Icon = Icon
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(18),
            BackColor = AppBackColor
        };
        layout.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(
            new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(
            new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(
            new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(
            new RowStyle(SizeType.AutoSize));

        var deviceLabel = new Label
        {
            AutoSize = true,
            Text = $"{device.MachineName}  ·  {device.Address}:{device.Port}",
            ForeColor = MutedTextColor,
            Margin = new Padding(0, 0, 0, 10),
            UseMnemonic = false
        };
        var input = new TextBox
        {
            Dock = DockStyle.Top,
            Text = device.Remark ?? string.Empty,
            MaxLength = AppSettingsService
                .MaxSavedDeviceRemarkLength,
            AccessibleName = "设备备注",
            PlaceholderText = "例如：办公室主机（留空可清除）",
            Margin = new Padding(0)
        };
        StyleInput(input);
        input.Margin = new Padding(0);
        var hint = new Label
        {
            AutoSize = true,
            Text = $"最多 {AppSettingsService.MaxSavedDeviceRemarkLength} 个字符。",
            ForeColor = MutedTextColor,
            Margin = new Padding(0, 7, 0, 0),
            UseMnemonic = false
        };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 14, 0, 0)
        };
        Button saveButton = CreatePrimaryButton("保存");
        Button cancelButton = CreateSecondaryButton("取消");
        saveButton.DialogResult = DialogResult.OK;
        cancelButton.DialogResult = DialogResult.Cancel;
        saveButton.Margin = new Padding(8, 0, 0, 0);
        cancelButton.Margin = new Padding(0);
        actions.Controls.Add(saveButton);
        actions.Controls.Add(cancelButton);

        layout.Controls.Add(deviceLabel, 0, 0);
        layout.Controls.Add(input, 0, 1);
        layout.Controls.Add(hint, 0, 2);
        layout.Controls.Add(actions, 0, 3);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = saveButton;
        dialog.CancelButton = cancelButton;
        dialog.Shown += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            remark = null;
            return false;
        }

        remark = AppSettingsService
            .NormalizeSavedDeviceRemark(input.Text);
        return true;
    }

    private void ReselectDisplayedRemoteDevice(
        string address,
        int port,
        string? deviceId = null)
    {
        RemoteDeviceListItem? match =
            _discoveredHostsList.Items
                .OfType<RemoteDeviceListItem>()
                .FirstOrDefault(device =>
                    RemoteDeviceIdentity.Normalize(deviceId) is not null ? RemoteDeviceIdentity.Same(device.DeviceId, deviceId) :
                    string.Equals(
                        device.Address,
                        address,
                        StringComparison.OrdinalIgnoreCase) &&
                    device.Port == port);
        _updatingDiscoveredHosts = true;
        try
        {
            _discoveredHostsBox.SelectedItem = match;
            _discoveredHostsList.SelectedItem = match;
        }
        finally
        {
            _updatingDiscoveredHosts = false;
        }

        UpdateSavedDeviceActionState();
    }

    private IReadOnlyList<RemoteDeviceListItem> BuildRemoteDeviceList(IReadOnlyList<DiscoveredHost> hosts)
    {
        return BuildRemoteDeviceList(hosts, GetRecentDevices(), NetworkUtils.GetLocalIPv4Addresses()
            .ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<RemoteDeviceListItem> BuildRemoteDeviceList(IReadOnlyList<DiscoveredHost> hosts,
        IReadOnlyList<SavedRemoteDevice> recentDevices, IReadOnlySet<string> localAddresses)
    {
        var devices = new List<RemoteDeviceListItem>(hosts.Count + recentDevices.Count);
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SavedRemoteDevice[] savedDevices = recentDevices
            .Where(IsValidSavedRemoteDevice)
            .Where(device => !IsLocalSavedRemoteDevice(device, localAddresses))
            .ToArray();

        foreach (DiscoveredHost host in CollapseDiscoveryAliases(hosts))
        {
            SavedRemoteDevice? savedDevice = FindSavedForDiscovery(savedDevices, host.Address, host.Port, host.DeviceId);
            devices.Add(RemoteDeviceListItem.FromDiscovered(host, savedDevice));
            seenKeys.Add(MakeDeviceListKey(host.Address, host.Port, host.DeviceId ?? savedDevice?.DeviceId));
            if (savedDevice is not null) seenKeys.Add(MakeDeviceListKey(savedDevice.Address!, savedDevice.Port, savedDevice.DeviceId));
        }

        foreach (SavedRemoteDevice savedDevice in recentDevices
            .Where(IsValidSavedRemoteDevice)
            .Where(device => !IsLocalSavedRemoteDevice(device, localAddresses))
            .OrderByDescending(device => device.LastConnectedAt))
        {
            string key = MakeDeviceListKey(savedDevice.Address!, savedDevice.Port, savedDevice.DeviceId);
            if (seenKeys.Contains(key))
            {
                continue;
            }

            devices.Add(RemoteDeviceListItem.FromSaved(savedDevice));
            seenKeys.Add(key);
        }

        return devices;
    }

    internal static IReadOnlyList<DiscoveredHost> MergeDiscoveredHosts(
        IReadOnlyList<DiscoveredHost> existingHosts,
        IReadOnlyList<DiscoveredHost> updatedHosts)
    {
        var merged = new Dictionary<string, DiscoveredHost>(StringComparer.OrdinalIgnoreCase);
        foreach (DiscoveredHost host in existingHosts)
        {
            merged[MakeDeviceKey(host.Address, host.Port)] = host;
        }

        foreach (DiscoveredHost host in updatedHosts)
        {
            merged[MakeDeviceKey(host.Address, host.Port)] = host;
        }

        return merged.Values
            .OrderBy(host => host.MachineName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(host => host.Address, StringComparer.OrdinalIgnoreCase)
            .ThenBy(host => host.Port)
            .ToArray();
    }

    private void RememberCurrentViewerDevice()
    {
        if (IsCurrentViewerRelayConnection())
        {
            return;
        }

        string address = _viewerHostBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        int port = (int)_viewerPortBox.Value;
        RemoteDeviceListItem? selectedDevice = GetSelectedRemoteDevice();
        RemoteDeviceDescriptor? connectedDevice = _lastConnectedDeviceInfo;
        if (connectedDevice is null || _rememberedViewerGeneration == _viewerClient.InputConnectionGeneration) return;
        if (connectedDevice.Capabilities.HasFlag(RemoteDeviceCapabilities.DeviceIdentity) &&
            connectedDevice.DeviceId is null) return;
        string machineName = connectedDevice?.MachineName ?? (selectedDevice is not null &&
            string.Equals(selectedDevice.Address, address, StringComparison.OrdinalIgnoreCase)
            ? selectedDevice.MachineName
            : address);
        string? captureTarget = selectedDevice is not null &&
            string.Equals(selectedDevice.Address, address, StringComparison.OrdinalIgnoreCase)
            ? selectedDevice.CaptureTarget
            : null;
        string platform = connectedDevice?.Platform ?? (selectedDevice is not null &&
            string.Equals(selectedDevice.Address, address, StringComparison.OrdinalIgnoreCase)
            ? selectedDevice.Platform
            : RemoteDevicePlatforms.Unknown);
        string? buildStamp = RemoteDeskBuildInfo.NormalizeBuildStamp(connectedDevice?.BuildStamp) ??
            (selectedDevice is not null &&
            string.Equals(selectedDevice.Address, address, StringComparison.OrdinalIgnoreCase)
                ? selectedDevice.BuildStamp
                : null);
        RemoteDeviceCapabilities capabilities = connectedDevice is not null &&
            connectedDevice.Capabilities != RemoteDeviceCapabilities.None
            ? connectedDevice.Capabilities
            : selectedDevice is not null &&
            string.Equals(selectedDevice.Address, address, StringComparison.OrdinalIgnoreCase)
            ? selectedDevice.Capabilities
            : RemoteDeviceCapabilities.RemoteDesktop | RemoteDeviceCapabilities.InputControl;

        List<SavedRemoteDevice> recentDevices = GetRecentDevices();
        SavedRemoteDevice? confirmedSource = _confirmedHistorySource is null ? null : FindSavedForDiscovery(recentDevices,
            _confirmedHistorySource.Address!, _confirmedHistorySource.Port, _confirmedHistorySource.DeviceId);
        if (_confirmedHistorySource is not null && confirmedSource is null) return; // Deleted while connecting.
        if (confirmedSource is not null && RemoteDeviceIdentity.Normalize(confirmedSource.DeviceId) is not null &&
            !RemoteDeviceIdentity.Same(confirmedSource.DeviceId, connectedDevice?.DeviceId)) confirmedSource = null;
        var currentDevice = new SavedRemoteDevice
        {
            DeviceId = connectedDevice?.DeviceId,
            Remark = confirmedSource?.Remark,
            ProtectedPassword = AppSettingsService.ProtectSecret(_viewerPasswordBox.Text),
            AutoDetectPort = _viewerAutoPortBox.Checked,
            MachineName = machineName,
            Address = address,
            Port = port,
            CaptureTarget = captureTarget,
            Platform = platform,
            Capabilities = capabilities,
            BuildStamp = buildStamp,
            LastConnectedAt = DateTimeOffset.Now
        };
        if (confirmedSource is not null) recentDevices.Remove(confirmedSource);
        UpsertRecentDevice(recentDevices, currentDevice);
        _confirmedHistorySource = currentDevice;
        _rememberedViewerGeneration = _viewerClient.InputConnectionGeneration;

        _settings.Viewer.Host = address;
        _settings.Viewer.Port = port;
        TrySaveSettings();
        UpdateDiscoveredHosts(_lastDiscoveredHosts, silent: true);
    }

    private RemoteDeviceListItem? GetSelectedRemoteDevice()
    {
        if (_discoveredHostsBox.SelectedItem is RemoteDeviceListItem comboDevice)
        {
            return comboDevice;
        }

        return _discoveredHostsList.SelectedItem as RemoteDeviceListItem;
    }

    private List<SavedRemoteDevice> GetRecentDevices()
    {
        _settings.Viewer.RecentDevices ??= [];
        return _settings.Viewer.RecentDevices;
    }

    internal static void UpsertRecentDevice(
        IList<SavedRemoteDevice> recentDevices,
        SavedRemoteDevice currentDevice)
    {
        ArgumentNullException.ThrowIfNull(recentDevices);
        ArgumentNullException.ThrowIfNull(currentDevice);
        if (!IsValidSavedRemoteDevice(currentDevice))
        {
            throw new ArgumentException(
                "保存设备必须包含有效地址和端口。",
                nameof(currentDevice));
        }

        string address = currentDevice.Address!.Trim();
        SavedRemoteDevice? previousDevice = recentDevices.FirstOrDefault(device =>
            IsValidSavedRemoteDevice(device) && SameSavedMachine(device, currentDevice));
        currentDevice.Address = address;
        currentDevice.DeviceId = RemoteDeviceIdentity.Normalize(currentDevice.DeviceId) ?? previousDevice?.DeviceId;
        currentDevice.ProtectedPassword ??= previousDevice?.ProtectedPassword;
        currentDevice.Remark = AppSettingsService
            .NormalizeSavedDeviceRemark(
                currentDevice.Remark ??
                recentDevices.FirstOrDefault(device => SameSavedMachine(device, currentDevice) &&
                    !string.IsNullOrWhiteSpace(device.Remark))?.Remark);

        for (int index = recentDevices.Count - 1;
             index >= 0;
             index--)
        {
            SavedRemoteDevice device =
                recentDevices[index];
            if (IsValidSavedRemoteDevice(device) && SameSavedMachine(device, currentDevice))
            {
                recentDevices.RemoveAt(index);
            }
        }

        recentDevices.Insert(0, currentDevice);
        while (recentDevices.Count >
            MaxRecentRemoteDevices)
        {
            recentDevices.RemoveAt(
                recentDevices.Count - 1);
        }
    }

    internal static bool UpdateSavedDeviceRemark(
        IList<SavedRemoteDevice> recentDevices,
        string? address,
        int port,
        string? remark,
        string? deviceId = null)
    {
        ArgumentNullException.ThrowIfNull(recentDevices);
        SavedRemoteDevice? device =
            FindSavedDevice(
                recentDevices,
                address,
                port,
                deviceId);
        if (device is null)
        {
            return false;
        }

        device.Remark = AppSettingsService
            .NormalizeSavedDeviceRemark(remark);
        return true;
    }

    internal static bool RemoveSavedDevice(
        IList<SavedRemoteDevice> recentDevices,
        string? address,
        int port,
        string? deviceId = null)
    {
        ArgumentNullException.ThrowIfNull(recentDevices);
        bool removed = false;
        for (int index = recentDevices.Count - 1;
             index >= 0;
             index--)
        {
            if (!(RemoteDeviceIdentity.Normalize(deviceId) is not null ? RemoteDeviceIdentity.Same(recentDevices[index].DeviceId, deviceId) : SavedDeviceMatchesEndpoint(
                    recentDevices[index],
                    address,
                    port)))
            {
                continue;
            }

            recentDevices.RemoveAt(index);
            removed = true;
        }

        return removed;
    }

    private static SavedRemoteDevice? FindSavedDevice(
        IEnumerable<SavedRemoteDevice> recentDevices,
        string? address,
        int port,
        string? deviceId = null)
    {
        return recentDevices.FirstOrDefault(device =>
            RemoteDeviceIdentity.Normalize(deviceId) is not null ? RemoteDeviceIdentity.Same(device.DeviceId, deviceId) : SavedDeviceMatchesEndpoint(
                device,
                address,
                port));
    }

    private static bool SavedDeviceMatchesEndpoint(
        SavedRemoteDevice device,
        string? address,
        int port)
    {
        return IsValidSavedRemoteDevice(device) &&
            !string.IsNullOrWhiteSpace(address) &&
            device.Port == port &&
            string.Equals(
                device.Address!.Trim(),
                address.Trim(),
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidSavedRemoteDevice(SavedRemoteDevice device)
    {
        return !string.IsNullOrWhiteSpace(device.Address) &&
            device.Port > 0 &&
            device.Port <= IPEndPoint.MaxPort;
    }

    private static bool IsLocalSavedRemoteDevice(SavedRemoteDevice device, IReadOnlySet<string> localAddresses)
    {
        return NetworkUtils.IsLikelyLocalEndpoint(device.Address, device.MachineName, localAddresses);
    }

    private static string MakeDeviceKey(string address, int port)
    {
        return $"{address.Trim()}:{port}";
    }

    private static string MakeDeviceListKey(string address, int port, string? deviceId) =>
        RemoteDeviceIdentity.Normalize(deviceId) is string id ? "id:" + id : "ep:" + MakeDeviceKey(address, port);

    private void RefreshLocalIps()
    {
        IReadOnlyList<string> addresses = NetworkUtils.GetLocalIPv4Addresses();
        _localIpsBox.Text = addresses.Count == 0 ? "未发现活动 IPv4 地址" : string.Join(", ", addresses);
    }

    private void RefreshCaptureTargets()
    {
        string? selectedId = (_captureTargetBox.SelectedItem as ScreenCaptureTarget)?.Id;
        IReadOnlyList<ScreenCaptureTarget> targets = ScreenCaptureService.GetAvailableTargets();

        _captureTargetBox.BeginUpdate();
        try
        {
            _captureTargetBox.Items.Clear();
            foreach (ScreenCaptureTarget target in targets)
            {
                _captureTargetBox.Items.Add(target);
            }

            ScreenCaptureTarget? selectedTarget = targets.FirstOrDefault(target => target.Id == selectedId);
            if (selectedTarget is null && targets.Count > 0)
            {
                selectedTarget = ScreenCaptureService.ChooseDefaultTarget(targets);
            }

            if (selectedTarget is not null)
            {
                _captureTargetBox.SelectedItem = selectedTarget;
            }
        }
        finally
        {
            _captureTargetBox.EndUpdate();
        }
    }

    private void ApplySettings()
    {
        _applyingSettings = true;
        try
        {
            string hostPassword = AppSettingsService.UnprotectSecret(_settings.Host.ProtectedPassword);
            string viewerPassword = AppSettingsService.UnprotectSecret(_settings.Viewer.ProtectedPassword);
            string relayViewerPassword = AppSettingsService.UnprotectSecret(
                _settings.Relay.ProtectedViewerPassword);
            _hostPortBox.Value = ClampToRange(_settings.Host.Port, _hostPortBox);
            _hostPasswordBox.Text = string.IsNullOrWhiteSpace(hostPassword) ? DefaultAccessPassword : hostPassword;
            _hostFpsBox.Value = ClampToRange(_settings.Host.Fps, _hostFpsBox);
            _jpegQualityTrack.Value = Math.Clamp(_settings.Host.JpegQuality, _jpegQualityTrack.Minimum, _jpegQualityTrack.Maximum);
            _jpegQualityLabel.Text = $"JPEG {_jpegQualityTrack.Value}";
            _adaptiveQualityBox.Checked = _settings.Host.AdaptiveQuality;
            _autoStartHostBox.Checked = _settings.Host.AutoStart;
            _allowRemoteStartBox.Checked = _settings.Host.AllowRemoteStart;
            StartupRegistrationStatus startupStatus = StartupService.GetStatus();
            if (StartupService.IsOrphanedRegistration(startupStatus))
            {
                try
                {
                    StartupService.SetEnabled(true);
                    startupStatus = StartupService.GetStatus();
                    AppendHostLog("已修复失效的开机启动路径，登录后会自动启动到托盘。");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
                {
                    AppendHostLog($"修复开机启动路径失败：{ex.Message}");
                }
            }

            ApplyStartupRegistrationStatus(startupStatus);
            _minimizeToTrayBox.Checked = _settings.App.MinimizeToTray;
            SetSelectedScalePercent(_settings.Host.ScalePercent);
            SelectLocalCaptureTarget(_settings.Host.CaptureTargetId);

            _viewerHostBox.Text = _settings.Viewer.Host ?? string.Empty;
            _viewerPortBox.Value = ClampToRange(_settings.Viewer.Port, _viewerPortBox);
            _viewerAutoPortBox.Checked = _settings.Viewer.AutoDetectPort;
            SetSelectedViewerVideoMode(_settings.Viewer.VideoMode);
            _viewerPasswordBox.Text = string.IsNullOrWhiteSpace(viewerPassword) ? DefaultAccessPassword : viewerPassword;
            _relayRegisterHostBox.Checked =
                _settings.Relay.RegisterThisDevice;
            _relayOptimizeNetworkBox.Checked = _settings.Relay.OptimizeNetworkRoute;
            _relayViewerPasswordBox.Text =
                string.IsNullOrWhiteSpace(relayViewerPassword)
                    ? DefaultAccessPassword
                    : relayViewerPassword;
            SetSelectedViewerVideoMode(
                _relayVideoModeBox,
                _settings.Relay.VideoMode);
            UpdateRelayServerSummary();
            UpdateDiscoveredHosts(_lastDiscoveredHosts, silent: true);
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    private void ApplyStartupRegistrationStatus(StartupRegistrationStatus status)
    {
        _startWithWindowsBox.Checked = status.IsRegistered;
        UpdateStartupRegistrationToolTip(status);
    }

    private void UpdateStartupRegistrationToolTip(StartupRegistrationStatus status)
    {
        if (status.IsPersistent)
        {
            SetToolTip(_startWithWindowsBox, status.IsRegistered
                ? "已安装常驻权限：当前用户登录后自动以管理员运行受保护的副本。取消勾选可停用。"
                : "常驻权限已停用；勾选可重新启用，不需要保存 Windows 密码。");
            return;
        }
        if (!status.IsRegistered)
        {
            SetToolTip(_startWithWindowsBox, "当前 Windows 用户登录后自动启动 RemoteDesk。");
            return;
        }

        if (status.TargetsCurrentExecutable)
        {
            SetToolTip(_startWithWindowsBox, "当前 Windows 用户登录后会自动启动这个 RemoteDesk 副本到托盘。");
            return;
        }

        string target = string.IsNullOrWhiteSpace(status.TargetExecutablePath)
            ? "无法识别的命令"
            : status.TargetExecutablePath;
        SetToolTip(
            _startWithWindowsBox,
            $"检测到 RemoteDesk 开机启动项，但它指向其他副本（{target}）。" +
            "取消勾选可移除；再次勾选会改为当前副本。");
    }

    private void SaveSettingsFromUi()
    {
        try
        {
            if (_hostPortBox is null || _hostPortBox.IsDisposed)
            {
                return;
            }

            _settings.Host.Port = (int)_hostPortBox.Value;
            _settings.Host.Fps = (int)_hostFpsBox.Value;
            _settings.Host.JpegQuality = _jpegQualityTrack.Value;
            _settings.Host.ScalePercent = GetSelectedScalePercent();
            _settings.Host.AdaptiveQuality = _adaptiveQualityBox.Checked;
            _settings.Host.AutoStart = _autoStartHostBox.Checked;
            _settings.Host.AllowRemoteStart = _allowRemoteStartBox.Checked;
            _settings.Host.CaptureTargetId = (_captureTargetBox.SelectedItem as ScreenCaptureTarget)?.Id;
            _settings.Host.ProtectedPassword = AppSettingsService.ProtectSecret(_hostPasswordBox.Text);
            _settings.App.StartWithWindows = _startWithWindowsBox.Checked;
            _settings.App.MinimizeToTray = _minimizeToTrayBox.Checked;

            _settings.Viewer.Host = _viewerHostBox.Text.Trim();
            _settings.Viewer.Port = (int)_viewerPortBox.Value;
            _settings.Viewer.AutoDetectPort = _viewerAutoPortBox.Checked;
            _settings.Viewer.VideoMode = GetSelectedViewerVideoMode();
            _settings.Viewer.PreferH264 = _settings.Viewer.VideoMode != ViewerVideoMode.StableJpeg;
            _settings.Viewer.ProtectedPassword = AppSettingsService.ProtectSecret(_viewerPasswordBox.Text);
            if (_viewerCaptureTargetBox.SelectedItem is CaptureTargetInfo captureTarget)
            {
                _settings.Viewer.CaptureTargetId = captureTarget.Id;
            }

            _settings.Relay.RegisterThisDevice =
                _relayRegisterHostBox.Checked;
            _settings.Relay.OptimizeNetworkRoute = _relayOptimizeNetworkBox.Checked;
            _settings.Relay.VideoMode =
                GetSelectedViewerVideoMode(_relayVideoModeBox);
            _settings.Relay.ProtectedViewerPassword =
                AppSettingsService.ProtectSecret(
                    _relayViewerPasswordBox.Text);

            TrySaveSettings();
            UpdateDiscoveryPresence();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private static decimal ClampToRange(int value, NumericUpDown input)
    {
        return Math.Clamp(value, (int)input.Minimum, (int)input.Maximum);
    }

    private bool TrySaveSettings()
    {
        SettingsSaveResult result = _settingsService.Save(_settings);
        if (!result.Success)
        {
            if (_lastSettingsSaveError != result.ErrorMessage)
            {
                _diagnosticLog.Append("SETTINGS", $"配置未能保存：{result.ErrorMessage}");
            }
            _lastSettingsSaveError = result.ErrorMessage;
        }
        else if (_lastSettingsSaveError is not null)
        {
            _diagnosticLog.Append("SETTINGS", "配置已重新成功保存。");
            _lastSettingsSaveError = null;
        }
        if (!_isClosing && !IsDisposed && _settingsSaveNotice is { IsDisposed: false })
        {
            _settingsSaveNotice.ApplyResult(result);
        }
        return result.Success;
    }

    internal static ListViewItem CreateRelayDeviceListItem(RelayOnlineDevice device, string? localDeviceId)
    {
        bool isLocal = RelayDeviceSelectionPolicy.IsLocalDevice(device, localDeviceId);
        var item = new ListViewItem(isLocal ? $"{device.MachineName}（本机）" : device.MachineName)
        {
            Tag = device,
            ForeColor = isLocal ? MutedTextColor : TextColor
        };
        item.SubItems.Add(device.Platform);
        item.SubItems.Add(device.BuildDisplay);
        item.SubItems.Add(isLocal ? "本机（不可自连）" : device.StatusText);
        item.SubItems.Add(device.LastSeenDisplay);
        item.SubItems.Add(device.AddressDisplay);
        item.ToolTipText = $"设备 ID：{device.DeviceId}\n{device.AddressDisplay}\n仅可达的地址能直连；跨网请选择中继连接。";
        return item;
    }

    private ViewerVideoMode GetSelectedViewerVideoMode()
    {
        return GetSelectedViewerVideoMode(_viewerVideoModeBox);
    }

    private static ViewerVideoMode GetSelectedViewerVideoMode(
        ComboBox videoModeBox)
    {
        return videoModeBox.SelectedItem is ViewerVideoModeItem item
            ? item.Mode
            : ViewerVideoMode.Automatic;
    }

    private void SetSelectedViewerVideoMode(ViewerVideoMode videoMode)
    {
        SetSelectedViewerVideoMode(
            _viewerVideoModeBox,
            videoMode);
    }

    private static void SetSelectedViewerVideoMode(
        ComboBox videoModeBox,
        ViewerVideoMode videoMode)
    {
        if (!Enum.IsDefined(videoMode))
        {
            videoMode = ViewerVideoMode.Automatic;
        }

        for (int index = 0; index < videoModeBox.Items.Count; index++)
        {
            if (videoModeBox.Items[index] is ViewerVideoModeItem item &&
                item.Mode == videoMode)
            {
                videoModeBox.SelectedIndex = index;
                return;
            }
        }

        videoModeBox.SelectedIndex = 0;
    }

    private void SelectLocalCaptureTarget(string? targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return;
        }

        ScreenCaptureTarget resolvedTarget = ScreenCaptureService.ResolveTargetOrDefault(
            _captureTargetBox.Items.OfType<ScreenCaptureTarget>().ToArray(),
            targetId);
        for (int index = 0; index < _captureTargetBox.Items.Count; index++)
        {
            if (_captureTargetBox.Items[index] is ScreenCaptureTarget target &&
                string.Equals(target.Id, resolvedTarget.Id, StringComparison.OrdinalIgnoreCase))
            {
                _captureTargetBox.SelectedIndex = index;
                return;
            }
        }
    }

    private void SetSelectedScalePercent(int scalePercent)
    {
        string target = scalePercent switch
        {
            ScreenCaptureService.QhdMaximumScaleMode =>
                "最高 1440p",
            75 => "75%",
            50 => "50%",
            _ => "100%（原生）"
        };

        _captureScaleBox.SelectedItem = target;
    }

    private ScreenCaptureTarget GetSelectedCaptureTarget()
    {
        return _captureTargetBox.SelectedItem as ScreenCaptureTarget ?? ScreenCaptureService.GetDefaultTarget();
    }

    private ScreenCaptureTarget
        GetLowLatencyStartupCaptureTarget()
    {
        ScreenCaptureTarget selected =
            GetSelectedCaptureTarget();
        ScreenCaptureTarget resolved =
            ScreenCaptureService
                .ChooseLowLatencyStartupTarget(
                    selected,
                    _captureTargetBox.Items
                        .OfType<ScreenCaptureTarget>()
                        .ToArray());
        if (string.Equals(
                selected.Id,
                resolved.Id,
                StringComparison.OrdinalIgnoreCase))
        {
            return selected;
        }

        _captureTargetBox.SelectedItem = resolved;
        _settings.Host.CaptureTargetId =
            resolved.Id;
        AppendHostLog(
            "低延迟保护：所有屏幕拼接区域 " +
            $"{selected.Bounds.Width}x" +
            $"{selected.Bounds.Height} 无法使用单屏 WGC/D3D11 GPU 捕获链，" +
            "为避免 GDI 高延迟路径，已切换到 " +
            $"{resolved.DisplayName}。可在被控端或连接后的屏幕列表中选择其他单屏；" +
            "多屏拼接桌面不再静默退化到低帧率 JPEG。");
        return resolved;
    }

    private int GetSelectedScalePercent()
    {
        string value =
            (_captureScaleBox.SelectedItem as string) ??
            "100%（原生）";
        return value switch
        {
            "最高 1440p" =>
                ScreenCaptureService.QhdMaximumScaleMode,
            "75%" => 75,
            "50%" => 50,
            _ => 100
        };
    }

    private void AppendHostLog(string message)
    {
        _diagnosticLog.Append("HOST", message);
        OnUi(() =>
        {
            if (_hostLogBox.TextLength > 100_000)
            {
                _hostLogBox.Clear();
            }

            _hostLogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        });
    }

    private void SetHostStatus(string status)
    {
        bool active = status.Contains("监听", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("连接", StringComparison.OrdinalIgnoreCase);

        SetStatusBadge(
            _hostStatusLabel,
            status,
            active ? SuccessBackColor : NeutralBadgeBackColor,
            active ? SuccessTextColor : NeutralBadgeTextColor);
    }

    private void SetViewerStatus(string status, Color foreColor)
    {
        _diagnosticLog.Append("VIEWER", status);
        _viewerStatusLabel.Text = status;
        _viewerStatusLabel.ForeColor = foreColor;
        SetToolTip(_viewerStatusLabel, status);
    }

    private void ExportDiagnosticLog()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "导出 RemoteDesk 诊断日志",
            FileName = $"RemoteDesk-Windows-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            AddExtension = true,
            DefaultExt = "log",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            _diagnosticLog.Append("APP", "正在导出 Windows 诊断日志。");
            _diagnosticLog.ExportTo(dialog.FileName);
            AppendHostLog($"诊断日志已导出：{dialog.FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            AppendHostLog($"导出诊断日志失败：{ex.Message}");
        }
    }

    private void UpdateViewerActionState()
    {
        UpdateRelayActionState();
        if (IsViewerReconnecting())
        {
            _viewerToggleButton.Text = "取消重连";
            _viewerToggleButton.Enabled = true;
            ApplyButtonStyle(
                _viewerToggleButton,
                DangerColor,
                Color.White,
                DangerColor);
            SetToolTip(
                _viewerToggleButton,
                "取消当前自动重连并保持断开。");
            return;
        }

        if (_viewerActionInProgress)
        {
            _viewerToggleButton.Text =
                _viewerActionDisconnecting
                    ? "断开中..."
                    : "连接中...";
            _viewerToggleButton.Enabled = false;
            ApplyButtonStyle(
                _viewerToggleButton,
                NeutralBadgeBackColor,
                NeutralBadgeTextColor,
                PanelBorderColor);
            SetToolTip(
                _viewerToggleButton,
                _viewerActionDisconnecting
                    ? "正在安全关闭当前远程会话。"
                    : "正在探测目标、完成加密握手并启动远程画面。");
            return;
        }

        if (_viewerClient.IsConnected)
        {
            _viewerToggleButton.Text = "断开";
            _viewerToggleButton.Enabled = true;
            ApplyButtonStyle(_viewerToggleButton, DangerColor, Color.White, DangerColor);
            SetToolTip(_viewerToggleButton, "断开当前远程连接。");
            return;
        }

        if (TryGetBlockedSelectedDevice(out RemoteDeviceListItem? device))
        {
            _viewerToggleButton.Text = device.GetBlockedActionText();
            _viewerToggleButton.Enabled = false;
            ApplyButtonStyle(_viewerToggleButton, NeutralBadgeBackColor, NeutralBadgeTextColor, PanelBorderColor);
            SetToolTip(_viewerToggleButton, device.GetBlockedActionHint());
            return;
        }

        _viewerToggleButton.Text = "连接";
        _viewerToggleButton.Enabled = true;
        ApplyButtonStyle(_viewerToggleButton, PrimaryColor, Color.White, PrimaryColor);
        SetToolTip(_viewerToggleButton, "连接到当前 IP/主机名，必要时会尝试同口令远程启动。");
    }

    private void UpdateRelayActionState()
    {
        if (_relayConnectButton is null ||
            _relayConnectButton.IsDisposed)
        {
            return;
        }

        bool configured = IsRelayConfigured();
        _relayConfigureButton.Enabled =
            !_relayOperationInProgress &&
            !_viewerActionInProgress &&
            !IsViewerReconnecting() &&
            !_viewerClient.IsConnected;
        _relayRefreshButton.Enabled =
            configured &&
            !_relayOperationInProgress &&
            !_relayRefreshInProgress;
        _relayReportAddressButton.Enabled = configured && !_relayOperationInProgress;
        _relayAddressButton.Enabled = _relayRefreshButton.Enabled &&
            !_viewerActionInProgress && !_viewerClient.IsConnected && !IsViewerReconnecting() &&
            GetSelectedRelayDevice() is not null;
        _relayRegisterHostBox.Enabled =
            !_relayOperationInProgress;
        _relayViewerPasswordBox.Enabled =
            !_viewerClient.IsConnected &&
            !_viewerActionInProgress &&
            !_relayOperationInProgress;
        _relayVideoModeBox.Enabled =
            _relayViewerPasswordBox.Enabled;
        _relayDevicesList.Enabled =
            !_viewerActionInProgress;

        if (IsViewerReconnecting())
        {
            _relayConnectButton.Text = "取消重连";
            _relayConnectButton.Enabled = true;
            ApplyButtonStyle(
                _relayConnectButton,
                DangerColor,
                Color.White,
                DangerColor);
            return;
        }

        if (_viewerActionInProgress)
        {
            _relayConnectButton.Text = _viewerActionDisconnecting
                ? "断开中..."
                : "连接中...";
            _relayConnectButton.Enabled = false;
            ApplyButtonStyle(
                _relayConnectButton,
                NeutralBadgeBackColor,
                NeutralBadgeTextColor,
                PanelBorderColor);
            return;
        }

        if (_viewerClient.IsConnected)
        {
            _relayConnectButton.Text = "断开";
            _relayConnectButton.Enabled = true;
            ApplyButtonStyle(
                _relayConnectButton,
                DangerColor,
                Color.White,
                DangerColor);
            return;
        }

        _relayConnectButton.Text = "连接所选设备";
        _relayConnectButton.Enabled =
            configured &&
            !_relayOperationInProgress &&
            RelayDeviceSelectionPolicy.GetConnectionBlockReason(
                GetSelectedRelayDevice(), _settings.Relay.DeviceId) is null;
        ApplyButtonStyle(
            _relayConnectButton,
            _relayConnectButton.Enabled
                ? PrimaryColor
                : NeutralBadgeBackColor,
            _relayConnectButton.Enabled
                ? Color.White
                : NeutralBadgeTextColor,
            _relayConnectButton.Enabled
                ? PrimaryColor
                : PanelBorderColor);
    }

    private void SetViewerConnectionInputsEnabled(
        bool enabled)
    {
        _viewerHostBox.Enabled = enabled;
        _viewerPortBox.Enabled = enabled;
        _viewerAutoPortBox.Enabled = enabled;
        _addDeviceButton.Enabled = enabled;
        _viewerPasswordBox.Enabled = enabled;
        _discoveredHostsBox.Enabled = enabled;
        _discoveredHostsList.Enabled = enabled;
        _discoverHostsButton.Enabled = enabled;
        _diagnoseConnectionButton.Enabled = enabled;
        _viewerVideoModeBox.Enabled = enabled;
        UpdateSavedDeviceActionState();
        UpdateRelayActionState();
    }

    internal static string FormatViewerConnectingStatus(
        string? host,
        int port)
    {
        string target =
            string.IsNullOrWhiteSpace(host)
                ? "远程设备"
                : $"{host.Trim()}:{port}";
        return
            $"正在连接 {target}（加密握手）...";
    }

    internal static string
        ResolveDisconnectedViewerStatus(
            string? currentStatus)
    {
        string normalized =
            currentStatus?.Trim() ??
            string.Empty;
        if (IsExpectedRemoteUpdateRestartStatus(
                normalized))
        {
            if (normalized.Contains(
                    "验证版本",
                    StringComparison.Ordinal))
            {
                return normalized;
            }

            return
                $"{TrimViewerStatusTerminator(normalized)}；" +
                "远端正在重启，请稍后点击“连接”验证版本。";
        }

        if (ShouldPreserveViewerDisconnectFailure(
                normalized))
        {
            if (normalized.Contains(
                    "点击“连接”",
                    StringComparison.Ordinal))
            {
                return normalized;
            }

            return
                $"{TrimViewerStatusTerminator(normalized)}；" +
                "点击“连接”重试。";
        }

        return
            "连接已断开；点击“连接”可重新连接。";
    }

    internal static bool
        ShouldPreserveViewerDisconnectFailure(
            string? currentStatus)
    {
        if (string.IsNullOrWhiteSpace(
                currentStatus))
        {
            return false;
        }

        string normalized =
            currentStatus.Trim();
        return normalized.StartsWith(
                "连接中断",
                StringComparison.Ordinal) ||
            normalized.StartsWith(
                "连接超时",
                StringComparison.Ordinal) ||
            normalized.StartsWith(
                "连接失败",
                StringComparison.Ordinal) ||
            normalized.StartsWith(
                "断开失败",
                StringComparison.Ordinal) ||
            normalized.StartsWith(
                "输入发送中断",
                StringComparison.Ordinal);
    }

    internal static bool
        IsExpectedRemoteUpdateRestartStatus(
            string? currentStatus)
    {
        return
            !string.IsNullOrWhiteSpace(
                currentStatus) &&
            currentStatus.Contains(
                "远程更新包已发送",
                StringComparison.Ordinal) &&
            currentStatus.Contains(
                "重启",
                StringComparison.Ordinal);
    }

    private void MarkRemoteUpdateRestartExpected(
        long now)
    {
        Interlocked.Exchange(
            ref _remoteUpdateRestartExpectedUntil,
            checked(now + 45_000));
    }

    private bool ConsumeExpectedRemoteUpdateRestart(
        long now)
    {
        long deadline =
            Interlocked.Exchange(
                ref _remoteUpdateRestartExpectedUntil,
                0);
        return IsRemoteUpdateRestartExpected(
            now,
            deadline);
    }

    internal static bool
        IsRemoteUpdateRestartExpected(
            long now,
            long deadline)
    {
        return deadline > 0 &&
            now >= 0 &&
            now <= deadline;
    }

    private static string
        TrimViewerStatusTerminator(
            string status)
    {
        return status.TrimEnd(
            ' ',
            '。',
            '；');
    }

    private bool ReportBlockedViewerAction()
    {
        if (!TryGetBlockedSelectedDevice(out RemoteDeviceListItem? device))
        {
            return false;
        }

        SetViewerStatus(device.GetBlockedActionHint(), DangerColor);
        return true;
    }

    private bool TryGetBlockedSelectedDevice(out RemoteDeviceListItem device)
    {
        device = null!;
        RemoteDeviceListItem? selectedDevice = GetSelectedRemoteDevice();
        if (selectedDevice is null ||
            selectedDevice.CanOpenViewerNow ||
            !string.Equals(selectedDevice.Address, _viewerHostBox.Text.Trim(), StringComparison.OrdinalIgnoreCase) ||
            selectedDevice.Port != (int)_viewerPortBox.Value)
        {
            return false;
        }

        device = selectedDevice;
        return true;
    }

    private async Task ViewerCaptureTargetChangedAsync()
    {
        if (_updatingViewerCaptureTargets ||
            !_viewerClient.IsConnected ||
            _viewerCaptureTargetBox.SelectedItem is not CaptureTargetInfo target)
        {
            return;
        }

        _settings.Viewer.CaptureTargetId = target.Id;
        SaveSettingsFromUi();
        await _viewerClient.SelectCaptureTargetAsync(target.Id);
        if (!_isClosing && !IsDisposed)
        {
            _viewerWindow?.Activate();
        }
    }

    private async Task SendClipboardAsync()
    {
        if (!CanConnectedViewer(RemoteDeviceCapabilities.ClipboardText))
        {
            SetViewerStatus("远程设备未声明剪贴板能力。", MutedTextColor);
            return;
        }

        _sendClipboardButton.Enabled = false;
        try
        {
            await _viewerClient.SendLocalClipboardToRemoteAsync();
        }
        catch (Exception ex)
        {
            if (!_isClosing && !IsDisposed)
            {
                SetViewerStatus($"发送剪贴板失败：{ex.Message}", DangerColor);
            }
        }
        finally
        {
            if (!_isClosing && !IsDisposed)
            {
                ApplyViewerCapabilityState(_viewerClient.IsConnected);
            }
        }
    }

    private async Task ReadClipboardAsync()
    {
        if (!CanConnectedViewer(RemoteDeviceCapabilities.ClipboardText))
        {
            SetViewerStatus("远程设备未声明剪贴板能力。", MutedTextColor);
            return;
        }

        _readClipboardButton.Enabled = false;
        try
        {
            await _viewerClient.ReadRemoteClipboardAsync();
        }
        catch (Exception ex)
        {
            if (!_isClosing && !IsDisposed)
            {
                SetViewerStatus($"读取剪贴板失败：{ex.Message}", DangerColor);
            }
        }
        finally
        {
            if (!_isClosing && !IsDisposed)
            {
                ApplyViewerCapabilityState(_viewerClient.IsConnected);
            }
        }
    }

    private async Task SendFileAsync()
    {
        if (!CanConnectedViewer(RemoteDeviceCapabilities.FileReceive))
        {
            SetViewerStatus("远程设备未声明文件接收能力。", MutedTextColor);
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "选择要发送到被控端的文件",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            SetViewerStatus("正在读取文件信息...", MutedTextColor);
            FileTransferConfirmationPreview preview = await CreateOutgoingFileTransferPreviewAsync(
                [dialog.FileName],
                maxFiles: 1,
                FileTransferConfirmation.FormatRemoteReceiveDestination);
            if (!ConfirmOutgoingFileTransfer(
                preview,
                "确认发送文件",
                "即将发送以下文件到被控端。"))
            {
                SetViewerStatus("已取消文件传输。", MutedTextColor);
                return;
            }

            _sendFileButton.Enabled = false;
            SetViewerStatus("正在准备发送文件...", MutedTextColor);
            await _viewerClient.SendFilePastePlanToRemoteAsync(preview.Plan, "发送文件失败");
        }
        catch (Exception ex)
        {
            if (!_isClosing && !IsDisposed)
            {
                SetViewerStatus($"发送文件失败：{ex.Message}", DangerColor);
            }
        }
        finally
        {
            if (!_isClosing && !IsDisposed)
            {
                ApplyViewerCapabilityState(_viewerClient.IsConnected);
            }
        }
    }

    private async Task SynchronizeRemoteUpdateAsync()
    {
        if (!RemoteUpdater.CanApplyRemoteUpdate)
        {
            SetViewerStatus(
                "当前 RemoteDesk.exe 缺少有效构建号或签名状态异常，远程更新已禁用。",
                DangerColor);
            return;
        }

        if (!CanConnectedViewer(RemoteDeviceCapabilities.RemoteUpdate))
        {
            SetViewerStatus("远程设备未声明自更新能力。", MutedTextColor);
            return;
        }

        string localBuildStamp = RemoteDeskBuildInfo.BuildStamp;
        string? remoteBuildStamp = _lastConnectedDeviceInfo?.BuildStamp;
        int? comparison = RemoteDeskBuildInfo.CompareBuildStamps(localBuildStamp, remoteBuildStamp);
        if (comparison is < 0)
        {
            await PullRemoteUpdatePackageAsync(remoteBuildStamp);
            return;
        }

        if (comparison is 0)
        {
            SetViewerStatus(
                $"本机和远端版本一致：{RemoteDeskBuildInfo.FormatBuildStamp(localBuildStamp)}，无需更新。",
                SuccessTextColor);
            return;
        }

        await SendLocalRemoteUpdatePackageAsync(remoteBuildStamp, comparison is null);
    }

    private async Task SendLocalRemoteUpdatePackageAsync(string? remoteBuildStamp, bool remoteVersionUnknown)
    {
        string? packagePath = ResolveLocalRemoteUpdatePackagePath();
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            SetViewerStatus("未找到本机 RemoteDesk.exe 更新包。", DangerColor);
            return;
        }

        try
        {
            RemoteUpdater.ValidateRemoteUpdatePackageName(packagePath);
            var package = new FileInfo(packagePath);
            RemoteUpdater.ValidateRemoteUpdatePackageLength(package.Length);
            string message =
                $"本机版本较新，将把本机 RemoteDesk.exe 发送到被控端并自动重启远端。{Environment.NewLine}{Environment.NewLine}" +
                $"本机版本：{RemoteDeskBuildInfo.FormatBuildStamp(RemoteDeskBuildInfo.BuildStamp)}{Environment.NewLine}" +
                $"远端版本：{(remoteVersionUnknown ? "未知" : RemoteDeskBuildInfo.FormatBuildStamp(remoteBuildStamp))}{Environment.NewLine}" +
                $"文件：{package.FullName}{Environment.NewLine}" +
                $"大小：{RemoteFileTransfer.FormatBytes(package.Length)}{Environment.NewLine}" +
                $"修改时间：{package.LastWriteTime:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{Environment.NewLine}" +
                "更新过程中当前远程连接会断开。确认继续？";
            DialogResult confirmed = MessageBox.Show(
                this,
                message,
                "确认远程更新",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmed != DialogResult.Yes)
            {
                SetViewerStatus("已取消远程更新。", MutedTextColor);
                return;
            }

            _remoteUpdateButton.Enabled = false;
            SetViewerStatus("正在发送远程更新包...", MutedTextColor);
            await _viewerClient.SendRemoteUpdateAsync(packagePath);
            const string remoteUpdateRestartStatus =
                "远程更新包已发送，等待被控端校验并重启。";
            MarkRemoteUpdateRestartExpected(
                Environment.TickCount64);
            string displayedStatus =
                remoteUpdateRestartStatus;
            if (!_viewerClient.IsConnected)
            {
                ConsumeExpectedRemoteUpdateRestart(
                    Environment.TickCount64);
                displayedStatus =
                    ResolveDisconnectedViewerStatus(
                        remoteUpdateRestartStatus);
            }

            SetViewerStatus(
                displayedStatus,
                SuccessTextColor);
        }
        catch (Exception ex) when (ex is IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            ObjectDisposedException or
            ArgumentException or
            NotSupportedException or
            System.Security.SecurityException or
            System.ComponentModel.Win32Exception)
        {
            if (!_isClosing && !IsDisposed)
            {
                Interlocked.Exchange(
                    ref _remoteUpdateRestartExpectedUntil,
                    0);
                SetViewerStatus($"远程更新失败：{ex.Message}", DangerColor);
            }
        }
        finally
        {
            if (!_isClosing && !IsDisposed)
            {
                ApplyViewerCapabilityState(_viewerClient.IsConnected);
            }
        }
    }

    private async Task PullRemoteUpdatePackageAsync(string? remoteBuildStamp)
    {
        try
        {
            string message =
                $"远端版本较新，将从被控端拉取 RemoteDesk.exe 并更新本机控制端。{Environment.NewLine}{Environment.NewLine}" +
                $"本机版本：{RemoteDeskBuildInfo.FormatBuildStamp(RemoteDeskBuildInfo.BuildStamp)}{Environment.NewLine}" +
                $"远端版本：{RemoteDeskBuildInfo.FormatBuildStamp(remoteBuildStamp)}{Environment.NewLine}{Environment.NewLine}" +
                "更新过程中当前远程连接会断开，本机 RemoteDesk 会自动重启。确认继续？";
            DialogResult confirmed = MessageBox.Show(
                this,
                message,
                "确认拉取远端更新",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmed != DialogResult.Yes)
            {
                SetViewerStatus("已取消拉取远端更新。", MutedTextColor);
                return;
            }

            _remoteUpdateButton.Enabled = false;
            SetViewerStatus("正在请求远端回传更新包...", MutedTextColor);
            await _viewerClient.RequestRemoteUpdatePackageAsync();
        }
        catch (Exception ex) when (ex is IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            ObjectDisposedException or
            ArgumentException or
            NotSupportedException or
            System.Security.SecurityException or
            System.ComponentModel.Win32Exception)
        {
            if (!_isClosing && !IsDisposed)
            {
                SetViewerStatus($"拉取远端更新失败：{ex.Message}", DangerColor);
            }
        }
        finally
        {
            if (!_isClosing && !IsDisposed)
            {
                ApplyViewerCapabilityState(_viewerClient.IsConnected);
            }
        }
    }

    private static string? ResolveLocalRemoteUpdatePackagePath()
    {
        return RemoteUpdater.TryGetCurrentPackagePath() ?? FindDefaultRemoteUpdatePackagePath();
    }

    private static string? FindDefaultRemoteUpdatePackagePath()
    {
        var candidates = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !File.Exists(path) ||
                !string.Equals(Path.GetFileName(path), "RemoteDesk.exe", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            candidates[Path.GetFullPath(path)] = File.GetLastWriteTimeUtc(path);
        }

        AddCandidate(Environment.ProcessPath);
        AddCandidate(Path.Combine(AppContext.BaseDirectory, "RemoteDesk.exe"));

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
        {
            AddCandidate(Path.Combine(directory.FullName, "artifacts", "RemoteDesk-win-x64", "RemoteDesk.exe"));
            AddCandidate(Path.Combine(directory.FullName, "artifacts", "RemoteDesk.exe"));
        }

        return candidates
            .OrderByDescending(item => item.Value)
            .Select(item => item.Key)
            .FirstOrDefault();
    }

    private async Task PasteClipboardFilesFromMainAsync()
    {
        if (_pastingClipboardFilesFromMain)
        {
            return;
        }

        if (!CanConnectedViewer(RemoteDeviceCapabilities.FileReceive))
        {
            SetViewerStatus("远程设备未声明文件接收能力。", MutedTextColor);
            return;
        }

        _pastingClipboardFilesFromMain = true;
        _pasteFilesButton.Enabled = false;
        try
        {
            IReadOnlyList<string> clipboardFiles = await ClipboardTextService.GetFileDropListAsync();
            if (clipboardFiles.Count == 0)
            {
                SetViewerStatus(
                    "本机剪贴板没有可发送的文件。请先在资源管理器复制文件或文件夹，再点击“粘贴文件”或在远程窗口按 Ctrl+V。",
                    MutedTextColor);
                return;
            }

            SetViewerStatus("正在读取剪贴板文件信息...", MutedTextColor);
            FileTransferConfirmationPreview preview = await CreateOutgoingFileTransferPreviewAsync(
                clipboardFiles,
                MaxClipboardFilePasteCount,
                FileTransferConfirmation.FormatRemoteReceiveDestination);
            if (!ConfirmOutgoingFileTransfer(
                preview,
                "确认粘贴文件",
                "即将把本机剪贴板文件发送到被控端。"))
            {
                SetViewerStatus("已取消剪贴板文件传输。", MutedTextColor);
                return;
            }

            SetViewerStatus("正在发送剪贴板文件...", MutedTextColor);
            RemoteFilePasteResult result = await _viewerClient.SendFilePastePlanToRemoteAsync(preview.Plan);
            string status = RemoteViewerWindow.FormatClipboardFilePasteStatus(result, MaxClipboardFilePasteCount);
            Color statusColor = result.FailedFiles > 0
                ? DangerColor
                : result.SentFiles > 0
                    ? SuccessTextColor
                    : MutedTextColor;
            SetViewerStatus(status, statusColor);
        }
        catch (Exception ex) when (ex is TimeoutException or
            System.Runtime.InteropServices.ExternalException or
            IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            ObjectDisposedException)
        {
            if (!_isClosing && !IsDisposed)
            {
                SetViewerStatus($"粘贴文件失败：{ex.Message}", DangerColor);
            }
        }
        finally
        {
            _pastingClipboardFilesFromMain = false;
            if (!_isClosing && !IsDisposed)
            {
                ApplyViewerCapabilityState(_viewerClient.IsConnected);
            }
        }
    }

    private static Task<FileTransferConfirmationPreview> CreateOutgoingFileTransferPreviewAsync(
        IReadOnlyList<string> paths,
        int maxFiles,
        Func<string, string> destinationFormatter)
    {
        return Task.Run(() =>
        {
            RemoteFilePastePlan plan = RemoteViewerClient.CreateFilePastePlan(
                paths,
                maxFiles,
                File.Exists,
                Directory.Exists,
                includeDirectories: true);
            return FileTransferConfirmation.CreatePreview(
                plan,
                (_item, transferName) => destinationFormatter(transferName));
        });
    }

    private bool ConfirmOutgoingFileTransfer(
        FileTransferConfirmationPreview preview,
        string title,
        string actionText)
    {
        if (preview.Plan.Files.Count == 0)
        {
            SetViewerStatus("没有可传输的文件。", MutedTextColor);
            return false;
        }

        return FileTransferConfirmation.Confirm(
            this,
            title,
            actionText,
            preview.Items,
            preview.Note);
    }

    private bool ConfirmRemoteClipboardFileTransfer(
        IReadOnlyList<FileTransferConfirmationItem> items,
        string? note)
    {
        if (_isClosing || IsDisposed || !IsHandleCreated || items.Count == 0)
        {
            return false;
        }

        bool confirmed = false;
        void ConfirmOnUi()
        {
            if (_isClosing || IsDisposed)
            {
                confirmed = false;
                return;
            }

            SetViewerStatus($"远端准备取回 {items.Count} 项文件，等待确认。", MutedTextColor);
            confirmed = FileTransferConfirmation.Confirm(
                GetRemoteFileConfirmationOwner(),
                "确认取回远端文件",
                "被控端准备把以下文件传回本机。确认后会保存到本机接收目录，并自动放入本机文件剪贴板，可在资源管理器按 Ctrl+V。",
                items,
                note);
            SetViewerStatus(
                confirmed
                    ? "已确认取回远端文件，正在接收…"
                    : "已取消取回远端文件。",
                confirmed ? MutedTextColor : DangerColor);
        }

        try
        {
            if (InvokeRequired)
            {
                Invoke((MethodInvoker)ConfirmOnUi);
            }
            else
            {
                ConfirmOnUi();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }

        return confirmed;
    }

    private IWin32Window GetRemoteFileConfirmationOwner()
    {
        if (_viewerWindow is { IsDisposed: false, Visible: true } viewerWindow)
        {
            return viewerWindow;
        }

        return this;
    }

    private async Task PullRemoteClipboardFilesAsync()
    {
        if (_pullingRemoteFilesFromMain || _viewerClient.IsRemoteClipboardFileRequestPending)
        {
            SetViewerStatus("远端文件正在取回，请等待当前操作完成。", MutedTextColor);
            return;
        }

        if (!CanConnectedViewer(RemoteDeviceCapabilities.FileSend))
        {
            SetViewerStatus("远程设备未声明文件回传能力。", MutedTextColor);
            return;
        }

        _remoteFilePullStatusStage = RemoteFilePullStatusStage.Waiting;
        SetRemoteFilePullPending(true);
        try
        {
            SetViewerStatus(
                $"正在请求取回远端剪贴板文件…（{RemoteFilePullUi.ShortcutText}）",
                MutedTextColor);
            bool requested = await _viewerClient.RequestRemoteClipboardFilesAsync();
            if (!requested && !_viewerClient.IsRemoteClipboardFileRequestPending)
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
                SetViewerStatus($"取回远端文件失败：{ex.Message}", DangerColor);
            }
        }
        finally
        {
            if (!_isClosing &&
                !IsDisposed &&
                !_viewerClient.IsRemoteClipboardFileRequestPending)
            {
                SetRemoteFilePullPending(false);
            }
        }
    }

    private void OnViewerCaptureTargetsReceived(
        CaptureTargetsUpdate update)
    {
        OnUi(() =>
        {
            if (!_viewerClient.IsCurrentCaptureTargetsUpdate(
                    update))
            {
                return;
            }

            IReadOnlyList<CaptureTargetInfo> targets =
                update.Targets;
            string? restoreTargetId = _pendingViewerCaptureTargetRestore ? _settings.Viewer.CaptureTargetId : null;
            CaptureTargetInfo? previousSelected =
                _viewerCaptureTargetBox.SelectedItem as
                    CaptureTargetInfo;
            string? selectedId = restoreTargetId ??
                previousSelected?.Id;
            CaptureTargetInfo? selectedTarget = null;
            _updatingViewerCaptureTargets = true;
            try
            {
                _viewerCaptureTargetBox.Items.Clear();
                foreach (CaptureTargetInfo target in targets)
                {
                    _viewerCaptureTargetBox.Items.Add(target);
                }

                selectedTarget = targets.FirstOrDefault(target =>
                    string.Equals(
                        target.Id,
                        selectedId,
                        StringComparison.OrdinalIgnoreCase));
                if (selectedTarget is null &&
                    restoreTargetId is null &&
                    previousSelected is not null)
                {
                    // A physical target disappears from the refreshed list.
                    // Retain the host-owned selection until its dedicated
                    // availability transition arrives; choosing the first
                    // list item here would falsely imply an aggregate/primary
                    // fallback that the host deliberately does not perform.
                    selectedTarget = previousSelected;
                    _viewerCaptureTargetBox.Items.Add(
                        previousSelected);
                }

                selectedTarget ??= targets.FirstOrDefault();
                if (selectedTarget is not null)
                {
                    _viewerCaptureTargetBox.SelectedItem = selectedTarget;
                }
            }
            finally
            {
                _updatingViewerCaptureTargets = false;
            }

            if (selectedTarget is not null &&
                restoreTargetId is not null &&
                string.Equals(selectedTarget.Id, restoreTargetId, StringComparison.OrdinalIgnoreCase))
            {
                _pendingViewerCaptureTargetRestore = false;
                _ = _viewerClient.SelectCaptureTargetAsync(selectedTarget.Id);
            }
            else if (restoreTargetId is not null)
            {
                _pendingViewerCaptureTargetRestore = false;
            }

            _viewerWindow?.SetCaptureTargets(
                targets,
                (_viewerCaptureTargetBox.SelectedItem as
                    CaptureTargetInfo)?.Id);
        });
    }

    private void OnViewerCaptureTargetAvailabilityChanged(
        CaptureTargetAvailabilityUpdate update)
    {
        OnUi(() =>
        {
            if (!_viewerClient
                    .IsCurrentCaptureTargetAvailabilityUpdate(
                        update))
            {
                return;
            }

            CaptureTargetInfo displayTarget =
                update.IsAvailable
                    ? update.Target
                    : update.Target with
                    {
                        DisplayName =
                            update.Target.DisplayName +
                            "（暂不可用）"
                    };
            SelectViewerCaptureTarget(displayTarget);
            SetViewerStatus(
                update.DisplayMessage,
                update.IsAvailable
                    ? SuccessTextColor
                    : DangerColor);
        });
    }

    private void OnViewerClipboardStatusReceived(
        string message)
    {
        // A modern viewer has a dedicated capture-target status path. Keep
        // the client's legacy ClipboardStatus event intact for compatibility,
        // but do not label its machine envelope as a clipboard operation.
        if (CaptureTargetAvailabilityStatusCodec.TryParse(
                message,
                out _))
        {
            return;
        }

        OnUi(() => SetViewerStatus(
            message,
            MutedTextColor));
    }

    private void OnViewerDeviceInfoReceived(
        RemoteDeviceInfoUpdate update)
    {
        if (!_viewerClient.IsCurrentDeviceInfoUpdate(update))
        {
            return;
        }

        Interlocked.Exchange(
            ref _lastAcceptedViewerConnectionGeneration,
            update.ConnectionGeneration);

        ViewerReconnectIntent? qualifiedIntent = null;
        bool scheduleStabilityCheck = false;
        lock (_viewerReconnectLock)
        {
            long reconnectAttemptOrdinal =
                _viewerReconnectConnecting &&
                _viewerReconnectIntent is { } reconnecting
                    ? reconnecting
                        .ActiveReconnectAttemptOrdinal
                    : 0;
            ViewerReconnectQualificationResult qualification =
                _viewerReconnectQualification
                    .ObserveDeviceInfo(
                        update.ConnectionGeneration,
                        reconnectAttemptOrdinal);
            if (qualification.Accepted &&
                qualification.Connection is { } connection)
            {
                if (_viewerReconnectIntent is null &&
                    qualification.IntentCreated)
                {
                    _viewerReconnectIntent =
                        new ViewerReconnectIntent(
                            qualification.IntentId,
                            connection);
                    _nextViewerReconnectGeneration = Math.Max(
                        _nextViewerReconnectGeneration,
                        qualification.IntentId);
                    _viewerReconnectTask = null;
                    _viewerReconnectRequested = false;
                    _viewerReconnectConnecting = false;
                    _pendingViewerConnection = null;
                }

                if (_viewerReconnectIntent is { } intent &&
                    intent.Generation ==
                        qualification.IntentId &&
                    _viewerReconnectQualification.IsCurrent(
                        intent.Generation,
                        update.ConnectionGeneration))
                {
                    qualifiedIntent = intent;
                    scheduleStabilityCheck =
                        RegisterQualifiedViewerConnectionLocked(
                            intent,
                            update.ConnectionGeneration,
                            qualification
                                .ReconnectAttemptOrdinal);
                }
            }
        }

        if (scheduleStabilityCheck &&
            qualifiedIntent is not null)
        {
            _ = RunViewerReconnectStabilityWindowAsync(
                qualifiedIntent,
                update.ConnectionGeneration);
        }

        RegisterViewerTelemetryConnection(
            update.ConnectionGeneration,
            qualifiedIntent?.Generation ??
                GetCurrentViewerTelemetryIntentGeneration());

        OnUi(() =>
        {
            if (!_viewerClient.IsCurrentDeviceInfoUpdate(
                    update))
            {
                return;
            }

            RemoteDeviceDescriptor device = update.Device;
            _lastConnectedDeviceInfo = device;
            _connectedViewerCapabilities = NormalizeCapabilities(device.Capabilities);
            ApplyViewerCapabilityState(_viewerClient.IsConnected);
            _viewerWindow?.SetRemotePlatform(device.Platform);
            if (_viewerClient.IsConnected &&
                !_connectedViewerCapabilities.HasFlag(RemoteDeviceCapabilities.InputControl))
            {
                SetViewerStatus(
                    $"已加密连接 | 仅观看：远程设备未声明输入控制能力。远端版本 {RemoteDeskBuildInfo.FormatBuildStamp(device.BuildStamp)}",
                    MutedTextColor);
            }
            else if (_viewerClient.IsConnected)
            {
                SetViewerStatus(
                    $"已加密连接 | 远端版本 {RemoteDeskBuildInfo.FormatBuildStamp(device.BuildStamp)}",
                    SuccessTextColor);
            }

            RememberCurrentViewerDevice();
        });
    }

    private void OnViewerCaptureTargetChanged(
        CaptureTargetChangedUpdate update)
    {
        OnUi(() =>
        {
            if (!_viewerClient
                    .IsCurrentCaptureTargetChangedUpdate(
                        update))
            {
                return;
            }

            CaptureTargetInfo target = update.Target;
            SelectViewerCaptureTarget(target);
            _settings.Viewer.CaptureTargetId = target.Id;
            SaveSettingsFromUi();
            string inputMode = NormalizeCapabilities(_connectedViewerCapabilities).HasFlag(RemoteDeviceCapabilities.InputControl)
                ? string.Empty
                : " | 仅观看";
            SetViewerStatus($"已加密连接{inputMode} | {target.DisplayName}", SuccessTextColor);
        });
    }

    private void SelectViewerCaptureTarget(CaptureTargetInfo target)
    {
        _updatingViewerCaptureTargets = true;
        try
        {
            for (int index = 0; index < _viewerCaptureTargetBox.Items.Count; index++)
            {
                if (_viewerCaptureTargetBox.Items[index] is CaptureTargetInfo current &&
                    string.Equals(current.Id, target.Id, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(
                            current.DisplayName,
                            target.DisplayName,
                            StringComparison.Ordinal))
                    {
                        _viewerCaptureTargetBox.Items[index] =
                            target;
                    }

                    _viewerCaptureTargetBox.SelectedIndex = index;
                    return;
                }
            }

            _viewerCaptureTargetBox.Items.Add(target);
            _viewerCaptureTargetBox.SelectedItem = target;
        }
        finally
        {
            _updatingViewerCaptureTargets = false;
        }
    }

    private static RemoteDeviceCapabilities DefaultViewerCapabilities =>
        RemoteDeviceCapabilityInfo.LegacyWindows(canRemoteStart: false);

    private void PrepareViewerCapabilitiesForConnection()
    {
        RemoteDeviceListItem? selectedDevice = GetSelectedRemoteDevice();
        _lastConnectedDeviceInfo = null;
        _connectedViewerCapabilities = NormalizeCapabilities(selectedDevice?.Capabilities ?? DefaultViewerCapabilities);
        ApplyViewerCapabilityState(_viewerClient.IsConnected);
    }

    private void ApplyViewerCapabilityState(bool connected)
    {
        RemoteDeviceCapabilities capabilities = NormalizeCapabilities(_connectedViewerCapabilities);
        _viewerCaptureTargetBox.Enabled = connected && capabilities.HasFlag(RemoteDeviceCapabilities.CaptureTargetSelection);
        _sendClipboardButton.Enabled = connected && capabilities.HasFlag(RemoteDeviceCapabilities.ClipboardText);
        _readClipboardButton.Enabled = connected && capabilities.HasFlag(RemoteDeviceCapabilities.ClipboardText);
        _sendFileButton.Enabled = connected && capabilities.HasFlag(RemoteDeviceCapabilities.FileReceive);
        _pasteFilesButton.Enabled = !_pastingClipboardFilesFromMain &&
            connected &&
            capabilities.HasFlag(RemoteDeviceCapabilities.FileReceive);
        _pullRemoteFilesButton.Enabled = !_pullingRemoteFilesFromMain &&
            connected &&
            capabilities.HasFlag(RemoteDeviceCapabilities.FileSend);
        _remoteUpdateButton.Enabled =
            connected &&
            RemoteUpdater.CanApplyRemoteUpdate &&
            capabilities.HasFlag(RemoteDeviceCapabilities.RemoteUpdate);
        _viewerWindow?.SetInputEnabled(connected && capabilities.HasFlag(RemoteDeviceCapabilities.InputControl));
        _viewerWindow?.SetClipboardTextEnabled(connected && capabilities.HasFlag(RemoteDeviceCapabilities.ClipboardText));
        _viewerWindow?.SetFilePasteEnabled(connected && capabilities.HasFlag(RemoteDeviceCapabilities.FileReceive));
        _viewerWindow?.SetFileDropPasteEnabled(connected && capabilities.HasFlag(RemoteDeviceCapabilities.FileDropPaste));
        _viewerWindow?.SetRemoteFilePullEnabled(connected && capabilities.HasFlag(RemoteDeviceCapabilities.FileSend));
        _viewerWindow?.SetCaptureTargetSelectionEnabled(
            connected &&
            capabilities.HasFlag(
                RemoteDeviceCapabilities
                    .CaptureTargetSelection));
        UpdateViewerCapabilityToolTips(connected, capabilities);
    }

    private void UpdateViewerCapabilityToolTips(bool connected, RemoteDeviceCapabilities capabilities)
    {
        string disconnected = "连接成功后可用。";
        string checksumSuffix = capabilities.HasFlag(RemoteDeviceCapabilities.FileChecksum)
            ? " 支持时会自动校验 SHA-256。"
            : string.Empty;
        SetToolTip(
            _sendClipboardButton,
            connected && capabilities.HasFlag(RemoteDeviceCapabilities.ClipboardText)
                ? "把本机文本剪贴板写入远端；远程窗口 Ctrl+V 使用远端剪贴板。"
                : disconnected);
        SetToolTip(
            _readClipboardButton,
            connected && capabilities.HasFlag(RemoteDeviceCapabilities.ClipboardText)
                ? "读取远端文本剪贴板到本机。远程窗口 Ctrl+C/Ctrl+X 后也会自动读取。"
                : disconnected);
        SetToolTip(
            _sendFileButton,
            connected && capabilities.HasFlag(RemoteDeviceCapabilities.FileReceive)
                ? $"选择一个本机文件发送到远端下载目录。{checksumSuffix}"
                : disconnected);
        SetToolTip(
            _pasteFilesButton,
            connected && capabilities.HasFlag(RemoteDeviceCapabilities.FileReceive)
                ? capabilities.HasFlag(RemoteDeviceCapabilities.FileDropPaste)
                    ? $"把资源管理器已复制的本机文件发送到远端；远程窗口拖放会尝试粘贴到远端当前位置。{checksumSuffix}"
                    : $"把资源管理器已复制的本机文件发送到远端下载目录。{checksumSuffix}"
                : disconnected);
        SetToolTip(
            _pullRemoteFilesButton,
            connected && capabilities.HasFlag(RemoteDeviceCapabilities.FileSend)
                ? RemoteFilePullUi.BuildAvailableToolTip(
                    capabilities.HasFlag(RemoteDeviceCapabilities.FileChecksum))
                : disconnected);
        SetToolTip(
            _remoteUpdateButton,
            connected && capabilities.HasFlag(RemoteDeviceCapabilities.RemoteUpdate)
                ? "自动比较本机和远端版本；本机较新则推送更新远端，远端较新则拉回更新本机。连接会短暂断开。"
                : connected
                    ? "当前远程设备不支持远程自更新。"
                    : disconnected);
        SetToolTip(
            _viewerCaptureTargetBox,
            connected && capabilities.HasFlag(RemoteDeviceCapabilities.CaptureTargetSelection)
                ? "切换被控端捕获的屏幕。"
                : disconnected);
    }

    private bool CanConnectedViewer(RemoteDeviceCapabilities capability)
    {
        return _viewerClient.IsConnected && NormalizeCapabilities(_connectedViewerCapabilities).HasFlag(capability);
    }

    private static RemoteDeviceCapabilities NormalizeCapabilities(RemoteDeviceCapabilities capabilities)
    {
        return capabilities == RemoteDeviceCapabilities.None ? DefaultViewerCapabilities : capabilities;
    }

    private void ClearViewerCaptureTargets()
    {
        _updatingViewerCaptureTargets = true;
        try
        {
            _viewerCaptureTargetBox.Items.Clear();
        }
        finally
        {
            _updatingViewerCaptureTargets = false;
        }

        _viewerWindow?.SetCaptureTargets(
            Array.Empty<CaptureTargetInfo>(),
            selectedTargetId: null);
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
                if (!IsHandleCreated)
                {
                    return;
                }

                BeginInvoke((Action)(() => RunUiAction(action)));
            }
            else
            {
                RunUiAction(action);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private void RunUiAction(Action action)
    {
        if (_isClosing || IsDisposed)
        {
            return;
        }

        try
        {
            action();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private TabPage CreatePage(string title)
    {
        return new TabPage(title)
        {
            AutoScroll = true,
            BackColor = AppBackColor,
            Padding = new Padding(0)
        };
    }

    private TableLayoutPanel CreateSection(
        string title,
        string? subtitle = null,
        bool sizeToContent = false)
    {
        var shell = new BufferedTableLayoutPanel
        {
            Dock = sizeToContent
                ? DockStyle.Top
                : DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = sizeToContent
                ? AutoSizeMode.GrowAndShrink
                : AutoSizeMode.GrowOnly,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = SurfaceBackColor,
            Margin = new Padding(0, 0, 12, 0)
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(
            sizeToContent
                ? SizeType.AutoSize
                : SizeType.Percent,
            sizeToContent ? 0 : 100));
        shell.Paint += (_, args) =>
        {
            using var pen = new Pen(PanelBorderColor);
            Rectangle bounds = shell.ClientRectangle;
            bounds.Width -= 1;
            bounds.Height -= 1;
            args.Graphics.DrawRectangle(pen, bounds);
        };

        var header = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = string.IsNullOrWhiteSpace(subtitle) ? 1 : 2,
            BackColor = SurfaceMutedColor,
            Padding = new Padding(14, 10, 14, 9),
            Margin = new Padding(1, 1, 1, 0)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var titleLabel = new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = SurfaceMutedColor,
            ForeColor = TextColor,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0)
        };
        header.Controls.Add(titleLabel, 0, 0);
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            header.Controls.Add(new Label
            {
                Text = subtitle,
                Dock = DockStyle.Top,
                AutoSize = true,
                BackColor = SurfaceMutedColor,
                ForeColor = MutedTextColor,
                Font = new Font(Font.FontFamily, 8.5F, FontStyle.Regular),
                Margin = new Padding(0, 2, 0, 0)
            }, 0, 1);
        }

        var content = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = sizeToContent
                ? AutoSizeMode.GrowAndShrink
                : AutoSizeMode.GrowOnly,
            ColumnCount = 2,
            Padding = new Padding(14),
            BackColor = SurfaceBackColor,
            Margin = new Padding(1, 0, 1, 1)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        shell.Controls.Add(header, 0, 0);
        shell.Controls.Add(content, 0, 1);
        return content;
    }

    private static NumericUpDown CreatePortInput()
    {
        var input = new NumericUpDown
        {
            Minimum = 1024,
            Maximum = 65535,
            Value = Protocol.DefaultPort,
            Width = 96
        };
        StyleInput(input);
        return input;
    }

    private static TextBox CreatePasswordInput()
    {
        var input = new TextBox
        {
            Width = 180,
            PlaceholderText = "连接口令",
            UseSystemPasswordChar = true
        };
        StyleInput(input);
        return input;
    }

    private static Label CreateToolbarLabel(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = MutedTextColor,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(8, 8, 4, 0)
        };
    }

    internal static FlowLayoutPanel
        CreateNonWrappingToolbarField(
            string labelText,
            Control input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(labelText);
        ArgumentNullException.ThrowIfNull(input);

        var field = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0),
            Margin = new Padding(0),
            TabStop = false
        };
        field.Controls.Add(CreateToolbarLabel(labelText));
        field.Controls.Add(input);
        return field;
    }

    private static Label CreateMutedLabel(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = MutedTextColor,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(8, 6, 2, 0)
        };
    }

    private static Label CreateFieldLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            AutoSize = true,
            ForeColor = MutedTextColor,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 7, 8, 7)
        };
    }

    private static Label CreateStatusBadge(string text, Color backColor, Color foreColor)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            BackColor = backColor,
            ForeColor = foreColor,
            Padding = new Padding(11, 7, 11, 7),
            Margin = new Padding(8, 3, 0, 0),
            TextAlign = ContentAlignment.MiddleCenter
        };
    }

    private static Label CreateStatusLine(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoEllipsis = false,
            ForeColor = MutedTextColor,
            TextAlign = ContentAlignment.TopLeft,
            Margin = new Padding(0),
            UseMnemonic = false
        };
    }

    private static Button CreatePrimaryButton(string text)
    {
        return CreateButton(text, PrimaryColor, Color.White, PrimaryColor);
    }

    private static Button CreateSecondaryButton(string text)
    {
        return CreateButton(text, SurfaceBackColor, TextColor, PanelBorderColor);
    }

    private static Button CreateButton(string text, Color backColor, Color foreColor, Color borderColor)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(96, 36),
            Padding = new Padding(13, 5, 13, 5),
            Margin = new Padding(0, 0, 8, 8),
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = foreColor,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = borderColor;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = GetButtonHoverColor(backColor);
        button.FlatAppearance.MouseDownBackColor = GetButtonPressedColor(backColor);
        return button;
    }

    private static void ApplyButtonStyle(Button button, Color backColor, Color foreColor, Color borderColor)
    {
        button.BackColor = backColor;
        button.ForeColor = foreColor;
        button.FlatAppearance.BorderColor = borderColor;
        button.FlatAppearance.MouseOverBackColor = GetButtonHoverColor(backColor);
        button.FlatAppearance.MouseDownBackColor = GetButtonPressedColor(backColor);
        button.UseVisualStyleBackColor = false;
    }

    private static Color GetButtonHoverColor(Color backColor)
    {
        if (backColor == PrimaryColor)
        {
            return Color.FromArgb(29, 78, 216);
        }

        if (backColor == DangerColor)
        {
            return Color.FromArgb(185, 28, 28);
        }

        return Color.FromArgb(248, 250, 252);
    }

    private static Color GetButtonPressedColor(Color backColor)
    {
        if (backColor == PrimaryColor)
        {
            return Color.FromArgb(30, 64, 175);
        }

        if (backColor == DangerColor)
        {
            return Color.FromArgb(153, 27, 27);
        }

        return Color.FromArgb(241, 245, 249);
    }

    private static void SetStatusBadge(Label label, string text, Color backColor, Color foreColor)
    {
        label.Text = text;
        label.BackColor = backColor;
        label.ForeColor = foreColor;
    }

    private static void StyleInput(Control control)
    {
        control.Margin = new Padding(0, 3, 12, 3);

        switch (control)
        {
            case TextBox textBox:
                textBox.BorderStyle = BorderStyle.FixedSingle;
                textBox.BackColor = SurfaceBackColor;
                textBox.ForeColor = TextColor;
                textBox.MinimumSize = new Size(0, 30);
                break;
            case ComboBox comboBox:
                comboBox.FlatStyle = FlatStyle.Flat;
                comboBox.BackColor = SurfaceBackColor;
                comboBox.ForeColor = TextColor;
                comboBox.MinimumSize = new Size(0, 30);
                break;
            case NumericUpDown numericUpDown:
                numericUpDown.BorderStyle = BorderStyle.FixedSingle;
                numericUpDown.BackColor = SurfaceBackColor;
                numericUpDown.ForeColor = TextColor;
                numericUpDown.MinimumSize = new Size(0, 30);
                break;
            case TrackBar trackBar:
                trackBar.Height = 32;
                break;
            case CheckBox checkBox:
                checkBox.ForeColor = TextColor;
                checkBox.Margin = new Padding(0, 7, 12, 7);
                checkBox.Cursor = Cursors.Hand;
                break;
        }
    }

    private static void AddSettingRow(
        TableLayoutPanel table,
        int row,
        string label,
        Control control)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        StyleInput(control);
        table.Controls.Add(CreateFieldLabel(label), 0, row);
        table.Controls.Add(control, 1, row);
    }

    private sealed class BufferedTabControl : TabControl
    {
        public BufferedTabControl()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true);
        }
    }

    private sealed class BufferedTableLayoutPanel : TableLayoutPanel
    {
        public BufferedTableLayoutPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true);
        }
    }

    private sealed class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true);
        }
    }

    private sealed class ViewerVideoModeItem(ViewerVideoMode mode, string label)
    {
        public ViewerVideoMode Mode { get; } = mode;

        public override string ToString()
        {
            return label;
        }
    }

    internal sealed class RemoteDeviceListItem
    {
        private RemoteDeviceListItem(
            string machineName,
            string? remark,
            string address,
            int port,
            string captureTarget,
            string platform,
            RemoteDeviceCapabilities capabilities,
            string? buildStamp,
            bool isHostRunning,
            bool canRemoteStart,
            bool isSaved,
            bool isSavedOnly,
            DateTimeOffset? lastConnectedAt,
            string? deviceId = null)
        {
            MachineName = machineName;
            DeviceId = RemoteDeviceIdentity.Normalize(deviceId);
            Remark = AppSettingsService
                .NormalizeSavedDeviceRemark(remark);
            Address = address;
            Port = port;
            CaptureTarget = captureTarget;
            Platform = platform;
            Capabilities = capabilities;
            BuildStamp = RemoteDeskBuildInfo.NormalizeBuildStamp(buildStamp);
            IsHostRunning = isHostRunning;
            CanRemoteStart = canRemoteStart;
            IsSaved = isSaved;
            IsSavedOnly = isSavedOnly;
            LastConnectedAt = lastConnectedAt;
        }

        public string MachineName { get; }
        public string? DeviceId { get; }

        public string? Remark { get; }

        public string DisplayName =>
            string.IsNullOrWhiteSpace(Remark)
                ? MachineName
                : Remark;

        public string Address { get; }

        public int Port { get; }

        public string CaptureTarget { get; }

        public string Platform { get; }

        public RemoteDeviceCapabilities Capabilities { get; }

        public string? BuildStamp { get; }

        public bool IsHostRunning { get; }

        public bool CanRemoteStart { get; }

        public bool IsSaved { get; }

        public bool IsSavedOnly { get; }

        public DateTimeOffset? LastConnectedAt { get; }

        public bool CanOpenViewerNow => IsSavedOnly || IsHostRunning || CanRemoteStart;

        public static RemoteDeviceListItem FromDiscovered(DiscoveredHost host, SavedRemoteDevice? savedDevice)
        {
            return new RemoteDeviceListItem(
                string.IsNullOrWhiteSpace(host.MachineName) ? host.Address : host.MachineName,
                savedDevice?.Remark,
                host.Address,
                host.Port,
                host.CaptureTarget,
                RemoteDevicePlatforms.Normalize(host.Platform, RemoteDevicePlatforms.Windows),
                host.Capabilities,
                host.BuildStamp,
                host.IsHostRunning,
                host.CanRemoteStart,
                isSaved: savedDevice is not null,
                isSavedOnly: false,
                GetSavedLastConnectedAt(savedDevice), host.DeviceId ?? savedDevice?.DeviceId);
        }

        public static RemoteDeviceListItem FromSaved(SavedRemoteDevice savedDevice)
        {
            string address = savedDevice.Address?.Trim() ?? string.Empty;
            string machineName = string.IsNullOrWhiteSpace(savedDevice.MachineName)
                ? address
                : savedDevice.MachineName.Trim();

            return new RemoteDeviceListItem(
                machineName,
                savedDevice.Remark,
                address,
                savedDevice.Port,
                savedDevice.CaptureTarget ?? string.Empty,
                RemoteDevicePlatforms.Normalize(savedDevice.Platform, RemoteDevicePlatforms.Windows),
                savedDevice.Capabilities == RemoteDeviceCapabilities.None
                    ? RemoteDeviceCapabilityInfo.LegacyWindows(canRemoteStart: false)
                    : savedDevice.Capabilities,
                savedDevice.BuildStamp,
                isHostRunning: false,
                canRemoteStart: false,
                isSaved: true,
                isSavedOnly: true,
                GetSavedLastConnectedAt(savedDevice), savedDevice.DeviceId);
        }

        private static DateTimeOffset? GetSavedLastConnectedAt(SavedRemoteDevice? savedDevice)
        {
            if (savedDevice is null || savedDevice.LastConnectedAt == default)
            {
                return null;
            }

            return savedDevice.LastConnectedAt;
        }

        public string GetShortStatus()
        {
            if (!IsSavedOnly)
            {
                if (!CanOpenViewerNow)
                {
                    return GetBlockedShortStatus();
                }

                string status = IsHostRunning ? "正在监听" : "可远程启动";
                return $"{status}，版本 {RemoteDeskBuildInfo.FormatShortBuildStamp(BuildStamp)}，{RemoteDeviceCapabilityInfo.Format(Capabilities)}";
            }

            return LastConnectedAt is null
                ? $"历史设备，版本 {RemoteDeskBuildInfo.FormatShortBuildStamp(BuildStamp)}"
                : $"历史设备，上次 {LastConnectedAt.Value.LocalDateTime:MM-dd HH:mm}，版本 {RemoteDeskBuildInfo.FormatShortBuildStamp(BuildStamp)}";
        }

        public string GetListSecondaryText(
            string status)
        {
            string machineNamePrefix =
                string.IsNullOrWhiteSpace(Remark)
                    ? string.Empty
                    : $"{MachineName} | ";
            return $"{machineNamePrefix}{Platform} | " +
                $"{Address}:{Port} | {status}";
        }

        public string GetLongStatus()
        {
            if (!IsSavedOnly)
            {
                if (!CanOpenViewerNow)
                {
                    return GetBlockedActionHint();
                }

                string status = IsHostRunning ? "被控端正在监听" : "软件已运行，可远程启动被控端";
                return $"{status}；平台 {Platform}；版本：{RemoteDeskBuildInfo.FormatBuildStamp(BuildStamp)}；能力：{RemoteDeviceCapabilityInfo.Format(Capabilities)}";
            }

            string capabilitiesText = RemoteDeviceCapabilityInfo.Format(Capabilities);
            return LastConnectedAt is null
                ? $"历史设备，平台 {Platform}，版本 {RemoteDeskBuildInfo.FormatBuildStamp(BuildStamp)}，能力：{capabilitiesText}，当前未扫描到在线状态"
                : $"历史设备，平台 {Platform}，版本 {RemoteDeskBuildInfo.FormatBuildStamp(BuildStamp)}，能力：{capabilitiesText}，上次连接 {LastConnectedAt.Value.LocalDateTime:yyyy-MM-dd HH:mm}，当前未扫描到在线状态";
        }

        public override string ToString()
        {
            string machineNameSuffix =
                string.IsNullOrWhiteSpace(Remark)
                    ? string.Empty
                    : $" / {MachineName}";
            return $"{DisplayName}{machineNameSuffix} " +
                $"[{Platform}] ({Address}:{Port}) - " +
                GetShortStatus();
        }

        public string GetBlockedActionText()
        {
            return string.Equals(Platform, RemoteDevicePlatforms.Android, StringComparison.OrdinalIgnoreCase)
                ? "等待手机启动"
                : "等待对方启动";
        }

        public string GetBlockedActionHint()
        {
            if (string.Equals(Platform, RemoteDevicePlatforms.Android, StringComparison.OrdinalIgnoreCase))
            {
                return "Android App 已在线，但手机端尚未启动被控端；请在手机上设置口令、点击“启动被控端”并授权屏幕录制后再连接。";
            }

            return "对方软件已在线，但被控端尚未监听，且未启用远程启动。请在对方机器启动被控端后再连接。";
        }

        private string GetBlockedShortStatus()
        {
            return string.Equals(Platform, RemoteDevicePlatforms.Android, StringComparison.OrdinalIgnoreCase)
                ? "等待手机录屏授权"
                : "仅软件运行，未开放远程启动";
        }
    }
}
