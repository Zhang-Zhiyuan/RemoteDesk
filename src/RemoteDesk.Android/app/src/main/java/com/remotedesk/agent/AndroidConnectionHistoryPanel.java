package com.remotedesk.agent;

import android.app.Activity;
import android.app.AlertDialog;
import android.text.InputFilter;
import android.text.TextUtils;
import android.view.Gravity;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.PopupMenu;
import android.widget.TextView;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.function.Consumer;

/** Compact recent nodes; credentials stay in the Keystore-encrypted store, never in view labels. */
@android.annotation.SuppressLint("ViewConstructor") // Constructed with callbacks, never inflated from XML.
final class AndroidConnectionHistoryPanel extends LinearLayout implements AutoCloseable {
    private final Activity activity;
    private final Consumer<AndroidConnectionHistory.Node> connect, fill;
    private final Consumer<String> status;
    private final ExecutorService storage = Executors.newSingleThreadExecutor();
    private boolean closed, expanded;
    private List<AndroidConnectionHistory.Node> currentNodes = java.util.Collections.emptyList();

    AndroidConnectionHistoryPanel(Activity activity, Consumer<AndroidConnectionHistory.Node> connect,
            Consumer<AndroidConnectionHistory.Node> fill, Consumer<String> status) {
        super(activity);
        this.activity = activity; this.connect = connect; this.fill = fill; this.status = status;
        setOrientation(VERTICAL);
    }

    void refresh(Consumer<List<AndroidConnectionHistory.Node>> loaded) {
        if (closed) return;
        storage.execute(() -> {
            try {
                List<AndroidConnectionHistory.Node> nodes = AndroidConnectionHistoryStore.load(activity).entries();
                activity.runOnUiThread(() -> {
                    if (closed || activity.isFinishing()) return;
                    render(nodes);
                    if (loaded != null) loaded.accept(nodes);
                });
            } catch (Exception ex) {
                activity.runOnUiThread(() -> {
                    if (closed) return;
                    currentNodes = java.util.Collections.emptyList();
                    removeAllViews();
                    addView(AndroidUiTheme.createSectionSubtitle(activity, "历史连接暂时无法读取；不影响手动连接。"));
                });
            }
        });
    }

    private void render(List<AndroidConnectionHistory.Node> nodes) {
        currentNodes = nodes;
        removeAllViews();
        TextView heading = AndroidUiTheme.createFieldLabel(activity,
            nodes.isEmpty() ? "最近连接" : "最近连接 · " + nodes.size());
        heading.setPadding(0, dp(8), 0, dp(6));
        addView(heading);
        if (nodes.isEmpty()) {
            addView(AndroidUiTheme.createSectionSubtitle(activity, "连接成功后自动保存，下次点一下即可连接。"));
            return;
        }
        for (int i = 0; i < (expanded ? nodes.size() : Math.min(3, nodes.size())); i++) {
            AndroidConnectionHistory.Node node = nodes.get(i);
            LinearLayout row = new LinearLayout(activity);
            row.setGravity(Gravity.CENTER_VERTICAL);
            Button entry = button(node.title() + "\n" +
                (node.relay() ? "中转 · " + node.address() + " · " + node.relayDeviceId.substring(0, 8) : "直连 · " + node.address()));
            entry.setGravity(Gravity.START | Gravity.CENTER_VERTICAL);
            entry.setMaxLines(3); entry.setEllipsize(TextUtils.TruncateAt.END);
            entry.setPadding(dp(12), dp(8), dp(12), dp(8));
            entry.setContentDescription("连接 " + node.title() + "，" + (node.relay() ? "中转 " : "直连 ") + node.address());
            entry.setOnClickListener(view -> connect.accept(node));
            row.addView(entry, new LinearLayout.LayoutParams(0, LayoutParams.WRAP_CONTENT, 1));
            Button more = button("⋮");
            more.setContentDescription("管理连接 " + node.title());
            more.setOnClickListener(view -> {
                PopupMenu menu = new PopupMenu(activity, more);
                menu.getMenu().add("连接").setOnMenuItemClickListener(item -> { connect.accept(node); return true; });
                if (!node.relay()) menu.getMenu().add("填入地址和设备密钥").setOnMenuItemClickListener(item -> { fill.accept(node); return true; });
                menu.getMenu().add("修改备注").setOnMenuItemClickListener(item -> { rename(node); return true; });
                menu.getMenu().add("删除记录").setOnMenuItemClickListener(item -> { remove(node); return true; });
                menu.show();
            });
            LinearLayout.LayoutParams moreParams = new LinearLayout.LayoutParams(dp(48), LayoutParams.WRAP_CONTENT);
            moreParams.setMarginStart(dp(4)); row.addView(more, moreParams);
            LinearLayout.LayoutParams rowParams = new LinearLayout.LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT);
            rowParams.bottomMargin = dp(4); addView(row, rowParams);
        }
        if (nodes.size() > 3) {
            Button toggle = button(expanded ? "收起" : "查看全部 " + nodes.size() + " 台");
            toggle.setOnClickListener(view -> { expanded = !expanded; render(nodes); });
            addView(toggle);
        }
    }

    List<AndroidConnectionHistory.Node> entries() { return currentNodes; }

    void add() {
        LinearLayout content = new LinearLayout(activity); content.setOrientation(VERTICAL);
        content.setPadding(dp(24), dp(8), dp(24), 0);
        EditText address = new EditText(activity), port = new EditText(activity), password = new EditText(activity), remark = new EditText(activity);
        EditText[] fields = {address, port, password, remark};
        String[] hints = {"IP / 主机名", "端口（选填）", "设备密钥", "备注（可选）"};
        for (int i=0; i<fields.length; i++) {
            fields[i].setSingleLine(true); fields[i].setHint(hints[i]); fields[i].setSaveEnabled(false);
            fields[i].setContentDescription(hints[i]);
            AndroidUiTheme.styleInput(activity, fields[i]);
            content.addView(fields[i], new LinearLayout.LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT));
        }
        // Keep the rule visible after typing, and let it wrap with large system
        // fonts instead of clipping it inside a single-line input hint.
        android.widget.TextView help = AndroidUiTheme.createSectionSubtitle(activity,
            "端口留空时自动探测；设备密钥填写对方设备设置的密钥。");
        LinearLayout.LayoutParams helpParams = new LinearLayout.LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT);
        helpParams.topMargin = dp(8);
        content.addView(help, helpParams);
        password.setInputType(android.text.InputType.TYPE_CLASS_TEXT | android.text.InputType.TYPE_TEXT_VARIATION_PASSWORD);
        port.setInputType(android.text.InputType.TYPE_CLASS_NUMBER);
        remark.setFilters(new InputFilter[]{new InputFilter.LengthFilter(64)});
        android.widget.ScrollView scroll = new android.widget.ScrollView(activity); scroll.addView(content);
        AlertDialog dialog = new AlertDialog.Builder(activity).setTitle("新增设备").setView(scroll)
            .setNegativeButton("取消", null).setPositiveButton("保存设备", null).create();
        dialog.setOnShowListener(ignored -> dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(view -> {
            int requestedPort = RemoteDeskProtocol.HOST_PORT;
            try {
                if (!port.getText().toString().trim().isEmpty()) requestedPort = Integer.parseInt(port.getText().toString().trim());
                if (requestedPort < 1 || requestedPort > 65535) throw new IllegalArgumentException();
            } catch (IllegalArgumentException ex) { port.setError("端口应为 1–65535"); return; }
            MainActivity.RemoteEndpoint target = MainActivity.parseRemoteEndpoint(address.getText().toString(), requestedPort);
            if (target == null) { address.setError("请输入有效 IP 或主机名"); return; }
            String secret = password.getText().toString().trim(), note = remark.getText().toString();
            if (secret.isEmpty()) { password.setError("请输入设备密钥"); return; }
            boolean autoPort = port.getText().toString().trim().isEmpty() && !AndroidLanDevice.explicitPort(address.getText().toString());
            mutate(() -> AndroidConnectionHistoryStore.add(activity, target.host, target.port, secret, note, autoPort));
            dialog.dismiss();
        }));
        dialog.setOnDismissListener(ignored -> password.setText("")); dialog.show();
    }

    private void rename(AndroidConnectionHistory.Node node) {
        EditText remark = new EditText(activity);
        remark.setSingleLine(true); remark.setHint("备注（可留空）"); remark.setText(node.remark);
        remark.setFilters(new InputFilter[] {new InputFilter.LengthFilter(64)});
        LinearLayout content = new LinearLayout(activity);
        content.setPadding(dp(24), dp(8), dp(24), 0);
        content.addView(remark, new LinearLayout.LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT));
        new AlertDialog.Builder(activity).setTitle("修改备注").setView(content)
            .setNegativeButton("取消", null)
            .setPositiveButton("保存", (dialog, which) -> {
                String value = remark.getText().toString();
                mutate(() -> AndroidConnectionHistoryStore.rename(activity, node.id, value));
            })
            .show();
    }

    private void remove(AndroidConnectionHistory.Node node) {
        new AlertDialog.Builder(activity).setTitle("删除这条连接记录？")
            .setMessage(node.title() + "\n会删除此记录保存的地址和设备密钥，不会影响远端设备。")
            .setNegativeButton("取消", null)
            .setPositiveButton("删除", (dialog, which) -> mutate(() -> AndroidConnectionHistoryStore.remove(activity, node.id)))
            .show();
    }

    private interface Mutation { void run() throws Exception; }
    private void mutate(Mutation action) {
        if (closed) return;
        storage.execute(() -> {
            try {
                action.run();
                activity.runOnUiThread(() -> { if (!closed) refresh(null); });
            } catch (Exception ex) {
                activity.runOnUiThread(() -> { if (!closed) status.accept("历史连接保存失败，请重试。"); });
            }
        });
    }

    private Button button(String text) {
        Button button = new Button(activity); button.setText(text);
        AndroidUiTheme.styleButton(activity, button, AndroidUiTheme.ButtonRole.SECONDARY);
        return button;
    }
    private int dp(int value) { return AndroidDisplay.dp(activity, value); }
    @Override public void close() { closed = true; storage.shutdown(); }
}
