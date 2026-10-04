package com.remotedesk.agent;

import android.app.Activity;
import android.content.res.Configuration;
import android.content.res.Resources;
import android.graphics.Rect;
import android.os.Bundle;
import android.text.Layout;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import java.io.File;
import java.io.FileOutputStream;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.util.List;
import org.json.JSONObject;

/** Actual product rows with in-memory fixtures; never activates discovery or reads saved history. */
public final class DeviceRowsProbeActivity extends Activity {
    private static final String NAME = "会议室主控电脑与图形设计工作站";
    private static final String HOST = "2001:db8:1234:5678:abcd:ef01:2345:6789";
    private Resources probeResources;
    private AndroidLanDiscoveryPanel lan;
    private AndroidConnectionHistoryPanel history;
    private Button entry;
    private final JSONObject result = new JSONObject();
    private String selectedAddress;

    @Override public Resources getResources() {
        return probeResources == null ? super.getResources() : probeResources;
    }

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        Configuration configuration = new Configuration(super.getResources().getConfiguration());
        configuration.fontScale = getIntent().getFloatExtra("fontScale", 2f);
        probeResources = createConfigurationContext(configuration).getResources();
        try {
            String mode = getIntent().getStringExtra("mode");
            boolean legacy = getIntent().getBooleanExtra("legacy", false);
            int widthDp = getIntent().getIntExtra("width", 320);
            result.put("runId", getIntent().getStringExtra("runId"));
            result.put("mode", mode).put("legacyThreeLineLimit", legacy);
            result.put("widthDp", widthDp).put("fontScale", configuration.fontScale);
            result.put("systemFontScale", super.getResources().getConfiguration().fontScale);

            LinearLayout content = new LinearLayout(this);
            content.setOrientation(LinearLayout.VERTICAL);
            content.setPadding(dp(16), dp(16), dp(16), dp(16));
            content.addView(AndroidUiTheme.createSectionTitle(this,
                "history".equals(mode) ? "最近连接 · 合成测试" : "附近设备 · 合成测试"));
            content.addView(AndroidUiTheme.createSectionSubtitle(this,
                widthDp + " dp · " + Math.round(configuration.fontScale * 100) + "% 字体"));
            LinearLayout card = AndroidUiTheme.createCard(this);
            content.addView(card, new LinearLayout.LayoutParams(-1, -2));
            if ("history".equals(mode)) {
                history = new AndroidConnectionHistoryPanel(this,
                    node -> selectedAddress = node.address(), node -> {}, status -> {});
                AndroidConnectionHistory.Node node = new AndroidConnectionHistory.Node(
                    "00000000-0000-0000-0000-000000000001", HOST, 56565, "",
                    "public-fixture-token", "", NAME, "", 1);
                invoke(history, "render", new Class<?>[] { List.class }, List.of(node));
                card.addView(history);
                entry = (Button) ((LinearLayout) history.getChildAt(1)).getChildAt(0);
            } else {
                lan = new AndroidLanDiscoveryPanel(this, () -> "",
                    device -> selectedAddress = device.address());
                Field status = AndroidLanDiscoveryPanel.class.getDeclaredField("status");
                status.setAccessible(true);
                ((android.widget.TextView) status.get(lan)).setText("合成数据；未启动网络发现");
                card.addView(lan);
                List<AndroidLanDevice> fixtures = List.of(device(56565));
                renderLan(fixtures);
                Button original = lanEntry();
                int replacements = 0;
                for (int i = 0; i < 100; i++) {
                    renderLan(List.of(device(56565)));
                    if (lanEntry() != original) replacements++;
                }
                result.put("identicalRefreshes", 100).put("rowReplacements", replacements);
                renderLan(List.of(device(56566)));
                result.put("changedRowReplaced", lanEntry() != original);
                lanEntry().performClick();
                result.put("changedActionAddress", selectedAddress);
                if (replacements != 0 || lanEntry() == original ||
                    !device(56566).address().equals(selectedAddress)) {
                    throw new AssertionError("Discovery refresh retained stale rows or actions");
                }
                renderLan(fixtures);
                entry = lanEntry();
            }
            entry.performClick();
            result.put("selectedAddress", selectedAddress);
            if (!device(56565).address().equals(selectedAddress)) {
                throw new AssertionError("Fixture row did not invoke its connection action");
            }
            if (legacy) {
                // Labelled negative control: reinstate the exact prior row limits.
                entry.setMaxLines(3);
                entry.setEllipsize(TextUtils.TruncateAt.END);
                entry.setPadding(dp(12), dp(8), dp(12), dp(8));
            }
            ScrollView scroll = new ScrollView(this);
            scroll.addView(content);
            FrameLayout root = new FrameLayout(this);
            root.addView(scroll, new FrameLayout.LayoutParams(dp(widthDp), -1, Gravity.TOP | Gravity.CENTER_HORIZONTAL));
            setContentView(root);
            entry.postDelayed(this::verifyLayout, 500);
        } catch (Throwable error) { fail(error); }
    }

    private AndroidLanDevice device(int port) {
        return new AndroidLanDevice(HOST, port, NAME, "Windows", true, true);
    }

    private void renderLan(List<AndroidLanDevice> devices) throws Exception {
        Field nearby = AndroidLanDiscoveryPanel.class.getDeclaredField("nearby");
        nearby.setAccessible(true);
        nearby.set(lan, devices);
        invoke(lan, "render", new Class<?>[0]);
    }

    private Button lanEntry() throws Exception {
        Field devices = AndroidLanDiscoveryPanel.class.getDeclaredField("devices");
        devices.setAccessible(true);
        return (Button) ((LinearLayout) devices.get(lan)).getChildAt(0);
    }

    private static void invoke(Object target, String name, Class<?>[] types, Object... arguments) throws Exception {
        Method method = target.getClass().getDeclaredMethod(name, types);
        method.setAccessible(true);
        method.invoke(target, arguments);
    }

    private void verifyLayout() {
        try {
            Layout layout = entry.getLayout();
            int hidden = 0;
            for (int line = 0; line < layout.getLineCount(); line++) hidden += layout.getEllipsisCount(line);
            Rect visible = new Rect();
            boolean fullyVisible = entry.getGlobalVisibleRect(visible) &&
                visible.width() == entry.getWidth() && visible.height() == entry.getHeight();
            int contentHeight = entry.getHeight() - entry.getCompoundPaddingTop() - entry.getCompoundPaddingBottom();
            boolean fitsHeight = layout.getHeight() <= contentHeight;
            result.put("lineCount", layout.getLineCount()).put("ellipsisCharacters", hidden);
            result.put("entryWidthPx", entry.getWidth()).put("entryHeightPx", entry.getHeight());
            result.put("textHeightPx", layout.getHeight()).put("contentHeightPx", contentHeight);
            result.put("fullyVisible", fullyVisible).put("textFitsHeight", fitsHeight);
            result.put("text", entry.getText()).put("textSizePx", entry.getTextSize());
            result.put("passed", hidden == 0 && fitsHeight && fullyVisible);
            save();
        } catch (Throwable error) { fail(error); }
    }

    private void fail(Throwable error) {
        try {
            result.put("passed", false).put("error", error.toString());
            save();
        } catch (Exception nested) { throw new RuntimeException(nested); }
    }

    private void save() throws Exception {
        try (FileOutputStream output = new FileOutputStream(new File(getFilesDir(), "device-rows-probe.json"))) {
            output.write(result.toString(2).getBytes(StandardCharsets.UTF_8));
        }
    }

    private int dp(int value) { return AndroidDisplay.dp(this, value); }

    @Override protected void onDestroy() {
        if (lan != null) lan.close();
        if (history != null) history.close();
        super.onDestroy();
    }
}
