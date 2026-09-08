package com.remotedesk.agent;

import android.content.Context;
import android.content.Intent;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.PixelFormat;
import android.graphics.Rect;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.Image;
import android.media.ImageReader;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.Looper;
import android.util.DisplayMetrics;
import android.view.Surface;
import android.view.WindowManager;
import android.view.WindowMetrics;

import java.io.ByteArrayOutputStream;
import java.nio.ByteBuffer;

final class AndroidScreenCaptureSession {
    private static final AndroidScreenCaptureSession INSTANCE = new AndroidScreenCaptureSession();
    private static final int MAX_VIRTUAL_DISPLAY_EDGE = 1600;
    private static final int IMAGE_READER_MAX_IMAGES = 2;
    private static final long DISPLAY_CONFIGURATION_CHECK_NANOS = 500_000_000L;

    private int resultCode;
    private Intent resultData;
    private volatile AndroidAccessibilityCapture accessibilityCapture;
    private MediaProjection mediaProjection;
    private MediaProjection.Callback mediaProjectionCallback;
    private ProjectionStoppedListener projectionStoppedListener;
    private ImageReader imageReader;
    private VirtualDisplay virtualDisplay;
    private Surface activeVideoSurface;
    private final AndroidVirtualDisplayLifecycle virtualDisplayLifecycle =
        new AndroidVirtualDisplayLifecycle();
    private final AndroidJpegRecoveryPolicy jpegRecoveryPolicy =
        new AndroidJpegRecoveryPolicy();
    private final AndroidCaptureGeometryPolicy captureGeometryPolicy =
        new AndroidCaptureGeometryPolicy();
    private HandlerThread imageThread;
    private Handler imageHandler;
    private volatile int sourceWidth;
    private volatile int sourceHeight;
    private volatile long displayConfigurationGeneration;
    private int sourceRotation;
    private int width;
    private int height;
    private int densityDpi;
    private long lastDisplayConfigurationCheckAt;
    private Bitmap captureBitmap;
    private Bitmap croppedBitmap;
    private Bitmap scaledBitmap;
    private Canvas croppedCanvas;
    private Canvas scaledCanvas;
    private final Paint scalePaint = new Paint(Paint.FILTER_BITMAP_FLAG);
    private final Rect sourceRect = new Rect();
    private final Rect destinationRect = new Rect();
    private ReusableByteArrayOutputStream jpegOutput = new ReusableByteArrayOutputStream(256 * 1024);

    private AndroidScreenCaptureSession() {
    }

    static AndroidScreenCaptureSession getInstance() {
        return INSTANCE;
    }

    synchronized void setProjectionGrant(int code, Intent data) {
        resultCode = code;
        resultData = data;
    }

    synchronized boolean hasProjectionGrant() {
        return resultCode != 0 && resultData != null;
    }

    int getSourceWidth() {
        return sourceWidth;
    }

    int getSourceHeight() {
        return sourceHeight;
    }

    long getDisplayConfigurationGeneration() {
        return displayConfigurationGeneration;
    }

    boolean isAccessibilityCapture() { return accessibilityCapture != null; }

    synchronized boolean startAccessibility(Context context) {
        if (Build.VERSION.SDK_INT < 30 || !RemoteDeskAccessibilityService.canCaptureScreen()) return false;
        if (accessibilityCapture != null) return true;
        clearInternal(true);
        DisplayMetrics metrics = resolveDisplayMetrics(context);
        sourceWidth = Math.max(1, metrics.widthPixels);
        sourceHeight = Math.max(1, metrics.heightPixels);
        width = sourceWidth;
        height = sourceHeight;
        accessibilityCapture = new AndroidAccessibilityCapture();
        displayConfigurationGeneration++;
        return true;
    }

    String captureStatus() {
        AndroidAccessibilityCapture capture = accessibilityCapture;
        return capture == null ? "屏幕录制" : capture.status();
    }

    synchronized boolean start(Context context) {
        return start(context, null);
    }

    synchronized boolean start(
        Context context,
        ProjectionStoppedListener stoppedListener) {
        if (mediaProjection != null) {
            // A repeated service start is idempotent even while H.264 owns the
            // output Surface (and therefore no JPEG ImageReader is present).
            if (stoppedListener != null) {
                projectionStoppedListener = stoppedListener;
            }
            return virtualDisplay != null;
        }
        if (!hasProjectionGrant()) {
            return false;
        }

        MediaProjectionManager manager =
            (MediaProjectionManager) context.getSystemService(Context.MEDIA_PROJECTION_SERVICE);
        if (manager == null) {
            return false;
        }

        DisplayMetrics metrics = resolveDisplayMetrics(context);
        sourceWidth = Math.max(1, metrics.widthPixels);
        sourceHeight = Math.max(1, metrics.heightPixels);
        densityDpi = metrics.densityDpi;
        sourceRotation = resolveDisplayRotation(context);
        displayConfigurationGeneration++;
        captureGeometryPolicy.reset();
        int[] streamSize = captureGeometryPolicy.fitWithinMaxEdge(
            sourceWidth,
            sourceHeight,
            MAX_VIRTUAL_DISPLAY_EDGE);
        width = streamSize[0];
        height = streamSize[1];

        imageThread = new HandlerThread("RemoteDeskCapture");
        imageThread.start();
        imageHandler = new Handler(imageThread.getLooper());
        MediaProjection projection;
        try {
            projection = manager.getMediaProjection(resultCode, resultData);
        } catch (RuntimeException ex) {
            AndroidSessionLog.error("Could not create the MediaProjection session.", ex);
            clearInternal(false);
            return false;
        }
        if (projection == null) {
            clearInternal(false);
            return false;
        }

        mediaProjection = projection;
        projectionStoppedListener = stoppedListener;
        long projectionGeneration = virtualDisplayLifecycle.beginProjection();
        MediaProjection.Callback callback = new MediaProjection.Callback() {
            @Override
            public void onCapturedContentResize(int width, int height) {
                handleCapturedContentResize(
                    projection,
                    projectionGeneration,
                    width,
                    height);
            }

            @Override
            public void onStop() {
                handleProjectionStopped(projection, projectionGeneration);
            }
        };
        mediaProjectionCallback = callback;

        try {
            projection.registerCallback(callback, new Handler(Looper.getMainLooper()));
            if (ensureJpegVirtualDisplay()) {
                return true;
            }
            AndroidSessionLog.info(
                "MediaProjection did not create its initial JPEG output; rolling back the grant.");
        } catch (RuntimeException ex) {
            AndroidSessionLog.error("Could not initialize the MediaProjection output.", ex);
        }

        // Android 14 grants are single-use. A partially initialized projection
        // cannot be retained for a later retry; release it transactionally and
        // require fresh user consent.
        clearInternal(true);
        return false;
    }

    synchronized int getStreamWidth() {
        return width;
    }

    synchronized int getStreamHeight() {
        return height;
    }

    synchronized void configureEncoderAlignment(
        int widthAlignment,
        int heightAlignment) {
        captureGeometryPolicy.configureEncoderAlignment(
            widthAlignment,
            heightAlignment);
        if (sourceWidth <= 0 || sourceHeight <= 0) {
            return;
        }

        int[] streamSize = captureGeometryPolicy.fitWithinMaxEdge(
            sourceWidth,
            sourceHeight,
            MAX_VIRTUAL_DISPLAY_EDGE);
        if (width == streamSize[0] && height == streamSize[1]) {
            return;
        }

        width = streamSize[0];
        height = streamSize[1];
        recycleCaptureBuffers();
    }

    synchronized boolean attachVideoSurface(Surface surface) {
        if (mediaProjection == null || surface == null || width <= 0 || height <= 0 || densityDpi <= 0) {
            return false;
        }

        try {
            if (!attachSurface(
                    surface,
                    AndroidVirtualDisplayLifecycle.Output.Video)) {
                return false;
            }
        } catch (RuntimeException ex) {
            AndroidSessionLog.error(
                "Could not attach the H.264 encoder Surface to MediaProjection.",
                ex);
            return false;
        }

        ImageReader previousReader = imageReader;
        imageReader = null;
        activeVideoSurface = surface;
        closeImageReader(previousReader);
        return true;
    }

    /**
     * Returns the projection to its JPEG Surface before an encoder Surface is
     * stopped or released. If JPEG attachment fails, the video Surface is
     * detached so the caller may still release it safely.
     */
    boolean releaseVideoSurface(Surface surface) {
        ProjectionStoppedListener listenerToNotify = null;
        long failedProjectionGeneration = 0L;
        boolean safelyDetached;
        synchronized (this) {
            if (surface == null || activeVideoSurface != surface) {
                return true;
            }

            if (ensureJpegVirtualDisplay()) {
                return true;
            }

            safelyDetached = virtualDisplay == null;
            if (!safelyDetached) {
                try {
                    virtualDisplay.setSurface(null);
                    virtualDisplayLifecycle.recordSurfaceAttached(
                        AndroidVirtualDisplayLifecycle.Output.None);
                    safelyDetached = true;
                } catch (RuntimeException ex) {
                    AndroidSessionLog.error(
                        "Could not detach a failed H.264 encoder Surface; " +
                            "terminating the projection.",
                        ex);
                }
            }

            activeVideoSurface = null;
            if (!safelyDetached) {
                failedProjectionGeneration =
                    virtualDisplayLifecycle.getProjectionGeneration();
                listenerToNotify = projectionStoppedListener;
                projectionStoppedListener = null;
                clearInternal(true);
            }
        }

        notifyProjectionStopped(listenerToNotify, failedProjectionGeneration);
        return safelyDetached;
    }

    synchronized ScreenFrame captureJpeg(Context context, int quality, int maxEdge) {
        if (Build.VERSION.SDK_INT >= 30 && accessibilityCapture != null) {
            AndroidAccessibilityCapture.Frame frame = accessibilityCapture.capture(quality, maxEdge, sourceWidth, sourceHeight);
            if (frame == null) return null;
            if (sourceWidth != frame.sourceWidth || sourceHeight != frame.sourceHeight) {
                sourceWidth = frame.sourceWidth;
                sourceHeight = frame.sourceHeight;
                displayConfigurationGeneration++;
            }
            return frame.encoded;
        }
        try {
            refreshDisplayConfigurationIfChanged(context);
            return captureJpegCore(quality, maxEdge);
        } catch (RuntimeException ex) {
            recoverJpegOutputAfterFailure(ex);
            return null;
        }
    }

    private ScreenFrame captureJpegCore(int quality, int maxEdge) {
        boolean jpegOutputReady =
            imageReader != null &&
            imageReader.getWidth() == width &&
            imageReader.getHeight() == height &&
            virtualDisplay != null &&
            virtualDisplayLifecycle.getOutput() ==
                AndroidVirtualDisplayLifecycle.Output.Jpeg &&
            !virtualDisplayLifecycle.needsResize(width, height, densityDpi);
        if (!jpegOutputReady && !ensureJpegVirtualDisplay()) {
            return null;
        }

        if (imageReader == null) {
            return null;
        }

        long captureStartedAt = System.nanoTime();
        Image image = imageReader.acquireLatestImage();
        if (image == null) {
            return null;
        }

        try {
            Image.Plane plane = image.getPlanes()[0];
            ByteBuffer buffer = plane.getBuffer();
            int pixelStride = plane.getPixelStride();
            int rowStride = plane.getRowStride();
            int rowPadding = Math.max(0, rowStride - pixelStride * width);
            int paddedWidth = width + rowPadding / Math.max(1, pixelStride);

            Bitmap frameBitmap = getCaptureBitmap(paddedWidth, height);
            buffer.rewind();
            frameBitmap.copyPixelsFromBuffer(buffer);

            Bitmap outputBase = frameBitmap;
            if (paddedWidth != width) {
                outputBase = getCroppedBitmap(width, height);
                sourceRect.set(0, 0, width, height);
                destinationRect.set(0, 0, width, height);
                croppedCanvas.drawBitmap(frameBitmap, sourceRect, destinationRect, null);
            }

            int outputWidth = width;
            int outputHeight = height;
            Bitmap outputBitmap = outputBase;
            int longestEdge = Math.max(width, height);
            if (maxEdge > 0 && longestEdge > maxEdge) {
                double scale = maxEdge / (double) longestEdge;
                outputWidth = Math.max(1, (int) Math.round(width * scale));
                outputHeight = Math.max(1, (int) Math.round(height * scale));
                outputBitmap = getScaledBitmap(outputWidth, outputHeight);
                destinationRect.set(0, 0, outputWidth, outputHeight);
                scaledCanvas.drawBitmap(outputBase, null, destinationRect, scalePaint);
            }

            double captureMillis =
                (System.nanoTime() - captureStartedAt) / 1_000_000d;
            long encodeStartedAt = System.nanoTime();
            jpegOutput.reset();
            if (!outputBitmap.compress(
                    Bitmap.CompressFormat.JPEG,
                    Math.max(30, Math.min(90, quality)),
                    jpegOutput) ||
                jpegOutput.length() <= 0) {
                return null;
            }

            double encodeMillis =
                (System.nanoTime() - encodeStartedAt) / 1_000_000d;
            ScreenFrame frame = new ScreenFrame(
                outputWidth,
                outputHeight,
                jpegOutput.buffer(),
                jpegOutput.length(),
                captureMillis,
                encodeMillis);
            jpegRecoveryPolicy.recordFrameSuccess();
            return frame;
        } finally {
            image.close();
        }
    }

    synchronized void clear() {
        projectionStoppedListener = null;
        clearInternal(true);
    }

    synchronized boolean refreshDisplayConfigurationIfChanged(Context context) {
        return refreshDisplayConfigurationIfChanged(context, false);
    }

    synchronized boolean refreshDisplayConfigurationIfChanged(Context context, boolean force) {
        if (mediaProjection == null || context == null) {
            return false;
        }

        long now = System.nanoTime();
        if (!force && now - lastDisplayConfigurationCheckAt < DISPLAY_CONFIGURATION_CHECK_NANOS) {
            return false;
        }

        lastDisplayConfigurationCheckAt = now;
        DisplayMetrics metrics = resolveDisplayMetrics(context);
        int nextSourceWidth = virtualDisplayLifecycle.hasCapturedContentSize()
            ? virtualDisplayLifecycle.getCapturedContentWidth()
            : Math.max(1, metrics.widthPixels);
        int nextSourceHeight = virtualDisplayLifecycle.hasCapturedContentSize()
            ? virtualDisplayLifecycle.getCapturedContentHeight()
            : Math.max(1, metrics.heightPixels);
        int nextDensityDpi = Math.max(1, metrics.densityDpi);
        int nextRotation = resolveDisplayRotation(context);
        // Preserve the active MediaCodec alignment. Resetting to the default
        // 2-pixel geometry here makes 16/32-aligned vendor codecs alternate
        // between two sizes and restart on every 500 ms topology poll.
        int[] streamSize = captureGeometryPolicy.fitWithinMaxEdge(
            nextSourceWidth,
            nextSourceHeight,
            MAX_VIRTUAL_DISPLAY_EDGE);
        int nextWidth = streamSize[0];
        int nextHeight = streamSize[1];
        if (nextSourceWidth == sourceWidth &&
            nextSourceHeight == sourceHeight &&
            nextDensityDpi == densityDpi &&
            nextRotation == sourceRotation &&
            nextWidth == width &&
            nextHeight == height) {
            return false;
        }

        sourceWidth = nextSourceWidth;
        sourceHeight = nextSourceHeight;
        densityDpi = nextDensityDpi;
        sourceRotation = nextRotation;
        displayConfigurationGeneration++;
        width = nextWidth;
        height = nextHeight;
        recycleCaptureBuffers();
        if (virtualDisplayLifecycle.getOutput() ==
                AndroidVirtualDisplayLifecycle.Output.Jpeg &&
            !ensureJpegVirtualDisplay()) {
            AndroidSessionLog.info(
                "Display size changed, but the JPEG projection Surface could not be refreshed.");
        }
        return true;
    }

    private void handleCapturedContentResize(
        MediaProjection expectedProjection,
        long expectedGeneration,
        int capturedWidth,
        int capturedHeight) {
        boolean changed;
        synchronized (this) {
            if (mediaProjection != expectedProjection) {
                return;
            }

            changed = virtualDisplayLifecycle.recordCapturedContentResize(
                expectedGeneration,
                capturedWidth,
                capturedHeight);
            if (changed) {
                // Let the active JPEG or H.264 loop consume the new geometry.
                // H.264 must close its old fixed-size input Surface before a
                // new encoder is attached; doing that work in this callback
                // would invert the capture/host lock order.
                lastDisplayConfigurationCheckAt = 0L;
            }
        }

        if (changed) {
            AndroidSessionLog.info(
                "MediaProjection captured content resized: " +
                capturedWidth + "x" + capturedHeight + ".");
        }
    }

    private void handleProjectionStopped(
        MediaProjection expectedProjection,
        long expectedGeneration) {
        ProjectionStoppedListener listenerToNotify;
        synchronized (this) {
            if (mediaProjection != expectedProjection ||
                !virtualDisplayLifecycle.isCurrentProjection(expectedGeneration)) {
                return;
            }

            listenerToNotify = projectionStoppedListener;
            projectionStoppedListener = null;
            clearInternal(false);
        }

        // Never call service/host code while holding the capture lock. The
        // host capture worker may already be inside this object during stop.
        notifyProjectionStopped(listenerToNotify, expectedGeneration);
    }

    private void clearInternal(boolean stopProjection) {
        AndroidAccessibilityCapture previousAccessibility = accessibilityCapture;
        accessibilityCapture = null;
        if (Build.VERSION.SDK_INT >= 30 && previousAccessibility != null) previousAccessibility.close();
        VirtualDisplay display = virtualDisplay;
        virtualDisplay = null;
        activeVideoSurface = null;
        MediaProjection projection = mediaProjection;
        mediaProjection = null;
        MediaProjection.Callback callback = mediaProjectionCallback;
        mediaProjectionCallback = null;
        projectionStoppedListener = null;
        virtualDisplayLifecycle.clearProjection();

        if (display != null) {
            try {
                // Detach first: the current Surface may belong to an encoder
                // which will stop and release it after the host worker exits.
                // VirtualDisplay.release() must not retain that Surface.
                display.setSurface(null);
            } catch (RuntimeException ignored) {
            }
            try {
                display.release();
            } catch (RuntimeException ignored) {
            }
        }

        closeImageReader(imageReader);
        imageReader = null;

        if (projection != null && callback != null) {
            try {
                projection.unregisterCallback(callback);
            } catch (RuntimeException ignored) {
            }
        }
        if (stopProjection && projection != null) {
            try {
                projection.stop();
            } catch (RuntimeException ignored) {
            }
        }

        if (imageThread != null) {
            imageThread.quitSafely();
            imageThread = null;
            imageHandler = null;
        }

        recycleCaptureBuffers();
        resultCode = 0;
        resultData = null;
        sourceWidth = 0;
        sourceHeight = 0;
        width = 0;
        height = 0;
        densityDpi = 0;
        sourceRotation = 0;
        displayConfigurationGeneration++;
        lastDisplayConfigurationCheckAt = 0;
        jpegRecoveryPolicy.reset();
        captureGeometryPolicy.reset();
    }

    synchronized long getProjectionGeneration() {
        return virtualDisplayLifecycle.getProjectionGeneration();
    }

    private static void notifyProjectionStopped(
        ProjectionStoppedListener listener,
        long projectionGeneration) {
        if (listener == null) {
            return;
        }

        try {
            listener.onProjectionStopped(projectionGeneration);
        } catch (RuntimeException ex) {
            AndroidSessionLog.error("MediaProjection stop listener failed.", ex);
        }
    }

    private Bitmap getCaptureBitmap(int bitmapWidth, int bitmapHeight) {
        if (captureBitmap == null ||
            captureBitmap.getWidth() != bitmapWidth ||
            captureBitmap.getHeight() != bitmapHeight) {
            recycleBitmap(captureBitmap);
            captureBitmap = Bitmap.createBitmap(bitmapWidth, bitmapHeight, Bitmap.Config.ARGB_8888);
        }

        return captureBitmap;
    }

    private boolean ensureJpegVirtualDisplay() {
        return ensureJpegVirtualDisplay(false);
    }

    private boolean ensureJpegVirtualDisplay(boolean replaceExistingReader) {
        if (!replaceExistingReader &&
            imageReader != null &&
            imageReader.getWidth() == width &&
            imageReader.getHeight() == height &&
            virtualDisplay != null &&
            virtualDisplayLifecycle.getOutput() ==
                AndroidVirtualDisplayLifecycle.Output.Jpeg &&
            !virtualDisplayLifecycle.needsResize(width, height, densityDpi)) {
            return true;
        }

        if (mediaProjection == null || width <= 0 || height <= 0 || densityDpi <= 0) {
            return false;
        }

        if (imageThread == null) {
            imageThread = new HandlerThread("RemoteDeskCapture");
            imageThread.start();
            imageHandler = new Handler(imageThread.getLooper());
        }

        ImageReader replacement = null;
        try {
            replacement = ImageReader.newInstance(
                width,
                height,
                PixelFormat.RGBA_8888,
                IMAGE_READER_MAX_IMAGES);
            if (!attachSurface(
                    replacement.getSurface(),
                    AndroidVirtualDisplayLifecycle.Output.Jpeg)) {
                closeImageReader(replacement);
                return false;
            }
        } catch (RuntimeException ex) {
            closeImageReader(replacement);
            AndroidSessionLog.error(
                "Could not attach the JPEG ImageReader Surface to MediaProjection.",
                ex);
            return false;
        }

        ImageReader previousReader = imageReader;
        imageReader = replacement;
        activeVideoSurface = null;
        if (previousReader != replacement) {
            closeImageReader(previousReader);
        }
        return true;
    }

    private void recoverJpegOutputAfterFailure(RuntimeException failure) {
        long now = System.nanoTime();
        if (!jpegRecoveryPolicy.shouldAttempt(now)) {
            return;
        }

        // Record the deadline before touching vendor ImageReader/VD code. A
        // second platform exception must still be bounded by the same policy.
        jpegRecoveryPolicy.recordAttempt(now);
        AndroidSessionLog.error(
            "JPEG ImageReader failed; replacing its Surface in the existing " +
                "MediaProjection VirtualDisplay.",
            failure);
        if (!ensureJpegVirtualDisplay(true)) {
            AndroidSessionLog.info(
                "JPEG ImageReader recovery was deferred; the control session " +
                    "remains active and capture will retry with backoff.");
        }
    }

    private boolean attachSurface(
        Surface surface,
        AndroidVirtualDisplayLifecycle.Output output) {
        if (mediaProjection == null || surface == null) {
            return false;
        }

        if (virtualDisplay == null) {
            if (!virtualDisplayLifecycle.shouldCreateVirtualDisplay()) {
                throw new IllegalStateException(
                    "MediaProjection VirtualDisplay ownership is inconsistent.");
            }

            VirtualDisplay created = mediaProjection.createVirtualDisplay(
                "RemoteDesk",
                width,
                height,
                densityDpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
                surface,
                null,
                imageHandler);
            if (created == null) {
                return false;
            }

            virtualDisplay = created;
            virtualDisplayLifecycle.recordVirtualDisplayCreated(
                width,
                height,
                densityDpi,
                output);
            AndroidSessionLog.info(
                "MediaProjection VirtualDisplay created once for this grant: " +
                width + "x" + height + "@" + densityDpi +
                ", output=" + output + ".");
            return true;
        }

        if (virtualDisplayLifecycle.needsResize(width, height, densityDpi)) {
            virtualDisplay.resize(width, height, densityDpi);
            virtualDisplayLifecycle.recordResize(width, height, densityDpi);
            AndroidSessionLog.info(
                "MediaProjection VirtualDisplay resized in place: " +
                width + "x" + height + "@" + densityDpi + ".");
        }

        AndroidVirtualDisplayLifecycle.Output previousOutput =
            virtualDisplayLifecycle.getOutput();
        virtualDisplay.setSurface(surface);
        virtualDisplayLifecycle.recordSurfaceAttached(output);
        if (previousOutput != output) {
            AndroidSessionLog.info(
                "MediaProjection output Surface switched in place: " +
                previousOutput + " -> " + output + ".");
        }
        return true;
    }

    private static void closeImageReader(ImageReader reader) {
        if (reader == null) {
            return;
        }

        try {
            reader.close();
        } catch (RuntimeException ignored) {
        }
    }

    private Bitmap getCroppedBitmap(int bitmapWidth, int bitmapHeight) {
        if (croppedBitmap == null ||
            croppedBitmap.getWidth() != bitmapWidth ||
            croppedBitmap.getHeight() != bitmapHeight) {
            recycleBitmap(croppedBitmap);
            croppedBitmap = Bitmap.createBitmap(bitmapWidth, bitmapHeight, Bitmap.Config.ARGB_8888);
            croppedCanvas = new Canvas(croppedBitmap);
        }

        return croppedBitmap;
    }

    private Bitmap getScaledBitmap(int bitmapWidth, int bitmapHeight) {
        if (scaledBitmap == null ||
            scaledBitmap.getWidth() != bitmapWidth ||
            scaledBitmap.getHeight() != bitmapHeight) {
            recycleBitmap(scaledBitmap);
            scaledBitmap = Bitmap.createBitmap(bitmapWidth, bitmapHeight, Bitmap.Config.ARGB_8888);
            scaledCanvas = new Canvas(scaledBitmap);
        }

        return scaledBitmap;
    }

    private void recycleCaptureBuffers() {
        recycleBitmap(captureBitmap);
        recycleBitmap(croppedBitmap);
        recycleBitmap(scaledBitmap);
        captureBitmap = null;
        croppedBitmap = null;
        scaledBitmap = null;
        croppedCanvas = null;
        scaledCanvas = null;
        jpegOutput = new ReusableByteArrayOutputStream(256 * 1024);
    }

    private static void recycleBitmap(Bitmap bitmap) {
        if (bitmap != null && !bitmap.isRecycled()) {
            bitmap.recycle();
        }
    }

    @SuppressWarnings("deprecation")
    private static DisplayMetrics resolveDisplayMetrics(Context context) {
        DisplayMetrics metrics = new DisplayMetrics();
        WindowManager manager = (WindowManager) context.getSystemService(Context.WINDOW_SERVICE);
        if (manager != null && Build.VERSION.SDK_INT >= 30) {
            WindowMetrics windowMetrics = manager.getMaximumWindowMetrics();
            Rect bounds = windowMetrics.getBounds();
            metrics.widthPixels = bounds.width();
            metrics.heightPixels = bounds.height();
            metrics.densityDpi = context.getResources().getDisplayMetrics().densityDpi;
            return metrics;
        }

        if (manager != null) {
            manager.getDefaultDisplay().getRealMetrics(metrics);
            return metrics;
        }

        return context.getResources().getDisplayMetrics();
    }

    @SuppressWarnings("deprecation")
    private static int resolveDisplayRotation(Context context) {
        WindowManager manager =
            (WindowManager) context.getSystemService(Context.WINDOW_SERVICE);
        return manager == null
            ? 0
            : manager.getDefaultDisplay().getRotation();
    }

    static int[] fitWithinMaxEdge(int sourceWidth, int sourceHeight, int maxEdge) {
        return fitWithinMaxEdgeAligned(
            sourceWidth,
            sourceHeight,
            maxEdge,
            2,
            2);
    }

    static int[] fitWithinMaxEdgeAligned(
        int sourceWidth,
        int sourceHeight,
        int maxEdge,
        int widthAlignment,
        int heightAlignment) {
        int longestEdge = Math.max(sourceWidth, sourceHeight);
        if (maxEdge <= 0 || longestEdge <= maxEdge) {
            return new int[] {
                alignVideoEdge(sourceWidth, widthAlignment),
                alignVideoEdge(sourceHeight, heightAlignment)
            };
        }

        double scale = maxEdge / (double) longestEdge;
        return new int[] {
            alignVideoEdge(
                (int) Math.round(sourceWidth * scale),
                widthAlignment),
            alignVideoEdge(
                (int) Math.round(sourceHeight * scale),
                heightAlignment)
        };
    }

    private static int alignVideoEdge(int value, int codecAlignment) {
        // AVC Surface encoders universally require at least 2-pixel chroma
        // alignment. Preserve any stricter vendor requirement discovered via
        // VideoCapabilities without ever rounding above the negotiated edge.
        int alignment = leastCommonMultiple(
            2,
            Math.min(256, Math.max(1, codecAlignment)));
        int normalized = Math.max(alignment, value);
        return Math.max(alignment, normalized - normalized % alignment);
    }

    private static int leastCommonMultiple(int left, int right) {
        int a = Math.max(1, left);
        int b = Math.max(1, right);
        int x = a;
        int y = b;
        while (y != 0) {
            int remainder = x % y;
            x = y;
            y = remainder;
        }
        long result = (long) a / x * b;
        return result > Integer.MAX_VALUE
            ? Integer.MAX_VALUE
            : (int) result;
    }

    static final class ScreenFrame {
        final int width;
        final int height;
        final byte[] jpegBytes;
        final int jpegLength;
        final double captureMillis;
        final double encodeMillis;

        ScreenFrame(
            int width,
            int height,
            byte[] jpegBytes,
            int jpegLength,
            double captureMillis,
            double encodeMillis) {
            this.width = width;
            this.height = height;
            this.jpegBytes = jpegBytes;
            this.jpegLength = jpegLength;
            this.captureMillis = captureMillis;
            this.encodeMillis = encodeMillis;
        }
    }

    interface ProjectionStoppedListener {
        void onProjectionStopped(long projectionGeneration);
    }

    private static final class ReusableByteArrayOutputStream extends ByteArrayOutputStream {
        ReusableByteArrayOutputStream(int size) {
            super(size);
        }

        byte[] buffer() {
            return buf;
        }

        int length() {
            return count;
        }
    }
}
