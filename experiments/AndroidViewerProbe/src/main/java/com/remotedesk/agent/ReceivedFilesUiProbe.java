package com.remotedesk.agent;

import android.app.AlertDialog;
import android.content.ContentValues;
import android.net.Uri;
import android.os.CancellationSignal;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.provider.MediaStore;
import java.io.File;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.List;
import java.util.UUID;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.TimeUnit;
import org.json.JSONArray;
import org.json.JSONObject;

/** Isolated probe APK only. Reads its own generated MediaStore rows; no production data. */
final class ReceivedFilesUiProbe {
    private final MainActivity activity;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final JSONArray checks = new JSONArray();
    private final List<Uri> owned = new ArrayList<>();
    private String fixturePath;
    private Throwable probeFailure;
    private boolean baselinePendingVisible;
    private ReceivedFilesUiProbe(MainActivity activity) { this.activity = activity; }
    static void start(MainActivity activity) {
        ReceivedFilesUiProbe probe = new ReceivedFilesUiProbe(activity);
        new Thread(probe::run, "received-files-probe").start();
    }
    private Object call(String name, Class<?>[] parameters, Object... args) throws Exception {
        Method method = MainActivity.class.getDeclaredMethod(name, parameters);
        method.setAccessible(true);
        return method.invoke(activity, args);
    }
    private synchronized void check(String name, boolean passed) throws Exception {
        checks.put(new JSONObject().put("name", name).put("passed", passed));
        if (!passed) throw new AssertionError(name);
    }
    private Uri create(String name, boolean published) throws Exception {
        ContentValues values = new ContentValues();
        values.put(MediaStore.MediaColumns.DISPLAY_NAME, name);
        values.put(MediaStore.MediaColumns.RELATIVE_PATH, "Download/RemoteDeskReceived/");
        values.put(MediaStore.MediaColumns.MIME_TYPE, "text/plain");
        values.put(MediaStore.MediaColumns.IS_PENDING, 1);
        Uri uri = activity.getContentResolver().insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values);
        if (uri == null) throw new IllegalStateException("Could not create isolated fixture");
        owned.add(uri);
        try (var stream = activity.getContentResolver().openOutputStream(uri)) {
            stream.write("Synthetic received-file fixture 中文😀".getBytes(StandardCharsets.UTF_8));
        }
        if (published) {
            values.clear(); values.put(MediaStore.MediaColumns.IS_PENDING, 0);
            activity.getContentResolver().update(uri, values, null, null);
        }
        return uri;
    }
    private void run() {
        CountDownLatch unblock = new CountDownLatch(1);
        try {
            String prefix = "rd-audit-" + UUID.randomUUID().toString().substring(0, 8);
            String visible = prefix + "-已保存-中文😀.txt", pending = prefix + "-正在传输.txt";
            create(visible, true); create(pending, false);
            boolean oldQueryExposesPending = false;
            try (var cursor = activity.getContentResolver().query(MediaStore.Downloads.EXTERNAL_CONTENT_URI,
                    new String[] { MediaStore.MediaColumns.DISPLAY_NAME }, "relative_path=?",
                    new String[] { "Download/RemoteDeskReceived/" }, null)) {
                while (cursor != null && cursor.moveToNext()) if (pending.equals(cursor.getString(0))) oldQueryExposesPending = true;
            }
            // Providers differ: MuMu's MediaStore already excludes pending rows by default.
            // Record this baseline honestly; the explicit IS_PENDING filter is defensive.
            baselinePendingVisible = oldQueryExposesPending;
            Object listing = call("listReceivedFiles", new Class<?>[] { CancellationSignal.class }, new CancellationSignal());
            List<?> files = (List<?>) ViewerProbeApplication.field(listing, "files");
            boolean found = false, premature = false;
            for (Object file : files) {
                String name = (String) ViewerProbeApplication.field(file, "name");
                if (visible.equals(name)) { found = true; fixturePath = (String) ViewerProbeApplication.field(file, "location"); }
                if (pending.equals(name)) premature = true;
            }
            check("published file listed and in-progress MediaStore item excluded", found && !premature);
            check("location includes actual complete filename", fixturePath != null && fixturePath.endsWith(visible));
            check("successful query has no false warning", "".equals(ViewerProbeApplication.field(listing, "warning")));
            ExecutorService worker = (ExecutorService) ViewerProbeApplication.field(activity, "receivedFilesExecutor");
            CountDownLatch blocked = new CountDownLatch(1), uiAlive = new CountDownLatch(1);
            worker.execute(() -> { blocked.countDown(); try { unblock.await(3, TimeUnit.SECONDS); } catch (InterruptedException ex) { Thread.currentThread().interrupt(); } });
            if (!blocked.await(2, TimeUnit.SECONDS)) throw new AssertionError("Worker did not start");
            main.post(() -> {
                try {
                    long started = SystemClock.uptimeMillis();
                    call("showReceivedFiles", new Class<?>[0]);
                    main.postDelayed(() -> {
                        try {
                            AlertDialog dialog = (AlertDialog) ViewerProbeApplication.field(activity, "receivedFilesLoadingDialog");
                            check("loading dialog and UI heartbeat remain responsive during storage delay", dialog != null && dialog.isShowing() &&
                                SystemClock.uptimeMillis() - started < 600);
                            dialog.cancel();
                        } catch (Throwable failure) { write(failure); }
                        finally { uiAlive.countDown(); }
                    }, 80);
                } catch (Throwable failure) { write(failure); uiAlive.countDown(); }
            });
            if (!uiAlive.await(2, TimeUnit.SECONDS)) throw new AssertionError("UI blocked by storage query");
            unblock.countDown();
            CountDownLatch drained = new CountDownLatch(1);
            worker.execute(drained::countDown);
            if (!drained.await(3, TimeUnit.SECONDS)) throw new AssertionError("Storage query did not finish");
            CountDownLatch finished = new CountDownLatch(1);
            main.post(() -> {
                try {
                    check("cancelled query does not leave a loading dialog", ViewerProbeApplication.field(activity, "receivedFilesLoadingDialog") == null);
                    call("showReceivedFileDetails", new Class<?>[] { String.class, String.class }, "文件位置（测试）",
                        "合成文件，测试后清理。\n文件实际保存位置：\n" + fixturePath + "\n\n此路径可以长按选中复制。");
                } catch (Throwable failure) { write(failure); }
                finally { finished.countDown(); }
            });
            if (!finished.await(2, TimeUnit.SECONDS)) throw new AssertionError("UI result did not render");
            write(null);
        } catch (Throwable failure) { write(failure); }
        finally {
            unblock.countDown();
            for (Uri uri : owned) { try { activity.getContentResolver().delete(uri, null, null); } catch (Exception ignored) {} }
        }
    }
    private synchronized void write(Throwable failure) {
        try {
            if (failure != null) probeFailure = failure;
            boolean passed = probeFailure == null;
            for (int i = 0; i < checks.length(); i++) passed &= checks.getJSONObject(i).getBoolean("passed");
            JSONObject result = new JSONObject().put("checks", checks).put("passed", passed)
                .put("baselineDirectoryQueryIncludedPending", baselinePendingVisible)
                .put("scope", "MuMu Android 15 isolated APK; product file-list UI and MediaStore, not a physical phone");
            if (probeFailure != null) result.put("failure", probeFailure.toString());
            Files.write(new File(activity.getFilesDir(), "received-files-probe.json").toPath(), result.toString(2).getBytes(StandardCharsets.UTF_8));
        } catch (Exception ignored) {}
    }
}
