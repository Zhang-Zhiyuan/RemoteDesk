package com.remotedesk.agent;

import android.app.Activity;
import android.content.Intent;
import android.graphics.Rect;
import android.os.Handler;
import android.os.Looper;
import android.text.Layout;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowInsets;
import android.view.inspector.WindowInspector;
import android.widget.EditText;
import android.widget.TextView;
import java.io.File;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.List;
import org.json.JSONArray;
import org.json.JSONObject;

/** Observe the actual Activity/window resources: never overrides density or font configuration. */
final class DpiUiProbe {
    private static final String NAME = "会议室主控电脑与图形设计工作站";
    private static final String HOST = "2001:db8:1234:5678:abcd:ef01:2345:6789";
    private static final Handler HANDLER = new Handler(Looper.getMainLooper());
    static Activity current;

    static void resume(Activity activity) {
        if (!activity.getIntent().getBooleanExtra("dpiProbe", false)) return;
        current = activity;
        if (activity instanceof MainActivity) {
            HANDLER.post(() -> {
                try {
                    // Only this isolated APK's UI fixture. Do not discover or connect real peers.
                    ((AndroidLanDiscoveryPanel) ViewerProbeApplication.field(activity, "lanPanel")).active(false);
                    ((AndroidRelayPanel) ViewerProbeApplication.field(activity, "relayPanel")).active(false);
                    new Thread(MainActivity::stopDiscoveryPreviewAndWait, "DpiOwnedDiscoveryStop").start();
                } catch (Exception failure) { throw new IllegalStateException(failure); }
            });
            HANDLER.postDelayed(() -> {
                if (current != activity || activity.isDestroyed()) return;
                try {
                    Object lan = ViewerProbeApplication.field(activity, "lanPanel");
                    java.lang.reflect.Field nearby = lan.getClass().getDeclaredField("nearby");
                    nearby.setAccessible(true); nearby.set(lan, List.of(new AndroidLanDevice(HOST, 56565, NAME, "Windows", true, true)));
                    invoke(lan, "render", new Class<?>[0]);
                    Object history = ViewerProbeApplication.field(activity, "historyPanel");
                    AndroidConnectionHistory.Node node = new AndroidConnectionHistory.Node(
                        "00000000-0000-0000-0000-0000000000d4", HOST, 56565, "", "public-fixture-token", "", NAME, "", 1);
                    invoke(history, "render", new Class<?>[] {List.class}, List.of(node));
                    ((EditText) ViewerProbeApplication.field(activity, "viewerAddressEdit")).setText("[" + HOST + "]:56565");
                    ((EditText) ViewerProbeApplication.field(activity, "viewerPasswordEdit")).setText("");
                    snapshot("ready");
                } catch (Exception failure) { throw new IllegalStateException(failure); }
            }, 1000);
        } else HANDLER.postDelayed(() -> snapshot("ready"), 1000);
    }

    static void command(Intent intent) throws Exception {
        if (current == null || current.isDestroyed()) throw new IllegalStateException("No owned DPI Activity");
        String action = intent.getStringExtra("action");
        if ("direct-key".equals(action)) {
            invoke(current, "openDiscoveredViewer", new Class<?>[] {AndroidLanDevice.class},
                new AndroidLanDevice(HOST, 56566, "新设备 · " + NAME, "Windows", true, true, "dpi-synthetic-device"));
        } else if ("relay-key".equals(action)) {
            AndroidRelay.Options target = new AndroidRelay.Options("192.0.2.1", 56567, "public-fixture-token-for-owned-dpi-tests",
                "A".repeat(64), "00000000-0000-0000-0000-0000000000d5", false);
            invoke(current, "openRelayViewer", new Class<?>[] {AndroidRelay.Options.class, String.class, boolean.class}, target, NAME, true);
        } else if ("relay-name".equals(action)) {
            AndroidRelayPanel panel = (AndroidRelayPanel) ViewerProbeApplication.field(current, "relayPanel");
            panel.active(false); // A previous Devices-tab click may have restarted its ticker.
            AndroidRelay.Options target = new AndroidRelay.Options("192.0.2.1", 56567, "public-fixture-token-for-owned-dpi-tests",
                "A".repeat(64), "00000000-0000-0000-0000-0000000000d5", false);
            // Change only the test panel's in-memory eligibility; never start a relay ticker or save settings.
            java.lang.reflect.Field saved = panel.getClass().getDeclaredField("saved"); saved.setAccessible(true); saved.set(panel, target);
            java.lang.reflect.Field active = panel.getClass().getDeclaredField("active"); active.setAccessible(true); active.set(panel, true);
            invoke(panel, "showNameDialog", new Class<?>[] {AndroidRelay.Options.class, AndroidRelay.Device.class}, target,
                new AndroidRelay.Device(target.deviceId, NAME, "Windows", false, List.of(), 0, "", NAME, true, ""));
        } else if ("reveal".equals(action) || "focus".equals(action) || "activate".equals(action)) {
            List<View> windows = WindowInspector.getGlobalWindowViews();
            View root = windows.get(windows.size() - 1);
            View match = find(root, intent.getStringExtra("text"));
            if (match == null) throw new IllegalArgumentException("Missing test view");
            match.requestRectangleOnScreen(new Rect(0, 0, match.getWidth(), match.getHeight()), true);
            if ("activate".equals(action) && (!match.isShown() || !match.isEnabled() || !match.performClick()))
                throw new IllegalStateException("Visible fixture control could not be activated");
            if ("focus".equals(action)) {
                match.requestFocus();
                match.postDelayed(() -> {
                    boolean shown = ((android.view.inputmethod.InputMethodManager) current.getSystemService(Activity.INPUT_METHOD_SERVICE))
                        .showSoftInput(match, android.view.inputmethod.InputMethodManager.SHOW_IMPLICIT);
                    android.util.Log.i("DpiOwnedProbe", "IME request=" + shown + " windowFocus=" + match.hasWindowFocus() + " focused=" + match.isFocused());
                }, 150);
            }
        }
        HANDLER.postDelayed(() -> snapshot(intent.getStringExtra("runId")), 600);
    }

    private static View find(View view, String text) {
        if (view.getVisibility() != View.VISIBLE) return null;
        if (text != null && (text.contentEquals(view.getContentDescription() == null ? "" : view.getContentDescription())
            || view instanceof TextView label && (text.contentEquals(label.getText()) || text.contentEquals(label.getHint() == null ? "" : label.getHint())))) return view;
        if (view instanceof ViewGroup group) for (int i = 0; i < group.getChildCount(); i++) {
            View match = find(group.getChildAt(i), text); if (match != null) return match;
        }
        return null;
    }

    private static Object invoke(Object target, String name, Class<?>[] types, Object... args) throws Exception {
        Method method = target.getClass().getDeclaredMethod(name, types); method.setAccessible(true);
        return method.invoke(target, args);
    }

    static void snapshot(String runId) {
        Activity activity = current;
        if (activity == null || activity.isDestroyed()) return;
        JSONObject report = new JSONObject();
        try {
            var resources = activity.getResources();
            report.put("runId", runId).put("activity", activity.getClass().getSimpleName())
                .put("configuration", resources.getConfiguration().toString())
                .put("densityDpi", resources.getDisplayMetrics().densityDpi)
                .put("density", resources.getDisplayMetrics().density)
                .put("fontScale", resources.getConfiguration().fontScale);
            JSONArray windows = new JSONArray();
            for (View root : WindowInspector.getGlobalWindowViews()) {
                JSONArray views = new JSONArray();
                collect(root, views);
                JSONObject window = new JSONObject().put("bounds", ViewerProbeApplication.bounds(root)).put("views", views);
                WindowInsets insets = root.getRootWindowInsets();
                if (insets != null) {
                    var bars = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout());
                    var ime = insets.getInsets(WindowInsets.Type.ime());
                    window.put("safeInsets", new JSONArray().put(bars.left).put(bars.top).put(bars.right).put(bars.bottom))
                        .put("imeInsets", new JSONArray().put(ime.left).put(ime.top).put(ime.right).put(ime.bottom));
                }
                windows.put(window);
            }
            report.put("windows", windows);
        } catch (Exception failure) {
            try { report.put("failure", failure.toString()); } catch (Exception ignored) { }
        }
        try {
            File temporary = new File(activity.getFilesDir(), "dpi-ui.json.tmp");
            Files.write(temporary.toPath(), report.toString(2).getBytes(StandardCharsets.UTF_8));
            Files.move(temporary.toPath(), new File(activity.getFilesDir(), "dpi-ui.json").toPath(),
                java.nio.file.StandardCopyOption.ATOMIC_MOVE, java.nio.file.StandardCopyOption.REPLACE_EXISTING);
        } catch (Exception failure) { throw new IllegalStateException(failure); }
    }

    private static void collect(View view, JSONArray results) throws Exception {
        if (view.getVisibility() != View.VISIBLE) return;
        if (view instanceof TextView text && text.getLayout() != null) {
            Layout layout = text.getLayout();
            Rect visible = new Rect();
            boolean onScreen = text.getLocalVisibleRect(visible);
            int[] screen = new int[2];
            text.getLocationOnScreen(screen);
            visible.offset(screen[0], screen[1]);
            int ellipsis = 0;
            for (int i = 0; i < layout.getLineCount(); i++) ellipsis += layout.getEllipsisCount(i);
            results.put(new JSONObject().put("class", view.getClass().getSimpleName())
                .put("text", text instanceof EditText ? "<input>" : text.getText())
                .put("hint", text.getHint()).put("description", view.getContentDescription())
                .put("error", text.getError())
                .put("bounds", ViewerProbeApplication.bounds(view)).put("visibleRect", new JSONArray().put(visible.left).put(visible.top).put(visible.right).put(visible.bottom))
                .put("onScreen", onScreen).put("clickable", view.isClickable()).put("focused", view.isFocused())
                .put("lineCount", layout.getLineCount()).put("ellipsis", ellipsis)
                .put("textHeight", layout.getHeight()).put("contentHeight", text.getHeight()-text.getCompoundPaddingTop()-text.getCompoundPaddingBottom()));
        }
        if (view instanceof ViewGroup group) for (int i = 0; i < group.getChildCount(); i++) collect(group.getChildAt(i), results);
    }
}
