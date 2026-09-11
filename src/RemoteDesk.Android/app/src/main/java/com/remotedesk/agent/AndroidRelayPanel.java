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

/** Online devices first; administrator configuration is hidden after enrollment. */
@android.annotation.SuppressLint("ViewConstructor") // Programmatic panel requiring an explicit connection callback; never inflated from XML.
final class AndroidRelayPanel extends LinearLayout {
    private final Handler ui = new Handler(Looper.getMainLooper());
    private final ExecutorService worker = Executors.newSingleThreadExecutor(r -> AndroidRelay.thread("RelayDirectory", r));
    interface Connector { void connect(AndroidRelay.Options target, String name, boolean editKey); }
    private final Connector connectTarget;
    private final Consumer<String> useDirectAddress;
    private final LinearLayout content, devices, configuration;
    private final TextView status, hostStatus, serverSummary;
    private final AndroidRelayPanelStatus panelStatus = new AndroidRelayPanelStatus();
    private final EditText server, sshPort, adminUser, adminPassword;
    private final CheckBox publish;
    private final Button saveButton, cancelSetupButton, manageButton, clearButton;
    private boolean restoringPublish;
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
            hostStatus.setText(getContext().getString(R.string.relay_host_status,
                RemoteDeskForegroundService.getRelayStatus()));
            if (content.getVisibility() == VISIBLE && System.currentTimeMillis() - lastRefresh > 15000) refresh();
            ui.postDelayed(this, 1000);
        }
    };

    AndroidRelayPanel(Context context, Consumer<AndroidRelay.Options> connectTarget) {
        this(context, connectTarget, null);
    }

    AndroidRelayPanel(Context context, Consumer<AndroidRelay.Options> connectTarget, Consumer<String> useDirectAddress) {
        this(context, (target, name, editKey) -> connectTarget.accept(target), useDirectAddress);
    }

    AndroidRelayPanel(Context context, Connector connectTarget, Consumer<String> useDirectAddress) {
        super(context);
        this.connectTarget = connectTarget;
        this.useDirectAddress = useDirectAddress;
        setOrientation(VERTICAL);
        Button expand = button("公网中继 · 在线设备", false);
        addView(expand, layout());
        content = new LinearLayout(context);
        content.setOrientation(VERTICAL);
        content.setVisibility(VISIBLE);
        addView(content, layout());
        serverSummary = AndroidUiTheme.createSectionSubtitle(context, "尚未登录服务器");
        content.addView(serverSummary, layout());
        manageButton = button("服务器设置", false);
        content.addView(manageButton, layout());
        configuration = new LinearLayout(context);
        configuration.setOrientation(VERTICAL);
        content.addView(configuration, layout());
        configuration.addView(AndroidUiTheme.createSectionSubtitle(context,
            "首次用服务器 root 密码登录，之后自动连接。连接设备时另填该设备自己的密钥。"), layout());
        manageButton.setOnClickListener(view -> {
            configuration.setVisibility(configuration.getVisibility() == VISIBLE ? GONE : VISIBLE);
            manageButton.setText(configuration.getVisibility() == VISIBLE ? "收起服务器设置" : "服务器设置");
        });
        server = field("服务器地址 / IP", false);
        adminPassword = field("服务器 root / 管理员密码", true);
        adminPassword.setHint("仅本次 SSH 登录使用，不保存；已登录可留空");
        adminPassword.setImportantForAutofill(View.IMPORTANT_FOR_AUTOFILL_NO);
        LinearLayout advanced = new LinearLayout(context);
        advanced.setOrientation(VERTICAL);
        advanced.setVisibility(GONE);
        Button advancedButton = button("高级设置（通常不用改）", false);
        advancedButton.setOnClickListener(view -> advanced.setVisibility(advanced.getVisibility() == VISIBLE ? GONE : VISIBLE));
        configuration.addView(advancedButton, layout());
        sshPort = field(advanced, "SSH 端口（默认 22）", false);
        sshPort.setInputType(InputType.TYPE_CLASS_NUMBER);
        adminUser = field(advanced, "服务器管理员账号", false);
        advanced.addView(AndroidUiTheme.createSectionSubtitle(context,
            "默认 root。中继端口和内部连接配置自动获取，无需填写密钥或证书。"), layout());
        configuration.addView(advanced, layout());
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
        saveButton = button("登录服务器", true);
        saveButton.setOnClickListener(view -> save());
        configuration.addView(saveButton, layout());
        cancelSetupButton = button("取消登录", false);
        cancelSetupButton.setVisibility(GONE);
        cancelSetupButton.setOnClickListener(view -> cancelSetup());
        configuration.addView(cancelSetupButton, layout());
        Button refresh = button("刷新在线设备", false);
        refresh.setOnClickListener(view -> refresh());
        content.addView(refresh, layout());
        Button report = button("立即上报本机 IP", false);
        report.setOnClickListener(view -> {
            if (!RemoteDeskForegroundService.isServiceRunning()) {
                setStatus("请先启动本机被控端并启用中转上线。");
                return;
            }
            getContext().startService(new Intent(getContext(), RemoteDeskForegroundService.class)
                .putExtra(RemoteDeskForegroundService.EXTRA_REPORT_ADDRESS, true));
            setStatus("已请求上报；稍后刷新在线设备可核对地址。");
        });
        content.addView(report, layout());
        devices = new LinearLayout(context);
        devices.setOrientation(VERTICAL);
        content.addView(devices, layout());
        clearButton = button("退出服务器", false);
        clearButton.setOnClickListener(view -> new AlertDialog.Builder(context)
            .setTitle("退出服务器？").setMessage("本机将从此服务器下线，设备记录和设备密钥保留。再次接入需要服务器管理员密码。\n\n如果服务器重装或身份变化，请先核实，再退出并重新登录。")
            .setNegativeButton("取消", null).setPositiveButton("退出服务器", (dialog, which) -> clear()).show());
        configuration.addView(clearButton, layout());
        expand.setOnClickListener(view -> {
            content.setVisibility(content.getVisibility() == VISIBLE ? GONE : VISIBLE);
            if (content.getVisibility() == VISIBLE) refresh();
        });
        try {
            deviceId = AndroidRelaySettings.localDeviceId(context);
            saved = AndroidRelaySettings.load(context);
            if (saved != null) {
                deviceId = saved.deviceId;
                server.setText(saved.serverAddress); sshPort.setText(String.format(java.util.Locale.ROOT, "%d", saved.sshPort));
                adminUser.setText(saved.adminUsername);
                publish.setChecked(saved.publish);
                setStatus(R.string.relay_saved_private);
            } else {
                sshPort.setText(R.string.relay_default_ssh_port); adminUser.setText(R.string.relay_default_admin);
                setStatus("请填写服务器地址和 root 密码；无需填写中继密钥或证书。");
            }
        } catch (Exception ex) { setStatus("中转配置无法读取，请重新登录服务器。"); }
        updateServerUi();
        publish.setOnCheckedChangeListener((button, checked) -> updatePublish(checked));
    }

    private void updateServerUi() {
        boolean configured = saved != null;
        serverSummary.setText(configured ? "服务器：" + saved.serverAddress + " · 已保存登录" : "尚未登录服务器");
        configuration.setVisibility(configured ? GONE : VISIBLE);
        manageButton.setVisibility(configured ? VISIBLE : GONE);
        manageButton.setText("服务器设置");
        clearButton.setEnabled(configured);
        publish.setEnabled(configured && !setupBusy);
    }

    private void updatePublish(boolean enabled) {
        if (restoringPublish || saved == null || closed || setupBusy) return;
        AndroidRelay.Options previous = saved;
        try {
            AndroidRelay.Options next = AndroidRelayUiPolicy.publish(previous, enabled,
                options -> AndroidRelaySettings.save(getContext(), options));
            saved = next;
            epoch++; AndroidRelay.close(pendingSocket);
            panelStatus.beginSetup();
            applyHostSettings();
            setStatus(enabled ? "已允许本机上线；被控端开启后自动发布。" : "已停止本机中继发布，仍可连接其他设备。");
        } catch (Exception failure) {
            // If persistence failed, the effective configuration is unchanged.
            // If the service could not be notified, keep the saved state visible.
            restoringPublish = true;
            publish.setChecked(saved.publish);
            restoringPublish = false;
            showSetupFailure(saved == previous ? "修改未保存，开关已恢复，请重试。" : "设置已保存，但服务更新失败；请重启本机被控端。");
        }
    }

    private LayoutParams layout() {
        LayoutParams value = new LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT);
        value.bottomMargin = Math.round(8 * getResources().getDisplayMetrics().density);
        return value;
    }

    private EditText field(String label, boolean secret) {
        return field(configuration, label, secret);
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
        panelStatus.beginSetup();
        try {
            if (deviceId == null) deviceId = AndroidRelaySettings.localDeviceId(getContext());
            String portText = sshPort.getText().toString().trim();
            int loginPort = portText.isEmpty() ? 22 : Integer.parseInt(portText);
            String host = AndroidRelay.checkedServer(server.getText().toString(), loginPort);
            String username = AndroidRelayAdminLogin.username(adminUser.getText().toString());
            boolean reuse = adminPassword.length() == 0 && saved != null &&
                saved.serverAddress.equalsIgnoreCase(host) && saved.sshPort == loginPort && saved.adminUsername.equals(username);
            if (adminPassword.length() == 0 && !reuse) {
                showSetupFailure("首次登录或更换服务器时，请输入服务器 root / 管理员密码；不是设备密钥。");
                return;
            }
            final int generation = ++epoch;
            AndroidRelay.close(pendingSocket);
            setSetupBusy(true);
            if (reuse) {
                verifyAndSave(new AndroidRelay.Options(saved.serverAddress, saved.port, saved.accessToken,
                    saved.tlsCertificateSha256, deviceId, publish.isChecked(), saved.sshPort,
                    saved.adminUsername, saved.sshHostKeySha256), generation);
                return;
            }
            String expected = AndroidRelayAdminLogin.knownIdentity(saved, host, loginPort);
            boolean publishDevice = publish.isChecked();
            byte[] secret = adminPassword.getText().toString().getBytes(java.nio.charset.StandardCharsets.UTF_8);
            adminPassword.setText("");
            AndroidRelayAdminLogin.Operation operation = new AndroidRelayAdminLogin.Operation();
            setupSocket(operation, generation);
            setStatus("正在登录服务器 SSH 并自动获取中继配置……");
            try {
                worker.execute(() -> {
                    try {
                        AndroidRelay.Options options = operation.login(getContext().getApplicationContext(), host,
                            loginPort, username, secret, expected, deviceId, publishDevice);
                        ui.post(() -> { if (setupCurrent(generation)) verifyAndSave(options, generation); });
                    } catch (AndroidRelayAdminLogin.LoginFailure ex) { setupFailed(generation, ex.getMessage()); }
                    finally { java.util.Arrays.fill(secret, (byte) 0); operation.close(); }
                });
            } catch (RuntimeException failure) {
                java.util.Arrays.fill(secret, (byte) 0); operation.close(); throw failure;
            }
        } catch (NumberFormatException ex) {
            showSetupFailure(getContext().getString(R.string.relay_invalid_port));
        } catch (IllegalArgumentException ex) {
            showSetupFailure(ex.getMessage());
        } catch (AndroidRelayAdminLogin.LoginFailure ex) {
            showSetupFailure(ex.getMessage());
        } catch (Exception ex) {
            setSetupBusy(false);
            showSetupFailure("无法开始配置，请检查本机存储状态后重试。");
        }
    }

    private void setSetupBusy(boolean value) {
        setupBusy = value;
        for (EditText field : new EditText[] {server, sshPort, adminUser, adminPassword}) field.setEnabled(!value);
        publish.setEnabled(!value && saved != null);
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
            showSetupFailure(message);
        });
    }

    private void cancelSetup() {
        panelStatus.beginSetup();
        epoch++;
        AndroidRelay.close(pendingSocket);
        setSetupBusy(false);
        setStatus("已取消，原配置未更改。");
    }

    private void verifyAndSave(AndroidRelay.Options options, int generation) {
        if (!setupCurrent(generation)) return;
        setStatus("服务器配置已就绪，正在验证公网中继连接……");
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
                        server.setText(options.serverAddress);
                        sshPort.setText(String.format(java.util.Locale.ROOT, "%d", options.sshPort));
                        adminUser.setText(options.adminUsername);
                        devices.removeAllViews();
                        setSetupBusy(false);
                        updateServerUi();
                        try { applyHostSettings(); }
                        catch (Exception serviceFailure) {
                            showSetupFailure("登录已保存，但服务更新失败；请重启本机被控端。");
                            refresh();
                            return;
                        }
                        setStatus("服务器登录成功，下次自动连接；root 密码未保存。");
                        refresh();
                    } catch (Exception ex) {
                        setSetupBusy(false);
                        showSetupFailure("连接成功，但本机无法保存配置，请检查存储后重试。");
                    }
                });
            } catch (AndroidRelay.IdentityFailure ex) { setupFailed(generation, ex.getMessage()); }
            catch (Exception ex) { setupFailed(generation, "中继连接未完成，请检查服务器中继端口是否开放；可重新用 root 密码登录。原配置未更改。"); }
        });
    }

    private void clear() {
        try {
            cancelSetup();
            AndroidRelaySettings.save(getContext(), null);
            saved = null; epoch++; devices.removeAllViews();
            adminPassword.setText(""); server.setText("");
            sshPort.setText(R.string.relay_default_ssh_port); adminUser.setText(R.string.relay_default_admin);
            AndroidRelay.close(pendingSocket);
            updateServerUi();
            try { applyHostSettings(); }
            catch (Exception serviceFailure) {
                showSetupFailure("已退出并清除登录，但服务更新失败；请重启本机被控端以停止旧连接。");
                return;
            }
            setStatus("已退出服务器，设备记录和设备密钥保留。");
        } catch (Exception ex) { showSetupFailure("配置清除失败，请重试。"); }
    }

    private void showSetupFailure(String message) {
        setSetupBusy(false);
        panelStatus.failed(message);
        setStatus("");
    }

    private void setStatus(int resourceId) { setStatus(getContext().getString(resourceId)); }
    private void setStatus(String message) { status.setText(panelStatus.display(message)); }

    private void refresh() {
        if (!active || closed || busy || setupBusy || saved == null) return;
        busy = true; lastRefresh = System.currentTimeMillis();
        final int generation = epoch;
        final AndroidRelay.Options options = saved;
        devices.removeAllViews(); setStatus("正在读取在线设备……");
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
                    if (safeIdentityError != null) setStatus(safeIdentityError);
                    else setStatus(R.string.relay_directory_failed);
                    return;
                }
                setStatus(getContext().getString(R.string.relay_online_count, online.size()));
                for (AndroidRelay.Device device : online) {
                    boolean local = device.deviceId.equals(deviceId);
                    Button item = button(device.name + " · " + device.platform + "\n" +
                        (local ? "本机（不可自连）" : device.busy ? "使用中（可接管）" : "在线 · 点击中转连接") +
                        "\n" + device.addressDisplay(), false);
                    item.setEnabled(!local);
                    item.setOnClickListener(view -> {
                        if (!closed && active && !setupBusy && epoch == generation && saved == options)
                            connectTarget.connect(options.target(device.deviceId), device.name, false);
                    });
                    item.setOnLongClickListener(view -> {
                        if (!closed && active && !setupBusy && epoch == generation && saved == options)
                            connectTarget.connect(options.target(device.deviceId), device.name, true);
                        return true;
                    });
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
        setStatus("正在核对设备最新地址……");
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
                    setStatus("无法取得最新地址；请刷新列表，或使用中转连接。"); return;
                }
                setStatus("已取得最新地址；仅可达的内网地址能直连。跨网仍用中转。");
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
        if (active == enabled && !closed) return;
        active = enabled; epoch++;
        ui.removeCallbacks(ticker);
        if (enabled) { lastRefresh = 0; ui.post(ticker); }
        else {
            AndroidRelay.close(pendingSocket);
            if (setupBusy) { setSetupBusy(false); setStatus("配置已暂停，原配置未更改；可重新保存连接。"); }
        }
    }

    void close() {
        closed = true; active(false); worker.shutdownNow();
    }
}
