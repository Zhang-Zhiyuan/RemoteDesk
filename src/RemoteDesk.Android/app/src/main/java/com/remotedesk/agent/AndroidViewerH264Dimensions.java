package com.remotedesk.agent;

import java.util.concurrent.atomic.AtomicLong;

/** Keeps a frame's width and height as one atomic, connection-local value. */
final class AndroidViewerH264Dimensions {
    private final AtomicLong packed = new AtomicLong();

    void set(int width, int height) {
        if (width <= 0 || height <= 0) {
            return;
        }
        packed.set(((long) width << 32) | (height & 0xFFFF_FFFFL));
    }

    Snapshot snapshot() {
        long value = packed.get();
        return new Snapshot((int) (value >>> 32), (int) value);
    }

    static final class Snapshot {
        final int width;
        final int height;

        Snapshot(int width, int height) {
            this.width = width;
            this.height = height;
        }

        boolean isValid() {
            return width > 0 && height > 0;
        }
    }
}
