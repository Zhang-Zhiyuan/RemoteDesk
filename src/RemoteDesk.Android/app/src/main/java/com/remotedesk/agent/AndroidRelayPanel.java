package com.remotedesk.agent;

import android.app.AlertDialog;
import android.content.Context;
import android.content.Intent;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.view.View;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;

import java.net.Socket;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.function.Consumer;

/** Collapsed by default so private relay configuration does not crowd mobile controls. */
@android.annotation.SuppressLint("ViewConstructor") // Programmatic panel requiring an explicit connection callback; never inflated from XML.
final class AndroidRelayPanel extends LinearLayout {
    private final Handler ui = new Handler(Looper.getMainLooper());
    private final ExecutorService worker = Executors.newSingleThreadExecutor(r -> AndroidRelay.thread("RelayDirectory", r));
    private final Consumer<AndroidRelay.Options> connectTarget;
    private final Consumer<String> useDirectAddress;
    private final LinearLayout content, devices;
    private final TextView status, hostStatus;
    private final EditText server, port, token, pin;
    private final CheckBox publish;
    private AndroidRelay.Options saved;
    private String deviceId;
    private volatile boolean closed, active;
    private volatile Socket pendingSocket;
    private boolean busy;
    private volatile int epoch;
    private long lastRefresh;
    private final Runnable ticker = new Runnable() {
        @Override public void run() {
            if (!active || closed) return;
            hostStatus.setText(RemoteDeskForegroundService.getRelayStatus());
            if (content.getVisibility() == VISIBLE && System.currentTimeMillis() - lastRefresh > 15000) refresh();
            ui.postDelayed(this, 1000);
        }
    };

    AndroidRelayPanel(Context context, Consumer<AndroidRelay.Options> connectTarget) {
        this(context, connectTarget, null);
    }

    AndroidRelayPanel(Context context, Consumer<AndroidRelay.Options> connectTarget, Consumer<String> useDirectAddress) {
        super(context);
        this.connectTarget = connectTarget;
        this.useDirectAddress = useDirectAddress;
        setOrientation(VERTICAL);
        Button expand = button("公网中转 · 配置与在线设备", false);
        addView(expand, layout());
        content = new LinearLayout(context);
        content.setOrientation(VERTICAL);
        content.setVisibility(GONE);
        addView(content, layout());
        content.addView(AndroidUiTheme.createSectionSubtitle(context,
            "使用已配置的私有服务器。共享访问密钥不是远控口令；目标口令仍填上方“连接口令”。"), layout());
        server = field("中转服务器 / IP", false);
        port = field("中转端口（默认 56567）", false);
        port.setInputType(InputType.TYPE_CLASS_NUMBER);
        token = field("中转共享访问密钥", true);
        pin = field("TLS 证书 SHA-256 指纹", false);
        publish = new CheckBox(context);
        publish.setText(R.string.relay_publish_addresses);
        publish.setTextColor(AndroidUiTheme.TEXT);
        publish.setChecked(true);
        content.addView(publish, layout());
        status = new TextView(context);
        status.setTextColor(AndroidUiTheme.MUTED);
        hostStatus = new TextView(context);
        hostStatus.setTextColor(AndroidUiTheme.MUTED);
        content.addView(status, layout());
        content.addView(hostStatus, layout());
        Button save = button("保存中转配置", true);
        save.setOnClickListener(view -> save());
        content.addView(save, layout());
        Button refresh = button("刷新在线设备", false);
        refresh.setOnClickListener(view -> refresh());
        content.addView(refresh, layout());
        Button report = button("立即上报本机 IP", false);
        report.setOnClickListener(view -> {
            if (!RemoteDeskForegroundService.isServiceRunning()) {
                status.setText("请先启动本机被控端并启用中转上线。");
                return;
            }
            getContext().startService(new Intent(getContext(), RemoteDeskForegroundService.class)
                .putExtra(RemoteDeskForegroundService.EXTRA_REPORT_ADDRESS, true));
            status.setText("已请求上报；稍后刷新在线设备可核对地址。");
        });
        content.addView(report, layout());
        devices = new LinearLayout(context);
        devices.setOrientation(VERTICAL);
        content.addView(devices, layout());
        Button clear = button("清除中转配置", false);
        clear.setOnClickListener(view -> new AlertDialog.Builder(context)
            .setTitle("清除中转配置？").setMessage("本机将从中转下线，直连与远控口令不变。")
            .setNegativeButton("取消", null).setPositiveButton("清除", (dialog, which) -> clear()).show());
        content.addView(clear, layout());
        expand.setOnClickListener(view -> {
            content.setVisibility(content.getVisibility() == VISIBLE ? GONE : VISIBLE);
            if (content.getVisibility() == VISIBLE) refresh();
        });
        try {
            deviceId = AndroidRelaySettings.localDeviceId(context);
            saved = AndroidRelaySettings.load(context);
            if (saved != null) {
                deviceId = saved.deviceId;
                server.setText(saved.serverAddress); port.setText(String.format(java.util.Locale.ROOT, "%d", saved.port));
                token.setText(saved.accessToken); pin.setText(saved.tlsCertificateSha256);
                publish.setChecked(saved.publish);
                status.setText(R.string.relay_saved_private);
            } else {
                port.setText(String.format(java.util.Locale.ROOT, "%d", AndroidRelay.PORT));
                status.setText("请填写服务器配置；录屏和无障碍授权仍需正常开启。");
            }
        } catch (Exception ex) { status.setText("中转配置无法读取，请重新保存。不得跳过指纹校验。"); }
    }

    private LayoutParams layout() {
        LayoutParams value = new LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT);
        value.bottomMargin = Math.round(8 * getResources().getDisplayMetrics().density);
        return value;
    }

    private EditText field(String label, boolean secret) {
        TextView caption = new TextView(getContext());
        caption.setText(label); caption.setTextColor(AndroidUiTheme.TEXT);
        content.addView(caption, layout());
        EditText input = new EditText(getContext());
        input.setHint(label); input.setSingleLine(true); input.setSaveEnabled(false);
        input.setInputType(InputType.TYPE_CLASS_TEXT | (secret ? InputType.TYPE_TEXT_VARIATION_PASSWORD : InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD));
        AndroidUiTheme.styleInput(getContext(), input);
        content.addView(input, layout());
        return input;
    }

    private Button button(String text, boolean primary) {
        Button button = new Button(getContext()); button.setText(text);
        AndroidUiTheme.styleButton(getContext(), button, primary ? AndroidUiTheme.ButtonRole.PRIMARY : AndroidUiTheme.ButtonRole.SECONDARY);
        return button;
    }

    private void applyHostSettings() {
        if (RemoteDeskForegroundService.isServiceRunning()) {
            getContext().startService(new Intent(getContext(), RemoteDeskForegroundService.class)
                .putExtra(RemoteDeskForegroundService.EXTRA_REFRESH_RELAY, true));
        }
    }

    private void save() {
        try {
            if (deviceId == null) deviceId = AndroidRelaySettings.localDeviceId(getContext());
            AndroidRelay.Options options = new AndroidRelay.Options(server.getText().toString(),
                Integer.parseInt(port.getText().toString().trim()), token.getText().toString(),
                pin.getText().toString(), deviceId, publish.isChecked());
            AndroidRelaySettings.save(getContext(), options);
            saved = options; epoch++; devices.removeAllViews();
            AndroidRelay.close(pendingSocket);
            applyHostSettings();
            status.setText("配置已加密保存；本机上线仍需要启动被控端。");
            refresh();
        } catch (Exception ex) { status.setText("未保存：请检查服务器、端口、密钥、指纹及本机存储状态。"); }
    }

    private void clear() {
        try {
            AndroidRelaySettings.save(getContext(), null);
            saved = null; epoch++; devices.removeAllViews();
            token.setText(""); pin.setText(""); server.setText("");
            AndroidRelay.close(pendingSocket);
            applyHostSettings();
            status.setText("中转配置已清除。");
        } catch (Exception ex) { status.setText("配置清除失败，请重试。"); }
    }

    private void refresh() {
        if (!active || closed || busy || saved == null) return;
        busy = true; lastRefresh = System.currentTimeMillis();
        final int generation = epoch;
        final AndroidRelay.Options options = saved;
        devices.removeAllViews(); status.setText("正在读取在线设备……");
        worker.execute(() -> {
            List<AndroidRelay.Device> result = null;
            String identityError = null;
            try {
                result = AndroidRelay.listDevices(getContext(), options, socket -> {
                    pendingSocket = socket;
                    if (socket != null && (!active || closed || epoch != generation)) AndroidRelay.close(socket);
                });
            } catch (AndroidRelay.IdentityFailure ex) { identityError = ex.getMessage(); }
            catch (Exception ignored) { }
            final List<AndroidRelay.Device> online = result;
            final String safeIdentityError = identityError;
            ui.post(() -> {
                busy = false;
                if (closed || !active) return;
                if (epoch != generation) { refresh(); return; }
                if (online == null) {
                    if (safeIdentityError != null) status.setText(safeIdentityError);
                    else status.setText(R.string.relay_directory_failed);
                    return;
                }
                status.setText(getContext().getString(R.string.relay_online_count, online.size()));
                for (AndroidRelay.Device device : online) {
                    boolean local = device.deviceId.equals(deviceId);
                    Button item = button(device.name + " · " + device.platform + "\n" +
                        (local ? "本机（不可自连）" : device.busy ? "使用中（可接管）" : "在线 · 点击中转连接") +
                        "\n" + device.addressDisplay(), false);
                    item.setEnabled(!local);
                    item.setOnClickListener(view -> connectTarget.accept(options.target(device.deviceId)));
                    devices.addView(item, layout());
                    if (!local && useDirectAddress != null && !device.directAddresses.isEmpty() && device.directPort > 0) {
                        Button address = button("查看 / 使用 IP", false);
                        address.setOnClickListener(view -> showAddresses(options, device.deviceId));
                        devices.addView(address, layout());
                    }
                }
            });
        });
    }

    private void showAddresses(AndroidRelay.Options options, String id) {
        if (closed || !active || busy || saved != options) return;
        busy = true;
        final int generation = epoch;
        status.setText("正在核对设备最新地址……");
        worker.execute(() -> {
            AndroidRelay.Device result = null;
            try {
                for (AndroidRelay.Device device : AndroidRelay.listDevices(getContext(), options, socket -> {
                    pendingSocket = socket;
                    if (socket != null && (closed || !active || epoch != generation)) AndroidRelay.close(socket);
                })) if (device.deviceId.equals(id)) result = device;
            } catch (Exception ignored) { }
            final AndroidRelay.Device target = result;
            ui.post(() -> {
                busy = false;
                if (closed || !active || epoch != generation || saved != options) return;
                if (target == null || target.directAddresses.isEmpty() || target.directPort == 0) {
                    status.setText("无法取得最新地址；请刷新列表，或使用中转连接。"); return;
                }
                status.setText("已取得最新地址；仅可达的内网地址能直连。跨网仍用中转。");
                String[] addresses = new String[target.directAddresses.size()];
                for (int i = 0; i < addresses.length; i++) addresses[i] = target.directAddresses.get(i) + ":" + target.directPort;
                new AlertDialog.Builder(getContext()).setTitle(target.name + " · 选择地址填入直连")
                    .setItems(addresses, (dialog, index) -> {
                        if (!closed && active && epoch == generation && saved == options) useDirectAddress.accept(addresses[index]);
                    }).setNegativeButton("取消", null).show();
            });
        });
    }

    void active(boolean enabled) {
        active = enabled; epoch++;
        ui.removeCallbacks(ticker);
        if (enabled) { lastRefresh = 0; ui.post(ticker); }
        else AndroidRelay.close(pendingSocket);
    }

    void close() {
        closed = true; active(false); worker.shutdownNow();
    }
}
