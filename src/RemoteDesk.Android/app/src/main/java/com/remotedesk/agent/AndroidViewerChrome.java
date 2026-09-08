package com.remotedesk.agent;

import android.app.Activity;
import android.graphics.Color;
import android.text.InputFilter;
import android.text.InputType;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.inputmethod.EditorInfo;
import android.widget.Button;
import android.widget.EditText;
import android.widget.HorizontalScrollView;
import android.widget.LinearLayout;
import android.widget.TextView;

/** Compact, app-local controls. No floating-window/accessibility permission. */
final class AndroidViewerChrome {
    interface Actions {
        void keyboard(boolean open);
        void mode();
        void mouse(int button);
        void drag();
        void more();
        void screens();
        void diagnostics();
        void shortcut(int... keys);
        boolean text(String text);
    }

    final LinearLayout header, dock, mainRow, keyboardPanel, mousePanel;
    final TextView status, health, hint;
    final View indicator;
    final EditText composer;
    final Button mode, keyboard, mouse, drag, screens, send;
    boolean keyboardOpen, mouseOpen;
    private boolean compact;
    private boolean compactKeys;
    private final HorizontalScrollView shortcutStrip;
    private final LinearLayout entryRow;
    private final Button compactSwitch, compactClose;
    private boolean modeInitialized, previousTrackpad, previousLocked;
    private final Activity activity;
    private final Actions actions;

    AndroidViewerChrome(Activity activity, Actions actions) {
        this.activity = activity; this.actions = actions;
        header = column(); header.setBackgroundColor(0xff111d30);
        header.setPadding(dp(14), dp(8), dp(14), dp(8));
        header.setMinimumHeight(dp(48));
        header.setOnClickListener(v -> actions.diagnostics());
        header.setContentDescription("连接状态与诊断，点击查看详情");
        LinearLayout headline = row();
        indicator = new View(activity);
        LinearLayout.LayoutParams dot = new LinearLayout.LayoutParams(dp(7), dp(7));
        dot.setMarginEnd(dp(9)); headline.addView(indicator, dot);
        status = text(13, Color.WHITE); status.setMaxLines(1);
        headline.addView(status, new LinearLayout.LayoutParams(0, -2, 1));
        header.addView(headline);
        health = text(10, 0xff99aac3); health.setMaxLines(1);
        header.addView(health);

        dock = column(); dock.setBackgroundColor(0xff111d30); dock.setElevation(dp(8));
        dock.setPadding(dp(8), dp(5), dp(8), dp(5));
        hint = text(10, 0xffa6b7cf); hint.setGravity(Gravity.CENTER); hint.setMaxLines(1);
        hint.setPadding(0, dp(3), 0, dp(5));
        hint.setText("轻触点击 · 长按拖动 · 双指滚动 / 缩放");
        dock.addView(hint);

        keyboardPanel = column(); keyboardPanel.setVisibility(View.GONE);
        LinearLayout shortcuts = row();
        shortcut(shortcuts, "Esc", 0x1B); shortcut(shortcuts, "Tab", 0x09);
        shortcut(shortcuts, "Ctrl+A", 0x11, 0x41); shortcut(shortcuts, "Ctrl+C", 0x11, 0x43);
        shortcut(shortcuts, "Ctrl+V", 0x11, 0x56); shortcut(shortcuts, "Alt+Tab", 0x12, 0x09);
        shortcut(shortcuts, "←", 0x25); shortcut(shortcuts, "↑", 0x26);
        shortcut(shortcuts, "↓", 0x28); shortcut(shortcuts, "→", 0x27);
        shortcut(shortcuts, "退格", 0x08); shortcut(shortcuts, "Enter", 0x0D);
        shortcutStrip = scroll(shortcuts); keyboardPanel.addView(shortcutStrip);
        LinearLayout entry = row(); entryRow = entry;
        composer = new EditText(activity);
        composer.setTextSize(14); composer.setTextColor(Color.WHITE); composer.setHintTextColor(0xff99aac3);
        composer.setHint("输入文字后发送到远端");
        composer.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_MULTI_LINE);
        composer.setMaxLines(2);
        composer.setFilters(new InputFilter[] { new InputFilter.LengthFilter(AndroidViewerKeyboard.MAX_TEXT_LENGTH) });
        composer.setSaveEnabled(false);
        composer.setImeOptions(EditorInfo.IME_ACTION_SEND | EditorInfo.IME_FLAG_NO_EXTRACT_UI | EditorInfo.IME_FLAG_NO_PERSONALIZED_LEARNING);
        composer.setPadding(dp(10), dp(6), dp(10), dp(6));
        composer.setBackground(AndroidUiTheme.shape(activity, 0xff21334d, 10, 0xff354d6e, 1));
        composer.setOnEditorActionListener((v, id, event) -> {
            if (id != EditorInfo.IME_ACTION_SEND) return false;
            sendText(); return true;
        });
        entry.addView(composer, new LinearLayout.LayoutParams(0, dp(48), 1));
        send = button("发送", v -> sendText()); send.setEnabled(false); entry.addView(send);
        composer.addTextChangedListener(new android.text.TextWatcher() {
            public void beforeTextChanged(CharSequence s, int start, int count, int after) { }
            public void onTextChanged(CharSequence s, int start, int before, int count) { }
            public void afterTextChanged(android.text.Editable text) { updateSendState(); }
        });
        entry.addView(button("收起", v -> actions.keyboard(false)));
        keyboardPanel.addView(entry);
        compactSwitch = button("按键", v -> {
            compactKeys = !compactKeys; adapt(compact);
            if (!compactKeys) actions.keyboard(true);
        });
        compactClose = button("收起", v -> actions.keyboard(false));
        compactSwitch.setVisibility(View.GONE); compactClose.setVisibility(View.GONE);
        keyboardPanel.addView(compactSwitch, new LinearLayout.LayoutParams(-2, dp(48)));
        keyboardPanel.addView(compactClose, new LinearLayout.LayoutParams(-2, dp(48)));
        dock.addView(keyboardPanel);

        mousePanel = row(); mousePanel.setVisibility(View.GONE);
        weighted(mousePanel, button("左键", v -> actions.mouse(RemoteDeskProtocol.MOUSE_LEFT)));
        weighted(mousePanel, button("右键", v -> actions.mouse(RemoteDeskProtocol.MOUSE_RIGHT)));
        drag = button("拖动锁定", v -> actions.drag()); weighted(mousePanel, drag);
        dock.addView(mousePanel);

        mainRow = row();
        keyboard = button("键盘", v -> actions.keyboard(!keyboardOpen));
        mode = button("触控板", v -> actions.mode());
        mouse = button("鼠标", v -> { setMouseOpen(!mouseOpen); });
        screens = button("屏幕", v -> actions.screens());
        weighted(mainRow, keyboard); weighted(mainRow, mode); weighted(mainRow, mouse);
        weighted(mainRow, screens); weighted(mainRow, button("更多", v -> actions.more()));
        dock.addView(mainRow);
    }

    void keyboard(boolean open) {
        keyboardOpen = open;
        if (!open) compactKeys = false;
        if (open) { mouseOpen = false; mousePanel.setVisibility(View.GONE); }
        keyboardPanel.setVisibility(open ? View.VISIBLE : View.GONE);
        mark(keyboard, open); mark(mouse, mouseOpen); adapt(compact);
    }

    void setMouseOpen(boolean open) {
        if (open && keyboardOpen) actions.keyboard(false);
        mouseOpen = open; mousePanel.setVisibility(open ? View.VISIBLE : View.GONE); mark(mouse, open);
    }

    void controls(boolean enabled) {
        keyboard.setEnabled(enabled); mouse.setEnabled(enabled);
        composer.setEnabled(enabled);
        updateSendState();
        keyboardPanel.setAlpha(enabled ? 1 : 0.45f);
        if (!enabled) { actions.keyboard(false); setMouseOpen(false); }
    }

    void adapt(boolean smallHeight) {
        compact = smallHeight;
        health.setVisibility(smallHeight ? View.GONE : View.VISIBLE);
        hint.setVisibility(smallHeight || keyboardOpen ? View.GONE : View.VISIBLE);
        mainRow.setVisibility(smallHeight && keyboardOpen ? View.GONE : View.VISIBLE);
        // Landscape IMEs can use most of the screen. Keep this dock to one
        // 48dp row, swapping text/shortcuts instead of covering the desktop.
        keyboardPanel.setOrientation(smallHeight ? LinearLayout.HORIZONTAL : LinearLayout.VERTICAL);
        keyboardPanel.setGravity(Gravity.CENTER_VERTICAL);
        shortcutStrip.setVisibility(!smallHeight || compactKeys ? View.VISIBLE : View.GONE);
        entryRow.setVisibility(!smallHeight || !compactKeys ? View.VISIBLE : View.GONE);
        compactSwitch.setVisibility(smallHeight ? View.VISIBLE : View.GONE);
        compactClose.setVisibility(smallHeight && compactKeys ? View.VISIBLE : View.GONE);
        String toggleLabel = compactKeys ? "文字" : "按键";
        if (!toggleLabel.contentEquals(compactSwitch.getText())) compactSwitch.setText(toggleLabel);
        layoutKeyboardRow(shortcutStrip, smallHeight);
        layoutKeyboardRow(entryRow, smallHeight);
    }

    private void layoutKeyboardRow(View view, boolean horizontal) {
        LinearLayout.LayoutParams params = (LinearLayout.LayoutParams) view.getLayoutParams();
        int width = horizontal ? 0 : ViewGroup.LayoutParams.MATCH_PARENT;
        float weight = horizontal ? 1 : 0;
        if (params.width != width || params.height != dp(48) || params.weight != weight) {
            view.setLayoutParams(new LinearLayout.LayoutParams(width, dp(48), weight));
        }
    }

    void mode(boolean trackpad, boolean locked) {
        if (modeInitialized && trackpad == previousTrackpad && locked == previousLocked) return;
        modeInitialized = true; previousTrackpad = trackpad; previousLocked = locked;
        mode.setText(trackpad ? "触控板" : "直接触摸");
        mode.setContentDescription(trackpad ? "当前触控板模式，点击切换直接触摸" : "当前直接触摸模式，点击切换触控板");
        drag.setText(locked ? "结束拖动" : "拖动锁定"); mark(drag, locked);
        hint.setText(locked ? "拖动已锁定 · 移动手指定位 · 点结束拖动释放" :
            trackpad ? "滑动移鼠标 · 轻触点击 · 双指滑动滚动" : "点哪里点哪里 · 单指拖动 · 双指滑动滚动");
    }

    private void updateSendState() { send.setEnabled(composer.isEnabled() && composer.length() > 0); }
    private void sendText() {
        if (composer.isEnabled() && composer.length() > 0 && actions.text(composer.getText().toString())) composer.getText().clear();
    }
    private void shortcut(LinearLayout parent, String label, int... keys) {
        Button button = button(label, v -> actions.shortcut(keys));
        button.setContentDescription("远端按键 " + label); parent.addView(button);
    }
    private LinearLayout column() { LinearLayout v = new LinearLayout(activity); v.setOrientation(LinearLayout.VERTICAL); return v; }
    private LinearLayout row() { LinearLayout v = new LinearLayout(activity); v.setGravity(Gravity.CENTER_VERTICAL); return v; }
    private TextView text(float size, int color) {
        TextView v = new TextView(activity); v.setTextSize(size); v.setTextColor(color);
        v.setEllipsize(TextUtils.TruncateAt.END); return v;
    }
    private Button button(String title, View.OnClickListener listener) {
        Button v = new Button(activity); AndroidUiTheme.styleViewerToolbarButton(activity, v);
        v.setText(title); v.setTextSize(12); v.setMinWidth(0); v.setMinimumWidth(0);
        v.setMinHeight(dp(48)); v.setMinimumHeight(dp(48)); v.setPadding(dp(6), 0, dp(6), 0);
        v.setSingleLine(true); v.setEllipsize(TextUtils.TruncateAt.END);
        v.setContentDescription(title); v.setOnClickListener(listener);
        v.setBackground(AndroidUiTheme.shape(activity, 0xff18283e, 10, 0xff18283e, 0));
        return v;
    }
    private void weighted(LinearLayout row, View button) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(0, dp(48), 1);
        params.setMargins(dp(2), dp(2), dp(2), dp(2)); row.addView(button, params);
    }
    private HorizontalScrollView scroll(View content) {
        HorizontalScrollView view = new HorizontalScrollView(activity);
        view.setHorizontalScrollBarEnabled(false); view.addView(content);
        return view;
    }
    private void mark(Button button, boolean selected) {
        button.setSelected(selected);
        button.setBackgroundTintList(android.content.res.ColorStateList.valueOf(selected ? 0xff235da0 : 0xff18283e));
    }
    private int dp(float value) { return AndroidDisplay.dp(activity, value); }
}
