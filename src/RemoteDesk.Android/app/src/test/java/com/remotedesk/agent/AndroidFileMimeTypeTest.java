package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.fail;

import java.util.Locale;
import java.util.Map;
import org.junit.Test;

public final class AndroidFileMimeTypeTest {
    private static final Map<String, String> TYPES = Map.of(
        "txt", "text/plain", "zip", "application/zip", "png", "image/png",
        "ini", "text/plain", "gz", "application/gzip");

    @Test public void recognizedExtensionsReachThePlatformResolver() {
        for (String name : new String[] {"测试文件.txt", "notes (1).TXT", "notes.TXT "})
            assertEquals("text/plain", AndroidFileMimeType.resolve(name, TYPES::get));
        assertEquals("application/zip", AndroidFileMimeType.resolve("folder.zip", TYPES::get));
        assertEquals("image/png", AndroidFileMimeType.resolve("image.PNG", TYPES::get));
        assertEquals("application/gzip", AndroidFileMimeType.resolve("archive.tar.gz", TYPES::get));
    }

    @Test public void unknownExtensionsUseBinaryWithoutChangingTheFileName() {
        assertEquals(AndroidFileMimeType.BINARY, AndroidFileMimeType.resolve("data.unknown", TYPES::get));
        assertEquals(AndroidFileMimeType.BINARY, AndroidFileMimeType.resolve("data.bin", ignored -> ""));
        // Do not guess an extension/MIME for extensionless files or dotfiles.
        for (String name : new String[] {null, "", "README", ".profile", "name."})
            assertEquals(AndroidFileMimeType.BINARY, AndroidFileMimeType.resolve(name, ignored -> {
                fail("No extension should be looked up"); return null;
            }));
    }

    @Test public void caseNormalizationIsIndependentOfDeviceLanguage() {
        Locale original = Locale.getDefault();
        try {
            Locale.setDefault(Locale.forLanguageTag("tr-TR"));
            assertEquals("text/plain", AndroidFileMimeType.resolve("settings.INI", TYPES::get));
        } finally { Locale.setDefault(original); }
    }
}
