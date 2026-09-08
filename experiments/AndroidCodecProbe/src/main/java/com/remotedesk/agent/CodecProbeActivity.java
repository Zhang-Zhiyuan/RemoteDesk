package com.remotedesk.agent;

import android.app.Activity;
import android.graphics.Bitmap;
import android.graphics.Color;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.util.Log;
import android.view.PixelCopy;
import android.view.Surface;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.WindowManager;
import android.widget.FrameLayout;
import org.json.JSONArray;
import org.json.JSONObject;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicReference;

/** Isolated test APK. Never opens a connection or reads the real app's data. */
public final class CodecProbeActivity extends Activity {
    private FrameLayout container;
    private final Handler main = new Handler(Looper.getMainLooper());
    private volatile AndroidH264SurfaceDecoder active;
    private volatile boolean destroyed;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        container = new FrameLayout(this);
        setContentView(container);
        new Thread(this::runProbe, "CodecProbe").start();
    }

    private void runProbe() {
        JSONObject report = new JSONObject();
        JSONArray results = new JSONArray();
        try {
            report.put("sdk", android.os.Build.VERSION.SDK_INT);
            report.put("model", android.os.Build.MODEL);
            report.put("fingerprint", android.os.Build.FINGERPRINT);
            JSONArray codecs = new JSONArray();
            for (var candidate : AndroidH264DecoderDiagnostics.h264DecoderCandidates())
                codecs.put(new JSONObject().put("name", candidate.codecName)
                    .put("hardware", candidate.hardwareAccelerated).put("software", candidate.softwareOnly));
            report.put("candidates", codecs);
            for (String fixture : new String[] {"static-current-gop1.h264", "static-research-gop30.h264"}) {
                JSONObject row = new JSONObject().put("fixture", fixture);
                try { testStream(fixture, row); row.put("passed", true); }
                catch (Throwable failure) { row.put("passed", false); row.put("failure", failure.toString()); }
                finally {
                    AndroidH264SurfaceDecoder decoder = active;
                    active = null;
                    if (decoder != null) decoder.close();
                }
                results.put(row);
                report.put("results", results);
                report.put("diagnostics", new JSONArray(AndroidSessionLog.snapshot()));
                save(report);
            }
        } catch (Throwable failure) {
            try { report.put("fatal", failure.toString()); } catch (Exception ignored) { }
        } finally {
            try { report.put("complete", true); save(report); }
            catch (Exception failure) { Log.e("RemoteDeskCodecProbe", "Cannot save result", failure); }
        }
    }

    private void testStream(String fixture, JSONObject row) throws Exception {
        List<RemoteDeskTransport.FrameMessage> frames = readFrames(fixture);
        check(frames.size() == 180, "Expected 180 complete fixture access units, got " + frames.size());
        row.put("sourceFrames", frames.size());
        AtomicInteger rendered = new AtomicInteger();
        AtomicInteger verifiedBuffers = new AtomicInteger();
        AndroidViewerHealthTracker health = new AndroidViewerHealthTracker();
        health.start(System.nanoTime());
        AtomicInteger recoveries = new AtomicInteger();
        AtomicReference<String> selected = new AtomicReference<>();
        AtomicReference<String> unavailable = new AtomicReference<>();
        AndroidH264SurfaceDecoder decoder = new AndroidH264SurfaceDecoder(
            AndroidH264DecoderDiagnostics.h264DecoderCandidates(), new AndroidH264SurfaceDecoder.Listener() {
                public void onRecoveryFrameNeeded(String reason) { recoveries.incrementAndGet(); }
                public void onDecoderStarted(AndroidH264DecoderDiagnostics.DecoderCandidate candidate) {
                    selected.set(candidate.codecName + " (" + candidate.selectionLabel() + ")");
                    health.markPresentationTelemetryUnavailable();
                }
                public void onFrameRendered() { rendered.incrementAndGet(); health.recordPresentedFrame(); }
                public void onSurfaceBufferAvailable() { verifiedBuffers.incrementAndGet(); }
                public void onDecoderUnavailable(String reason) { unavailable.set(reason); }
            });
        active = decoder;
        SurfaceView view = createSurface().get(10, TimeUnit.SECONDS);
        decoder.setOutputSurface(view.getHolder().getSurface());
        boolean dependentFixture = frames.get(1).flags == 0;
        if (dependentFixture) {
            check(decoder.offer(frames.get(1)) == AndroidH264SurfaceDecoder.OfferResult.DROPPED_WAITING_FOR_RECOVERY,
                "Cold-start dependent frame was not rejected");
            row.put("coldOrphanRejected", true);
        }
        long started = SystemClock.elapsedRealtime();
        int accepted = 0;
        for (var frame : frames) {
            check(!destroyed, "Activity was destroyed");
            check(unavailable.get() == null, "Decoder unavailable: " + unavailable.get());
            check(!decoder.rejectUnpresentedCandidateIfTimedOut(System.nanoTime()),
                "Presentation watchdog rejected the active decoder");
            if (decoder.offer(frame) == AndroidH264SurfaceDecoder.OfferResult.ACCEPTED) accepted++;
            if (accepted == 20) {
                row.put("callbacksAtPixelSample", rendered.get()).put("selectedAtPixelSample", selected.get());
                try { row.put("earlySurfacePixels", inspectPixels(view)); }
                catch (Exception failure) { row.put("earlyPixelFailure", failure.toString()); }
            }
            // Functional test at a deliberately bounded 30 Hz, not a 60 FPS claim.
            SystemClock.sleep(34);
        }
        SystemClock.sleep(500);
        row.put("accepted", accepted).put("renderedCallbacks", rendered.get())
            .put("verifiedSurfaceBuffers", verifiedBuffers.get())
            .put("submittedSurfaceOutputs", decoder.submittedSurfaceOutputCount())
            .put("elapsedMs", SystemClock.elapsedRealtime() - started).put("selected", selected.get())
            .put("recoveryRequests", recoveries.get()).put("unavailable", unavailable.get());
        check(unavailable.get() == null, "Decoder unavailable: " + unavailable.get());
        check(!decoder.rejectUnpresentedCandidateIfTimedOut(System.nanoTime()), "Late presentation timeout");
        check(decoder.submittedSurfaceOutputCount() >= 150, "Too few real Surface outputs");
        check(rendered.get() >= 150 || verifiedBuffers.get() > 0, "No presentation evidence");
        boolean unknownFps = Double.isNaN(health.snapshot(System.nanoTime(), false, false, false)
            .presentedFramesPerSecond);
        row.put("presentedFpsUnknown", unknownFps);
        if (rendered.get() == 0) check(unknownFps, "Invented FPS without render callbacks");
        row.put("surfacePixels", inspectPixels(view));

        decoder.setOutputSurface(null);
        if (dependentFixture)
            check(decoder.offer(frames.get(1)) == AndroidH264SurfaceDecoder.OfferResult.DROPPED_WAITING_FOR_RECOVERY,
                "Dependent frame was accepted without a Surface");
        SurfaceView replacement = createSurface().get(10, TimeUnit.SECONDS);
        decoder.setOutputSurface(replacement.getHolder().getSurface());
        if (dependentFixture)
            check(decoder.offer(frames.get(1)) == AndroidH264SurfaceDecoder.OfferResult.DROPPED_WAITING_FOR_RECOVERY,
                "Dependent frame was accepted on a new Surface before recovery");
        int previous = rendered.get();
        int previousVerified = verifiedBuffers.get();
        for (var frame : frames.subList(120, 180)) {
            check(!decoder.rejectUnpresentedCandidateIfTimedOut(System.nanoTime()), "Replacement timed out");
            decoder.offer(frame);
            SystemClock.sleep(34);
        }
        SystemClock.sleep(500);
        row.put("renderedAfterSurfaceReplacement", rendered.get() - previous);
        row.put("verifiedAfterSurfaceReplacement", verifiedBuffers.get() - previousVerified);
        row.put("submittedAfterSurfaceReplacement", decoder.submittedSurfaceOutputCount());
        check(unavailable.get() == null, "Replacement decoder unavailable: " + unavailable.get());
        check(decoder.submittedSurfaceOutputCount() >= 45 &&
            (rendered.get() - previous >= 45 || verifiedBuffers.get() > previousVerified),
            "No continuous output after Surface replacement");
        row.put("replacementPixels", inspectPixels(replacement));
        decoder.close();
        decoder.close();
        check(decoder.offer(frames.get(0)) == AndroidH264SurfaceDecoder.OfferResult.REJECTED_CLOSED,
            "Closed decoder accepted a frame");
        row.put("closeIsIdempotent", true);
    }

    private CompletableFuture<SurfaceView> createSurface() {
        CompletableFuture<SurfaceView> ready = new CompletableFuture<>();
        main.post(() -> {
            if (destroyed) { ready.completeExceptionally(new IllegalStateException("Activity destroyed")); return; }
            container.removeAllViews();
            SurfaceView view = new SurfaceView(this);
            view.getHolder().setFixedSize(1920, 1080);
            view.getHolder().addCallback(new SurfaceHolder.Callback() {
                public void surfaceCreated(SurfaceHolder holder) { ready.complete(view); }
                public void surfaceChanged(SurfaceHolder holder, int format, int w, int h) { }
                public void surfaceDestroyed(SurfaceHolder holder) { }
            });
            container.addView(view, new FrameLayout.LayoutParams(-1, -1));
        });
        return ready;
    }

    private JSONObject inspectPixels(SurfaceView view) throws Exception {
        Bitmap pixels = Bitmap.createBitmap(1920, 1080, Bitmap.Config.ARGB_8888);
        try {
            CompletableFuture<Integer> copied = new CompletableFuture<>();
            main.post(() -> PixelCopy.request(view, pixels, copied::complete, main));
            int status = copied.get(5, TimeUnit.SECONDS);
            check(status == PixelCopy.SUCCESS, "Surface PixelCopy status " + status);
            long left = 0, right = 0;
            int count = 0;
            for (int y = 10; y < 1000; y += 20)
                for (int x = 10; x < 900; x += 20) {
                    left += Color.red(pixels.getPixel(x, y));
                    right += Color.red(pixels.getPixel(x + 960, y));
                    count++;
                }
            double leftMean = left / (double)count, rightMean = right / (double)count;
            check(leftMean > 160 && rightMean < 100 && leftMean - rightMean > 90,
                "Rendered pixels do not match the synthetic light/dark desktop: " + leftMean + "/" + rightMean);
            return new JSONObject().put("leftRedMean", leftMean).put("rightRedMean", rightMean);
        } finally { pixels.recycle(); }
    }

    private List<RemoteDeskTransport.FrameMessage> readFrames(String name) throws Exception {
        byte[] bytes;
        try (InputStream input = getAssets().open(name); ByteArrayOutputStream output = new ByteArrayOutputStream()) {
            byte[] buffer = new byte[65536];
            for (int n; (n = input.read(buffer)) > 0;) output.write(buffer, 0, n);
            bytes = output.toByteArray();
        }
        List<Integer> starts = new ArrayList<>();
        for (int index = 0; index < bytes.length - 4; index++) {
            int prefix = prefixAt(bytes, index);
            if (prefix > 0) {
                if ((bytes[index + prefix] & 31) == 9) starts.add(index);
                index += prefix;
            }
        }
        List<RemoteDeskTransport.FrameMessage> frames = new ArrayList<>();
        for (int i = 0; i < starts.size(); i++) {
            int end = i + 1 < starts.size() ? starts.get(i + 1) : bytes.length;
            byte[] au = Arrays.copyOfRange(bytes, starts.get(i), end);
            boolean idr = false, sps = false, pps = false;
            for (int index = 0; index < au.length - 4; index++) {
                int prefix = prefixAt(au, index);
                if (prefix > 0) {
                    int type = au[index + prefix] & 31;
                    idr |= type == 5; sps |= type == 7; pps |= type == 8;
                    index += prefix;
                }
            }
            check(!idr || (sps && pps), "Recovery fixture lacks SPS/PPS");
            frames.add(new RemoteDeskTransport.FrameMessage(RemoteDeskProtocol.FRAME_ENCODING_H264_ANNEX_B,
                1920, 1080, idr ? 3 : 0, 0, 0, au));
        }
        return frames;
    }

    private static int prefixAt(byte[] bytes, int index) {
        if (bytes[index] != 0 || bytes[index + 1] != 0) return 0;
        if (bytes[index + 2] == 1) return 3;
        return bytes[index + 2] == 0 && bytes[index + 3] == 1 ? 4 : 0;
    }
    private static void check(boolean passed, String message) {
        if (!passed) throw new IllegalStateException(message);
    }
    private void save(JSONObject report) throws Exception {
        try (FileOutputStream output = new FileOutputStream(new File(getFilesDir(), "codec-probe.json"))) {
            output.write(report.toString(2).getBytes(StandardCharsets.UTF_8));
        }
        Log.i("RemoteDeskCodecProbe", report.toString());
    }
    @Override protected void onDestroy() {
        destroyed = true;
        AndroidH264SurfaceDecoder decoder = active;
        if (decoder != null) decoder.close();
        super.onDestroy();
    }
}
