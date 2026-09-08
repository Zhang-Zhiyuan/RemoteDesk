package com.remotedesk.agent;

import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.os.Bundle;
import android.view.Surface;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.util.Collections;
import java.util.List;
import java.util.Set;

final class AndroidH264ScreenEncoder implements AutoCloseable {
    private static final String MIME_TYPE = "video/avc";
    // GOP1 is the safe baseline shared with the Windows implementation. Every
    // access unit can recover a freshly created decoder and latest-only queues
    // may discard stale work without retaining a dependency chain.
    static final int I_FRAME_INTERVAL_SECONDS = 0;
    static final long OUTPUT_DEQUEUE_SLICE_US = 10_000;
    static final long FIRST_RECOVERY_FRAME_TIMEOUT_MILLIS = 1_500L;
    private static final int MAX_SILENT_STARTS_PER_CANDIDATE = 2;
    private static final byte[] START_CODE = new byte[] { 0, 0, 0, 1 };
    private static final int BITRATE_MODE_DEFAULT = -1;
    static final int HINT_LATENCY = 1;
    static final int HINT_PRIORITY = 1 << 1;
    static final int HINT_OPERATING_RATE = 1 << 2;
    static final int HINT_MAX_FPS_TO_ENCODER = 1 << 3;
    static final int HINT_REPEAT_PREVIOUS_FRAME = 1 << 4;
    private static final int[] LOW_LATENCY_HINT_PROFILES = new int[] {
        HINT_LATENCY | HINT_PRIORITY | HINT_OPERATING_RATE |
            HINT_MAX_FPS_TO_ENCODER | HINT_REPEAT_PREVIOUS_FRAME,
        HINT_LATENCY | HINT_PRIORITY | HINT_OPERATING_RATE | HINT_MAX_FPS_TO_ENCODER,
        HINT_LATENCY | HINT_PRIORITY | HINT_OPERATING_RATE,
        HINT_LATENCY | HINT_PRIORITY,
        HINT_LATENCY,
        0
    };

    private final AndroidScreenCaptureSession captureSession;
    private final Set<String> excludedCodecNames;
    private final MediaCodec.BufferInfo bufferInfo = new MediaCodec.BufferInfo();

    private MediaCodec codec;
    private Surface inputSurface;
    private byte[] codecConfig = new byte[0];
    private int width;
    private int height;
    private int currentBitrate;
    private long lastPresentationTimeUs = Long.MIN_VALUE;
    private VideoFrame pendingFirstFrame;
    private String selectedCodecName;
    private int currentFps;
    private long supersededOutputFrames;
    private boolean selectedHardwareAccelerated;

    AndroidH264ScreenEncoder(AndroidScreenCaptureSession captureSession) {
        this(captureSession, Collections.emptySet());
    }

    AndroidH264ScreenEncoder(
        AndroidScreenCaptureSession captureSession,
        Set<String> excludedCodecNames) {
        this.captureSession = captureSession;
        this.excludedCodecNames = excludedCodecNames == null
            ? Collections.emptySet()
            : excludedCodecNames;
    }

    boolean start(int fps) throws IOException {
        if (captureSession.getStreamWidth() <= 0 ||
            captureSession.getStreamHeight() <= 0) {
            return false;
        }

        int requestedFps = Math.max(1, fps);
        List<AndroidVideoCodecDiagnostics.EncoderCandidate> candidates;
        try {
            candidates = AndroidVideoCodecDiagnostics.h264EncoderCandidates();
        } catch (RuntimeException ex) {
            AndroidSessionLog.error(
                "H.264 encoder enumeration failed; falling back to JPEG.",
                ex);
            return false;
        }

        if (candidates.isEmpty()) {
            AndroidSessionLog.info(
                "No Surface-input H.264 encoder candidate is available; falling back to JPEG.");
            return false;
        }

        boolean hardwareCandidateAttempted = false;
        boolean compatibilityFallbackLogged = false;
        for (AndroidVideoCodecDiagnostics.EncoderCandidate candidate : candidates) {
            if (excludedCodecNames.contains(candidate.codecName)) {
                AndroidSessionLog.info(
                    "Skipping H.264 encoder rejected earlier in this viewer session: " +
                    candidate.codecName +
                    ".");
                continue;
            }

            boolean hardwareCandidate =
                candidate.hardwareAccelerated && !candidate.softwareOnly;
            if (hardwareCandidate) {
                hardwareCandidateAttempted = true;
            } else if (!compatibilityFallbackLogged) {
                compatibilityFallbackLogged = true;
                AndroidSessionLog.info(
                    hardwareCandidateAttempted
                        ? "All hardware H.264 encoder candidates failed; trying compatibility candidates."
                        : "No known hardware H.264 encoder is available; trying compatibility candidates.");
            }

            captureSession.configureEncoderAlignment(
                candidate.widthAlignment,
                candidate.heightAlignment);
            width = captureSession.getStreamWidth();
            height = captureSession.getStreamHeight();
            Throwable lastFailure = null;
            int silentStarts = 0;
            int candidateFps = candidateFrameRate(requestedFps, candidate);
            if (!candidate.supportsSizeAndRate(width, height, candidateFps)) {
                if (candidateFps > 30 &&
                    candidate.supportsSizeAndRate(width, height, 30)) {
                    AndroidSessionLog.info(
                        "H.264 encoder does not support " +
                        width + "x" + height + "@" + candidateFps +
                        "; trying 30 fps: " + candidate.codecName + ".");
                    candidateFps = 30;
                } else {
                    AndroidSessionLog.info(
                        "Skipping H.264 encoder with unsupported size/rate: " +
                        candidate.codecName + ", " + width + "x" + height +
                        "@" + candidateFps + ".");
                    continue;
                }
            }
            int targetBitrate = estimateBitrate(width, height, candidateFps);
            int[] bitrateModes = bitrateModesForCandidate(candidate);
            candidateAttempts:
            for (int bitrateMode : bitrateModes) {
                for (int lowLatencyHints : LOW_LATENCY_HINT_PROFILES) {
                    try {
                        currentBitrate = targetBitrate;
                        if (startWithFormat(
                                createFormat(
                                    candidateFps,
                                    targetBitrate,
                                    bitrateMode,
                                    lowLatencyHints),
                                candidate.codecName)) {
                            VideoFrame firstFrame =
                                awaitFirstRecoveryFrame(FIRST_RECOVERY_FRAME_TIMEOUT_MILLIS);
                            if (firstFrame == null) {
                                silentStarts++;
                                lastFailure = new IOException(
                                    "encoder started but did not produce a recovery access unit");
                                close();
                                if (silentStarts >= MAX_SILENT_STARTS_PER_CANDIDATE) {
                                    break candidateAttempts;
                                }
                                continue;
                            }

                            pendingFirstFrame = firstFrame;
                            selectedCodecName = candidate.codecName;
                            currentFps = candidateFps;
                            selectedHardwareAccelerated =
                                candidate.hardwareAccelerated &&
                                !candidate.softwareOnly;
                            AndroidVideoCodecDiagnostics
                                .recordSelectedH264Encoder(candidate);
                            AndroidSessionLog.info(
                                "H.264 encoder produced a recovery access unit: " +
                                candidate.codecName +
                                ", selection=" +
                                candidate.selectionLabel() +
                                ", bitrateMode=" +
                                bitrateModeLabel(bitrateMode) +
                                ", lowLatencyHints=" +
                                lowLatencyHintLabel(lowLatencyHints) +
                                ", fps=" + candidateFps +
                                ".");
                            return true;
                        }
                    } catch (IOException | RuntimeException ex) {
                        lastFailure = ex;
                        close();
                    }
                }
            }

            String failureMessage =
                "H.264 encoder candidate failed: " +
                candidate.codecName +
                ", selection=" +
                candidate.selectionLabel() +
                ".";
            if (lastFailure == null) {
                AndroidSessionLog.info(failureMessage);
            } else {
                AndroidSessionLog.error(
                    failureMessage,
                    lastFailure);
            }
        }

        return false;
    }

    private boolean startWithFormat(
        MediaFormat format,
        String codecName) throws IOException {
        codec = MediaCodec.createByCodecName(codecName);
        codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
        inputSurface = codec.createInputSurface();
        codec.start();
        if (!captureSession.attachVideoSurface(inputSurface)) {
            close();
            return false;
        }

        requestKeyFrame();
        return true;
    }

    static int[] bitrateModesForCandidate(
        AndroidVideoCodecDiagnostics.EncoderCandidate candidate) {
        int modeCount = 1;
        if (candidate.cbrSupported) {
            modeCount++;
        }

        if (candidate.vbrSupported) {
            modeCount++;
        }

        int[] modes = new int[modeCount];
        int index = 0;
        if (candidate.cbrSupported) {
            modes[index++] =
                MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR;
        }

        if (candidate.vbrSupported) {
            modes[index++] =
                MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_VBR;
        }

        modes[index] = BITRATE_MODE_DEFAULT;
        return modes;
    }

    static int candidateFrameRate(
        int requestedFps,
        AndroidVideoCodecDiagnostics.EncoderCandidate candidate) {
        int normalized = Math.max(1, requestedFps);
        return candidate != null &&
            candidate.hardwareAccelerated &&
            !candidate.softwareOnly
            ? normalized
            : Math.min(
                normalized,
                AndroidVideoStreamSettings.H264_COMPATIBILITY_FPS);
    }

    static int[] lowLatencyHintProfiles() {
        return LOW_LATENCY_HINT_PROFILES.clone();
    }

    private static String bitrateModeLabel(int bitrateMode) {
        if (bitrateMode ==
                MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR) {
            return "CBR";
        }

        if (bitrateMode ==
                MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_VBR) {
            return "VBR";
        }

        return "codec-default";
    }

    private static String lowLatencyHintLabel(int hints) {
        if (hints == 0) {
            return "none";
        }

        StringBuilder label = new StringBuilder();
        appendHintLabel(label, hints, HINT_LATENCY, "latency");
        appendHintLabel(label, hints, HINT_PRIORITY, "priority");
        appendHintLabel(label, hints, HINT_OPERATING_RATE, "operating-rate");
        appendHintLabel(label, hints, HINT_MAX_FPS_TO_ENCODER, "max-fps");
        appendHintLabel(label, hints, HINT_REPEAT_PREVIOUS_FRAME, "static-repeat");
        return label.toString();
    }

    private static void appendHintLabel(
        StringBuilder label,
        int hints,
        int hint,
        String name) {
        if ((hints & hint) == 0) {
            return;
        }

        if (label.length() > 0) {
            label.append('+');
        }
        label.append(name);
    }

    private MediaFormat createFormat(int fps, int bitrate, int bitrateMode, int lowLatencyHints) {
        MediaFormat format = MediaFormat.createVideoFormat(MIME_TYPE, width, height);
        AndroidH264LowLatencyOptions options =
            AndroidH264LowLatencyOptions.forFrameRate(fps);
        format.setInteger(
            MediaFormat.KEY_COLOR_FORMAT,
            MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
        format.setInteger(MediaFormat.KEY_BIT_RATE, bitrate);
        format.setInteger(MediaFormat.KEY_FRAME_RATE, fps);
        format.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, I_FRAME_INTERVAL_SECONDS);
        if (bitrateMode != BITRATE_MODE_DEFAULT) {
            format.setInteger(MediaFormat.KEY_BITRATE_MODE, bitrateMode);
        }

        // The Windows viewer uses a strict one-input/one-output low-latency
        // decode transaction. Frame reordering would violate that contract,
        // so even the compatibility candidate must either accept no B-frames
        // or fail and let the session use JPEG.
        format.setInteger(
            AndroidH264LowLatencyOptions.KEY_MAX_B_FRAMES,
            options.maxBFrames);

        if ((lowLatencyHints & HINT_LATENCY) != 0) {
            format.setInteger(AndroidH264LowLatencyOptions.KEY_LATENCY, options.latencyFrames);
        }
        if ((lowLatencyHints & HINT_PRIORITY) != 0) {
            format.setInteger(AndroidH264LowLatencyOptions.KEY_PRIORITY, options.priority);
        }
        if ((lowLatencyHints & HINT_OPERATING_RATE) != 0) {
            format.setFloat(AndroidH264LowLatencyOptions.KEY_OPERATING_RATE, options.operatingRate);
        }
        if ((lowLatencyHints & HINT_MAX_FPS_TO_ENCODER) != 0) {
            format.setFloat(
                AndroidH264LowLatencyOptions.KEY_MAX_FPS_TO_ENCODER,
                options.maxFpsToEncoder);
        }
        if ((lowLatencyHints & HINT_REPEAT_PREVIOUS_FRAME) != 0) {
            format.setLong(
                AndroidH264LowLatencyOptions.KEY_REPEAT_PREVIOUS_FRAME_AFTER,
                options.repeatPreviousFrameAfterMicroseconds);
        }

        return format;
    }

    VideoFrame dequeueFrame(long timeoutMillis) {
        VideoFrame firstFrame = pendingFirstFrame;
        if (firstFrame != null) {
            pendingFirstFrame = null;
            return firstFrame;
        }

        return dequeueCodecFrame(timeoutMillis);
    }

    private VideoFrame awaitFirstRecoveryFrame(long timeoutMillis) {
        long deadline = System.nanoTime() + Math.max(1L, timeoutMillis) * 1_000_000L;
        while (System.nanoTime() < deadline) {
            long remainingNanos = Math.max(1L, deadline - System.nanoTime());
            long remainingMillis = Math.max(1L, remainingNanos / 1_000_000L);
            VideoFrame frame = dequeueCodecFrame(remainingMillis);
            if (frame == null) {
                return null;
            }

            if (AndroidH264DecoderPolicy.isRecoveryAccessUnit(frame.flags)) {
                return frame;
            }

            requestKeyFrame();
        }

        return null;
    }

    private VideoFrame dequeueCodecFrame(long timeoutMillis) {
        MediaCodec activeCodec = codec;
        if (activeCodec == null) {
            return null;
        }

        long deadline = System.nanoTime() + Math.max(1, timeoutMillis) * 1_000_000L;
        VideoFrame latestFrame = null;
        while (System.nanoTime() < deadline) {
            long remainingNanos = Math.max(1L, deadline - System.nanoTime());
            int index = activeCodec.dequeueOutputBuffer(
                bufferInfo,
                latestFrame == null
                    ? boundedOutputDequeueTimeoutUs(remainingNanos)
                    : 0);
            if (index == MediaCodec.INFO_TRY_AGAIN_LATER) {
                if (latestFrame != null) {
                    return latestFrame;
                }
                continue;
            }

            if (index == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                codecConfig = collectCodecConfig(activeCodec.getOutputFormat());
                continue;
            }

            if (index < 0) {
                continue;
            }

            try {
                if (bufferInfo.size <= 0) {
                    continue;
                }

                ByteBuffer outputBuffer = activeCodec.getOutputBuffer(index);
                if (outputBuffer == null) {
                    continue;
                }

                outputBuffer.position(bufferInfo.offset);
                outputBuffer.limit(bufferInfo.offset + bufferInfo.size);
                byte[] encodedBytes = new byte[bufferInfo.size];
                outputBuffer.get(encodedBytes);
                byte[] annexB = toAnnexB(encodedBytes);

                boolean codecConfigFrame = (bufferInfo.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0;
                if (codecConfigFrame) {
                    codecConfig = annexB;
                    continue;
                }

                if (hasPresentationOrderRegression(
                        lastPresentationTimeUs,
                        bufferInfo.presentationTimeUs)) {
                    throw new IllegalStateException(
                        "H.264 encoder produced reordered output despite the zero B-frame contract.");
                }

                lastPresentationTimeUs = bufferInfo.presentationTimeUs;
                boolean prependConfig =
                    ((bufferInfo.flags & MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0 ||
                        containsAnnexBNalType(annexB, 5)) &&
                    codecConfig.length > 0;
                if (prependConfig) {
                    annexB = prependCodecConfig(codecConfig, annexB);
                }
                // Some vendor codecs put SPS/PPS in the same output buffer as
                // the IDR but expose neither csd-* nor a CONFIG buffer.
                int flags = recoveryFlagsForAnnexB(
                    annexB,
                    bufferInfo.flags,
                    prependConfig);


                if (I_FRAME_INTERVAL_SECONDS == 0 &&
                    !AndroidH264DecoderPolicy.isRecoveryAccessUnit(flags)) {
                    throw new IllegalStateException(
                        "H.264 encoder ignored the GOP1 recovery contract.");
                }

                if (latestFrame != null) {
                    supersededOutputFrames++;
                    if (supersededOutputFrames == 1 ||
                        supersededOutputFrames % 120 == 0) {
                        AndroidSessionLog.info(
                            "H.264 latest-only encoder drain discarded " +
                            supersededOutputFrames +
                            " stale access unit(s) after sender backpressure.");
                    }
                }
                latestFrame = new VideoFrame(
                    width,
                    height,
                    annexB,
                    annexB.length,
                    flags,
                    0,
                    0);
            } finally {
                activeCodec.releaseOutputBuffer(index, false);
            }
        }

        return latestFrame;
    }

    static long boundedOutputDequeueTimeoutUs(long remainingNanos) {
        if (remainingNanos <= 0) {
            return 0;
        }

        long remainingUs = Math.max(1L, (remainingNanos + 999L) / 1_000L);
        return Math.min(OUTPUT_DEQUEUE_SLICE_US, remainingUs);
    }

    void requestKeyFrame() {
        MediaCodec activeCodec = codec;
        if (activeCodec == null) {
            return;
        }

        try {
            Bundle parameters = new Bundle();
            parameters.putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0);
            activeCodec.setParameters(parameters);
        } catch (RuntimeException ignored) {
        }
    }

    int getCurrentBitrate() {
        return currentBitrate;
    }

    String getSelectedCodecName() {
        return selectedCodecName;
    }

    int getCurrentFps() {
        return currentFps;
    }

    boolean supportsHighFrameRate() {
        return supportsHighFrameRate(
            selectedHardwareAccelerated,
            currentFps);
    }

    static boolean supportsHighFrameRate(
        boolean hardwareAccelerated,
        int actualFps) {
        return hardwareAccelerated &&
            actualFps >= AndroidVideoStreamSettings.H264_TARGET_FPS;
    }

    boolean setBitrate(int bitrate) {
        MediaCodec activeCodec = codec;
        if (activeCodec == null) {
            return false;
        }

        int normalized = Math.max(
            AndroidVideoStreamSettings.H264_MIN_BITRATE,
            Math.min(AndroidVideoStreamSettings.H264_MAX_BITRATE, bitrate));
        if (normalized == currentBitrate) {
            return true;
        }

        try {
            Bundle parameters = new Bundle();
            parameters.putInt(MediaCodec.PARAMETER_KEY_VIDEO_BITRATE, normalized);
            activeCodec.setParameters(parameters);
            currentBitrate = normalized;
            return true;
        } catch (RuntimeException ex) {
            return false;
        }
    }

    @Override
    public void close() {
        Surface activeInputSurface = inputSurface;
        inputSurface = null;
        if (activeInputSurface != null) {
            captureSession.releaseVideoSurface(activeInputSurface);
        }

        MediaCodec activeCodec = codec;
        codec = null;
        if (activeCodec != null) {
            try {
                activeCodec.stop();
            } catch (RuntimeException ignored) {
            }

            try {
                activeCodec.release();
            } catch (RuntimeException ignored) {
            }
        }

        if (activeInputSurface != null) {
            try {
                activeInputSurface.release();
            } catch (RuntimeException ignored) {
            }
        }

        codecConfig = new byte[0];
        currentBitrate = 0;
        currentFps = 0;
        supersededOutputFrames = 0;
        selectedCodecName = null;
        selectedHardwareAccelerated = false;
        lastPresentationTimeUs = Long.MIN_VALUE;
        pendingFirstFrame = null;
    }

    static boolean hasPresentationOrderRegression(
        long previousPresentationTimeUs,
        long currentPresentationTimeUs) {
        return previousPresentationTimeUs != Long.MIN_VALUE &&
            currentPresentationTimeUs < previousPresentationTimeUs;
    }

    static int estimateBitrate(int width, int height, int fps) {
        double megapixels = Math.max(1, width * height) / 1_000_000d;
        int bitrate = (int) Math.round(megapixels * Math.max(1, fps) * 85_000);
        return Math.max(
            AndroidVideoStreamSettings.H264_MIN_BITRATE,
            Math.min(AndroidVideoStreamSettings.H264_MAX_BITRATE, bitrate));
    }

    private static byte[] collectCodecConfig(MediaFormat format) {
        ByteArrayOutputStream output = new ByteArrayOutputStream(128);
        appendFormatBuffer(output, format, "csd-0");
        appendFormatBuffer(output, format, "csd-1");
        return output.toByteArray();
    }

    private static void appendFormatBuffer(ByteArrayOutputStream output, MediaFormat format, String key) {
        if (!format.containsKey(key)) {
            return;
        }

        ByteBuffer buffer = format.getByteBuffer(key);
        if (buffer == null) {
            return;
        }

        ByteBuffer duplicate = buffer.duplicate();
        byte[] bytes = new byte[duplicate.remaining()];
        duplicate.get(bytes);
        byte[] annexB = toAnnexB(bytes);
        output.write(annexB, 0, annexB.length);
    }

    private static byte[] prependCodecConfig(byte[] config, byte[] frame) {
        if (config.length == 0) {
            return frame;
        }

        byte[] combined = new byte[config.length + frame.length];
        System.arraycopy(config, 0, combined, 0, config.length);
        System.arraycopy(frame, 0, combined, config.length, frame.length);
        return combined;
    }

    private static byte[] toAnnexB(byte[] bytes) {
        if (bytes.length == 0 || startsWithStartCode(bytes)) {
            return bytes;
        }

        ByteArrayOutputStream output = new ByteArrayOutputStream(bytes.length + 16);
        int offset = 0;
        boolean parsedLengthPrefixedNal = false;
        while (offset + 4 <= bytes.length) {
            int nalLength = ((bytes[offset] & 0xFF) << 24) |
                ((bytes[offset + 1] & 0xFF) << 16) |
                ((bytes[offset + 2] & 0xFF) << 8) |
                (bytes[offset + 3] & 0xFF);
            if (nalLength <= 0 || nalLength > bytes.length - offset - 4) {
                parsedLengthPrefixedNal = false;
                break;
            }

            output.write(START_CODE, 0, START_CODE.length);
            output.write(bytes, offset + 4, nalLength);
            offset += 4 + nalLength;
            parsedLengthPrefixedNal = true;
        }

        if (parsedLengthPrefixedNal && offset == bytes.length) {
            return output.toByteArray();
        }

        output.reset();
        output.write(START_CODE, 0, START_CODE.length);
        output.write(bytes, 0, bytes.length);
        return output.toByteArray();
    }

    private static boolean startsWithStartCode(byte[] bytes) {
        return bytes.length >= 4 &&
            bytes[0] == 0 &&
            bytes[1] == 0 &&
            ((bytes[2] == 0 && bytes[3] == 1) || bytes[2] == 1);
    }

    static boolean isAnnexBRecoveryAccessUnit(byte[] bytes) {
        return containsAnnexBNalType(bytes, 5) &&
            containsAnnexBNalType(bytes, 7) &&
            containsAnnexBNalType(bytes, 8);
    }

    static int recoveryFlagsForAnnexB(
        byte[] annexB,
        int codecFlags,
        boolean prependedCodecConfig) {
        boolean keyFrame =
            (codecFlags & MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0 ||
            containsAnnexBNalType(annexB, 5);
        int flags = keyFrame
            ? RemoteDeskProtocol.FRAME_FLAG_KEY_FRAME
            : 0;
        if (keyFrame &&
            (prependedCodecConfig || isAnnexBRecoveryAccessUnit(annexB))) {
            flags |= RemoteDeskProtocol.FRAME_FLAG_CODEC_CONFIG;
        }
        return flags;
    }

    static boolean containsAnnexBNalType(byte[] bytes, int requestedNalType) {
        if (bytes == null || requestedNalType < 0 || requestedNalType > 31) {
            return false;
        }

        for (int offset = 0; offset < bytes.length;) {
            int startCodeLength = annexBStartCodeLengthAt(bytes, offset);
            if (startCodeLength == 0) {
                offset++;
                continue;
            }

            int nalOffset = offset + startCodeLength;
            if (nalOffset < bytes.length &&
                (bytes[nalOffset] & 0x1F) == requestedNalType) {
                return true;
            }
            offset = Math.max(nalOffset + 1, offset + 1);
        }

        return false;
    }

    private static int annexBStartCodeLengthAt(byte[] bytes, int offset) {
        if (offset < 0 || offset + 2 >= bytes.length ||
            bytes[offset] != 0 || bytes[offset + 1] != 0) {
            return 0;
        }

        if (bytes[offset + 2] == 1) {
            return 3;
        }
        return offset + 3 < bytes.length &&
            bytes[offset + 2] == 0 &&
            bytes[offset + 3] == 1
            ? 4
            : 0;
    }

    static final class VideoFrame {
        final int width;
        final int height;
        final byte[] bytes;
        final int length;
        final int flags;
        final double captureMillis;
        final double encodeMillis;

        VideoFrame(
            int width,
            int height,
            byte[] bytes,
            int length,
            int flags,
            double captureMillis,
            double encodeMillis) {
            this.width = width;
            this.height = height;
            this.bytes = bytes;
            this.length = length;
            this.flags = flags;
            this.captureMillis = captureMillis;
            this.encodeMillis = encodeMillis;
        }
    }
}
