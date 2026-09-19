package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;

import java.io.IOException;
import java.net.ConnectException;
import java.net.SocketTimeoutException;

import org.junit.Test;

public final class AndroidViewerStatusTextTest {
    @Test public void terminalFailureNoLongerClaimsEncryptionOrWaitingForFrames() {
        assertEquals("连接未建立 · 请返回选择其他设备", AndroidViewerStatusText.terminalConnectionDetail(false));
    }

    @Test public void terminalCloseOfEstablishedConnectionExplainsHowToResume() {
        assertEquals("连接已结束 · 请返回重新连接", AndroidViewerStatusText.terminalConnectionDetail(true));
    }
    private static final String TRAILER =
        "RemoteDesk.CaptureTargetStatus/v1|unavailable|QQ==|Qg==|3";

    @Test
    public void stripsOnlyAUniqueTrailerAtTheStartOfTheFinalLine() {
        assertEquals(
            "捕获目标暂不可用，正在等待。",
            AndroidViewerStatusText.forDisplay(
                "捕获目标暂不可用，正在等待。\n" + TRAILER));
    }

    @Test
    public void preservesMarkerWithoutBodyOrWhenItIsNotTheFinalLine() {
        assertEquals(TRAILER, AndroidViewerStatusText.forDisplay(TRAILER));
        assertEquals(
            "正文\n" + TRAILER + "\n尾随文本",
            AndroidViewerStatusText.forDisplay(
                "正文\n" + TRAILER + "\n尾随文本"));
        assertEquals(
            "正文 " + TRAILER,
            AndroidViewerStatusText.forDisplay("正文 " + TRAILER));
    }

    @Test
    public void preservesWrongOrDuplicateMarkers() {
        assertEquals(
            "正文\nRemoteDesk.CaptureTargetStatus/v10|unavailable|QQ==|Qg==|3",
            AndroidViewerStatusText.forDisplay(
                "正文\nRemoteDesk.CaptureTargetStatus/v10|unavailable|QQ==|Qg==|3"));
        assertEquals(
            "正文 " + TRAILER + "\n" + TRAILER,
            AndroidViewerStatusText.forDisplay(
                "正文 " + TRAILER + "\n" + TRAILER));
    }

    @Test
    public void preservesMalformedMachineTrailers() {
        String[] invalid = {
            "RemoteDesk.CaptureTargetStatus/v1|unavailable-ish|QQ==|Qg==|3",
            "RemoteDesk.CaptureTargetStatus/v1|unavailable|***|Qg==|3",
            "RemoteDesk.CaptureTargetStatus/v1|unavailable|QQ==|Qg==",
            "RemoteDesk.CaptureTargetStatus/v1|unavailable|QQ==|Qg==|3|extra",
            "RemoteDesk.CaptureTargetStatus/v1|unavailable|QQ|Qg==|3",
            "RemoteDesk.CaptureTargetStatus/v1|unavailable|QQ==|Qg==|-1",
            "RemoteDesk.CaptureTargetStatus/v1|unavailable|QQ==|Qg==|2147483648"
        };

        for (String trailer : invalid) {
            String message = "正文\n" + trailer;
            assertEquals(message, AndroidViewerStatusText.forDisplay(message));
        }
    }

    @Test
    public void formatsCommonConnectionFailuresForPeople() {
        assertEquals(
            "无法连接到远端，请检查地址、端口与服务状态",
            AndroidViewerStatusText.connectionFailure(new ConnectException("refused")));
        assertEquals(
            "连接超时，请检查网络与防火墙",
            AndroidViewerStatusText.connectionFailure(new SocketTimeoutException("timed out")));
    }

    @Test
    public void formatsARecognizedNestedConnectionFailure() {
        assertEquals(
            "无法连接到远端，请检查地址、端口与服务状态",
            AndroidViewerStatusText.connectionFailure(
                new IOException("outer", new ConnectException("refused"))));
    }
}
