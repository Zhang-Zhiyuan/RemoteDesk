package com.remotedesk.agent;

import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.os.Handler;
import android.os.Looper;

import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.function.BooleanSupplier;

final class AndroidClipboardText {
    private static final int MAX_TEXT_CHARS = 256_000;
    private static final long MAIN_THREAD_TIMEOUT_MS = 1500;

    private AndroidClipboardText() {
    }

    static String getText(Context context) throws Exception {
        return runOnMainThread(() -> {
            ClipboardManager manager = clipboardManager(context);
            if (manager == null || !manager.hasPrimaryClip()) {
                throw new IllegalStateException("剪贴板没有可读取的文字，或系统限制后台读取；请让 RemoteDesk 位于手机前台后重试。本机剪贴板不会被清空。");
            }

            ClipData clip = manager.getPrimaryClip();
            if (clip == null || clip.getItemCount() == 0) {
                throw new IllegalStateException("系统未提供剪贴板内容，请让 RemoteDesk 位于手机前台后重试。");
            }

            // Do not resolve content:// URIs or turn a copied file into arbitrary text.
            CharSequence text = clip.getItemAt(0).getText();
            return boundText(text == null ? "" : text.toString());
        });
    }

    static void setText(Context context, String text) throws Exception {
        setText(context, text, () -> true);
    }

    static void setText(Context context, String text, BooleanSupplier isCurrent) throws Exception {
        String bounded = boundText(text == null ? "" : text);
        runOnMainThread(() -> {
            if (!isCurrent.getAsBoolean()) throw new IllegalStateException("剪贴板请求已失效，内容未修改。");
            ClipboardManager manager = clipboardManager(context);
            if (manager == null) {
                throw new IllegalStateException("Android clipboard service is unavailable.");
            }

            manager.setPrimaryClip(ClipData.newPlainText("RemoteDesk", bounded));
            return null;
        });
    }

    private static ClipboardManager clipboardManager(Context context) {
        return (ClipboardManager) context.getSystemService(Context.CLIPBOARD_SERVICE);
    }

    static String boundText(String text) {
        if (text.length() > MAX_TEXT_CHARS)
            throw new IllegalArgumentException("剪贴板文字超过 256000 字符，未截断或发送，请分段复制。");
        return text;
    }

    private static <T> T runOnMainThread(ThrowingSupplier<T> supplier) throws Exception {
        if (Looper.myLooper() == Looper.getMainLooper()) {
            return supplier.get();
        }

        CountDownLatch latch = new CountDownLatch(1);
        AtomicReference<T> result = new AtomicReference<>();
        AtomicReference<Exception> failure = new AtomicReference<>();
        AtomicBoolean active = new AtomicBoolean(true);
        new Handler(Looper.getMainLooper()).post(() -> {
            try {
                if (active.get()) result.set(supplier.get());
            } catch (Exception ex) {
                failure.set(ex);
            } finally {
                latch.countDown();
            }
        });

        try {
            if (!latch.await(MAIN_THREAD_TIMEOUT_MS, TimeUnit.MILLISECONDS))
                throw new IllegalStateException("Android clipboard operation timed out.");
        } finally { active.set(false); }

        if (failure.get() != null) {
            throw failure.get();
        }

        return result.get();
    }

    private interface ThrowingSupplier<T> {
        T get() throws Exception;
    }
}
