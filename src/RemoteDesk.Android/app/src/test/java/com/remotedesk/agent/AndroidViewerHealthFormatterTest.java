package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;

import org.junit.Test;

public final class AndroidViewerHealthFormatterTest {
    @Test
    public void formatterUsesUnknownRouteBeforeAConnectionOwnerExists() {
        assertEquals(
            "画面 — · FPS — · — Mbps · 路由 — · 捕获 — · 编码 — · ACK — · 仅观看",
            AndroidViewerHealthFormatter.format(null));
    }

    @Test
    public void formatterUsesDashesWhenNoSamplesExist() {
        AndroidViewerHealthTracker tracker = new AndroidViewerHealthTracker();

        String text = AndroidViewerHealthFormatter.format(
            tracker.snapshot(1L, false, false, false));

        assertEquals(
            "画面 — · FPS — · — Mbps · 路由 TCP · 捕获 — ms · 编码 — ms · ACK — · 仅观看",
            text);
    }

    @Test
    public void formatterIncludesRouteCodecRatesAndAck() {
        AndroidViewerHealthTracker tracker = new AndroidViewerHealthTracker();
        tracker.setVideoFormat(
            AndroidViewerHealthTracker.Codec.H264_HARDWARE,
            1920,
            1080);
        tracker.start(1_000_000_000L);
        tracker.recordReceivedFrame(1_000_000, 2.25d, 1.75d);
        tracker.recordPresentedFrame();
        tracker.recordMouseAck(1_570L, 1L);

        String text = AndroidViewerHealthFormatter.format(
            tracker.snapshot(2_000_000_000L, true, true, true));

        assertEquals(
            "H.264硬解 1920×1080 · FPS 1.0 · 8.00 Mbps · 路由 UDP视频/输入 · 捕获 2.3 ms · 编码 1.8 ms · ACK 1.57 ms · 可控制",
            text);
    }
}
