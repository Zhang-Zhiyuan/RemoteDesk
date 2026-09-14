package com.remotedesk.agent;

import android.app.Activity;
import android.app.AlertDialog;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.text.TextUtils;
import android.view.Gravity;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;
import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.function.Consumer;
import java.util.function.Supplier;

/** Foreground-only, bounded discovery; pausing the screen cancels sockets and Wi-Fi locks. */
@android.annotation.SuppressLint("ViewConstructor")
final class AndroidLanDiscoveryPanel extends LinearLayout implements AutoCloseable {
    private static final long REFRESH_MILLIS = 30_000;
    private final Activity activity;
    private final Handler ui = new Handler(Looper.getMainLooper());
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Supplier<String> enteredAddress;
    private final Consumer<AndroidLanDevice> select;
    private final TextView status;
    private final LinearLayout devices;
    private final Button refresh, detectPort;
    private AndroidLanDiscovery pending;
    private List<AndroidLanDevice> nearby = Collections.emptyList();
    private long refreshedAt;
    private int epoch;
    private boolean active, closed, busy;
    private final Runnable ticker = new Runnable() {
        @Override public void run() {
            if (!active || closed) return;
            if (!busy && SystemClock.elapsedRealtime() - refreshedAt >= REFRESH_MILLIS) scan(null, null);
            ui.postDelayed(this, 2000);
        }
    };

    AndroidLanDiscoveryPanel(Activity activity, Supplier<String> enteredAddress, Consumer<AndroidLanDevice> select) {
        super(activity); this.activity = activity; this.enteredAddress = enteredAddress; this.select = select;
        setOrientation(VERTICAL);
        status = AndroidUiTheme.createSectionSubtitle(activity, "正在查找局域网设备…");
        LinearLayout actions = new LinearLayout(activity);
        refresh = button("查找附近设备"); refresh.setOnClickListener(view -> scan(null, null));
        detectPort = button("探测端口"); detectPort.setContentDescription("探测所填 IP 的端口"); detectPort.setOnClickListener(view -> {
            MainActivity.RemoteEndpoint endpoint = MainActivity.parseRemoteEndpoint(enteredAddress.get(), RemoteDeskProtocol.HOST_PORT);
            if (endpoint == null) { status.setText(R.string.discovery_enter_host); return; }
            resolve(endpoint.host, result -> showChoices(result, select));
        });
        actions.addView(refresh, new LinearLayout.LayoutParams(0, LayoutParams.WRAP_CONTENT, 1));
        actions.addView(detectPort, new LinearLayout.LayoutParams(0, LayoutParams.WRAP_CONTENT, 1));
        actions.addOnLayoutChangeListener((view, left, top, right, bottom, oldLeft, oldTop, oldRight, oldBottom) -> {
            int widthDp = Math.round((right - left) / getResources().getDisplayMetrics().density);
            boolean stacked = AndroidAdaptiveLayout.stackDiscoveryActions(widthDp, getResources().getConfiguration().fontScale);
            int orientation = stacked ? VERTICAL : HORIZONTAL;
            if (actions.getOrientation() == orientation) return;
            actions.setOrientation(orientation);
            for (Button action : new Button[] { refresh, detectPort }) {
                action.setLayoutParams(new LinearLayout.LayoutParams(
                    stacked ? LayoutParams.MATCH_PARENT : 0, LayoutParams.WRAP_CONTENT, stacked ? 0 : 1));
            }
        });
        addView(actions);
        status.setPadding(0, dp(6), 0, dp(6)); addView(status);
        devices = new LinearLayout(activity); devices.setOrientation(VERTICAL); addView(devices);
    }

    void active(boolean value) {
        if (closed || active == value) return;
        active = value; ui.removeCallbacks(ticker);
        if (value) { scan(null, null); ui.postDelayed(ticker, 2000); }
        else { cancelPending(); nearby = Collections.emptyList(); render(); }
    }

    void resolve(String host, Consumer<List<AndroidLanDevice>> completed) {
        scan(host, result -> {
            List<AndroidLanDevice> ready = new ArrayList<>();
            for (AndroidLanDevice device : result) if (device.listening) ready.add(device);
            completed.accept(ready);
        });
    }

    void locate(AndroidConnectionHistory.Node node, Consumer<List<AndroidLanDevice>> completed) {
        List<AndroidLanDevice> current = AndroidLanDevice.candidates(node, nearby);
        if (SystemClock.elapsedRealtime() - refreshedAt < REFRESH_MILLIS && !current.isEmpty()) {
            completed.accept(current); return;
        }
        scan(null, result -> {
            List<AndroidLanDevice> candidates = AndroidLanDevice.candidates(node, result);
            if (!candidates.isEmpty()) completed.accept(candidates);
            else resolve(node.host, completed);
        });
    }

    void showChoices(List<AndroidLanDevice> options, Consumer<AndroidLanDevice> chosen) {
        if (!active || closed) return;
        if (options.isEmpty()) {
            status.setText(R.string.discovery_no_port);
        } else if (options.size() == 1) chosen.accept(options.get(0));
        else {
            String[] labels = new String[options.size()];
            for (int i = 0; i < labels.length; i++) labels[i] = options.get(i).description();
            new AlertDialog.Builder(activity).setTitle("选择设备 / 端口").setItems(labels,
                (dialog, which) -> { if (active && !closed) chosen.accept(options.get(which)); })
                .setNegativeButton("取消", null).show();
        }
    }

    private void scan(String target, Consumer<List<AndroidLanDevice>> completed) {
        if (!active || closed) return;
        cancelPending(); busy = true;
        refresh.setEnabled(false); detectPort.setEnabled(false);
        status.setText(target == null ? "正在查找局域网设备和监听端口…" : "正在探测端口，不发送口令…");
        final int generation = epoch;
        AndroidLanDiscovery scanner = new AndroidLanDiscovery(); pending = scanner;
        // A slow DNS resolver must not leave the UI stuck or retain a multicast
        // lock. Its late result is fenced by epoch and cannot launch a viewer.
        Runnable timeout = () -> {
            if (!active || closed || epoch != generation) return;
            cancelPending(); refreshedAt = SystemClock.elapsedRealtime();
            status.setText("发现超时；可以手动填写地址，或使用中转在线列表。");
            if (completed != null) completed.accept(Collections.emptyList());
        };
        ui.postDelayed(timeout, 6500);
        worker.execute(() -> {
            List<AndroidLanDevice> found = Collections.emptyList();
            try {
                List<AndroidConnectionHistory.Node> known;
                try { known = AndroidConnectionHistoryStore.load(activity).entries(); }
                catch (Exception ignored) { known = Collections.emptyList(); }
                found = scanner.scan(activity.getApplicationContext(), target, known);
            } catch (Exception ignored) { /* Missing interfaces/DNS/UDP do not break manual connections. */ }
            finally { scanner.close(); }
            List<AndroidLanDevice> result = found;
            ui.post(() -> {
                ui.removeCallbacks(timeout);
                if (!active || closed || generation != epoch) return;
                pending = null; busy = false; refresh.setEnabled(true); detectPort.setEnabled(true);
                if (target == null) {
                    nearby = result; refreshedAt = SystemClock.elapsedRealtime();
                } else {
                    LinkedHashMap<String, AndroidLanDevice> merged = new LinkedHashMap<>();
                    for (AndroidLanDevice device : nearby) if (!device.host.equals(target)) merged.put(device.address(), device);
                    for (AndroidLanDevice device : result)
                        if (merged.containsKey(device.address()) || merged.size() < AndroidLanDiscovery.MAX_DEVICES) merged.put(device.address(), device);
                    nearby = new ArrayList<>(merged.values());
                }
                render();
                status.setText(result.isEmpty()
                    ? "未发现设备：请确认对方已打开 RemoteDesk。同网段可自动发现；跨网段可填 IP 探测端口，或走中转。"
                    : "发现 " + result.size() + " 个设备 / 端口，点选即可连接；无需记 IP 和端口。");
                if (completed != null) completed.accept(result);
            });
        });
    }

    private void render() {
        devices.removeAllViews();
        List<AndroidLanDevice> visible = AndroidLanDevice.collapseAliases(nearby);
        for (int i = 0; i < Math.min(3, visible.size()); i++) {
            AndroidLanDevice device = visible.get(i);
            Button item = button(device.description());
            item.setGravity(Gravity.START | Gravity.CENTER_VERTICAL);
            item.setMaxLines(3); item.setEllipsize(TextUtils.TruncateAt.END);
            item.setPadding(dp(12), dp(8), dp(12), dp(8));
            item.setContentDescription("发现设备 " + device.name + "，" + device.address());
            item.setOnClickListener(view -> select.accept(device));
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT);
            params.bottomMargin = dp(4); devices.addView(item, params);
        }
        if (visible.size() > 3) {
            Button all = button("查看全部发现设备（" + visible.size() + "）");
            all.setOnClickListener(view -> showChoices(visible, select)); devices.addView(all);
        }
    }

    private void cancelPending() {
        epoch++; busy = false;
        if (pending != null) { pending.close(); pending = null; }
        refresh.setEnabled(true); detectPort.setEnabled(true);
    }
    private Button button(String label) {
        Button button = new Button(activity); button.setText(label);
        AndroidUiTheme.styleButton(activity, button, AndroidUiTheme.ButtonRole.SECONDARY);
        return button;
    }
    private int dp(int value) { return AndroidDisplay.dp(activity, value); }
    @Override public void close() { active(false); closed = true; ui.removeCallbacksAndMessages(null); worker.shutdownNow(); }
}
