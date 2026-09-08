package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertNotNull;
import static org.junit.Assert.assertNull;

import org.junit.Test;

public final class MainActivityTest {
    @Test
    public void formatReceivedFileActionFailureIncludesTrimmedExceptionMessage() {
        String message = MainActivity.formatReceivedFileActionFailure(
            "打开",
            new IllegalStateException(" resolver denied "));

        assertEquals("打开接收文件失败：resolver denied", message);
    }

    @Test
    public void formatReceivedFileActionFailureFallsBackToExceptionType() {
        String message = MainActivity.formatReceivedFileActionFailure(
            "分享",
            new SecurityException());

        assertEquals("分享接收文件失败：SecurityException", message);
    }

    @Test
    public void normalizeReceivedFileMimeTypeUsesFallbackForMissingValues() {
        assertEquals("*/*", MainActivity.normalizeReceivedFileMimeType(null, "*/*"));
        assertEquals("application/octet-stream", MainActivity.normalizeReceivedFileMimeType("  ", null));
    }

    @Test
    public void normalizeReceivedFileMimeTypeTrimsUsableValues() {
        assertEquals("image/png", MainActivity.normalizeReceivedFileMimeType(" image/png ", "*/*"));
    }

    @Test
    public void formatSystemActionFailureIncludesTrimmedExceptionMessage() {
        String message = MainActivity.formatSystemActionFailure(
            "复制连接信息",
            new IllegalStateException(" clipboard unavailable "));

        assertEquals("复制连接信息失败：clipboard unavailable", message);
    }

    @Test
    public void formatSystemActionFailureFallsBackToExceptionType() {
        String message = MainActivity.formatSystemActionFailure(
            "打开无障碍设置",
            new UnsupportedOperationException());

        assertEquals("打开无障碍设置失败：UnsupportedOperationException", message);
    }

    @Test
    public void formatManualSettingsInstructionIncludesFailureReason() {
        String message = MainActivity.formatManualSettingsInstruction(
            new SecurityException("settings blocked"));

        assertEquals(
            "无法打开系统设置，请手动在系统设置中允许 RemoteDesk 相关权限：settings blocked",
            message);
    }

    @Test
    public void parseRemoteEndpointUsesDefaultPortForHostOnly() {
        MainActivity.RemoteEndpoint endpoint = MainActivity.parseRemoteEndpoint("5090.zzy.college", 56565);

        assertEquals("5090.zzy.college", endpoint.host);
        assertEquals(56565, endpoint.port);
    }

    @Test
    public void parseRemoteEndpointReadsExplicitPort() {
        MainActivity.RemoteEndpoint endpoint = MainActivity.parseRemoteEndpoint("192.168.1.20:56566", 56565);

        assertEquals("192.168.1.20", endpoint.host);
        assertEquals(56566, endpoint.port);
    }

    @Test
    public void parseRemoteEndpointReadsBracketedIpv6Port() {
        MainActivity.RemoteEndpoint endpoint = MainActivity.parseRemoteEndpoint("[2001:db8::1]:56565", 56564);

        assertEquals("2001:db8::1", endpoint.host);
        assertEquals(56565, endpoint.port);
    }

    @Test
    public void parseRemoteEndpointRejectsBadPort() {
        assertNull(MainActivity.parseRemoteEndpoint("host:not-a-port", 56565));
        assertNull(MainActivity.parseRemoteEndpoint("host:70000", 56565));
    }

    @Test
    public void mapViewPointToFrameAccountsForFitCenterLetterboxing() {
        int[] point = RemoteDeskViewerActivity.mapViewPointToFrame(
            1000,
            1000,
            1920,
            1080,
            500,
            500);

        assertEquals(960, point[0]);
        assertEquals(540, point[1]);
    }

    @Test
    public void mapViewPointToFrameClampsOutsideImageArea() {
        int[] point = RemoteDeskViewerActivity.mapViewPointToFrame(
            1000,
            1000,
            1920,
            1080,
            10,
            10);

        assertEquals(19, point[0]);
        assertEquals(0, point[1]);
    }

    @Test
    public void mapViewPointToFrameIfInsideRejectsLetterboxForGestureStart() {
        int[] point = RemoteDeskViewerActivity.mapViewPointToFrameIfInside(
            1000,
            1000,
            1920,
            1080,
            500,
            100);

        assertNull(point);
    }

    @Test
    public void mapViewPointToFrameIfInsideAcceptsDisplayedFrame() {
        int[] point = RemoteDeskViewerActivity.mapViewPointToFrameIfInside(
            1000,
            1000,
            1920,
            1080,
            500,
            500);

        assertNotNull(point);
        assertEquals(960, point[0]);
        assertEquals(540, point[1]);
    }

    @Test
    public void clearScaleModeMapsOnlyTheCenteredOneToOneFrame() {
        int[] outside = RemoteDeskViewerActivity.mapViewPointToFrameIfInside(
            2560,
            1280,
            1920,
            1080,
            100,
            640,
            false);
        int[] center = RemoteDeskViewerActivity.mapViewPointToFrameIfInside(
            2560,
            1280,
            1920,
            1080,
            1280,
            640,
            false);

        assertNull(outside);
        assertNotNull(center);
        assertEquals(960, center[0]);
        assertEquals(540, center[1]);
    }
}
