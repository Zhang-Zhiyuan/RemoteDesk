package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import java.util.List;

import org.junit.After;
import org.junit.Before;
import org.junit.Rule;
import org.junit.Test;
import org.junit.rules.TemporaryFolder;

public final class AndroidSessionLogTest {
    @Rule
    public TemporaryFolder temporaryFolder = new TemporaryFolder();

    @Before
    public void setUp() {
        AndroidSessionLog.clearForTests();
    }

    @After
    public void tearDown() {
        AndroidSessionLog.clearForTests();
    }

    @Test
    public void snapshotContainsRecentInfoAndNormalizesNewlines() {
        AndroidSessionLog.info("first\nline");

        List<String> entries = AndroidSessionLog.snapshot();

        assertEquals(1, entries.size());
        assertTrue(entries.get(0).contains("[INFO] first line"));
        assertFalse(entries.get(0).contains("\n"));
    }

    @Test
    public void errorEntryIncludesThrowableTypeAndMessage() {
        AndroidSessionLog.error("failed", new IllegalStateException("bad state"));

        String entry = AndroidSessionLog.snapshot().get(0);

        assertTrue(entry.contains("[ERROR] failed"));
        assertTrue(entry.contains("IllegalStateException"));
        assertTrue(entry.contains("bad state"));
    }

    @Test
    public void keepsOnlyMostRecentEntries() {
        for (int index = 0; index < 205; index++) {
            AndroidSessionLog.info("event-" + index);
        }

        List<String> entries = AndroidSessionLog.snapshot();

        assertEquals(200, entries.size());
        assertFalse(entries.get(0).contains("event-0"));
        assertTrue(entries.get(0).contains("event-5"));
        assertTrue(entries.get(entries.size() - 1).contains("event-204"));
    }

    @Test
    public void appendToFileForTestsWritesUtf8LogLines() throws Exception {
        java.io.File file = temporaryFolder.newFile("current.log");

        AndroidSessionLog.appendToFileForTests(file, "第一行", 1024);
        AndroidSessionLog.appendToFileForTests(file, "second", 1024);

        String text = new String(
            java.nio.file.Files.readAllBytes(file.toPath()),
            java.nio.charset.StandardCharsets.UTF_8);

        assertTrue(text.contains("第一行"));
        assertTrue(text.contains("second"));
    }

    @Test
    public void appendToFileForTestsRotatesLargeCurrentLog() throws Exception {
        java.io.File file = temporaryFolder.newFile("current.log");
        java.nio.file.Files.write(
            file.toPath(),
            "0123456789".getBytes(java.nio.charset.StandardCharsets.UTF_8));

        AndroidSessionLog.appendToFileForTests(file, "new", 4);

        java.io.File oldFile = new java.io.File(file.getAbsolutePath() + ".old");
        String text = new String(
            java.nio.file.Files.readAllBytes(file.toPath()),
            java.nio.charset.StandardCharsets.UTF_8);

        assertTrue(oldFile.isFile());
        assertTrue(text.contains("new"));
        assertFalse(text.contains("0123456789"));
    }

    @Test
    public void copyLogFileIfPresentForTestsCopiesUtf8TextAndAddsSeparator() throws Exception {
        java.io.File file = temporaryFolder.newFile("current.log");
        java.nio.file.Files.write(
            file.toPath(),
            "旧日志\n包含中文".getBytes(java.nio.charset.StandardCharsets.UTF_8));
        java.io.ByteArrayOutputStream bytes = new java.io.ByteArrayOutputStream();
        java.io.OutputStreamWriter writer = new java.io.OutputStreamWriter(
            bytes,
            java.nio.charset.StandardCharsets.UTF_8);

        AndroidSessionLog.copyLogFileIfPresentForTests(file, writer);
        writer.flush();

        String text = new String(bytes.toByteArray(), java.nio.charset.StandardCharsets.UTF_8);

        assertTrue(text.contains("旧日志"));
        assertTrue(text.contains("包含中文"));
        assertTrue(text.endsWith("\n"));
    }

    @Test
    public void copyLogFileIfPresentForTestsIgnoresMissingFile() throws Exception {
        java.io.File file = new java.io.File(temporaryFolder.getRoot(), "missing.log");
        java.io.ByteArrayOutputStream bytes = new java.io.ByteArrayOutputStream();
        java.io.OutputStreamWriter writer = new java.io.OutputStreamWriter(
            bytes,
            java.nio.charset.StandardCharsets.UTF_8);

        AndroidSessionLog.copyLogFileIfPresentForTests(file, writer);
        writer.flush();

        assertEquals(0, bytes.size());
    }

    @Test
    public void clearDiagnosticFilesForTestsDeletesOnlyDiagnosticLogs() throws Exception {
        java.io.File directory = temporaryFolder.newFolder("logs");
        java.io.File current = new java.io.File(directory, "remotedesk-android-current.log");
        java.io.File previous = new java.io.File(directory, "remotedesk-android-current.log.old");
        java.io.File export = new java.io.File(directory, "remotedesk-android-log-20260603.txt");
        java.io.File unrelated = new java.io.File(directory, "keep.txt");
        java.nio.file.Files.write(current.toPath(), "current".getBytes(java.nio.charset.StandardCharsets.UTF_8));
        java.nio.file.Files.write(previous.toPath(), "previous".getBytes(java.nio.charset.StandardCharsets.UTF_8));
        java.nio.file.Files.write(export.toPath(), "export".getBytes(java.nio.charset.StandardCharsets.UTF_8));
        java.nio.file.Files.write(unrelated.toPath(), "keep".getBytes(java.nio.charset.StandardCharsets.UTF_8));

        AndroidSessionLog.clearDiagnosticFilesForTests(directory);

        assertFalse(current.exists());
        assertFalse(previous.exists());
        assertFalse(export.exists());
        assertTrue(unrelated.exists());
    }
}
