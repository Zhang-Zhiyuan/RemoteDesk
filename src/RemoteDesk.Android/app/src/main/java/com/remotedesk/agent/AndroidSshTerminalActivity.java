package com.remotedesk.agent;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Context;
import android.content.Intent;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.view.View;
import android.view.WindowManager;
import android.webkit.JavascriptInterface;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.Button;
import android.widget.EditText;
import android.widget.HorizontalScrollView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.Base64;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import org.json.JSONObject;

/** Dedicated SSH shell, not a RemoteDeskViewerActivity. Credentials never enter an Intent or saved state. */
public final class AndroidSshTerminalActivity extends Activity {
    private static final String ORIGIN = "https://ssh.remotedesk.invalid/";
    private final Handler ui = new Handler(Looper.getMainLooper());
    private AndroidRelay.Options target;
    private String deviceName, savedKey = "";
    private LinearLayout root, terminalPanel;
    private ScrollView loginPanel;
    private TextView status;
    private EditText username, password, deviceKey;
    private Button connect, ctrl;
    private WebView terminal;
    private volatile AndroidSshConnection connection;
    private volatile boolean destroyed;
    private boolean pageReady;
    private AlertDialog trustDialog;
    private volatile CountDownLatch trustWait, outputWait;
    private volatile int outputSequence;
    private volatile int inputGeneration;
    private byte[] selectedPrivateKey;
    private int selectionGeneration;
    private boolean identityBusy;
    private volatile boolean foreground;
    private boolean sshConnected;
    private String keepAliveId;
    private String pendingOutput;
    private int pendingSequence;
    private android.window.OnBackInvokedCallback backCallback;

    static void launch(Context context, AndroidRelay.Options options, String name) {
        context.startActivity(recallIntent(context, options, name));
    }

    private static Intent recallIntent(Context context, AndroidRelay.Options options, String name) {
        return new Intent(context, AndroidSshTerminalActivity.class)
            .setData(new android.net.Uri.Builder().scheme("remotedesk-ssh").authority("terminal")
                .appendPath(AndroidSshProtocol.alias(options)).build())
            .addFlags(Intent.FLAG_ACTIVITY_NEW_DOCUMENT)
            .putExtra("deviceId", options.deviceId).putExtra("relayPin", options.tlsCertificateSha256)
            .putExtra("deviceName", name); // No relay token, device key, SSH account or password.
    }

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_SECURE);
        AndroidDisplay.configureEdgeToEdge(this, true);
        try {
            AndroidRelay.Options options = AndroidRelaySettings.load(this);
            if (options == null || !options.tlsCertificateSha256.equals(getIntent().getStringExtra("relayPin")))
                throw new IOException("服务器登录已变化，请从在线列表重新打开 SSH。");
            target = options.target(getIntent().getStringExtra("deviceId"));
            deviceName = getIntent().getStringExtra("deviceName");
            if (deviceName == null) deviceName = target.deviceId;
            if (deviceName.length() > 120) deviceName = deviceName.substring(0, 120);
            AndroidConnectionHistory.Node saved = AndroidConnectionHistoryStore.load(this).findRelay(target);
            if (saved != null) savedKey = saved.password;
        } catch (Exception ex) {
            new AlertDialog.Builder(this).setTitle("无法打开 SSH").setMessage("服务器登录或设备身份已变化，请返回在线列表重试。")
                .setPositiveButton("返回", (d, w) -> finish()).setOnCancelListener(d -> finish()).show();
            return;
        }
        try {
            buildUi();
            if (android.os.Build.VERSION.SDK_INT >= 33) {
                backCallback = this::leaveTerminal;
                getOnBackInvokedDispatcher().registerOnBackInvokedCallback(android.window.OnBackInvokedDispatcher.PRIORITY_DEFAULT, backCallback);
            }
        }
        catch (RuntimeException ex) { new AlertDialog.Builder(this).setTitle("终端组件不可用")
            .setMessage("无法启动 Android System WebView。请启用或更新系统 WebView 后重试；尚未连接 SSH。")
            .setPositiveButton("返回", (d, w) -> finish()).setOnCancelListener(d -> finish()).show(); }
    }

    private void buildUi() {
        root = new LinearLayout(this); root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(AndroidUiTheme.BACKGROUND);
        root.setOnApplyWindowInsetsListener((view, insets) -> {
            AndroidDisplay.SafeInsets safe = AndroidDisplay.safeInsets(insets, true);
            view.setPadding(safe.left, safe.top, safe.right, safe.bottom);
            return insets;
        });
        status = label("SSH · " + deviceName);
        // A long device name/error must not consume the whole login form at large font scales.
        // Keep the full text available through a native, scrollable detail dialog.
        status.setMaxLines(2); status.setEllipsize(android.text.TextUtils.TruncateAt.END);
        status.setMinHeight(AndroidDisplay.dp(this, 48));
        status.setGravity(android.view.Gravity.CENTER_VERTICAL);
        status.setOnClickListener(v -> new AlertDialog.Builder(this).setTitle("SSH 状态")
            .setMessage(status.getText()).setPositiveButton("关闭", null).show());
        root.addView(status);
        loginPanel = new ScrollView(this);
        LinearLayout form = new LinearLayout(this); form.setOrientation(LinearLayout.VERTICAL);
        int pad = AndroidDisplay.dp(this, 12); form.setPadding(pad, pad, pad, pad);
        form.addView(label("目标：" + deviceName + "\nSSH 为独立通道。切到后台或返回列表保持连接，再点同一设备或通知可唤回原终端。断开 / 移除终端任务会结束连接；断网不会自动重放命令。"));
        form.addView(label(savedKey.isEmpty() ? "填写此目标设备的设备密钥，以及目标 SSH 账号密码。" :
            "复用已保存的设备密钥。下面仅填写目标 SSH 账号密码（不是中继 root 密码）。"));
        deviceKey = field("设备密钥", true); deviceKey.setText(savedKey);
        deviceKey.setContentDescription("目标设备密钥；已保存密钥可在此更改，仅用于本次 SSH 连接");
        form.addView(deviceKey);
        username = field("目标 SSH 用户名", false); form.addView(username);
        username.setText(getSharedPreferences("ssh_target_accounts", MODE_PRIVATE).getString(AndroidSshProtocol.alias(target), ""));
        password = field("目标 SSH 密码（不保存）", true); form.addView(password);
        form.addView(button("选择 SSH 私钥（可选）", v -> {
            Intent picker = new Intent(Intent.ACTION_OPEN_DOCUMENT).setType("*/*").addCategory(Intent.CATEGORY_OPENABLE)
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            try { startActivityForResult(picker, 7101); }
            catch (android.content.ActivityNotFoundException ex) { status.setText("系统没有可用的文件选择器，请使用密码登录或启用文件应用。"); }
        }));
        form.addView(button("改用密码登录", v -> { clearPrivateKey(); password.setHint("目标 SSH 密码（不保存）"); password.setContentDescription("目标 SSH 密码（不保存）"); status.setText(R.string.ssh_password_mode); }));
        form.addView(button("SSH 主机身份", v -> showHostIdentity()));
        connect = button("连接 SSH", v -> startConnection()); connect.setEnabled(false); form.addView(connect);
        Button back = button("返回在线设备", v -> finish()); form.addView(back);
        loginPanel.addView(form); root.addView(loginPanel, new LinearLayout.LayoutParams(-1, 0, 1));
        terminalPanel = new LinearLayout(this); terminalPanel.setOrientation(LinearLayout.VERTICAL);
        terminalPanel.setVisibility(View.GONE);
        terminal = new WebView(this); configureTerminal();
        terminalPanel.addView(terminal, new LinearLayout.LayoutParams(-1, 0, 1));
        HorizontalScrollView horizontal = new HorizontalScrollView(this);
        LinearLayout keys = new LinearLayout(this); keys.setOrientation(LinearLayout.HORIZONTAL);
        String[] names = {"键盘", "Ctrl", "Tab", "Esc", "←", "↑", "↓", "→"};
        String[] codes = {"keyboard", "ctrl", "tab", "esc", "left", "up", "down", "right"};
        for (int i = 0; i < names.length; i++) {
            final String code = codes[i];
            Button key = button(names[i], v -> {
                terminal.requestFocus(); terminal.evaluateJavascript("sshKey(" + JSONObject.quote(code) + ")", null);
                if (code.equals("keyboard")) {
                    android.view.inputmethod.InputMethodManager ime = (android.view.inputmethod.InputMethodManager)
                        getSystemService(Context.INPUT_METHOD_SERVICE);
                    if (ime != null) ime.showSoftInput(terminal, android.view.inputmethod.InputMethodManager.SHOW_IMPLICIT);
                }
            });
            key.setContentDescription("SSH " + names[i]); keys.addView(key);
            if (code.equals("ctrl")) ctrl = key;
        }
        keys.addView(button("断开", v -> disconnect("SSH 已断开；未发送的输入已丢弃。")));
        keys.addView(button("返回（保持连接）", v -> moveTaskToBack(true)));
        Button copy = button("复制", v -> terminal.evaluateJavascript("sshCopy()", null));
        copy.setContentDescription("复制终端所选文字；Ctrl+C 是远端中断，不是复制"); keys.addView(copy);
        keys.addView(button("粘贴", v -> pasteClipboard()));
        horizontal.addView(keys); terminalPanel.addView(horizontal);
        root.addView(terminalPanel, new LinearLayout.LayoutParams(-1, 0, 1));
        setContentView(root); root.requestApplyInsets();
        terminal.loadUrl(ORIGIN + "terminal.html");
        ui.postDelayed(() -> { if (!destroyed && !pageReady) terminalUnavailable(); }, 10000);
    }

    @android.annotation.SuppressLint("SetJavaScriptEnabled") // Fixed packaged terminal only; no remote HTML/navigation/URL.
    private void configureTerminal() {
        WebSettings settings = terminal.getSettings();
        settings.setJavaScriptEnabled(true); settings.setAllowFileAccess(false); settings.setAllowContentAccess(false);
        settings.setBlockNetworkLoads(true); settings.setDomStorageEnabled(false);
        settings.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);
        settings.setJavaScriptCanOpenWindowsAutomatically(false);
        terminal.setImportantForAutofill(View.IMPORTANT_FOR_AUTOFILL_NO_EXCLUDE_DESCENDANTS);
        terminal.setSaveEnabled(false);
        terminal.addJavascriptInterface(new Bridge(), "RemoteSsh");
        terminal.setWebViewClient(new WebViewClient() {
            @Override public void onPageFinished(WebView view, String url) {
                if (!ORIGIN.concat("terminal.html").equals(url)) { terminalUnavailable(); return; }
                view.evaluateJavascript("typeof window.sshOutput === 'function' && typeof ResizeObserver === 'function'",
                    value -> { if (!destroyed && !"true".equals(value)) terminalUnavailable(); });
            }
            @Override public void onReceivedError(WebView view, WebResourceRequest request, android.webkit.WebResourceError error) {
                if (request.isForMainFrame()) terminalUnavailable();
            }
            @Override public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) { return true; }
            @Override public WebResourceResponse shouldInterceptRequest(WebView view, WebResourceRequest request) {
                String url = request.getUrl().toString();
                String file = url.startsWith(ORIGIN) ? url.substring(ORIGIN.length()) : "";
                if (file.matches("terminal\\.(html|js|css)|xterm\\.(js|css)|addon-fit\\.js|compat\\.js")) {
                    try { return new WebResourceResponse(file.endsWith("html") ? "text/html" :
                        file.endsWith("css") ? "text/css" : "application/javascript", "UTF-8", getAssets().open("ssh/" + file)); }
                    catch (IOException ignored) { }
                }
                return new WebResourceResponse("text/plain", "UTF-8", new ByteArrayInputStream(new byte[0]));
            }
            @Override public boolean onRenderProcessGone(WebView view, android.webkit.RenderProcessGoneDetail detail) {
                disconnect("终端显示进程已退出，SSH 已安全断开。请返回在线列表重新打开。");
                pageReady = false; connect.setEnabled(false); return true;
            }
        });
    }

    private void terminalUnavailable() {
        disconnect("终端组件加载失败或系统 WebView 过旧。请更新/启用 Android System WebView 后重新打开；未建立可用终端。");
        pageReady = false;
        if (connect != null) connect.setEnabled(false);
    }

    private void startConnection() {
        if (connection != null || !pageReady || identityBusy) return;
        try {
            AndroidRelay.Options saved = AndroidRelaySettings.load(this);
            if (saved == null || !saved.tlsCertificateSha256.equals(target.tlsCertificateSha256))
                throw new IOException("服务器登录已变化");
            // An existing document task may outlive logout/re-login. Never
            // reconnect using credentials cached in that old activity.
            target = saved.target(target.deviceId);
            AndroidConnectionHistory.Node node = AndroidConnectionHistoryStore.load(this).findRelay(target);
            savedKey = node == null ? "" : node.password;
        } catch (Exception ex) {
            disconnect("服务器登录已变化，请从在线列表重新打开 SSH。"); return;
        }
        String user = username.getText().toString().trim();
        String key = deviceKey.length() == 0 ? savedKey : deviceKey.getText().toString();
        if (user.isEmpty() || user.length() > 128 || user.matches(".*[\\p{Cntrl}].*") ||
                (selectedPrivateKey == null && password.length() == 0) || password.length() > 4096 || key.isEmpty() || key.length() > 4096) {
            status.setText(R.string.ssh_credentials_required); return;
        }
        byte[] secret = password.getText().toString().getBytes(StandardCharsets.UTF_8);
        String sessionId = java.util.UUID.randomUUID().toString();
        if (!AndroidSshKeepAliveService.keep(this, sessionId, deviceName, recallIntent(this, target, deviceName),
                () -> ui.post(() -> { if (sessionId.equals(keepAliveId)) disconnect("SSH 已断开（通知、系统服务或服务器登录状态已变化）。"); }))) {
            Arrays.fill(secret, (byte) 0);
            status.setText(R.string.ssh_keepalive_unavailable);
            return;
        }
        keepAliveId = sessionId;
        ++selectionGeneration; // A pending provider read must not replace the chosen authentication mode.
        final int sessionGeneration = ++inputGeneration;
        password.setText(""); deviceKey.setText("");
        loginPanel.setVisibility(View.GONE); terminalPanel.setVisibility(View.VISIBLE);
        final AndroidSshConnection[] owner = new AndroidSshConnection[1];
        AndroidSshConnection next = new AndroidSshConnection(this, new AndroidSshConnection.Listener() {
            private boolean current() { return !destroyed && connection == owner[0]; }
            @Override public boolean confirmHostKey(String fingerprint) throws Exception {
                CountDownLatch wait = new CountDownLatch(1);
                java.util.concurrent.atomic.AtomicBoolean accepted = new java.util.concurrent.atomic.AtomicBoolean();
                ui.post(() -> {
                    if (!current()) { wait.countDown(); return; }
                    trustWait = wait;
                    trustDialog = new AlertDialog.Builder(AndroidSshTerminalActivity.this).setTitle("首次连接 SSH 主机")
                        .setMessage("目标：" + deviceName + "\n设备 ID：" + target.deviceId + "\n\n主机指纹：\n" + fingerprint +
                            "\n\n请通过可信渠道核对目标 sshd 指纹。确认后保存到此中继身份与设备；变化时将拒绝连接。")
                        .setNegativeButton("取消", (d, w) -> wait.countDown())
                        .setPositiveButton("指纹一致，信任", (d, w) -> { if (current()) accepted.set(true); wait.countDown(); })
                        .setOnCancelListener(d -> wait.countDown()).show();
                });
                boolean completed = wait.await(60, TimeUnit.SECONDS);
                ui.post(() -> { if (trustWait == wait && trustDialog != null) { trustDialog.dismiss(); trustDialog = null; } });
                return completed && accepted.get() && current();
            }
            @Override public void status(String text) { ui.post(() -> { if (current()) {
                status.setText(text); terminal.evaluateJavascript("sshEnabled(" + text.startsWith("SSH 已连接") + "," + sessionGeneration + ")", null);
                if (text.startsWith("SSH 已连接")) {
                    sshConnected = true;
                    android.content.SharedPreferences accounts = getSharedPreferences("ssh_target_accounts", MODE_PRIVATE);
                    String alias = AndroidSshProtocol.alias(target);
                    if (accounts.contains(alias) || accounts.getAll().size() < 256) accounts.edit().putString(alias, user).apply();
                }
            } }); }
            @Override public void output(byte[] bytes) throws Exception {
                CountDownLatch wait = new CountDownLatch(1);
                String encoded = Base64.getEncoder().encodeToString(bytes);
                Arrays.fill(bytes, (byte) 0);
                ui.post(() -> {
                    if (!current()) wait.countDown();
                    else {
                        // Publish only on the UI thread after checking this exact
                        // connection. A late old reader must never replace the
                        // current session's ACK latch or sequence after reconnect.
                        outputWait = wait;
                        pendingOutput = encoded;
                        pendingSequence = ++outputSequence;
                        displayPendingOutput();
                    }
                });
                long visibleWait = 0;
                while (!wait.await(100, TimeUnit.MILLISECONDS) && current()) {
                    // Background WebViews may stop running JavaScript. Keep one
                    // bounded chunk under backpressure, not an unbounded log or
                    // a false 10-second disconnect. SSH keepalives run separately.
                    visibleWait = foreground ? visibleWait + 100 : 0;
                    if (visibleWait >= 10000) throw new IOException("终端显示未响应，SSH 已断开。");
                }
                ui.post(() -> { if (current() && outputWait == wait) pendingOutput = null; });
            }
            @Override public void ended(String text) { ui.post(() -> { if (current()) disconnect(text); }); }
        });
        // Listener callbacks must be fenced to the exact session, never a later retry.
        owner[0] = next;
        connection = next;
        byte[] privateKey = selectedPrivateKey; selectedPrivateKey = null;
        password.setHint("目标 SSH 密码（不保存）"); password.setContentDescription("目标 SSH 密码（不保存）");
        next.start(target, key, user, secret, privateKey);
    }

    private void displayPendingOutput() {
        if (foreground && pageReady && !destroyed && pendingOutput != null && outputWait != null && outputWait.getCount() != 0)
            terminal.evaluateJavascript("sshOutput('" + pendingOutput + "'," + pendingSequence + ")", null);
    }

    @Override protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        if (target != null && target.deviceId.equals(intent.getStringExtra("deviceId")) &&
                target.tlsCertificateSha256.equals(intent.getStringExtra("relayPin"))) setIntent(intent);
    }

    @Override protected void onStart() {
        super.onStart(); foreground = true;
        if (connection != null) {
            try {
                AndroidRelay.Options saved = AndroidRelaySettings.load(this);
                if (!AndroidRelaySettings.sameSshLogin(target, saved)) disconnect("服务器登录已变化，SSH 已断开。");
            } catch (Exception ex) { disconnect("无法确认服务器登录，SSH 已断开。"); }
        }
        if (terminal != null && pageReady) terminal.evaluateJavascript("sshEnabled(" + sshConnected + "," + inputGeneration + ")", null);
        displayPendingOutput();
    }

    private void leaveTerminal() {
        if (connection != null) moveTaskToBack(true);
        else finish();
    }
    @SuppressWarnings("deprecation") // API 26–32 fallback; newer Android uses the registered dispatcher.
    @Override public void onBackPressed() { leaveTerminal(); }

    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request != 7101 || result != RESULT_OK || data == null || data.getData() == null) return;
        final int generation = ++selectionGeneration;
        final android.net.Uri uri = data.getData();
        status.setText(R.string.ssh_key_reading);
        AndroidRelay.thread("SshSelectedKey", () -> {
            byte[] bytes = null;
            try (java.io.InputStream stream = getContentResolver().openInputStream(uri)) {
                if (stream == null) throw new IOException("不可读取");
                bytes = AndroidSshProtocol.readPrivateKey(stream);
            } catch (Exception ignored) { }
            final byte[] selected = bytes;
            ui.post(() -> {
                if (destroyed || generation != selectionGeneration || connection != null) {
                    if (selected != null) Arrays.fill(selected, (byte) 0); return;
                }
                if (selected == null) { status.setText(R.string.ssh_key_read_failed); return; }
                clearPrivateKey(); selectedPrivateKey = selected;
                password.setText(""); password.setHint("私钥口令（无口令可留空）"); password.setContentDescription("所选 SSH 私钥口令（不保存，无口令可留空）");
                status.setText(R.string.ssh_key_selected);
            });
        }).start();
    }

    private void clearPrivateKey() {
        ++selectionGeneration; // Includes an explicit switch back to password while a provider is still reading.
        if (selectedPrivateKey != null) Arrays.fill(selectedPrivateKey, (byte) 0);
        selectedPrivateKey = null;
        if (password != null) { password.setHint("目标 SSH 密码（不保存）"); password.setContentDescription("目标 SSH 密码（不保存）"); }
    }

    private void showHostIdentity() {
        if (connection != null || identityBusy) return;
        AndroidSshHostKeys keys = new AndroidSshHostKeys(this);
        String alias = AndroidSshProtocol.alias(target);
        String fingerprint = keys.get(alias);
        AlertDialog.Builder dialog = new AlertDialog.Builder(this).setTitle("SSH 主机身份")
            .setMessage("目标：" + deviceName + "\n设备 ID：" + target.deviceId + "\n\n" +
                (fingerprint.isEmpty() ? "尚未保存 SSH 主机身份。首次连接必须核对并确认指纹。" : fingerprint +
                    "\n\n此身份仅用于此中继证书与目标设备的 SSH 服务；与中继管理员登录、远程桌面密钥分别保存。"))
            .setNegativeButton("关闭",null);
        if (!fingerprint.isEmpty()) dialog.setNeutralButton("忘记此身份",(d,w) -> {
            if (connection != null) return;
            new AlertDialog.Builder(this).setTitle("忘记此 SSH 主机身份？")
                .setMessage("仅在已通过独立可信渠道核实目标重装或主机密钥变更后继续。忘记后，下次连接仍须确认新指纹。不会修改其他设备或中继管理员的信任。")
                .setNegativeButton("取消",null).setPositiveButton("已核实，忘记",(confirm,which) -> {
                    if (connection != null || identityBusy) return;
                    identityBusy = true; connect.setEnabled(false);
                    AndroidRelay.thread("SshForgetIdentity",() -> {
                        boolean removed;
                        try { keys.forget(alias); removed = true; } catch (IOException ex) { removed = false; }
                        final boolean success = removed;
                        ui.post(() -> { if (!destroyed) { identityBusy = false; connect.setEnabled(pageReady);
                            status.setText(success ? "仅此设备的 SSH 主机身份已忘记；下次连接必须重新核实指纹。" : "无法忘记 SSH 主机身份，原身份保留。"); } });
                    }).start();
                }).show();
        });
        dialog.show();
    }

    private final class Bridge {
        @JavascriptInterface public void unavailable() { ui.post(() -> { if (!destroyed) terminalUnavailable(); }); }
        @JavascriptInterface public void ready() { ui.post(() -> { if (!destroyed) { pageReady = true; connect.setEnabled(true); } }); }
        @JavascriptInterface public void input(String text, int generation) {
            AndroidSshConnection current = connection;
            if (!foreground || current == null || generation != inputGeneration || text == null || text.isEmpty()) return;
            if (text.length() > 8192) { ui.post(() -> { if (connection == current) disconnect("SSH 输入过长，未发送；会话已断开。"); }); return; }
            byte[] bytes = text.getBytes(StandardCharsets.UTF_8);
            boolean sent = bytes.length <= 8192 && current.send(bytes);
            Arrays.fill(bytes, (byte) 0);
            if (!sent) ui.post(() -> { if (connection == current) disconnect("SSH 输入未发送（未就绪或队列已满）；为避免缺字误执行，会话已断开。"); });
        }
        @JavascriptInterface public void resize(int cols, int rows) { AndroidSshConnection current = connection; if (current != null) current.resize(cols, rows); }
        @JavascriptInterface public void ack(int sequence) { ui.post(() -> {
            // Sequence and latch are a pair; compare them on the same thread
            // that publishes the next chunk, including after reconnect.
            if (sequence == outputSequence && outputWait != null) outputWait.countDown();
        }); }
        @JavascriptInterface public void ctrl(boolean enabled) { ui.post(() -> { if (!destroyed) ctrl.setText(enabled ? "Ctrl ✓" : "Ctrl"); }); }
        @JavascriptInterface public void copy(String text) { ui.post(() -> {
            if (destroyed || text == null || text.length() > 65536) return;
            if (text.isEmpty()) { android.widget.Toast.makeText(AndroidSshTerminalActivity.this,"请先长按选择终端文字",android.widget.Toast.LENGTH_SHORT).show(); return; }
            android.content.ClipboardManager clipboard = (android.content.ClipboardManager)getSystemService(CLIPBOARD_SERVICE);
            if (clipboard != null) clipboard.setPrimaryClip(android.content.ClipData.newPlainText("SSH 选择文字", text));
        }); }
        @JavascriptInterface public void chooseCopy(String text) { ui.post(() -> {
            if (destroyed || text == null || text.length() > 65536) return;
            TextView selectable = label(text); selectable.setTextIsSelectable(true);
            int padding = AndroidDisplay.dp(AndroidSshTerminalActivity.this,16);
            selectable.setPadding(padding,padding,padding,padding);
            ScrollView scroll = new ScrollView(AndroidSshTerminalActivity.this); scroll.addView(selectable);
            new AlertDialog.Builder(AndroidSshTerminalActivity.this).setTitle("长按选择终端文字")
                .setView(scroll).setNegativeButton("关闭",null).setPositiveButton("复制全部可见文字",(d,w)->copy(text)).show();
        }); }
    }

    private void pasteClipboard() {
        AndroidSshConnection current = connection;
        if (current == null) return;
        android.content.ClipboardManager clipboard = (android.content.ClipboardManager)getSystemService(CLIPBOARD_SERVICE);
        android.content.ClipData data = clipboard == null ? null : clipboard.getPrimaryClip();
        CharSequence content = data == null || data.getItemCount() == 0 ? null : data.getItemAt(0).getText();
        if (content == null || content.length() == 0) { status.setText("剪贴板没有可粘贴的文字。"); return; }
        String text = content.toString();
        byte[] bytes = text.getBytes(StandardCharsets.UTF_8);
        if (bytes.length > 8192) { Arrays.fill(bytes,(byte)0); status.setText(R.string.ssh_paste_too_long); return; }
        String encoded = Base64.getEncoder().encodeToString(bytes); Arrays.fill(bytes,(byte)0);
        new AlertDialog.Builder(this).setTitle("发送剪贴板文字？")
            .setMessage("内容将发往当前 SSH 终端；换行或控制字符可能立即执行命令。\n\n" + text.substring(0,Math.min(400,text.length())) + (text.length()>400?"\n……":""))
            .setNegativeButton("取消",null).setPositiveButton("确认粘贴",(d,w)-> {
                if (!destroyed && connection == current) terminal.evaluateJavascript("sshPaste('"+encoded+"')",null);
            }).show();
    }

    private void disconnect(String message) {
        AndroidSshConnection current = connection; connection = null;
        sshConnected = false; pendingOutput = null;
        String previous = keepAliveId; keepAliveId = null;
        if (previous != null) AndroidSshKeepAliveService.release(this, previous);
        ++inputGeneration;
        if (trustWait != null) { trustWait.countDown(); trustWait = null; }
        if (outputWait != null) { outputWait.countDown(); outputWait = null; }
        if (trustDialog != null) { trustDialog.dismiss(); trustDialog = null; }
        if (current != null) current.close();
        if (terminal != null && !destroyed && pageReady) terminal.evaluateJavascript("sshEnabled(false," + inputGeneration + ")", null);
        if (status != null) status.setText(message);
        if (loginPanel != null) { loginPanel.setVisibility(View.VISIBLE); terminalPanel.setVisibility(View.GONE); }
    }
    @Override protected void onStop() {
        foreground = false;
        super.onStop();
        if (password != null) password.setText("");
        if (deviceKey != null) deviceKey.setText("");
        clearPrivateKey();
        // Established SSH is owned by the visible foreground service, not by
        // page visibility. Explicit disconnect, task removal or destruction ends it.
    }
    @Override public void onWindowFocusChanged(boolean focused) {
        super.onWindowFocusChanged(focused);
        if (focused) AndroidDisplay.configureEdgeToEdge(this, true);
    }
    @Override protected void onDestroy() {
        if (android.os.Build.VERSION.SDK_INT >= 33 && backCallback != null)
            getOnBackInvokedDispatcher().unregisterOnBackInvokedCallback(backCallback);
        destroyed = true; disconnect("SSH 已关闭。"); savedKey = "";
        clearPrivateKey();
        if (terminal != null) { terminal.removeJavascriptInterface("RemoteSsh"); terminal.destroy(); }
        ui.removeCallbacksAndMessages(null); super.onDestroy();
    }
    private TextView label(String text) { TextView v = new TextView(this); v.setText(text); v.setTextColor(AndroidUiTheme.TEXT); v.setTextSize(15); return v; }
    private EditText field(String hint, boolean secret) {
        EditText field = new EditText(this); field.setHint(hint); field.setContentDescription(hint);
        field.setSingleLine(true); field.setInputType(InputType.TYPE_CLASS_TEXT |
            (secret ? InputType.TYPE_TEXT_VARIATION_PASSWORD : InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS));
        field.setSaveEnabled(false); field.setImportantForAutofill(View.IMPORTANT_FOR_AUTOFILL_NO);
        AndroidUiTheme.styleInput(this, field); return field;
    }
    private Button button(String text, View.OnClickListener click) {
        Button button = new Button(this); button.setText(text); AndroidUiTheme.styleButton(this, button, AndroidUiTheme.ButtonRole.SECONDARY);
        button.setOnClickListener(click); return button;
    }
}
