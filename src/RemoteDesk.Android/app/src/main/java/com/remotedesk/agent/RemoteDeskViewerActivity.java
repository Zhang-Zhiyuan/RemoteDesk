package com.remotedesk.agent;

import android.app.Activity;
import android.content.Context;
import android.content.res.Configuration;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Color;
import android.graphics.Typeface;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Process;
import android.os.SystemClock;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.Surface;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowInsets;
import android.window.OnBackInvokedCallback;
import android.window.OnBackInvokedDispatcher;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;

import java.io.IOException;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.security.GeneralSecurityException;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;

public final class RemoteDeskViewerActivity extends Activity {
    static final String EXTRA_HOST = "com.remotedesk.agent.extra.HOST";
    static final String EXTRA_PORT = "com.remotedesk.agent.extra.PORT";
    static final String EXTRA_RELAY_DEVICE_ID = "com.remotedesk.agent.extra.RELAY_DEVICE_ID";
    private AndroidRelay.Options relayOptions;

    private static final int CONNECT_TIMEOUT_MILLIS = 8000;
    private static final int AUTHENTICATION_TIMEOUT_MILLIS = 10_000;
    private static final int RELIABLE_INPUT_UDP_RESUME_DELAY_MILLIS = 25;

    private final ExecutorService connectionExecutor = Executors.newSingleThreadExecutor();
    private final ExecutorService ownerTeardownExecutor =
        Executors.newSingleThreadExecutor();
    // BitmapFactory cannot be interrupted reliably. A single worker plus one
    // queued drain bounds stale work across reconnect generations without ever
    // joining a decoder from the UI thread.
    private final ExecutorService jpegDecodeExecutor = new ThreadPoolExecutor(
        1,
        1,
        0L,
        TimeUnit.MILLISECONDS,
        new ArrayBlockingQueue<>(1));
    private final AtomicBoolean running = new AtomicBoolean();
    private final AtomicLong nextConnectionGeneration = new AtomicLong();
    private final AndroidRealtimeLogSink realtimeLogSink =
        new AndroidRealtimeLogSink(AndroidSessionLog::info);
    private final LatestValueMailbox<DecodedViewerFrame> frameMailbox =
        new LatestValueMailbox<>(DecodedViewerFrame::recycle);
    private final AndroidNetworkGeneration networkGeneration =
        new AndroidNetworkGeneration();
    private final Object reconnectSignal = new Object();

    private Api33FullscreenBackHandler fullscreenBackHandler;

    private TextView statusView;
    private TextView healthView;
    private ImageView imageView;
    private SurfaceView surfaceView;
    private RemoteTouchFrameLayout viewerFrame;
    private FrameLayout rootLayout, videoLayer;
    private LinearLayout toolbar;
    private View connectionIndicator;
    private Button exitFullscreenButton;
    private AndroidViewerChrome chrome;
    private CursorOverlay cursorOverlay;
    private final AndroidViewerViewport viewport = new AndroidViewerViewport();
    private AndroidViewerGestures gestures;
    private ViewerConnectionOwner gestureOwner;
    private RemoteDeskTransport.CaptureTarget[] captureTargets = new RemoteDeskTransport.CaptureTarget[0];
    private String selectedTargetId = "";
    private final Runnable longPressGesture = () -> {
        if (gestures != null && inputReady(gestureOwner) && gestures.longPress()) {
            viewerFrame.performHapticFeedback(android.view.HapticFeedbackConstants.LONG_PRESS);
            refreshInteraction();
        }
    };
    private volatile Socket socket;
    private volatile RemoteDeskTransport.SecureSession session;
    private volatile ViewerConnectionOwner connectionOwner;
    private volatile Socket pendingConnectionSocket;
    private volatile AndroidH264SurfaceDecoder h264Decoder;
    private int frameWidth;
    private int frameHeight;
    private volatile boolean h264SurfaceActive;
    private WifiManager.WifiLock viewerWifiLock;
    private DecodedViewerFrame displayedFrame;
    private int viewerTopInset;
    private boolean viewerFullscreen;
    private volatile boolean preferJpegClarity;
    private ConnectivityManager connectivityManager;
    private ConnectivityManager.NetworkCallback networkCallback;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        AndroidDisplay.configureEdgeToEdge(this, false);
        AndroidSessionLog.configure(this);

        String host = trimExtra(EXTRA_HOST);
        int port = getIntent().getIntExtra(EXTRA_PORT, RemoteDeskProtocol.HOST_PORT);
        String password = AndroidPasswordStore.loadViewer(this).trim();

        buildViewerUi(host, port);

        String relayTarget = trimExtra(EXTRA_RELAY_DEVICE_ID);
        if (!relayTarget.isEmpty()) {
            try {
                AndroidRelay.Options saved = AndroidRelaySettings.load(this);
                if (saved == null) throw new IllegalStateException();
                relayOptions = saved.target(relayTarget);
                if (saved.deviceId.equals(relayOptions.deviceId)) {
                    updateStatus("不能通过中转连接本机。");
                    return;
                }
            } catch (Exception ex) {
                updateStatus("中转配置无效，请返回首页重新保存。");
                return;
            }
        }

        if (host.isEmpty() || password.isEmpty() || port <= 0 || port > 65535) {
            updateStatus("远端地址或口令无效");
            return;
        }

        running.set(true);
        registerDefaultNetworkCallback();
        connectionExecutor.execute(() -> runViewer(host, port, password));
    }

    @Override
    protected void onStart() {
        super.onStart();
        acquireViewerWifiLock();
    }

    private void buildViewerUi(String host, int port) {
        gestures = new AndroidViewerGestures(viewport, (kind, button, x, y, data) -> {
            ViewerConnectionOwner owner = gestureOwner;
            if (owner == null || !AndroidViewerGestures.maySend(kind,
                isCurrentConnectionOwner(owner) && canSendRemoteInput(owner), owner.displayGeometryReady)) return;
            long now = SystemClock.elapsedRealtime();
            if (kind == RemoteDeskProtocol.INPUT_MOUSE_MOVE &&
                !AndroidTouchInputPolicy.shouldSendMove(owner.lastMoveSentAt, now)) return;
            owner.lastMoveSentAt = now;
            sendInput(owner, kind, button, x, y, data);
        }, getResources().getDisplayMetrics().density);
        boolean trackpad = getPreferences(MODE_PRIVATE).getBoolean("viewer.trackpad", true);
        gestures.mode(trackpad);
        chrome = new AndroidViewerChrome(this, new AndroidViewerChrome.Actions() {
            public void keyboard(boolean open) { setKeyboardOpen(open); }
            public void mode() {
                releaseViewerGesture();
                gestures.mode(!gestures.trackpad);
                getPreferences(MODE_PRIVATE).edit().putBoolean("viewer.trackpad", gestures.trackpad).apply();
                refreshInteraction();
                toast(gestures.trackpad ? "触控板：滑动推动鼠标，轻触点击" : "直接触摸：点哪里，鼠标就到哪里并点击");
            }
            public void mouse(int button) {
                if (inputReady(connectionOwner)) { gestureOwner = connectionOwner; gestures.click(button); refreshInteraction(); }
            }
            public void drag() {
                if (inputReady(connectionOwner)) { gestureOwner = connectionOwner; gestures.toggleDrag(); refreshInteraction(); }
            }
            public void more() { showViewerMenu(); }
            public void screens() { showScreenSelector(); }
            public void diagnostics() { showDiagnostics(); }
            public void shortcut(int... keys) { sendKeyboard(null, keys); }
            public boolean text(String text) { return sendKeyboard(text); }
        });
        statusView = chrome.status; healthView = chrome.health;
        connectionIndicator = chrome.indicator; toolbar = chrome.header;
        statusView.setText(getString(R.string.connecting_to, host, port));
        healthView.setText("加密连接 · 等待远端画面");
        AndroidUiTheme.styleViewerStatusIndicator(this, connectionIndicator, statusView.getText().toString());

        viewerFrame = new RemoteTouchFrameLayout();
        viewerFrame.setBackgroundColor(AndroidUiTheme.VIEWER_BACKGROUND);
        viewerFrame.setKeepScreenOn(true);
        viewerFrame.setOnTouchListener(this::handleRemoteTouch);
        viewerFrame.setContentDescription("远程桌面，支持触控板、双指滚动与缩放");
        viewerFrame.setClipChildren(true);
        videoLayer = new FrameLayout(this);
        imageView = new ImageView(this);
        imageView.setScaleType(ImageView.ScaleType.FIT_XY);
        surfaceView = new SurfaceView(this);
        surfaceView.setVisibility(AndroidTouchInputPolicy.hiddenVideoSurfaceVisibility());
        surfaceView.setAlpha(AndroidTouchInputPolicy.hiddenVideoSurfaceAlpha());
        surfaceView.getHolder().addCallback(new SurfaceHolder.Callback() {
            public void surfaceCreated(SurfaceHolder holder) { attachDecoderSurface(holder.getSurface()); }
            public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) { attachDecoderSurface(holder.getSurface()); }
            public void surfaceDestroyed(SurfaceHolder holder) { attachDecoderSurface(null); }
        });
        videoLayer.addView(imageView, new FrameLayout.LayoutParams(-1, -1));
        videoLayer.addView(surfaceView, new FrameLayout.LayoutParams(-1, -1));
        viewerFrame.addView(videoLayer, new FrameLayout.LayoutParams(1, 1, Gravity.CENTER));
        cursorOverlay = new CursorOverlay();
        cursorOverlay.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        viewerFrame.addView(cursorOverlay, new FrameLayout.LayoutParams(-1, -1));
        rootLayout = new FrameLayout(this);
        rootLayout.setBackgroundColor(AndroidUiTheme.VIEWER_BACKGROUND);
        rootLayout.addView(viewerFrame, new FrameLayout.LayoutParams(-1, -1));
        rootLayout.addView(toolbar, new FrameLayout.LayoutParams(-1, -2, Gravity.TOP));
        rootLayout.addView(chrome.dock, new FrameLayout.LayoutParams(-1, -2, Gravity.BOTTOM));
        exitFullscreenButton = new Button(this);
        exitFullscreenButton.setText("工具栏");
        exitFullscreenButton.setContentDescription("显示远控工具栏，退出全屏");
        AndroidUiTheme.styleViewerToolbarButton(this, exitFullscreenButton);
        exitFullscreenButton.setMinimumHeight(dp(48));
        exitFullscreenButton.setVisibility(View.GONE);
        exitFullscreenButton.setOnClickListener(v -> setViewerFullscreen(false));
        FrameLayout.LayoutParams floating = new FrameLayout.LayoutParams(-2, dp(48), Gravity.TOP | Gravity.END);
        floating.setMargins(dp(8), dp(8), dp(8), 0);
        rootLayout.addView(exitFullscreenButton, floating);
        rootLayout.setOnApplyWindowInsetsListener(this::applyViewerWindowInsets);
        // Changing child visibility during layout can leave a cached portrait
        // measurement on OEM builds. Adapt chrome only in the posted inset /
        // configuration updates; layout callbacks just update content margins.
        View.OnLayoutChangeListener layoutChanged = (v, l, t, r, b, ol, ot, or, ob) -> updateViewerContentLayout();
        toolbar.addOnLayoutChangeListener(layoutChanged); chrome.dock.addOnLayoutChangeListener(layoutChanged);
        rootLayout.addOnLayoutChangeListener(layoutChanged);
        viewerFrame.addOnLayoutChangeListener((v, l, t, r, b, ol, ot, or, ob) -> updateH264SurfaceLayout());
        setContentView(rootLayout);
        chrome.controls(false); chrome.screens.setEnabled(false);
        refreshInteraction(); updateFullscreenBackHandler();
        rootLayout.requestApplyInsets();
    }

    private boolean inputReady(ViewerConnectionOwner owner) {
        return owner != null && isCurrentConnectionOwner(owner) && owner.displayGeometryReady && canSendRemoteInput(owner);
    }

    private void refreshInteraction() {
        if (videoLayer == null || gestures == null) return;
        videoLayer.setScaleX(viewport.zoom); videoLayer.setScaleY(viewport.zoom);
        videoLayer.setTranslationX(viewport.panX); videoLayer.setTranslationY(viewport.panY);
        cursorOverlay.invalidate();
        chrome.mode(gestures.trackpad, gestures.lockedDrag);
    }

    private void releaseViewerGesture() {
        if (viewerFrame != null) viewerFrame.removeCallbacks(longPressGesture);
        if (gestures != null) gestures.cancel();
        refreshInteraction();
    }

    private void setKeyboardOpen(boolean open) {
        if (chrome == null) return;
        if (open && !inputReady(connectionOwner)) { toast("等待可控制的远端画面"); return; }
        releaseViewerGesture();
        chrome.keyboard(open);
        android.view.inputmethod.InputMethodManager ime =
            (android.view.inputmethod.InputMethodManager) getSystemService(INPUT_METHOD_SERVICE);
        if (open) {
            chrome.composer.requestFocus();
            chrome.composer.post(() -> { if (chrome.keyboardOpen && ime != null) ime.showSoftInput(chrome.composer, android.view.inputmethod.InputMethodManager.SHOW_IMPLICIT); });
        } else {
            if (ime != null) ime.hideSoftInputFromWindow(chrome.composer.getWindowToken(), 0);
            chrome.composer.clearFocus();
        }
        rootLayout.requestApplyInsets();
        rootLayout.post(this::updateViewerToolbarLayout);
    }

    private boolean sendKeyboard(String text, int... keys) {
        ViewerConnectionOwner owner = connectionOwner;
        if (!inputReady(owner)) { toast("当前仅观看或连接尚未就绪"); return false; }
        boolean queued;
        synchronized (owner.inputRoutingLock) {
            long route = owner.mouseRouteGeneration.get(), generation = owner.inputCapabilityGeneration.get();
            List<AndroidViewerInputQueue.Command> batch = text == null
                ? AndroidViewerKeyboard.shortcut(route, generation, keys)
                : AndroidViewerKeyboard.text(text, route, generation);
            queued = canSendRemoteInput(owner) && owner.inputQueue.offerKeyboardBatch(batch);
        }
        if (!queued) toast("输入队列忙或没有可发送的文字，内容已保留，请稍后重试");
        return queued;
    }

    private void showViewerMenu() {
        releaseViewerGesture();
        String[] choices = { "画面缩放（" + Math.round(viewport.zoom * 100) + "%）", "画质：" +
            (preferJpegClarity ? "文字清晰 JPEG" : "流畅 H.264 自动"), "全屏显示", "屏幕方向", "手势帮助", "连接诊断", "断开连接…" };
        new android.app.AlertDialog.Builder(this).setTitle("远控工具")
            .setItems(choices, (dialog, which) -> {
                if (which == 0) showZoomOptions();
                else if (which == 1) new android.app.AlertDialog.Builder(this).setTitle("选择画质")
                    .setSingleChoiceItems(new String[] { "流畅 · H.264 硬件优先", "文字清晰 · JPEG（带宽更高）" }, preferJpegClarity ? 1 : 0,
                        (selection, value) -> { if (preferJpegClarity != (value == 1)) toggleVideoQualityMode(); selection.dismiss(); }).show();
                else if (which == 2) setViewerFullscreen(true);
                else if (which == 3) showOrientationOptions();
                else if (which == 4) new android.app.AlertDialog.Builder(this).setTitle("手机远控手势")
                    .setMessage("触控板：单指滑动推动鼠标，不会跳到手指位置；轻触左键，连续轻触双击，长按后滑动拖拽。\n\n直接触摸：点哪里，鼠标就到哪里并点击；单指滑动拖拽。底部模式按钮可切换并记住选择。\n\n两种模式均支持双指平行上下滑动，让远端网页或列表滚动。触控板模式先把鼠标移到要滚动的区域。\n\n双指轻触：右键；双指开合：只缩放本地画面，缩放中可平移。\n\n鼠标面板可锁定拖动，再点一次释放。失焦、切换模式和退出时会释放拖动。\n\n键盘：先点选远端输入框，使用手机输入法编辑后点发送；快捷键直接作用于远端。")
                    .setPositiveButton("知道了", null).show();
                else if (which == 5) showDiagnostics();
                else confirmDisconnect();
            }).show();
    }

    private void showOrientationOptions() {
        new android.app.AlertDialog.Builder(this).setTitle("手机屏幕方向")
            .setItems(new String[] { "跟随系统", "横屏", "竖屏" }, (dialog, which) -> {
                releaseViewerGesture();
                setRequestedOrientation(which == 1 ? android.content.pm.ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE :
                    which == 2 ? android.content.pm.ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT :
                    android.content.pm.ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED);
            }).show();
    }

    private void showZoomOptions() {
        new android.app.AlertDialog.Builder(this).setTitle("画面缩放（不改变传输画质）")
            .setItems(new String[] { "适应窗口 · 重置位置", "原始像素 1:1", "放大", "缩小" }, (dialog, which) -> {
                releaseViewerGesture();
                if (which == 0) viewport.reset();
                else if (which == 1) viewport.originalSize();
                else viewport.zoomAt(which == 2 ? 1.5f : 1 / 1.5f, viewport.viewWidth / 2f, viewport.viewHeight / 2f);
                refreshInteraction();
            }).show();
    }

    private void showScreenSelector() {
        ViewerConnectionOwner owner = connectionOwner;
        if (owner == null || !isCurrentConnectionOwner(owner) || captureTargets.length == 0 ||
            (owner.remoteCapabilities.get() & RemoteDeskProtocol.CAPABILITY_CAPTURE_TARGET_SELECTION) == 0) {
            toast("此设备没有提供可切换屏幕"); return;
        }
        RemoteDeskTransport.CaptureTarget[] targets = captureTargets.clone();
        String[] names = new String[targets.length]; int selected = -1;
        for (int i = 0; i < targets.length; i++) { names[i] = targets[i].displayName; if (targets[i].id.equals(selectedTargetId)) selected = i; }
        new android.app.AlertDialog.Builder(this).setTitle("切换远端屏幕")
            .setSingleChoiceItems(names, selected, (dialog, which) -> {
                releaseViewerGesture();
                if (!isCurrentConnectionOwner(owner)) { dialog.dismiss(); return; }
                try {
                    owner.controlExecutor.execute(() -> {
                        try { sendControlMessageIfCurrent(owner, RemoteDeskTransport.encodeSelectCaptureTarget(targets[which].id)); }
                        catch (IOException | GeneralSecurityException failure) { closeConnectionOwner(owner); }
                    });
                } catch (RuntimeException ignored) { toast("连接已结束，请重新连接后切换"); }
                dialog.dismiss();
            }).show();
    }

    private void showDiagnostics() {
        new android.app.AlertDialog.Builder(this).setTitle("连接诊断")
            .setMessage(statusView.getText() + "\n\n" + healthView.getText() + "\n\n手势缩放 " +
                Math.round(viewport.zoom * 100) + "% · " + (gestures.trackpad ? "触控板" : "直接触摸"))
            .setPositiveButton("关闭", null).show();
    }

    private void confirmDisconnect() {
        releaseViewerGesture();
        new android.app.AlertDialog.Builder(this).setTitle("断开远程连接？")
            .setMessage("只结束本次查看，不会关闭远端电脑。")
            .setNegativeButton("继续控制", null).setPositiveButton("断开", (dialog, which) -> finish()).show();
    }

    private void toast(String message) { android.widget.Toast.makeText(this, message, android.widget.Toast.LENGTH_SHORT).show(); }

    private final class CursorOverlay extends View {
        private final android.graphics.Paint paint = new android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG);
        private final android.graphics.Path arrow = new android.graphics.Path();
        CursorOverlay() { super(RemoteDeskViewerActivity.this); setWillNotDraw(false); }
        @Override protected void onDraw(android.graphics.Canvas canvas) {
            if (viewport.scale() <= 0 || !inputReady(connectionOwner)) return;
            float x = viewport.left() + gestures.cursorX * viewport.scale(), y = viewport.top() + gestures.cursorY * viewport.scale();
            canvas.save(); canvas.translate(x, y);
            arrow.reset(); arrow.moveTo(0, 0); arrow.lineTo(dp(3), dp(21)); arrow.lineTo(dp(8), dp(15));
            arrow.lineTo(dp(17), dp(15)); arrow.close();
            paint.setStyle(android.graphics.Paint.Style.STROKE); paint.setStrokeWidth(dp(2)); paint.setColor(0xff0b1220); canvas.drawPath(arrow, paint);
            paint.setStyle(android.graphics.Paint.Style.FILL); paint.setColor(gestures.lockedDrag || gestures.dragging ? 0xff60a5fa : Color.WHITE); canvas.drawPath(arrow, paint);
            canvas.restore();
        }
    }

    @Override
    protected void onStop() {
        releaseViewerWifiLock();
        super.onStop();
    }

    private int dp(float value) {
        return AndroidDisplay.dp(this, value);
    }

    private void setViewerFullscreen(boolean enabled) {
        releaseViewerGesture();
        if (chrome.keyboardOpen) setKeyboardOpen(false);
        viewerFullscreen = enabled;
        toolbar.setVisibility(enabled ? View.GONE : View.VISIBLE);
        chrome.dock.setVisibility(enabled ? View.GONE : View.VISIBLE);
        exitFullscreenButton.setVisibility(enabled ? View.VISIBLE : View.GONE);
        AndroidDisplay.setImmersiveMode(this, enabled);
        rootLayout.requestApplyInsets();
        rootLayout.post(this::updateViewerContentLayout);
    }

    private void toggleVideoQualityMode() {
        preferJpegClarity = !preferJpegClarity;
        boolean claritySelected = preferJpegClarity;

        ViewerConnectionOwner owner = connectionOwner;
        if (owner == null || !isCurrentConnectionOwner(owner)) {
            return;
        }

        int selectedCodecs = AndroidH264DecoderPolicy.videoCodecsForClarityMode(
            owner.automaticVideoCodecs,
            claritySelected);
        try {
            owner.controlExecutor.execute(() -> {
                try {
                    if (!sendControlMessageIfCurrent(
                            owner,
                            RemoteDeskTransport.encodeViewerInfo(selectedCodecs))) {
                        return;
                    }
                    updateStatus(
                        owner,
                        claritySelected
                            ? "清晰优先：JPEG 文字模式"
                            : "流畅优先：H.264 自动模式");
                } catch (IOException | GeneralSecurityException ex) {
                    AndroidSessionLog.error(
                        "Android viewer video quality switch failed.",
                        ex);
                    closeConnectionOwner(owner);
                }
            });
        } catch (RuntimeException ex) {
            AndroidSessionLog.error(
                "Android viewer video quality switch could not be scheduled.",
                ex);
            closeConnectionOwner(owner);
        }
    }

    private WindowInsets applyViewerWindowInsets(View view, WindowInsets windowInsets) {
        AndroidDisplay.SafeInsets insets = AndroidDisplay.safeInsets(windowInsets, true);
        viewerTopInset = insets.top;
        rootLayout.setPadding(insets.left, 0, insets.right, insets.bottom);
        toolbar.setPadding(dp(14), insets.top + dp(8), dp(14), dp(8));
        FrameLayout.LayoutParams floating = (FrameLayout.LayoutParams) exitFullscreenButton.getLayoutParams();
        floating.topMargin = insets.top + dp(8);
        exitFullscreenButton.setLayoutParams(floating);
        rootLayout.post(this::updateViewerToolbarLayout);
        return windowInsets;
    }

    private void updateViewerContentLayout() {
        if (viewerFrame == null || chrome == null) return;
        FrameLayout.LayoutParams params = (FrameLayout.LayoutParams) viewerFrame.getLayoutParams();
        int top = viewerFullscreen ? 0 : toolbar.getVisibility() == View.VISIBLE ? toolbar.getHeight() : viewerTopInset;
        int bottom = viewerFullscreen ? 0 : chrome.dock.getHeight();
        if (params.topMargin != top || params.bottomMargin != bottom) {
            params.topMargin = top; params.bottomMargin = bottom; viewerFrame.setLayoutParams(params);
        }
    }

    private void updateViewerToolbarLayout() {
        if (chrome == null) return;
        int height = getResources().getConfiguration().screenHeightDp;
        chrome.adapt(height < 420);
        toolbar.setVisibility(viewerFullscreen || height < 420 && chrome.keyboardOpen ? View.GONE : View.VISIBLE);
        updateViewerContentLayout();
    }

    @Override
    public void onConfigurationChanged(Configuration newConfig) {
        super.onConfigurationChanged(newConfig);
        releaseViewerGesture();
        AndroidDisplay.configureEdgeToEdge(this, false);
        if (viewerFullscreen) {
            AndroidDisplay.setImmersiveMode(this, true);
        }
        rootLayout.requestApplyInsets();
        rootLayout.post(() -> {
            updateViewerToolbarLayout();
            updateViewerContentLayout();
        });
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (!hasFocus) releaseViewerGesture();
        if (hasFocus && viewerFullscreen) {
            AndroidDisplay.setImmersiveMode(this, true);
        }
    }

    @Override
    @SuppressWarnings("deprecation")
    public void onBackPressed() { handleViewerBack(); }

    private void handleViewerBack() {
        if (chrome.keyboardOpen) setKeyboardOpen(false);
        else if (chrome.mouseOpen) chrome.setMouseOpen(false);
        else if (viewerFullscreen) setViewerFullscreen(false);
        else confirmDisconnect();
    }

    @Override
    protected void onPause() {
        releaseViewerGesture();
        super.onPause();
    }

    @Override
    protected void onDestroy() {
        clearFullscreenBackHandler();
        running.set(false);
        unregisterDefaultNetworkCallback();
        frameMailbox.close();
        closeQuietly(pendingConnectionSocket);
        pendingConnectionSocket = null;
        closeSocketFromUi();
        clearDisplayedFrame();
        releaseViewerWifiLock();
        jpegDecodeExecutor.shutdownNow();
        connectionExecutor.shutdownNow();
        ownerTeardownExecutor.shutdown();
        realtimeLogSink.close();
        super.onDestroy();
    }

    private void updateFullscreenBackHandler() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) {
            return;
        }

        if (chrome != null) {
            if (fullscreenBackHandler == null) {
                fullscreenBackHandler = new Api33FullscreenBackHandler(this);
                fullscreenBackHandler.register();
            }
            return;
        }

        clearFullscreenBackHandler();
    }

    private void clearFullscreenBackHandler() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            fullscreenBackHandler != null) {
            fullscreenBackHandler.unregister();
            fullscreenBackHandler = null;
        }
    }

    @androidx.annotation.RequiresApi(Build.VERSION_CODES.TIRAMISU)
    private static final class Api33FullscreenBackHandler {
        private final RemoteDeskViewerActivity activity;
        private final OnBackInvokedCallback callback;
        private boolean registered;

        Api33FullscreenBackHandler(RemoteDeskViewerActivity activity) {
            this.activity = activity;
            callback = activity::handleViewerBack;
        }

        void register() {
            if (registered) {
                return;
            }

            activity.getOnBackInvokedDispatcher().registerOnBackInvokedCallback(
                OnBackInvokedDispatcher.PRIORITY_DEFAULT,
                callback);
            registered = true;
        }

        void unregister() {
            if (!registered) {
                return;
            }

            activity.getOnBackInvokedDispatcher().unregisterOnBackInvokedCallback(
                callback);
            registered = false;
        }
    }

    private void runViewer(String host, int port, String password) {
        preferDisplayThreadPriority();
        int failedAttemptCount = 0;
        boolean firstAttempt = true;
        boolean intentQualifiedOnce = false;
        long attemptNetworkGeneration = networkGeneration.getGeneration();
        while (running.get()) {
            if (!firstAttempt) {
                long delayMillis =
                    AndroidViewerReconnectPolicy.retryDelayMillis(
                        Math.max(0, failedAttemptCount - 1));
                updateStatus("连接中断，" + formatRetryDelay(delayMillis) + "后重连");
                if (!sleepWhileRunning(
                        delayMillis,
                        attemptNetworkGeneration)) {
                    return;
                }
            }
            firstAttempt = false;

            attemptNetworkGeneration = networkGeneration.getGeneration();
            ViewerAttemptResult attempt = runViewerAttempt(host, port, password);
            ViewerConnectionOwner owner = attempt.owner;
            Throwable failure = attempt.failure;
            if (owner != null) {
                closeConnectionOwner(owner);
            }

            if (!running.get()) {
                return;
            }

            boolean qualified = owner != null && owner.deviceInfoReceived.get();
            boolean networkChangedDuringAttempt =
                networkGeneration.getGeneration() != attemptNetworkGeneration;
            boolean shouldReconnect = AndroidViewerReconnectPolicy.shouldReconnect(
                intentQualifiedOnce,
                qualified,
                networkChangedDuringAttempt);
            intentQualifiedOnce |= qualified;
            long connectedDurationNanos = attempt.connectedDurationNanos;
            failedAttemptCount = AndroidViewerReconnectPolicy.recordAttemptResult(
                failedAttemptCount,
                AndroidViewerReconnectPolicy.isStableQualifiedSession(
                    qualified,
                    connectedDurationNanos));
            if (failure != null) {
                AndroidSessionLog.error("Android viewer connection ended.", failure);
                if (failure instanceof AndroidSessionRejectedException) {
                    updateStatus(
                        "连接已结束：" +
                            AndroidViewerStatusText.connectionFailure(failure));
                    return;
                }
                if (!AndroidViewerReconnectPolicy.isRetryable(failure) || !shouldReconnect) {
                    updateStatus("连接失败：" + AndroidViewerStatusText.connectionFailure(failure));
                    return;
                }
            } else if (!shouldReconnect) {
                updateStatus("连接在远端信息确认前结束");
                return;
            }
        }
    }

    private ViewerAttemptResult runViewerAttempt(String host, int port, String password) {
        ViewerConnectionOwner owner = null;
        Socket connectedSocket = null;
        try {
            connectedSocket = relayOptions == null ? new Socket() : AndroidRelay.newSocket(relayOptions);
            pendingConnectionSocket = connectedSocket;
            if (!running.get()) throw new IOException("连接已取消。");
            configureViewerSocket(connectedSocket);
            connectedSocket.setSoTimeout(AUTHENTICATION_TIMEOUT_MILLIS);
            if (relayOptions == null) connectedSocket.connect(new InetSocketAddress(host, port), CONNECT_TIMEOUT_MILLIS);
            else AndroidRelay.connectViewer((javax.net.ssl.SSLSocket) connectedSocket, relayOptions);
            if (!running.get()) throw new IOException("连接已取消。");
            RemoteDeskTransport.SecureSession connectedSession =
                RemoteDeskTransport.authenticateClient(
                connectedSocket.getInputStream(),
                connectedSocket.getOutputStream(),
                password);
            connectedSocket.setSoTimeout(0);

            List<AndroidH264DecoderDiagnostics.DecoderCandidate> decoderCandidates =
                inspectH264DecoderCandidates();
            int automaticVideoCodecs =
                AndroidH264DecoderPolicy.advertisedVideoCodecs(decoderCandidates);
            long connectionGeneration = nextConnectionGeneration();
            AndroidH264SurfaceDecoder decoder = null;
            if (!decoderCandidates.isEmpty()) {
                decoder = new AndroidH264SurfaceDecoder(
                    decoderCandidates,
                    new ViewerH264DecoderListener(connectionGeneration));
            }
            owner = new ViewerConnectionOwner(
                connectionGeneration,
                connectedSocket,
                connectedSession,
                decoder,
                automaticVideoCodecs,
                System.nanoTime());
            publishConnectionOwner(owner);
            if (pendingConnectionSocket == connectedSocket) {
                pendingConnectionSocket = null;
            }
            attachCurrentDecoderSurface();
            ViewerConnectionOwner activeOwner = owner;
            owner.inputExecutor.execute(() -> runInputSender(activeOwner));
            startHeartbeat(owner);

            int advertisedVideoCodecs =
                AndroidH264DecoderPolicy.videoCodecsForClarityMode(
                    automaticVideoCodecs,
                    preferJpegClarity);
            int advertisedCapabilities =
                advertisedViewerCapabilities(decoderCandidates);
            if (relayOptions != null) advertisedCapabilities = relayViewerCapabilities(advertisedCapabilities);
            owner.lastAdvertisedViewerCapabilities.set(advertisedCapabilities);
            RemoteDeskTransport.writeMessage(
                connectedSocket.getOutputStream(),
                RemoteDeskProtocol.MESSAGE_CONTROL,
                RemoteDeskTransport.encodeViewerInfo(advertisedVideoCodecs),
                connectedSession,
                owner.writeLock);
            RemoteDeskTransport.writeMessage(
                connectedSocket.getOutputStream(),
                RemoteDeskProtocol.MESSAGE_CONTROL,
                RemoteDeskTransport.encodeViewerCapabilities(
                    advertisedCapabilities),
                connectedSession,
                owner.writeLock);
            updateStatus(owner, relayOptions == null ? "已连接，等待远端画面" : "公网中转已连接，等待远端画面");

            while (running.get() && isCurrentConnectionOwner(owner)) {
                RemoteDeskTransport.ProtocolMessage message = RemoteDeskTransport.readMessage(
                    connectedSocket.getInputStream(),
                    connectedSession);
                owner.lastInboundNanos.set(System.nanoTime());
                handleMessage(owner, message);
            }
            return new ViewerAttemptResult(
                owner,
                null,
                Math.max(0L, System.nanoTime() - owner.connectedAtNanos));
        } catch (Exception ex) {
            long connectedDurationNanos = owner == null
                ? 0L
                : Math.max(0L, System.nanoTime() - owner.connectedAtNanos);
            if (owner != null) {
                closeConnectionOwner(owner);
            } else {
                closeQuietly(connectedSocket);
            }
            if (pendingConnectionSocket == connectedSocket) {
                pendingConnectionSocket = null;
            }
            return new ViewerAttemptResult(owner, ex, connectedDurationNanos);
        }
    }

    private void registerDefaultNetworkCallback() {
        ConnectivityManager manager =
            (ConnectivityManager) getSystemService(Context.CONNECTIVITY_SERVICE);
        if (manager == null) {
            return;
        }

        ConnectivityManager.NetworkCallback callback =
            new ConnectivityManager.NetworkCallback() {
                @Override
                public void onAvailable(Network network) {
                    long previousGeneration = networkGeneration.getGeneration();
                    boolean liveRouteReplaced =
                        networkGeneration.onAvailable(network);
                    boolean networkChanged =
                        networkGeneration.getGeneration() != previousGeneration;
                    signalReconnectWait();
                    if (networkChanged) {
                        onDefaultNetworkInvalidated(
                            liveRouteReplaced
                                ? "默认网络已切换"
                                : "默认网络已恢复");
                    }
                }

                @Override
                public void onLost(Network network) {
                    boolean liveRouteLost = networkGeneration.onLost(network);
                    signalReconnectWait();
                    if (liveRouteLost) {
                        onDefaultNetworkInvalidated("默认网络已断开");
                    }
                }
            };
        try {
            networkGeneration.setInitialNetwork(manager.getActiveNetwork());
            manager.registerDefaultNetworkCallback(callback);
            connectivityManager = manager;
            networkCallback = callback;
        } catch (RuntimeException ex) {
            AndroidSessionLog.error(
                "Could not monitor Android default-network changes.",
                ex);
        }
    }

    private void unregisterDefaultNetworkCallback() {
        ConnectivityManager manager = connectivityManager;
        ConnectivityManager.NetworkCallback callback = networkCallback;
        connectivityManager = null;
        networkCallback = null;
        if (manager == null || callback == null) {
            return;
        }

        try {
            manager.unregisterNetworkCallback(callback);
        } catch (RuntimeException ignored) {
        }
    }

    private void onDefaultNetworkInvalidated(String reason) {
        if (!running.get()) {
            return;
        }

        AndroidSessionLog.info(
            reason + "；关闭旧 TCP/UDP owner 并立即进入现有重连策略。");
        updateStatus(reason + "，正在重连");
        ViewerConnectionOwner owner = connectionOwner;
        if (owner != null) {
            // Closing the owner socket wakes blocked authenticated reads and
            // writes. The connection loop remains the sole teardown owner.
            closeQuietly(owner.socket);
        }
        closeQuietly(pendingConnectionSocket);
    }

    private void signalReconnectWait() {
        synchronized (reconnectSignal) {
            reconnectSignal.notifyAll();
        }
    }

    private void handleMessage(
        ViewerConnectionOwner owner,
        RemoteDeskTransport.ProtocolMessage message)
        throws IOException, GeneralSecurityException {
        if (!isCurrentConnectionOwner(owner)) {
            return;
        }
        if (message.messageType == RemoteDeskProtocol.MESSAGE_PING) {
            RemoteDeskTransport.writeMessage(
                owner.socket.getOutputStream(),
                RemoteDeskProtocol.MESSAGE_PONG,
                new byte[0],
                owner.session,
                owner.writeLock);
            return;
        }

        if (message.messageType == RemoteDeskProtocol.MESSAGE_PONG) {
            return;
        }

        if (message.messageType == RemoteDeskProtocol.MESSAGE_CONTROL) {
            RemoteDeskTransport.ControlMessage control = RemoteDeskTransport.decodeControl(message.payload);
            if (control.kind == RemoteDeskProtocol.CONTROL_DEVICE_INFO) {
                boolean inputControlAvailable =
                    applyRemoteCapabilities(owner, control.capabilities);
                updateStatus(
                    owner,
                    "已连接 " + control.machineName + " (" + control.platform + ")" +
                        (inputControlAvailable ? "" : " · 仅观看"));
                updateHealthViewingMode(owner, inputControlAvailable);
                runOnUiThread(() -> {
                    if (!isCurrentConnectionOwner(owner)) return;
                    long inputGeneration = owner.inputCapabilityGeneration.get();
                    if (AndroidViewerInputCapabilityPolicy.shouldResetGesture(owner.uiInputCapabilityGeneration, inputGeneration)) {
                        releaseViewerGesture();
                        owner.uiInputCapabilityGeneration = inputGeneration;
                    }
                    // DeviceInfo can refresh names/capabilities repeatedly. It
                    // must not release a held button unless input authority changed.
                    // Read the current authority here, not a stale queued snapshot.
                    chrome.controls(canSendRemoteInput(owner));
                    chrome.screens.setEnabled(captureTargets.length > 0 &&
                        (owner.remoteCapabilities.get() & RemoteDeskProtocol.CAPABILITY_CAPTURE_TARGET_SELECTION) != 0);
                });
            } else if (control.kind == RemoteDeskProtocol.CONTROL_CAPTURE_TARGET_LIST) {
                runOnUiThread(() -> {
                    if (!isCurrentConnectionOwner(owner)) return;
                    captureTargets = control.captureTargets.clone();
                    chrome.screens.setEnabled(captureTargets.length > 0 &&
                        (owner.remoteCapabilities.get() & RemoteDeskProtocol.CAPABILITY_CAPTURE_TARGET_SELECTION) != 0);
                });
            } else if (control.kind == RemoteDeskProtocol.CONTROL_CAPTURE_TARGET_CHANGED &&
                control.captureTargets.length > 0) {
                owner.presentation.invalidate();
                owner.displayGeometryReady = false;
                updateStatus(owner, "正在切换到 " + control.captureTargets[0].displayName + "，等待画面…");
                runOnUiThread(() -> {
                    if (!isCurrentConnectionOwner(owner)) return;
                    releaseViewerGesture();
                    clearDisplayedFrame();
                    selectedTargetId = control.captureTargets[0].id;
                    viewport.reset();
                    gestures.centerCursor();
                    refreshInteraction();
                });
            } else if (control.kind == RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_OFFER &&
                control.lowLatencyVideoOffer != null &&
                owner.deviceInfoReceived.get() &&
                (owner.remoteCapabilities.get() &
                    RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_VIDEO) != 0) {
                startLowLatencyVideo(owner, control.lowLatencyVideoOffer);
            } else if (control.kind == RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_STOPPED) {
                AndroidLowLatencyVideoTransport.Viewer udp = owner.lowLatencyVideo;
                if (udp != null) {
                    udp.acknowledgeStopped(
                        control.lowLatencyVideoChannelId,
                        control.lowLatencyVideoEpoch,
                        control.lowLatencyVideoStopReason);
                    if (!udp.isUdpMouseActive()) {
                        udp.close();
                        owner.lowLatencyVideo = null;
                    }
                }
            } else if (control.kind ==
                RemoteDeskProtocol.CONTROL_SESSION_REJECTED) {
                throw new AndroidSessionRejectedException(
                    control.statusMessage == null ||
                        control.statusMessage.trim().isEmpty()
                            ? "被控端当前无法接受新的查看连接。"
                            : control.statusMessage.trim());
            } else if (control.statusMessage != null) {
                updateStatus(
                    owner,
                    AndroidViewerStatusText.forDisplay(control.statusMessage));
            }
            return;
        }

        if (message.messageType == RemoteDeskProtocol.MESSAGE_FRAME) {
            AndroidLowLatencyVideoTransport.Viewer activeUdp = owner.lowLatencyVideo;
            if (activeUdp != null && activeUdp.isVideoActive()) {
                return;
            }
            RemoteDeskTransport.FrameMessage frame = RemoteDeskTransport.decodeFrame(message.payload);
            long presentation = recordReceivedViewerFrame(owner, frame);
            offerJpegFrame(owner, frame, presentation);
            return;
        }

        if (message.messageType == RemoteDeskProtocol.MESSAGE_VIDEO_FRAME) {
            AndroidLowLatencyVideoTransport.Viewer activeUdp = owner.lowLatencyVideo;
            if (activeUdp != null && activeUdp.isVideoActive()) {
                return;
            }
            RemoteDeskTransport.FrameMessage frame =
                RemoteDeskTransport.decodeVideoFrame(message.payload);
            long presentation = recordReceivedViewerFrame(owner, frame);
            if (frame.encoding == RemoteDeskProtocol.FRAME_ENCODING_JPEG) {
                offerJpegFrame(owner, frame, presentation);
                return;
            }

            AndroidH264SurfaceDecoder decoder = owner.decoder;
            if (decoder == null) {
                requestJpegFallback(owner.generation, "Android H.264 decoder is unavailable.");
                return;
            }

            // Publish dimensions before handing the access unit to the
            // decoder worker; its output callback may run before offer()
            // returns on a fast hardware path.
            owner.pendingH264Dimensions.set(frame.width, frame.height);
            prepareH264Surface(owner, frame, presentation);
            decoder.offer(frame);
        }
    }

    private void offerJpegFrame(
        ViewerConnectionOwner owner,
        RemoteDeskTransport.FrameMessage frame,
        long presentation) {
        if (!isCurrentConnectionOwner(owner)) {
            return;
        }
        owner.jpegDecodeMailbox.offer(new JpegDecodeRequest(frame, presentation));
    }

    private static long recordReceivedViewerFrame(
        ViewerConnectionOwner owner,
        RemoteDeskTransport.FrameMessage frame) {
        long previous = owner.presentation.version();
        long presentation = owner.presentation.accept(frame.encoding);
        if (previous != presentation) {
            owner.h264FirstFramePresented.set(false);
            owner.jpegFirstFramePresented.set(false);
        }
        owner.healthTracker.recordReceivedFrame(
            frame.encodedBytes.length,
            frame.captureMillis,
            frame.encodeMillis);
        if (frame.encoding == RemoteDeskProtocol.FRAME_ENCODING_JPEG) {
            owner.healthTracker.setVideoFormat(
                AndroidViewerHealthTracker.Codec.JPEG,
                frame.width,
                frame.height);
        } else {
            owner.healthTracker.setResolution(frame.width, frame.height);
        }
        return presentation;
    }

    private void prepareH264Surface(ViewerConnectionOwner owner, RemoteDeskTransport.FrameMessage frame, long presentation) {
        if (owner.h264PreparedVersion.getAndSet(presentation) == presentation) return;
        runOnUiThread(() -> {
            if (!isCurrentConnectionOwner(owner) || !owner.presentation.current(presentation, RemoteDeskProtocol.FRAME_ENCODING_H264_ANNEX_B)) return;
            // A JPEG warm-up/fallback destroys the unused Surface on affected
            // vendors. Recreate it BEFORE waiting for a hardware output callback.
            frameWidth = frame.width; frameHeight = frame.height;
            updateH264SurfaceLayout();
            // A new screen can retain the same dimensions and Surface. Reset
            // the decoder's surface generation as well so callback-less codecs
            // repeat their one-shot PixelCopy confirmation for this presentation.
            AndroidH264SurfaceDecoder decoder = owner.decoder;
            if (decoder != null) decoder.setOutputSurface(null);
            surfaceView.setAlpha(0f);
            surfaceView.setVisibility(View.VISIBLE);
            attachCurrentDecoderSurface();
        });
    }

    private void decodeJpegFrame(
        ViewerConnectionOwner owner,
        JpegDecodeRequest request) {
        preferDisplayThreadPriority();
        if (!isCurrentConnectionOwner(owner) || !owner.presentation.current(request.presentation, RemoteDeskProtocol.FRAME_ENCODING_JPEG)) {
            return;
        }
        RemoteDeskTransport.FrameMessage frame = request.frame;

        BitmapFactory.Options bounds = new BitmapFactory.Options();
        bounds.inJustDecodeBounds = true;
        BitmapFactory.decodeByteArray(
            frame.encodedBytes,
            0,
            frame.encodedBytes.length,
            bounds);
        if (!RemoteDeskTransport.areFrameDimensionsAllowed(
                bounds.outWidth,
                bounds.outHeight) ||
            bounds.outWidth != frame.width ||
            bounds.outHeight != frame.height) {
            return;
        }

        Bitmap bitmap = BitmapFactory.decodeByteArray(
            frame.encodedBytes,
            0,
            frame.encodedBytes.length);
        if (bitmap == null ||
            bitmap.getWidth() != bounds.outWidth ||
            bitmap.getHeight() != bounds.outHeight) {
            if (bitmap != null) {
                bitmap.recycle();
            }
            return;
        }

        if (!isCurrentConnectionOwner(owner) || !owner.presentation.current(request.presentation, RemoteDeskProtocol.FRAME_ENCODING_JPEG)) {
            bitmap.recycle();
            return;
        }

        DecodedViewerFrame decodedFrame =
            new DecodedViewerFrame(owner.generation, frame.width, frame.height, bitmap);
        decodedFrame.presentation = request.presentation;
        if (frameMailbox.offer(decodedFrame)) {
            runOnUiThread(this::displayLatestFrame);
        }
    }

    private boolean applyRemoteCapabilities(
        ViewerConnectionOwner owner,
        int capabilities) {
        synchronized (owner.inputRoutingLock) {
            boolean previousInputControl = canSendRemoteInput(owner);
            owner.remoteCapabilities.set(capabilities);
            owner.deviceInfoReceived.set(true);
            boolean currentInputControl = canSendRemoteInput(owner);
            if (previousInputControl != currentInputControl) {
                owner.inputCapabilityGeneration.set(
                    AndroidViewerInputCapabilityPolicy.nextGeneration(
                        owner.inputCapabilityGeneration.get(),
                        previousInputControl,
                        currentInputControl));
                owner.mouseRouteGeneration.incrementAndGet();
                owner.inputQueue.discardAll();
                owner.pendingReliablePointerInputs.set(0);
                owner.reliablePointerGateUntilMillis.set(0L);
                owner.udpMouseRouteObserved = false;
                owner.remoteGestureActive = false;
                discardPendingUdpMouseMove(owner);
            }
            return currentInputControl;
        }
    }

    private static boolean canSendRemoteInput(ViewerConnectionOwner owner) {
        return AndroidViewerInputCapabilityPolicy.canSendInput(
            owner.deviceInfoReceived.get(),
            owner.remoteCapabilities.get());
    }

    private boolean handleRemoteTouch(View view, MotionEvent event) {
        if (viewport.scale() <= 0) return true;
        ViewerConnectionOwner owner = connectionOwner;
        if (owner != gestureOwner) {
            releaseViewerGesture();
            gestureOwner = owner;
        }
        int action = event.getActionMasked();
        if (action == MotionEvent.ACTION_DOWN) {
            gestures.down(event.getX(), event.getY(), event.getEventTime());
            viewerFrame.postDelayed(longPressGesture, android.view.ViewConfiguration.getLongPressTimeout());
        } else if (action == MotionEvent.ACTION_POINTER_DOWN) {
            viewerFrame.removeCallbacks(longPressGesture);
            if (event.getPointerCount() == 2) gestures.secondDown(centerX(event), centerY(event), span(event));
            else gestures.cancel();
        } else if (action == MotionEvent.ACTION_MOVE) {
            if (event.getPointerCount() == 2) gestures.multiMove(centerX(event), centerY(event), span(event));
            else if (event.getPointerCount() == 1) gestures.move(event.getX(), event.getY());
        } else if (action == MotionEvent.ACTION_POINTER_UP) {
            gestures.pointerUp();
        } else if (action == MotionEvent.ACTION_UP) {
            viewerFrame.removeCallbacks(longPressGesture);
            gestures.up(event.getX(), event.getY(), event.getEventTime());
            view.performClick();
        } else if (action == MotionEvent.ACTION_CANCEL) {
            releaseViewerGesture();
        }
        refreshInteraction();
        return true;
    }

    private static float centerX(MotionEvent e) { return (e.getX(0) + e.getX(1)) / 2f; }
    private static float centerY(MotionEvent e) { return (e.getY(0) + e.getY(1)) / 2f; }
    private static float span(MotionEvent e) { return (float) Math.hypot(e.getX(0) - e.getX(1), e.getY(0) - e.getY(1)); }

    private void sendInput(
        ViewerConnectionOwner owner,
        int kind,
        int button,
        int x,
        int y,
        int data) {
        if (!running.get() ||
            !isCurrentConnectionOwner(owner) ||
            !canSendRemoteInput(owner)) {
            return;
        }

        synchronized (owner.inputRoutingLock) {
            if (!canSendRemoteInput(owner)) {
                return;
            }
            long routeGeneration = owner.mouseRouteGeneration.get();
            long capabilityGeneration = owner.inputCapabilityGeneration.get();
            AndroidViewerInputQueue.Command command = new AndroidViewerInputQueue.Command(
                kind,
                button,
                x,
                y,
                data,
                routeGeneration,
                capabilityGeneration);
            if (kind == RemoteDeskProtocol.INPUT_MOUSE_MOVE &&
                owner.pendingReliablePointerInputs.get() == 0 &&
                SystemClock.elapsedRealtime() >= owner.reliablePointerGateUntilMillis.get() &&
                offerUdpMouseMove(owner, command)) {
                if (!owner.udpMouseRouteObserved) {
                    owner.udpMouseRouteObserved = true;
                    owner.mouseRouteGeneration.incrementAndGet();
                }
                owner.inputQueue.discardPendingMouseMoves();
                return;
            }

            if (owner.udpMouseRouteObserved) {
                owner.udpMouseRouteObserved = false;
                routeGeneration = owner.mouseRouteGeneration.incrementAndGet();
                command = new AndroidViewerInputQueue.Command(
                    kind,
                    button,
                    x,
                    y,
                    data,
                    routeGeneration,
                    capabilityGeneration);
            }

            if (!isReliablePointerInput(kind)) {
                owner.inputQueue.offer(command);
                return;
            }

            owner.pendingReliablePointerInputs.incrementAndGet();
            owner.mouseRouteGeneration.incrementAndGet();
            discardPendingUdpMouseMove(owner);
            if (!owner.inputQueue.offer(command)) {
                owner.pendingReliablePointerInputs.decrementAndGet();
                if (kind == RemoteDeskProtocol.INPUT_MOUSE_UP) closeConnectionOwner(owner);
            }
        }
    }

    private void runInputSender(ViewerConnectionOwner owner) {
        preferDisplayThreadPriority();
        while (running.get() && isCurrentConnectionOwner(owner)) {
            AndroidViewerInputQueue.Command command;
            try {
                command = owner.inputQueue.take();
            } catch (InterruptedException ex) {
                Thread.currentThread().interrupt();
                break;
            }

            if (command == null) {
                break;
            }

            if (!AndroidViewerInputCapabilityPolicy.isCurrentCommand(
                    canSendRemoteInput(owner),
                    command.inputCapabilityGeneration,
                    owner.inputCapabilityGeneration.get())) {
                continue;
            }
            if (command.kind == RemoteDeskProtocol.INPUT_MOUSE_MOVE &&
                command.mouseRouteGeneration != owner.mouseRouteGeneration.get()) {
                continue;
            }

            try {
                RemoteDeskTransport.writeMessage(
                    owner.socket.getOutputStream(),
                    RemoteDeskProtocol.MESSAGE_INPUT,
                    RemoteDeskTransport.encodeInput(
                        command.kind,
                        command.button,
                        command.x,
                        command.y,
                        command.data),
                    owner.session,
                    owner.writeLock);
                if (isReliablePointerInput(command.kind)) {
                    int remaining = owner.pendingReliablePointerInputs.decrementAndGet();
                    if (remaining <= 0) {
                        owner.pendingReliablePointerInputs.set(0);
                        owner.reliablePointerGateUntilMillis.set(
                            SystemClock.elapsedRealtime() +
                                RELIABLE_INPUT_UDP_RESUME_DELAY_MILLIS);
                    }
                }
            } catch (Exception ex) {
                if (isCurrentConnectionOwner(owner)) {
                    AndroidSessionLog.error("Android viewer input send failed.", ex);
                    closeConnectionOwner(owner);
                }
                return;
            }
        }
    }

    private void displayLatestFrame() {
        DecodedViewerFrame nextFrame = frameMailbox.pollLatestForDispatch();
        if (nextFrame == null) {
            return;
        }
        ViewerConnectionOwner owner = connectionOwner;
        if (owner == null || !isCurrentConnectionOwner(owner) || nextFrame.generation != owner.generation ||
            !owner.presentation.current(nextFrame.presentation, RemoteDeskProtocol.FRAME_ENCODING_JPEG)) {
            nextFrame.recycle();
            return;
        }

        surfaceView.setVisibility(AndroidTouchInputPolicy.jpegVideoSurfaceVisibility());
        h264SurfaceActive = false;
        imageView.setVisibility(View.VISIBLE);
        imageView.setImageBitmap(nextFrame.bitmap);
        DecodedViewerFrame previousFrame = displayedFrame;
        displayedFrame = nextFrame;
        frameWidth = nextFrame.width;
        frameHeight = nextFrame.height;
        owner.displayGeometryReady = true;
        updateH264SurfaceLayout();
        owner.healthTracker.recordPresentedFrame();
        if (owner.jpegFirstFramePresented.compareAndSet(false, true)) {
            updateStatus(owner, "JPEG 兼容画面已启用");
        }
        if (previousFrame != null) {
            previousFrame.recycle();
        }
    }

    private void clearDisplayedFrame() {
        if (imageView != null) {
            imageView.setImageDrawable(null);
        }
        // A target announcement invalidates input immediately. Do not leave a
        // still-visible old hardware frame labelled as the newly selected screen.
        if (surfaceView != null) surfaceView.setAlpha(0f);
        h264SurfaceActive = false;
        if (cursorOverlay != null) cursorOverlay.invalidate();

        DecodedViewerFrame previousFrame = displayedFrame;
        displayedFrame = null;
        frameWidth = 0;
        frameHeight = 0;
        viewport.geometry(0, 0, 0, 0);
        if (previousFrame != null) {
            previousFrame.recycle();
        }
    }

    private void showH264Surface(
        ViewerConnectionOwner owner,
        int width,
        int height) {
        if (!running.get() ||
            surfaceView == null ||
            !isCurrentConnectionOwner(owner) || owner.presentation.encoding() != RemoteDeskProtocol.FRAME_ENCODING_H264_ANNEX_B) {
            return;
        }

        imageView.setImageDrawable(null);
        imageView.setVisibility(View.GONE);
        DecodedViewerFrame previousFrame = displayedFrame;
        displayedFrame = null;
        if (previousFrame != null) {
            previousFrame.recycle();
        }
        frameWidth = width;
        frameHeight = height;
        owner.displayGeometryReady = true;
        updateH264SurfaceLayout();
        surfaceView.setVisibility(View.VISIBLE);
        surfaceView.setAlpha(1.0f);
        h264SurfaceActive = true;
    }

    private void queueH264SurfaceUpdate(
        ViewerConnectionOwner owner,
        int width,
        int height) {
        if (width <= 0 || height <= 0 || !isCurrentConnectionOwner(owner)) {
            return;
        }

        owner.pendingH264Dimensions.set(width, height);
        owner.h264UiVersion.incrementAndGet();
        if (owner.h264UiUpdateQueued.compareAndSet(false, true)) {
            runOnUiThread(() -> displayPendingH264Surface(owner));
        }
    }

    private void displayPendingH264Surface(ViewerConnectionOwner owner) {
        if (!isCurrentConnectionOwner(owner)) {
            owner.h264UiUpdateQueued.set(false);
            return;
        }
        long displayedVersion = owner.h264UiVersion.get();
        AndroidViewerH264Dimensions.Snapshot dimensions =
            owner.pendingH264Dimensions.snapshot();
        if (dimensions.isValid()) {
            showH264Surface(owner, dimensions.width, dimensions.height);
        }
        owner.h264UiUpdateQueued.set(false);
        if (displayedVersion != owner.h264UiVersion.get() &&
            owner.h264UiUpdateQueued.compareAndSet(false, true)) {
            runOnUiThread(() -> displayPendingH264Surface(owner));
        }
    }

    private void updateH264SurfaceLayout() {
        if (videoLayer == null || viewerFrame == null || frameWidth <= 0 || frameHeight <= 0) return;
        boolean newFrameSize = viewport.frameWidth != frameWidth || viewport.frameHeight != frameHeight;
        if (newFrameSize) releaseViewerGesture();
        viewport.geometry(viewerFrame.getWidth(), viewerFrame.getHeight(), frameWidth, frameHeight);
        if (newFrameSize) gestures.centerCursor();
        if (chrome.keyboardOpen) viewport.reveal(gestures.cursorX, gestures.cursorY);
        int width = AndroidViewerScalePolicy.scaledWidth(frameWidth, viewport.baseScale());
        int height = AndroidViewerScalePolicy.scaledHeight(frameHeight, viewport.baseScale());
        FrameLayout.LayoutParams params = (FrameLayout.LayoutParams) videoLayer.getLayoutParams();
        if (params.width != width || params.height != height) {
            params.width = width; params.height = height; videoLayer.setLayoutParams(params);
        }
        refreshInteraction();
    }

    private void attachDecoderSurface(Surface surface) {
        AndroidH264SurfaceDecoder decoder = h264Decoder;
        if (decoder != null) {
            decoder.setOutputSurface(surface);
        }
    }

    private void attachCurrentDecoderSurface() {
        if (surfaceView == null) {
            return;
        }

        Surface surface = surfaceView.getHolder().getSurface();
        attachDecoderSurface(surface != null && surface.isValid() ? surface : null);
    }

    private List<AndroidH264DecoderDiagnostics.DecoderCandidate>
        inspectH264DecoderCandidates() {
        try {
            List<AndroidH264DecoderDiagnostics.DecoderCandidate> candidates =
                AndroidH264DecoderDiagnostics.h264DecoderCandidates();
            AndroidSessionLog.info(
                "Android viewer H.264 decoder candidates: " + candidates.size() + ".");
            return candidates;
        } catch (RuntimeException ex) {
            AndroidSessionLog.error(
                "Android viewer H.264 decoder enumeration failed; advertising JPEG only.",
                ex);
            return Collections.emptyList();
        }
    }

    private void requestVideoKeyFrame(long generation, String reason) {
        ViewerConnectionOwner owner = connectionOwner;
        if (owner == null || owner.generation != generation ||
            !running.get() || !owner.keyFrameRequestQueued.compareAndSet(false, true)) {
            return;
        }

        AndroidSessionLog.info("Android viewer requests an H.264 recovery frame: " + reason + ".");
        try {
            owner.controlExecutor.execute(() -> {
                try {
                    sendControlMessage(owner, RemoteDeskTransport.encodeVideoKeyFrameRequest());
                } catch (IOException | GeneralSecurityException ex) {
                    if (running.get()) {
                        AndroidSessionLog.error(
                            "Android viewer key-frame request failed.",
                            ex);
                    }
                } finally {
                    owner.keyFrameRequestQueued.set(false);
                }
            });
        } catch (RuntimeException ex) {
            owner.keyFrameRequestQueued.set(false);
            if (running.get()) {
                AndroidSessionLog.error(
                    "Android viewer key-frame request could not be scheduled.",
                    ex);
            }
        }
    }

    private void requestJpegFallback(long generation, String reason) {
        ViewerConnectionOwner owner = connectionOwner;
        if (owner == null || owner.generation != generation ||
            !running.get() || !owner.jpegFallbackQueued.compareAndSet(false, true)) {
            return;
        }

        AndroidSessionLog.info(
            "Android viewer requests JPEG compatibility fallback: " + reason);
        try {
            owner.controlExecutor.execute(() -> {
                try {
                    if (!sendControlMessageIfCurrent(
                            owner,
                            RemoteDeskTransport.encodeViewerInfo(
                                RemoteDeskProtocol.VIDEO_CODEC_JPEG))) {
                        return;
                    }
                    updateStatus(owner, "H.264 解码不可用，已回退 JPEG");
                } catch (IOException | GeneralSecurityException ex) {
                    if (running.get()) {
                        AndroidSessionLog.error(
                            "Android viewer JPEG fallback request failed.",
                            ex);
                    }
                    closeOwnerAfterJpegFallbackFailure(owner, generation);
                }
            });
        } catch (RuntimeException ex) {
            if (running.get()) {
                AndroidSessionLog.error(
                    "Android viewer JPEG fallback could not be scheduled.",
                    ex);
            }
            closeOwnerAfterJpegFallbackFailure(owner, generation);
        }
    }

    private void closeOwnerAfterJpegFallbackFailure(
        ViewerConnectionOwner owner,
        long requestedGeneration) {
        if (shouldCloseOwnerAfterJpegFallbackFailure(
                requestedGeneration,
                owner == null ? 0L : owner.generation,
                owner != null && isCurrentConnectionOwner(owner))) {
            // This is a one-shot compatibility transition. If its reliable
            // control write fails, close only the same live generation and let
            // the existing bounded reconnect policy establish a clean session.
            closeConnectionOwner(owner);
        }
    }

    static boolean shouldCloseOwnerAfterJpegFallbackFailure(
        long requestedGeneration,
        long ownerGeneration,
        boolean ownerIsCurrent) {
        return requestedGeneration != 0L &&
            requestedGeneration == ownerGeneration &&
            ownerIsCurrent;
    }

    private void sendControlMessage(ViewerConnectionOwner owner, byte[] payload)
        throws IOException, GeneralSecurityException {
        sendControlMessageIfCurrent(owner, payload);
    }

    private boolean sendControlMessageIfCurrent(
        ViewerConnectionOwner owner,
        byte[] payload) throws IOException, GeneralSecurityException {
        if (!running.get() || !isCurrentConnectionOwner(owner)) {
            return false;
        }

        RemoteDeskTransport.writeMessage(
            owner.socket.getOutputStream(),
            RemoteDeskProtocol.MESSAGE_CONTROL,
            payload,
            owner.session,
            owner.writeLock);
        return true;
    }

    private void closeH264Decoder() {
        AndroidH264SurfaceDecoder decoder;
        synchronized (this) {
            decoder = h264Decoder;
            h264Decoder = null;
        }
        if (decoder != null) {
            decoder.close();
        }
    }

    static int[] mapViewPointToFrame(
        int viewWidth,
        int viewHeight,
        int frameWidth,
        int frameHeight,
        float viewX,
        float viewY) {
        return mapViewPointToFrame(
            viewWidth,
            viewHeight,
            frameWidth,
            frameHeight,
            viewX,
            viewY,
            true);
    }

    static int[] mapViewPointToFrame(
        int viewWidth,
        int viewHeight,
        int frameWidth,
        int frameHeight,
        float viewX,
        float viewY,
        boolean allowUpscaling) {
        if (viewWidth <= 0 || viewHeight <= 0 || frameWidth <= 0 || frameHeight <= 0) {
            return new int[] { 0, 0 };
        }

        float scale = AndroidViewerScalePolicy.fitScale(
            viewWidth,
            viewHeight,
            frameWidth,
            frameHeight,
            allowUpscaling);
        float displayedWidth = frameWidth * scale;
        float displayedHeight = frameHeight * scale;
        float offsetX = (viewWidth - displayedWidth) / 2.0f;
        float offsetY = (viewHeight - displayedHeight) / 2.0f;
        int x = Math.round((viewX - offsetX) / scale);
        int y = Math.round((viewY - offsetY) / scale);
        return new int[] {
            clamp(x, 0, frameWidth - 1),
            clamp(y, 0, frameHeight - 1)
        };
    }

    static int[] mapViewPointToFrameIfInside(
        int viewWidth,
        int viewHeight,
        int frameWidth,
        int frameHeight,
        float viewX,
        float viewY) {
        return mapViewPointToFrameIfInside(
            viewWidth,
            viewHeight,
            frameWidth,
            frameHeight,
            viewX,
            viewY,
            true);
    }

    static int[] mapViewPointToFrameIfInside(
        int viewWidth,
        int viewHeight,
        int frameWidth,
        int frameHeight,
        float viewX,
        float viewY,
        boolean allowUpscaling) {
        if (viewWidth <= 0 || viewHeight <= 0 || frameWidth <= 0 || frameHeight <= 0 ||
            Float.isNaN(viewX) || Float.isNaN(viewY)) {
            return null;
        }

        float scale = AndroidViewerScalePolicy.fitScale(
            viewWidth,
            viewHeight,
            frameWidth,
            frameHeight,
            allowUpscaling);
        float displayedWidth = frameWidth * scale;
        float displayedHeight = frameHeight * scale;
        float offsetX = (viewWidth - displayedWidth) / 2.0f;
        float offsetY = (viewHeight - displayedHeight) / 2.0f;
        if (viewX < offsetX || viewX > offsetX + displayedWidth ||
            viewY < offsetY || viewY > offsetY + displayedHeight) {
            return null;
        }
        return mapViewPointToFrame(
            viewWidth,
            viewHeight,
            frameWidth,
            frameHeight,
            viewX,
            viewY,
            allowUpscaling);
    }

    private static int clamp(int value, int min, int max) {
        return Math.max(min, Math.min(max, value));
    }

    static void configureViewerSocket(Socket viewerSocket) {
        try {
            viewerSocket.setTcpNoDelay(true);
        } catch (IOException | RuntimeException ignored) {
        }

        try {
            viewerSocket.setKeepAlive(true);
        } catch (IOException | RuntimeException ignored) {
        }

        try {
            viewerSocket.setReceiveBufferSize(AndroidVideoStreamSettings.VIEWER_RECEIVE_BUFFER_BYTES);
        } catch (IOException | RuntimeException ignored) {
        }
    }

    private void acquireViewerWifiLock() {
        if (viewerWifiLock != null && viewerWifiLock.isHeld()) {
            return;
        }

        try {
            WifiManager wifiManager =
                (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
            if (wifiManager == null) {
                return;
            }

            viewerWifiLock = wifiManager.createWifiLock(
                RemoteDeskForegroundService.getStreamingWifiLockMode(),
                "RemoteDeskViewer");
            viewerWifiLock.setReferenceCounted(false);
            viewerWifiLock.acquire();
        } catch (RuntimeException ignored) {
            viewerWifiLock = null;
        }
    }

    private void releaseViewerWifiLock() {
        WifiManager.WifiLock activeLock = viewerWifiLock;
        viewerWifiLock = null;
        if (activeLock == null) {
            return;
        }

        try {
            if (activeLock.isHeld()) {
                activeLock.release();
            }
        } catch (RuntimeException ignored) {
        }
    }

    private static void preferDisplayThreadPriority() {
        try {
            Process.setThreadPriority(Process.THREAD_PRIORITY_DISPLAY);
        } catch (RuntimeException ignored) {
        }
    }

    private void startHeartbeat(ViewerConnectionOwner owner) {
        owner.pingFuture = owner.heartbeatExecutor.scheduleWithFixedDelay(
            () -> sendHeartbeatPing(owner),
            1,
            1,
            TimeUnit.SECONDS);
        owner.watchdogFuture = owner.heartbeatExecutor.scheduleWithFixedDelay(
            () -> runHeartbeatWatchdog(owner),
            1,
            1,
            TimeUnit.SECONDS);
    }

    private void sendHeartbeatPing(ViewerConnectionOwner owner) {
        if (!running.get() || !isCurrentConnectionOwner(owner)) {
            return;
        }

        long now = System.nanoTime();
        long lastPing = owner.lastPingNanos.get();
        if (!AndroidViewerHeartbeat.shouldSendPing(now, lastPing) ||
            !owner.lastPingNanos.compareAndSet(lastPing, now)) {
            return;
        }

        try {
            RemoteDeskTransport.writeMessage(
                owner.socket.getOutputStream(),
                RemoteDeskProtocol.MESSAGE_PING,
                new byte[0],
                owner.session,
                owner.writeLock);
        } catch (Exception ex) {
            if (isCurrentConnectionOwner(owner)) {
                AndroidSessionLog.error("Android viewer heartbeat send failed.", ex);
                closeConnectionOwner(owner);
            }
        }
    }

    private void runHeartbeatWatchdog(ViewerConnectionOwner owner) {
        if (!running.get() || !isCurrentConnectionOwner(owner)) {
            return;
        }

        long now = System.nanoTime();
        AndroidH264SurfaceDecoder decoder = owner.decoder;
        if (decoder != null && decoder.rejectUnpresentedCandidateIfTimedOut(now)) {
            updateStatus(owner, "H.264 首帧超时，正在切换兼容解码器…");
        }
        if (AndroidViewerHeartbeat.hasDeviceInfoTimedOut(
                now,
                owner.connectedAtNanos,
                owner.deviceInfoReceived.get())) {
            AndroidSessionLog.info(
                "Android viewer did not receive DeviceInfo within 5 seconds; reconnecting.");
            closeConnectionOwner(owner);
            return;
        }

        if (AndroidViewerHeartbeat.hasInboundTimedOut(now, owner.lastInboundNanos.get())) {
            AndroidSessionLog.info(
                "Android viewer received no authenticated TCP message for 18 seconds; reconnecting.");
            closeConnectionOwner(owner);
            return;
        }

        updateViewerHealth(owner, now);
    }

    private void updateViewerHealth(ViewerConnectionOwner owner, long nowNanos) {
        if (!isCurrentConnectionOwner(owner)) {
            return;
        }
        AndroidLowLatencyVideoTransport.Viewer udp = owner.lowLatencyVideo;
        AndroidViewerHealthTracker.Snapshot snapshot = owner.healthTracker.snapshot(
            nowNanos,
            udp != null && udp.isVideoActive(),
            udp != null && udp.isUdpMouseActive(),
            canSendRemoteInput(owner));
        String text = AndroidViewerHealthFormatter.format(snapshot);
        if (relayOptions != null) text = text.replace("路由 TCP", "路由 公网中转 TLS/TCP");
        final String healthText = text;
        runOnUiThread(() -> {
            if (!isCurrentConnectionOwner(owner) ||
                healthText.contentEquals(healthView.getText())) {
                return;
            }
            healthView.setText(healthText);
            toolbar.post(this::updateViewerToolbarLayout);
        });
    }

    private void updateHealthViewingMode(
        ViewerConnectionOwner owner,
        boolean inputControlAvailable) {
        String text = inputControlAvailable
            ? "可控制 · 正在统计画面健康状态"
            : "仅观看 · 正在统计画面健康状态";
        runOnUiThread(() -> {
            if (!isCurrentConnectionOwner(owner)) {
                return;
            }
            healthView.setText(text);
            toolbar.post(this::updateViewerToolbarLayout);
        });
    }

    private void updateStatus(String text) {
        runOnUiThread(() -> {
            statusView.setText(text);
            AndroidUiTheme.styleViewerStatusIndicator(this, connectionIndicator, text);
            toolbar.post(this::updateViewerToolbarLayout);
        });
    }

    private void updateStatus(ViewerConnectionOwner owner, String text) {
        runOnUiThread(() -> {
            if (!isCurrentConnectionOwner(owner)) {
                return;
            }
            statusView.setText(text);
            AndroidUiTheme.styleViewerStatusIndicator(this, connectionIndicator, text);
            toolbar.post(this::updateViewerToolbarLayout);
        });
    }

    private synchronized void publishConnectionOwner(ViewerConnectionOwner owner) {
        connectionOwner = owner;
        socket = owner.socket;
        session = owner.session;
        h264Decoder = owner.decoder;
        resetViewerPresentation(owner);
    }

    private synchronized boolean isCurrentConnectionOwner(ViewerConnectionOwner owner) {
        return connectionOwner == owner &&
            socket == owner.socket &&
            !owner.closed.get();
    }

    private void closeConnectionOwner(ViewerConnectionOwner owner) {
        if (!signalConnectionOwnerClose(owner)) {
            if (getMainLooper().getThread() != Thread.currentThread()) {
                awaitOwnerTeardown(owner);
            }
            return;
        }
        if (getMainLooper().getThread() == Thread.currentThread()) {
            scheduleOwnerTeardown(owner);
        } else {
            teardownConnectionOwner(owner);
        }
    }

    private boolean signalConnectionOwnerClose(ViewerConnectionOwner owner) {
        if (!owner.closed.compareAndSet(false, true)) {
            return false;
        }
        closeQuietly(owner.socket);
        return true;
    }

    private void teardownConnectionOwner(ViewerConnectionOwner owner) {
        owner.teardownThread = Thread.currentThread();

        try {
            // Closing the owner mailbox is immediate. An already-running
            // BitmapFactory call may finish later, so all published results
            // remain fenced by the exact owner identity and generation.
            owner.jpegDecodeMailbox.close();
            owner.inputQueue.close();
            cancelFuture(owner.pingFuture);
            cancelFuture(owner.watchdogFuture);

            // Break all TCP reads and any writer blocked behind the per-owner
            // lock before waiting for the UDP workers. This also prevents a
            // dead route from delaying the next connection generation.
            synchronized (this) {
                if (connectionOwner == owner) {
                    connectionOwner = null;
                    socket = null;
                    session = null;
                    if (h264Decoder == owner.decoder) {
                        h264Decoder = null;
                    }
                }
            }
            resetViewerPresentation(owner);

            owner.inputExecutor.shutdownNow();
            owner.controlExecutor.shutdownNow();
            owner.heartbeatExecutor.shutdownNow();
            if (owner.decoder != null) {
                owner.decoder.close();
            }

            AndroidLowLatencyVideoTransport.Viewer udp = owner.lowLatencyVideo;
            owner.lowLatencyVideo = null;
            if (udp != null) {
                udp.close();
            }
        } finally {
            owner.teardownThread = null;
            owner.teardownComplete.countDown();
        }
    }

    private static void awaitOwnerTeardown(ViewerConnectionOwner owner) {
        if (owner.teardownThread == Thread.currentThread()) {
            return;
        }
        try {
            owner.teardownComplete.await(5, TimeUnit.SECONDS);
        } catch (InterruptedException ex) {
            Thread.currentThread().interrupt();
        }
    }

    private void resetViewerPresentation(ViewerConnectionOwner owner) {
        runOnUiThread(() -> {
            ViewerConnectionOwner current = connectionOwner;
            if (current == null || current == owner) {
                releaseViewerGesture();
                gestureOwner = null;
                captureTargets = new RemoteDeskTransport.CaptureTarget[0];
                selectedTargetId = "";
                chrome.controls(false);
                chrome.screens.setEnabled(false);
                owner.remoteGestureActive = false;
                owner.lastMoveSentAt = 0L;
                owner.displayGeometryReady = false;
                clearDisplayedFrame();
                if (healthView != null) {
                    healthView.setText(AndroidViewerHealthFormatter.format(null));
                }
                if (surfaceView != null) {
                    surfaceView.setVisibility(
                        AndroidTouchInputPolicy.hiddenVideoSurfaceVisibility());
                    surfaceView.setAlpha(AndroidTouchInputPolicy.hiddenVideoSurfaceAlpha());
                }
                h264SurfaceActive = false;
            }
        });
    }

    private static void cancelFuture(ScheduledFuture<?> future) {
        if (future != null) {
            future.cancel(true);
        }
    }

    private static void closeQuietly(Socket socket) {
        if (socket == null) {
            return;
        }
        try {
            socket.close();
        } catch (IOException ignored) {
        }
    }

    private long nextConnectionGeneration() {
        return nextConnectionGeneration.updateAndGet(
            current -> current == Long.MAX_VALUE ? 1L : current + 1L);
    }

    private boolean sleepWhileRunning(
        long delayMillis,
        long attemptNetworkGeneration) {
        long deadline = SystemClock.elapsedRealtime() + delayMillis;
        while (running.get()) {
            if (networkGeneration.getGeneration() != attemptNetworkGeneration) {
                return true;
            }
            long remaining = deadline - SystemClock.elapsedRealtime();
            if (remaining <= 0) {
                return true;
            }
            try {
                synchronized (reconnectSignal) {
                    if (networkGeneration.getGeneration() ==
                            attemptNetworkGeneration &&
                        running.get()) {
                        reconnectSignal.wait(Math.min(remaining, 100L));
                    }
                }
            } catch (InterruptedException ex) {
                Thread.currentThread().interrupt();
                return false;
            }
        }
        return false;
    }

    private static String formatRetryDelay(long delayMillis) {
        return delayMillis < 1_000L
            ? delayMillis + " 毫秒"
            : String.format(java.util.Locale.ROOT, "%.1f 秒", delayMillis / 1_000d);
    }

    private static boolean isReliablePointerInput(int kind) {
        return kind == RemoteDeskProtocol.INPUT_MOUSE_DOWN ||
            kind == RemoteDeskProtocol.INPUT_MOUSE_UP ||
            kind == RemoteDeskProtocol.INPUT_MOUSE_WHEEL;
    }

    private boolean offerUdpMouseMove(
        ViewerConnectionOwner owner,
        AndroidViewerInputQueue.Command command) {
        if (!AndroidViewerInputCapabilityPolicy.isCurrentCommand(
                canSendRemoteInput(owner),
                command.inputCapabilityGeneration,
                owner.inputCapabilityGeneration.get())) {
            return false;
        }
        AndroidLowLatencyVideoTransport.Viewer udp = owner.lowLatencyVideo;
        if (udp == null || !udp.isUdpMouseActive()) {
            return false;
        }
        try {
            return udp.offerMouseMove(RemoteDeskTransport.encodeInput(
                command.kind,
                command.button,
                command.x,
                command.y,
                command.data));
        } catch (IOException ex) {
            return false;
        }
    }

    private void discardPendingUdpMouseMove(ViewerConnectionOwner owner) {
        AndroidLowLatencyVideoTransport.Viewer udp = owner.lowLatencyVideo;
        if (udp != null) {
            udp.discardPendingMouseMove();
        }
    }

    private void startLowLatencyVideo(
        ViewerConnectionOwner owner,
        LowLatencyVideoProtocol.Offer offer) {
        if (!isCurrentConnectionOwner(owner) || owner.lowLatencyVideo != null) {
            return;
        }
        try {
            int localCapabilities = owner.lastAdvertisedViewerCapabilities.get();
            int negotiatedCapabilities = localCapabilities & owner.remoteCapabilities.get();
            int features = LowLatencyVideoProtocol.negotiatedFeatures(
                localCapabilities,
                owner.remoteCapabilities.get());
            if ((negotiatedCapabilities &
                    RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_VIDEO) == 0 ||
                (negotiatedCapabilities &
                    RemoteDeskProtocol.CAPABILITY_UDP_VIDEO_CONGESTION_FEEDBACK) == 0) {
                return;
            }
            InetAddress peerAddress =
                ((InetSocketAddress) owner.socket.getRemoteSocketAddress()).getAddress();
            AndroidLowLatencyVideoTransport.Viewer transport =
                new AndroidLowLatencyVideoTransport.Viewer(
                    peerAddress,
                    offer,
                    features,
                    payload -> {
                        sendControlMessage(owner, payload);
                        return isCurrentConnectionOwner(owner);
                    },
                    reason -> {
                        AndroidSessionLog.error(reason, new IOException(reason));
                        closeConnectionOwner(owner);
                    },
                    (frameKind, payload) -> handleUdpVideoFrame(
                        owner,
                        frameKind,
                        payload),
                    (sequence, elapsedNanos) -> recordMouseAckLatency(
                        owner,
                        sequence,
                        elapsedNanos));
            synchronized (this) {
                if (!isCurrentConnectionOwner(owner) || owner.lowLatencyVideo != null) {
                    transport.close();
                    return;
                }
                owner.lowLatencyVideo = transport;
            }
        } catch (Exception ex) {
            AndroidSessionLog.error(
                "Low-latency UDP viewer setup failed; continuing on TCP.",
                ex);
        }
    }

    private void handleUdpVideoFrame(
        ViewerConnectionOwner owner,
        int frameKind,
        byte[] payload) {
        if (!isCurrentConnectionOwner(owner)) {
            return;
        }
        try {
            RemoteDeskTransport.FrameMessage frame =
                frameKind == RemoteDeskProtocol.MESSAGE_FRAME
                    ? RemoteDeskTransport.decodeFrame(payload)
                    : RemoteDeskTransport.decodeVideoFrame(payload);
            long presentation = recordReceivedViewerFrame(owner, frame);
            if (frame.encoding == RemoteDeskProtocol.FRAME_ENCODING_JPEG) {
                offerJpegFrame(owner, frame, presentation);
                return;
            }
            AndroidH264SurfaceDecoder decoder = owner.decoder;
            if (decoder == null) {
                requestUdpVideoFallback(owner);
                return;
            }
            owner.pendingH264Dimensions.set(frame.width, frame.height);
            prepareH264Surface(owner, frame, presentation);
            decoder.offer(frame);
        } catch (Exception ex) {
            AndroidSessionLog.error("UDP video frame decode failed.", ex);
            requestUdpVideoFallback(owner);
        }
    }

    static int relayViewerCapabilities(int capabilities) {
        return capabilities & ~(RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_VIDEO |
            RemoteDeskProtocol.CAPABILITY_UDP_VIDEO_CONGESTION_FEEDBACK |
            RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_VIDEO_XOR_FEC |
            RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_MOUSE_INPUT |
            RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_MOUSE_INPUT_APPLIED_ACK |
            RemoteDeskProtocol.CAPABILITY_AUTHENTICATED_UDP_HEARTBEAT);
    }

    static int advertisedViewerCapabilities(
        List<AndroidH264DecoderDiagnostics.DecoderCandidate> decoderCandidates) {
        return AndroidH264DecoderPolicy.advertisedViewerCapabilities(decoderCandidates) |
            RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_VIDEO |
            RemoteDeskProtocol.CAPABILITY_UDP_VIDEO_CONGESTION_FEEDBACK |
            RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_VIDEO_XOR_FEC |
            RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_MOUSE_INPUT |
            RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_MOUSE_INPUT_APPLIED_ACK |
            RemoteDeskProtocol.CAPABILITY_AUTHENTICATED_UDP_HEARTBEAT |
            RemoteDeskProtocol.CAPABILITY_HIGH_QUALITY_JPEG;
    }

    private void requestUdpVideoFallback(ViewerConnectionOwner owner) {
        AndroidLowLatencyVideoTransport.Viewer udp = owner.lowLatencyVideo;
        if (udp != null && udp.isVideoActive()) {
            udp.requestVideoFallback(
                RemoteDeskProtocol.LOW_LATENCY_FALLBACK_PRESERVE_UDP_INPUT);
        }
    }

    private void recordMouseAckLatency(
        ViewerConnectionOwner owner,
        long sequence,
        long elapsedNanos) {
        if (!isCurrentConnectionOwner(owner)) {
            return;
        }
        AndroidMouseAckLatencyTracker.Snapshot snapshot =
            owner.mouseAckLatencyTracker.record(sequence, elapsedNanos);
        owner.healthTracker.recordMouseAck(
            snapshot.smoothedLatencyMicros,
            snapshot.sampleCount);
        if (snapshot.shouldLog) {
            realtimeLogSink.offer(
                "UDP mouse applied-ACK latency: latest=" +
                    snapshot.latestLatencyMicros + " us, smoothed=" +
                    snapshot.smoothedLatencyMicros + " us, samples=" +
                    snapshot.sampleCount + ".");
        }
    }

    private String trimExtra(String name) {
        String value = getIntent().getStringExtra(name);
        return value == null ? "" : value.trim();
    }

    private void closeSocket() {
        ViewerConnectionOwner owner = connectionOwner;
        if (owner != null) {
            closeConnectionOwner(owner);
            return;
        }
        Socket currentSocket = socket;
        socket = null;
        session = null;
        closeQuietly(currentSocket);
    }

    private void closeSocketFromUi() {
        ViewerConnectionOwner owner = connectionOwner;
        if (owner == null) {
            closeSocket();
            return;
        }
        AndroidViewerClosePolicy.closeFromUi(
            new AndroidViewerClosePolicy.Owner() {
                @Override
                public boolean signalClose() {
                    return signalConnectionOwnerClose(owner);
                }

                @Override
                public void teardown() {
                    teardownConnectionOwner(owner);
                }
            },
            work -> scheduleOwnerTeardown(owner));
    }

    private boolean scheduleOwnerTeardown(ViewerConnectionOwner owner) {
        Runnable work = () -> teardownConnectionOwner(owner);
        try {
            ownerTeardownExecutor.execute(work);
            return true;
        } catch (RuntimeException ex) {
            Thread fallback = new Thread(
                work,
                "RemoteDesk-Viewer-Owner-Teardown");
            fallback.start();
            return true;
        }
    }

    private final class ViewerConnectionOwner {
        final long generation;
        final Socket socket;
        final RemoteDeskTransport.SecureSession session;
        final AndroidH264SurfaceDecoder decoder;
        final int automaticVideoCodecs;
        final long connectedAtNanos;
        final AndroidViewerInputQueue inputQueue =
            new AndroidViewerInputQueue(AndroidVideoStreamSettings.VIEWER_INPUT_QUEUE_LIMIT);
        final Object writeLock = new Object();
        final Object inputRoutingLock = new Object();
        final ExecutorService inputExecutor = Executors.newSingleThreadExecutor();
        final ExecutorService controlExecutor = Executors.newSingleThreadExecutor();
        final ScheduledExecutorService heartbeatExecutor =
            Executors.newScheduledThreadPool(2);
        final AtomicBoolean closed = new AtomicBoolean();
        final AtomicBoolean deviceInfoReceived = new AtomicBoolean();
        final AtomicBoolean keyFrameRequestQueued = new AtomicBoolean();
        final AtomicBoolean jpegFallbackQueued = new AtomicBoolean();
        final AtomicBoolean h264UiUpdateQueued = new AtomicBoolean();
        final AtomicLong h264UiVersion = new AtomicLong();
        final AndroidViewerH264Dimensions pendingH264Dimensions =
            new AndroidViewerH264Dimensions();
        final AtomicLong lastInboundNanos;
        final AtomicLong lastPingNanos;
        final AtomicLong mouseRouteGeneration = new AtomicLong();
        final AtomicLong inputCapabilityGeneration = new AtomicLong();
        final AtomicInteger pendingReliablePointerInputs = new AtomicInteger();
        final AtomicLong reliablePointerGateUntilMillis = new AtomicLong();
        final AtomicInteger lastAdvertisedViewerCapabilities = new AtomicInteger();
        final AtomicInteger remoteCapabilities = new AtomicInteger();
        final AtomicBoolean h264FirstFramePresented = new AtomicBoolean();
        final AtomicBoolean jpegFirstFramePresented = new AtomicBoolean();
        final AndroidMouseAckLatencyTracker mouseAckLatencyTracker =
            new AndroidMouseAckLatencyTracker();
        final AndroidViewerHealthTracker healthTracker =
            new AndroidViewerHealthTracker();
        final AndroidViewerPresentationEpoch presentation = new AndroidViewerPresentationEpoch();
        final AtomicLong h264PreparedVersion = new AtomicLong(-1);
        final LatestWorkerMailbox<JpegDecodeRequest>
            jpegDecodeMailbox = new LatestWorkerMailbox<>(
                jpegDecodeExecutor,
                frame -> decodeJpegFrame(this, frame),
                ignored -> {
                },
                failure -> AndroidSessionLog.error(
                    "Android JPEG decode worker failed; dropping the frame.",
                    failure));
        final CountDownLatch teardownComplete = new CountDownLatch(1);
        volatile ScheduledFuture<?> pingFuture;
        volatile ScheduledFuture<?> watchdogFuture;
        volatile AndroidLowLatencyVideoTransport.Viewer lowLatencyVideo;
        volatile AndroidH264DecoderDiagnostics.DecoderCandidate activeH264Candidate;
        volatile Thread teardownThread;
        boolean udpMouseRouteObserved;
        volatile boolean remoteGestureActive;
        volatile boolean displayGeometryReady;
        long lastMoveSentAt;
        long uiInputCapabilityGeneration = -1L; // UI thread only, per connection

        ViewerConnectionOwner(
            long generation,
            Socket socket,
            RemoteDeskTransport.SecureSession session,
            AndroidH264SurfaceDecoder decoder,
            int automaticVideoCodecs,
            long connectedAtNanos) {
            this.generation = generation;
            this.socket = socket;
            this.session = session;
            this.decoder = decoder;
            this.automaticVideoCodecs = automaticVideoCodecs;
            this.connectedAtNanos = connectedAtNanos;
            healthTracker.start(connectedAtNanos);
            lastInboundNanos = new AtomicLong(connectedAtNanos);
            lastPingNanos = new AtomicLong(connectedAtNanos);
        }
    }

    private static final class ViewerAttemptResult {
        final ViewerConnectionOwner owner;
        final Throwable failure;
        final long connectedDurationNanos;

        ViewerAttemptResult(
            ViewerConnectionOwner owner,
            Throwable failure,
            long connectedDurationNanos) {
            this.owner = owner;
            this.failure = failure;
            this.connectedDurationNanos = connectedDurationNanos;
        }
    }

    private static final class JpegDecodeRequest {
        final RemoteDeskTransport.FrameMessage frame;
        final long presentation;
        JpegDecodeRequest(RemoteDeskTransport.FrameMessage frame, long presentation) {
            this.frame = frame; this.presentation = presentation;
        }
    }

    private static final class DecodedViewerFrame {
        long presentation;
        final long generation;
        final int width;
        final int height;
        final Bitmap bitmap;

        DecodedViewerFrame(long generation, int width, int height, Bitmap bitmap) {
            this.generation = generation;
            this.width = width;
            this.height = height;
            this.bitmap = bitmap;
        }

        void recycle() {
            if (!bitmap.isRecycled()) {
                bitmap.recycle();
            }
        }
    }

    private final class ViewerH264DecoderListener
        implements AndroidH264SurfaceDecoder.Listener {
        private final long generation;

        ViewerH264DecoderListener(long generation) {
            this.generation = generation;
        }

        @Override
        public void onRecoveryFrameNeeded(String reason) {
            requestVideoKeyFrame(generation, reason);
        }

        @Override
        public void onDecoderStarted(
            AndroidH264DecoderDiagnostics.DecoderCandidate candidate) {
            ViewerConnectionOwner owner = connectionOwner;
            if (owner == null || owner.generation != generation ||
                !isCurrentConnectionOwner(owner) || owner.presentation.encoding() != RemoteDeskProtocol.FRAME_ENCODING_H264_ANNEX_B) {
                return;
            }
            AndroidViewerH264Dimensions.Snapshot dimensions =
                owner.pendingH264Dimensions.snapshot();
            owner.activeH264Candidate = candidate;
            owner.h264FirstFramePresented.set(false);
            owner.healthTracker.markPresentationTelemetryUnavailable();
            owner.healthTracker.setVideoFormat(
                candidate.hardwareAccelerated && !candidate.softwareOnly
                    ? AndroidViewerHealthTracker.Codec.H264_HARDWARE
                    : AndroidViewerHealthTracker.Codec.H264_COMPATIBILITY,
                dimensions.width,
                dimensions.height);
            if (!candidate.hardwareAccelerated || candidate.softwareOnly) {
                int previous = owner.lastAdvertisedViewerCapabilities.get();
                int downgraded = previous &
                    ~RemoteDeskProtocol.CAPABILITY_HIGH_FRAME_RATE_H264;
                if (downgraded != previous &&
                    owner.lastAdvertisedViewerCapabilities.compareAndSet(
                        previous,
                        downgraded)) {
                    try {
                        owner.controlExecutor.execute(() -> {
                            try {
                                sendControlMessage(
                                    owner,
                                    RemoteDeskTransport.encodeViewerCapabilities(downgraded));
                                AndroidSessionLog.info(
                                    "Android viewer selected a compatibility H.264 decoder; " +
                                    "withdrawing the 60 FPS capability.");
                            } catch (IOException | GeneralSecurityException ex) {
                                if (isCurrentConnectionOwner(owner)) {
                                    AndroidSessionLog.error(
                                        "Android viewer capability downgrade failed.",
                                        ex);
                                    closeConnectionOwner(owner);
                                }
                            }
                        });
                    } catch (RuntimeException ignored) {
                        // Owner teardown raced the decoder callback.
                    }
                }
            }
            updateStatus(
                owner,
                candidate.hardwareAccelerated && !candidate.softwareOnly
                    ? "H.264 硬件解码器已启动，正在验证首帧…"
                    : "H.264 兼容解码器已启动，正在验证首帧…");
        }

        @Override
        public void onFrameRendered() {
            confirmFirstFrame(true);
        }

        @Override
        public void onSurfaceBufferAvailable() {
            confirmFirstFrame(false);
        }

        private void confirmFirstFrame(boolean measuredPresentation) {
            ViewerConnectionOwner owner = connectionOwner;
            if (owner == null || owner.generation != generation ||
                !isCurrentConnectionOwner(owner) || owner.presentation.encoding() != RemoteDeskProtocol.FRAME_ENCODING_H264_ANNEX_B) {
                return;
            }
            if (measuredPresentation) {
                owner.healthTracker.recordPresentedFrame();
            }
            AndroidViewerH264Dimensions.Snapshot dimensions =
                owner.pendingH264Dimensions.snapshot();
            if (!dimensions.isValid() ||
                !owner.h264FirstFramePresented.compareAndSet(false, true)) {
                return;
            }

            queueH264SurfaceUpdate(owner, dimensions.width, dimensions.height);
            AndroidH264DecoderDiagnostics.DecoderCandidate candidate =
                owner.activeH264Candidate;
            updateStatus(
                owner,
                candidate != null &&
                    candidate.hardwareAccelerated &&
                    !candidate.softwareOnly
                    ? "H.264 Surface 硬件解码已启用"
                    : "H.264 Surface 兼容解码已启用");
        }

        @Override
        public void onDecoderUnavailable(String reason) {
            requestJpegFallback(generation, reason);
        }
    }

    private final class RemoteTouchFrameLayout extends FrameLayout {
        RemoteTouchFrameLayout() {
            super(RemoteDeskViewerActivity.this);
        }

        @Override
        public boolean performClick() {
            super.performClick();
            return true;
        }
    }
}
