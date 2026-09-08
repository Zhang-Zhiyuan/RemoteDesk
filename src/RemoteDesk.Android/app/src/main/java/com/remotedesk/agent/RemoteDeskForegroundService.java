package com.remotedesk.agent;

import android.annotation.SuppressLint;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.IBinder;
import android.os.Handler;
import android.os.Looper;
import android.os.PowerManager;

import org.json.JSONObject;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class RemoteDeskForegroundService extends Service {
    static final String EXTRA_RESULT_CODE = "resultCode";
    static final String EXTRA_RESULT_DATA = "resultData";
    static final String EXTRA_PRESENCE_ONLY = "presenceOnly";
    static final String EXTRA_REFRESH_RELAY = "refreshRelay";
    static final String EXTRA_REFRESH_POWER = "refreshPower";
    static final String EXTRA_STOP = "stop";
    static final String EXTRA_COMPATIBLE = "compatibleCapture";
    static final String EXTRA_AUTO_RESUME = "automaticResume";
    static final String PREFS_NAME = "remotedesk-agent";
    static final String PREF_PASSWORD = "password";
    static final String PREF_KEEP_SCREEN_AWAKE = "keepScreenAwake";

    private static final String CHANNEL_ID = "remotedesk-agent";
    private static final int NOTIFICATION_ID = 56565;
    private static volatile boolean serviceRunning;
    private static volatile boolean hostRunning;
    private static volatile String lastStartFailure = "";
    private static final AndroidProjectionState projectionState = new AndroidProjectionState();

    private final ExecutorService discoveryExecutor =
        Executors.newSingleThreadExecutor();
    private final AndroidDiscoveryRetryPolicy discoveryRetryPolicy =
        new AndroidDiscoveryRetryPolicy();
    private DatagramSocket discoverySocket;
    private RemoteDeskHostServer hostServer;
    private AndroidRelay.HostConnector relayHost;
    private volatile long relayGeneration;
    private static volatile String relayStatus = "中转未上线";
    private WifiManager.MulticastLock multicastLock;
    private WifiManager.WifiLock streamingWifiLock;
    private PowerManager.WakeLock streamingWakeLock;
    private final AndroidScreenAwakeController screenAwakeController =
        new AndroidScreenAwakeController(this::createScreenWakeLock);
    private volatile boolean running;
    private boolean discoveryResponderStarted;

    @Override
    public void onCreate() {
        super.onCreate();
        AndroidSessionLog.configure(this);
        projectionState.reset();
        createNotificationChannel();
        hostServer = new RemoteDeskHostServer(this, AndroidScreenCaptureSession.getInstance());
        Handler mainHandler = new Handler(Looper.getMainLooper());
        hostServer.setSessionChangedListener(() -> mainHandler.post(() -> {
            if (running) updateStreamingPower();
        }));
        AndroidSessionLog.info("Foreground service created.");
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        return runStartSafely(() -> handleStartCommand(intent, startId), ex -> {
            lastStartFailure = "被控启动被系统拒绝或暂不可用，请检查权限后重试；详情见诊断日志。";
            AndroidSessionLog.error("Android host start failed without crashing the accessibility process.", ex);
            hostRunning = false;
            stopSelf(startId);
        });
    }

    static int runStartSafely(java.util.function.IntSupplier start,
        java.util.function.Consumer<RuntimeException> onFailure) {
        try { return start.getAsInt(); }
        catch (RuntimeException ex) {
            onFailure.accept(ex);
            return START_NOT_STICKY;
        }
    }

    private int handleStartCommand(Intent intent, int startId) {
        if (intent != null && intent.getBooleanExtra(EXTRA_STOP, false)) {
            AndroidHostResume.setArmed(this, false);
            lastStartFailure = "";
            stopSelf();
            return START_NOT_STICKY;
        }
        // Recheck persisted user intent when the queued start is delivered. A resume
        // posted before the user pressed Stop must not silently re-arm the host.
        boolean automatic = intent == null || intent.getBooleanExtra(EXTRA_AUTO_RESUME, false);
        if (shouldRejectAutomaticStart(automatic, AndroidHostResume.shouldResume(this))) {
            if (!running) stopSelf(startId);
            return activeRestartMode();
        }
        if (intent != null && intent.getBooleanExtra(EXTRA_REFRESH_POWER, false)) {
            updateStreamingPower();
            if (!running) stopSelf(startId);
            return activeRestartMode();
        }
        if (intent != null && intent.getBooleanExtra(EXTRA_REFRESH_RELAY, false)) {
            refreshRelayRegistration();
            if (!running) stopSelf(startId);
            return activeRestartMode();
        }
        boolean presenceOnly = intent != null && intent.getBooleanExtra(EXTRA_PRESENCE_ONLY, false);
        boolean compatible = intent == null ? AndroidHostResume.shouldResume(this)
            : intent.getBooleanExtra(EXTRA_COMPATIBLE, false);
        int resultCode = intent != null ? intent.getIntExtra(EXTRA_RESULT_CODE, 0) : 0;
        Intent resultData = getProjectionData(intent);
        String password = AndroidPasswordStore.load(this);

        if (resultCode != 0 && resultData != null) {
            AndroidScreenCaptureSession.getInstance().setProjectionGrant(resultCode, resultData);
        }

        if (presenceOnly) {
            if (hostServer != null && hostServer.isRunning()) {
                serviceRunning = true;
                hostRunning = true;
                updateNotification();
                AndroidSessionLog.info("Presence start ignored because host is already running.");
                return activeRestartMode();
            }

            startForegroundCompat(false);
            running = true;
            serviceRunning = true;
            hostRunning = false;
            acquireMulticastLock();
            MainActivity.stopDiscoveryPreviewAndWait();
            startDiscoveryResponder();
            updateNotification();
            AndroidSessionLog.info("Presence responder started.");
            return START_NOT_STICKY;
        }

        if ((compatible ? !RemoteDeskAccessibilityService.canCaptureScreen()
                : !AndroidScreenCaptureSession.getInstance().hasProjectionGrant()) ||
            password == null ||
            password.trim().isEmpty()) {
            AndroidSessionLog.info("Host start rejected: screen capture grant or password is missing.");
            lastStartFailure = "被控未启动：缺少可用的截图权限或连接口令。";
            stopSelf(startId);
            return START_NOT_STICKY;
        }

        startForegroundCompat(!compatible);
        running = true;
        serviceRunning = true;
        acquireMulticastLock();
        MainActivity.stopDiscoveryPreviewAndWait();
        startDiscoveryResponder();

        AndroidScreenCaptureSession captureSession =
            AndroidScreenCaptureSession.getInstance();
        boolean captureStarted = compatible ? captureSession.startAccessibility(this)
            : captureSession.start(this, this::onProjectionStopped);
        if (!captureStarted) {
            AndroidSessionLog.info("Host start failed: screen capture session could not start.");
            lastStartFailure = "截图服务未能启动，请检查权限后重试。";
            stopSelf(startId);
            return START_NOT_STICKY;
        }
        if (compatible) projectionState.reset();
        else projectionState.started(captureSession.getProjectionGeneration());

        try {
            boolean alreadyListening = hostServer.isRunning();
            hostServer.start(password);
            hostRunning = true;
            lastStartFailure = "";
            AndroidHostResume.setArmed(this, compatible);
            updateStreamingPower();
            // Duplicate Activity/accessibility resume requests must not tear down an
            // established relay session. Configuration changes have their own action.
            if (!alreadyListening) refreshRelayRegistration();
            updateNotification();
            AndroidSessionLog.info("Host server started on port " + RemoteDeskProtocol.HOST_PORT + ".");
        } catch (Exception ignored) {
            AndroidSessionLog.error("Host server failed to start.", ignored);
            lastStartFailure = "被控监听未能启动，请检查端口占用或查看诊断日志。";
            stopSelf(startId);
            return START_NOT_STICKY;
        }

        return compatible ? START_STICKY : START_NOT_STICKY;
    }

    @Override
    public void onDestroy() {
        AndroidSessionLog.info("Foreground service destroying.");
        running = false;
        projectionState.reset();
        serviceRunning = false;
        hostRunning = false;
        stopRelayRegistration();
        closeDiscoverySocket();
        discoveryExecutor.shutdownNow();

        releaseMulticastLock();
        releaseStreamingLocks();
        if (hostServer != null) {
            hostServer.shutdown();
        }

        AndroidScreenCaptureSession.getInstance().clear();
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    static boolean isServiceRunning() {
        return serviceRunning;
    }

    private int activeRestartMode() {
        return hostRunning && AndroidScreenCaptureSession.getInstance().isAccessibilityCapture()
            ? START_STICKY : START_NOT_STICKY;
    }

    static boolean shouldRejectAutomaticStart(boolean automatic, boolean resumeAllowed) {
        return automatic && !resumeAllowed;
    }

    static boolean isHostRunning() {
        return hostRunning;
    }

    static String getLastStartFailure() { return lastStartFailure; }

    static boolean isCapturePaused() {
        return projectionState.isPaused();
    }

    static boolean shouldKeepScreenAwake(Context context) {
        return context.getSharedPreferences(PREFS_NAME, MODE_PRIVATE)
            .getBoolean(PREF_KEEP_SCREEN_AWAKE, true);
    }

    static String getRelayStatus() { return relayStatus; }

    private void stopRelayRegistration() {
        relayGeneration++;
        AndroidRelay.HostConnector previous = relayHost;
        relayHost = null;
        if (previous != null) previous.close();
        relayStatus = "中转未上线";
    }

    private void refreshRelayRegistration() {
        stopRelayRegistration();
        if (hostServer == null || !hostServer.isRunning()) return;
        try {
            AndroidRelay.Options options = AndroidRelaySettings.load(this);
            if (options == null || !options.publish) return;
            long generation = relayGeneration;
            relayHost = new AndroidRelay.HostConnector(options, RemoteDeskProtocol.HOST_PORT,
                android.os.Build.MODEL, value -> {
                    if (generation == relayGeneration) relayStatus = value;
                });
            relayHost.start();
        } catch (Exception ex) { relayStatus = "中转配置无法读取，请重新保存配置。"; }
    }

    private void onProjectionStopped(long projectionGeneration) {
        new Handler(Looper.getMainLooper()).post(() -> {
            if (!running || !projectionState.stopped(projectionGeneration)) {
                return;
            }

            AndroidSessionLog.info(
                "MediaProjection stopped; closing the host and retaining discovery for reauthorization.");
            hostRunning = false;
            stopRelayRegistration();
            if (hostServer != null) {
                hostServer.stop();
            }
            releaseStreamingLocks();
            try {
                // A locked device invalidates its projection grant on recent Android.
                // Keep presence only; never reuse the revoked token or claim the screen is online.
                startForegroundCompat(false);
                updateNotification();
            } catch (RuntimeException ex) {
                AndroidSessionLog.error("Could not retain discovery after screen capture stopped.", ex);
                stopSelf();
            }
        });
    }

    private synchronized void startDiscoveryResponder() {
        if (discoveryResponderStarted) {
            return;
        }

        discoveryResponderStarted = true;
        try {
            discoveryExecutor.execute(this::runDiscoveryResponder);
        } catch (RuntimeException ex) {
            discoveryResponderStarted = false;
            if (running) {
                AndroidSessionLog.error(
                    "Discovery responder worker could not start; host remains active.",
                    ex);
            }
        }
    }

    private void runDiscoveryResponder() {
        byte[] buffer = new byte[512];
        boolean boundLogged = false;
        try {
            while (running && !Thread.currentThread().isInterrupted()) {
                DatagramSocket socket = null;
                Exception failure = null;
                long boundAtNanos = 0L;
                try {
                    socket = AndroidDiscoverySockets.openBoundDiscoverySocket();
                    synchronized (this) {
                        if (!running) {
                            socket.close();
                            return;
                        }
                        discoverySocket = socket;
                    }
                    boundAtNanos = System.nanoTime();
                    if (!boundLogged) {
                        boundLogged = true;
                        AndroidSessionLog.info(
                            "Discovery responder bound on UDP " +
                                RemoteDeskProtocol.DISCOVERY_PORT + ".");
                    }

                    while (running) {
                        DatagramPacket request =
                            new DatagramPacket(buffer, buffer.length);
                        socket.receive(request);
                        String text = new String(
                            request.getData(),
                            request.getOffset(),
                            request.getLength(),
                            StandardCharsets.UTF_8);
                        if (!RemoteDeskProtocol.DISCOVERY_REQUEST.equals(text)) {
                            continue;
                        }

                        byte[] response = createDiscoveryResponse()
                            .getBytes(StandardCharsets.UTF_8);
                        DatagramPacket packet = new DatagramPacket(
                            response,
                            response.length,
                            request.getAddress(),
                            request.getPort());
                        socket.send(packet);
                        discoveryRetryPolicy.recordBound();
                    }
                } catch (Exception ex) {
                    failure = ex;
                } finally {
                    clearDiscoverySocket(socket);
                    if (socket != null) {
                        socket.close();
                    }
                }

                if (!running || Thread.currentThread().isInterrupted()) {
                    return;
                }

                long nowNanos = System.nanoTime();
                if (boundAtNanos != 0L &&
                    nowNanos - boundAtNanos >=
                        AndroidDiscoveryRetryPolicy.failureLogIntervalNanos()) {
                    // A socket that stayed healthy for a full diagnostic window
                    // starts a fresh backoff sequence even on a quiet LAN.
                    discoveryRetryPolicy.recordBound();
                }
                AndroidDiscoveryRetryPolicy.FailureAction action =
                    discoveryRetryPolicy.recordFailure(nowNanos);
                if (action.shouldLog) {
                    AndroidSessionLog.error(
                        "Discovery responder I/O failed; host/capture remain " +
                            "active and UDP discovery will rebind in " +
                            action.retryDelayMillis + " ms.",
                        failure == null
                            ? new IllegalStateException(
                                "Discovery responder ended without an error.")
                            : failure);
                }
                if (!sleepDiscoveryRetry(action.retryDelayMillis)) {
                    return;
                }
            }
        } finally {
            synchronized (this) {
                discoveryResponderStarted = false;
            }
        }
    }

    private boolean sleepDiscoveryRetry(long delayMillis) {
        try {
            Thread.sleep(delayMillis);
            return running;
        } catch (InterruptedException ex) {
            Thread.currentThread().interrupt();
            return false;
        }
    }

    private synchronized void clearDiscoverySocket(DatagramSocket socket) {
        if (discoverySocket == socket) {
            discoverySocket = null;
        }
    }

    private synchronized void closeDiscoverySocket() {
        DatagramSocket socket = discoverySocket;
        discoverySocket = null;
        if (socket != null) {
            socket.close();
        }
    }

    private String createDiscoveryResponse() throws Exception {
        JSONObject response = new JSONObject();
        boolean activeHost = hostServer != null && hostServer.isRunning();
        response.put("Type", RemoteDeskProtocol.DISCOVERY_RESPONSE_TYPE);
        response.put("MachineName", AndroidDeviceNames.displayName());
        response.put("DeviceId", AndroidRelaySettings.localDeviceId(this));
        response.put("Port", RemoteDeskProtocol.HOST_PORT);
        response.put("CaptureTarget", activeHost ? RemoteDeskProtocol.CAPTURE_TARGET_NAME
            : isCapturePaused() ? "录屏已停止，请在手机上重新授权" : "Android App 常驻，等待录屏授权");
        response.put("IsHostRunning", activeHost);
        response.put("CanRemoteStart", false);
        response.put("Platform", RemoteDeskProtocol.PLATFORM_ANDROID);
        response.put("Capabilities", activeHost
            ? RemoteDeskHostServer.getCapabilities()
            : 0);
        return response.toString();
    }

    private Notification createNotification() {
        Notification.Builder builder = new Notification.Builder(this, CHANNEL_ID);

        String status = hostServer != null && hostServer.isRunning()
            ? AndroidScreenCaptureSession.getInstance().isAccessibilityCapture()
                ? "兼容被控运行中 · 锁屏后仍可连接 · 56565"
                : "正在监听 56565，可被局域网连接"
            : isCapturePaused() ? "录屏已停止，解锁后点此重新授权" : "发现常驻中，等待屏幕录制授权";
        return builder
            .setContentTitle("RemoteDesk Agent")
            .setContentText(status)
            .setSmallIcon(android.R.drawable.presence_online)
            .setContentIntent(createContentIntent())
            .addAction(new Notification.Action.Builder(
                android.graphics.drawable.Icon.createWithResource(
                    this, android.R.drawable.ic_menu_close_clear_cancel),
                "停止服务",
                PendingIntent.getService(this, 1,
                    new Intent(this, RemoteDeskForegroundService.class).putExtra(EXTRA_STOP, true),
                    PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE)).build())
            .setOngoing(true)
            .build();
    }

    private PendingIntent createContentIntent() {
        Intent intent = new Intent(this, MainActivity.class)
            .addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP | Intent.FLAG_ACTIVITY_SINGLE_TOP);
        int flags = PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE;

        return PendingIntent.getActivity(this, 0, intent, flags);
    }

    @SuppressWarnings("deprecation")
    private static Intent getProjectionData(Intent intent) {
        if (intent == null) {
            return null;
        }

        if (Build.VERSION.SDK_INT >= 33) {
            return intent.getParcelableExtra(EXTRA_RESULT_DATA, Intent.class);
        }

        return intent.getParcelableExtra(EXTRA_RESULT_DATA);
    }

    private void startForegroundCompat(boolean mediaProjection) {
        Notification notification = createNotification();
        if (Build.VERSION.SDK_INT >= 29) {
            startForeground(
                NOTIFICATION_ID,
                notification,
                foregroundServiceType(mediaProjection));
            return;
        }

        startForeground(NOTIFICATION_ID, notification);
    }

    @androidx.annotation.RequiresApi(Build.VERSION_CODES.Q)
    static int foregroundServiceType(boolean mediaProjection) {
        return mediaProjection
            ? ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION
            : ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE;
    }

    private void updateNotification() {
        NotificationManager manager = getSystemService(NotificationManager.class);
        if (manager != null) {
            manager.notify(NOTIFICATION_ID, createNotification());
        }
    }

    private void acquireMulticastLock() {
        if (multicastLock != null && multicastLock.isHeld()) {
            return;
        }

        WifiManager wifiManager = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
        if (wifiManager == null) {
            return;
        }

        multicastLock = wifiManager.createMulticastLock("RemoteDeskDiscovery");
        multicastLock.setReferenceCounted(false);
        multicastLock.acquire();
    }

    private void releaseMulticastLock() {
        if (multicastLock == null) {
            return;
        }

        WifiManager.MulticastLock previous = multicastLock;
        multicastLock = null;
        try {
            if (previous.isHeld()) previous.release();
        } catch (RuntimeException ignored) {
            // A revoked permission / dead system service must not crash teardown.
        }
    }

    private void acquireStreamingLocks() {
        acquireStreamingWifiLock();
        acquireStreamingWakeLock();
    }

    static boolean shouldHoldStreamingPower(boolean listening, boolean authenticatedSession) {
        return listening && authenticatedSession;
    }

    private void updateStreamingPower() {
        boolean active = shouldHoldStreamingPower(hostRunning,
            hostServer != null && hostServer.hasAuthenticatedSession());
        if (active) acquireStreamingLocks();
        else releaseStreamingLocks();
        try {
            // A listener is not a stream. Oplus/realme can forcibly stop an idle app
            // holding a screen lock for ~30 minutes, and clear its accessibility grant.
            screenAwakeController.setEnabled(active && shouldKeepScreenAwake(this));
        } catch (RuntimeException ex) {
            AndroidSessionLog.error("Could not keep the shared screen awake.", ex);
        }
    }

    @SuppressWarnings("deprecation")
    private AndroidScreenAwakeController.ScreenLock createScreenWakeLock() {
        PowerManager manager = (PowerManager) getSystemService(Context.POWER_SERVICE);
        if (manager == null) throw new IllegalStateException("Power manager unavailable");
        // FLAG_KEEP_SCREEN_ON only works while an Activity is visible. The host returns
        // to the launcher, so use an app-scoped dim screen lock for the approved session.
        // No ACQUIRE_CAUSES_WAKEUP: starting a session must not unlock/wake a locked phone.
        PowerManager.WakeLock wakeLock = manager.newWakeLock(
            PowerManager.SCREEN_DIM_WAKE_LOCK, "RemoteDesk:SharedScreen");
        wakeLock.setReferenceCounted(false);
        return new AndroidScreenAwakeController.ScreenLock() {
            @Override
            @SuppressLint("WakelockTimeout")
            public void acquire() { wakeLock.acquire(); }
            @Override
            public void release() { wakeLock.release(); }
            @Override
            public boolean isHeld() { return wakeLock.isHeld(); }
        };
    }

    private void acquireStreamingWifiLock() {
        if (streamingWifiLock != null && streamingWifiLock.isHeld()) {
            return;
        }

        try {
            WifiManager wifiManager = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
            if (wifiManager == null) {
                return;
            }

            streamingWifiLock = wifiManager.createWifiLock(
                getStreamingWifiLockMode(),
                "RemoteDeskStreaming");
            streamingWifiLock.setReferenceCounted(false);
            streamingWifiLock.acquire();
        } catch (RuntimeException ignored) {
            streamingWifiLock = null;
        }
    }

    @SuppressLint("WakelockTimeout")
    private void acquireStreamingWakeLock() {
        if (streamingWakeLock != null && streamingWakeLock.isHeld()) {
            return;
        }

        try {
            PowerManager powerManager = (PowerManager) getSystemService(Context.POWER_SERVICE);
            if (powerManager == null) {
                return;
            }

            streamingWakeLock = powerManager.newWakeLock(
                PowerManager.PARTIAL_WAKE_LOCK,
                "RemoteDesk:Streaming");
            streamingWakeLock.setReferenceCounted(false);
            // The host may legitimately stream for many hours. A fixed timeout would silently
            // degrade an otherwise healthy session; stop/onDestroy always releases this lock,
            // and Android releases it automatically if the process dies.
            streamingWakeLock.acquire();
        } catch (RuntimeException ignored) {
            streamingWakeLock = null;
        }
    }

    private void releaseStreamingLocks() {
        screenAwakeController.close();
        if (streamingWifiLock != null) {
            try {
                if (streamingWifiLock.isHeld()) {
                    streamingWifiLock.release();
                }
            } catch (RuntimeException ignored) {
            }

            streamingWifiLock = null;
        }

        if (streamingWakeLock != null) {
            try {
                if (streamingWakeLock.isHeld()) {
                    streamingWakeLock.release();
                }
            } catch (RuntimeException ignored) {
            }

            streamingWakeLock = null;
        }
    }

    @SuppressWarnings("deprecation")
    static int getStreamingWifiLockMode() {
        if (Build.VERSION.SDK_INT >= 29) {
            return WifiManager.WIFI_MODE_FULL_LOW_LATENCY;
        }

        return WifiManager.WIFI_MODE_FULL_HIGH_PERF;
    }

    private void createNotificationChannel() {
        NotificationChannel channel = new NotificationChannel(
            CHANNEL_ID,
            "RemoteDesk Agent",
            NotificationManager.IMPORTANCE_LOW);
        NotificationManager manager = getSystemService(NotificationManager.class);
        if (manager != null) {
            manager.createNotificationChannel(channel);
        }
    }
}
