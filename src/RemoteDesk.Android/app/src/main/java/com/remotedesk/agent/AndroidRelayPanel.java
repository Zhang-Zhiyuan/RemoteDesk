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
    private final Button saveButton, cancelSetupButton;
    private AlertDialog trustDialog;
    private boolean setupBusy;
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
            "填写服务器和共享访问密钥即可；首次连接确认一次，以后自动记住。共享密钥不是上方的远控口令。"), layout());
        server = field("中转服务器 / IP", false);
        port = field("中转端口（默认 56567）", false);
        port.setInputType(InputType.TYPE_CLASS_NUMBER);
        token = field("中转共享访问密钥", true);
        LinearLayout advanced = new LinearLayout(context);
        advanced.setOrientation(VERTICAL);
        advanced.setVisibility(GONE);
        Button advancedButton = button("高级设置（通常不用改）", false);
        advancedButton.setOnClickListener(view -> advanced.setVisibility(advanced.getVisibility() == VISIBLE ? GONE : VISIBLE));
        content.addView(advancedButton, layout());
        pin = field(advanced, "服务器身份指纹（可选）", false);
        pin.setHint("留空自动获取；已配对的服务器自动沿用");
        advanced.addView(AndroidUiTheme.createSectionSubtitle(context,
            "只有核对身份或服务器重装后才需要手动填写。更换已保存的身份会再次确认。"), layout());
        content.addView(advanced, layout());
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
        saveButton = button("保存并连接", true);
        saveButton.setOnClickListener(view -> save());
        content.addView(saveButton, layout());
        cancelSetupButton = button("取消连接", false);
        cancelSetupButton.setVisibility(GONE);
        cancelSetupButton.setOnClickListener(view -> cancelSetup());
        content.addView(cancelSetupButton, layout());
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
                token.setText(saved.accessToken);
                publish.setChecked(saved.publish);
                status.setText(R.string.relay_saved_private);
            } else {
                port.setText(String.format(java.util.Locale.ROOT, "%d", AndroidRelay.PORT));
                status.setText("请填写服务器配置；录屏和无障碍授权仍需正常开启。");
            }
        } catch (Exception ex) { status.setText("中转配置无法读取，请重新配置服务器。"); }
    }

    private LayoutParams layout() {
        LayoutParams value = new LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT);
        value.bottomMargin = Math.round(8 * getResources().getDisplayMetrics().density);
        return value;
    }

    private EditText field(String label, boolean secret) {
        return field(content, label, secret);
    }

    private EditText field(LinearLayout parent, String label, boolean secret) {
        TextView caption = new TextView(getContext());
        caption.setText(label); caption.setTextColor(AndroidUiTheme.TEXT);
        parent.addView(caption, layout());
        EditText input = new EditText(getContext());
        input.setHint(label); input.setSingleLine(true); input.setSaveEnabled(false);
        input.setInputType(InputType.TYPE_CLASS_TEXT | (secret ? InputType.TYPE_TEXT_VARIATION_PASSWORD : InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD));
        AndroidUiTheme.styleInput(getContext(), input);
        parent.addView(input, layout());
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
        if (closed || !active || setupBusy) return;
        try {
            if (deviceId == null) deviceId = AndroidRelaySettings.localDeviceId(getContext());
            String portText = port.getText().toString().trim();
            AndroidRelayEnrollment.Draft draft = new AndroidRelayEnrollment.Draft(server.getText().toString(),
                portText.isEmpty() ? AndroidRelay.PORT : Integer.parseInt(portText), token.getText().toString(),
                deviceId, publish.isChecked());
            String knownPin = AndroidRelayEnrollment.knownPin(saved, draft, pin.getText().toString());
            final int generation = ++epoch;
            AndroidRelay.close(pendingSocket);
            setSetupBusy(true);
            if (!knownPin.isEmpty()) {
                if (AndroidRelayEnrollment.replacesIdentity(saved, draft, knownPin))
                    confirmIdentity(draft, knownPin, generation, true);
                else verifyAndSave(draft.options(knownPin), generation);
                return;
            }
            status.setText("正在连接服务器，获取身份信息……尚未发送共享密钥。");
            worker.execute(() -> {
                try {
                    String discovered = AndroidRelayEnrollment.discover(draft.server, draft.port,
                        socket -> setupSocket(socket, generation));
                    ui.post(() -> {
                        if (setupCurrent(generation)) confirmIdentity(draft, discovered, generation, false);
                    });
                } catch (Exception ex) { setupFailed(generation, "无法连接服务器，请检查地址、端口和网络；原配置未更改。"); }
            });
        } catch (NumberFormatException ex) {
            status.setText(R.string.relay_invalid_port);
        } catch (IllegalArgumentException ex) {
            status.setText(ex.getMessage());
        } catch (Exception ex) {
            setSetupBusy(false);
            status.setText("无法开始配置，请检查本机存储状态后重试。");
        }
    }

    private void setSetupBusy(boolean value) {
        setupBusy = value;
        for (EditText field : new EditText[] {server, port, token, pin}) field.setEnabled(!value);
        publish.setEnabled(!value);
        saveButton.setEnabled(!value);
        cancelSetupButton.setVisibility(value ? VISIBLE : GONE);
    }

    private boolean setupCurrent(int generation) { return !closed && active && setupBusy && epoch == generation; }

    private void setupSocket(Socket socket, int generation) {
        pendingSocket = socket;
        if (socket != null && (closed || !active || epoch != generation)) AndroidRelay.close(socket);
    }

    private void setupFailed(int generation, String message) {
        ui.post(() -> {
            if (!setupCurrent(generation)) return;
            setSetupBusy(false);
            status.setText(message);
        });
    }

    private void cancelSetup() {
        epoch++;
        AndroidRelay.close(pendingSocket);
        if (trustDialog != null) { trustDialog.dismiss(); trustDialog = null; }
        setSetupBusy(false);
        status.setText("已取消，原配置未更改。");
    }

    private void confirmIdentity(AndroidRelayEnrollment.Draft draft, String identity, int generation, boolean replacement) {
        if (!setupCurrent(generation)) return;
        String message = draft.server + ":" + draft.port + "\n\n" + (replacement
            ? "你正在更换这台服务器已保存的身份。请确认服务器确实由你更换或重装，再继续。"
            : "首次连接会记住这台服务器，以后自动校验，无需填写证书。请确认地址属于你，并在可信网络中完成首次配置。") +
            "\n\n确认后才会发送共享访问密钥。";
        trustDialog = new AlertDialog.Builder(getContext())
            .setTitle(replacement ? "更新服务器身份？" : "信任这台中转服务器？")
            .setMessage(message)
            .setNegativeButton("取消", (dialog, which) -> cancelSetup())
            .setPositiveButton(replacement ? "确认更新并连接" : "信任并连接", (dialog, which) -> {
                trustDialog = null;
                if (setupCurrent(generation)) verifyAndSave(draft.options(identity), generation);
            }).create();
        trustDialog.setOnCancelListener(dialog -> cancelSetup());
        trustDialog.show();
    }

    private void verifyAndSave(AndroidRelay.Options options, int generation) {
        if (!setupCurrent(generation)) return;
        status.setText("正在验证服务器和共享访问密钥……");
        worker.execute(() -> {
            try {
                // Reconnect with the exact accepted identity. The observation
                // socket is never reused to send credentials or directory data.
                AndroidRelay.listDevices(getContext(), options, socket -> setupSocket(socket, generation));
                ui.post(() -> {
                    if (!setupCurrent(generation)) return;
                    try {
                        AndroidRelaySettings.save(getContext(), options);
                        saved = options;
                        epoch++;
                        port.setText(String.format(java.util.Locale.ROOT, "%d", options.port));
                        pin.setText("");
                        devices.removeAllViews();
                        setSetupBusy(false);
                        applyHostSettings();
                        status.setText("连接成功，服务器身份和配置已保存；下次自动连接。");
                        refresh();
                    } catch (Exception ex) {
                        setSetupBusy(false);
                        status.setText("连接成功，但本机无法保存配置，请检查存储后重试。");
                    }
                });
            } catch (AndroidRelay.IdentityFailure ex) { setupFailed(generation, ex.getMessage()); }
            catch (Exception ex) { setupFailed(generation, "验证未完成，请检查网络、地址和中转访问密钥；原配置未更改。"); }
        });
    }

    private void clear() {
        try {
            cancelSetup();
            AndroidRelaySettings.save(getContext(), null);
            saved = null; epoch++; devices.removeAllViews();
            token.setText(""); pin.setText(""); server.setText("");
            AndroidRelay.close(pendingSocket);
            applyHostSettings();
            status.setText("中转配置已清除。");
        } catch (Exception ex) { status.setText("配置清除失败，请重试。"); }
    }

    private void refresh() {
        if (!active || closed || busy || setupBusy || saved == null) return;
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
        if (closed || !active || busy || setupBusy || saved != options) return;
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
        else {
            AndroidRelay.close(pendingSocket);
            if (trustDialog != null) { trustDialog.dismiss(); trustDialog = null; }
            if (setupBusy) { setSetupBusy(false); status.setText("配置已暂停，原配置未更改；可重新保存连接。"); }
        }
    }

    void close() {
        closed = true; active(false); worker.shutdownNow();
    }
}
