package com.remotedesk.agent;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.ActivityNotFoundException;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.ContentUris;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.database.Cursor;
import android.graphics.Color;
import android.graphics.Typeface;
import android.media.projection.MediaProjectionManager;
import android.media.projection.MediaProjectionConfig;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Environment;
import android.os.SystemClock;
import android.provider.MediaStore;
import android.provider.Settings;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowInsets;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONObject;

import java.io.File;
import java.net.Inet4Address;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class MainActivity extends Activity {
    private static final int REQUEST_MEDIA_PROJECTION = 1001;
    private static final Object DISCOVERY_PREVIEW_LOCK = new Object();
    private static final String STATE_VIEWER_ADDRESS = "main.viewer_address";
    private static final String STATE_SCROLL_X = "main.scroll_x";
    private static final String STATE_SCROLL_Y = "main.scroll_y";
    private static final int CONTENT_MARGIN_DP = 16;
    private static final int COLUMN_GAP_DP = 16;
    private static final int CONTROL_SPACING_DP = 8;

    private final ExecutorService discoveryExecutor = Executors.newSingleThreadExecutor();
    private MediaProjectionManager projectionManager;
    private TextView statusView;
    private TextView readinessView;
    private EditText passwordEdit;
    private EditText viewerAddressEdit;
    private EditText viewerPasswordEdit;
    private Button startHostButton;
    private Button returnToDesktopButton;
    private final AndroidHostLaunchPolicy hostLaunchPolicy = new AndroidHostLaunchPolicy();
    private final Runnable hostStartupCheck = this::checkHostStartup;
    private boolean activityResumed;
    private ScrollView scrollView;
    private LinearLayout mainContentLayout;
    private LinearLayout columnsLayout;
    private LinearLayout connectionColumn;
    private AndroidRelayPanel relayPanel;
    private AndroidConnectionHistoryPanel historyPanel;
    private AndroidLanDiscoveryPanel lanPanel;
    private int viewerLaunchEpoch;
    private boolean historyRestored;
    private LinearLayout statusColumn;
    private int appliedContentWidth = -1;
    private boolean adaptiveColumnsApplied;
    private boolean appliedTwoColumns;
    private static DatagramSocket discoverySocket;
    private static volatile boolean discoveryPreviewRunning;
    private static CountDownLatch discoveryPreviewStopped = new CountDownLatch(0);

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        AndroidDisplay.configureEdgeToEdge(this, true);
        projectionManager = (MediaProjectionManager) getSystemService(Context.MEDIA_PROJECTION_SERVICE);
        AndroidSessionLog.configure(this);
        AndroidSessionLog.info("MainActivity created; sdk=" + Build.VERSION.SDK_INT);
        AndroidSessionLog.info(AndroidVideoCodecDiagnostics.formatH264LogLine(
            AndroidVideoCodecDiagnostics.cachedH264Report()));

        statusView = new TextView(this);
        AndroidUiTheme.applyStatusBanner(
            this,
            statusView,
            getString(R.string.main_ready));

        readinessView = new TextView(this);
        AndroidUiTheme.styleReadiness(readinessView);

        passwordEdit = new EditText(this);
        passwordEdit.setHint("本机被控口令");
        passwordEdit.setSingleLine(true);
        passwordEdit.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        passwordEdit.setSaveEnabled(false);
        passwordEdit.setText(AndroidPasswordStore.load(this));
        AndroidUiTheme.styleInput(this, passwordEdit);

        viewerAddressEdit = new EditText(this);
        viewerAddressEdit.setHint("IP / 主机名（端口可省略）");
        viewerAddressEdit.setSingleLine(true);
        viewerAddressEdit.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        AndroidUiTheme.styleInput(this, viewerAddressEdit);

        viewerPasswordEdit = new EditText(this);
        viewerPasswordEdit.setHint("远端口令");
        viewerPasswordEdit.setSingleLine(true);
        viewerPasswordEdit.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        viewerPasswordEdit.setSaveEnabled(false);
        viewerPasswordEdit.setText(AndroidPasswordStore.loadViewer(this));
        AndroidUiTheme.styleInput(this, viewerPasswordEdit);

        Button viewerButton = new Button(this);
        viewerButton.setText("控制远端");
        viewerButton.setOnClickListener(view -> openViewer());
        AndroidUiTheme.styleButton(
            this,
            viewerButton,
            AndroidUiTheme.ButtonRole.PRIMARY);

        startHostButton = new Button(this);
        startHostButton.setText("启动被控端");
        startHostButton.setOnClickListener(view -> requestProjection());
        AndroidUiTheme.styleButton(
            this,
            startHostButton,
            AndroidUiTheme.ButtonRole.PRIMARY);

        returnToDesktopButton = new Button(this);
        returnToDesktopButton.setText("返回桌面，保持被控");
        returnToDesktopButton.setOnClickListener(view -> returnToDesktop());
        AndroidUiTheme.styleButton(this, returnToDesktopButton, AndroidUiTheme.ButtonRole.SECONDARY);

        Button presenceButton = new Button(this);
        presenceButton.setText("保持发现常驻");
        presenceButton.setOnClickListener(view -> startPresenceService());
        AndroidUiTheme.styleButton(
            this,
            presenceButton,
            AndroidUiTheme.ButtonRole.SECONDARY);

        Button stopButton = new Button(this);
        stopButton.setText("停止服务");
        AndroidUiTheme.styleButton(
            this,
            stopButton,
            AndroidUiTheme.ButtonRole.DANGER);
        stopButton.setOnClickListener(view -> requestStopService());

        Button receivedFilesButton = new Button(this);
        receivedFilesButton.setText("查看接收文件");
        receivedFilesButton.setOnClickListener(view -> showReceivedFiles());
        AndroidUiTheme.styleButton(
            this,
            receivedFilesButton,
            AndroidUiTheme.ButtonRole.SECONDARY);

        Button accessibilityButton = new Button(this);
        accessibilityButton.setText("打开无障碍设置");
        accessibilityButton.setOnClickListener(view -> openAccessibilitySettings());
        AndroidUiTheme.styleButton(
            this,
            accessibilityButton,
            AndroidUiTheme.ButtonRole.SECONDARY);

        Button notificationButton = new Button(this);
        notificationButton.setText("打开通知设置");
        notificationButton.setOnClickListener(view -> openNotificationSettings());
        AndroidUiTheme.styleButton(
            this,
            notificationButton,
            AndroidUiTheme.ButtonRole.SECONDARY);

        Button batteryOptimizationButton = new Button(this);
        batteryOptimizationButton.setText("打开电池优化设置");
        batteryOptimizationButton.setOnClickListener(view -> openBatteryOptimizationSettings());
        AndroidUiTheme.styleButton(
            this,
            batteryOptimizationButton,
            AndroidUiTheme.ButtonRole.SECONDARY);

        Button diagnosticLogButton = new Button(this);
        diagnosticLogButton.setText("导出诊断日志");
        diagnosticLogButton.setOnClickListener(view -> exportDiagnosticLog());
        AndroidUiTheme.styleButton(
            this,
            diagnosticLogButton,
            AndroidUiTheme.ButtonRole.SECONDARY);

        Button clearDiagnosticLogButton = new Button(this);
        clearDiagnosticLogButton.setText("清空诊断日志");
        clearDiagnosticLogButton.setOnClickListener(view -> clearDiagnosticLog());
        AndroidUiTheme.styleButton(
            this,
            clearDiagnosticLogButton,
            AndroidUiTheme.ButtonRole.DANGER);

        Button copyConnectionInfoButton = new Button(this);
        copyConnectionInfoButton.setText("复制连接信息");
        copyConnectionInfoButton.setOnClickListener(view -> copyConnectionInfo());
        AndroidUiTheme.styleButton(
            this,
            copyConnectionInfoButton,
            AndroidUiTheme.ButtonRole.SECONDARY);

        Button refreshButton = new Button(this);
        refreshButton.setText("刷新状态");
        refreshButton.setOnClickListener(view -> updateStatusPanel(currentHeadline()));
        AndroidUiTheme.styleButton(
            this,
            refreshButton,
            AndroidUiTheme.ButtonRole.SECONDARY);

        connectionColumn = createVerticalColumn();
        addColumnView(
            connectionColumn,
            AndroidUiTheme.createEyebrow(this, "远程控制"));
        addColumnView(
            connectionColumn,
            AndroidUiTheme.createSectionTitle(this, "连接另一台设备"));
        addColumnView(
            connectionColumn,
            AndroidUiTheme.createSectionSubtitle(
                this,
                "点选发现的设备，或填写 IP / 主机名；端口可自动探测。"));
        lanPanel = new AndroidLanDiscoveryPanel(this, () -> viewerAddressEdit.getText().toString(), this::openDiscoveredViewer);
        addColumnView(connectionColumn, lanPanel);
        Button addDevice = new Button(this); addDevice.setText("新增设备");
        AndroidUiTheme.styleButton(this, addDevice, AndroidUiTheme.ButtonRole.SECONDARY);
        addDevice.setOnClickListener(view -> historyPanel.add());
        addColumnView(connectionColumn, addDevice);
        addLabeledField(connectionColumn, "远端地址", viewerAddressEdit);
        addLabeledField(connectionColumn, "连接口令", viewerPasswordEdit);
        addColumnView(connectionColumn, viewerButton);
        historyPanel = new AndroidConnectionHistoryPanel(this, this::openHistoryViewer,
            this::fillHistoryNode, this::updateStatusPanel);
        addColumnView(connectionColumn, historyPanel);
        relayPanel = new AndroidRelayPanel(this, this::openRelayViewer);
        addColumnView(connectionColumn, relayPanel);

        addColumnDivider(connectionColumn);
        addColumnView(
            connectionColumn,
            AndroidUiTheme.createEyebrow(this, "本机被控"));
        addColumnView(
            connectionColumn,
            AndroidUiTheme.createSectionTitle(this, "共享这台 Android"));
        addColumnView(
            connectionColumn,
            AndroidUiTheme.createSectionSubtitle(
                this,
                "录屏授权时请选择“整个屏幕”。启动成功后自动返回桌面；无障碍权限用于远程触控与文本输入。"));
        addLabeledField(connectionColumn, "本机访问口令", passwordEdit);
        CheckBox compatibleHost = new CheckBox(this);
        compatibleHost.setText("免重复录屏授权（无障碍兼容模式）");
        compatibleHost.setChecked(AndroidHostResume.compatibleSelected(this));
        compatibleHost.setEnabled(Build.VERSION.SDK_INT >= 30);
        compatibleHost.setOnCheckedChangeListener((button, checked) -> {
            if (RemoteDeskForegroundService.isHostRunning() && checked != AndroidHostResume.compatibleSelected(this)) {
                compatibleHost.setChecked(!checked);
                Toast.makeText(this, "请先停止被控，再切换模式。", Toast.LENGTH_SHORT).show();
                return;
            }
            getSharedPreferences(RemoteDeskForegroundService.PREFS_NAME, MODE_PRIVATE)
                .edit().putBoolean(AndroidHostResume.PREF_COMPATIBLE, checked).apply();
            if (!checked) AndroidHostResume.setArmed(this, false);
        });
        addColumnView(connectionColumn, compatibleHost);
        addColumnView(connectionColumn, AndroidUiTheme.createSectionSubtitle(this,
            "Android 11 及以上可用。开启无障碍后不再弹录屏授权，锁屏后仍可连接，约 3 FPS；系统保护的内容除外。关闭此项使用 H.264 流畅模式。"));
        Button unlockPinButton = new Button(this);
        unlockPinButton.setText("设置 / 清除自动解锁 PIN");
        AndroidUiTheme.styleButton(this, unlockPinButton, AndroidUiTheme.ButtonRole.SECONDARY);
        unlockPinButton.setOnClickListener(view -> configureUnlockPin());
        addColumnView(connectionColumn, unlockPinButton);
        CheckBox keepScreenAwake = new CheckBox(this);
        keepScreenAwake.setText("远控连接期间保持屏幕亮起");
        keepScreenAwake.setChecked(RemoteDeskForegroundService.shouldKeepScreenAwake(this));
        keepScreenAwake.setOnCheckedChangeListener((button, checked) -> {
            getSharedPreferences(RemoteDeskForegroundService.PREFS_NAME, MODE_PRIVATE)
                .edit().putBoolean(RemoteDeskForegroundService.PREF_KEEP_SCREEN_AWAKE, checked).apply();
            if (RemoteDeskForegroundService.isServiceRunning()) {
                startService(new Intent(this, RemoteDeskForegroundService.class)
                    .putExtra(RemoteDeskForegroundService.EXTRA_REFRESH_POWER, true));
            }
        });
        addColumnView(connectionColumn, keepScreenAwake);
        addColumnView(connectionColumn, AndroidUiTheme.createSectionSubtitle(this,
            "无人连接时释放亮屏和性能锁，降低耗电及系统清理风险。兼容模式不依赖录屏授权；H.264 在锁屏后可能需要重新授权。"));
        addColumnView(connectionColumn, startHostButton);
        addColumnView(connectionColumn, returnToDesktopButton);
        addColumnView(connectionColumn, AndroidUiTheme.createSectionSubtitle(this,
            "含口令的配置页可能被系统录屏保护遮黑。返回桌面即可查看其它内容；可通过应用通知回来管理连接。"));
        addColumnView(connectionColumn, presenceButton);
        addColumnView(connectionColumn, stopButton);

        statusColumn = createVerticalColumn();
        addColumnView(
            statusColumn,
            AndroidUiTheme.createEyebrow(this, "设备状态"));
        addColumnView(
            statusColumn,
            AndroidUiTheme.createSectionTitle(this, "就绪检查"));
        addColumnView(
            statusColumn,
            AndroidUiTheme.createSectionSubtitle(
                this,
                "集中查看录屏、通知、电池、编码和输入控制状态。"));
        addColumnView(statusColumn, readinessView);
        addColumnDivider(statusColumn);
        addColumnView(
            statusColumn,
            AndroidUiTheme.createSectionTitle(this, "工具与权限"));
        addColumnView(statusColumn, receivedFilesButton);
        addColumnView(statusColumn, accessibilityButton);
        addColumnView(statusColumn, notificationButton);
        addColumnView(statusColumn, batteryOptimizationButton);
        addColumnView(statusColumn, diagnosticLogButton);
        addColumnView(statusColumn, clearDiagnosticLogButton);
        addColumnView(statusColumn, copyConnectionInfoButton);
        addColumnView(statusColumn, refreshButton);

        columnsLayout = new LinearLayout(this);
        columnsLayout.setOrientation(LinearLayout.VERTICAL);
        columnsLayout.addView(connectionColumn, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT));
        columnsLayout.addView(statusColumn, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT));

        mainContentLayout = new LinearLayout(this);
        mainContentLayout.setOrientation(LinearLayout.VERTICAL);
        mainContentLayout.setGravity(Gravity.CENTER_HORIZONTAL);
        View appHeader = createAppHeader();
        LinearLayout.LayoutParams headerParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT);
        headerParams.bottomMargin = dp(12);
        mainContentLayout.addView(appHeader, headerParams);
        LinearLayout.LayoutParams statusParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT);
        statusParams.bottomMargin = dp(12);
        mainContentLayout.addView(statusView, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT));
        statusView.setLayoutParams(statusParams);
        mainContentLayout.addView(columnsLayout, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT));

        FrameLayout contentFrame = new FrameLayout(this);
        contentFrame.addView(mainContentLayout, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT,
            Gravity.TOP | Gravity.CENTER_HORIZONTAL));

        scrollView = new ScrollView(this);
        scrollView.setBackgroundColor(AndroidUiTheme.BACKGROUND);
        scrollView.setFillViewport(true);
        scrollView.setClipToPadding(false);
        scrollView.addView(contentFrame, new ScrollView.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT));
        scrollView.setOnApplyWindowInsetsListener(this::applyMainWindowInsets);
        scrollView.addOnLayoutChangeListener((view, left, top, right, bottom,
                                               oldLeft, oldTop, oldRight, oldBottom) ->
            updateMainAdaptiveLayout());
        setContentView(scrollView);
        scrollView.requestApplyInsets();

        restoreMainUiState(savedInstanceState);

        if (Build.VERSION.SDK_INT >= 33 &&
                checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED &&
                !getSharedPreferences(RemoteDeskForegroundService.PREFS_NAME, MODE_PRIVATE)
                    .getBoolean("notification.requested", false)) {
            getSharedPreferences(RemoteDeskForegroundService.PREFS_NAME, MODE_PRIVATE)
                .edit().putBoolean("notification.requested", true).apply();
            requestPermissions(new String[] { Manifest.permission.POST_NOTIFICATIONS }, 1002);
        }

        startDiscoveryPreview();
        updateStatusPanel(currentHeadline());
    }

    private int dp(float value) {
        return AndroidDisplay.dp(this, value);
    }

    private LinearLayout createVerticalColumn() {
        return AndroidUiTheme.createCard(this);
    }

    private void addColumnView(LinearLayout column, View child) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT);
        params.bottomMargin = dp(CONTROL_SPACING_DP);
        column.addView(child, params);
    }

    private void addLabeledField(LinearLayout column, String label, EditText field) {
        LinearLayout fieldGroup = new LinearLayout(this);
        fieldGroup.setOrientation(LinearLayout.VERTICAL);

        TextView labelView = AndroidUiTheme.createFieldLabel(this, label);
        LinearLayout.LayoutParams labelParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT);
        labelParams.bottomMargin = dp(6);
        fieldGroup.addView(labelView, labelParams);
        fieldGroup.addView(field, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT));
        addColumnView(column, fieldGroup);
    }

    private void addColumnDivider(LinearLayout column) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            dp(1));
        params.topMargin = dp(10);
        params.bottomMargin = dp(18);
        column.addView(AndroidUiTheme.createDivider(this), params);
    }

    private View createAppHeader() {
        LinearLayout header = new LinearLayout(this);
        header.setOrientation(LinearLayout.HORIZONTAL);
        header.setGravity(Gravity.CENTER_VERTICAL);
        header.setPadding(dp(18), dp(18), dp(18), dp(18));
        header.setBackground(AndroidUiTheme.shape(
            this,
            AndroidUiTheme.HEADER,
            20,
            AndroidUiTheme.HEADER,
            0));
        header.setElevation(dp(2));

        TextView mark = new TextView(this);
        mark.setText(R.string.brand_mark);
        mark.setTextColor(Color.WHITE);
        mark.setTextSize(15.0f);
        mark.setTypeface(Typeface.create("sans-serif-medium", Typeface.BOLD));
        mark.setGravity(Gravity.CENTER);
        mark.setBackground(AndroidUiTheme.shape(
            this,
            AndroidUiTheme.PRIMARY,
            14,
            AndroidUiTheme.PRIMARY,
            0));
        LinearLayout.LayoutParams markParams = new LinearLayout.LayoutParams(dp(52), dp(52));
        markParams.setMarginEnd(dp(14));
        header.addView(mark, markParams);

        LinearLayout copy = new LinearLayout(this);
        copy.setOrientation(LinearLayout.VERTICAL);

        TextView title = new TextView(this);
        title.setText(R.string.app_name);
        title.setTextColor(Color.WHITE);
        title.setTextSize(23.0f);
        title.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        copy.addView(title, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT));

        TextView subtitle = new TextView(this);
        subtitle.setText(R.string.header_subtitle);
        subtitle.setTextColor(AndroidUiTheme.HEADER_MUTED);
        subtitle.setTextSize(12.5f);
        LinearLayout.LayoutParams subtitleParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT);
        subtitleParams.topMargin = dp(1);
        copy.addView(subtitle, subtitleParams);

        TextView badge = new TextView(this);
        badge.setText(R.string.platform_badge);
        badge.setTextColor(Color.rgb(191, 219, 254));
        badge.setTextSize(10.0f);
        badge.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        badge.setLetterSpacing(0.08f);
        badge.setGravity(Gravity.CENTER);
        badge.setPadding(dp(9), dp(4), dp(9), dp(4));
        badge.setBackground(AndroidUiTheme.shape(
            this,
            AndroidUiTheme.HEADER_SURFACE,
            99,
            Color.rgb(51, 65, 85),
            1));
        LinearLayout.LayoutParams badgeParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WRAP_CONTENT,
            ViewGroup.LayoutParams.WRAP_CONTENT);
        badgeParams.topMargin = dp(8);
        badgeParams.gravity = Gravity.START;
        copy.addView(badge, badgeParams);

        header.addView(copy, new LinearLayout.LayoutParams(
            0,
            ViewGroup.LayoutParams.WRAP_CONTENT,
            1.0f));
        return header;
    }

    private WindowInsets applyMainWindowInsets(View view, WindowInsets windowInsets) {
        AndroidDisplay.SafeInsets insets = AndroidDisplay.safeInsets(windowInsets, true);
        view.setPadding(insets.left, insets.top, insets.right, insets.bottom);
        view.post(this::updateMainAdaptiveLayout);
        return windowInsets;
    }

    private void updateMainAdaptiveLayout() {
        if (scrollView == null || mainContentLayout == null || scrollView.getWidth() <= 0) {
            return;
        }

        int availableWidth = Math.max(
            0,
            scrollView.getWidth() - scrollView.getPaddingLeft() - scrollView.getPaddingRight());
        if (availableWidth == 0) {
            return;
        }

        float density = getResources().getDisplayMetrics().density;
        int availableWidthDp = Math.max(0, (int) Math.floor(availableWidth / density));
        int contentWidthDp = AndroidAdaptiveLayout.mainContentWidthDp(
            availableWidthDp,
            CONTENT_MARGIN_DP);
        int contentWidth = Math.max(1, dp(contentWidthDp));
        if (contentWidth != appliedContentWidth) {
            FrameLayout.LayoutParams contentParams =
                (FrameLayout.LayoutParams) mainContentLayout.getLayoutParams();
            contentParams.width = contentWidth;
            contentParams.height = ViewGroup.LayoutParams.WRAP_CONTENT;
            contentParams.gravity = Gravity.TOP | Gravity.CENTER_HORIZONTAL;
            contentParams.topMargin = dp(CONTENT_MARGIN_DP);
            contentParams.bottomMargin = dp(CONTENT_MARGIN_DP);
            mainContentLayout.setLayoutParams(contentParams);
            appliedContentWidth = contentWidth;
        }

        boolean useTwoColumns = AndroidAdaptiveLayout.useMainTwoColumns(
            availableWidthDp,
            getResources().getConfiguration().fontScale);
        if (adaptiveColumnsApplied && useTwoColumns == appliedTwoColumns) {
            return;
        }

        int gap = dp(COLUMN_GAP_DP);
        columnsLayout.setOrientation(useTwoColumns
            ? LinearLayout.HORIZONTAL
            : LinearLayout.VERTICAL);
        LinearLayout.LayoutParams connectionParams;
        LinearLayout.LayoutParams statusParams;
        if (useTwoColumns) {
            connectionParams = new LinearLayout.LayoutParams(
                0,
                ViewGroup.LayoutParams.WRAP_CONTENT,
                1.0f);
            statusParams = new LinearLayout.LayoutParams(
                0,
                ViewGroup.LayoutParams.WRAP_CONTENT,
                1.0f);
            connectionParams.setMarginEnd(gap / 2);
            statusParams.setMarginStart(gap - gap / 2);
        } else {
            connectionParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT);
            statusParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT);
            connectionParams.bottomMargin = gap;
        }
        connectionColumn.setLayoutParams(connectionParams);
        statusColumn.setLayoutParams(statusParams);
        adaptiveColumnsApplied = true;
        appliedTwoColumns = useTwoColumns;
    }

    private void restoreMainUiState(Bundle savedInstanceState) {
        if (savedInstanceState == null) {
            return;
        }

        viewerAddressEdit.setText(savedInstanceState.getString(
            STATE_VIEWER_ADDRESS,
            viewerAddressEdit.getText().toString()));
        int scrollX = savedInstanceState.getInt(STATE_SCROLL_X, 0);
        int scrollY = savedInstanceState.getInt(STATE_SCROLL_Y, 0);
        scrollView.post(() -> scrollView.scrollTo(scrollX, scrollY));
    }

    @Override
    protected void onSaveInstanceState(Bundle outState) {
        outState.putString(STATE_VIEWER_ADDRESS, viewerAddressEdit.getText().toString());
        outState.putInt(STATE_SCROLL_X, scrollView.getScrollX());
        outState.putInt(STATE_SCROLL_Y, scrollView.getScrollY());
        super.onSaveInstanceState(outState);
    }

    @Override
    protected void onResume() {
        super.onResume();
        activityResumed = true;
        if (historyPanel != null) historyPanel.refresh(nodes -> {
            if (historyRestored) return;
            historyRestored = true;
            if (viewerAddressEdit.getText().length() != 0) return;
            for (AndroidConnectionHistory.Node node : nodes) {
                if (!node.relay()) { fillHistoryNode(node); break; }
            }
        });
        if (lanPanel != null) lanPanel.active(true);
        AndroidHostResume.tryResume(this);
        if (relayPanel != null) relayPanel.active(true);
        if (RemoteDeskForegroundService.isServiceRunning()) {
            stopDiscoveryPreviewAndWait();
            updateStatusPanel(currentHeadline());
        } else {
            startDiscoveryPreview();
            updateStatusPanel(currentHeadline());
        }
        if (hostLaunchPolicy.isPending()) {
            statusView.removeCallbacks(hostStartupCheck);
            statusView.post(hostStartupCheck);
        }
    }

    @Override
    protected void onDestroy() {
        cancelHostStartupNavigation();
        if (relayPanel != null) relayPanel.close();
        if (historyPanel != null) historyPanel.close();
        if (lanPanel != null) lanPanel.close();
        stopDiscoveryPreviewAndWait();
        discoveryExecutor.shutdownNow();
        super.onDestroy();
    }

    @Override
    protected void onPause() {
        activityResumed = false;
        viewerLaunchEpoch++;
        if (lanPanel != null) lanPanel.active(false);
        cancelHostStartupNavigation();
        if (relayPanel != null) relayPanel.active(false);
        super.onPause();
    }

    private void openRelayViewer(AndroidRelay.Options target) {
        String password = viewerPasswordEdit.getText().toString().trim();
        if (password.isEmpty()) password = passwordEdit.getText().toString().trim();
        if (password.isEmpty()) { updateStatusPanel("请先填写目标设备的连接口令。"); return; }
        try {
            AndroidPasswordStore.saveViewer(this, password);
            startActivity(new Intent(this, RemoteDeskViewerActivity.class)
                .putExtra(RemoteDeskViewerActivity.EXTRA_HOST, target.serverAddress)
                .putExtra(RemoteDeskViewerActivity.EXTRA_PORT, target.port)
                .putExtra(RemoteDeskViewerActivity.EXTRA_RELAY_DEVICE_ID, target.deviceId));
        } catch (Exception ex) { updateStatusPanel("中转连接未启动，请检查口令保存状态。"); }
    }

    private void fillHistoryNode(AndroidConnectionHistory.Node node) {
        viewerAddressEdit.setText(node.address());
        viewerPasswordEdit.setText(node.password);
    }

    private void openHistoryViewer(AndroidConnectionHistory.Node node) {
        if (node.relay() || !node.autoPort) { launchHistoryViewer(node, null); return; }
        int generation = ++viewerLaunchEpoch;
        updateStatusPanel("正在查找 " + node.title() + " 的地址和端口…");
        lanPanel.locate(node, options -> {
            if (!activityResumed || viewerLaunchEpoch != generation) return;
            if (options.isEmpty()) { launchHistoryViewer(node, null); return; }
            lanPanel.showChoices(options, device -> {
                if (node.host.equals(device.host) && node.port == device.port) launchHistoryViewer(node, null);
                else confirmHistoryAddress(node, device);
            });
        });
    }

    private void launchHistoryViewer(AndroidConnectionHistory.Node node, AndroidLanDevice replacement) {
        // Pass only an opaque local ID, not a credential or relay access token.
        Intent intent = new Intent(this, RemoteDeskViewerActivity.class)
            .putExtra(RemoteDeskViewerActivity.EXTRA_HISTORY_ID, node.id);
        if (replacement != null) intent.putExtra(RemoteDeskViewerActivity.EXTRA_HISTORY_REDIRECT, true)
            .putExtra(RemoteDeskViewerActivity.EXTRA_HOST, replacement.host)
            .putExtra(RemoteDeskViewerActivity.EXTRA_PORT, replacement.port);
        startActivity(intent);
    }

    private void confirmHistoryAddress(AndroidConnectionHistory.Node node, AndroidLanDevice device) {
        new AlertDialog.Builder(this).setTitle("使用新地址连接？")
            .setMessage("历史设备：" + node.title() + "\n原地址：" + node.address() +
                "\n发现设备：" + device.name + "\n新地址：" + device.address() +
                "\n\n设备发现不能验证身份；请确认这是你的设备。连接成功后更新原记录，保留备注。")
            .setNegativeButton("取消", null)
            .setPositiveButton("更新地址并连接", (dialog, which) -> launchHistoryViewer(node, device)).show();
    }

    private void openDiscoveredViewer(AndroidLanDevice device) {
        viewerLaunchEpoch++;
        if (!device.listening) {
            new AlertDialog.Builder(this).setTitle(device.name)
                .setMessage("已发现 " + device.address() + "，但被控端尚未启动。请先在对方设备上启动被控端。")
                .setPositiveButton("知道了", null).show();
            return;
        }
        List<AndroidConnectionHistory.Node> recentConnections = historyPanel.entries();
        AndroidConnectionHistory.Node exact = AndroidLanDevice.exact(recentConnections, device);
        if (exact != null) { launchHistoryViewer(exact, null); return; }
        for (AndroidConnectionHistory.Node node : recentConnections)
            if (!node.relay() && !node.deviceId.isEmpty() && node.deviceId.equals(device.deviceId)) {
                confirmHistoryAddress(node, device); return;
            }
        List<AndroidConnectionHistory.Node> matches = new ArrayList<>();
        for (AndroidConnectionHistory.Node node : recentConnections)
            if (!node.relay() && device.advertised && !node.name.isEmpty() && node.name.equalsIgnoreCase(device.name)) matches.add(node);
        if (matches.size() == 1) { confirmHistoryAddress(matches.get(0), device); return; }
        EditText password = new EditText(this);
        password.setSingleLine(true); password.setSaveEnabled(false);
        password.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        password.setHint("对方设备的访问口令");
        AndroidUiTheme.styleInput(this, password);
        LinearLayout content = new LinearLayout(this); content.setOrientation(LinearLayout.VERTICAL);
        content.setPadding(dp(24), dp(8), dp(24), 0);
        content.addView(AndroidUiTheme.createSectionSubtitle(this, device.address() + "\n首次连接需要对方的访问口令，成功后自动记住。"));
        content.addView(password, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        AlertDialog dialog = new AlertDialog.Builder(this).setTitle("连接 " + device.name).setView(content)
            .setNegativeButton("取消", null).setPositiveButton("连接", null).create();
        dialog.setOnShowListener(ignored -> dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(view -> {
            String value = password.getText().toString().trim();
            if (value.isEmpty()) { password.setError("请输入访问口令"); return; }
            viewerAddressEdit.setText(device.address()); viewerPasswordEdit.setText(value);
            dialog.dismiss(); launchDirectViewer(device.host, device.port, value);
        }));
        dialog.setOnDismissListener(ignored -> password.setText(""));
        dialog.show();
    }

    private void requestProjection() {
        if (RemoteDeskForegroundService.isHostRunning()) {
            returnToDesktop();
            return;
        }
        if (hostLaunchPolicy.isPending()) return;
        String password = passwordEdit.getText().toString().trim();
        if (password.isEmpty()) {
            updateStatusPanel("请先设置连接口令");
            return;
        }

        try {
            AndroidPasswordStore.save(this, password);
        } catch (Exception ex) {
            AndroidSessionLog.error("Failed to save connection password.", ex);
            updateStatusPanel("保存连接口令失败：" + ex.getMessage());
            return;
        }

        if (AndroidHostResume.compatibleSelected(this)) {
            if (!RemoteDeskAccessibilityService.canCaptureScreen()) {
                updateStatusPanel("请启用 RemoteDesk 无障碍权限；更新后如仍不可用，请在系统设置中关闭再开启一次。");
                openAccessibilitySettings();
                return;
            }
            if (startRemoteDeskService(new Intent(this, RemoteDeskForegroundService.class)
                    .putExtra(RemoteDeskForegroundService.EXTRA_COMPATIBLE, true), "正在启动兼容被控...")) {
                hostLaunchPolicy.begin(SystemClock.elapsedRealtime());
                startHostButton.setEnabled(false);
                statusView.post(hostStartupCheck);
            }
            return;
        }

        updateStatusPanel("正在请求屏幕录制授权...");
        try {
            Intent captureIntent = Build.VERSION.SDK_INT >= 34
                ? projectionManager.createScreenCaptureIntent(MediaProjectionConfig.createConfigForDefaultDisplay())
                : projectionManager.createScreenCaptureIntent();
            startActivityForResult(
                captureIntent,
                REQUEST_MEDIA_PROJECTION);
        } catch (RuntimeException ex) {
            AndroidSessionLog.error("Failed to request screen capture permission.", ex);
            updateStatusPanel("请求屏幕录制授权失败：" + formatExceptionMessage(ex));
        }
    }

    private void configureUnlockPin() {
        if (AndroidRemoteUnlock.isLocked(this)) {
            updateStatusPanel("请先在手机上解锁，再设置或清除 PIN。");
            return;
        }
        LinearLayout content = new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);
        content.setPadding(dp(24), dp(8), dp(24), dp(8));
        content.setImportantForAutofill(View.IMPORTANT_FOR_AUTOFILL_NO_EXCLUDE_DESCENDANTS);
        TextView notice = new TextView(this);
        notice.setText((AndroidPasswordStore.hasUnlockPin(this) ? "已加密保存 PIN，不会回显。\n" : "尚未保存 PIN。\n") +
            "可选：仅保存在这台手机的 Android Keystore 加密存储中。通过 RemoteDesk 连接口令验证的人可以用它解锁手机。\n" +
            "只支持数字 PIN，失败后不自动重试；更改手机 PIN 后也请更新这里。重启后的首次解锁仍需在手机上完成。留空不修改已保存的 PIN。");
        content.addView(notice);
        EditText pin = new EditText(this);
        EditText confirm = new EditText(this);
        for (EditText field : new EditText[] {pin, confirm}) {
            field.setSaveEnabled(false);
            field.setSingleLine(true);
            field.setInputType(InputType.TYPE_CLASS_NUMBER | InputType.TYPE_NUMBER_VARIATION_PASSWORD);
            field.setTransformationMethod(android.text.method.PasswordTransformationMethod.getInstance());
            field.setFilters(new android.text.InputFilter[] {new android.text.InputFilter.LengthFilter(16)});
            content.addView(field);
        }
        pin.setHint("手机锁屏 PIN");
        confirm.setHint("再次输入 PIN");
        AlertDialog dialog = new AlertDialog.Builder(this)
            .setTitle("自动解锁（可选）").setView(content)
            .setNegativeButton("取消", null)
            .setNeutralButton("清除已保存 PIN", (ignored, which) -> {
                try {
                    RemoteDeskAccessibilityService.cancelRemoteUnlock();
                    AndroidPasswordStore.saveUnlockPin(this, "");
                    AndroidRemoteUnlock.setAttemptBlocked(this, false);
                    updateStatusPanel("已清除自动解锁 PIN");
                } catch (Exception ex) { updateStatusPanel("PIN 清除失败，请重试。"); }
            })
            .setPositiveButton("加密保存", null).create();
        dialog.setOnShowListener(ignored -> dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(view -> {
            if (AndroidRemoteUnlock.isLocked(this)) { dialog.dismiss(); return; }
            String value = pin.getText().toString();
            if (value.isEmpty() && confirm.getText().length() == 0) { dialog.dismiss(); return; }
            if (!AndroidPinUnlockPolicy.validPin(value) || !value.equals(confirm.getText().toString())) {
                pin.setError("请两次输入相同的 4 至 16 位数字 PIN");
                return;
            }
            try {
                RemoteDeskAccessibilityService.cancelRemoteUnlock();
                AndroidPasswordStore.saveUnlockPin(this, value);
                AndroidRemoteUnlock.setAttemptBlocked(this, false);
                dialog.dismiss();
                updateStatusPanel("PIN 已加密保存在本机；仅认证成功的连接可尝试解锁。");
            } catch (Exception ex) { pin.setError("加密保存失败，请重试"); }
        }));
        dialog.setOnDismissListener(ignored -> { pin.setText(""); confirm.setText(""); });
        if (dialog.getWindow() != null) dialog.getWindow().addFlags(android.view.WindowManager.LayoutParams.FLAG_SECURE);
        dialog.show();
    }

    private void startPresenceService() {
        Intent serviceIntent = new Intent(this, RemoteDeskForegroundService.class);
        serviceIntent.putExtra(RemoteDeskForegroundService.EXTRA_PRESENCE_ONLY, true);
        startRemoteDeskService(serviceIntent, "发现常驻已启动，可被局域网扫描");
    }

    private void openViewer() {
        int generation = ++viewerLaunchEpoch;
        String enteredAddress = viewerAddressEdit.getText().toString();
        String enteredPassword = viewerPasswordEdit.getText().toString();
        RemoteEndpoint endpoint = parseRemoteEndpoint(
            enteredAddress,
            RemoteDeskProtocol.HOST_PORT);
        String password = viewerPasswordEdit.getText().toString().trim();
        if (password.isEmpty()) {
            password = passwordEdit.getText().toString().trim();
        }

        if (endpoint == null || password.isEmpty()) {
            updateStatusPanel("请填写远端地址和口令");
            return;
        }

        final String connectionPassword = password;
        if (!AndroidLanDevice.explicitPort(enteredAddress)) {
            updateStatusPanel("正在自动探测端口…");
            lanPanel.resolve(endpoint.host, options -> {
                if (!activityResumed || viewerLaunchEpoch != generation ||
                        !enteredAddress.equals(viewerAddressEdit.getText().toString()) ||
                        !enteredPassword.equals(viewerPasswordEdit.getText().toString())) return;
                if (options.isEmpty()) {
                    // Unreachable discovery must not break the existing manual
                    // path (e.g. a firewall allows TCP but blocks UDP).
                    launchDirectViewer(endpoint.host, endpoint.port, connectionPassword);
                } else lanPanel.showChoices(options, device -> {
                    viewerAddressEdit.setText(AndroidLanDevice.formatAddress(endpoint.host, device.port));
                    launchDirectViewer(endpoint.host, device.port, connectionPassword);
                });
            });
            return;
        }
        launchDirectViewer(endpoint.host, endpoint.port, connectionPassword);
    }

    private void launchDirectViewer(String host, int port, String password) {
        try {
            AndroidPasswordStore.saveViewer(this, password);
        } catch (Exception ex) {
            AndroidSessionLog.error("Failed to save viewer password.", ex);
            updateStatusPanel("保存远端口令失败：" + ex.getMessage());
            return;
        }

        Intent intent = new Intent(this, RemoteDeskViewerActivity.class)
            .putExtra(RemoteDeskViewerActivity.EXTRA_HOST, host)
            .putExtra(RemoteDeskViewerActivity.EXTRA_PORT, port);
        startActivity(intent);
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode != REQUEST_MEDIA_PROJECTION) {
            return;
        }

        if (resultCode != RESULT_OK || data == null) {
            updateStatusPanel("屏幕录制授权未完成");
            return;
        }

        Intent serviceIntent = new Intent(this, RemoteDeskForegroundService.class);
        serviceIntent.putExtra(RemoteDeskForegroundService.EXTRA_RESULT_CODE, resultCode);
        serviceIntent.putExtra(RemoteDeskForegroundService.EXTRA_RESULT_DATA, data);
        if (startRemoteDeskService(serviceIntent, "正在启动被控，成功后自动返回桌面...")) {
            hostLaunchPolicy.begin(SystemClock.elapsedRealtime());
            startHostButton.setEnabled(false);
            statusView.post(hostStartupCheck);
        }
    }

    private void checkHostStartup() {
        if (isFinishing() || isDestroyed()) {
            cancelHostStartupNavigation();
            return;
        }
        AndroidHostLaunchPolicy.Action action = hostLaunchPolicy.check(
            SystemClock.elapsedRealtime(),
            RemoteDeskForegroundService.isHostRunning(),
            activityResumed && hasWindowFocus());
        switch (action) {
            case WAIT:
                statusView.postDelayed(hostStartupCheck, 150);
                break;
            case RETURN_TO_DESKTOP:
                updateStatusPanel(currentHeadline());
                returnToDesktop();
                break;
            case TIMED_OUT:
                updateStatusPanel("被控启动未完成，请检查录屏授权和服务状态后重试。");
                break;
            default:
                break;
        }
    }

    private void cancelHostStartupNavigation() {
        hostLaunchPolicy.cancel();
        if (statusView != null) statusView.removeCallbacks(hostStartupCheck);
    }

    private void returnToDesktop() {
        if (!RemoteDeskForegroundService.isHostRunning()) {
            updateStatusPanel("请先启动被控端并完成整个屏幕的录制授权。");
            return;
        }
        cancelHostStartupNavigation();
        try {
            // Leave the sensitive settings Activity; never disable Android's screen-share protections.
            startActivity(new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
            Toast.makeText(this, "被控保持运行，可从 RemoteDesk 通知返回设置。", Toast.LENGTH_LONG).show();
            AndroidSessionLog.info("Host is ready; settings task moved behind the desktop to avoid sensitive-page screen masking.");
        } catch (RuntimeException ex) {
            AndroidSessionLog.error("Could not return to desktop after host startup.", ex);
            updateStatusPanel("被控已启动，请按手机 Home 键返回桌面；口令页可能被系统录屏保护遮黑。");
        }
    }

    private void requestStopService() {
        if (RemoteDeskForegroundService.isHostRunning()) {
            new AlertDialog.Builder(this)
                .setTitle("停止被控服务？")
                .setMessage("这会断开控制端并关闭自动恢复。再次启动兼容模式无需重复录屏授权；H.264 模式需要重新授权。")
                .setNegativeButton("取消", null)
                .setPositiveButton("停止服务", (dialog, which) -> stopRemoteDeskService())
                .show();
        } else {
            stopRemoteDeskService();
        }
    }

    private void stopRemoteDeskService() {
        cancelHostStartupNavigation();
        AndroidHostResume.setArmed(this, false);
        stopService(new Intent(this, RemoteDeskForegroundService.class));
        startDiscoveryPreview();
        updateStatusPanel("已请求停止服务");
        statusView.postDelayed(() -> {
            startDiscoveryPreview();
            updateStatusPanel(currentHeadline());
        }, 600);
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] grantResults) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == 1002) {
            updateStatusPanel(currentHeadline());
        }
    }

    private synchronized void startDiscoveryPreview() {
        synchronized (DISCOVERY_PREVIEW_LOCK) {
            if (discoveryPreviewRunning || RemoteDeskForegroundService.isServiceRunning()) {
                return;
            }

            discoveryPreviewRunning = true;
            discoveryPreviewStopped = new CountDownLatch(1);
        }

        discoveryExecutor.execute(() -> {
            try {
                DatagramSocket socket = AndroidDiscoverySockets.openBoundDiscoverySocket();
                synchronized (DISCOVERY_PREVIEW_LOCK) {
                    if (!discoveryPreviewRunning) {
                        socket.close();
                        return;
                    }

                    discoverySocket = socket;
                }

                byte[] buffer = new byte[512];
                while (discoveryPreviewRunning) {
                    DatagramPacket request = new DatagramPacket(buffer, buffer.length);
                    socket.receive(request);
                    String text = new String(request.getData(), request.getOffset(), request.getLength(), StandardCharsets.UTF_8);
                    if (!RemoteDeskProtocol.DISCOVERY_REQUEST.equals(text)) {
                        continue;
                    }

                    byte[] response = createDiscoveryPreviewResponse().getBytes(StandardCharsets.UTF_8);
                    DatagramPacket packet = new DatagramPacket(
                        response,
                        response.length,
                        request.getAddress(),
                        request.getPort());
                    socket.send(packet);
                }
            } catch (Exception ignored) {
            } finally {
                synchronized (DISCOVERY_PREVIEW_LOCK) {
                    discoveryPreviewRunning = false;
                    if (discoverySocket != null) {
                        discoverySocket.close();
                        discoverySocket = null;
                    }

                    discoveryPreviewStopped.countDown();
                }
            }
        });
    }

    private boolean startRemoteDeskService(Intent serviceIntent, String successStatus) {
        stopDiscoveryPreviewAndWait();
        try {
            startForegroundService(serviceIntent);

            updateStatusPanel(successStatus);
            return true;
        } catch (RuntimeException ex) {
            AndroidSessionLog.error("Failed to start RemoteDesk service.", ex);
            startDiscoveryPreview();
            updateStatusPanel("启动 RemoteDesk 服务失败：" + formatExceptionMessage(ex));
            return false;
        }
    }

    static String formatExceptionMessage(Throwable throwable) {
        String message = throwable.getMessage();
        if (message == null || message.trim().isEmpty()) {
            return throwable.getClass().getSimpleName();
        }

        return message.trim();
    }

    static RemoteEndpoint parseRemoteEndpoint(String value, int defaultPort) {
        if (value == null) {
            return null;
        }

        String text = value.trim();
        if (text.isEmpty()) {
            return null;
        }

        String host = text;
        int port = defaultPort;
        if (text.startsWith("[")) {
            int end = text.indexOf(']');
            if (end <= 1) {
                return null;
            }

            host = text.substring(1, end);
            if (text.length() > end + 1) {
                if (text.charAt(end + 1) != ':') {
                    return null;
                }

                port = parsePort(text.substring(end + 2), defaultPort);
            }
        } else {
            int firstColon = text.indexOf(':');
            int lastColon = text.lastIndexOf(':');
            if (firstColon == lastColon && firstColon > 0) {
                host = text.substring(0, firstColon);
                port = parsePort(text.substring(firstColon + 1), defaultPort);
            }
        }

        host = host.trim();
        if (host.isEmpty() || port <= 0 || port > 65535) {
            return null;
        }

        return new RemoteEndpoint(host, port);
    }

    private static int parsePort(String value, int fallback) {
        if (value == null || value.trim().isEmpty()) {
            return fallback;
        }

        try {
            return Integer.parseInt(value.trim());
        } catch (NumberFormatException ignored) {
            return -1;
        }
    }

    static void stopDiscoveryPreviewAndWait() {
        CountDownLatch stopped;
        synchronized (DISCOVERY_PREVIEW_LOCK) {
            discoveryPreviewRunning = false;
            stopped = discoveryPreviewStopped;
            if (discoverySocket != null) {
                discoverySocket.close();
                discoverySocket = null;
            }
        }

        try {
            stopped.await(250, TimeUnit.MILLISECONDS);
        } catch (InterruptedException ex) {
            Thread.currentThread().interrupt();
        }
    }

    private String createDiscoveryPreviewResponse() throws Exception {
        JSONObject response = new JSONObject();
        response.put("Type", RemoteDeskProtocol.DISCOVERY_RESPONSE_TYPE);
        response.put("MachineName", AndroidDeviceNames.displayName());
        response.put("DeviceId", AndroidRelaySettings.localDeviceId(this));
        response.put("Port", RemoteDeskProtocol.HOST_PORT);
        response.put("CaptureTarget", "Android App 已打开，请启动被控端");
        response.put("IsHostRunning", false);
        response.put("CanRemoteStart", false);
        response.put("Platform", RemoteDeskProtocol.PLATFORM_ANDROID);
        response.put("Capabilities", 0);
        return response.toString();
    }

    private void showReceivedFiles() {
        List<ReceivedFileItem> files = listReceivedFiles();
        if (files.isEmpty()) {
            updateStatusPanel("暂无接收文件");
            return;
        }

        String[] labels = new String[files.size()];
        for (int index = 0; index < files.size(); index++) {
            labels[index] = files.get(index).label();
        }

        new AlertDialog.Builder(this)
            .setTitle("接收文件")
            .setItems(labels, (dialog, which) -> showReceivedFileActions(files.get(which)))
            .setPositiveButton("关闭", null)
            .show();
    }

    private List<ReceivedFileItem> listReceivedFiles() {
        List<ReceivedFileItem> files = new ArrayList<>();
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            addPublicReceivedFiles(files);
        }

        addAppSpecificReceivedFiles(files);
        files.sort(Comparator.comparingLong((ReceivedFileItem item) -> item.modifiedAt).reversed());
        return files;
    }

    @androidx.annotation.RequiresApi(Build.VERSION_CODES.Q)
    private void addPublicReceivedFiles(List<ReceivedFileItem> files) {
        String relativePath = Environment.DIRECTORY_DOWNLOADS + File.separator +
            AndroidFileTransferReceiver.RECEIVE_FOLDER_NAME + File.separator;
        String[] projection = {
            MediaStore.MediaColumns._ID,
            MediaStore.MediaColumns.DISPLAY_NAME,
            MediaStore.MediaColumns.SIZE,
            MediaStore.MediaColumns.DATE_MODIFIED
        };

        try (Cursor cursor = getContentResolver().query(
            MediaStore.Downloads.EXTERNAL_CONTENT_URI,
            projection,
            MediaStore.MediaColumns.RELATIVE_PATH + "=?",
            new String[] { relativePath },
            MediaStore.MediaColumns.DATE_MODIFIED + " DESC")) {
            if (cursor == null) {
                return;
            }

            int idColumn = cursor.getColumnIndexOrThrow(MediaStore.MediaColumns._ID);
            int nameColumn = cursor.getColumnIndexOrThrow(MediaStore.MediaColumns.DISPLAY_NAME);
            int sizeColumn = cursor.getColumnIndexOrThrow(MediaStore.MediaColumns.SIZE);
            int modifiedColumn = cursor.getColumnIndexOrThrow(MediaStore.MediaColumns.DATE_MODIFIED);
            while (cursor.moveToNext()) {
                long id = cursor.getLong(idColumn);
                String name = cursor.getString(nameColumn);
                long size = cursor.getLong(sizeColumn);
                long modifiedAt = cursor.getLong(modifiedColumn) * 1000L;
                Uri uri = ContentUris.withAppendedId(MediaStore.Downloads.EXTERNAL_CONTENT_URI, id);
                files.add(new ReceivedFileItem(name, size, modifiedAt, uri, "Downloads/" + AndroidFileTransferReceiver.RECEIVE_FOLDER_NAME));
            }
        } catch (Exception ignored) {
        }
    }

    private void addAppSpecificReceivedFiles(List<ReceivedFileItem> files) {
        File directory = AndroidFileTransferReceiver.getAppSpecificReceiveDirectory(this);
        File[] localFiles = directory.listFiles(file ->
            file.isFile() && !AndroidFileTransferReceiver.isOwnedTemporaryFileName(file.getName()));
        if (localFiles == null) {
            return;
        }

        for (File file : localFiles) {
            files.add(new ReceivedFileItem(
                file.getName(),
                file.length(),
                file.lastModified(),
                RemoteDeskFileProvider.uriForReceivedFile(this, file),
                file.getAbsolutePath()));
        }
    }

    private void showReceivedFileActions(ReceivedFileItem file) {
        String[] actions = { "打开", "分享", "显示位置" };
        new AlertDialog.Builder(this)
            .setTitle(file.name)
            .setItems(actions, (dialog, which) -> {
                if (which == 0) {
                    openReceivedFile(file);
                } else if (which == 1) {
                    shareReceivedFile(file);
                } else {
                    updateStatusPanel("文件位置：" + file.location);
                }
            })
            .setPositiveButton("关闭", null)
            .show();
    }

    private void openReceivedFile(ReceivedFileItem file) {
        if (file.uri == null) {
            updateStatusPanel("文件路径：" + file.location);
            return;
        }

        try {
            String type = normalizeReceivedFileMimeType(getContentResolver().getType(file.uri), "*/*");
            Intent intent = new Intent(Intent.ACTION_VIEW)
                .setDataAndType(file.uri, type);
            intent.setClipData(ClipData.newUri(getContentResolver(), file.name, file.uri));
            intent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            startActivity(Intent.createChooser(intent, "打开接收文件"));
        } catch (ActivityNotFoundException ex) {
            AndroidSessionLog.error("No app can open received file.", ex);
            updateStatusPanel("没有可打开该文件的应用");
        } catch (RuntimeException ex) {
            AndroidSessionLog.error("Failed to open received file.", ex);
            updateStatusPanel(formatReceivedFileActionFailure("打开", ex));
        }
    }

    private void shareReceivedFile(ReceivedFileItem file) {
        if (file.uri == null) {
            updateStatusPanel("该文件只能在本机路径查看：" + file.location);
            return;
        }

        try {
            String type = normalizeReceivedFileMimeType(getContentResolver().getType(file.uri), "application/octet-stream");
            Intent intent = new Intent(Intent.ACTION_SEND)
                .setType(type)
                .putExtra(Intent.EXTRA_STREAM, file.uri);
            intent.setClipData(ClipData.newUri(getContentResolver(), file.name, file.uri));
            intent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            startActivity(Intent.createChooser(intent, "分享接收文件"));
        } catch (ActivityNotFoundException ex) {
            AndroidSessionLog.error("No app can share received file.", ex);
            updateStatusPanel("没有可分享该文件的应用");
        } catch (RuntimeException ex) {
            AndroidSessionLog.error("Failed to share received file.", ex);
            updateStatusPanel(formatReceivedFileActionFailure("分享", ex));
        }
    }

    static String formatReceivedFileActionFailure(String action, Throwable throwable) {
        return action + "接收文件失败：" + formatExceptionMessage(throwable);
    }

    static String normalizeReceivedFileMimeType(String type, String fallback) {
        String normalizedFallback = fallback == null || fallback.trim().isEmpty()
            ? "application/octet-stream"
            : fallback.trim();
        if (type == null || type.trim().isEmpty()) {
            return normalizedFallback;
        }

        return type.trim();
    }

    private void updateStatusPanel(String headline) {
        AndroidSessionLog.info("UI: " + headline);
        AndroidUiTheme.applyStatusBanner(this, statusView, headline);
        readinessView.setText(localStatusText());
        boolean hostRunning = RemoteDeskForegroundService.isHostRunning();
        if (startHostButton != null) {
            startHostButton.setEnabled(!hostRunning && !hostLaunchPolicy.isPending());
            startHostButton.setText(RemoteDeskForegroundService.isCapturePaused()
                ? "重新授权，恢复被控" : "启动被控端");
        }
        if (returnToDesktopButton != null) returnToDesktopButton.setEnabled(hostRunning);
    }

    private String currentHeadline() {
        if (RemoteDeskForegroundService.isHostRunning()) {
            if (AndroidScreenCaptureSession.getInstance().isAccessibilityCapture()) {
                return "免重复授权被控正在运行\n" + AndroidScreenCaptureSession.getInstance().captureStatus();
            }
            return "被控端正在运行\n若远端看到黑屏，请返回手机桌面；口令页可能被系统录屏保护遮蔽。";
        }

        String startFailure = RemoteDeskForegroundService.getLastStartFailure();
        if (!startFailure.isEmpty()) return startFailure;

        if (AndroidHostResume.compatibleSelected(this) && !RemoteDeskAccessibilityService.canCaptureScreen()) {
            return "无障碍截图服务未连接\n如系统开关已开启，请关闭后重新开启，再启动被控。";
        }

        if (RemoteDeskForegroundService.isServiceRunning()) {
            if (RemoteDeskForegroundService.isCapturePaused()) {
                return "屏幕录制已停止\n锁屏或系统停止共享后，需要在手机上重新授权；设备发现仍在运行。";
            }
            return "发现常驻中";
        }

        return AndroidHostResume.compatibleSelected(this)
            ? "已打开，可被局域网扫描\n点击启动兼容被控，无需录屏授权"
            : "已打开，可被局域网扫描\n等待屏幕录制授权";
    }

    private String localStatusText() {
        boolean hasPassword = passwordEdit != null &&
            !passwordEdit.getText().toString().trim().isEmpty();
        boolean inputEnabled = AndroidInputInjector.isEnabled();
        boolean serviceRunning = RemoteDeskForegroundService.isServiceRunning();
        boolean hostRunning = RemoteDeskForegroundService.isHostRunning();
        boolean projectionGranted = AndroidScreenCaptureSession.getInstance().hasProjectionGrant();
        boolean compatible = AndroidHostResume.compatibleSelected(this);
        String discoveryStatus = hostRunning
            ? "正在广播完整被控能力"
            : serviceRunning
            ? "正在常驻广播 App 已打开，需在手机上启动被控端"
            : "仅广播 App 已打开，启动被控端后才能连接屏幕";
        String projectionStatus = hostRunning && AndroidScreenCaptureSession.getInstance().isAccessibilityCapture()
            ? "无障碍兼容模式，无需重复录屏授权"
            : hostRunning && projectionGranted
            ? "已授权并运行"
            : RemoteDeskForegroundService.isCapturePaused()
            ? "已停止，请重新授权" : compatible ? "使用无障碍截图，无需录屏授权" : "启动被控端时会请求";
        String capabilitiesStatus = hostRunning
            ? "屏幕观看、" + (compatible ? "JPEG 兼容截图" : "H.264/JPEG") + "、剪贴板文本、文件接收" + (inputEnabled ? "、输入控制、聚焦文本输入" : "")
            : "待启动被控端后提供屏幕观看、剪贴板和文件接收";
        String notificationStatus = notificationPermissionStatus();
        String batteryOptimizationStatus = AndroidBatteryOptimization.formatStatus(
            AndroidBatteryOptimization.supportsBatteryOptimizationBypass(Build.VERSION.SDK_INT),
            AndroidBatteryOptimization.isIgnoringBatteryOptimizations(this));
        String h264Status = AndroidVideoCodecDiagnostics.formatH264Status(
            AndroidVideoCodecDiagnostics.cachedH264Report());

        return "口令：" + (hasPassword ? "已设置" : "未设置") + "\n" +
            "屏幕录制：" + projectionStatus + "\n" +
            "通知权限：" + notificationStatus + "\n" +
            "电池优化：" + batteryOptimizationStatus + "\n" +
            "H.264编码：" + h264Status + "\n" +
            "无障碍输入：" + (inputEnabled ? "已启用，可远程点击/拖动/滚动/缩放/右键返回/中键主页/系统键/文本输入" : "未启用，仅可观看屏幕") + "\n" +
            "扫描状态：" + discoveryStatus + "\n" +
            "被控能力：" + capabilitiesStatus + "\n" +
            "连接端口：56565，发现端口：56566\n" +
            "本机地址：" + String.join(", ", localIpv4Addresses());
    }

    private void copyConnectionInfo() {
        boolean hasPassword = passwordEdit != null &&
            !passwordEdit.getText().toString().trim().isEmpty();
        boolean inputEnabled = AndroidInputInjector.isEnabled();
        boolean hostRunning = RemoteDeskForegroundService.isHostRunning();
        boolean projectionGranted = AndroidScreenCaptureSession.getInstance().hasProjectionGrant();
        String projectionStatus = AndroidHostResume.compatibleSelected(this)
            ? "无障碍兼容模式，无需录屏授权"
            : hostRunning && projectionGranted
            ? "已授权并运行"
            : "启动被控端时会请求";
        String h264Status = AndroidVideoCodecDiagnostics.formatH264Status(
            AndroidVideoCodecDiagnostics.cachedH264Report());
        String text = AndroidConnectionInfoFormatter.format(
            localIpv4Addresses(),
            currentHeadline(),
            hasPassword,
            inputEnabled,
            projectionStatus,
            h264Status);

        ClipboardManager clipboard = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
        if (clipboard == null) {
            updateStatusPanel("复制连接信息失败：系统剪贴板不可用");
            return;
        }

        try {
            clipboard.setPrimaryClip(ClipData.newPlainText("RemoteDesk Android 连接信息", text));
            AndroidSessionLog.info("Copied Android connection info to clipboard.");
            updateStatusPanel("连接信息已复制，可在 Windows 端手填 IP 或用于排障");
        } catch (RuntimeException ex) {
            AndroidSessionLog.error("Failed to copy Android connection info.", ex);
            updateStatusPanel(formatSystemActionFailure("复制连接信息", ex));
        }
    }

    private String notificationPermissionStatus() {
        if (Build.VERSION.SDK_INT < 33) {
            return "系统无需单独授权";
        }

        return checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
            ? "已允许"
            : "未允许，建议允许以确认被控端常驻状态";
    }

    private void openNotificationSettings() {
        Intent intent = new Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS)
            .putExtra(Settings.EXTRA_APP_PACKAGE, getPackageName());

        try {
            startActivity(intent);
        } catch (ActivityNotFoundException ex) {
            AndroidSessionLog.error("Android notification settings activity was not found.", ex);
            openApplicationDetailsSettings();
        } catch (RuntimeException ex) {
            AndroidSessionLog.error("Failed to open Android notification settings.", ex);
            openApplicationDetailsSettings();
        }
    }

    private void openAccessibilitySettings() {
        try {
            startActivity(new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS));
        } catch (RuntimeException ex) {
            AndroidSessionLog.error("Failed to open Android accessibility settings.", ex);
            updateStatusPanel(formatSystemActionFailure("打开无障碍设置", ex));
        }
    }

    private void openBatteryOptimizationSettings() {
        try {
            startActivity(AndroidBatteryOptimization.createSettingsIntent(this));
        } catch (RuntimeException ex) {
            AndroidSessionLog.error("Failed to open Android battery optimization settings.", ex);
            try {
                startActivity(new Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS));
            } catch (RuntimeException fallbackEx) {
                AndroidSessionLog.error("Failed to open Android fallback battery optimization settings.", fallbackEx);
                openApplicationDetailsSettings();
            }
        }
    }

    private void openApplicationDetailsSettings() {
        try {
            startActivity(new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS)
                .setData(Uri.parse("package:" + getPackageName())));
        } catch (RuntimeException ex) {
            AndroidSessionLog.error("Failed to open Android application settings.", ex);
            updateStatusPanel(formatManualSettingsInstruction(ex));
        }
    }

    private void exportDiagnosticLog() {
        File file;
        try {
            file = AndroidSessionLog.exportToFile(this);
        } catch (Exception ex) {
            AndroidSessionLog.error("Failed to export diagnostic log.", ex);
            updateStatusPanel("导出诊断日志失败：" + formatExceptionMessage(ex));
            return;
        }

        try {
            Uri uri = RemoteDeskFileProvider.uriForDiagnosticFile(this, file);
            Intent intent = new Intent(Intent.ACTION_SEND)
                .setType("text/plain")
                .putExtra(Intent.EXTRA_SUBJECT, "RemoteDesk Android 诊断日志")
                .putExtra(Intent.EXTRA_STREAM, uri);
            intent.setClipData(ClipData.newUri(getContentResolver(), file.getName(), uri));
            intent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            startActivity(Intent.createChooser(intent, "导出诊断日志"));
            updateStatusPanel("诊断日志已生成：" + file.getName());
        } catch (ActivityNotFoundException ex) {
            AndroidSessionLog.error("No app can share diagnostic log.", ex);
            updateStatusPanel("诊断日志已生成，但没有可分享的应用：" + file.getAbsolutePath());
        } catch (RuntimeException ex) {
            AndroidSessionLog.error("Failed to share diagnostic log.", ex);
            updateStatusPanel("诊断日志已生成，但分享失败：" + formatExceptionMessage(ex) + "：" + file.getAbsolutePath());
        }
    }

    static String formatSystemActionFailure(String action, Throwable throwable) {
        return action + "失败：" + formatExceptionMessage(throwable);
    }

    static String formatManualSettingsInstruction(Throwable throwable) {
        return "无法打开系统设置，请手动在系统设置中允许 RemoteDesk 相关权限：" +
            formatExceptionMessage(throwable);
    }

    private void clearDiagnosticLog() {
        try {
            AndroidSessionLog.clearPersistent(this);
            AndroidSessionLog.info("Diagnostic log cleared from Android UI.");
            updateStatusPanel("诊断日志已清空");
        } catch (Exception ex) {
            AndroidSessionLog.error("Failed to clear diagnostic log.", ex);
            updateStatusPanel("清空诊断日志失败：" + formatExceptionMessage(ex));
        }
    }

    private static List<String> localIpv4Addresses() {
        List<String> addresses = new ArrayList<>();
        try {
            for (NetworkInterface networkInterface : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                if (!networkInterface.isUp() || networkInterface.isLoopback()) {
                    continue;
                }

                for (java.net.InetAddress address : Collections.list(networkInterface.getInetAddresses())) {
                    if (address instanceof Inet4Address && !address.isLoopbackAddress()) {
                        addresses.add(address.getHostAddress());
                    }
                }
            }
        } catch (Exception ignored) {
        }

        if (addresses.isEmpty()) {
            addresses.add("未获取到局域网 IPv4");
        }

        return addresses;
    }

    private static String formatBytes(long bytes) {
        String[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.length - 1) {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? bytes + " " + units[unit] : String.format(java.util.Locale.ROOT, "%.1f %s", value, units[unit]);
    }

    private static final class ReceivedFileItem {
        final String name;
        final long size;
        final long modifiedAt;
        final Uri uri;
        final String location;

        ReceivedFileItem(String name, long size, long modifiedAt, Uri uri, String location) {
            this.name = name == null || name.trim().isEmpty() ? "remote-file" : name;
            this.size = Math.max(0, size);
            this.modifiedAt = modifiedAt;
            this.uri = uri;
            this.location = location;
        }

        String label() {
            return name + " · " + formatBytes(size) + " · " + location;
        }
    }

    static final class RemoteEndpoint {
        final String host;
        final int port;

        RemoteEndpoint(String host, int port) {
            this.host = host;
            this.port = port;
        }
    }
}
