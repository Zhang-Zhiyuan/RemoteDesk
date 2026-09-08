package com.remotedesk.agent;

import android.content.Context;
import android.os.Build;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.OutputStreamWriter;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Date;
import java.util.List;
import java.util.Locale;

final class AndroidSessionLog {
    private static final int MAX_ENTRIES = 200;
    private static final int MAX_EXPORT_FILES = 8;
    private static final long MAX_CURRENT_LOG_BYTES = 256 * 1024L;
    private static final String DIAGNOSTIC_FOLDER_NAME = "RemoteDeskDiagnostics";
    private static final String EXPORT_PREFIX = "remotedesk-android-log-";
    private static final String EXPORT_SUFFIX = ".txt";
    private static final String CURRENT_LOG_NAME = "remotedesk-android-current.log";
    private static final Object LOCK = new Object();
    private static final ArrayDeque<String> ENTRIES = new ArrayDeque<>();
    private static File currentLogFile;

    private AndroidSessionLog() {
    }

    static void info(String message) {
        append("INFO", message, null);
    }

    static void configure(Context context) {
        synchronized (LOCK) {
            File directory = getDiagnosticDirectory(context);
            if (!directory.exists() && !directory.mkdirs()) {
                return;
            }

            currentLogFile = new File(directory, CURRENT_LOG_NAME);
        }
    }

    static void error(String message, Throwable throwable) {
        append("ERROR", message, throwable);
    }

    static List<String> snapshot() {
        synchronized (LOCK) {
            return new ArrayList<>(ENTRIES);
        }
    }

    static File exportToFile(Context context) throws IOException {
        Context appContext = context.getApplicationContext();
        File directory = getDiagnosticDirectory(appContext);
        if (!directory.exists() && !directory.mkdirs()) {
            throw new IOException("无法创建诊断日志目录。");
        }

        File file = new File(directory, EXPORT_PREFIX + timestampForFileName(new Date()) + EXPORT_SUFFIX);
        try (OutputStreamWriter writer = new OutputStreamWriter(
            new FileOutputStream(file, false),
            StandardCharsets.UTF_8)) {
            writer.write("RemoteDesk Android diagnostic log\n");
            writer.write("Generated: " + timestamp(new Date()) + "\n");
            writer.write("Android SDK: " + Build.VERSION.SDK_INT + "\n");
            writer.write("Device: " + Build.MANUFACTURER + " " + Build.MODEL + "\n\n");
            File previousLog = getPreviousLogFile(appContext);
            File currentLog = getCurrentLogFile(appContext);
            if (previousLog.isFile() || currentLog.isFile()) {
                copyLogFileIfPresent(previousLog, writer);
                copyLogFileIfPresent(currentLog, writer);
            } else {
                for (String entry : snapshot()) {
                    writer.write(entry);
                    writer.write('\n');
                }
            }
        }

        pruneOldExports(directory);
        return file;
    }

    static File getDiagnosticDirectory(Context context) {
        return new File(context.getApplicationContext().getFilesDir(), DIAGNOSTIC_FOLDER_NAME);
    }

    static File getCurrentLogFile(Context context) {
        return new File(getDiagnosticDirectory(context), CURRENT_LOG_NAME);
    }

    static File getPreviousLogFile(Context context) {
        return new File(getCurrentLogFile(context).getAbsolutePath() + ".old");
    }

    static void clearPersistent(Context context) throws IOException {
        synchronized (LOCK) {
            ENTRIES.clear();
            File directory = getDiagnosticDirectory(context);
            clearDiagnosticFiles(directory);
            currentLogFile = new File(directory, CURRENT_LOG_NAME);
        }
    }

    static void clearForTests() {
        synchronized (LOCK) {
            ENTRIES.clear();
            currentLogFile = null;
        }
    }

    private static void append(String level, String message, Throwable throwable) {
        String normalized = normalize(message);
        String entry = timestamp(new Date()) + " [" + level + "] " + normalized;
        if (throwable != null) {
            entry += " | " + describe(throwable);
        }

        synchronized (LOCK) {
            while (ENTRIES.size() >= MAX_ENTRIES) {
                ENTRIES.removeFirst();
            }

            ENTRIES.addLast(entry);
            appendToCurrentLogLocked(entry);
        }
    }

    static void appendToFileForTests(File file, String entry, long maxBytes) throws IOException {
        appendToFile(file, entry, maxBytes);
    }

    static void clearDiagnosticFilesForTests(File directory) throws IOException {
        clearDiagnosticFiles(directory);
    }

    private static String normalize(String message) {
        if (message == null || message.trim().isEmpty()) {
            return "(empty)";
        }

        return message.replace('\r', ' ').replace('\n', ' ').trim();
    }

    private static String describe(Throwable throwable) {
        StringBuilder builder = new StringBuilder();
        builder
            .append(throwable.getClass().getSimpleName())
            .append(": ")
            .append(normalize(throwable.getMessage()));

        StackTraceElement[] stackTrace = throwable.getStackTrace();
        int count = Math.min(3, stackTrace.length);
        for (int index = 0; index < count; index++) {
            builder.append(" at ").append(stackTrace[index]);
        }

        return builder.toString();
    }

    private static String timestamp(Date date) {
        return new SimpleDateFormat("yyyy-MM-dd HH:mm:ss.SSS", Locale.ROOT).format(date);
    }

    private static String timestampForFileName(Date date) {
        return new SimpleDateFormat("yyyyMMdd-HHmmss", Locale.ROOT).format(date);
    }

    private static void appendToCurrentLogLocked(String entry) {
        if (currentLogFile == null) {
            return;
        }

        try {
            appendToFile(currentLogFile, entry, MAX_CURRENT_LOG_BYTES);
        } catch (IOException ignored) {
        }
    }

    private static void appendToFile(File file, String entry, long maxBytes) throws IOException {
        File directory = file.getParentFile();
        if (directory != null && !directory.exists() && !directory.mkdirs()) {
            throw new IOException("Unable to create diagnostic log directory.");
        }

        if (file.isFile() && file.length() > maxBytes) {
            File previous = new File(file.getAbsolutePath() + ".old");
            if (previous.exists() && !previous.delete()) {
                throw new IOException("Unable to replace old diagnostic log.");
            }

            if (!file.renameTo(previous)) {
                throw new IOException("Unable to rotate diagnostic log.");
            }
        }

        try (OutputStreamWriter writer = new OutputStreamWriter(
            new FileOutputStream(file, true),
            StandardCharsets.UTF_8)) {
            writer.write(entry);
            writer.write('\n');
        }
    }

    static void copyLogFileIfPresentForTests(File file, OutputStreamWriter writer) throws IOException {
        copyLogFileIfPresent(file, writer);
    }

    private static void copyLogFileIfPresent(File file, OutputStreamWriter writer) throws IOException {
        if (!file.isFile() || file.length() <= 0) {
            return;
        }

        char[] buffer = new char[8192];
        try (InputStreamReader input = new InputStreamReader(
            new FileInputStream(file),
            StandardCharsets.UTF_8)) {
            int read;
            while ((read = input.read(buffer)) != -1) {
                writer.write(buffer, 0, read);
            }
        }

        writer.write('\n');
    }

    private static void clearDiagnosticFiles(File directory) throws IOException {
        if (!directory.exists()) {
            return;
        }

        File[] files = directory.listFiles((dir, name) ->
            CURRENT_LOG_NAME.equals(name) ||
            (CURRENT_LOG_NAME + ".old").equals(name) ||
            (name.startsWith(EXPORT_PREFIX) && name.endsWith(EXPORT_SUFFIX)));
        if (files == null) {
            return;
        }

        for (File file : files) {
            if (file.isFile() && !file.delete()) {
                throw new IOException("Unable to delete diagnostic log: " + file.getName());
            }
        }
    }

    private static void pruneOldExports(File directory) {
        File[] files = directory.listFiles((dir, name) ->
            name.startsWith(EXPORT_PREFIX) && name.endsWith(EXPORT_SUFFIX));
        if (files == null || files.length <= MAX_EXPORT_FILES) {
            return;
        }

        Arrays.sort(files, (left, right) -> Long.compare(right.lastModified(), left.lastModified()));
        for (int index = MAX_EXPORT_FILES; index < files.length; index++) {
            if (files[index].isFile()) {
                files[index].delete();
            }
        }
    }
}
