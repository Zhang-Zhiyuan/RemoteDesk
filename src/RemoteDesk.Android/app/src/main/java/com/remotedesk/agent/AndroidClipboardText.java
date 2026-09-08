package com.remotedesk.agent;

import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.os.Handler;
import android.os.Looper;

import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

final class AndroidClipboardText {
    private static final int MAX_TEXT_CHARS = 256_000;
    private static final long MAIN_THREAD_TIMEOUT_MS = 1500;

    private AndroidClipboardText() {
    }

    static String getText(Context context) throws Exception {
        return runOnMainThread(() -> {
            ClipboardManager manager = clipboardManager(context);
            if (manager == null || !manager.hasPrimaryClip()) {
                return "";
            }

            ClipData clip = manager.getPrimaryClip();
            if (clip == null || clip.getItemCount() == 0) {
                return "";
            }

            CharSequence text = clip.getItemAt(0).coerceToText(context);
            return boundText(text == null ? "" : text.toString());
        });
    }

    static void setText(Context context, String text) throws Exception {
        String bounded = boundText(text == null ? "" : text);
        runOnMainThread(() -> {
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

    private static String boundText(String text) {
        return text.length() <= MAX_TEXT_CHARS ? text : text.substring(0, MAX_TEXT_CHARS);
    }

    private static <T> T runOnMainThread(ThrowingSupplier<T> supplier) throws Exception {
        if (Looper.myLooper() == Looper.getMainLooper()) {
            return supplier.get();
        }

        CountDownLatch latch = new CountDownLatch(1);
        AtomicReference<T> result = new AtomicReference<>();
        AtomicReference<Exception> failure = new AtomicReference<>();
        new Handler(Looper.getMainLooper()).post(() -> {
            try {
                result.set(supplier.get());
            } catch (Exception ex) {
                failure.set(ex);
            } finally {
                latch.countDown();
            }
        });

        if (!latch.await(MAIN_THREAD_TIMEOUT_MS, TimeUnit.MILLISECONDS)) {
            throw new IllegalStateException("Android clipboard operation timed out.");
        }

        if (failure.get() != null) {
            throw failure.get();
        }

        return result.get();
    }

    private interface ThrowingSupplier<T> {
        T get() throws Exception;
    }
}
