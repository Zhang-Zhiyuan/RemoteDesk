package com.remotedesk.agent;

import org.json.JSONObject;
import org.junit.Test;
import java.io.IOException;
import static org.junit.Assert.*;

public final class AndroidRelayDeviceNameTest {
    @Test public void namesAreTrimmedWithoutChangingChineseOrEmoji() {
        assertEquals("", AndroidRelayDeviceName.normalize("  "));
        assertEquals("广州主机 🖥", AndroidRelayDeviceName.normalize("  广州主机 🖥  "));
        assertEquals("手机", AndroidRelayDeviceName.normalize("　手机　"));
        assertEquals(80, AndroidRelayDeviceName.normalize("🙂".repeat(40)).length());
        assertEquals(80, AndroidRelayDeviceName.normalize("文".repeat(80)).length());
    }

    @Test public void invalidNamesAreRejected() {
        for (String name : new String[] {"文".repeat(81), "🙂".repeat(41), "a\nb", "a\0b", "a\u202eb", "a\u2028b", "\ud800", "\udfff"})
            assertThrows(IllegalArgumentException.class, () -> AndroidRelayDeviceName.normalize(name));
    }

    @Test public void onlyMatchingServerAcknowledgementConfirmsName() throws Exception {
        AndroidRelay.verifyNameReply(new JSONObject().put("ok", true).put("deviceId", "owned").put("sharedName", "手机"), "owned", "手机");
        assertThrows(IOException.class, () -> AndroidRelay.verifyNameReply(new JSONObject().put("ok", true)
            .put("deviceId", "other").put("sharedName", "手机"), "owned", "手机"));
        assertThrows(IOException.class, () -> AndroidRelay.verifyNameReply(new JSONObject().put("ok", true)
            .put("deviceId", "owned"), "owned", ""));
        assertThrows(IOException.class, () -> AndroidRelay.verifyNameReply(new JSONObject().put("ok", false)
            .put("error", "未知的中继连接类型。"), "owned", "手机"));
        assertThrows(AndroidRelay.IdentityFailure.class, () -> AndroidRelay.verifyNameReply(new JSONObject().put("ok", false)
            .put("error", "中继访问密钥错误。"), "owned", "手机"));
    }

    @Test public void errorsDoNotEchoSecretsAndLegacyDevicesRemainVisible() {
        assertFalse(AndroidRelayDeviceName.errorMessage("unknown-secret").contains("secret"));
        assertTrue(AndroidRelayDeviceName.errorMessage("设备名称保存失败。").contains("磁盘"));
        AndroidRelay.Device legacy = new AndroidRelay.Device("fixture", "Old PC", "Windows", false);
        assertEquals("Old PC", legacy.name);
        assertEquals("Old PC", legacy.originalName);
        assertFalse(legacy.canRename);
        assertEquals("", legacy.sharedName);
    }
}
