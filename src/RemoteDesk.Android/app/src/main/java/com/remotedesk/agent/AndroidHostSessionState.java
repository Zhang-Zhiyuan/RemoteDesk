package com.remotedesk.agent;

import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;

final class AndroidHostSessionState {
    static final long GESTURE_CLOSE_TIMEOUT_MILLIS = 250L;
    final AtomicBoolean running = new AtomicBoolean(true);
    final AtomicInteger lastFrameWidth = new AtomicInteger();
    final AtomicInteger lastFrameHeight = new AtomicInteger();
    final AtomicInteger viewerVideoCodecs =
        new AtomicInteger(RemoteDeskProtocol.VIDEO_CODEC_JPEG);
    final AtomicInteger viewerCapabilities = new AtomicInteger();
    final AtomicBoolean viewerInfoReceived = new AtomicBoolean();
    final AtomicBoolean viewerCapabilitiesReceived = new AtomicBoolean();
    final AtomicBoolean keyFrameRequested = new AtomicBoolean();
    final AtomicInteger udpTargetBitrate = new AtomicInteger();
    volatile AndroidLowLatencyVideoTransport.Host lowLatencyVideo;
    final AndroidInputInjector.GestureState gestureState =
        new AndroidInputInjector.GestureState();

    boolean tryStop() {
        // This can run on the Android main thread during service or
        // MediaProjection teardown. Queue the release without waiting; the
        // session worker performs the bounded drain after I/O stops.
        gestureState.reset();
        return running.compareAndSet(true, false);
    }

    boolean finishGestureTeardown() {
        return gestureState.closeGracefully(GESTURE_CLOSE_TIMEOUT_MILLIS);
    }

    void closeLowLatencyVideo() {
        AndroidLowLatencyVideoTransport.Host transport = lowLatencyVideo;
        lowLatencyVideo = null;
        if (transport != null) {
            transport.close();
        }
    }

    void signalLowLatencyVideoClose() {
        AndroidLowLatencyVideoTransport.Host transport = lowLatencyVideo;
        if (transport != null) {
            transport.signalClose();
        }
    }
}
