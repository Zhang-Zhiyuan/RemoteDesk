package com.remotedesk.agent;

import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Rect;
import java.io.ByteArrayOutputStream;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

final class AndroidAccessibilityCapture implements AutoCloseable {
    static final class Frame {
        final AndroidScreenCaptureSession.ScreenFrame encoded;
        final int sourceWidth;
        final int sourceHeight;
        Frame(AndroidScreenCaptureSession.ScreenFrame encoded, int width, int height) {
            this.encoded = encoded;
            sourceWidth = width;
            sourceHeight = height;
        }
    }

    private final AndroidScreenshotGate<Frame> gate = new AndroidScreenshotGate<>();
    // Process-owned callback worker: a late Binder reply must still be accepted
    // after a capture session closes, so its HardwareBuffer can be released.
    private static final ExecutorService executor = Executors.newSingleThreadExecutor();
    private volatile String status = "无障碍兼容模式，等待画面";

    Frame capture(int quality, int maxEdge, int width, int height) {
        Frame completed = gate.poll();
        AndroidScreenshotGate.Request request = gate.begin(System.nanoTime());
        if (request != null) {
            long started = System.nanoTime();
            if (request.recoveredFromTimeout) {
                status = "截图响应超时，正在恢复";
                AndroidSessionLog.info("Accessibility screenshot timed out; retrying with a new request owner.");
                if (completed == null) {
                    try { completed = encodeStatus(status, width, height, quality, maxEdge, started); }
                    catch (RuntimeException ignored) { }
                }
            }
            boolean redactUnlock = RemoteDeskAccessibilityService.isEnteringUnlockPin();
            boolean requested = RemoteDeskAccessibilityService.requestScreenshot(executor,
                new RemoteDeskAccessibilityService.ScreenCallback() {
                    @Override public void onBitmap(Bitmap bitmap) {
                        try {
                            if (gate.isCurrent(request)) {
                                if (redactUnlock || RemoteDeskAccessibilityService.isEnteringUnlockPin()) {
                                    completeStatus(request, "正在解锁手机，请稍候", bitmap.getWidth(), bitmap.getHeight(), quality, maxEdge, started);
                                } else {
                                    if (gate.complete(request, encode(bitmap, quality, maxEdge, started)))
                                        status = "无障碍兼容模式（约 3 FPS）";
                                }
                            }
                        } catch (RuntimeException ex) {
                            if (gate.complete(request, null)) status = "兼容截图暂不可用，正在重试";
                        } finally { bitmap.recycle(); }
                    }
                    @Override public void onFailure(int error) {
                        completeStatus(request, error == 6 ? "此页面受系统保护" : "无障碍截图不可用（" + error + "），正在重试",
                            width, height, quality, maxEdge, started);
                    }
                });
            if (!requested) {
                completeStatus(request, "请启用 RemoteDesk 无障碍服务", width, height, quality, maxEdge, started);
            }
        }
        return completed;
    }

    String status() { return status; }

    private void completeStatus(AndroidScreenshotGate.Request request, String message,
        int width, int height, int quality, int maxEdge, long started) {
        if (!gate.isCurrent(request)) return;
        Frame frame = null;
        try { frame = encodeStatus(message, width, height, quality, maxEdge, started); }
        catch (RuntimeException ignored) { }
        if (gate.complete(request, frame)) status = message;
    }

    private static Frame encodeStatus(String status, int width, int height, int quality, int maxEdge, long started) {
        int[] size = AndroidScreenCaptureSession.fitWithinMaxEdge(Math.max(1, width), Math.max(1, height), maxEdge);
        Bitmap message = Bitmap.createBitmap(size[0], size[1], Bitmap.Config.ARGB_8888);
        try {
            Canvas canvas = new Canvas(message);
            canvas.drawColor(Color.rgb(15, 23, 42));
            Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
            paint.setColor(Color.WHITE);
            paint.setTextSize(Math.max(18, size[0] / 25f));
            canvas.drawText(status, size[0] / 12f, size[1] / 2f, paint);
            return new Frame(encode(message, quality, maxEdge, started).encoded, width, height);
        } finally { message.recycle(); }
    }

    private static Frame encode(Bitmap bitmap, int quality, int maxEdge, long started) {
        int[] size = AndroidScreenCaptureSession.fitWithinMaxEdge(bitmap.getWidth(), bitmap.getHeight(), maxEdge);
        boolean resize = size[0] != bitmap.getWidth() || size[1] != bitmap.getHeight();
        Bitmap output = resize ? Bitmap.createBitmap(size[0], size[1], Bitmap.Config.ARGB_8888) : bitmap;
        try {
            if (resize) new Canvas(output).drawBitmap(bitmap, null, new Rect(0, 0, size[0], size[1]), new Paint(Paint.FILTER_BITMAP_FLAG));
            double captureMillis = (System.nanoTime() - started) / 1_000_000d;
            long encodingAt = System.nanoTime();
            ByteArrayOutputStream stream = new ByteArrayOutputStream(128 * 1024);
            if (!output.compress(Bitmap.CompressFormat.JPEG, Math.max(30, Math.min(90, quality)), stream))
                throw new IllegalStateException("Screenshot encoding failed");
            byte[] bytes = stream.toByteArray();
            return new Frame(new AndroidScreenCaptureSession.ScreenFrame(size[0], size[1], bytes, bytes.length,
                captureMillis, (System.nanoTime() - encodingAt) / 1_000_000d), bitmap.getWidth(), bitmap.getHeight());
        } finally { if (resize) output.recycle(); }
    }

    @Override public void close() {
        gate.close();
    }
}
